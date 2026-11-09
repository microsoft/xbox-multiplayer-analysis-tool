// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CaptureAnalysisEngine;
using XMAT.DataAbstractionLayer;
using XMAT.Operations;
using XMAT.Operations.Analysis;
using XMAT.Operations.Replay;
using XMAT.SharedInterfaces;
using XMAT.Tests.Fixtures;
using XMAT.Tests.Replay;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using XMAT.XboxLiveCaptureAnalysis;

namespace XMAT.Tests.Operations.Analysis
{
    public class SazAnalysisParityTests
    {
        private const string DeviceName = "Synthetic SAZ device";

        [Fact]
        public void AnalysisUrlKeepsTheZoneWithoutAddingItToTheLogicalHost()
        {
            var record = new ProxyConnectionModel(
                1, "200", DateTime.UnixEpoch, TimeSpan.FromMilliseconds(1),
                "https", "example.test:443", "POST", "/users?q=%25",
                "POST /users?q=%25 HTTP/1.1\r\n" +
                    "Host: userpresence.xboxlive.com\r\n" +
                    "User-Agent: XboxServicesAPI\r\n\r\n",
                Array.Empty<byte>(), "HTTP/1.1 200 OK\r\n\r\n", Array.Empty<byte>(), "127.0.0.1");
            typeof(ProxyConnectionModel)
                .GetProperty(nameof(ProxyConnectionModel.Host))
                .SetValue(record, "fe80::1%Ethernet");

            ServiceCallItem call = WebServiceCaptureRecordConverter.Convert(
                AnalyzeCaptureOperation.AnalysisDataRecord.FromProxyModel(1, record));

            Assert.NotNull(call);
            Assert.Equal("userpresence.xboxlive.com", call.Host);
            Assert.Equal("https://[fe80::1%Ethernet]/users?q=%25", call.Uri);
        }

        [Fact]
        public async Task AnalysisWithEscapedUserInformationAndAScopedHost_DoesNotFailTheRun()
        {
            using var directory = new TestDirectory();
            var record = new ProxyConnectionModel(
                1, "200", DateTime.UnixEpoch, TimeSpan.FromMilliseconds(1),
                "http", "example.test:80", "GET", "http://a{b@[fe80::1%12]/a%20b",
                "GET http://a{b@[fe80::1%12]/a%20b HTTP/1.1\r\n" +
                    "Host: userpresence.xboxlive.com\r\n" +
                    "User-Agent: XboxServicesAPI\r\n\r\n",
                Array.Empty<byte>(), "HTTP/1.1 200 OK\r\n\r\n", Array.Empty<byte>(), "127.0.0.1");
            ServiceCallItem call = WebServiceCaptureRecordConverter.Convert(
                AnalyzeCaptureOperation.AnalysisDataRecord.FromProxyModel(1, record));
            string unscoped = new Uri("http://a{b@[fe80::1]/a%20b").ToString();

            Assert.NotNull(call);
            Assert.Equal("userpresence.xboxlive.com", call.Host);
            Assert.Equal(unscoped.Replace("[fe80::1]", "[fe80::1%12]", StringComparison.Ordinal), call.Uri);

            OperationResult<AnalysisResult> result = await AnalyzeRecords(
                new[] { AnalyzeCaptureOperation.AnalysisDataRecord.FromProxyModel(1, record) },
                System.IO.Path.Join(directory.Path, "analysis"));

            Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
        }

        [Theory]
        [InlineData("%0")]
        [InlineData("%Ethernet")]
        [InlineData("%25NoSuchIf")]
        public async Task ScopedAbsoluteTargets_KeepTheirLexicalZoneThroughBothReadersAndDataUses(string zone)
        {
            using var directory = new TestDirectory();
            string host = "fe80::1" + zone;
            string url = $"http://[{host}]:8080/x?keep=%25";
            string path = ReplayCaptures.WriteSaz(
                directory.Path,
                "scoped.saz",
                new CapturedExchange(
                    "GET",
                    url,
                    "Host: userpresence.xboxlive.com\r\nUser-Agent: XboxServicesAPI\r\n"));
            FiddlerSazImportResult imported =
                FiddlerSazHandler.ImportSazFileWithDiagnostics(path, CancellationToken.None);
            BoundedSazReadResult bounded = ReadBounded(path);
            ProxyConnectionModel[] records =
            {
                Assert.Single(imported.Records),
                Assert.Single(bounded.Records),
            };

            var constructed = new ProxyConnectionModel(
                7,
                "200",
                DateTime.UnixEpoch,
                TimeSpan.Zero,
                "http",
                $"[{host}]:8080",
                "GET",
                "/x?keep=%25",
                $"GET /x?keep=%25 HTTP/1.1\r\nHost: [{host}]:8080\r\n\r\n",
                Array.Empty<byte>(),
                "HTTP/1.1 200 OK\r\n\r\n",
                Array.Empty<byte>(),
                "127.0.0.1");
            Assert.Equal((host, "8080"), (constructed.Host, constructed.Port));

            foreach (ProxyConnectionModel record in records)
            {
                Assert.Equal(("http", host, "8080", "/x?keep=%25"), (record.Scheme, record.Host, record.Port, record.Path));
                Assert.StartsWith($"GET {url} HTTP/1.1\r\n", record.RequestLineAndHeaders, StringComparison.Ordinal);
                Assert.Contains("\r\nHost: userpresence.xboxlive.com\r\n", record.RequestLineAndHeaders, StringComparison.Ordinal);
                RequestReplayDraft draft = RequestReplayDraft.FromCapture(record);
                Assert.Equal(url, draft.Url);
                Assert.Equal(ReplayCaptureDestinationStatus.ScopedIpv6, ReplayDestination.ClassifyCaptureDestination(record));

                using var requestFile = new MemoryStream();
                RequestFileWriter.Write(
                    draft,
                    ReplayHttpVersion.Auto,
                    RequestFileProfile.Content,
                    requestFile,
                    CancellationToken.None);
                RequestFileReadResult read = RequestFileReader.Read(requestFile.ToArray());
                Assert.Null(read.Code);
                Assert.Equal(url, read.Content.Url);
                Assert.Equal(url, RequestReplayDraft.FromRequestFile(read.Content).Url);

                ServiceCallItem call = WebServiceCaptureRecordConverter.Convert(
                    AnalyzeCaptureOperation.AnalysisDataRecord.FromProxyModel(1, record));
                Assert.NotNull(call);
                Assert.Equal("userpresence.xboxlive.com", call.Host);
                Assert.Equal(url, call.Uri);
            }

            var ordinary = new ProxyConnectionModel(
                8,
                "200",
                DateTime.UnixEpoch,
                TimeSpan.Zero,
                "http",
                "127.0.0.1:9",
                "GET",
                "/ordinary",
                "GET /ordinary HTTP/1.1\r\nHost: 127.0.0.1:9\r\n\r\n",
                Array.Empty<byte>(),
                "HTTP/1.1 200 OK\r\n\r\n",
                Array.Empty<byte>(),
                "127.0.0.1");
            int senders = 0;
            var operation = new ReplayOperation
            {
                SenderFactory = () =>
                {
                    senders++;
                    throw new InvalidOperationException("A scoped plan must not create a sender.");
                },
            };
            OperationResult<ReplayResult> send = await operation.ExecuteAsync(
                new ReplayRequest(ReplaySource.FromCaptures(new[] { ordinary, records[0] })),
                OperationContext.None,
                CancellationToken.None);
            Assert.Equal(ReplayDestination.ScopedIpv6Code, Assert.Single(send.Diagnostics).Code);
            Assert.Equal(0, senders);

            OperationResult<ReplayExport> exported = operation.PrepareForExport(
                new ReplayRequest(ReplaySource.FromCaptures(new[] { ordinary, records[0] })),
                CancellationToken.None);
            Assert.True(exported.Succeeded);
            int scripts = 0;
            CurlExportException curl = Assert.Throws<CurlExportException>(() =>
            {
                foreach (ReplayExportItem item in exported.Value.Items)
                {
                    RequestCurlExporter.CheckDestination(item.Draft);
                }

                scripts++;
            });
            Assert.Equal(ReplayDestination.ScopedIpv6Code, curl.Code);
            Assert.Equal(0, scripts);
        }

        [Theory]
        [InlineData("http://[fe80::1%a?x]:8080/path")]
        [InlineData("http://[fe80::1%a@[2001:db8::1]:8080/path")]
        [InlineData("http://[fe80::1%a]@[2001:db8::1]:8080/path")]
        [InlineData("http://x@[fe80::1%a@[2001:db8::1]:8080/path")]
        public void AbsoluteTargetWithRejectedZoneText_FallsBackToANonUrlRow(string target)
        {
            using var directory = new TestDirectory();
            Assert.True(Uri.TryCreate(target, UriKind.Absolute, out _));
            string path = WriteSazRequest(
                directory.Path,
                "rejected-zone.saz",
                $"GET {target} HTTP/1.1\r\nHost: conflicting.example\r\n\r\n");
            FiddlerSazImportResult imported =
                FiddlerSazHandler.ImportSazFileWithDiagnostics(path, CancellationToken.None);
            BoundedSazReadResult bounded = ReadBounded(path);

            foreach (ProxyConnectionModel record in new[] { Assert.Single(imported.Records), Assert.Single(bounded.Records) })
            {
                Assert.Equal((string.Empty, "conflicting.example", target), (record.Scheme, record.Host, record.Path));
            }
        }

        [Theory]
        [InlineData("http://b.test:/x", "b.test")]
        [InlineData("http://[2001:db8::1]:/x", "2001:db8::1")]
        public void AbsoluteTargetWithAnEmptyPort_RemainsAUrlRow(string target, string expectedHost)
        {
            using var directory = new TestDirectory();
            string path = WriteSazRequest(
                directory.Path,
                "empty-port.saz",
                $"GET {target} HTTP/1.1\r\nHost: conflicting.example\r\n\r\n");
            FiddlerSazImportResult imported =
                FiddlerSazHandler.ImportSazFileWithDiagnostics(path, CancellationToken.None);
            BoundedSazReadResult bounded = ReadBounded(path);

            foreach (ProxyConnectionModel record in new[] { Assert.Single(imported.Records), Assert.Single(bounded.Records) })
            {
                Assert.Equal(("http", expectedHost, string.Empty, "/x"), (record.Scheme, record.Host, record.Port, record.Path));
            }
        }

        [Theory]
        [InlineData("http://[fe80::1%playfab.com]/x", false)]
        [InlineData("http://api.playfab.com/x", true)]
        public void PlayFabAllowList_UsesTheHostIdentityWithoutItsZone(string url, bool analyzed)
        {
            using var directory = new TestDirectory();
            string path = ReplayCaptures.WriteSaz(
                directory.Path,
                "playfab.saz",
                new CapturedExchange("GET", url));
            ProxyConnectionModel record = Assert.Single(
                FiddlerSazHandler.ImportSazFileWithDiagnostics(path, CancellationToken.None).Records);

            ServiceCallItem call = WebServiceCaptureRecordConverter.Convert(
                AnalyzeCaptureOperation.AnalysisDataRecord.FromProxyModel(1, record));

            Assert.Equal(analyzed, call != null);
        }

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
            Assert.Equal(string.Empty, importedRecord.Scheme);
            Assert.Equal(expectedPort, importedRecord.Port);
            Assert.Null(importedRecord.ActionableHost);
            AssertEquivalent(importedRecord, boundedRecord);
        }

        // An absolute URL names the host whatever the Host header says, and whether there is one, in both readers: the frame
        // keeps its request line and headers, and its host is the URL's, which host-list actions can use. Analysis names the
        // call by its Host header when it has one, even an empty one, and otherwise by the URL's host; a call named by an
        // empty host keeps its path alone, and its physical host is the URL's either way.
        [Theory]
        [InlineData(SyntheticSazFrameKind.AbsoluteTargetMissingHost, 0, "userpresence.xboxlive.com")]
        [InlineData(SyntheticSazFrameKind.AbsoluteTargetMalformedHost, 1, "userpresence.xboxlive.com")]
        [InlineData(SyntheticSazFrameKind.AbsoluteTargetConflictingHost, 0, SyntheticCaptures.ConflictingHost)]
        [InlineData(SyntheticSazFrameKind.AbsoluteTargetEmptyHost, 0, "")]
        [InlineData(SyntheticSazFrameKind.AbsoluteTargetBlankHost, 0, "")]
        public void AbsoluteTargets_NameTheHost_WhateverTheHostHeaderSays(
            SyntheticSazFrameKind kind,
            int malformedHeaders,
            string label)
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteSazFrames(
                directory.Path,
                "absolute-targets.saz",
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
            Assert.Equal(
                (malformedHeaders, malformedHeaders),
                (imported.SkippedMalformedHeaderLines, bounded.SkippedMalformedHeaderLines));
            Assert.Equal(
                ("https", "userpresence.xboxlive.com", string.Empty),
                (importedRecord.Scheme, importedRecord.Host, importedRecord.Port));
            Assert.Equal("userpresence.xboxlive.com", importedRecord.ActionableHost);
            Assert.StartsWith(
                "POST https://userpresence.xboxlive.com/",
                importedRecord.RequestLineAndHeaders,
                StringComparison.Ordinal);
            Assert.Equal(
                kind == SyntheticSazFrameKind.AbsoluteTargetConflictingHost,
                importedRecord.RequestLineAndHeaders.Contains(
                    $"\r\nHost: {SyntheticCaptures.ConflictingHost}\r\n",
                    StringComparison.Ordinal));
            AssertEquivalent(importedRecord, boundedRecord);
            foreach (ProxyConnectionModel record in new[] { importedRecord, boundedRecord })
            {
                ServiceCallItem call = WebServiceCaptureRecordConverter.Convert(
                    AnalyzeCaptureOperation.AnalysisDataRecord.FromProxyModel(1, record));
                string url = "https://userpresence.xboxlive.com" + record.Path;
                Assert.Equal(
                    (label, "userpresence.xboxlive.com", label.Length == 0 ? record.Path : url),
                    (call.Host, call.PhysicalHost, call.Uri));
            }
        }

        // An absolute URL whose host has no IDN form is kept as written, with an unknown scheme and the Host header's host,
        // in both readers, between frames that are read as always; the archive is analyzed whole, from records (the GUI)
        // and from the file (xmat), rather than failing.
        [Fact]
        public async Task AUrlWhoseHostHasNoIdnForm_IsKeptAsWritten_AndTheArchiveIsAnalyzed()
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteSazFrames(
                directory.Path,
                "idn-too-long.saz",
                new[]
                {
                    SyntheticSazFrameKind.Valid,
                    SyntheticSazFrameKind.AbsoluteTargetIdnTooLong,
                    SyntheticSazFrameKind.Valid,
                });

            FiddlerSazImportResult imported =
                FiddlerSazHandler.ImportSazFileWithDiagnostics(
                    path,
                    CancellationToken.None);
            BoundedSazReadResult bounded = ReadBounded(path);

            Assert.Equal(3, imported.Records.Count);
            Assert.Equal(3, bounded.Records.Count);
            for (int index = 0; index < 3; index++)
            {
                AssertEquivalent(imported.Records[index], bounded.Records[index]);
            }

            ProxyConnectionModel kept = imported.Records[1];
            Assert.StartsWith("http://" + SyntheticCaptures.IdnTooLongHost + "/", kept.Path, StringComparison.Ordinal);
            Assert.StartsWith("POST " + kept.Path + " HTTP/1.1\r\n", kept.RequestLineAndHeaders, StringComparison.Ordinal);
            Assert.Equal(
                (string.Empty, "userpresence.xboxlive.com", "userpresence.xboxlive.com"),
                (kept.Scheme, kept.Host, kept.ActionableHost));
            Assert.All(
                new[] { imported.Records[0], imported.Records[2] },
                record => Assert.Equal(("https", "userpresence.xboxlive.com"), (record.Scheme, record.Host)));

            OperationResult<AnalysisResult> fromRecords = await AnalyzeRecords(
                imported.Records.Select(
                    (model, index) => (IDataRecord)AnalyzeCaptureOperation.AnalysisDataRecord.FromProxyModel(index + 1, model)),
                System.IO.Path.Join(directory.Path, "records"));
            OperationResult<AnalysisResult> fromFile = await AnalyzeFile(path, System.IO.Path.Join(directory.Path, "file"));
            foreach (OperationResult<AnalysisResult> analyzed in new[] { fromRecords, fromFile })
            {
                Assert.Equal(OperationOutcome.Succeeded, analyzed.Outcome);
                Assert.Equal(3, analyzed.Value.Summary.TotalCalls);
            }
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

        // Requests whose targets are no URL are analyzed from records (the GUI) and from a file (xmat), keep their unknown
        // scheme and their paths through the export and the import, and fail nothing: a path, a method that can be no scheme,
        // and text that looks like a URL but is none, where a made-up scheme once failed the whole analysis.
        [Fact]
        public async Task TargetsWithoutAUrl_AreAnalyzed_FromRecordsAndFromAFile()
        {
            using var directory = new TestDirectory();
            ProxyConnectionModel[] models = new[]
            {
                ("GET", "/users/me"),
                ("GET_X", "/users/me"),
                ("GET", "http:x"),
                ("GET", "https:/x"),
                ("GET", "https://["),
            }.Select((frame, index) => ReadTarget((uint)(index + 1), frame.Item1, frame.Item2)).ToArray();
            string path = System.IO.Path.Join(directory.Path, "targets.saz");
            FiddlerSazHandler.CreateSazFile(path, models);

            OperationResult<AnalysisResult> fromRecords = await AnalyzeRecords(
                models.Select(
                    (model, index) => (IDataRecord)AnalyzeCaptureOperation.AnalysisDataRecord.FromProxyModel(index + 1, model)),
                System.IO.Path.Join(directory.Path, "records"));
            OperationResult<AnalysisResult> fromFile = await AnalyzeFile(path, System.IO.Path.Join(directory.Path, "file"));
            FiddlerSazImportResult imported = FiddlerSazHandler.ImportSazFileWithDiagnostics(path, CancellationToken.None);

            foreach (OperationResult<AnalysisResult> analyzed in new[] { fromRecords, fromFile })
            {
                Assert.Equal(OperationOutcome.Succeeded, analyzed.Outcome);
                Assert.Equal(models.Length, analyzed.Value.Summary.TotalCalls);
            }

            Assert.Equal(
                models.Select(model => (string.Empty, model.Path, "example.test")),
                imported.Records.Select(record => (record.Scheme, record.Path, record.Host)));
        }

        // A client address flag without a value is malformed in both readers, and xmat refuses the archive as malformed.
        [Fact]
        public async Task AClientAddressFlagWithoutAValue_IsRefusedByBothReaders()
        {
            using var directory = new TestDirectory();
            string path = WriteFrame(
                directory.Path,
                "no-client-address.saz",
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><Session SID=\"1\" BitFlags=\"0\">" +
                "<SessionTimers ClientBeginRequest=\"2026-01-01T00:00:00.0000000Z\" " +
                "ClientDoneResponse=\"2026-01-01T00:00:01.0000000Z\" />" +
                "<SessionFlags><SessionFlag N=\"x-clientip\" /></SessionFlags></Session>");

            FormatException imported = Assert.Throws<FormatException>(
                () => FiddlerSazHandler.ImportSazFileWithDiagnostics(path, CancellationToken.None));
            BoundedSazException bounded = Assert.Throws<BoundedSazException>(() => ReadBounded(path));
            OperationResult<AnalysisResult> analyzed = await AnalyzeFile(path, System.IO.Path.Join(directory.Path, "output"));

            Assert.Equal("x-clientip has no value", imported.Message);
            Assert.Equal(BoundedSazFailure.Malformed, bounded.Failure);
            Assert.IsType<FormatException>(bounded.InnerException);
            Assert.Equal(OperationOutcome.InvalidInput, analyzed.Outcome);
            Assert.Contains(analyzed.Diagnostics, diagnostic => diagnostic.Code == "analysis.input.malformed");
        }

        // A header name with an apostrophe, which replay sends, is kept by both readers, and the capture is still analyzed:
        // analysis leaves the line out of the headers that it reads.
        [Fact]
        public async Task AnApostropheInAHeaderName_IsKeptByBothReaders_AndTheCaptureIsAnalyzed()
        {
            using var directory = new TestDirectory();
            string path = XMAT.Tests.Replay.ReplayCaptures.WriteSaz(
                directory.Path,
                "apostrophe.saz",
                new XMAT.Tests.Replay.CapturedExchange(
                    "GET", "http://example.test/users/me", "User-Agent: XboxServicesAPI\r\nX'Name: kept\r\n"));

            FiddlerSazImportResult imported = FiddlerSazHandler.ImportSazFileWithDiagnostics(path, CancellationToken.None);
            BoundedSazReadResult bounded = ReadBounded(path);
            OperationResult<AnalysisResult> analyzed = await AnalyzeFile(path, System.IO.Path.Combine(directory.Path, "output"));

            Assert.Equal((0, 0), (imported.SkippedMalformedHeaderLines, bounded.SkippedMalformedHeaderLines));
            Assert.Contains("\r\nX'Name: kept\r\n", Assert.Single(imported.Records).RequestLineAndHeaders, StringComparison.Ordinal);
            Assert.Contains("\r\nX'Name: kept\r\n", Assert.Single(bounded.Records).RequestLineAndHeaders, StringComparison.Ordinal);
            Assert.Equal(OperationOutcome.Succeeded, analyzed.Outcome);
            Assert.Equal(1, Assert.Single(analyzed.Value.Summary.Devices).Consoles.Sum(console => console.Calls));
        }

        // A call whose time is unknown, such as a frame's without its metadata, cannot be placed among the calls that rules
        // compare in time: it is not analyzed, and analysis goes on with the others, from records (the GUI) and from a file
        // (xmat), where its time once failed the whole device.
        [Fact]
        public async Task ACallWithAnUnknownTime_IsNotAnalyzed_AndTheOthersAre()
        {
            using var directory = new TestDirectory();
            ProxyConnectionModel unknown = ReadFrame(1, metadata: null);
            ProxyConnectionModel known = ReadFrame(
                2,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><Session SID=\"2\" BitFlags=\"0\">" +
                "<SessionTimers ClientBeginRequest=\"2026-01-01T00:00:00.0000000Z\" " +
                "ClientDoneResponse=\"2026-01-01T00:00:01.0000000Z\" />" +
                "<SessionFlags><SessionFlag N=\"x-clientip\" V=\"10.0.0.1\" /></SessionFlags></Session>");
            Assert.Equal((DateTime.MinValue, DateTimeKind.Unspecified), (unknown.RequestTime, unknown.RequestTime.Kind));
            string path = System.IO.Path.Join(directory.Path, "unknown-time.saz");
            FiddlerSazHandler.CreateSazFile(path, new[] { unknown, known });

            OperationResult<AnalysisResult> fromRecords = await AnalyzeRecords(
                new[] { unknown, known }.Select(
                    (model, index) => (IDataRecord)AnalyzeCaptureOperation.AnalysisDataRecord.FromProxyModel(index + 1, model)),
                System.IO.Path.Join(directory.Path, "records"));
            OperationResult<AnalysisResult> fromFile = await AnalyzeFile(path, System.IO.Path.Join(directory.Path, "file"));

            foreach (OperationResult<AnalysisResult> analyzed in new[] { fromRecords, fromFile })
            {
                Assert.Equal(OperationOutcome.Succeeded, analyzed.Outcome);
                Assert.Equal(1, analyzed.Value.Summary.TotalCalls);
            }
        }

        // An Xbox Live call read from a SAZ frame, with its metadata or without it.
        private static ProxyConnectionModel ReadFrame(uint id, string metadata)
        {
            using var request = new MemoryStream(
                Encoding.Latin1.GetBytes(
                    "GET http://example.test/users/me HTTP/1.1\r\nHost: example.test\r\nUser-Agent: XboxServicesAPI\r\n\r\n"),
                writable: false);
            using var response = new MemoryStream(Encoding.Latin1.GetBytes("HTTP/1.1 200 OK\r\n\r\n"), writable: false);
            using MemoryStream timers = metadata == null ? null : new MemoryStream(Encoding.UTF8.GetBytes(metadata), writable: false);
            ProxyConnectionModel model = FiddlerSazHandler.FromFiddlerFrame(id, request, timers, response, out _);
            Assert.NotNull(model);
            return model;
        }

        // An Xbox Live call to example.test with this method and request target, read from a SAZ frame with its times.
        private static ProxyConnectionModel ReadTarget(uint id, string method, string target)
        {
            using var request = new MemoryStream(
                Encoding.Latin1.GetBytes(
                    $"{method} {target} HTTP/1.1\r\nHost: example.test\r\nUser-Agent: XboxServicesAPI\r\n\r\n"),
                writable: false);
            using var response = new MemoryStream(Encoding.Latin1.GetBytes("HTTP/1.1 200 OK\r\n\r\n"), writable: false);
            using var timers = new MemoryStream(
                Encoding.UTF8.GetBytes(
                    $"<?xml version=\"1.0\" encoding=\"utf-8\"?><Session SID=\"{id}\" BitFlags=\"0\">" +
                    "<SessionTimers ClientBeginRequest=\"2026-01-01T00:00:00.0000000Z\" " +
                    "ClientDoneResponse=\"2026-01-01T00:00:01.0000000Z\" />" +
                    "<SessionFlags><SessionFlag N=\"x-clientip\" V=\"10.0.0.1\" /></SessionFlags></Session>"),
                writable: false);
            ProxyConnectionModel model = FiddlerSazHandler.FromFiddlerFrame(id, request, timers, response, out _);
            Assert.NotNull(model);
            return model;
        }

        // A Fiddler archive of one Xbox Live call with this metadata.
        private static string WriteFrame(string directory, string fileName, string metadata)
        {
            string path = System.IO.Path.Join(directory, fileName);
            using (var archive = new ZipArchive(File.Create(path), ZipArchiveMode.Create))
            {
                foreach ((string name, byte[] bytes) in new[]
                {
                    ("raw/0001_c.txt", Encoding.Latin1.GetBytes(
                        "GET http://example.test/users/me HTTP/1.1\r\nHost: example.test\r\nUser-Agent: XboxServicesAPI\r\n\r\n")),
                    ("raw/0001_s.txt", Encoding.Latin1.GetBytes("HTTP/1.1 200 OK\r\n\r\n")),
                    ("raw/0001_m.xml", Encoding.UTF8.GetBytes(metadata)),
                })
                {
                    using Stream entry = archive.CreateEntry(name).Open();
                    entry.Write(bytes);
                }
            }

            return path;
        }

        private static string WriteSazRequest(string directory, string fileName, string request)
        {
            string metadata =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><Session SID=\"1\" BitFlags=\"0\">" +
                "<SessionTimers ClientBeginRequest=\"2026-01-01T00:00:00.0000000Z\" " +
                "ClientDoneResponse=\"2026-01-01T00:00:01.0000000Z\" />" +
                "<SessionFlags><SessionFlag N=\"x-clientip\" V=\"10.0.0.1\" /></SessionFlags></Session>";
            string path = System.IO.Path.Join(directory, fileName);
            using (var archive = new ZipArchive(File.Create(path), ZipArchiveMode.Create))
            {
                foreach ((string name, byte[] bytes) in new[]
                {
                    ("raw/0001_c.txt", Encoding.Latin1.GetBytes(request)),
                    ("raw/0001_s.txt", Encoding.Latin1.GetBytes("HTTP/1.1 200 OK\r\n\r\n")),
                    ("raw/0001_m.xml", Encoding.UTF8.GetBytes(metadata)),
                })
                {
                    using Stream entry = archive.CreateEntry(name).Open();
                    entry.Write(bytes);
                }
            }

            return path;
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

            public void Dispose() => TestFolderCleanup.Delete(Path);
        }
    }
}
