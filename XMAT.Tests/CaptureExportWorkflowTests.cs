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
using static XMAT.Tests.Operations.Export.ExportTestData;

namespace XMAT.Tests
{
    // A GUI export holds the data-operation lease from its snapshot to its result, confirms what it will export (with
    // the counts) before it asks for a file, asks for consent before writing a sanitized archive, and always disposes its
    // snapshot and releases the lease. File > Export exports every session of the selected tab and never another tab's.
    public class CaptureExportWorkflowTests
    {
        // Three of four sessions are selected; the live capture has not recorded the third one's response yet.
        [Fact]
        public async Task Export_ConfirmsTheCountsAndRunsTheSharedOperationUnderTheLease()
        {
            var app = new CaptureAppModel();
            ICaptureDeviceContext tab = Tab(app, "Game PC");
            var snapshot = new Snapshot(
                true, Session(1), Session(2), Session(3, responseHeaders: false, responseTimestamp: false), Session(4));
            CaptureSelectionSpec selection = Selection(keys: ImmutableHashSet.Create(Key(1), Key(2), Key(3)));
            var dialogs = new Dialogs { Destination = new("subset.xmatcap", CaptureExportFormat.Xmatcap, true) };
            var operation = new Operation(app);

            CaptureExportWorkflowResult result = await Workflow(dialogs, snapshot, operation)
                .RunAsync(app, tab, CaptureExportScope.Selected, selection, sanitizedOnly: false);

            Assert.Equal(CaptureExportWorkflowResult.Exported, result);
            Assert.Equal(new[] { "confirm Selected Game PC", "destination", "progress", "result" }, dialogs.Calls);
            Assert.Equal(new CaptureExportCounts(4, 4, 3, 2, 0, 0, 1, 1), dialogs.Counts);
            ExportCaptureRequest request = operation.Request;
            Assert.Same(snapshot, request.Snapshot);
            Assert.Same(selection, request.Selection);
            Assert.Equal(
                ((string)null, CaptureExportFormat.Xmatcap, "subset.xmatcap", true, AnalysisLimits.Default),
                (request.InputPath, request.Format, request.OutputPath, request.ReplaceExisting, request.Limits));
            Assert.True(operation.LeaseHeld, "The export must hold the data-operation lease while it runs.");
            Assert.True(operation.SnapshotOpen, "The snapshot must stay open until the export completes.");
            AssertReleased(app, snapshot);
        }

        // Busy: nothing is snapshotted. Nothing eligible (a live session without a recorded response): no file is asked
        // for. Otherwise the export stops at the dialog that the user cancels.
        [Theory]
        [InlineData("busy", "Busy")]
        [InlineData("nothing", "NothingToExport")]
        [InlineData("confirm", "Cancelled")]
        [InlineData("destination", "Cancelled")]
        [InlineData("consent", "Cancelled")]
        public async Task Export_WritesNothingWhenBusyEmptyOrCancelled(string step, string expected)
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

            CaptureExportWorkflowResult result;
            using (busy)
            {
                result = await Workflow(dialogs, snapshot, operation)
                    .RunAsync(app, Tab(app, "Game PC"), CaptureExportScope.Visible, Selection(), sanitizedOnly: false);
            }

            Assert.Equal(Enum.Parse<CaptureExportWorkflowResult>(expected), result);
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

            CaptureExportWorkflowResult result = await Workflow(dialogs, snapshot, operation)
                .RunForSelectedTabAsync(app, sanitized);

            Assert.Equal(CaptureExportWorkflowResult.Exported, result);
            Assert.Same(selected, Assert.Single(snapshot.Tabs));
            Assert.Equal(
                sanitized
                    ? new[] { "confirm Tab Game PC", "destination sanitized", "consent", "progress", "result" }
                    : new[] { "confirm Tab Game PC", "destination", "progress", "result" },
                dialogs.Calls);
            CaptureSelectionSpec all = operation.Request.Selection;
            Assert.True(
                ProcessFilter.IsEmpty(all.Process) && all.Host.Include == null && all.Host.Exclude.IsDefaultOrEmpty &&
                    all.Statuses == null && all.Methods == null && all.Keys == null && !all.MatchNone,
                "File > Export ignores the capture list's filters.");
            AssertReleased(app, snapshot);
        }

        private static ICaptureDeviceContext Tab(CaptureAppModel app, string name)
        {
            var tab = new Probe(name);
            app.CaptureDeviceContexts.Add(tab);
            app.SelectedDeviceContext = tab;
            return tab;
        }

        private static CaptureExportWorkflow Workflow(Dialogs dialogs, Snapshot snapshot, Operation operation) =>
            new(dialogs.Owner, dialogs, tab =>
            {
                snapshot.Tabs.Add(tab);
                return snapshot;
            }, operation);

        // Each snapshot that the export took is disposed once, and the lease is free again.
        private static void AssertReleased(CaptureAppModel app, Snapshot snapshot)
        {
            Assert.Equal(snapshot.Tabs.Count, snapshot.Disposals);
            Assert.True(app.TryBeginCaptureDataOperation(out IDisposable next), "The export must release the lease.");
            next.Dispose();
        }

        // Records each dialog as "name [scope] [tab]" and answers as configured.
        private sealed class Dialogs : ICaptureExportDialogService
        {
            internal object Owner { get; } = new();
            internal List<string> Calls { get; } = new();
            internal bool Confirm { get; init; } = true;
            internal CaptureExportDestination Destination { get; init; } = new("subset.saz", CaptureExportFormat.Saz, false);
            internal bool Consent { get; init; } = true;
            internal CaptureExportCounts Counts { get; private set; }

            public Task ShowBusyAsync(object owner) => Record(owner, "busy", Task.CompletedTask);

            public Task ShowNothingToExportAsync(object owner, CaptureExportScope scope, CaptureExportCounts counts) =>
                Record(owner, $"nothing {scope}", Task.CompletedTask, counts);

            public Task<bool> ConfirmAsync(object owner, CaptureExportScope scope, string tabName, CaptureExportCounts counts) =>
                Record(owner, $"confirm {scope} {tabName}", Task.FromResult(Confirm), counts);

            public Task<CaptureExportDestination> ChooseDestinationAsync(object owner, bool sanitizedOnly) =>
                Record(owner, sanitizedOnly ? "destination sanitized" : "destination", Task.FromResult(Destination));

            public Task<bool> ConfirmSanitizedAsync(object owner) => Record(owner, "consent", Task.FromResult(Consent));

            public Task<OperationResult<ExportCaptureResult>> RunWithProgressAsync(
                object owner,
                Func<IProgress<OperationProgress>, CancellationToken, Task<OperationResult<ExportCaptureResult>>> export) =>
                Record(owner, "progress", export(null, CancellationToken.None));

            public Task ShowResultAsync(object owner, CaptureExportScope scope, OperationResult<ExportCaptureResult> result) =>
                Record(owner, result.Succeeded ? "result" : "failure", Task.CompletedTask);

            private T Record<T>(object owner, string call, T answer, CaptureExportCounts counts = null)
            {
                Assert.Same(Owner, owner);
                Calls.Add(call);
                Counts = counts ?? Counts;
                return answer;
            }
        }

        // Records the request, and whether the lease was held and the snapshot open while it ran.
        private sealed class Operation(CaptureAppModel app) : IOperation<ExportCaptureRequest, ExportCaptureResult>
        {
            internal ExportCaptureRequest Request { get; private set; }
            internal bool LeaseHeld { get; private set; }
            internal bool SnapshotOpen { get; private set; }

            public Task<OperationResult<ExportCaptureResult>> ExecuteAsync(
                ExportCaptureRequest request, OperationContext context, CancellationToken cancellationToken)
            {
                Request = request;
                LeaseHeld = !app.TryBeginCaptureDataOperation(out IDisposable other);
                other?.Dispose();
                SnapshotOpen = ((Snapshot)request.Snapshot).Disposals == 0;
                return Task.FromResult(OperationResult<ExportCaptureResult>.Success(new ExportCaptureResult(
                    request.Format, request.Selection, null, null, null, null, "records", null, 1, "sha256", true)));
            }
        }

        private sealed class Snapshot(bool isLive, params CaptureExportSession[] sessions) : ICaptureExportSnapshot
        {
            internal List<ICaptureDeviceContext> Tabs { get; } = new();
            internal int Disposals { get; private set; }
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

            public void Dispose() => Disposals++;
        }

        private sealed class Probe(string name) : ICaptureDeviceContext
        {
            public DeviceType DeviceType => DeviceType.LocalPC;
            public CaptureType CaptureType => CaptureType.WebProxy;
            public string DeviceName => name;
            public bool IsSelected { get; set; }
            public bool IsReadOnly { get; set; }
            public System.Drawing.Color DeviceColor { get; set; }
            public IDeviceCaptureController CaptureController => null;
        }
    }
}
