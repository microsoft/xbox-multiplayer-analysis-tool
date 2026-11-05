// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using XMAT.DataAbstractionLayer;
using XMAT.Models;
using XMAT.Operations;
using XMAT.Operations.Analysis;
using XMAT.Operations.Captures;
using XMAT.Operations.Export;
using XMAT.Operations.Processes;
using XMAT.SharedInterfaces;
using XMAT.Tests.Cli;
using XMAT.Tests.Operations.Analysis;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using static XMAT.Tests.Operations.Export.ExportTestData;

namespace XMAT.Tests
{
    // A GUI export holds the data-operation lease from its snapshot to its result, confirms what it will export (with
    // the counts) before it asks for a file, asks for consent before writing a sanitized archive, and always disposes its
    // snapshot and releases the lease. File > Export exports every session of the selected tab and never another tab's.
    public class CaptureExportWorkflowTests
    {
        // Three of four sessions are selected; the live capture has not recorded the third one's response yet. Stop waits
        // for the export, and a snapshot that fails to close keeps neither the lease nor the result from the user.
        [Fact]
        public async Task Export_ConfirmsTheCountsAndRunsTheSharedOperationUnderTheLease()
        {
            var app = new CaptureAppModel();
            ICaptureDeviceContext tab = Tab(app, "Game PC");
            var snapshot = new Snapshot(
                true, Session(1), Session(2), Session(3, responseHeaders: false, responseTimestamp: false), Session(4))
            {
                DisposeFailure = new IOException("Simulated close failure."),
            };
            CaptureSelectionSpec selection = Selection(keys: ImmutableHashSet.Create(Key(1), Key(2), Key(3)));
            var dialogs = new Dialogs { Destination = new("subset.xmatcap", CaptureExportFormat.Xmatcap, true) };
            var operation = new Operation(app);

            await Workflow(dialogs, snapshot, operation).RunAsync(app, tab, selection, sanitizedOnly: false);

            Assert.Equal(new[] { "confirm Game PC", "destination", "progress", "result" }, dialogs.Calls);
            Assert.Equal(new CaptureExportCounts(4, 4, 3, 2, 0, 0, 1, 1), dialogs.Counts);
            ExportCaptureRequest request = operation.Request;
            Assert.Same(snapshot, request.Snapshot);
            Assert.Same(selection, request.Selection);
            Assert.Equal(
                ((string)null, CaptureExportFormat.Xmatcap, "subset.xmatcap", true, AnalysisLimits.Default, false),
                (request.InputPath, request.Format, request.OutputPath, request.ReplaceExisting, request.Limits,
                    request.AllowUnprotectedOutput));
            Assert.True(operation.LeaseHeld, "The export must hold the data-operation lease while it runs.");
            Assert.False(operation.StopAllowed, "Stop must wait for the export.");
            Assert.True(operation.SnapshotOpen, "The snapshot must stay open until the export completes.");
            AssertReleased(app, snapshot);
        }

        // Busy: nothing is snapshotted. Nothing eligible (a live session without a recorded response): no file is asked
        // for. Otherwise the export stops at the dialog that the user cancels.
        [Theory]
        [InlineData("busy")]
        [InlineData("nothing")]
        [InlineData("confirm")]
        [InlineData("destination")]
        [InlineData("consent")]
        public async Task Export_WritesNothingWhenBusyEmptyOrCancelled(string step)
        {
            var app = new CaptureAppModel();
            var snapshot = new Snapshot(true, Session(1, responseTimestamp: step != "nothing"));
            var dialogs = new Dialogs
            {
                Confirm = step != "confirm",
                Destination = step == "destination"
                    ? null
                    : new CaptureExportDestination("subset.saz", CaptureExportFormat.SanitizedSaz, false),
                Consent = false,
            };
            var operation = new Operation(app);
            IDisposable busy = null;
            if (step == "busy")
            {
                Assert.True(app.TryBeginCaptureDataOperation(out busy));
            }

            using (busy)
            {
                await Workflow(dialogs, snapshot, operation)
                    .RunAsync(app, Tab(app, "Game PC"), Selection(), sanitizedOnly: false);
            }

            Assert.Equal(step, dialogs.Calls[^1].Split(' ')[0]);
            Assert.Equal(step == "busy" ? 0 : 1, snapshot.Tabs.Count);
            Assert.Null(operation.Request);
            AssertReleased(app, snapshot);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task FileExport_ExportsEverySessionOfTheSelectedTabOnly(bool sanitized)
        {
            var app = new CaptureAppModel();
            Tab(app, "Console");
            ICaptureDeviceContext selected = Tab(app, "Game PC");
            var snapshot = new Snapshot(true, Session(1));
            var dialogs = new Dialogs
            {
                Destination = new("tab.saz", sanitized ? CaptureExportFormat.SanitizedSaz : CaptureExportFormat.Saz, false),
            };
            var operation = new Operation(app);

            await Workflow(dialogs, snapshot, operation).RunForSelectedTabAsync(app, sanitized);

            Assert.Same(selected, Assert.Single(snapshot.Tabs));
            Assert.Equal(
                sanitized
                    ? new[] { "confirm Game PC", "destination sanitized", "consent", "progress", "result" }
                    : new[] { "confirm Game PC", "destination", "progress", "result" },
                dialogs.Calls);
            CaptureSelectionSpec all = operation.Request.Selection;
            Assert.True(
                ProcessFilter.IsEmpty(all.Process) && all.Host.Include == null && all.Host.Exclude.IsDefaultOrEmpty &&
                    all.Statuses == null && all.Methods == null && all.Keys == null && !all.MatchNone,
                "File > Export ignores the capture list's filters.");
            AssertReleased(app, snapshot);
        }

        // A file name of the other format is refused with a message before anything is written, and the save dialog
        // opens again.
        [Theory]
        [InlineData("subset.saz", nameof(CaptureExportFormat.Xmatcap), "subset.xmatcap")]
        [InlineData("subset.xmatcap", nameof(CaptureExportFormat.Saz), "subset.saz")]
        [InlineData("subset.XMATCAP", nameof(CaptureExportFormat.SanitizedSaz), "subset.SAZ")]
        public async Task Export_RefusesAFileNameOfTheOtherFormatAndAsksAgain(string mislabeled, string formatName, string labeled)
        {
            var app = new CaptureAppModel();
            CaptureExportFormat format = Enum.Parse<CaptureExportFormat>(formatName);
            var snapshot = new Snapshot(true, Session(1));
            var dialogs = new Dialogs { Destination = new(labeled, format, false) };
            dialogs.Destinations.Enqueue(new(mislabeled, format, false));
            var operation = new Operation(app);

            await Workflow(dialogs, snapshot, operation).RunAsync(app, Tab(app, "Game PC"), Selection(), sanitizedOnly: false);

            Assert.Equal(
                new[] { "destination", "failure " + CaptureExportErrorCodes.FormatMismatch, "destination" },
                dialogs.Calls.Skip(1).Take(3));
            Assert.Equal((labeled, format), (Assert.Single(operation.Requests).OutputPath, operation.Request.Format));
            AssertReleased(app, snapshot);
        }

        // Where staging cannot be made private, the export asks once about this destination: yes repeats the same export
        // with the exception allowed, no publishes nothing, and a cleanup failure never leads to the question. Nothing is
        // remembered: the next export asks again.
        [Theory]
        [InlineData(null, false, "progress|result")]
        [InlineData(nameof(OperationOutcome.Failed), true, "progress|unprotected subset.saz|progress|result")]
        [InlineData(nameof(OperationOutcome.Failed), false, "progress|unprotected subset.saz")]
        [InlineData(nameof(OperationOutcome.CleanupFailed), true, "progress|failure " + CaptureExportErrorCodes.OutputUnprotected)]
        public async Task Export_AsksOnceBeforePublishingWhereAccessCannotBeRestricted(string outcome, bool allow, string calls)
        {
            var app = new CaptureAppModel();
            var snapshot = new Snapshot(true, Session(1));
            var dialogs = new Dialogs { AllowUnprotected = allow, Destination = new("subset.saz", CaptureExportFormat.Saz, true) };
            var operation = new Operation(app);
            CaptureExportWorkflow workflow = Workflow(dialogs, snapshot, operation);
            for (var export = 0; export < 2; export++)
            {
                dialogs.Calls.Clear();
                operation.Requests.Clear();
                if (outcome != null)
                {
                    operation.Failures.Enqueue((Enum.Parse<OperationOutcome>(outcome), CaptureExportErrorCodes.OutputUnprotected));
                }

                await workflow.RunAsync(app, Tab(app, "Game PC"), Selection(), sanitizedOnly: false);

                Assert.Equal(calls.Split('|'), dialogs.Calls.Skip(2));
                Assert.Equal(calls.Split('|').Count(call => call == "progress"), operation.Requests.Count);
                Assert.Equal(
                    operation.Requests.Select((_, index) => operation.Requests[0] with { AllowUnprotectedOutput = index > 0 }),
                    operation.Requests);
            }

            AssertReleased(app, snapshot);
        }

        // The GUI exports a stored session of any size; only xmat export admits its input under analysis limits.
        [Fact]
        public async Task FileExport_WritesAStoredSessionLargerThanAnAdmittedRecord()
        {
            using var directory = new CliTestDirectory();
            byte[] body = new byte[(int)(AnalysisLimits.Default.MaxEntryBytes * 3 / 4) + 65536];
            var large = new ProxyConnectionModel(
                1, "200", new DateTime(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc), TimeSpan.FromMilliseconds(25), "https",
                "large.test:443", "GET", "/large", "GET https://large.test/large HTTP/1.1\r\nHost: large.test\r\n\r\n",
                Array.Empty<byte>(), "HTTP/1.1 200 OK\r\n\r\n", body, ProcessCaptures.ClientIp);
            using var database = new SqLtReadonlyDatabase(
                CaptureExportViewTests.WriteCapture(directory.Root, "large.xmatcap", new[] { large }));
            var controller = new WebServiceDeviceCaptureController(ProcessCaptures.Device, DeviceType.LocalPC, true);
            controller.LoadCaptureDataset(database.TableByName(WebServiceCaptureMethod.TableName).Dataset);
            var app = new CaptureAppModel();
            Tab(app, ProcessCaptures.Device, controller);
            string output = Path.Join(directory.Root, "large-copy.xmatcap");
            var dialogs = new Dialogs { Destination = new(output, CaptureExportFormat.Xmatcap, false) };

            await CaptureExportWorkflow.For(dialogs).RunForSelectedTabAsync(app, sanitizedOnly: false);

            Assert.Equal(OperationOutcome.Succeeded, dialogs.Result.Outcome);
            Assert.Equal(new long[] { 1 }, XmatExportTests.RequestNumbers(output));
        }

        // The last message tells what happened to the file: N of the capture's M sessions exported, with a warning when
        // the file could not be made private; exported although cleanup failed; or not changed. A failure is described by
        // its code, never with the operation's own text.
        [Theory]
        [InlineData(null, true, true, "EXPORT_COMPLETE")]
        [InlineData(null, true, false, "EXPORT_UNPROTECTED_WARNING")]
        [InlineData("export.cleanup.failed", true, true, "EXPORT_PUBLISHED_CLEANUP_FAILED")]
        [InlineData("export.cleanup.failed", false, null, "EXPORT_ERROR_CLEANUP")]
        [InlineData("export.not-yet-known", false, null, "EXPORT_ERROR_UNKNOWN")]
        public void ResultMessage_IsLocalizedByCodeAndTruthfulAboutTheFile(
            string code, bool published, bool? outputProtected, string key)
        {
            Localization.LoadLanguage("en-us");
            var value = new ExportCaptureResult(
                CaptureExportFormat.Saz, Selection(), new CaptureExportCounts(9, 6, 4, 3, published ? 2 : 0, 1, 1, 1), null, null,
                null, "records", null, null, null, published, outputProtected);
            OperationResult<ExportCaptureResult> result = code == null
                ? OperationResult<ExportCaptureResult>.Success(value)
                : new OperationResult<ExportCaptureResult>(
                    code == "export.cleanup.failed" ? OperationOutcome.CleanupFailed : OperationOutcome.Failed,
                    value,
                    new[] { new OperationDiagnostic(code, OperationDiagnosticSeverity.Error, "operation-text-canary") });

            string message = MainWindow.ExportResultMessage(result);
            bool Says(string text, params object[] values) =>
                message.Contains(Localization.GetLocalizedString(text, values), StringComparison.Ordinal);

            Assert.True(Says(key, 2L, 9L), key);
            Assert.Equal(published, Says("EXPORT_COMPLETE", 2L, 9L));
            Assert.Equal(!published, Says("EXPORT_UNCHANGED"));
            Assert.Equal(outputProtected == false, Says("EXPORT_UNPROTECTED_WARNING"));
            Assert.DoesNotContain("operation-text-canary", message, StringComparison.Ordinal);
            Assert.DoesNotContain("NO_IMPL_", message, StringComparison.Ordinal);
        }

        private static ICaptureDeviceContext Tab(CaptureAppModel app, string name, IDeviceCaptureController controller = null)
        {
            var tab = new Probe(name, controller);
            app.CaptureDeviceContexts.Add(tab);
            app.SelectedDeviceContext = tab;
            return tab;
        }

        private static CaptureExportWorkflow Workflow(Dialogs dialogs, Snapshot snapshot, Operation operation) =>
            new(dialogs, tab =>
            {
                snapshot.Tabs.Add(tab);
                return snapshot;
            }, operation);

        // Each snapshot that the export took is disposed once, and Stop and the lease are free again.
        private static void AssertReleased(CaptureAppModel app, Snapshot snapshot)
        {
            Assert.Equal(snapshot.Tabs.Count, snapshot.Disposals);
            Assert.True(app.CanStopCaptures, "The export must let Stop run again.");
            Assert.True(app.TryBeginCaptureDataOperation(out IDisposable next), "The export must release the lease.");
            next.Dispose();
        }

        // Records each dialog (the confirmation with its tab) and answers as configured; the save dialog first answers with
        // the queued Destinations.
        private sealed class Dialogs : ICaptureExportDialogService
        {
            internal List<string> Calls { get; } = new();
            internal bool Confirm { get; init; } = true;
            internal CaptureExportDestination Destination { get; init; } = new("subset.saz", CaptureExportFormat.Saz, false);
            internal Queue<CaptureExportDestination> Destinations { get; } = new();
            internal bool Consent { get; init; } = true;
            internal bool AllowUnprotected { get; init; }
            internal CaptureExportCounts Counts { get; private set; }
            internal OperationResult<ExportCaptureResult> Result { get; private set; }

            public Task ShowBusyAsync() => Record("busy", Task.CompletedTask);

            public Task ShowNothingToExportAsync(CaptureExportCounts counts) => Record("nothing", Task.CompletedTask, counts);

            public Task<bool> ConfirmAsync(string tabName, CaptureExportCounts counts) =>
                Record("confirm " + tabName, Task.FromResult(Confirm), counts);

            public Task<CaptureExportDestination> ChooseDestinationAsync(bool sanitizedOnly) =>
                Record(
                    sanitizedOnly ? "destination sanitized" : "destination",
                    Task.FromResult(Destinations.Count > 0 ? Destinations.Dequeue() : Destination));

            public Task<bool> ConfirmSanitizedAsync() => Record("consent", Task.FromResult(Consent));

            public Task<bool> ConfirmUnprotectedAsync(string path) =>
                Record("unprotected " + path, Task.FromResult(AllowUnprotected));

            public Task<OperationResult<ExportCaptureResult>> RunWithProgressAsync(
                Func<IProgress<OperationProgress>, CancellationToken, Task<OperationResult<ExportCaptureResult>>> export) =>
                Record("progress", export(null, CancellationToken.None));

            public Task ShowResultAsync(OperationResult<ExportCaptureResult> result)
            {
                Result = result;
                return Record(result.Succeeded ? "result" : "failure " + result.Diagnostics[0].Code, Task.CompletedTask);
            }

            private T Record<T>(string call, T answer, CaptureExportCounts counts = null)
            {
                Calls.Add(call);
                Counts = counts ?? Counts;
                return answer;
            }
        }

        // Records each request, and whether the lease was held, Stop allowed and the snapshot open while it ran. Each
        // queued failure answers one run; otherwise the export publishes, unprotected only where allowed.
        private sealed class Operation(CaptureAppModel app) : IOperation<ExportCaptureRequest, ExportCaptureResult>
        {
            internal List<ExportCaptureRequest> Requests { get; } = new();
            internal ExportCaptureRequest Request => Requests.Count == 0 ? null : Requests[^1];
            internal Queue<(OperationOutcome Outcome, string Code)> Failures { get; } = new();
            internal bool LeaseHeld { get; private set; }
            internal bool StopAllowed { get; private set; }
            internal bool SnapshotOpen { get; private set; }

            public Task<OperationResult<ExportCaptureResult>> ExecuteAsync(
                ExportCaptureRequest request, OperationContext context, CancellationToken cancellationToken)
            {
                Requests.Add(request);
                LeaseHeld = !app.TryBeginCaptureDataOperation(out IDisposable other);
                other?.Dispose();
                StopAllowed = app.CanStopCaptures;
                SnapshotOpen = ((Snapshot)request.Snapshot).Disposals == 0;
                bool failed = Failures.TryDequeue(out (OperationOutcome Outcome, string Code) failure);
                var value = new ExportCaptureResult(
                    request.Format, request.Selection, null, null, null, null, "records", null, failed ? null : 1,
                    failed ? null : "sha256", !failed, failed ? null : !request.AllowUnprotectedOutput);
                return Task.FromResult(failed
                    ? new OperationResult<ExportCaptureResult>(
                        failure.Outcome,
                        value,
                        new[] { new OperationDiagnostic(failure.Code, OperationDiagnosticSeverity.Error, "Operation text.") })
                    : OperationResult<ExportCaptureResult>.Success(value));
            }
        }

        private sealed class Snapshot(bool isLive, params CaptureExportSession[] sessions) : ICaptureExportSnapshot
        {
            internal List<ICaptureDeviceContext> Tabs { get; } = new();
            internal int Disposals { get; private set; }
            internal Exception DisposeFailure { get; init; }
            public IReadOnlyList<SqLtSchemaColumn> CaptureFields => Array.Empty<SqLtSchemaColumn>();
            public IReadOnlyList<SqLtSchemaColumn> DeviceFields => Array.Empty<SqLtSchemaColumn>();
            public IReadOnlyDictionary<string, RawExportRow> DeviceDescriptors { get; } = new Dictionary<string, RawExportRow>();
            public IReadOnlyList<CaptureExportSession> Sessions => sessions;
            public bool IsLive => isLive;
            public string InputKind => "records";
            public SqLtFileIdentity? InputIdentity => null;
            public string InputSha256 => null;

            public IReadOnlyList<RawExportRow> ReadChunk(IReadOnlyList<CaptureRowKey> keys, CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            public void ValidateDestination(string path)
            {
            }

            public void Dispose()
            {
                Disposals++;
                if (DisposeFailure != null)
                {
                    throw DisposeFailure;
                }
            }
        }

        private sealed class Probe(string name, IDeviceCaptureController controller) : ICaptureDeviceContext
        {
            public DeviceType DeviceType => DeviceType.LocalPC;
            public CaptureType CaptureType => CaptureType.WebProxy;
            public string DeviceName => name;
            public bool IsSelected { get; set; }
            public bool IsReadOnly { get; set; }
            public System.Drawing.Color DeviceColor { get; set; }
            public IDeviceCaptureController CaptureController => controller;
        }
    }
}
