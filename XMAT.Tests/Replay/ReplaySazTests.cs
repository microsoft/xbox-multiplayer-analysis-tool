// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using XMAT.Operations.Capture;
using XMAT.Operations.Replay;
using XMAT.Tests.Fixtures;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Replay
{
    // A send as a session of a Fiddler archive (ReplaySaz), written by SazArchiveWriter and read back by both readers: the
    // GUI's import (FiddlerSazHandler.ImportSazFileWithDiagnostics) and the command line's (BoundedSazReader).
    public sealed class ReplaySazTests : IDisposable
    {
        private static readonly DateTime Started = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        private readonly string _directory = Path.Join(Path.GetTempPath(), "xmat-replay-saz-" + Guid.NewGuid().ToString("N"));

        public ReplaySazTests() => Directory.CreateDirectory(_directory);

        public void Dispose() => TestFolderCleanup.Delete(_directory);

        public static IEnumerable<object[]> Urls()
        {
            yield return new object[] { "http://example.test/path?q=1", "http://example.test/path?q=1", "example.test" };
            yield return new object[] { "https://example.test:8443/a%20b?x=%C3%A9", "https://example.test:8443/a%20b?x=%C3%A9", "example.test:8443" };
            yield return new object[] { "http://[::1]:8080/v6", "http://[::1]:8080/v6", "[::1]:8080" };
            yield return new object[] { "http://[fe80::1%251]:8081/zone", "http://[fe80::1]:8081/zone", "[fe80::1]:8081" };
            yield return new object[]
            {
                "http://b\u00fccher.example:18081/\u00e4 b?\u00f6=1", "http://xn--bcher-kva.example:18081/%C3%A4%20b?%C3%B6=1",
                "xn--bcher-kva.example:18081",
            };
        }

        // The target is absolute, in ASCII and escaped, with the authority that Host has: the one the HTTP stack sends. The
        // readers take the record's host and port from it.
        [Theory]
        [MemberData(nameof(Urls))]
        public void ASend_ReadsBackThroughBothReaders_WithItsAsciiTargetAndHost(string url, string target, string authority)
        {
            var uri = new Uri(url);
            Assert.Equal(authority, ReplayAuthority.Ascii(uri));
            Assert.True(HostAuthority.TryParse(authority, out string host, out int? port));
            byte[] body = Enumerable.Range(0, 300).Select(value => (byte)value).ToArray();
            ReplayExchange exchange = Exchange(
                uri,
                "POST",
                new[] { Header("Host", ReplayAuthority.Ascii(uri)), Header("X'Name", "apostrophe"), Header("X-Twice", "1"), Header("x-twice", "2") },
                body,
                Response("HTTP/1.1 201 Created", new[] { Header("X-Latin", "caf\u00e9 \u00ff"), Header("X-Empty", "") }, "made"u8.ToArray()));

            foreach (ProxyConnectionModel record in RoundTrip(Result(1), exchange))
            {
                Assert.Equal(
                    ("POST", uri.Scheme, host, port?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "", uri.PathAndQuery, "201"),
                    (record.Method, record.Scheme, record.Host, record.Port, record.Path, record.Status));
                Assert.Equal(
                    $"POST {target} HTTP/1.1\r\nHost: {authority}\r\nX'Name: apostrophe\r\nX-Twice: 1,2\r\nContent-Length: 300\r\n\r\n",
                    record.RequestLineAndHeaders);
                Assert.Equal(body, record.RequestBody);
                Assert.Equal("HTTP/1.1 201 Created\r\nX-Latin: caf\u00e9 \u00ff\r\nX-Empty: \r\n\r\n", record.ResponseLineAndHeaders);
                Assert.Equal("made"u8.ToArray(), record.ResponseBody);
                Assert.Equal(Started, record.RequestTime.ToUniversalTime());
                Assert.Equal(DateTimeKind.Local, record.RequestTime.Kind);
                Assert.Equal(TimeSpan.FromMilliseconds(25), record.Duration);
            }
        }

        // A response over the response size limit came without its body: the archive keeps its head with the marker, which
        // makes a readback show the body as not kept, never as an empty body.
        [Theory]
        [InlineData("Content-Length", "99999", "99999")]
        [InlineData("Content-Type", "text/plain", "unknown")]
        public void ATooLargeResponse_KeepsItsHeadWithTheMarker_AndNoBody(string name, string value, string marker)
        {
            var uri = new Uri("http://example.test/large");
            ReplayExchange exchange = Exchange(uri, "GET", new[] { Header("Host", "example.test") }, [], Response("HTTP/1.1 200 OK", Array.Empty<KeyValuePair<string, string>>(), []), ReplayOutcome.TooLarge);
            exchange = With(exchange, response: new ReplayMessage("HTTP/1.1 200 OK", null, new[] { Header(name, value) }, "partial"u8.ToArray()));

            foreach (ProxyConnectionModel record in RoundTrip(Result(1, code: ReplaySender.TooLargeCode), exchange))
            {
                Assert.Equal($"HTTP/1.1 200 OK\r\n{name}: {value}\r\n{RequestReplayDraft.ResponseBodyNotKeptHeader}: {marker}\r\n\r\n", record.ResponseLineAndHeaders);
                Assert.Empty(record.ResponseBody);
                Assert.True(record.ResponseHeaders.ContainsKey(RequestReplayDraft.ResponseBodyNotKeptHeader));
            }
        }

        // A send without a complete response is a request-only session: the response, if any, is not written.
        [Theory]
        [InlineData("Failed")]
        [InlineData("TimedOut")]
        public void ASendWithoutACompleteResponse_IsARequestOnlySession(string name)
        {
            ReplayOutcome outcome = Enum.Parse<ReplayOutcome>(name);
            var uri = new Uri("http://example.test/none");
            ReplayExchange exchange = Exchange(
                uri, "GET", new[] { Header("Host", "example.test") }, [], Response("HTTP/1.1 200 OK", Array.Empty<KeyValuePair<string, string>>(), "partial"u8.ToArray()), outcome);

            SazSession session = ReplaySaz.ToSession(Result(1, outcome: outcome), exchange);
            Assert.Null(session.Response);
            foreach (ProxyConnectionModel record in RoundTrip(Result(1, outcome: outcome), exchange))
            {
                Assert.Equal(string.Empty, record.ResponseLineAndHeaders);
                Assert.Equal(("GET", "/none"), (record.Method, record.Path));
            }

            using ZipArchive archive = Archive((Result(1, outcome: outcome), exchange));
            Assert.Equal(new[] { "raw/1_c.txt", "raw/1_m.xml" }, archive.Entries.Select(entry => entry.FullName));
        }

        [Fact]
        public void TheFlags_NameTheSend_InTheirOrder()
        {
            var uri = new Uri("http://example.test/item/7");
            ReplayExchange exchange = Exchange(
                uri, "GET", new[] { Header("Host", "example.test") }, [], Response("HTTP/1.1 302 Found", Array.Empty<KeyValuePair<string, string>>(), []));
            exchange = With(exchange, redirects: 2, code: ReplaySender.RedirectLimitCode);
            ReplayItemResult result = Result(4, deviceId: "d2", requestNumber: 31, sequence: 7, code: ReplaySender.RedirectLimitCode) with { Redirects = 2 };

            using ZipArchive archive = Archive((result, exchange));

            Assert.Equal(
                new[]
                {
                    "x-clientip=0.0.0.0", "x-xmat-replay-id=4", "x-xmat-replay-of=31", "x-xmat-replay-device=d2",
                    "x-xmat-replay-sequence=7", "x-xmat-replay-outcome=completed",
                    "x-xmat-replay-code=" + ReplaySender.RedirectLimitCode, "x-xmat-replay-redirects=2",
                },
                Flags(archive, 4));
            Assert.Equal(
                new[] { "x-clientip=0.0.0.0", "x-xmat-replay-id=1", "x-xmat-replay-outcome=too-large", "x-xmat-replay-code=replay.response.too-large", "x-xmat-replay-redirects=0" },
                Flags(Archive((Result(1, code: ReplaySender.TooLargeCode), With(exchange, outcome: ReplayOutcome.TooLarge, code: ReplaySender.TooLargeCode, redirects: 0))), 1));
            Assert.Equal("x-xmat-replay-outcome=timed-out", Flags(Archive((Result(1), With(exchange, outcome: ReplayOutcome.TimedOut, code: null, redirects: 0))), 1)[2]);
            Assert.Equal("x-xmat-replay-outcome=failed", Flags(Archive((Result(1), With(exchange, outcome: ReplayOutcome.Failed, code: null, redirects: 0))), 1)[2]);
        }

        // A send that was not direct says how it was sent, by its transport's name and never its endpoint or proxy, and one
        // that could offer a client certificate or answer with Windows authentication says so, last and in that order, never
        // which certificate or label nor whether a server received or asked for either; a direct send's flags are unchanged.
        [Theory]
        [InlineData(ReplayTransportOptions.DirectName, false, false, null)]
        [InlineData(ReplayTransportOptions.ConnectToName, false, false, "x-xmat-replay-transport=connect-to")]
        [InlineData(ReplayTransportOptions.ProxyName, false, false, "x-xmat-replay-transport=proxy")]
        [InlineData(ReplayTransportOptions.DirectName, true, false, null)]
        [InlineData(ReplayTransportOptions.ConnectToName, true, false, "x-xmat-replay-transport=connect-to")]
        [InlineData(ReplayTransportOptions.DirectName, false, true, null)]
        [InlineData(ReplayTransportOptions.DirectName, true, true, null)]
        public void TheTransportFlags_NameASendThatWasNotDirect_OrCouldOfferACredential(
            string transport, bool clientCertificate, bool windowsAuth, string flag)
        {
            ReplayExchange exchange = Exchange(
                new Uri("http://example.test/"), "GET", new[] { Header("Host", "example.test") }, [], Response("HTTP/1.1 200 OK", Array.Empty<KeyValuePair<string, string>>(), []));

            string[] flags = Flags(
                Archive((Result(1) with
                {
                    Transport = transport,
                    ClientCertificateConfigured = clientCertificate,
                    WindowsAuthConfigured = windowsAuth,
                    AuthHeaderSchemeObserved = windowsAuth ? "NTLM" : null,
                }, exchange)),
                1);

            IEnumerable<string> expected = new[] { "x-clientip=0.0.0.0", "x-xmat-replay-id=1", "x-xmat-replay-outcome=completed", "x-xmat-replay-redirects=0" };
            if (flag != null)
            {
                expected = expected.Append(flag);
            }

            if (clientCertificate)
            {
                expected = expected.Append("x-xmat-replay-client-certificate-configured=true");
            }

            if (windowsAuth)
            {
                expected = expected.Append("x-xmat-replay-windows-auth-configured=true");
            }

            Assert.Equal(expected, flags);
        }

        // A raw send's session is its normalized request (RawRequestImage.ToMessage), read back as any send's: the target in
        // absolute form, Host first with the target origin's authority whatever Host was sent (EXAMPLE.test:8080 here), a
        // chunked body's framing replaced in place by Content-Length, and the entity. Its flags end with x-xmat-replay-raw
        // and its write state, whatever came back.
        [Theory]
        [InlineData(RawWriteState.Complete, true)]
        [InlineData(RawWriteState.Partial, false)]
        [InlineData(RawWriteState.Uncertain, false)]
        [InlineData(RawWriteState.None, false)]
        public void ARawSend_IsSavedNormalized_WithTheRawFlagAndItsWriteState(string write, bool responded)
        {
            Assert.True(ReplayRawTarget.TryParse("http://example.test:8080", out ReplayRawTarget target));
            RawRequestImage image = RawRequest.FromBytes(
                target,
                Encoding.Latin1.GetBytes(
                    "POST /p?q HTTP/1.1\r\nHost: EXAMPLE.test:8080\r\nTransfer-Encoding: chunked\r\nX-A:  a  \r\n\r\n5\r\nhello\r\n0\r\n\r\n"),
                false,
                CancellationToken.None).Image;
            string code = responded ? null : RawCodes.SendCancelled;
            var exchange = new ReplayExchange
            {
                Outcome = responded ? ReplayOutcome.Completed : ReplayOutcome.Failed,
                Code = code,
                Request = image.ToMessage(),
                RequestUri = image.RequestUri,
                Started = Started,
                Response = responded ? Response("HTTP/1.1 200 OK", new[] { Header("Content-Length", "0") }, []) : null,
                StatusCode = responded ? 200 : 0,
                Duration = TimeSpan.FromMilliseconds(25),
                Raw = true,
                RequestWrite = write,
            };
            ReplayItemResult result = Result(1, exchange.Outcome, code: code) with { Raw = true, RequestWrite = write };

            using ZipArchive archive = Archive((result, exchange));

            IEnumerable<string> expected = new[]
            {
                "x-clientip=0.0.0.0", "x-xmat-replay-id=1", "x-xmat-replay-outcome=" + (responded ? "completed" : "failed"),
            };
            expected = expected
                .Concat(code == null ? Array.Empty<string>() : new[] { "x-xmat-replay-code=" + code })
                .Concat(new[] { "x-xmat-replay-redirects=0", "x-xmat-replay-raw=true", "x-xmat-replay-request-write=" + write });
            Assert.Equal(expected, Flags(archive, 1));
            const string head = "POST http://example.test:8080/p?q HTTP/1.1\r\nHost: example.test:8080\r\nContent-Length: 5\r\nX-A: a\r\n\r\n";
            using (var request = new StreamReader(archive.GetEntry("raw/1_c.txt").Open(), Encoding.Latin1))
            {
                Assert.Equal(head + "hello", request.ReadToEnd());
            }

            Assert.Equal(responded, archive.GetEntry("raw/1_s.txt") != null);
            if (responded)
            {
                Assert.All(RoundTrip(result, exchange), record =>
                {
                    Assert.Equal(head, record.RequestLineAndHeaders);
                    Assert.Equal("hello"u8.ToArray(), record.RequestBody);
                });
            }
        }

        // The command line's flags keep to their alphabet, so a value outside it is a bug that stops the run.
        [Theory]
        [InlineData("d 1")]
        [InlineData("d\u00e91")]
        [InlineData("")]
        public void AFlagValueOutsideItsAlphabet_IsRefused(string deviceId)
        {
            ReplayExchange exchange = Exchange(
                new Uri("http://example.test/"), "GET", new[] { Header("Host", "example.test") }, [], Response("HTTP/1.1 200 OK", Array.Empty<KeyValuePair<string, string>>(), []));

            Assert.Throws<InvalidOperationException>(() => ReplaySaz.ToSession(Result(1, deviceId: deviceId), exchange));
            Assert.Throws<InvalidOperationException>(
                () => ReplaySaz.ToSession(Result(1, deviceId: "d" + new string('1', ReplaySaz.MaxFlagChars)), exchange));
        }

        // Sessions keep their plan Ids in the order they were added, which under --parallel is the order they completed.
        [Fact]
        public void Sessions_AreWrittenInTheOrderTheyCome_WithTheirIds()
        {
            ReplayExchange exchange = Exchange(
                new Uri("http://example.test/"), "GET", new[] { Header("Host", "example.test") }, [], Response("HTTP/1.1 200 OK", Array.Empty<KeyValuePair<string, string>>(), []));

            using ZipArchive archive = Archive((Result(3), exchange), (Result(1), exchange), (Result(2), exchange));

            Assert.Equal(
                new[] { "raw/3_c.txt", "raw/3_m.xml", "raw/3_s.txt", "raw/1_c.txt", "raw/1_m.xml", "raw/1_s.txt", "raw/2_c.txt", "raw/2_m.xml", "raw/2_s.txt" },
                archive.Entries.Select(entry => entry.FullName));
        }

        // The archive's Host is the authority that the target names, as the HTTP stack sends it, whatever Host replay
        // recorded: a recorded Host is replaced, and the other fields keep their order.
        [Fact]
        public void TheArchivedHost_IsTheAuthoritySent_WhateverHostWasRecorded()
        {
            var uri = new Uri("http://b\u00fccher.example:18081/item");
            ReplayExchange exchange = Exchange(
                uri,
                "GET",
                new[] { Header("Host", "b\u00fccher.example:18081"), Header("X-First", "1"), Header("host", "other.example"), Header("X-Last", "2") },
                [],
                Response("HTTP/1.1 200 OK", Array.Empty<KeyValuePair<string, string>>(), []));

            SazSession session = ReplaySaz.ToSession(Result(1), exchange);

            Assert.Equal("GET http://xn--bcher-kva.example:18081/item HTTP/1.1", session.Request.StartLine);
            Assert.Equal(
                new[] { "Host: xn--bcher-kva.example:18081", "X-First: 1", "X-Last: 2" },
                session.Request.Fields.Select(field => field.Key + ": " + field.Value));
        }

        [Fact]
        public void ASendThatRecordedNoRequest_HasNoSession()
        {
            var exchange = new ReplayExchange { Outcome = ReplayOutcome.Failed, RequestUri = new Uri("http://example.test/"), Started = Started };

            Assert.Null(ReplaySaz.ToSession(Result(1, outcome: ReplayOutcome.Failed), exchange));
        }

        private IEnumerable<ProxyConnectionModel> RoundTrip(ReplayItemResult result, ReplayExchange exchange)
        {
            byte[] bytes = Write((result, exchange));
            string path = Path.Join(_directory, Guid.NewGuid().ToString("N") + ".saz");
            File.WriteAllBytes(path, bytes);

            FiddlerSazImportResult imported = FiddlerSazHandler.ImportSazFileWithDiagnostics(path, CancellationToken.None);
            Assert.Equal((0, 0), (imported.SkippedIncompleteFrames, imported.SkippedMalformedHeaderLines));
            BoundedSazReadResult read = BoundedSazReader.Read(
                new MemoryStream(bytes),
                new BoundedSazLimits(1L << 30, 1L << 28, 1L << 30, 1000, 3064, BoundedSazReader.DefaultMaxCentralDirectoryBytes),
                CancellationToken.None);
            Assert.Equal((0, 0), (read.SkippedIncompleteFrames, read.SkippedMalformedHeaderLines));
            return new[] { Assert.Single(imported.Records), Assert.Single(read.Records) };
        }

        private static ZipArchive Archive(params (ReplayItemResult Result, ReplayExchange Exchange)[] sends) =>
            new(new MemoryStream(Write(sends)), ZipArchiveMode.Read);

        private static byte[] Write(params (ReplayItemResult Result, ReplayExchange Exchange)[] sends)
        {
            using var destination = new MemoryStream();
            using (var writer = new SazArchiveWriter(destination))
            {
                foreach ((ReplayItemResult result, ReplayExchange exchange) in sends)
                {
                    writer.Add(ReplaySaz.ToSession(result, exchange), CancellationToken.None);
                }

                writer.Complete(CancellationToken.None);
            }

            return destination.ToArray();
        }

        private static string[] Flags(ZipArchive archive, long id)
        {
            using Stream metadata = archive.GetEntry($"raw/{id}_m.xml").Open();
            return XDocument.Load(metadata).Root.Element("SessionFlags").Elements("SessionFlag")
                .Select(flag => (string)flag.Attribute("N") + "=" + (string)flag.Attribute("V"))
                .ToArray();
        }

        private static ReplayItemResult Result(
            int id,
            ReplayOutcome outcome = ReplayOutcome.Completed,
            string deviceId = null,
            long? requestNumber = null,
            long? sequence = null,
            string code = null) =>
            new(id, deviceId, requestNumber, 1, outcome, 200, TimeSpan.FromMilliseconds(25), 0, 0, code, null, Sequence: sequence);

        private static ReplayExchange Exchange(
            Uri uri,
            string method,
            KeyValuePair<string, string>[] headers,
            byte[] body,
            ReplayMessage response,
            ReplayOutcome outcome = ReplayOutcome.Completed)
        {
            var contentHeaders = body.Length == 0
                ? Array.Empty<KeyValuePair<string, string>>()
                : new[] { Header("Content-Length", body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
            return new ReplayExchange
            {
                Outcome = outcome,
                Code = outcome == ReplayOutcome.TooLarge ? ReplaySender.TooLargeCode : null,
                Request = new ReplayMessage($"{method} {uri.PathAndQuery} HTTP/1.1", headers, contentHeaders, body),
                RequestUri = uri,
                Started = Started,
                Response = response,
                StatusCode = response == null ? 0 : int.Parse(response.StartLine.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture),
                Duration = TimeSpan.FromMilliseconds(25),
            };
        }

        private static ReplayExchange With(
            ReplayExchange exchange,
            ReplayMessage response = null,
            ReplayOutcome? outcome = null,
            int? redirects = null,
            string code = "unchanged") =>
            new()
            {
                Outcome = outcome ?? exchange.Outcome,
                Code = code == "unchanged" ? exchange.Code : code,
                Request = exchange.Request,
                RequestUri = exchange.RequestUri,
                Started = exchange.Started,
                Response = response ?? exchange.Response,
                StatusCode = exchange.StatusCode,
                Duration = exchange.Duration,
                Redirects = redirects ?? exchange.Redirects,
            };

        private static ReplayMessage Response(string startLine, KeyValuePair<string, string>[] headers, byte[] body) =>
            new(startLine, headers, Array.Empty<KeyValuePair<string, string>>(), body);

        private static KeyValuePair<string, string> Header(string name, string value) => new(name, value);
    }
}
