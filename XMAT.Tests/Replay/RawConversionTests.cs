// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using XMAT.DataAbstractionLayer;
using XMAT.Operations.Replay;
using XMAT.WebServiceCapture;

namespace XMAT.Tests.Replay
{
    // The conversions between the composer's modes (slice L, contract 2.4; design 1.2 and 1.3): their refusals in order,
    // what each writes, each disclosure only when it happens, and that Send prepares exactly what Parsed to Raw wrote.
    public sealed class RawConversionTests
    {
        [Theory]
        [InlineData("/relative", "GET", "", "Url")]
        [InlineData("ftp://example.test/", "GET", "", "Url")]
        [InlineData("http://user@example.test/", "GET", "", "Url")]
        [InlineData("http://example.test/#f", "GET", "", "Url")]
        [InlineData("http://example.test/a#", "GET", "", "Url")]
        [InlineData("http://[fe80::1%253]/x", "GET", "", "Target")]
        [InlineData("http://example.test/", "", "", "Method")]
        [InlineData("http://example.test/", "GE T", "", "Method")]
        [InlineData("http://example.test/", "GE:T", "", "Method")]
        [InlineData("http://example.test/", "GET", "X-A value", "Headers")]
        [InlineData("http://example.test/", "GET", "X-A: a\rb", "Headers")]
        // Each earlier check wins over a later one.
        [InlineData("/relative", "", "X-A value", "Url")]
        [InlineData("http://[fe80::1%253]/x", "", "X-A value", "Target")]
        [InlineData("http://example.test/", "GE T", "X-A value", "Method")]
        public void FromDraft_Refuses_InItsOrder(string url, string method, string headers, string refusal)
        {
            var draft = new RequestReplayDraft { Url = url, Method = method, HeadersText = headers };

            RawFromDraftResult result = RawConversion.FromDraft(draft, 0, false, CancellationToken.None);

            Assert.Null(result.Value);
            Assert.Equal(Enum.Parse<RawFromDraftRefusal>(refusal), result.Refusal);
        }

        [Fact]
        public void FromDraft_RefusesWhatRawModesLimitsCannotHold()
        {
            var draft = new RequestReplayDraft { Url = "http://example.test/", Method = "GET" };
            Assert.Null(RawConversion.FromDraft(draft, RawRequest.MaxEntityBytes, false, CancellationToken.None).Refusal);
            Assert.Equal(
                RawFromDraftRefusal.Limits,
                RawConversion.FromDraft(draft, RawRequest.MaxEntityBytes + 1L, false, CancellationToken.None).Refusal);

            var method = new RequestReplayDraft { Url = "http://example.test/", Method = new string('M', 65) };
            Assert.Equal(RawFromDraftRefusal.Limits, RawConversion.FromDraft(method, 0, false, CancellationToken.None).Refusal);

            var head = new RequestReplayDraft
            {
                Url = "http://example.test/",
                Method = "GET",
                HeadersText = "X-Pad: " + new string('a', RawRequest.MaxHeadBytes),
            };
            Assert.Equal(RawFromDraftRefusal.Limits, RawConversion.FromDraft(head, 0, false, CancellationToken.None).Refusal);

            // A left-out line is not written, so it does not count.
            var leftOut = new RequestReplayDraft
            {
                Url = "http://example.test/",
                Method = "GET",
                HeadersText = "Proxy-Authorization: " + new string('a', RawRequest.MaxHeadBytes),
            };
            Assert.Null(RawConversion.FromDraft(leftOut, 0, false, CancellationToken.None).Refusal);
        }

        [Fact]
        public void FromDraft_WritesHostFirst_TheLinesParsedModeSends_AndContentLength_WithEachLoss()
        {
            var draft = new RequestReplayDraft
            {
                Method = "PUT",
                Url = "https://Example.TEST:443/a b?q=1",
                HeadersText =
                    "X-A:   spaced  \r\n" +
                    "Host: other.test\r\n" +
                    "Connection: close, X-Hop\r\n" +
                    "X-Hop: 1\r\n" +
                    "Content-Length: 99\r\n" +
                    "Transfer-Encoding: chunked\r\n" +
                    "Proxy-Authorization: Basic x\r\n" +
                    "keep-alive: 5\r\n" +
                    "Expect: 100-continue\r\n" +
                    "Upgrade: h2c\r\n" +
                    "X-OverrideHost: other.test\r\n" +
                    "x-a: again\r\n" +
                    "X-Text: caf\u00e9",
            };

            RawFromDraft raw = RawConversion.FromDraft(draft, 5, true, CancellationToken.None).Value;

            Assert.Equal("https://example.test", raw.TargetText);
            Assert.Equal(ParseTarget("https://example.test"), raw.Target);
            Assert.Equal(
                "PUT /a%20b?q=1 HTTP/1.1\r\n" +
                "Host: example.test\r\n" +
                "X-A: spaced\r\n" +
                "Content-Length: 5\r\n" +
                "Expect: 100-continue\r\n" +
                "Upgrade: h2c\r\n" +
                "X-OverrideHost: other.test\r\n" +
                "x-a: again\r\n" +
                "X-Text: caf\u00e9",
                raw.HeadText);
            Assert.Equal(
                new[]
                {
                    new RawDisclosure(RawDisclosureKind.LeftOut, "Connection, X-Hop, Transfer-Encoding, Proxy-Authorization, keep-alive"),
                    new RawDisclosure(RawDisclosureKind.HostWritten, "example.test"),
                    new RawDisclosure(RawDisclosureKind.LengthWritten, "5"),
                    new RawDisclosure(RawDisclosureKind.Spacing, null),
                    new RawDisclosure(RawDisclosureKind.TargetEscaped, "/a%20b?q=1"),
                    new RawDisclosure(RawDisclosureKind.FormParts, null),
                },
                raw.Disclosures);
        }

        [Theory]
        [InlineData("http://example.test", "GET / HTTP/1.1\r\nHost: example.test\r\nContent-Length: 0")]
        [InlineData("http://example.test?q", "GET /?q HTTP/1.1\r\nHost: example.test\r\nContent-Length: 0")]
        [InlineData("http://[::1]:8080/x", "GET /x HTTP/1.1\r\nHost: [::1]:8080\r\nContent-Length: 0")]
        [InlineData("http://b\u00fccher.example/x", "GET /x HTTP/1.1\r\nHost: xn--bcher-kva.example\r\nContent-Length: 0")]
        public void FromDraft_DisclosesNothing_WhenNothingIsLost(string url, string head)
        {
            var draft = new RequestReplayDraft { Method = "GET", Url = url, HeadersText = "Host: " + Authority(url) };

            RawFromDraft raw = RawConversion.FromDraft(draft, 0, false, CancellationToken.None).Value;

            Assert.Equal(head, raw.HeadText);
            Assert.Empty(raw.Disclosures);
        }

        [Fact]
        public void FromDraft_WritesContentLength_InPlaceOfTheEditorsLine_OrLast()
        {
            var inPlace = new RequestReplayDraft
            {
                Method = "POST",
                Url = "http://example.test/p",
                HeadersText = "X-A: 1\r\ncontent-length: 2\r\nX-B: 2",
            };
            Assert.Equal(
                "POST /p HTTP/1.1\r\nHost: example.test\r\nX-A: 1\r\nContent-Length: 2\r\nX-B: 2",
                RawConversion.FromDraft(inPlace, 2, false, CancellationToken.None).Value.HeadText);
            Assert.Empty(RawConversion.FromDraft(inPlace, 2, false, CancellationToken.None).Value.Disclosures);

            var last = new RequestReplayDraft { Method = "POST", Url = "http://example.test/p", HeadersText = "X-A: 1" };
            Assert.Equal(
                "POST /p HTTP/1.1\r\nHost: example.test\r\nX-A: 1\r\nContent-Length: 2",
                RawConversion.FromDraft(last, 2, false, CancellationToken.None).Value.HeadText);
        }

        [Fact]
        public void SendPrepares_ExactlyWhatParsedToRawWrote()
        {
            var draft = new RequestReplayDraft
            {
                Method = "POST",
                Url = "http://example.test/p?x=1",
                HeadersText = "Content-Type: text/plain\r\nX-A: 1",
            };
            byte[] body = Encoding.UTF8.GetBytes("hello");
            RawFromDraft raw = RawConversion.FromDraft(draft, body.Length, false, CancellationToken.None).Value;

            RawImageResult prepared = RawRequest.Prepare(raw.HeadText, body, raw.Target, CancellationToken.None);

            Assert.Null(prepared.Code);
            Assert.Equal(
                Encoding.ASCII.GetBytes(
                    "POST /p?x=1 HTTP/1.1\r\nHost: example.test\r\nContent-Type: text/plain\r\nX-A: 1\r\n" +
                    "Content-Length: 5\r\n\r\nhello"),
                prepared.Image.CopyHead().Concat(prepared.Image.CopyWireBody()));
            Assert.Equal(0, prepared.Image.ConvertedLineFeeds);
        }

        [Fact]
        public void ToDraft_KeepsEachFieldLineAsWritten_ButHostAndContentLength_AndShowsTheBodyAsFromCaptureDoes()
        {
            RawEditorParse parse = Parse(
                "POST /p?x=1 HTTP/1.1\r\nHost: example.test\r\nX-A:  spaced  \r\nContent-Length: 5\r\nContent-Type: text/plain",
                "hello");

            RawToDraft converted = RawConversion.ToDraft(parse, null, 1024, false, CancellationToken.None).Value;

            RequestReplayDraft draft = converted.Draft;
            Assert.Equal(("POST", "http://example.test/p?x=1"), (draft.Method, draft.Url));
            Assert.Equal("X-A:  spaced  \r\nContent-Type: text/plain", draft.HeadersText);
            Assert.Equal(("hello", false), (draft.BodyText, draft.BodyIsBase64));
            Assert.Null(draft.CapturedOrigin);
            Assert.Empty(converted.Disclosures);
        }

        [Fact]
        public void ToDraft_DecodesAChunkedBody_AndDisclosesEachLoss()
        {
            RawEditorParse parse = Parse(
                "POST http://example.test:80/a/./b?c HTTP/1.1\r\nHost: example.test:80\r\nConnection: close, X-Hop\r\n" +
                "X-Hop: 1\r\nProxy-Authorization: Basic x\r\nTransfer-Encoding: chunked",
                "5\r\nhello\r\n0\r\n\r\n");

            RawToDraft converted = RawConversion.ToDraft(parse, null, 1024, true, CancellationToken.None).Value;

            Assert.Equal("http://example.test/a/./b?c", converted.Draft.Url);
            Assert.Equal("Connection: close, X-Hop\r\nX-Hop: 1\r\nProxy-Authorization: Basic x", converted.Draft.HeadersText);
            Assert.Equal("hello", converted.Draft.BodyText);
            Assert.Equal(
                new[]
                {
                    new RawDisclosure(RawDisclosureKind.ChunkedDecoded, null),
                    new RawDisclosure(RawDisclosureKind.AbsoluteTarget, null),
                    new RawDisclosure(RawDisclosureKind.TargetNormalized, "/a/b?c"),
                    new RawDisclosure(RawDisclosureKind.NotSentByParsed, "Connection, X-Hop, Proxy-Authorization"),
                    new RawDisclosure(RawDisclosureKind.VersionAutomatic, null),
                },
                converted.Disclosures);
        }

        [Fact]
        public void ToDraft_ShowsEncodedOrBinaryBodiesAsBase64_AndLargeOnesAsTheirBytes()
        {
            RawToDraft encoded = RawConversion.ToDraft(
                Parse("POST /p HTTP/1.1\r\nHost: example.test\r\nContent-Encoding: gzip\r\nContent-Length: 2", "hi"),
                null, 1024, false, CancellationToken.None).Value;
            Assert.Equal((Convert.ToBase64String(Encoding.ASCII.GetBytes("hi")), true), (encoded.Draft.BodyText, encoded.Draft.BodyIsBase64));

            RawToDraft binary = RawConversion.ToDraft(
                Parse("POST /p HTTP/1.1\r\nHost: example.test\r\nContent-Length: 2", "\u0001\u00ff"),
                null, 1024, false, CancellationToken.None).Value;
            Assert.Equal(("Af8=", true), (binary.Draft.BodyText, binary.Draft.BodyIsBase64));

            RawToDraft held = RawConversion.ToDraft(
                Parse("POST /p HTTP/1.1\r\nHost: example.test\r\nContent-Length: 5", "hello"),
                null, 4, false, CancellationToken.None).Value;
            Assert.Equal(Encoding.ASCII.GetBytes("hello"), held.Draft.GetBodyBytes());
        }

        [Fact]
        public void ToDraft_KeepsTheProvenancesCapturedOrigin_SoAModeSwitchCannotLaunderIt()
        {
            var origin = new Uri("https://captured.example/");
            RequestReplayDraft provenance = new RequestReplayDraft { Url = "https://captured.example/x" }.WithCapturedOrigin(origin);
            RawEditorParse parse = Parse("GET /x HTTP/1.1\r\nHost: example.test", string.Empty);

            RequestReplayDraft draft = RawConversion.ToDraft(parse, provenance, 1024, false, CancellationToken.None).Value.Draft;

            Assert.Same(origin, draft.CapturedOrigin);
            Assert.True(draft.ChangesOrigin);
            Assert.Null(RawConversion.ToDraft(parse, new RequestReplayDraft(), 1024, false, CancellationToken.None).Value.Draft.CapturedOrigin);
        }

        // Raw to Parsed keeps the stored-text source of the draft that the request came from, through both conversions and
        // next to its captured origin, so a mode switch cannot relabel unmarked text as Literal (L2-QP-RAW). Without a
        // draft, the source is Unknown, never Literal.
        [Fact]
        public void ToDraft_KeepsTheProvenancesStoredTextSource_SoAModeSwitchCannotRelabelIt()
        {
            CaptureTextSource unmarked = CaptureTextSource.CreateFile(
                CaptureTextSemantics.AmbiguousUnmarked, default, _ => new byte[32], () => true);
            var origin = new Uri("https://captured.example/");
            RequestReplayDraft provenance = new RequestReplayDraft
            {
                Method = "GET",
                Url = "https://captured.example/x",
                CaptureTextSource = unmarked,
            }.WithCapturedOrigin(origin);
            RawFromDraft raw = RawConversion.FromDraft(provenance, 0, false, CancellationToken.None).Value;
            RawEditorParse parse = RawRequest.ParseForEditor(raw.HeadText, Array.Empty<byte>(), raw.Target, CancellationToken.None).Parse;

            RequestReplayDraft back = RawConversion.ToDraft(parse, provenance, 1024, false, CancellationToken.None).Value.Draft;

            Assert.Same(unmarked, back.CaptureTextSource);
            Assert.Same(origin, back.CapturedOrigin);
            Assert.Same(unmarked, back.WithUrl(back.Url).CaptureTextSource);
            Assert.Same(
                CaptureTextSource.Unknown,
                RawConversion.ToDraft(parse, new RequestReplayDraft { CaptureTextSource = CaptureTextSource.Unknown }, 1024, false, CancellationToken.None)
                    .Value.Draft.CaptureTextSource);
            Assert.Same(
                CaptureTextSource.Literal,
                RawConversion.ToDraft(parse, new RequestReplayDraft(), 1024, false, CancellationToken.None).Value.Draft.CaptureTextSource);
            Assert.Same(
                CaptureTextSource.Unknown,
                RawConversion.ToDraft(parse, null, 1024, false, CancellationToken.None).Value.Draft.CaptureTextSource);
        }

        [Fact]
        public void ARoundTripWithoutEdits_ComesBackAsItWas()
        {
            var draft = new RequestReplayDraft
            {
                Method = "PATCH",
                Url = "http://example.test/items/7?v=2",
                HeadersText = "Content-Type: application/json\r\nX-A: 1",
                BodyText = "{\"a\":\"b\"}",
            };
            byte[] body = draft.GetBodyBytes();
            RawFromDraft raw = RawConversion.FromDraft(draft, body.Length, false, CancellationToken.None).Value;
            RawEditorParse parse = RawRequest.ParseForEditor(raw.HeadText, body, raw.Target, CancellationToken.None).Parse;

            RawToDraft back = RawConversion.ToDraft(parse, draft, 1024, false, CancellationToken.None).Value;

            Assert.Empty(raw.Disclosures);
            Assert.Empty(back.Disclosures);
            Assert.Equal(
                (draft.Method, draft.Url, draft.HeadersText, draft.BodyText, draft.BodyIsBase64),
                (back.Draft.Method, back.Draft.Url, back.Draft.HeadersText, back.Draft.BodyText, back.Draft.BodyIsBase64));
        }

        [Fact]
        public void BothConversions_StopWhenCancelled()
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var draft = new RequestReplayDraft { Method = "GET", Url = "http://example.test/" };
            RawEditorParse parse = Parse("GET / HTTP/1.1\r\nHost: example.test", string.Empty);

            Assert.Throws<OperationCanceledException>(() => RawConversion.FromDraft(draft, 0, false, cancelled.Token));
            Assert.Throws<OperationCanceledException>(() => RawConversion.ToDraft(parse, null, 1024, false, cancelled.Token));
        }

        // The reader that FromDraft and the request file checks share reads every text as ParseHeaderText does: the same
        // lines, names and values, and a FormatException for the same texts.
        [Fact]
        public void TheHeaderLineReader_ReadsEachTextAsParseHeaderTextDoes()
        {
            var texts = new List<string>
            {
                "", "\r\n", "X-A: 1", "X-A: 1\r", "X-A: 1\r\r\n", "X-A: a\rb", "bad line\rX", " \t\r\n\v\f\u00a0\nX-A:", ":x",
                "X-A:\t \t", "X-A: \u0085", "X-A: a\u007fb", "X\u00e9: 1", "X-A: 1\n\nX-B:2\r\n\r\n", "X-A: a:b ",
            };
            var random = new Random(20261108);
            for (int index = 0; index < 4_000; index++)
            {
                texts.Add(RandomHeaders(random));
            }

            foreach (string text in texts)
            {
                List<KeyValuePair<string, string>> expected = null;
                List<(string Name, string Raw, string Value)> actual = null;
                try
                {
                    expected = RequestReplayDraft.ParseHeaderText(text).ToList();
                }
                catch (FormatException)
                {
                }

                try
                {
                    actual = ReadLines(text);
                }
                catch (FormatException)
                {
                }

                Assert.True((expected == null) == (actual == null), Escape(text));
                if (expected != null)
                {
                    Assert.Equal(expected, actual.Select(line => new KeyValuePair<string, string>(line.Name, line.Value)));
                    Assert.All(actual, line => Assert.Equal(line.Raw.Trim(' ', '\t'), line.Value));
                }
            }
        }

        // Below the header preparation budget, FromDraft's three passes give exactly what the frozen rendering gave.
        [Fact]
        public void FromDraft_AgreesWithTheFrozenRendering_BelowTheBudget()
        {
            string[] urls = { "http://example.test/", "https://Example.TEST:443/a b?q=1", "http://[::1]:8080/x", "http://example.test/p?x=%41" };
            string[] methods = { "GET", "POST", "M" };
            long[] lengths = { 0, 5, 99 };
            var random = new Random(20261107);
            for (int index = 0; index < 4_000; index++)
            {
                var draft = new RequestReplayDraft
                {
                    Url = urls[random.Next(urls.Length)],
                    Method = methods[random.Next(methods.Length)],
                    HeadersText = RandomHeaders(random),
                };
                long length = lengths[random.Next(lengths.Length)];
                bool formBody = random.Next(2) == 0;

                AssertSameResult(
                    FrozenFromDraft(draft, length, formBody),
                    RawConversion.FromDraft(draft, length, formBody, CancellationToken.None),
                    Escape(draft.HeadersText));
            }
        }

        // At each of Raw mode's limits, and one past it, FromDraft and the frozen rendering agree: it checks each line before
        // it writes it, against exactly the limits that RawRequest counts.
        [Fact]
        public void FromDraft_AgreesWithTheFrozenRendering_AtRawModesLimits()
        {
            var accepted = new List<bool>();
            void Check(string url, string method, string headers)
            {
                var draft = new RequestReplayDraft { Url = url, Method = method, HeadersText = headers };
                RawFromDraftResult expected = FrozenFromDraft(draft, 0, false);
                AssertSameResult(expected, RawConversion.FromDraft(draft, 0, false, CancellationToken.None), url + " " + method);
                accepted.Add(expected.Refusal == null);
            }

            // Field lines: Host, the kept lines and Content-Length, at most 256.
            foreach (int kept in new[] { 253, 254, 255 })
            {
                Check("http://example.test/", "GET", string.Join("\r\n", Enumerable.Range(0, kept).Select(i => $"X-L{i}: 1")));
                Check("http://example.test/", "GET", "Content-Length: 1\r\n" + string.Join("\r\n", Enumerable.Range(0, kept).Select(i => $"X-L{i}: 1")));
            }

            Assert.Equal(new[] { true, true, true, true, false, false }, accepted);

            // The head's bytes, with Content-Length last or in place: a padding value that makes it exactly 64 KiB, and one
            // character either side; a left-out line does not count.
            accepted.Clear();
            int room = RawRequest.MaxHeadBytes - 4 - "GET / HTTP/1.1\r\nHost: example.test\r\nX-Pad: \r\nContent-Length: 0".Length;
            foreach (int pad in new[] { room - 1, room, room + 1 })
            {
                Check("http://example.test/", "GET", "X-Pad: " + new string('a', pad));
                Check("http://example.test/", "GET", "Content-Length: 9\r\nX-Pad: " + new string('a', pad));
                Check("http://example.test/", "GET", "X-Pad: " + new string('a', pad) + "\r\nTE: " + new string('t', 70_000));
            }

            Assert.Equal(new[] { true, true, true, true, true, true, false, false, false }, accepted);

            // The request line, at most 16 KiB with its CRLF, and the method, at most 64 characters.
            accepted.Clear();
            int path = RawRequest.MaxRequestLineBytes - "GET / HTTP/1.1\r\n".Length;
            Check("http://example.test/" + new string('p', path), "GET", "X-A: 1");
            Check("http://example.test/" + new string('p', path + 1), "GET", "X-A: 1");
            Check("http://example.test/", new string('M', RawRequest.MaxMethodChars), "X-A: 1");
            Check("http://example.test/", new string('M', RawRequest.MaxMethodChars + 1), "X-A: 1");
            Assert.Equal(new[] { true, false, true, false }, accepted);
        }

        // D-L1-1: Parsed to Raw holds at most 1,024 distinct candidate names, of at most 256 KiB together, counted once in
        // any case before Connection nominations are applied; Host, Content-Length, the hop-by-hop fields, values and
        // nomination tokens never count. More is Limits, even when nominations would have left the head small, and a line
        // that is not a header still makes it Headers.
        [Fact]
        public void FromDraft_HoldsAtMost1024CandidateNames_Of256KiB_BeforeConnectionNominatesAny()
        {
            static string Names(int count) => string.Join("\r\n", Enumerable.Range(0, count).Select(i => $"X-N{i}: 1"));
            static string Nominating(int count) =>
                "Connection: " + string.Join(", ", Enumerable.Range(0, count).Select(i => $"x-n{i}"));
            static string Long(char first, int chars) => first + new string('a', chars - 1);
            const string Empty = "GET / HTTP/1.1\r\nHost: example.test\r\nContent-Length: 0";

            foreach (string headers in new[] { Names(1_024) + "\r\n" + Nominating(1_024), Nominating(1_024) + "\r\n" + Names(1_024) })
            {
                RawFromDraftResult result = ToRaw(headers);
                Assert.Equal(Empty, result.Value.HeadText);
                Assert.Equal(1_025, Assert.Single(result.Value.Disclosures).Detail.Split(", ").Length);
                AssertSameResult(FrozenFromDraft(Draft(headers), 0, false), result, "at the budget");
            }

            foreach (string headers in new[] { Names(1_025) + "\r\n" + Nominating(1_025), Nominating(1_025) + "\r\n" + Names(1_025) })
            {
                Assert.Null(FrozenFromDraft(Draft(headers), 0, false).Refusal);
                Assert.Equal(RawFromDraftRefusal.Limits, ToRaw(headers).Refusal);
            }

            string uncounted = Names(1_024) + "\r\n" + Names(1_024).ToLowerInvariant() + "\r\n" + Nominating(1_024) +
                "\r\nHost: example.test\r\nContent-Length: 0\r\nKeep-Alive: 5\r\nTE: trailers\r\nTrailer: X-T\r\n" +
                "Transfer-Encoding: chunked\r\nProxy-Authenticate: Basic\r\nProxy-Connection: close\r\n" +
                "Proxy-Authorization: " + new string('v', 1_048_576) + "\r\nConnection: " +
                string.Join(", ", Enumerable.Range(0, 50_000).Select(i => $"t{i}"));
            Assert.Equal(Empty, ToRaw(uncounted).Value.HeadText);

            string four = string.Join("\r\n", "ABCD".Select(c => Long(c, 65_536) + ": 1"));
            string nominateFour = "Connection: " + string.Join(", ", "ABCD".Select(c => Long(c, 65_536)));
            Assert.Equal(Empty, ToRaw(four + "\r\n" + nominateFour).Value.HeadText);
            Assert.Equal(RawFromDraftRefusal.Limits, ToRaw(four + "\r\nE: 1\r\n" + nominateFour + ", E").Refusal);
            Assert.Equal(Empty, ToRaw(Long('A', 262_144) + ": 1\r\nConnection: " + Long('A', 262_144)).Value.HeadText);
            Assert.Equal(RawFromDraftRefusal.Limits, ToRaw(Long('A', 262_145) + ": 1\r\nConnection: " + Long('A', 262_145)).Refusal);
            Assert.Equal(RawFromDraftRefusal.Limits, ToRaw(Long('A', 262_145) + ": 1").Refusal);

            // Past the budget, or any other limit, every later line is still checked, however far it is.
            Assert.Equal(RawFromDraftRefusal.Headers, ToRaw(Names(1_025) + "\r\nX-Later: 1\r\nbad line").Refusal);
            Assert.Equal(RawFromDraftRefusal.Headers, ToRaw(Long('A', 262_145) + ": 1\r\nX-Later: 1\r\nX-B: a\u0001b").Refusal);
            Assert.Equal(RawFromDraftRefusal.Headers, ToRaw(Names(1_025) + "\r\nX-Later: 1\r\nX-B: 1\r").Refusal);
            Assert.Equal(
                RawFromDraftRefusal.Headers,
                RawConversion.FromDraft(Draft("X-A: 1\r\nX-B: 2\r\nbad line"), RawRequest.MaxEntityBytes + 1L, false, CancellationToken.None).Refusal);
        }

        // D-L1-2: a URL longer than a request file holds, 65,536 characters, is refused as Url before any of it is parsed,
        // even when its path would normalize to fit the request line, and ahead of every later check. At 65,536
        // characters it is parsed and checked as before.
        [Fact]
        public void FromDraft_RefusesAUrlLongerThanARequestFileHolds_AheadOfEveryLaterCheck()
        {
            const string Origin = "http://example.test/";
            int room = RequestFileLimits.MaxUrlChars - Origin.Length;
            static RawFromDraftResult Convert(string url, string method = "GET", string headers = "X-A: 1", long body = 0) =>
                RawConversion.FromDraft(
                    new RequestReplayDraft { Url = url, Method = method, HeadersText = headers }, body, false, CancellationToken.None);

            // At the length: a long path is over the request line, and one whose dot segments normalize away converts.
            Assert.Equal(RawFromDraftRefusal.Limits, Convert(Origin + new string('p', room)).Refusal);
            string dots = string.Concat(Enumerable.Repeat("a/../", room / 5));
            string atLimit = Origin + dots + new string('b', room - dots.Length);
            Assert.Equal(RequestFileLimits.MaxUrlChars, atLimit.Length);
            Assert.Equal("GET /b HTTP/1.1\r\nHost: example.test\r\nX-A: 1\r\nContent-Length: 0", Convert(atLimit).Value.HeadText);

            // One character more is Url, whatever the URL would normalize to and whatever else is wrong.
            Assert.Equal(RawFromDraftRefusal.Url, Convert(Origin + new string('p', room + 1)).Refusal);
            Assert.Equal(RawFromDraftRefusal.Url, Convert(atLimit + "b").Refusal);
            Assert.Equal(
                RawFromDraftRefusal.Url,
                Convert(atLimit + "b", "GE T", "bad line", RawRequest.MaxEntityBytes + 1L).Refusal);
            Assert.Equal(RawFromDraftRefusal.Url, Convert("http://[fe80::1%253]/" + new string('p', room)).Refusal);
        }

        // A URL over the length is refused without parsing it: a multi-MiB URL, valid or not, allocates nothing in
        // proportion to it.
        [Fact]
        public void FromDraft_RefusesALongUrl_WithoutParsingIt()
        {
            var valid = new RequestReplayDraft { Url = "http://example.test/" + new string('a', 8 * 1024 * 1024), Method = "GET" };
            var malformed = new RequestReplayDraft { Url = "http://exa mple.test/" + new string('a', 8 * 1024 * 1024), Method = "GET" };
            RawConversion.FromDraft(
                new RequestReplayDraft { Url = "http://example.test/" + new string('a', RequestFileLimits.MaxUrlChars), Method = "GET" },
                0,
                false,
                CancellationToken.None);

            foreach (RequestReplayDraft draft in new[] { valid, malformed })
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                RawFromDraftResult result = RawConversion.FromDraft(draft, 0, false, CancellationToken.None);
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

                Assert.Equal(RawFromDraftRefusal.Url, result.Refusal);
                Assert.InRange(allocated, 0, 64 * 1024);
            }
        }

        // The header text is read in place: a multi-MiB value, left out or over the limits, allocates nothing in proportion
        // to it.
        [Fact]
        public void FromDraft_ReadsTheHeaderTextInPlace_AllocatingNothingInProportionToIt()
        {
            string value = new string('v', 8 * 1024 * 1024);
            RequestReplayDraft omitted = Draft("X-A: 1\r\nProxy-Authorization: " + value + "\r\nX-B: 2");
            RequestReplayDraft kept = Draft("X-A: 1\r\nX-Big: " + value + "\r\nX-B: 2");
            Assert.Null(ToRaw("X-A: 1\r\nProxy-Authorization: v\r\nX-B: 2").Refusal);
            Assert.Equal(RawFromDraftRefusal.Limits, ToRaw("X-A: 1\r\nX-Big: " + new string('v', 70_000) + "\r\nX-B: 2").Refusal);

            long before = GC.GetAllocatedBytesForCurrentThread();
            RawFromDraftResult leftOut = RawConversion.FromDraft(omitted, 0, false, CancellationToken.None);
            long leftOutAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
            before = GC.GetAllocatedBytesForCurrentThread();
            RawFromDraftResult over = RawConversion.FromDraft(kept, 0, false, CancellationToken.None);
            long overAllocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal("GET / HTTP/1.1\r\nHost: example.test\r\nX-A: 1\r\nX-B: 2\r\nContent-Length: 0", leftOut.Value.HeadText);
            Assert.Equal(RawFromDraftRefusal.Limits, over.Refusal);
            Assert.InRange(leftOutAllocated, 0, 64 * 1024);
            Assert.InRange(overAllocated, 0, 64 * 1024);
        }

        // Each of FromDraft's scans of the header text, its Connection values' included, checks the token every
        // RawRequest.CheckChars characters, and a cancellation stops it at its next check.
        [Fact]
        public void FromDraft_ChecksItsToken_InEachOfItsScans()
        {
            RequestReplayDraft draft = Draft(
                "X-A: 1\r\nConnection: " + string.Join(",", Enumerable.Range(0, 40_000).Select(i => "t" + i)) + ", X-A");
            var reads = new List<long>();
            RawFromDraftResult result = RawConversion.FromDraft(draft, 0, false, CancellationToken.None, reads.Add);
            Assert.Equal("GET / HTTP/1.1\r\nHost: example.test\r\nContent-Length: 0", result.Value.HeadText);
            Assert.Equal(new RawDisclosure(RawDisclosureKind.LeftOut, "X-A, Connection"), Assert.Single(result.Value.Disclosures));

            // The lines' check, the Connection lines, their value's names, and the rendering: each reads from the start.
            List<int> scans = Enumerable.Range(0, reads.Count).Where(index => reads[index] == RawRequest.CheckChars).ToList();
            Assert.Equal(4, scans.Count);
            for (int index = 1; index < reads.Count; index++)
            {
                Assert.True(scans.Contains(index) || reads[index] == reads[index - 1] + RawRequest.CheckChars);
            }

            Assert.All(scans, start => Assert.Equal(2L * RawRequest.CheckChars, reads[start + 1]));
            foreach (int start in scans)
            {
                using var cancellation = new CancellationTokenSource();
                int calls = 0;
                Assert.Throws<OperationCanceledException>(() => RawConversion.FromDraft(
                    draft, 0, false, cancellation.Token, read =>
                    {
                        if (++calls == start + 1)
                        {
                            cancellation.Cancel();
                        }
                    }));
                Assert.Equal(start + 1, calls);
            }

            // The method is read the same way.
            var method = new RequestReplayDraft { Url = "http://example.test/", Method = new string('M', 3 * RawRequest.CheckChars) };
            using var methodCancellation = new CancellationTokenSource();
            var methodChecks = new List<long>();
            Assert.Throws<OperationCanceledException>(() => RawConversion.FromDraft(
                method, 0, false, methodCancellation.Token, read =>
                {
                    methodChecks.Add(read);
                    methodCancellation.Cancel();
                }));
            Assert.Equal(new long[] { RawRequest.CheckChars }, methodChecks);

            // A multi-MiB line, left out or over the limits, is read the same way: here cancelled at the third check.
            string value = new string('v', 4 * 1024 * 1024);
            foreach (string headers in new[] { "X-A: 1\r\nProxy-Authorization: " + value, "X-A: 1\r\nX-Big: " + value })
            {
                using var lineCancellation = new CancellationTokenSource();
                var lineChecks = new List<long>();
                Assert.Throws<OperationCanceledException>(() => RawConversion.FromDraft(
                    Draft(headers), 0, false, lineCancellation.Token, read =>
                    {
                        lineChecks.Add(read);
                        if (lineChecks.Count == 3)
                        {
                            lineCancellation.Cancel();
                        }
                    }));
                Assert.Equal(new[] { 1L, 2L, 3L }.Select(count => count * RawRequest.CheckChars), lineChecks);
            }
        }

        // Every character that a scan reads counts toward its next check, a Connection value's commas and each CRLF's LF
        // included, so a value of commas, or text of empty lines, is checked as often as any other text.
        [Fact]
        public void FromDraft_CountsEveryCharacterItReads_CommasAndEachCrlfsLfIncluded()
        {
            long[] three = { RawRequest.CheckChars, 2L * RawRequest.CheckChars, 3L * RawRequest.CheckChars };
            const string Kept = "GET / HTTP/1.1\r\nHost: example.test\r\nX-A: 1\r\nContent-Length: 0";

            // The lines' check, the Connection lines, the value's names and the rendering each read the commas.
            RequestReplayDraft commas = Draft("X-A: 1\r\nConnection: " + new string(',', 3 * RawRequest.CheckChars));
            var reads = new List<long>();
            Assert.Equal(Kept, RawConversion.FromDraft(commas, 0, false, CancellationToken.None, reads.Add).Value.HeadText);
            Assert.Equal(Enumerable.Repeat(three, 4).SelectMany(read => read), reads);

            using var cancellation = new CancellationTokenSource();
            int calls = 0;
            Assert.Throws<OperationCanceledException>(() => RawConversion.FromDraft(
                commas, 0, false, cancellation.Token, read =>
                {
                    if (++calls == 7)
                    {
                        cancellation.Cancel();
                    }
                }));
            Assert.Equal(7, calls);

            // The lines' check and the rendering each read every CR and LF of the empty lines.
            RequestReplayDraft breaks = Draft("X-A: 1" + string.Concat(Enumerable.Repeat("\r\n", 3 * RawRequest.CheckChars / 2)));
            reads.Clear();
            Assert.Equal(Kept, RawConversion.FromDraft(breaks, 0, false, CancellationToken.None, reads.Add).Value.HeadText);
            Assert.Equal(Enumerable.Repeat(three, 2).SelectMany(read => read), reads);
        }

        private static readonly string[] GeneratedNames =
        {
            "Host", "host", "Content-Length", "CONTENT-LENGTH", "Connection", "connection", "Keep-Alive", "TE", "te", "Trailer",
            "Transfer-Encoding", "Proxy-Authorization", "Proxy-Authenticate", "proxy-connection", "X-A", "x-a", "X-B", "X-Hop",
            "x-hop", "Upgrade", "Expect", "X-Text",
        };

        private static readonly string[] GeneratedValues =
        {
            "", "1", "close", "X-A", "x-hop, X-B", " , X-Hop ,, x-a ", "example.test", "5", "a:b", "caf\u00e9", "Keep-Alive, TE",
            "Host", "Content-Length, x-a", "\t,\u00a0X-B\u00a0",
        };

        private static readonly string[] GeneratedSpacing = { "", " ", "  ", "\t", " \t" };

        private static readonly string[] GeneratedBlankLines = { "", "  ", "\t", "\u00a0", "\v\f" };

        private static readonly string[] GeneratedInvalidLines =
        {
            "bad line", ":x", "X A: 1", "X-A: a\u0001b", "X-A: a\rb", "\u00e9: 1", "X-A: \u0085",
        };

        private static string RandomHeaders(Random random)
        {
            static string Pick(Random random, string[] items) => items[random.Next(items.Length)];
            var text = new StringBuilder();
            int count = random.Next(0, 14);
            for (int line = 0; line < count; line++)
            {
                int kind = random.Next(100);
                if (kind < 4)
                {
                    text.Append(Pick(random, GeneratedBlankLines));
                }
                else if (kind < 6)
                {
                    text.Append(Pick(random, GeneratedInvalidLines));
                }
                else
                {
                    text.Append(Pick(random, GeneratedNames)).Append(':').Append(Pick(random, GeneratedSpacing))
                        .Append(Pick(random, GeneratedValues)).Append(Pick(random, GeneratedSpacing));
                }

                if (line < count - 1 || random.Next(4) == 0)
                {
                    text.Append(random.Next(2) == 0 ? "\r\n" : "\n");
                }
            }

            if (random.Next(80) == 0)
            {
                text.Append('\r');
            }

            return text.ToString();
        }

        private static List<(string Name, string Raw, string Value)> ReadLines(string text)
        {
            var lines = new List<(string, string, string)>();
            var reader = new RequestReplayDraft.HeaderLineReader(text, null, CancellationToken.None);
            while (reader.Read(out ReadOnlySpan<char> name, out ReadOnlySpan<char> raw, out ReadOnlySpan<char> value))
            {
                lines.Add((name.ToString(), raw.ToString(), value.ToString()));
            }

            return lines;
        }

        private static void AssertSameResult(RawFromDraftResult expected, RawFromDraftResult actual, string because)
        {
            Assert.True(expected.Refusal == actual.Refusal, $"{actual.Refusal} where {expected.Refusal} was expected: {because}");
            if (expected.Value == null)
            {
                Assert.Null(actual.Value);
                return;
            }

            Assert.Equal(expected.Value.Target, actual.Value.Target);
            Assert.Equal(expected.Value.TargetText, actual.Value.TargetText);
            Assert.True(expected.Value.HeadText == actual.Value.HeadText, because);
            Assert.Equal(expected.Value.Disclosures, actual.Value.Disclosures);
        }

        // The frozen design's Parsed to Raw (l2-gui-design.md 1.2) as L1 first wrote it, with whole-text passes through
        // ParseHeaderText and string lines: FromDraft must agree with it exactly below the header preparation budget.
        private static RawFromDraftResult FrozenFromDraft(RequestReplayDraft draft, long bodyLength, bool formBody)
        {
            string[] notSentByParsed =
            {
                "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "Proxy-Connection", "TE", "Trailer",
                "Transfer-Encoding",
            };
            static bool Is(string name, string expected) => string.Equals(name, expected, StringComparison.OrdinalIgnoreCase);
            string url = draft.Url ?? string.Empty;
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) || url.Contains('#'))
            {
                return new RawFromDraftResult(null, RawFromDraftRefusal.Url);
            }

            ReplayRawTarget target = ReplayRawTarget.FromUrl(uri);
            if (target == null)
            {
                return new RawFromDraftResult(null, RawFromDraftRefusal.Target);
            }

            string method = draft.Method ?? string.Empty;
            if (method.Length == 0 || !method.All(RequestReplayDraft.IsTokenCharacter))
            {
                return new RawFromDraftResult(null, RawFromDraftRefusal.Method);
            }

            try
            {
                RequestReplayDraft.ParseHeaderText(draft.HeadersText);
            }
            catch (FormatException)
            {
                return new RawFromDraftResult(null, RawFromDraftRefusal.Headers);
            }

            var lines = new List<(string Name, string Raw, string Value)>();
            foreach (string line in (draft.HeadersText ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    int separator = line.IndexOf(':');
                    string afterColon = line[(separator + 1)..];
                    lines.Add((line[..separator], afterColon, afterColon.Trim(' ', '\t')));
                }
            }

            HashSet<string> nominated = RequestReplayDraft.ConnectionNominations(
                lines.Select(line => new KeyValuePair<string, string>(line.Name, line.Value)));
            string length = bodyLength.ToString(CultureInfo.InvariantCulture);
            string pathAndQuery = uri.PathAndQuery;
            var head = new List<string> { method + " " + pathAndQuery + " HTTP/1.1", "Host: " + target.Authority };
            var leftOut = new List<string>();
            bool hostReplaced = false;
            bool lengthReplaced = false;
            bool lengthWritten = false;
            bool spacing = false;
            int hosts = 0;
            int lengths = 0;
            foreach ((string name, string raw, string value) in lines)
            {
                if (Is(name, "Host"))
                {
                    hostReplaced |= ++hosts > 1 || !string.Equals(value, target.Authority, StringComparison.Ordinal);
                }
                else if (Is(name, "Content-Length"))
                {
                    lengthReplaced |= ++lengths > 1 || !string.Equals(value, length, StringComparison.Ordinal);
                    if (!lengthWritten)
                    {
                        head.Add("Content-Length: " + length);
                        lengthWritten = true;
                    }
                }
                else if (notSentByParsed.Any(field => Is(name, field)) || nominated.Contains(name))
                {
                    if (!leftOut.Any(field => Is(field, name)))
                    {
                        leftOut.Add(name);
                    }
                }
                else
                {
                    spacing |= raw != value && raw != " " + value;
                    head.Add(name + ": " + value);
                }
            }

            if (!lengthWritten)
            {
                head.Add("Content-Length: " + length);
            }

            string headText = string.Join("\r\n", head);
            if (RawRequest.CountHead(headText).OverLimit || bodyLength > RawRequest.MaxEntityBytes)
            {
                return new RawFromDraftResult(null, RawFromDraftRefusal.Limits);
            }

            var disclosures = new List<RawDisclosure>();
            if (leftOut.Count > 0)
            {
                disclosures.Add(new RawDisclosure(RawDisclosureKind.LeftOut, string.Join(", ", leftOut)));
            }

            if (hostReplaced)
            {
                disclosures.Add(new RawDisclosure(RawDisclosureKind.HostWritten, target.Authority));
            }

            if (lengthReplaced)
            {
                disclosures.Add(new RawDisclosure(RawDisclosureKind.LengthWritten, length));
            }

            if (spacing)
            {
                disclosures.Add(new RawDisclosure(RawDisclosureKind.Spacing, null));
            }

            int authority = url.IndexOf("://", StringComparison.Ordinal) + 3;
            int end = url.IndexOfAny(new[] { '/', '?' }, authority);
            string typed = end < 0 ? string.Empty : url[end..];
            if (!string.Equals(typed.StartsWith('/') ? typed : "/" + typed, pathAndQuery, StringComparison.Ordinal))
            {
                disclosures.Add(new RawDisclosure(RawDisclosureKind.TargetEscaped, pathAndQuery));
            }

            if (formBody)
            {
                disclosures.Add(new RawDisclosure(RawDisclosureKind.FormParts, null));
            }

            return new RawFromDraftResult(
                new RawFromDraft(target, target.Scheme + "://" + target.Authority, headText, disclosures.AsReadOnly()),
                null);
        }

        private static RequestReplayDraft Draft(string headers) =>
            new() { Url = "http://example.test/", Method = "GET", HeadersText = headers };

        private static RawFromDraftResult ToRaw(string headers) =>
            RawConversion.FromDraft(Draft(headers), 0, false, CancellationToken.None);

        private static string Escape(string text) =>
            (text ?? string.Empty).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

        private static RawEditorParse Parse(string head, string body)
        {
            RawEditorParseResult result = RawRequest.ParseForEditor(
                head, Encoding.Latin1.GetBytes(body), ParseTarget("http://example.test"), CancellationToken.None);
            Assert.True(result.Code == null, $"refused: {result.Code} at line {result.Line}");
            return result.Parse;
        }

        private static ReplayRawTarget ParseTarget(string text)
        {
            Assert.True(ReplayRawTarget.TryParse(text, out ReplayRawTarget target));
            return target;
        }

        private static string Authority(string url) => ReplayRawTarget.FromUrl(new Uri(url)).Authority;
    }
}
