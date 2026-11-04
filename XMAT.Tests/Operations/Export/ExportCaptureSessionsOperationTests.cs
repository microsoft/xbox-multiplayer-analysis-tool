// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using XMAT.Operations;
using XMAT.Operations.Analysis;
using XMAT.Operations.Captures;
using XMAT.Operations.Export;
using XMAT.Tests.Fixtures;
using XMAT.WebServiceCapture;
using static XMAT.Tests.Operations.Export.ExportTestData;

namespace XMAT.Tests.Operations.Export
{
    public sealed class ExportCaptureSessionsOperationTests
    {
        [Fact]
        public async Task Execute_ExportsTheExactSubsetWithPairsIntactInBoundedChunks()
        {
            CaptureExportSession[] sessions = Enumerable.Range(1, 130)
                .Select(index => Session(index))
                .ToArray();
            RawExportRow[] rows = Enumerable.Range(1, 130).Select(index => Row(index)).ToArray();
            var snapshot = new FakeExportSnapshot(sessions, rows);
            ImmutableHashSet<CaptureRowKey> keys = sessions.Where((_, index) => index % 2 == 0)
                .Select(session => session.Key)
                .ToImmutableHashSet();
            var writer = new RecordingExportWriter();
            var publisher = new RecordingExportPublisher();
            long expectedRows = -1;
            var operation = new ExportCaptureSessionsOperation
            {
                Publisher = publisher,
                WriterFactory = (_, _, _, expected) =>
                {
                    expectedRows = expected;
                    return writer;
                },
            };
            var progress = new RecordingProgress();
            ExportCaptureRequest request = Request(snapshot, Selection(keys: keys));

            OperationResult<ExportCaptureResult> result = await operation.ExecuteAsync(
                request,
                new OperationContext(progress: progress),
                CancellationToken.None);

            Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
            Assert.Equal(65, expectedRows);
            Assert.True(result.Value.Published);
            Assert.Equal(123, result.Value.OutputBytes);
            Assert.Equal("output-sha256", result.Value.OutputSha256);
            Assert.Equal(keys.OrderBy(key => key.RowId), writer.Rows.Select(row => row.Key).OrderBy(key => key.RowId));
            Assert.All(snapshot.ReadRequests, chunk => Assert.InRange(chunk.Count, 1, 64));
            Assert.Equal(2, snapshot.ReadRequests.Count);
            Assert.All(writer.Rows, row =>
            {
                Assert.Contains("GET ", (string)row.Values[WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders]);
                Assert.Contains("200 OK",
                    (string)row.Values[WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders]);
                Assert.False(string.IsNullOrEmpty(
                    (string)row.Values[WebServiceCaptureMethod.FieldKey_RequestBody]));
                Assert.False(string.IsNullOrEmpty(
                    (string)row.Values[WebServiceCaptureMethod.FieldKey_ResponseBody]));
            });
            Assert.Equal(new[]
            {
                ExportCaptureSessionsOperation.SelectPhase,
                ExportCaptureSessionsOperation.ReadPhase,
                ExportCaptureSessionsOperation.WritePhase,
                ExportCaptureSessionsOperation.PublishPhase,
            }, progress.Updates.Select(update => update.Phase).Distinct());
            Assert.Contains(progress.Updates, update =>
                update.Phase == ExportCaptureSessionsOperation.ReadPhase && update.Fraction is >= 0 and <= 1);
            Assert.Contains(progress.Updates, update =>
                update.Phase == ExportCaptureSessionsOperation.WritePhase && update.Fraction is >= 0 and <= 1);
        }

        [Fact]
        public async Task Execute_CountsIncompleteAndUnavailableRowsWithoutSubstitution()
        {
            CaptureExportSession[] sessions =
            [
                Session(1),
                Session(2, responseHeaders: false, responseTimestamp: false),
                Session(3),
            ];
            var snapshot = new FakeExportSnapshot(sessions, sessions.Select(session => Row(session.Key.RowId)))
            {
                IsLive = true,
            };
            snapshot.Missing.Add(Key(3));
            var writer = new RecordingExportWriter();
            var operation = Operation(snapshot, writer);

            OperationResult<ExportCaptureResult> result = await operation.ExecuteAsync(
                Request(snapshot, Selection()),
                OperationContext.None,
                CancellationToken.None);

            Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
            Assert.Equal(new CaptureExportCounts(3, 3, 3, 2, 1, 1, 1, 1), result.Value.Counts);
            Assert.Equal(new[] { Key(1) }, writer.Rows.Select(row => row.Key));
            Assert.True(result.Value.Published);
        }

        [Fact]
        public async Task Execute_ZeroEligibleRowsReturnsCountsWithoutPublishing()
        {
            CaptureExportSession[] sessions =
            [
                Session(1, responseTimestamp: false),
                Session(2, responseHeaders: false, responseTimestamp: false),
            ];
            var snapshot = new FakeExportSnapshot(sessions, sessions.Select(session => Row(session.Key.RowId)))
            {
                IsLive = true,
            };
            var publisher = new RecordingExportPublisher();
            var operation = new ExportCaptureSessionsOperation
            {
                Publisher = publisher,
                WriterFactory = (_, _, _, _) => new RecordingExportWriter(),
            };

            OperationResult<ExportCaptureResult> result = await operation.ExecuteAsync(
                Request(snapshot, Selection()),
                OperationContext.None,
                CancellationToken.None);

            Assert.Equal(OperationOutcome.NoAnalyzableData, result.Outcome);
            Assert.NotNull(result.Value);
            Assert.Equal(new CaptureExportCounts(2, 2, 2, 0, 0, 0, 2, 1), result.Value.Counts);
            Assert.False(result.Value.Published);
            Assert.Null(result.Value.OutputBytes);
            Assert.Null(result.Value.OutputSha256);
            Assert.Equal(0, publisher.Calls);
        }

        [Theory]
        [InlineData(CaptureExportErrorCodes.InvalidHttp, nameof(OperationOutcome.InvalidInput))]
        [InlineData(CaptureExportErrorCodes.InvalidBody, nameof(OperationOutcome.InvalidInput))]
        [InlineData(CaptureExportErrorCodes.SanitizerFailed, nameof(OperationOutcome.Failed))]
        [InlineData(CaptureExportErrorCodes.WriteFailed, nameof(OperationOutcome.Failed))]
        public async Task Execute_MapsCodedWriterFailuresAndKeepsEvaluatedCounts(string code, string outcomeName)
        {
            OperationOutcome expected = Enum.Parse<OperationOutcome>(outcomeName);
            var snapshot = new FakeExportSnapshot([Session(1)], [Row(1)]);
            var failure = new CaptureExportException(code, expected);
            var writer = new RecordingExportWriter();
            if (code == CaptureExportErrorCodes.SanitizerFailed)
            {
                writer.CompleteFailure = failure;
            }
            else
            {
                writer.WriteFailure = failure;
            }
            var operation = Operation(snapshot, writer);

            OperationResult<ExportCaptureResult> result = await operation.ExecuteAsync(
                Request(snapshot, Selection()),
                OperationContext.None,
                CancellationToken.None);

            Assert.Equal(expected, result.Outcome);
            Assert.NotNull(result.Value);
            Assert.NotNull(result.Value.Counts);
            Assert.Equal(0, result.Value.Counts.Exported);
            Assert.False(result.Value.Published);
            Assert.Null(result.Value.OutputBytes);
            Assert.Null(result.Value.OutputSha256);
            OperationDiagnostic diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal(code, diagnostic.Code);
            Assert.DoesNotContain("Game.exe", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Execute_InvalidRequestKeepsUnevaluatedCountsNull()
        {
            var snapshot = new FakeExportSnapshot([Session(1)], [Row(1)]);
            ExportCaptureRequest request = Request(snapshot, Selection()) with
            {
                InputPath = "input.xmatcap",
            };
            var operation = Operation(snapshot, new RecordingExportWriter());

            OperationResult<ExportCaptureResult> result = await operation.ExecuteAsync(
                request,
                OperationContext.None,
                CancellationToken.None);

            Assert.Equal(OperationOutcome.InvalidInput, result.Outcome);
            Assert.NotNull(result.Value);
            Assert.Null(result.Value.Counts);
            Assert.Null(result.Value.Devices);
            Assert.Null(result.Value.ProcessScope);
            Assert.False(result.Value.Published);
        }

        [Fact]
        public async Task Execute_CancellationRetainsCancelledClassificationAndPublishesNothing()
        {
            var snapshot = new FakeExportSnapshot([Session(1)], [Row(1)]);
            var writer = new RecordingExportWriter
            {
                WriteFailure = new OperationCanceledException(),
            };
            var operation = Operation(snapshot, writer);

            OperationResult<ExportCaptureResult> result = await operation.ExecuteAsync(
                Request(snapshot, Selection()),
                OperationContext.None,
                CancellationToken.None);

            Assert.Equal(OperationOutcome.Cancelled, result.Outcome);
            Assert.False(result.Value.Published);
            Assert.Equal(0, result.Value.Counts.Exported);
        }

        [Fact]
        public async Task Execute_FileInputLeavesTheSourceByteForByteUnchanged()
        {
            using var directory = new ExportTestDirectory();
            string source = SyntheticCaptures.WriteXmatcap(directory.Root, "source.xmatcap");
            string sourceHash = Sha256(source);
            var writer = new RecordingExportWriter();
            var operation = new ExportCaptureSessionsOperation
            {
                Publisher = new RecordingExportPublisher(),
                WriterFactory = (_, _, _, _) => writer,
            };
            var request = new ExportCaptureRequest(
                source,
                null,
                null,
                Selection(),
                CaptureExportFormat.Xmatcap,
                directory.PathOf("subset.xmatcap"),
                ReplaceExisting: false,
                AnalysisLimits.Default);

            OperationResult<ExportCaptureResult> result = await operation.ExecuteAsync(
                request,
                OperationContext.None,
                CancellationToken.None);

            Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
            Assert.Equal(sourceHash, Sha256(source));
            Assert.Equal(sourceHash, result.Value.InputSha256);
        }

        private static ExportCaptureSessionsOperation Operation(
            FakeExportSnapshot snapshot,
            RecordingExportWriter writer) =>
            new()
            {
                Publisher = new RecordingExportPublisher(),
                WriterFactory = (_, _, candidate, _) =>
                {
                    Assert.Same(snapshot, candidate);
                    return writer;
                },
            };

        private static ExportCaptureRequest Request(
            ICaptureExportSnapshot snapshot,
            CaptureSelectionSpec selection) =>
            new(
                null,
                snapshot,
                null,
                selection,
                CaptureExportFormat.Xmatcap,
                "result.xmatcap",
                ReplaceExisting: false,
                AnalysisLimits.Default);

        private sealed class RecordingProgress : IProgress<OperationProgress>
        {
            internal List<OperationProgress> Updates { get; } = new();

            public void Report(OperationProgress value) => Updates.Add(value);
        }
    }
}
