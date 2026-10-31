// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using CaptureAnalysisEngine;
using XMAT.DataAbstractionLayer;
using XMAT.Operations;
using XMAT.Operations.Analysis;
using XMAT.SharedInterfaces;
using XMAT.Tests.Fixtures;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Operations.Analysis
{
    public class SazAnalysisParityTests
    {
        private const string DeviceName = "Synthetic SAZ device";

        [Fact]
        public async Task LegacySazImportAndSharedFileAnalysis_ProduceEquivalentFirstConsoleResults()
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteSaz(
                directory.Path,
                "analysis-parity.saz",
                new SyntheticSazOptions(
                    Consoles: 2,
                    RequestsPerConsole: 3,
                    IncludeXr049Violation: true));
            var fromFile = await AnalyzeFile(
                path,
                System.IO.Path.Combine(directory.Path, "file-output"));
            ProxyConnectionsCollection imported =
                FiddlerSazHandler.ImportSazFile(
                    path,
                    CancellationToken.None);

            using var database = new SqLtDatabase();
            database.Initialize();
            IDataTable table = CreateCaptureTable(database);
            foreach (ProxyConnectionModel record in imported)
            {
                record.AddToDataTable(DeviceName, table);
            }
            var fromLegacyImport = await AnalyzeRecords(
                table.Dataset.DataRecords,
                System.IO.Path.Combine(directory.Path, "legacy-output"));

            Assert.Equal(OperationOutcome.Succeeded, fromFile.Outcome);
            Assert.Equal(
                OperationOutcome.Succeeded,
                fromLegacyImport.Outcome);
            ConsoleSummary fileConsole = Assert.Single(
                Assert.Single(fromFile.Value.Summary.Devices)
                    .Consoles,
                console => console.Id == "c1");
            ConsoleSummary legacyConsole = Assert.Single(
                Assert.Single(fromLegacyImport.Value.Summary.Devices)
                    .Consoles,
                console => console.Id == "c1");
            Assert.Equal(fileConsole.Calls, legacyConsole.Calls);
            Assert.Equal(fileConsole.Findings, legacyConsole.Findings);
            IReadOnlyDictionary<string, string> fileReports =
                ReportJsonByType(FirstConsoleReports(fromFile));
            IReadOnlyDictionary<string, string> legacyReports =
                ReportJsonByType(FirstConsoleReports(fromLegacyImport));
            Assert.Equal(
                fileReports.Keys.OrderBy(key => key, StringComparer.Ordinal),
                legacyReports.Keys.OrderBy(key => key, StringComparer.Ordinal));
            foreach (string reportType in fileReports.Keys)
            {
                Assert.Equal(
                    fileReports[reportType],
                    legacyReports[reportType]);
            }
        }

        [Fact]
        public void ZipEntryAndBoundedStreamSazParsing_ProduceEquivalentRecords()
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteSaz(
                directory.Path,
                "record-parity.saz",
                new SyntheticSazOptions(
                    Consoles: 2,
                    RequestsPerConsole: 3,
                    IncludeCanaries: true));
            FiddlerSazImportResult imported =
                FiddlerSazHandler.ImportSazFileWithDiagnostics(
                    path,
                    CancellationToken.None);
            ProxyConnectionModel[] fromEntries =
                imported.Records
                    .OrderBy(record => record.RequestNumber)
                    .ToArray();
            BoundedSazReadResult bounded = ReadBounded(path);
            ProxyConnectionModel[] fromStreams = bounded.Records
                .OrderBy(record => record.RequestNumber)
                .ToArray();

            Assert.Equal(0, imported.SkippedIncompleteFrames);
            Assert.Equal(0, bounded.SkippedIncompleteFrames);
            Assert.Equal(0, imported.SkippedMalformedHeaderLines);
            Assert.Equal(0, bounded.SkippedMalformedHeaderLines);
            Assert.Equal(fromEntries.Length, fromStreams.Length);
            for (var index = 0; index < fromEntries.Length; index++)
            {
                AssertEquivalent(fromEntries[index], fromStreams[index]);
            }
        }

        [Fact]
        public void BoundedSazParsing_LeavesHeaderCollectionsLazy()
        {
            using var directory = new TestDirectory();
            string path = SyntheticCaptures.WriteSaz(
                directory.Path,
                "lazy-headers.saz",
                new SyntheticSazOptions(
                    RequestsPerConsole: 1));

            ProxyConnectionModel record =
                Assert.Single(
                    ReadBounded(path).Records);

            Assert.False(
                record.HeaderCollectionsMaterialized);
            Assert.NotEmpty(record.RequestHeaders);
            Assert.True(
                record.HeaderCollectionsMaterialized);
        }

        [Fact]
        public void MalformedHeaders_AreSkippedConsistentlyInBothSazReaders()
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteMalformedHeaderSaz(
                directory.Path,
                "malformed-headers.saz");

            FiddlerSazImportResult imported =
                FiddlerSazHandler.ImportSazFileWithDiagnostics(
                    path,
                    CancellationToken.None);
            BoundedSazReadResult bounded = ReadBounded(path);

            Assert.Equal(0, imported.SkippedIncompleteFrames);
            Assert.Equal(0, bounded.SkippedIncompleteFrames);
            Assert.Equal(2, imported.SkippedMalformedHeaderLines);
            Assert.Equal(2, bounded.SkippedMalformedHeaderLines);
            AssertEquivalent(
                Assert.Single(imported.Records),
                Assert.Single(bounded.Records));
        }

        [Theory]
        [InlineData(
            SyntheticSazFrameKind.MalformedRequestStartLine)]
        [InlineData(
            SyntheticSazFrameKind.MalformedResponseStartLine)]
        public void MalformedStartLine_SkipsTheFrameWithoutCountingItsHeaders(
            SyntheticSazFrameKind kind)
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteSazFrames(
                directory.Path,
                "malformed-start-line.saz",
                new[] { kind });

            FiddlerSazImportResult imported =
                FiddlerSazHandler.ImportSazFileWithDiagnostics(
                    path,
                    CancellationToken.None);
            BoundedSazReadResult bounded = ReadBounded(path);

            Assert.Empty(imported.Records);
            Assert.Empty(bounded.Records);
            Assert.Equal(1, imported.SkippedIncompleteFrames);
            Assert.Equal(1, bounded.SkippedIncompleteFrames);
            Assert.Equal(0, imported.SkippedMalformedHeaderLines);
            Assert.Equal(0, bounded.SkippedMalformedHeaderLines);
        }

        [Theory]
        [InlineData(
            SyntheticSazFrameKind.MissingHost,
            "",
            0)]
        [InlineData(
            SyntheticSazFrameKind.MalformedHost,
            "",
            1)]
        [InlineData(
            SyntheticSazFrameKind.EmptyHostWithPort,
            "8080",
            0)]
        public void HostlessHeaders_KeepTheFrameWithoutInferringAHost(
            SyntheticSazFrameKind kind,
            string expectedPort,
            int malformedHeaders)
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteSazFrames(
                directory.Path,
                "missing-host.saz",
                new[] { kind });

            FiddlerSazImportResult imported =
                FiddlerSazHandler.ImportSazFileWithDiagnostics(
                    path,
                    CancellationToken.None);
            BoundedSazReadResult bounded = ReadBounded(path);

            ProxyConnectionModel importedRecord =
                Assert.Single(imported.Records);
            ProxyConnectionModel boundedRecord =
                Assert.Single(bounded.Records);
            Assert.Equal(0, imported.SkippedIncompleteFrames);
            Assert.Equal(0, bounded.SkippedIncompleteFrames);
            Assert.Equal(
                malformedHeaders,
                imported.SkippedMalformedHeaderLines);
            Assert.Equal(
                malformedHeaders,
                bounded.SkippedMalformedHeaderLines);
            Assert.Equal(string.Empty, importedRecord.Host);
            Assert.Equal(expectedPort, importedRecord.Port);
            Assert.Null(importedRecord.ActionableHost);
            AssertEquivalent(importedRecord, boundedRecord);
        }

        [Fact]
        public void MixedMalformedStartLines_PreserveSurvivingFramesInOrder()
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteSazFrames(
                directory.Path,
                "mixed-start-lines.saz",
                new[]
                {
                    SyntheticSazFrameKind.Valid,
                    SyntheticSazFrameKind.MalformedRequestStartLine,
                    SyntheticSazFrameKind.Valid,
                    SyntheticSazFrameKind.MalformedResponseStartLine,
                });

            FiddlerSazImportResult imported =
                FiddlerSazHandler.ImportSazFileWithDiagnostics(
                    path,
                    CancellationToken.None);
            BoundedSazReadResult bounded = ReadBounded(path);

            Assert.Equal(
                new long[] { 1, 3 },
                imported.Records.Select(record => record.RequestNumber));
            Assert.Equal(
                new long[] { 1, 3 },
                bounded.Records.Select(record => record.RequestNumber));
            Assert.Equal(2, imported.SkippedIncompleteFrames);
            Assert.Equal(2, bounded.SkippedIncompleteFrames);
            Assert.Equal(0, imported.SkippedMalformedHeaderLines);
            Assert.Equal(0, bounded.SkippedMalformedHeaderLines);
            Assert.Equal(imported.Records.Count, bounded.Records.Count);
            for (var index = 0; index < imported.Records.Count; index++)
            {
                AssertEquivalent(
                    imported.Records[index],
                    bounded.Records[index]);
            }
        }

        [Fact]
        public void MixedHostlessFrames_AreKeptInOrder()
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteSazFrames(
                directory.Path,
                "mixed-hosts.saz",
                new[]
                {
                    SyntheticSazFrameKind.Valid,
                    SyntheticSazFrameKind.MissingHost,
                    SyntheticSazFrameKind.Valid,
                    SyntheticSazFrameKind.MalformedHost,
                });

            FiddlerSazImportResult imported =
                FiddlerSazHandler.ImportSazFileWithDiagnostics(
                    path,
                    CancellationToken.None);
            BoundedSazReadResult bounded = ReadBounded(path);

            Assert.Equal(
                new long[] { 1, 2, 3, 4 },
                imported.Records.Select(record => record.RequestNumber));
            Assert.Equal(
                new long[] { 1, 2, 3, 4 },
                bounded.Records.Select(record => record.RequestNumber));
            Assert.Equal(0, imported.SkippedIncompleteFrames);
            Assert.Equal(0, bounded.SkippedIncompleteFrames);
            Assert.Equal(1, imported.SkippedMalformedHeaderLines);
            Assert.Equal(1, bounded.SkippedMalformedHeaderLines);
            Assert.Equal(
                new[]
                {
                    "userpresence.xboxlive.com",
                    string.Empty,
                    "userpresence.xboxlive.com",
                    string.Empty,
                },
                imported.Records.Select(record => record.Host));
            Assert.Equal(imported.Records.Count, bounded.Records.Count);
            for (var index = 0; index < imported.Records.Count; index++)
            {
                AssertEquivalent(
                    imported.Records[index],
                    bounded.Records[index]);
            }
        }

        private static Task<OperationResult<AnalysisResult>> AnalyzeFile(
            string path,
            string output)
        {
            return new AnalyzeCaptureOperation().ExecuteAsync(
                new AnalyzeCaptureRequest(
                    AnalysisInput.FromFile(path),
                    new AnalysisOptions(
                        ProduceLegacyReports: true)),
                new OperationContext(workingDirectory: output),
                CancellationToken.None);
        }

        private static Task<OperationResult<AnalysisResult>> AnalyzeRecords(
            IEnumerable<IDataRecord> records,
            string output)
        {
            return new AnalyzeCaptureOperation().ExecuteAsync(
                new AnalyzeCaptureRequest(
                    AnalysisInput.FromRecords(records, DeviceName),
                    new AnalysisOptions(
                        ProduceLegacyReports: true)),
                new OperationContext(workingDirectory: output),
                CancellationToken.None);
        }

        private static BoundedSazReadResult ReadBounded(string path)
        {
            AnalysisLimits limits = AnalysisLimits.Default;
            using var stream = File.OpenRead(path);
            return BoundedSazReader.Read(
                stream,
                new BoundedSazLimits(
                    limits.MaxInputBytes,
                    limits.MaxEntryBytes,
                    limits.MaxTotalRetainedBytes,
                    limits.MaxRecords,
                    BoundedSazReader.EntryLimitForRecords(
                        limits.MaxRecords),
                    BoundedSazReader.DefaultMaxCentralDirectoryBytes),
                CancellationToken.None);
        }

        private static IReadOnlyList<ReportDocument> FirstConsoleReports(
            OperationResult<AnalysisResult> result) =>
            result.Value.Legacy.PerConsoleReports.First().Value;

        private static IReadOnlyDictionary<string, string> ReportJsonByType(
            IEnumerable<ReportDocument> reports) =>
            reports.ToDictionary(
                report => report.GetType().FullName,
                report => CanonicalizeJson(report.ToJson()),
                StringComparer.Ordinal);

        private static string CanonicalizeJson(string json)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return CanonicalizeJson(document.RootElement);
        }

        private static string CanonicalizeJson(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    return "{" + string.Join(
                        ",",
                        element.EnumerateObject()
                            .OrderBy(
                                property => property.Name,
                                StringComparer.Ordinal)
                            .Select(property =>
                                JsonSerializer.Serialize(property.Name) +
                                ":" +
                                CanonicalizeJson(property.Value))) + "}";

                case JsonValueKind.Array:
                    return "[" + string.Join(
                        ",",
                        element.EnumerateArray()
                            .Select(CanonicalizeJson)
                            .OrderBy(
                                value => value,
                                StringComparer.Ordinal)) + "]";

                case JsonValueKind.String:
                    return JsonSerializer.Serialize(element.GetString());

                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                case JsonValueKind.Null:
                    return element.GetRawText();

                default:
                    throw new InvalidOperationException(
                        $"Unsupported JSON value kind {element.ValueKind}.");
            }
        }

        private static IDataTable CreateCaptureTable(
            SqLtDatabase database)
        {
            // Keep this explicit schema as a parity fixture that freezes the persisted capture format.
            return database.CreateTable(
                WebServiceCaptureMethod.TableName,
                new Field<string>(
                    WebServiceCaptureMethod.FieldKey_DeviceName),
                new Field<long>(
                    WebServiceCaptureMethod.FieldKey_RequestNumber),
                new Field<long>(
                    WebServiceCaptureMethod.FieldKey_ConnectionId),
                new Field<string>(
                    WebServiceCaptureMethod.FieldKey_RequestTimestamp),
                new Field<string>(
                    WebServiceCaptureMethod.FieldKey_ResponseTimestamp),
                new Field<string>(
                    WebServiceCaptureMethod.FieldKey_RequestScheme),
                new Field<string>(
                    WebServiceCaptureMethod.FieldKey_RequestHost),
                new Field<string>(
                    WebServiceCaptureMethod.FieldKey_RequestPort),
                new Field<string>(
                    WebServiceCaptureMethod.FieldKey_RequestMethod),
                new Field<string>(
                    WebServiceCaptureMethod.FieldKey_RequestPath),
                new Field<string>(
                    WebServiceCaptureMethod.FieldKey_RequestStatus),
                new Field<string>(
                    WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders),
                new Field<string>(
                    WebServiceCaptureMethod.FieldKey_RequestBody),
                new Field<string>(
                    WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders),
                new Field<string>(
                    WebServiceCaptureMethod.FieldKey_ResponseBody),
                new Field<string>(
                    WebServiceCaptureMethod.FieldKey_ClientIP));
        }

        private static void AssertEquivalent(
            ProxyConnectionModel expected,
            ProxyConnectionModel actual)
        {
            Assert.Equal(expected.RequestNumber, actual.RequestNumber);
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.RequestTime, actual.RequestTime);
            Assert.Equal(expected.Duration, actual.Duration);
            Assert.Equal(expected.Status, actual.Status);
            Assert.Equal(expected.Scheme, actual.Scheme);
            Assert.Equal(expected.Host, actual.Host);
            Assert.Equal(expected.Port, actual.Port);
            Assert.Equal(expected.Method, actual.Method);
            Assert.Equal(expected.Path, actual.Path);
            Assert.Equal(
                expected.RequestLineAndHeaders,
                actual.RequestLineAndHeaders);
            Assert.Equal(expected.RequestBody, actual.RequestBody);
            Assert.Equal(
                expected.ResponseLineAndHeaders,
                actual.ResponseLineAndHeaders);
            Assert.Equal(expected.ResponseBody, actual.ResponseBody);
            Assert.Equal(expected.ClientIP, actual.ClientIP);
        }

        private sealed class TestDirectory : IDisposable
        {
            internal TestDirectory()
            {
                Path = System.IO.Path.Combine(
                    AppContext.BaseDirectory,
                    "HeadlessTestArtifacts",
                    $"XMAT-saz-parity-{Guid.NewGuid():N}");
                Directory.CreateDirectory(Path);
            }

            internal string Path { get; }

            public void Dispose()
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, true);
                }
            }
        }
    }
}
