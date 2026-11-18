// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.IO.Compression;
using System.Reflection;
using System.Text;
using XMAT.DataAbstractionLayer;
using XMAT.Operations;
using XMAT.Operations.Captures;
using XMAT.Operations.Export;
using XMAT.Operations.Processes;
using XMAT.Tests.Operations.Analysis;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using static XMAT.Tests.ProcessFilterTestSupport;

namespace XMAT.Tests
{
    // SAZ exports name frames in export order and carry XMAT's identities, exact timestamps, scheme, path and client
    // address as session flags that an import restores. A sanitized export drops the client address, the process and
    // any raw path. Failures are coded and name no captured value.
    public sealed class SazExportWriterTests : IDisposable
    {
        private const string RequestNumberFlag = "x-xmat-request-number";
        private const string ConnectionIdFlag = "x-xmat-connection-id";
        private const string RequestTimeFlag = "x-xmat-request-timestamp";
        private const string ResponseTimeFlag = "x-xmat-response-timestamp";
        private const string SchemeFlag = "x-xmat-scheme";
        private const string PathFlag = "x-xmat-request-path";
        private const string ClientIpFlag = "x-xmat-clientip";
        private const string TrailersFlag = "x-xmat-response-trailers";
        private const string TruncatedAtFlag = "x-xmat-response-body-truncated-at";
        private const string UninspectableFlag = "x-xmat-headers-uninspectable";
        private const string RequestEncodingFlag = "x-xmat-request-header-encoding";
        private const string ResponseEncodingFlag = "x-xmat-response-header-encoding";
        // Characters above U+00FF, one of them outside the Basic Multilingual Plane, as a device could send in UTF-8.
        private const string UnicodeName = "\u010Capek \u65E5\u672C \U0001F600";
        private const string Requested = "2026-02-01T00:00:01.1234567Z";
        private const string Responded = "2026-02-01T00:00:01.2345678Z";
        private const string Secret = "q7f3a-secret";
        private static readonly Guid Source = Guid.NewGuid();
        private readonly string _directory = Path.Join(
            AppContext.BaseDirectory,
            "HeadlessTestArtifacts",
            "SazExport-" + Guid.NewGuid().ToString("N"));

        public SazExportWriterTests()
        {
            Directory.CreateDirectory(_directory);
        }

        [Fact]
        public void Write_NamesFramesInExportOrderEvenWhenRequestNumbersRepeat()
        {
            (string path, _) = Write(false, Row(31, 7, "Alpha PC"), Row(32, 7, "Beta PC"));

            Assert.Equal(
                new[] { "raw/1_c.txt", "raw/1_m.xml", "raw/1_s.txt", "raw/2_c.txt", "raw/2_m.xml", "raw/2_s.txt" },
                ProcessCaptures.Entries(path).Keys.OrderBy(name => name, StringComparer.Ordinal));
            Assert.Equal(
                new long[] { 7, 7 },
                FiddlerSazHandler.ImportSazFile(path, CancellationToken.None).Select(model => model.RequestNumber));
        }

        // An ordinary archive keeps XMAT's values exactly (xmat export's SAZ round trip reads them back). A sanitized archive
        // keeps the identities, timestamps and scheme, and drops the client address, the process and the raw path.
        [Fact]
        public void Write_RecordsExactXmatFlags_AndSanitizingDropsTheIdentifyingOnes()
        {
            RawExportRow row = Row(5, 41, ProcessCaptures.Device, Local("Game.exe", 4100), values =>
            {
                values[WebServiceCaptureMethod.FieldKey_ConnectionId] = 9L;
                values[WebServiceCaptureMethod.FieldKey_RequestTimestamp] = Requested;
                values[WebServiceCaptureMethod.FieldKey_ResponseTimestamp] = Responded;
                values[WebServiceCaptureMethod.FieldKey_RequestScheme] = "http";
                values[WebServiceCaptureMethod.FieldKey_ClientIP] = "fe80::1";
                values[WebServiceCaptureMethod.FieldKey_RequestPath] = "/presence?access_token=" + Secret;
            });

            Dictionary<string, string> ordinary = Flags(Write(false, row).Path);
            (string sanitizedPath, ExportWriteSummary summary) = Write(true, row);
            Dictionary<string, string> sanitized = Flags(sanitizedPath);

            string[] identity = { RequestNumberFlag, ConnectionIdFlag, RequestTimeFlag, ResponseTimeFlag, SchemeFlag };
            Assert.Equal(new[] { "41", "9", Requested, Responded, "http" }, identity.Select(flag => ordinary[flag]));
            Assert.Equal("/presence?access_token=" + Secret, ordinary[PathFlag]);
            Assert.Equal(("fe80::1", "fe80::1"), (ordinary["x-clientip"], ordinary[ClientIpFlag]));
            Assert.Equal("Game.exe:4100", ordinary["x-ProcessInfo"]);
            Assert.Equal(new[] { "41", "9", Requested, Responded, "http" }, identity.Select(flag => sanitized[flag]));
            Assert.Equal("0.0.0.0", sanitized["x-clientip"]);
            Assert.False(sanitized.ContainsKey(ClientIpFlag));
            Assert.DoesNotContain(sanitized.Keys, flag => flag.Contains("process", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                Secret,
                string.Join("\n", ProcessCaptures.Entries(sanitizedPath).Values.Select(Encoding.UTF8.GetString)),
                StringComparison.Ordinal);
            Assert.Equal(1, summary.RowsWritten);
            Assert.True(summary.Sanitization.RedactedValues >= 2, "The client address and the process are redactions.");
        }

        // A streamed response keeps its trailers and the size of its truncated capture, and an import restores both. A
        // sanitized archive keeps the truncation but never the trailers, whose values can hold secrets; each trailer it
        // leaves out counts as an omitted header. A complete response without trailers gets neither flag.
        [Fact]
        public void Write_RecordsResponseTrailersAndTruncation_AndSanitizingDropsTheTrailers()
        {
            const string trailers = "x-checksum: abc123\r\nx-session-token: " + Secret + "\r\n";
            RawExportRow plain = Row(7, 7, ProcessCaptures.Device);
            RawExportRow streamed = Row(7, 7, ProcessCaptures.Device, change: values =>
            {
                values[WebProxyCaptureSchema.ResponseTrailers] = trailers;
                values[WebProxyCaptureSchema.ResponseBodyTruncatedAt] = 16L;
            });

            string ordinaryPath = Write(false, streamed).Path;
            Dictionary<string, string> ordinary = Flags(ordinaryPath);
            (string sanitizedPath, ExportWriteSummary summary) = Write(true, streamed);
            Dictionary<string, string> sanitized = Flags(sanitizedPath);
            ExportWriteSummary baseline = Write(true, plain).Summary;

            Assert.Equal(trailers, ordinary.GetValueOrDefault(TrailersFlag));
            Assert.Equal("16", ordinary.GetValueOrDefault(TruncatedAtFlag));
            ProxyConnectionModel imported = Assert.Single(
                FiddlerSazHandler.ImportSazFile(ordinaryPath, CancellationToken.None));
            Assert.Equal((trailers, 16L), (imported.ResponseTrailers, imported.ResponseBodyTruncatedAt));
            Assert.False(sanitized.ContainsKey(TrailersFlag));
            Assert.Equal("16", sanitized.GetValueOrDefault(TruncatedAtFlag));
            Assert.DoesNotContain(
                Secret,
                string.Join("\n", ProcessCaptures.Entries(sanitizedPath).Values.Select(Encoding.UTF8.GetString)),
                StringComparison.Ordinal);
            Assert.Equal(baseline.Sanitization.OmittedHeaders + 2, summary.Sanitization.OmittedHeaders);
            Assert.DoesNotContain(Flags(Write(false, plain).Path).Keys, flag => flag is TrailersFlag or TruncatedAtFlag);
        }

        // The flags come from an untrusted file: a malformed one leaves its field as a Fiddler import reads it.
        [Theory]
        [InlineData(RequestNumberFlag, "-4")]
        [InlineData(ConnectionIdFlag, "nine")]
        [InlineData(RequestTimeFlag, "yesterday")]
        [InlineData(SchemeFlag, "1http")]
        [InlineData(PathFlag, "")]
        [InlineData(ClientIpFlag, "")]
        [InlineData(TruncatedAtFlag, "-16")]
        [InlineData(TruncatedAtFlag, "16 bytes")]
        public void Import_IgnoresAMalformedXmatFlag(string flag, string value)
        {
            var baseline = new Dictionary<string, string> { ["x-clientip"] = ProcessCaptures.SazClientIp };
            ProxyConnectionModel legacy = ProcessCaptures.ImportFrame(baseline);

            ProxyConnectionModel model = ProcessCaptures.ImportFrame(
                new Dictionary<string, string>(baseline) { [flag] = value });

            Assert.Equal(
                (legacy.RequestNumber, legacy.Id, legacy.RequestTime, legacy.Duration, legacy.Scheme, legacy.Path, legacy.ClientIP),
                (model.RequestNumber, model.Id, model.RequestTime, model.Duration, model.Scheme, model.Path, model.ClientIP));
            Assert.Equal(
                (legacy.ResponseTrailers, legacy.ResponseBodyTruncatedAt),
                (model.ResponseTrailers, model.ResponseBodyTruncatedAt));
        }

        // Trailer text with a line that is not a header field is kept as it is rather than dropped unread, so a sanitized
        // export of the import can still fail closed on it.
        [Theory]
        [InlineData("not a header line")]
        [InlineData("x-checksum: abc123\r\nbad line\r\n")]
        public void Import_KeepsMalformedTrailersAsTheyAre(string value)
        {
            ProxyConnectionModel model = ProcessCaptures.ImportFrame(new Dictionary<string, string>
            {
                ["x-clientip"] = ProcessCaptures.SazClientIp,
                [TrailersFlag] = value,
            });

            Assert.Equal(value, model.ResponseTrailers);
        }

        [Theory]
        [InlineData(true, CaptureExportErrorCodes.InvalidHttp)]
        [InlineData(false, CaptureExportErrorCodes.InvalidBody)]
        public void Write_FailsWithACodedInputErrorThatNamesNoCapturedValue(bool sanitized, string code)
        {
            RawExportRow row = Row(6, 6, ProcessCaptures.Device, change: values =>
            {
                if (sanitized)
                {
                    values[WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders] = "pfcanary-not-http\r\n\r\n";
                }
                else
                {
                    values[WebServiceCaptureMethod.FieldKey_RequestBody] = "pfcanary*not-base64";
                }
            });
            using ICaptureExportWriter writer =
                FiddlerSazHandler.CreateExportWriter(Path.Join(_directory, "failed.saz"), sanitized, 1);

            CaptureExportException failure = Assert.Throws<CaptureExportException>(
                () => writer.Write(row, CancellationToken.None));

            Assert.Equal((code, OperationOutcome.InvalidInput), (failure.Code, failure.Outcome));
            Assert.DoesNotContain("pfcanary", failure.Message, StringComparison.Ordinal);
        }

        // Header text that Latin-1 can hold is one byte per character: an import reads a byte above 0x7F, such as a no-break
        // space (0xA0) or a NEL (0x85), as that character rather than '?', and an ordinary export writes it back as the same
        // byte without an encoding flag, so a round trip keeps the start line and headers exactly.
        [Fact]
        public void ImportAndOrdinaryExport_KeepHeaderBytesAboveAscii()
        {
            const string requestHeaders = "POST /caf\u00E9 HTTP/1.1\r\nHost: example.test\r\nx-note: caf\u00E9\u00A0\r\n" +
                "x-session-token: \u0085" + Secret + "\u00A0\r\n\r\n";
            const string responseHeaders = "HTTP/1.1 200 D\u00E9j\u00E0 vu\r\nContent-Type: text/plain\r\n" +
                "x-reason: d\u00E9j\u00E0\u0085\r\n\r\n";
            string original = Path.Join(_directory, "latin1.saz");
            SazArchive.WriteRaw(
                original,
                Encoding.Latin1.GetBytes(requestHeaders + "body"),
                Encoding.Latin1.GetBytes(responseHeaders + "ok"));

            ProxyConnectionModel imported = Assert.Single(FiddlerSazHandler.ImportSazFile(original, CancellationToken.None));
            Assert.Equal((requestHeaders, responseHeaders), (imported.RequestLineAndHeaders, imported.ResponseLineAndHeaders));
            string copy = Path.Join(_directory, "latin1-copy.saz");
            SazArchive.Write(copy, new[] { imported });
            Dictionary<string, byte[]> entries = ProcessCaptures.Entries(copy);
            ProxyConnectionModel reimported = Assert.Single(FiddlerSazHandler.ImportSazFile(copy, CancellationToken.None));

            Assert.Equal(Encoding.Latin1.GetBytes(requestHeaders + "body"), entries["raw/1_c.txt"]);
            Assert.Equal(Encoding.Latin1.GetBytes(responseHeaders + "ok"), entries["raw/1_s.txt"]);
            Assert.DoesNotContain(FrameFlags(entries, 1).Keys, key => key is RequestEncodingFlag or ResponseEncodingFlag);
            Assert.Equal(
                (requestHeaders, responseHeaders),
                (reimported.RequestLineAndHeaders, reimported.ResponseLineAndHeaders));
        }

        // A header value above U+00FF, such as a name that a device sent in UTF-8, neither fails an ordinary export nor
        // leaves its session out: that message's start line and headers are written as UTF-8 under its encoding flag, every
        // other message stays Latin-1 without one, and the bodies are written as stored.
        [Fact]
        public void Write_KeepsAUnicodeHeaderSessionBesideAnAsciiOne()
        {
            RawExportRow unicode = Row(8, 8, ProcessCaptures.Device, change: values =>
                values[WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders] =
                    ((string)values[WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders]).Replace(
                        "\r\n\r\n", "\r\nx-display-name: " + UnicodeName + "\r\n\r\n", StringComparison.Ordinal));
            RawExportRow ascii = Row(9, 9, ProcessCaptures.Device);

            Dictionary<string, byte[]> entries = ProcessCaptures.Entries(Write(false, unicode, ascii).Path);

            Assert.Equal(StoredEntry(unicode, true, Encoding.UTF8), entries["raw/1_c.txt"]);
            Assert.Equal(StoredEntry(unicode, false, Encoding.Latin1), entries["raw/1_s.txt"]);
            Assert.Equal(StoredEntry(ascii, true, Encoding.Latin1), entries["raw/2_c.txt"]);
            Assert.Equal(StoredEntry(ascii, false, Encoding.Latin1), entries["raw/2_s.txt"]);
            Assert.Equal("utf-8", FrameFlags(entries, 1).GetValueOrDefault(RequestEncodingFlag));
            Assert.False(FrameFlags(entries, 1).ContainsKey(ResponseEncodingFlag));
            Assert.DoesNotContain(FrameFlags(entries, 2).Keys, key => key is RequestEncodingFlag or ResponseEncodingFlag);
        }

        // Each message's encoding is chosen and recorded on its own. A request or response whose start line or headers have
        // a character above U+00FF is written as UTF-8 under its flag, and an import reads back exactly the stored text,
        // unmarked; a message without such a character stays Latin-1.
        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void OrdinaryRoundTrip_KeepsUnicodeHeaderTextInEitherMessage(bool request, bool response)
        {
            string requestHeaders = "POST /caf\u00E9" + (request ? "/\u20AC" : string.Empty) + " HTTP/1.1\r\n" +
                "Host: example.test\r\nx-display-name: " + (request ? UnicodeName : "caf\u00E9\u00A0") + "\r\n\r\n";
            string responseHeaders = "HTTP/1.1 200 " + (response ? "\u00C9t\u00E9 \u20AC" : "OK") + "\r\n" +
                "Content-Type: text/plain\r\nx-display-name: " + (response ? UnicodeName : "d\u00E9j\u00E0\u0085") +
                "\r\n\r\n";
            var record = new ProxyConnectionModel(
                1,
                "200",
                new DateTime(2026, 2, 1, 0, 0, 1, DateTimeKind.Utc),
                TimeSpan.FromMilliseconds(25),
                "https",
                "example.test:443",
                "POST",
                "/caf\u00E9",
                requestHeaders,
                "body"u8.ToArray(),
                responseHeaders,
                "ok"u8.ToArray(),
                ProcessCaptures.SazClientIp)
            {
                RecordRowId = 1,
            };
            string path = Path.Join(_directory, "unicode-round-trip.saz");

            SazArchive.Write(path, new[] { record });
            Dictionary<string, byte[]> entries = ProcessCaptures.Entries(path);
            Dictionary<string, string> flags = FrameFlags(entries, 1);
            ProxyConnectionModel imported = Assert.Single(FiddlerSazHandler.ImportSazFile(path, CancellationToken.None));

            Assert.Equal(
                (request ? Encoding.UTF8 : Encoding.Latin1).GetBytes(requestHeaders + "body"),
                entries["raw/1_c.txt"]);
            Assert.Equal(
                (response ? Encoding.UTF8 : Encoding.Latin1).GetBytes(responseHeaders + "ok"),
                entries["raw/1_s.txt"]);
            Assert.Equal(request ? "utf-8" : null, flags.GetValueOrDefault(RequestEncodingFlag));
            Assert.Equal(response ? "utf-8" : null, flags.GetValueOrDefault(ResponseEncodingFlag));
            Assert.Equal((requestHeaders, responseHeaders), (imported.RequestLineAndHeaders, imported.ResponseLineAndHeaders));
            Assert.Equal(0L, SazArchive.Values(imported)["HeadersUninspectable"]);
        }

        // Text that neither Latin-1 nor UTF-8 can hold, such as an unpaired surrogate, is never replaced: the export fails
        // with a coded input error that names no captured value.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Write_FailsWithACodedInputErrorForHeaderTextNoEncodingCanHold(bool inRequest)
        {
            string column = inRequest
                ? WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders
                : WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders;
            RawExportRow row = Row(8, 8, ProcessCaptures.Device, change: values => values[column] =
                ((string)values[column]).Replace(
                    "\r\n\r\n", "\r\nx-display-name: pfcanary\uD800\r\n\r\n", StringComparison.Ordinal));
            using ICaptureExportWriter writer =
                FiddlerSazHandler.CreateExportWriter(Path.Join(_directory, "unpaired-surrogate.saz"), false, 1);

            CaptureExportException failure = Assert.Throws<CaptureExportException>(
                () => writer.Write(row, CancellationToken.None));

            Assert.Equal((CaptureExportErrorCodes.InvalidHttp, OperationOutcome.InvalidInput), (failure.Code, failure.Outcome));
            Assert.DoesNotContain("pfcanary", failure.Message, StringComparison.Ordinal);
        }

        // Every archive carries each session's header history (the HeadersUninspectable column) as the
        // x-xmat-headers-uninspectable flag: "1" uninspectable, "0" no known loss, and "-1" unknown, as for a row without
        // the column. A sanitized archive leaves only the uninspectable session's pair out and counts the unknown ones; the
        // sessions after it are unaffected.
        [Fact]
        public void Write_CarriesEachSessionsHeaderHistory_AndSanitizingLeavesOnlyTheUninspectablePairOut()
        {
            RawExportRow uninspectable = Row(9, 9, ProcessCaptures.Device, change: values => values["HeadersUninspectable"] = 1L);
            RawExportRow recorded = Row(10, 10, ProcessCaptures.Device);
            RawExportRow unknown = Row(11, 11, ProcessCaptures.Device, change: values => values["HeadersUninspectable"] = -1L);
            RawExportRow legacy = Row(12, 12, ProcessCaptures.Device, change: values => values.Remove("HeadersUninspectable"));

            Dictionary<string, byte[]> ordinary =
                ProcessCaptures.Entries(Write(false, uninspectable, recorded, unknown, legacy).Path);
            (string sanitizedPath, ExportWriteSummary summary) = Write(true, uninspectable, recorded, unknown, legacy);
            Dictionary<string, byte[]> sanitized = ProcessCaptures.Entries(sanitizedPath);

            string[] histories = { "1", "0", "-1", "-1" };
            Assert.Equal(histories, Enumerable.Range(1, 4).Select(frame =>
                FrameFlags(ordinary, frame).GetValueOrDefault(UninspectableFlag)));
            Assert.Equal(histories, Enumerable.Range(1, 4).Select(frame =>
                FrameFlags(sanitized, frame).GetValueOrDefault(UninspectableFlag)));
            Assert.Equal(2, summary.Sanitization.OmittedBodies);
            Assert.Equal(2, SazArchive.UnknownHeaderProvenanceSessions(summary.Sanitization));
            Assert.Contains("Host: redacted.invalid", Encoding.Latin1.GetString(sanitized["raw/1_c.txt"]));
            Assert.Empty(Body(sanitized["raw/1_c.txt"]));
            Assert.Empty(Body(sanitized["raw/1_s.txt"]));
            foreach (int frame in new[] { 2, 3, 4 })
            {
                Assert.Contains(
                    "Host: userpresence.xboxlive.com", Encoding.Latin1.GetString(sanitized[$"raw/{frame}_c.txt"]));
                Assert.Contains(
                    "activity", Encoding.UTF8.GetString(Body(sanitized[$"raw/{frame}_c.txt"])), StringComparison.Ordinal);
                Assert.Contains(
                    "ok", Encoding.UTF8.GetString(Body(sanitized[$"raw/{frame}_s.txt"])), StringComparison.Ordinal);
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, true);
            }
        }

        // One stored session as the snapshot reads it: raw values by column name, the process as the frozen overlay.
        private static RawExportRow Row(
            long rowId,
            long requestNumber,
            string device,
            ClientProcessInfo process = null,
            Action<Dictionary<string, object>> change = null)
        {
            ProxyConnectionModel model = ProcessCaptures.ExportRecord((int)rowId);
            model.SetClientProcess(process ?? ClientProcessInfo.NotRecorded);
            Dictionary<string, object> values = SazArchive.Values(model, device);
            values[WebServiceCaptureMethod.FieldKey_RequestNumber] = requestNumber;
            change?.Invoke(values);
            return new RawExportRow(new CaptureRowKey(Source, 0, rowId, rowId), rowId, 1, values);
        }

        private (string Path, ExportWriteSummary Summary) Write(bool sanitized, params RawExportRow[] rows)
        {
            string path = Path.Join(_directory, Guid.NewGuid().ToString("N") + ".saz");
            using ICaptureExportWriter writer = FiddlerSazHandler.CreateExportWriter(path, sanitized, rows.Length);
            foreach (RawExportRow row in rows)
            {
                writer.Write(row, CancellationToken.None);
            }

            return (path, writer.Complete(CancellationToken.None));
        }

        private static Dictionary<string, string> Flags(string path) =>
            ProcessCaptures.SessionFlags(ProcessCaptures.Entries(path)["raw/1_m.xml"])
                .ToDictionary(flag => flag.Key, flag => flag.Value, StringComparer.Ordinal);

        private static Dictionary<string, string> FrameFlags(Dictionary<string, byte[]> entries, int frame) =>
            ProcessCaptures.SessionFlags(entries[$"raw/{frame}_m.xml"])
                .ToDictionary(flag => flag.Key, flag => flag.Value, StringComparer.Ordinal);

        // A message entry's bytes after the empty line that ends its headers.
        private static byte[] Body(byte[] entry) => entry.AsSpan(entry.AsSpan().IndexOf("\r\n\r\n"u8) + 4).ToArray();

        // A stored message's start line and headers in the given encoding, then its body as stored.
        private static byte[] StoredEntry(RawExportRow row, bool request, Encoding encoding) =>
            encoding.GetBytes((string)row.Values[request
                    ? WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders
                    : WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders])
                .Concat(Convert.FromBase64String((string)row.Values[request
                    ? WebServiceCaptureMethod.FieldKey_RequestBody
                    : WebServiceCaptureMethod.FieldKey_ResponseBody]))
                .ToArray();
    }

    // Writes Fiddler archives of records with the export writer, as the shared export operation does.
    internal static class SazArchive
    {
        // Returns the sanitizer's summary of a sanitized archive; an ordinary archive has none.
        internal static SanitizedExportSummary Write(string path, IEnumerable<ProxyConnectionModel> records, bool sanitized = false)
        {
            ProxyConnectionModel[] rows = records.ToArray();
            using ICaptureExportWriter writer = FiddlerSazHandler.CreateExportWriter(path, sanitized, rows.Length);
            foreach (ProxyConnectionModel record in rows)
            {
                writer.Write(
                    new RawExportRow(new CaptureRowKey(Guid.Empty, 0, record.RecordRowId, 1), record.RecordRowId, 1, Values(record)),
                    CancellationToken.None);
            }

            return writer.Complete(CancellationToken.None).Sanitization;
        }

        // A record's values as a capture file stores them, by column name.
        internal static Dictionary<string, object> Values(ProxyConnectionModel record, string device = "Test device")
        {
            object[] stored = record.ToDataTableValues(device);
            return WebProxyCaptureSchema.Fields
                .Select((field, index) => (field.Name, Value: stored[index]))
                .ToDictionary(item => item.Name, item => item.Value, StringComparer.Ordinal);
        }

        // Writes one frame byte for byte, as another tool could: its metadata has the client address and the given flags,
        // and none of XMAT's own.
        internal static void WriteRaw(string path, byte[] request, byte[] response, params KeyValuePair<string, string>[] flags)
        {
            using FileStream file = File.Create(path);
            using var archive = new ZipArchive(file, ZipArchiveMode.Create);
            byte[] metadata = Encoding.UTF8.GetBytes(ProcessCaptures.Metadata(
                1,
                flags.Prepend(new KeyValuePair<string, string>("x-clientip", ProcessCaptures.SazClientIp))));
            foreach ((string name, byte[] bytes) in new[]
            {
                ("raw/1_c.txt", request),
                ("raw/1_s.txt", response),
                ("raw/1_m.xml", metadata),
            })
            {
                using Stream entry = archive.CreateEntry(name).Open();
                entry.Write(bytes);
            }
        }

        // A sanitized export's count of the exported sessions whose header history is unknown. It is read and set by name,
        // so these tests also compile against a summary without the count, where it reads null and can't be set.
        internal static int? UnknownHeaderProvenanceSessions(SanitizedExportSummary summary) =>
            (int?)UnknownHeaderProvenanceProperty?.GetValue(summary);

        internal static void SetUnknownHeaderProvenanceSessions(SanitizedExportSummary summary, int sessions) =>
            UnknownHeaderProvenanceProperty?.SetValue(summary, sessions);

        private static PropertyInfo UnknownHeaderProvenanceProperty =>
            typeof(SanitizedExportSummary).GetProperty(
                "UnknownHeaderProvenanceSessions",
                BindingFlags.Instance | BindingFlags.NonPublic);
    }
}
