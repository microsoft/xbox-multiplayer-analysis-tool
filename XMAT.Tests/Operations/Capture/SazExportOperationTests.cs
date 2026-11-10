// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.IO.Compression;
using System.Text;
using XMAT.DataAbstractionLayer;
using XMAT.Operations;
using XMAT.Operations.Capture;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Operations.Capture
{
    public sealed class SazExportOperationTests
    {
        [Fact]
        public void T1_HeadsKeepLatin1_ReplaceWideCharacters_AndNormalizeFields()
        {
            using var output = new MemoryStream();
            ProxyConnectionModel record = Model(
                requestHead:
                    "GET / HTTP/1.1\n" +
                    "Host: example.test\n" +
                    "X-Space:  value  \n" +
                    "X-Latin: \u00e9\n" +
                    "X-Wide: \ud83d\ude00\n" +
                    "Bad\u0001: value\n\n");

            OperationResult<SazExportResult> result =
                SazExportTestSupport.ExportRecords(
                    output,
                    new[] { record },
                    SazExportKind.Plain);
            byte[] request = Entry(
                output,
                "raw/1_c.txt");
            string head = Encoding.Latin1.GetString(
                request,
                0,
                Separator(request));

            Assert.True(result.Succeeded);
            Assert.Contains("X-Space: value\r\n", head);
            Assert.Contains("X-Latin: \u00e9\r\n", head);
            Assert.EndsWith("X-Wide: ?", head);
            Assert.DoesNotContain("Bad", head);
            Assert.Equal(
                1,
                result.Value.Summary.ReplacedHeadCharacters);
            Assert.True(
                result.Value.Summary.DroppedHeaderLines >= 1);
            Assert.True(
                result.Value.Summary.ReformattedHeaderLines >= 3);
        }

        [Fact]
        public void T2_AbsoluteTargetsUseTheWrittenHost_AndTheSharedReaderRule()
        {
            using var output = new MemoryStream();
            int calls = 0;
            SazRequestTarget.RefuseAbsoluteTargetSeam.Value =
                (method, target) =>
                {
                    calls++;
                    return false;
                };
            try
            {
                OperationResult<SazExportResult> result =
                    SazExportTestSupport.ExportRecords(
                        output,
                        new[]
                        {
                            Model(
                                requestHead:
                                    "GET /Path?q=1 HTTP/1.1\r\n" +
                                    "Host: Api.Example.TEST:80\r\n\r\n",
                                scheme: "HTTP"),
                        },
                        SazExportKind.Plain);
                string request = Encoding.Latin1.GetString(
                    Entry(
                        output,
                        "raw/1_c.txt"));

                Assert.True(result.Succeeded);
                Assert.StartsWith(
                    "GET http://Api.Example.TEST:80/Path?q=1 HTTP/1.1\r\n",
                    request);
                Assert.Equal(
                    1,
                    result.Value.Summary.AbsoluteTargets);
                Assert.Equal(1, calls);
            }
            finally
            {
                SazRequestTarget.RefuseAbsoluteTargetSeam.Value =
                    null;
            }
        }

        [Theory]
        [InlineData("Host: ex\u00e4mple.test\r\n")]
        [InlineData("Host: a.test\r\nHost: b.test\r\n")]
        [InlineData("")]
        public void T2_DeclinedCandidatesKeepTheirOriginFormLine(
            string hostLines)
        {
            using var output = new MemoryStream();
            OperationResult<SazExportResult> result =
                SazExportTestSupport.ExportRecords(
                    output,
                    new[]
                    {
                        Model(
                            requestHead:
                                "GET /Path?q=1 HTTP/1.1\r\n" +
                                hostLines +
                                "\r\n",
                            scheme: "http"),
                    },
                    SazExportKind.Plain);
            string request = Encoding.Latin1.GetString(
                Entry(
                    output,
                    "raw/1_c.txt"));

            Assert.True(result.Succeeded);
            Assert.StartsWith(
                "GET /Path?q=1 HTTP/1.1\r\n" + hostLines,
                request);
            Assert.Equal(
                0,
                result.Value.Summary.AbsoluteTargets);
        }

        [Fact]
        public void T2_ACandidateTheReaderRefusesKeepsItsOriginFormLine()
        {
            using var output = new MemoryStream();
            SazRequestTarget.RefuseAbsoluteTargetSeam.Value =
                static (method, target) => true;
            try
            {
                OperationResult<SazExportResult> result =
                    SazExportTestSupport.ExportRecords(
                        output,
                        new[]
                        {
                            Model(
                                requestHead:
                                    "GET /Path HTTP/1.1\r\n" +
                                    "Host: example.test\r\n\r\n",
                                scheme: "http"),
                        },
                        SazExportKind.Plain);
                string request = Encoding.Latin1.GetString(
                    Entry(
                        output,
                        "raw/1_c.txt"));

                Assert.True(result.Succeeded);
                Assert.StartsWith(
                    "GET /Path HTTP/1.1\r\n",
                    request);
                Assert.Equal(
                    0,
                    result.Value.Summary.AbsoluteTargets);
            }
            finally
            {
                SazRequestTarget.RefuseAbsoluteTargetSeam.Value =
                    null;
            }
        }

        [Fact]
        public void T2_OnlyProjectedHostFieldsCountTowardTheSingleHostRule()
        {
            using var output = new MemoryStream();
            OperationResult<SazExportResult> result =
                SazExportTestSupport.ExportRecords(
                    output,
                    new[]
                    {
                        Model(
                            requestHead:
                                "GET /Path HTTP/1.1\r\n" +
                                "Host: good.test\r\n" +
                                "Host: bad\u0001.test\r\n\r\n",
                            scheme: "http"),
                    },
                    SazExportKind.Plain);
            string request = Encoding.Latin1.GetString(
                Entry(
                    output,
                    "raw/1_c.txt"));

            Assert.True(result.Succeeded);
            Assert.StartsWith(
                "GET http://good.test/Path HTTP/1.1\r\n",
                request);
            Assert.DoesNotContain("bad", request);
            Assert.Equal(
                1,
                result.Value.Summary.AbsoluteTargets);
        }

        [Fact]
        public void T3_RepeatedAndOutOfRangeNumbersAreRenumberedAfterTheLargestKept()
        {
            using var output = new MemoryStream();
            ProxyConnectionModel first = Model(number: 5);
            ProxyConnectionModel repeat = Model(number: 5);
            ProxyConnectionModel zero = Model(number: 0);

            OperationResult<SazExportResult> result =
                SazExportTestSupport.ExportRecords(
                    output,
                    new[] { first, repeat, zero },
                    SazExportKind.Plain);
            string[] names = EntryNames(output);

            Assert.True(result.Succeeded);
            Assert.Contains("raw/5_c.txt", names);
            Assert.Contains("raw/6_c.txt", names);
            Assert.Contains("raw/7_c.txt", names);
            Assert.Equal(
                2,
                result.Value.Summary.RenumberedSessions);
        }

        [Fact]
        public void T4_MissingRequestsAreSkipped_ButMalformedStartLinesRefuseTheRun()
        {
            OperationResult<SazExportResult> missing =
                SazExportTestSupport.ExportRecords(
                    new MemoryStream(),
                    new[]
                    {
                        Model(requestHead: string.Empty),
                    },
                    SazExportKind.Plain);
            OperationResult<SazExportResult> malformed =
                SazExportTestSupport.ExportRecords(
                    new MemoryStream(),
                    new[]
                    {
                        Model(
                            requestHead:
                                "GET  / HTTP/1.1\r\n\r\n"),
                    },
                    SazExportKind.Plain);

            Assert.True(missing.Succeeded);
            Assert.Equal(
                1,
                missing.Value.Summary.RowsWithoutRequest);
            Assert.Equal(
                OperationOutcome.InvalidInput,
                malformed.Outcome);
            Assert.Equal(
                SazExportCodes.InvalidStartLine,
                malformed.Value.Code);
        }

        [Fact]
        public void T5_UnknownTimesWriteTheFrozenUnknownValue_AndRequireZeroDuration()
        {
            using var output = new MemoryStream();
            using (var writer =
                new SazArchiveWriter(output))
            {
                writer.Add(
                    new SazSession(
                        1,
                        Message("GET / HTTP/1.1"),
                        null,
                        null,
                        TimeSpan.Zero,
                        Array.Empty<
                            KeyValuePair<string, string>>()),
                    CancellationToken.None);
                writer.Complete(
                    CancellationToken.None);
            }

            string metadata = Encoding.UTF8.GetString(
                Entry(
                    output,
                    "raw/1_m.xml"));
            Assert.Contains(
                SazArchiveWriter.UnknownTimeText,
                metadata);

            using var failed =
                new SazArchiveWriter(
                    new MemoryStream());
            Assert.Throws<ArgumentException>(
                () => failed.Add(
                    new SazSession(
                        1,
                        Message("GET / HTTP/1.1"),
                        null,
                        null,
                        TimeSpan.FromSeconds(1),
                        Array.Empty<
                            KeyValuePair<string, string>>()),
                    CancellationToken.None));
        }

        [Fact]
        public void T6_ClientAddressFlagsAreXmlEscapedAndRoundTrip()
        {
            using var output = new MemoryStream();
            const string Client = "\"<&>'";
            ProxyConnectionModel record = Model(
                clientAddress: Client);

            OperationResult<SazExportResult> result =
                SazExportTestSupport.ExportRecords(
                    output,
                    new[] { record },
                    SazExportKind.Plain);
            string metadata = Encoding.UTF8.GetString(
                Entry(
                    output,
                    "raw/1_m.xml"));
            using TempArchive archive =
                CopyToArchive(output);
            ProxyConnectionModel imported = Assert.Single(
                FiddlerSazHandler.ImportSazFile(
                    archive.Path,
                    CancellationToken.None));

            Assert.True(result.Succeeded);
            Assert.Contains("&lt;", metadata);
            Assert.Contains("&amp;", metadata);
            Assert.Equal(Client, imported.ClientIP);
        }

        [Fact]
        public void T7_PlainBinaryBodiesRoundTripThroughTheGuiImporter()
        {
            byte[] body = Enumerable.Range(0, 256)
                .Select(static value => (byte)value)
                .ToArray();
            using var output = new MemoryStream();

            OperationResult<SazExportResult> result =
                SazExportTestSupport.ExportRecords(
                    output,
                    new[]
                    {
                        Model(
                            requestBody: body,
                            responseBody:
                                body.Reverse().ToArray()),
                    },
                    SazExportKind.Plain);
            using TempArchive archive =
                CopyToArchive(output);
            ProxyConnectionModel imported = Assert.Single(
                FiddlerSazHandler.ImportSazFile(
                    archive.Path,
                    CancellationToken.None));

            Assert.True(result.Succeeded);
            Assert.Equal(body, imported.RequestBody);
            Assert.Equal(
                body.Reverse(),
                imported.ResponseBody);
        }

        [Fact]
        public async Task T10_UninspectedSanitizedBodiesAreOmittedAndFlagged()
        {
            ProxyConnectionModel model = Model();
            var source =
                new SazExportTestSupport.ScriptedSource(
                    new[]
                    {
                        new SazExportKey(1, 1, 0),
                    },
                    (_, _) =>
                        new StoredRow(
                            1,
                            1,
                            StoredRowState.Available,
                            0,
                            model.RequestLineAndHeaders,
                            StoredBody.NotRead(
                                4_194_305,
                                undecodable: false),
                            model.ResponseLineAndHeaders,
                            StoredBody.Empty,
                            model.Scheme,
                            false,
                            ProxyConnectionModel
                                .ToDateTimeString(
                                    model.RequestTime),
                            false,
                            ProxyConnectionModel
                                .ToDateTimeString(
                                    model.ResponseDoneTime),
                            false,
                            model.ClientIP,
                            false));
            using var output = new MemoryStream();
            OperationResult<SazExportResult> result = await RunAsync(
                source,
                SazExportKind.Sanitized,
                output);
            string metadata = Encoding.UTF8.GetString(
                Entry(
                    output,
                    "raw/1_m.xml"));

            Assert.True(result.Succeeded);
            Assert.Equal(
                1,
                result.Value.Summary.Sanitized.OmittedBodies);
            Assert.Equal(
                0,
                result.Value.Summary.BodiesNotKept);
            Assert.Contains(
                "x-xmat-request-body-omitted",
                metadata);
        }

        [Fact]
        public async Task T11_FailuresAndProgressNeverContainCapturedOrExceptionText()
        {
            const string Canary =
                "secret-exception-canary";
            var phases = new List<string>();
            var source =
                new SazExportTestSupport.ScriptedSource(
                    new[]
                    {
                        new SazExportKey(1, 1, 0),
                    },
                    (_, _) =>
                        throw new IOException(Canary));
            var operation = new SazExportOperation();
            OperationResult<SazExportPlan> plan =
                await operation.PrepareAsync(
                        new(
                            source,
                            SazExportKind.Plain,
                            null,
                            SazExportBudgets.Default),
                        new OperationContext(
                            progress: new InlineProgress<
                                OperationProgress>(
                                progress =>
                                    phases.Add(
                                        progress.Phase))),
                        CancellationToken.None);
            OperationResult<SazExportResult> result =
                await operation.ExecuteAsync(
                        new(
                            plan.Value,
                            new SazExportDestination.ToStream(
                                new MemoryStream()),
                            null),
                        OperationContext.None,
                        CancellationToken.None);

            Assert.DoesNotContain(
                Canary,
                string.Join(
                    "|",
                    result.Diagnostics.Select(
                        diagnostic =>
                            diagnostic.Code +
                            diagnostic.Message)));
            Assert.DoesNotContain(
                phases,
                phase => phase.Contains(
                    Canary,
                    StringComparison.Ordinal));
        }

        [Fact]
        public async Task T12_ProgressEndsAtEveryPlannedRow_IncludingSkippedRows()
        {
            ProxyConnectionModel model = Model();
            var source =
                new SazExportTestSupport.ScriptedSource(
                    new[]
                    {
                        new SazExportKey(1, 1, 0),
                        new SazExportKey(2, 2, 0),
                        new SazExportKey(3, 3, 0),
                    },
                    (index, _) =>
                        index == 1
                            ? SazExportTestSupport.Stored(
                                Model(
                                    requestHead:
                                        string.Empty),
                                index + 1)
                            : SazExportTestSupport.Stored(
                                model,
                                index + 1));
            var reports =
                new List<SazExportProgress>();
            using var output = new MemoryStream();

            OperationResult<SazExportResult> result = await RunAsync(
                source,
                SazExportKind.Plain,
                output,
                new InlineProgress<SazExportProgress>(
                    reports.Add));

            Assert.True(result.Succeeded);
            Assert.Equal(
                new SazExportProgress(3, 3),
                reports[^1]);
            Assert.Equal(
                1,
                result.Value.Summary.RowsWithoutRequest);
        }

        [Fact]
        public async Task T15_StoredTextAcceptanceIsBoundToOneSource()
        {
            CaptureTextSource text =
                CaptureTextSource.CreateFile(
                    CaptureTextSemantics
                        .AmbiguousUnmarked,
                    new SqLtFileIdentity(1, 1),
                    _ => Enumerable.Repeat(
                            (byte)0x2a,
                            32)
                        .ToArray(),
                    () => true);
            CaptureTextSource bound =
                await text.BindAsync(
                    CancellationToken.None);
            var source =
                new SazExportTestSupport.ScriptedSource(
                    Array.Empty<SazExportKey>(),
                    (_, _) =>
                        throw new InvalidOperationException(),
                    text);
            var operation = new SazExportOperation();

            OperationResult<SazExportPlan> refused =
                await operation.PrepareAsync(
                    new(
                        source,
                        SazExportKind.Plain,
                        null,
                        SazExportBudgets.Default),
                    OperationContext.None,
                    CancellationToken.None);
            OperationResult<SazExportPlan> accepted =
                await operation.PrepareAsync(
                    new(
                        source,
                        SazExportKind.Plain,
                        StoredTextAcceptance.For(bound),
                        SazExportBudgets.Default),
                    OperationContext.None,
                    CancellationToken.None);

            Assert.Equal(
                SazExportCodes.AmbiguousExport,
                Assert.Single(
                    refused.Diagnostics).Code);
            Assert.True(accepted.Succeeded);
        }

        [Fact]
        public async Task T16_PrepareWritesNothing_AndAPlanCanRunOnlyOnce()
        {
            var source =
                new SazExportTestSupport.ScriptedSource(
                    Array.Empty<SazExportKey>(),
                    (_, _) =>
                        throw new InvalidOperationException());
            var operation = new SazExportOperation();
            OperationResult<SazExportPlan> prepared =
                await operation.PrepareAsync(
                        new(
                            source,
                            SazExportKind.Plain,
                            null,
                            SazExportBudgets.Default),
                        OperationContext.None,
                        CancellationToken.None);
            using var first = new MemoryStream();
            using var second = new MemoryStream();

            OperationResult<SazExportResult> ran =
                await operation.ExecuteAsync(
                        new(
                            prepared.Value,
                            new SazExportDestination.ToStream(
                                first),
                            null),
                        OperationContext.None,
                        CancellationToken.None);
            OperationResult<SazExportResult> repeated =
                await operation.ExecuteAsync(
                        new(
                            prepared.Value,
                            new SazExportDestination.ToStream(
                                second),
                            null),
                        OperationContext.None,
                        CancellationToken.None);

            Assert.True(ran.Succeeded);
            Assert.True(first.Length > 0);
            Assert.Equal(
                SazExportCodes.PlanUsed,
                repeated.Value.Code);
        }

        [Theory]
        [InlineData(
            "POST / HTTP/1.1\r\nHost: example.test\r\nContent-Type: text/plain\r\n\r\n")]
        [InlineData(
            "POST / HTTP/1.1\r\nHost: example.test\r\nContent-Type: text/plain\r\n")]
        [InlineData(
            "POST / HTTP/1.1\r\nHost: example.test\r\nContent-Type: text/plain")]
        [InlineData(
            "POST / HTTP/1.1\nHost: example.test\nContent-Type: text/plain\n\n")]
        public void T18_HeadsAlwaysHaveOneSeparatorBeforeTheirBodies(
            string head)
        {
            byte[] body =
                "X-Injected: 1\r\n\r\ntail"u8.ToArray();
            using var output = new MemoryStream();
            OperationResult<SazExportResult> result =
                SazExportTestSupport.ExportRecords(
                    output,
                    new[]
                    {
                        Model(
                            requestHead: head,
                            requestBody: body),
                    },
                    SazExportKind.Plain);
            byte[] written = Entry(
                output,
                "raw/1_c.txt");
            int separator = Separator(written);

            Assert.True(result.Succeeded);
            Assert.Equal(
                body,
                written[(separator + 4)..]);
            Assert.DoesNotContain(
                "X-Injected",
                Encoding.Latin1.GetString(
                    written,
                    0,
                    separator));
        }

        private static async Task<OperationResult<SazExportResult>> RunAsync(
            SazExportSource source,
            SazExportKind kind,
            Stream output,
            IProgress<SazExportProgress> progress = null)
        {
            var operation = new SazExportOperation();
            OperationResult<SazExportPlan> prepared =
                await operation.PrepareAsync(
                        new(
                            source,
                            kind,
                            null,
                            SazExportBudgets.Default),
                        OperationContext.None,
                        CancellationToken.None);
            Assert.True(prepared.Succeeded);
            return await operation.ExecuteAsync(
                    new(
                        prepared.Value,
                        new SazExportDestination.ToStream(
                            output),
                        progress),
                    OperationContext.None,
                    CancellationToken.None);
        }

        private static ProxyConnectionModel Model(
            long number = 1,
            string requestHead =
                "POST / HTTP/1.1\r\n" +
                "Host: example.test\r\n" +
                "Content-Type: application/octet-stream\r\n\r\n",
            byte[] requestBody = null,
            string responseHead =
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: application/octet-stream\r\n\r\n",
            byte[] responseBody = null,
            string scheme = "https",
            string clientAddress = "10.0.0.1") =>
            new(
                number,
                "200",
                new DateTime(
                    2026,
                    1,
                    1,
                    0,
                    0,
                    0,
                    DateTimeKind.Utc),
                TimeSpan.FromMilliseconds(10),
                scheme,
                "example.test:443",
                "POST",
                "/",
                requestHead,
                requestBody ??
                    Array.Empty<byte>(),
                responseHead,
                responseBody ??
                    Array.Empty<byte>(),
                clientAddress);

        private static SazMessage Message(
            string startLine) =>
            new(
                startLine,
                Array.Empty<
                    KeyValuePair<string, string>>(),
                ReadOnlyMemory<byte>.Empty);

        private static int Separator(byte[] message)
        {
            int separator =
                message.AsSpan().IndexOf(
                    "\r\n\r\n"u8);
            Assert.True(separator >= 0);
            return separator;
        }

        private static byte[] Entry(
            MemoryStream archive,
            string name)
        {
            archive.Position = 0;
            using var zip = new ZipArchive(
                archive,
                ZipArchiveMode.Read,
                leaveOpen: true);
            ZipArchiveEntry entry =
                zip.GetEntry(name);
            Assert.NotNull(entry);
            using Stream input = entry.Open();
            using var bytes = new MemoryStream();
            input.CopyTo(bytes);
            return bytes.ToArray();
        }

        private static string[] EntryNames(
            MemoryStream archive)
        {
            archive.Position = 0;
            using var zip = new ZipArchive(
                archive,
                ZipArchiveMode.Read,
                leaveOpen: true);
            return zip.Entries
                .Select(
                    static entry =>
                        entry.FullName)
                .ToArray();
        }

        private static TempArchive CopyToArchive(
            MemoryStream archive)
        {
            string path = Path.Join(
                Path.GetTempPath(),
                "xmat-s2-operation-" +
                    Guid.NewGuid().ToString("N") +
                    ".saz");
            File.WriteAllBytes(
                path,
                archive.ToArray());
            return new TempArchive(path);
        }

        private sealed class TempArchive : IDisposable
        {
            internal TempArchive(string path)
            {
                Path = path;
            }

            internal string Path { get; }

            public void Dispose() =>
                File.Delete(Path);
        }

        private sealed class InlineProgress<T> :
            IProgress<T>
        {
            private readonly Action<T> _report;

            internal InlineProgress(Action<T> report)
            {
                _report = report;
            }

            public void Report(T value) =>
                _report(value);
        }
    }
}
