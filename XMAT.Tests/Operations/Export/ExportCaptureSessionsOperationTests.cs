// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Text;
using XMAT.DataAbstractionLayer;
using XMAT.Operations;
using XMAT.Operations.Analysis;
using XMAT.Operations.Captures;
using XMAT.Operations.Export;
using XMAT.SharedInterfaces;
using XMAT.Tests.Fixtures;
using XMAT.Tests.Operations.Analysis;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using static XMAT.Tests.Operations.Export.ExportTestData;

namespace XMAT.Tests.Operations.Export
{
    public sealed class ExportCaptureSessionsOperationTests
    {
        [Fact]
        public async Task Execute_ExportsDistinctRequestResponsePairsWithoutChangingTheSource()
        {
            using var directory = new ExportTestDirectory();
            string source = WriteMarkedCapture(directory.Root);
            string sourceHash = Sha256(source);
            string output = directory.PathOf("selected.xmatcap");
            var selection = Selection(host: new HostSelection(
                HostMatchMode.Exact,
                ImmutableArray.Create("userpresence.xboxlive.com"),
                ImmutableArray<string>.Empty));
            var progress = new RecordingProgress();

            OperationResult<ExportCaptureResult> result = await new ExportCaptureSessionsOperation().ExecuteAsync(
                new ExportCaptureRequest(
                    source,
                    null,
                    null,
                    selection,
                    CaptureExportFormat.Xmatcap,
                    output,
                    ReplaceExisting: false,
                    AnalysisLimits.Default,
                    AllowUnprotectedOutput: false),
                new OperationContext(progress: progress),
                CancellationToken.None);

            Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
            Assert.True(result.Value.Published);
            Assert.Equal(true, result.Value.OutputProtected);
            Assert.Equal(2, result.Value.Counts.Exported);
            Assert.Equal(sourceHash, Sha256(source));
            int publish = progress.Updates.FindIndex(update =>
                update.Phase == ExportCaptureSessionsOperation.PublishPhase);
            int lastReadOrWrite = progress.Updates.FindLastIndex(update =>
                update.Phase == ExportCaptureSessionsOperation.ReadPhase ||
                update.Phase == ExportCaptureSessionsOperation.WritePhase);
            Assert.True(publish > lastReadOrWrite);
            using var capture = new SqLtReadonlyDatabase(output);
            IDataRecord[] rows = capture.TableByName(WebServiceCaptureMethod.TableName).Dataset.DataRecords.ToArray();
            Assert.Equal(new long[] { 1, 3 }, rows.Select(row =>
                row.Int(WebServiceCaptureMethod.FieldKey_RequestNumber)));
            foreach (IDataRecord row in rows)
            {
                long number = row.Int(WebServiceCaptureMethod.FieldKey_RequestNumber);
                Assert.Contains($"request-{number}", row.Str(WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders));
                Assert.Equal($"request-body-{number}", Decode(row.Str(WebServiceCaptureMethod.FieldKey_RequestBody)));
                Assert.Contains($"response-{number}", row.Str(WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders));
                Assert.Equal($"response-body-{number}", Decode(row.Str(WebServiceCaptureMethod.FieldKey_ResponseBody)));
            }
        }

        [Fact]
        public async Task Execute_SazBodiesRoundTripAsTextThroughXmatcapAndAnalysis()
        {
            using var directory = new ExportTestDirectory();
            string source = SyntheticCaptures.WriteSazFrames(
                directory.Root,
                "source.saz",
                [SyntheticSazFrameKind.Valid, SyntheticSazFrameKind.RequestOnly]);
            string output = directory.PathOf("from-saz.xmatcap");
            ProxyConnectionModel[] expected = FiddlerSazHandler.ImportSazFile(source, CancellationToken.None).ToArray();

            OperationResult<ExportCaptureResult> export = await new ExportCaptureSessionsOperation().ExecuteAsync(
                new ExportCaptureRequest(
                    source,
                    null,
                    null,
                    Selection(),
                    CaptureExportFormat.Xmatcap,
                    output,
                    ReplaceExisting: false,
                    AnalysisLimits.Default,
                    AllowUnprotectedOutput: false),
                OperationContext.None,
                CancellationToken.None);

            Assert.Equal(OperationOutcome.Succeeded, export.Outcome);
            using var capture = new SqLtReadonlyDatabase(output);
            IDataRecord[] rows = capture.TableByName(WebServiceCaptureMethod.TableName).Dataset.DataRecords.ToArray();
            Assert.Equal(2, rows.Length);
            for (var index = 0; index < rows.Length; index++)
            {
                var raw = (IRawDataRecord)rows[index];
                Assert.True(raw.TryReadRaw(WebServiceCaptureMethod.FieldKey_RequestBody, out object request));
                Assert.True(raw.TryReadRaw(WebServiceCaptureMethod.FieldKey_ResponseBody, out object response));
                Assert.IsType<string>(request);
                Assert.IsType<string>(response);
                var model = new ProxyConnectionModel(rows[index]);
                Assert.Equal(
                    expected[index].RequestBody ?? Array.Empty<byte>(),
                    model.RequestBody ?? Array.Empty<byte>());
                Assert.Equal(
                    expected[index].ResponseBody ?? Array.Empty<byte>(),
                    model.ResponseBody ?? Array.Empty<byte>());
            }
            OperationResult<AnalysisResult> analysis = await new AnalyzeCaptureOperation().ExecuteAsync(
                new AnalyzeCaptureRequest(AnalysisInput.FromFile(output), new AnalysisOptions()),
                OperationContext.None,
                CancellationToken.None);
            Assert.Equal(OperationOutcome.Succeeded, analysis.Outcome);
            Assert.True(analysis.Value.Summary.TotalCalls > 0);
        }

        // xmat export reads XMAT's SAZ flags for a streamed response's trailers and truncated capture, and keeps both in the
        // optional columns of an XMATCAP.
        [Fact]
        public async Task Execute_SazResponseTrailersAndTruncationRoundTripThroughXmatcap()
        {
            using var directory = new ExportTestDirectory();
            string source = directory.PathOf("streamed.saz");
            ProxyConnectionModel streamed = ProcessCaptures.ExportRecord(1);
            streamed.SetResponseCaptureDetails("x-checksum: abc123\r\n", 16);
            SazArchive.Write(source, new[] { streamed });
            string output = directory.PathOf("streamed.xmatcap");

            OperationResult<ExportCaptureResult> export = await new ExportCaptureSessionsOperation().ExecuteAsync(
                new ExportCaptureRequest(
                    source,
                    null,
                    null,
                    Selection(),
                    CaptureExportFormat.Xmatcap,
                    output,
                    ReplaceExisting: false,
                    AnalysisLimits.Default,
                    AllowUnprotectedOutput: false),
                OperationContext.None,
                CancellationToken.None);

            Assert.Equal(OperationOutcome.Succeeded, export.Outcome);
            using var capture = new SqLtReadonlyDatabase(output);
            var model = new ProxyConnectionModel(
                Assert.Single(capture.TableByName(WebServiceCaptureMethod.TableName).Dataset.DataRecords));
            Assert.Equal(("x-checksum: abc123\r\n", 16L), (model.ResponseTrailers, model.ResponseBodyTruncatedAt));
        }

        [Fact]
        public async Task Execute_CountsIncompleteAndUnavailableRowsWithoutSubstitution()
        {
            CaptureExportSession[] sessions =
            [
                Session(1),
                Session(2),
                Session(3),
                Session(4),
            ];
            var snapshot = new FakeExportSnapshot(sessions, sessions.Select(session => Row(session.Key.RowId)))
            {
                MaxConsumed = 1,
            };
            snapshot.Missing.Add(Key(2));
            var writer = new RecordingExportWriter();
            var operation = Operation(snapshot, writer);

            OperationResult<ExportCaptureResult> result = await operation.ExecuteAsync(
                Request(snapshot, Selection()),
                OperationContext.None,
                CancellationToken.None);

            Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
            Assert.Equal(new CaptureExportCounts(4, 4, 4, 4, 3, 1, 0, 0), result.Value.Counts);
            Assert.Equal(new[] { Key(1), Key(3), Key(4) }, writer.Rows.Select(row => row.Key));
            Assert.Equal(4, snapshot.ReadRequests.Count);
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
        [InlineData("validation")]
        [InlineData("after-selection")]
        [InlineData("final-loop")]
        public async Task Execute_CancellationDuringSelectionStopsBeforePublisherOrWriter(string phase)
        {
            using var cancellation = new CancellationTokenSource();
            CaptureExportSession[] values = Enumerable.Range(1, 32).Select(index => Session(index)).ToArray();
            CancelingSessionList canceling = phase == "validation"
                ? new CancelingSessionList(values, cancellation, cancelOnVisit: 2)
                : null;
            var comparer = phase == "final-loop" ? new CancelingKeyComparer(cancellation, cancelOnCall: 2) : null;
            ImmutableHashSet<CaptureRowKey> keys = comparer == null ? null :
                ImmutableHashSet.CreateRange(comparer, values.Select(session => session.Key));
            if (comparer != null)
            {
                comparer.Armed = true;
            }
            IReadOnlyList<CaptureExportSession> sessions = canceling == null ? values : canceling;
            var snapshot = new FakeExportSnapshot(sessions, values.Select(session => Row(session.Key.RowId)));
            var progress = phase == "after-selection"
                ? new RecordingProgress(value =>
                {
                    if (value?.Phase == ExportCaptureSessionsOperation.SelectPhase && value.Fraction == 1)
                    {
                        cancellation.Cancel();
                    }
                })
                : null;

            await AssertCancelledBeforePublishAsync(snapshot, cancellation.Token, progress,
                keys == null ? null : Selection(keys: keys));

            if (canceling != null)
            {
                Assert.True(canceling.Visits < canceling.Count, "Selection continued visiting sessions after cancellation.");
            }
            else if (comparer != null)
            {
                Assert.True(comparer.Calls < values.Length, "Final selection continued after cancellation.");
            }
            else
            {
                Assert.True(cancellation.IsCancellationRequested);
            }
        }

        private static async Task AssertCancelledBeforePublishAsync(
            ICaptureExportSnapshot snapshot,
            CancellationToken cancellationToken,
            IProgress<OperationProgress> progress = null,
            CaptureSelectionSpec selection = null)
        {
            var publisher = new RecordingExportPublisher();
            var writerCreations = 0;
            var operation = new ExportCaptureSessionsOperation
            {
                Publisher = publisher,
                WriterFactory = (_, _, _, _) =>
                {
                    writerCreations++;
                    return new RecordingExportWriter();
                },
            };

            OperationResult<ExportCaptureResult> result = await operation.ExecuteAsync(
                Request(snapshot, selection ?? Selection()),
                progress == null ? OperationContext.None : new OperationContext(progress: progress),
                cancellationToken);

            Assert.Equal(OperationOutcome.Cancelled, result.Outcome);
            OperationDiagnostic diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal("export.cancelled", diagnostic.Code);
            Assert.Equal(OperationDiagnosticSeverity.Info, diagnostic.Severity);
            Assert.Equal(0, publisher.Calls);
            Assert.Equal(0, writerCreations);
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
            var publisher = new RecordingExportPublisher();
            var operation = new ExportCaptureSessionsOperation
            {
                Publisher = publisher,
                WriterFactory = (_, _, _, _) => new RecordingExportWriter(),
            };

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

            OperationResult<ExportCaptureResult> mismatch = await operation.ExecuteAsync(
                Request(snapshot, Selection()) with { OutputPath = "result.saz" },
                OperationContext.None,
                CancellationToken.None);
            Assert.Equal(OperationOutcome.InvalidInput, mismatch.Outcome);
            Assert.Equal(CaptureExportErrorCodes.FormatMismatch, Assert.Single(mismatch.Diagnostics).Code);
            Assert.Equal(0, publisher.Calls);
        }

        [Fact]
        public async Task Execute_CancellationAndUnexpectedWriterFailuresKeepTheirClassifications()
        {
            var snapshot = new FakeExportSnapshot([Session(1)], [Row(1)]);
            var cancelledWriter = new RecordingExportWriter
            {
                WriteFailure = new OperationCanceledException(),
            };

            OperationResult<ExportCaptureResult> cancelled = await Operation(snapshot, cancelledWriter).ExecuteAsync(
                Request(snapshot, Selection()),
                OperationContext.None,
                CancellationToken.None);

            Assert.Equal(OperationOutcome.Cancelled, cancelled.Outcome);
            Assert.False(cancelled.Value.Published);
            Assert.Equal(0, cancelled.Value.Counts.Exported);

            var unexpectedWriter = new RecordingExportWriter
            {
                WriteFailure = new InvalidOperationException("unexpected writer failure"),
            };
            OperationResult<ExportCaptureResult> unexpected = await Operation(snapshot, unexpectedWriter).ExecuteAsync(
                Request(snapshot, Selection()),
                OperationContext.None,
                CancellationToken.None);
            Assert.Equal(OperationOutcome.InternalError, unexpected.Outcome);
            Assert.Equal("export.internal", Assert.Single(unexpected.Diagnostics).Code);
            Assert.False(unexpected.Value.Published);

            var sqliteWriter = new RecordingExportWriter
            {
                WriteFailure = SqliteFailure(19),
            };
            OperationResult<ExportCaptureResult> sqlite = await Operation(snapshot, sqliteWriter).ExecuteAsync(
                Request(snapshot, Selection()),
                OperationContext.None,
                CancellationToken.None);
            Assert.Equal(OperationOutcome.InternalError, sqlite.Outcome);
        }

        [Fact]
        public async Task Execute_FileAdmissionUsesExportCodesAndPrechecksDestinationConflicts()
        {
            using var directory = new ExportTestDirectory();
            string source = WriteMarkedCapture(directory.Root);
            var request = new ExportCaptureRequest(
                source,
                null,
                null,
                Selection(),
                CaptureExportFormat.Xmatcap,
                directory.PathOf("subset.xmatcap"),
                ReplaceExisting: false,
                AnalysisLimits.Default with { MaxInputBytes = 1 },
                AllowUnprotectedOutput: false);

            OperationResult<ExportCaptureResult> result = await new ExportCaptureSessionsOperation().ExecuteAsync(
                request,
                OperationContext.None,
                CancellationToken.None);

            Assert.Equal(OperationOutcome.InvalidInput, result.Outcome);
            Assert.Equal("export.input.limit", Assert.Single(result.Diagnostics).Code);
            Assert.DoesNotContain(result.Diagnostics, diagnostic =>
                diagnostic.Code == CaptureExportErrorCodes.InvalidBody);
            string input = directory.PathOf("malformed.xmatcap");
            string output = directory.PathOf("existing.xmatcap");
            File.WriteAllText(input, "not a capture");
            File.WriteAllText(output, "keep");

            OperationResult<ExportCaptureResult> conflict = await new ExportCaptureSessionsOperation().ExecuteAsync(
                new ExportCaptureRequest(
                    input,
                    null,
                    null,
                    Selection(),
                    CaptureExportFormat.Xmatcap,
                    output,
                    ReplaceExisting: false,
                    AnalysisLimits.Default,
                    AllowUnprotectedOutput: false),
                OperationContext.None,
                CancellationToken.None);

            Assert.Equal(OperationOutcome.Conflict, conflict.Outcome);
            Assert.Equal("export.output.exists", Assert.Single(conflict.Diagnostics).Code);
            Assert.Equal("keep", File.ReadAllText(output));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Execute_CleanupFailurePreservesPublicationState(bool committed)
        {
            var snapshot = new FakeExportSnapshot([Session(1)], [Row(1)]);
            var operation = new ExportCaptureSessionsOperation
            {
                Publisher = new CleanupFailurePublisher(committed),
                WriterFactory = (_, _, _, _) => new RecordingExportWriter(),
            };

            OperationResult<ExportCaptureResult> result = await operation.ExecuteAsync(
                Request(snapshot, Selection()),
                OperationContext.None,
                CancellationToken.None);

            Assert.Equal(OperationOutcome.CleanupFailed, result.Outcome);
            Assert.Equal(committed, result.Value.Published);
            Assert.Equal(committed ? 1 : 0, result.Value.Counts.Exported);
            Assert.Equal(committed ? 123L : (long?)null, result.Value.OutputBytes);
            Assert.Equal(committed ? true : (bool?)null, result.Value.OutputProtected);
        }

        [Fact]
        public async Task Execute_DestinationLockIsADistinctConflict()
        {
            using var directory = new ExportTestDirectory();
            string output = directory.PathOf("locked.xmatcap");
            File.WriteAllText(output, "locked");
            using var blocker = new FileStream(output, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var snapshot = new FakeExportSnapshot([Session(1)], [Row(1)]);
            var operation = new ExportCaptureSessionsOperation
            {
                WriterFactory = (_, _, _, _) => new RecordingExportWriter(),
            };

            OperationResult<ExportCaptureResult> result = await operation.ExecuteAsync(
                Request(snapshot, Selection()) with
                {
                    OutputPath = output,
                    ReplaceExisting = true,
                },
                OperationContext.None,
                CancellationToken.None);

            Assert.Equal(OperationOutcome.Conflict, result.Outcome);
            Assert.Equal("export.output.locked", Assert.Single(result.Diagnostics).Code);
            blocker.Position = 0;
            using var reader = new StreamReader(blocker, Encoding.UTF8, true, 1024, leaveOpen: true);
            Assert.Equal("locked", reader.ReadToEnd());
        }

        [Theory]
        [InlineData("sharing", nameof(OperationOutcome.Conflict), "export.output.locked")]
        [InlineData("lock", nameof(OperationOutcome.Conflict), "export.output.locked")]
        [InlineData("denied", nameof(OperationOutcome.Failed), CaptureExportErrorCodes.WriteFailed)]
        [InlineData("wrapped-denied", nameof(OperationOutcome.Failed), CaptureExportErrorCodes.WriteFailed)]
        [InlineData("wrapped-sharing", nameof(OperationOutcome.Conflict), "export.output.locked")]
        public async Task Execute_ClassifiesLockingSeparatelyFromAccessDenied(
            string failureKind,
            string outcomeName,
            string code)
        {
            Exception failure = failureKind switch
            {
                "sharing" => new Win32IOException(32),
                "lock" => new Win32IOException(33),
                "denied" => new UnauthorizedAccessException("denied"),
                "wrapped-denied" => new CaptureSaveException(
                    CaptureSaveFailure.DestinationLocked,
                    new UnauthorizedAccessException("denied")),
                _ => new CaptureSaveException(
                    CaptureSaveFailure.DestinationLocked,
                    new Win32IOException(32)),
            };
            var snapshot = new FakeExportSnapshot([Session(1)], [Row(1)]);
            var operation = new ExportCaptureSessionsOperation
            {
                Publisher = new FailurePublisher(failure),
                WriterFactory = (_, _, _, _) => new RecordingExportWriter(),
            };

            OperationResult<ExportCaptureResult> result = await operation.ExecuteAsync(
                Request(snapshot, Selection()),
                OperationContext.None,
                CancellationToken.None);

            Assert.Equal(Enum.Parse<OperationOutcome>(outcomeName), result.Outcome);
            Assert.Equal(code, Assert.Single(result.Diagnostics).Code);
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
                AnalysisLimits.Default,
                AllowUnprotectedOutput: false);

        private static string WriteMarkedCapture(string directory)
            => ProcessCaptures.WriteXmatcap(
                directory,
                "marked-source.xmatcap",
                [
                    new ProcessSession("Game.exe", 4100, "local", PairMarker: "1"),
                    new ProcessSession("Tool.exe", 4200, "local", Analyzable: false, PairMarker: "2"),
                    new ProcessSession("Game.exe", 4100, "local", PairMarker: "3"),
                ]);

        private static string Decode(string value) =>
            Encoding.UTF8.GetString(Convert.FromBase64String(value));

        private sealed class CleanupFailurePublisher : ICaptureExportPublisher
        {
            private readonly bool _committed;

            internal CleanupFailurePublisher(bool committed)
            {
                _committed = committed;
            }

            public async Task<PublishedCapture> PublishAsync(
                string destination,
                bool replaceExisting,
                bool allowUnprotected,
                Action<string> validateDestination,
                Func<string, CancellationToken, Task> writeStaged,
                CancellationToken cancellationToken)
            {
                _ = replaceExisting;
                _ = allowUnprotected;
                validateDestination(destination);
                await writeStaged("staged-output", cancellationToken);
                var publication = new PublishedCapture(123, "output-sha256", false, Protected: true);
                throw new CaptureExportCleanupException(
                    _committed ? publication : null,
                    null,
                    new IOException("injected cleanup failure"));
            }
        }

        private sealed class FailurePublisher(Exception failure) : ICaptureExportPublisher
        {
            public Task<PublishedCapture> PublishAsync(
                string destination,
                bool replaceExisting,
                bool allowUnprotected,
                Action<string> validateDestination,
                Func<string, CancellationToken, Task> writeStaged,
                CancellationToken cancellationToken) =>
                Task.FromException<PublishedCapture>(failure);
        }

        private sealed class Win32IOException : IOException
        {
            internal Win32IOException(int code)
                : base("injected Win32 failure " + code)
            {
                HResult = unchecked((int)0x80070000) | code;
            }
        }

        private sealed class RecordingProgress(Action<OperationProgress> reported = null)
            : IProgress<OperationProgress>
        {
            internal List<OperationProgress> Updates { get; } = new();

            public void Report(OperationProgress value)
            {
                Updates.Add(value);
                reported?.Invoke(value);
            }
        }

        private sealed class CancelingSessionList(
            IReadOnlyList<CaptureExportSession> sessions,
            CancellationTokenSource cancellation,
            int cancelOnVisit) : IReadOnlyList<CaptureExportSession>
        {
            internal int Visits { get; private set; }
            public int Count => sessions.Count;
            public CaptureExportSession this[int index] => sessions[index];

            public IEnumerator<CaptureExportSession> GetEnumerator()
            {
                foreach (CaptureExportSession session in sessions)
                {
                    Visits++;
                    if (Visits == cancelOnVisit)
                    {
                        cancellation.Cancel();
                    }
                    yield return session;
                }
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        private sealed class CancelingKeyComparer(
            CancellationTokenSource cancellation,
            int cancelOnCall) : IEqualityComparer<CaptureRowKey>
        {
            internal bool Armed { get; set; }
            internal int Calls { get; private set; }
            public bool Equals(CaptureRowKey left, CaptureRowKey right) =>
                EqualityComparer<CaptureRowKey>.Default.Equals(left, right);
            public int GetHashCode(CaptureRowKey value)
            {
                if (Armed && ++Calls == cancelOnCall)
                {
                    cancellation.Cancel();
                }
                return EqualityComparer<CaptureRowKey>.Default.GetHashCode(value);
            }
        }
    }
}
