// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

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
        [InlineData(TrailersFlag, "not a header line")]
        [InlineData(TrailersFlag, "x-checksum: abc123\r\nbad line\r\n")]
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
    }
}
