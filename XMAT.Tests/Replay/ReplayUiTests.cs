// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using XMAT.DataAbstractionLayer;
using XMAT.Models;
using XMAT.Operations;
using XMAT.Operations.Replay;
using XMAT.SharedInterfaces;
using XMAT.Tests.Fixtures;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Tests.Replay
{
    // The GUI's Replay menu, its Replays tab and the lifecycle of a run, with requests that target loopback servers only.
    // Each test records into its own application model, never the application's.
    [Collection(CaptureUiApplicationCollection.Name)]
    public class ReplayUiTests
    {
        private const string SourceTab = "Source tab";

        private readonly CaptureUiApplicationFixture _application;

        public ReplayUiTests(CaptureUiApplicationFixture application)
        {
            _application = application;
        }

        static ReplayUiTests()
        {
            Localization.LoadLanguage("en-us");
        }

        [Fact]
        public void RowMenu_ReplaysTheSelectionInListOrder_OrOnlyTheClickedRow()
        {
            _application.Run(window =>
            {
                var first = Capture(1, 80, "/one");
                var second = Capture(2, 80, "/two");
                var third = Capture(3, 80, "/three");
                CaptureConnectionView view = HostListUiTests.CaptureView(new HostListPolicy(), first, second, third);
                view.ReplayTarget = Target(new CaptureAppModel(Dispatcher.CurrentDispatcher), table: null);
                HostListUiTests.ShowCaptureView(window, view);
                var grid = (DataGrid)view.FindName("ConnectionList");
                grid.SelectedItems.Add(third);
                grid.SelectedItems.Add(first);
                WpfTestHost.Pump();

                // Right-clicking a selected row keeps the selection.
                DataGridRow selectedRow = HostListUiTests.Row(grid, third);
                RightClick(grid, selectedRow);
                Assert.Equal(new[] { first, third }, grid.SelectedItems.Cast<ProxyConnectionModel>().OrderBy(row => row.RequestNumber));
                ContextMenu menu = HostListUiTests.Open(selectedRow);
                MenuItem[] items = ReissueItems(menu);
                Assert.All(items, item => Assert.Equal(new[] { first, third }, Request(item).Captures));
                Assert.Equal(
                    new[] { "1:False:False", "1:True:False", "1:False:True", "2:False:False", "5:False:False", "10:False:False", "25:False:False", "100:False:False" },
                    items.Select(item => $"{Request(item).Repeat}:{Request(item).Unconditional}:{Request(item).Verify}"));
                Assert.All(items, item => Assert.Same(view, item.CommandTarget));
                // A closed submenu defers CanExecute, so ask the commands that its items route to the view.
                Assert.All(items, item => Assert.True(WebServiceProxyCommands.Reissue.CanExecute(item.CommandParameter, view)));
                MenuItem cancel = Assert.Single(Flatten(menu), item => item.Command == WebServiceProxyCommands.CancelReissue);
                Assert.Same(view, cancel.CommandTarget);
                Assert.False(WebServiceProxyCommands.CancelReissue.CanExecute(null, view));
                menu.IsOpen = false;
                WpfTestHost.Pump();

                // Right-clicking another row replays only that row.
                DataGridRow otherRow = HostListUiTests.Row(grid, second);
                RightClick(grid, otherRow);
                Assert.Same(second, Assert.Single(grid.SelectedItems));
                ContextMenu other = HostListUiTests.Open(otherRow);
                Assert.All(ReissueItems(other), item => Assert.Equal(new[] { second }, Request(item).Captures));
                other.IsOpen = false;
                WpfTestHost.Pump();
            });
        }

        // Each send becomes exactly one row of the tab and one row of the real capture table that analysis reads, with
        // the tab, number and table row of the request it replays; the captured rows do not change.
        [Fact]
        public Task Reissue_RecordsEachSendOnceInTheTableBackedReplaysTab_WithWhereItComesFrom() => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(request => LoopbackResponse.Ok("reply " + request.Target));
            using SqLtDatabase database = CreateDatabase(out IDataTable table);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var first = Capture(7, server.Port, "/one", responseBody: "reply /one"u8.ToArray());
            var second = Capture(9, server.Port, "/two", "If-None-Match: \"v1\"\r\n", "stale"u8.ToArray());
            first.AddToDataTable(SourceTab, table);
            second.AddToDataTable(SourceTab, table);
            string[] sourceRows = Rows(table, SourceTab);
            string[] sourceModels = new[] { first, second }.Select(Describe).ToArray();
            ICaptureDeviceContext replays = model.GetOrAddReplayContext();
            WebServiceDeviceCaptureController controller = UseTable(replays, table);
            try
            {
                ReplayRunResult run = await CaptureConnectionView.ReissueAsync(
                    ReplaySnapshot.Of(
                        new ReissueRequest(new[] { first, second }, Repeat: 2, Unconditional: true, Verify: true), SourceTab),
                    allowUnsafeMethods: false,
                    Target(model, table));

                Assert.True(run.Result.Succeeded);
                Assert.False(CaptureConnectionView.IsReissueRunning);
                Assert.True(model.CanModifyCaptureData);
                Assert.Same(replays, model.SelectedDeviceContext);
                Assert.Equal((0, 0), (run.OmittedBodies, run.NotRecorded));
                Assert.Equal(new long[] { 1, 2, 3, 4 }, controller.ProxyConnections.Select(row => row.RequestNumber));
                Assert.Equal(
                    new[] { "Source tab #7", "Source tab #9", "Source tab #7", "Source tab #9" },
                    controller.ProxyConnections.Select(row => row.ReplaySource));
                Assert.Equal(
                    new long?[] { first.RecordRowId, second.RecordRowId, first.RecordRowId, second.RecordRowId },
                    controller.ProxyConnections.Select(row => row.Replay.RecordRowId));
                Assert.All(controller.ProxyConnections, row => Assert.Equal(ReplaySourceKind.Reissued, row.Replay.Kind));

                // Once each in the table, under the tab's own name, where analysis reads the tab's rows.
                IDataRecord[] records = controller.GetCaptureRecords().OrderBy(record => record.RowId).ToArray();
                Assert.Equal(4, Rows(table, replays.DeviceName).Length);
                Assert.Equal(
                    controller.ProxyConnections.Select(row => row.RecordRowId).OrderBy(id => id),
                    records.Select(record => (long)record.RowId));
                var reloaded = new ProxyConnectionModel(records[0]);
                Assert.Equal(("200", "GET", "/one"), (reloaded.Status, reloaded.Method, reloaded.Path));
                Assert.StartsWith("GET /one HTTP/1.1\r\nHost: 127.0.0.1:", reloaded.RequestLineAndHeaders);
                Assert.Equal("reply /one"u8.ToArray(), reloaded.ResponseBody);

                Assert.All(server.Requests, request => Assert.Null(request.Header("If-None-Match")));
                Assert.Equal((2, 2), (run.Result.Value.Matched, run.Result.Value.Mismatched));
                string summary = CaptureConnectionView.VerifySummary(run.Result.Value);
                Assert.StartsWith("Replayed 4 requests: 2 matched the captured response, 2 differed and 0 got no complete response.", summary);
                Assert.Contains("Request #9: body", summary);
                Assert.DoesNotContain("Request #7", summary);

                // The captured rows, in memory and in the table, are as they were.
                Assert.Equal(sourceModels, new[] { first, second }.Select(Describe));
                Assert.Equal(sourceRows, Rows(table, SourceTab));
            }
            finally
            {
                await RemoveAllAsync(model);
            }
        });

        // The menu's sends traverse ReplayOperation's policy: without permission an unsafe method is refused before
        // anything is sent.
        [Fact]
        public Task Reissue_GoesThroughTheSharedReplayPolicy() => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                ReplayRunResult run = await CaptureConnectionView.ReissueAsync(
                    ReplaySnapshot.Of(new ReissueRequest(new[] { Capture(1, server.Port, "/item", method: "DELETE") }), SourceTab),
                    allowUnsafeMethods: false,
                    Target(model, table: null));

                Assert.Equal(OperationOutcome.InvalidInput, run.Result.Outcome);
                Assert.Equal("replay.policy.unsafe-method", Assert.Single(run.Result.Diagnostics).Code);
                Assert.Empty(server.Requests);
                Assert.Empty(ReplaysOf(model).ProxyConnections);
                Assert.True(model.CanModifyCaptureData);
            }
            finally
            {
                await RemoveAllAsync(model);
            }
        });

        // The confirmation states every send, repeats included; the rows are copied before it is shown, so the rows sent
        // are the rows confirmed even when a capture updates one meanwhile; a declined run sends nothing.
        [Fact]
        public Task Reissue_ConfirmsEverySend_AndSendsExactlyTheConfirmedRows() => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var post = Capture(1, server.Port, "/create", method: "POST");
            var get = Capture(2, server.Port, "/read");
            CaptureConnectionView view = HostListUiTests.CaptureView(new HostListPolicy(), post, get);
            view.ReplayTarget = Target(model, table: null);
            var messages = new List<string>();
            view.ShowReplayMessage = (message, _) => messages.Add(message);
            var confirmations = new List<string>();
            view.ConfirmReplay = message =>
            {
                confirmations.Add(message);
                return false;
            };
            try
            {
                Assert.Null(await view.ReissueFromMenuAsync(new ReissueRequest(new[] { post, get }, Repeat: 3)));
                Assert.Equal(
                    "This replay sends 6 requests: the 2 selected requests, 3 times each. 3 of these sends use a method " +
                        "that can change or delete server data (anything but GET, HEAD and OPTIONS). Cancelling later cannot " +
                        "undo server-side changes. Send them?",
                    Assert.Single(confirmations));
                Assert.Empty(server.Requests);
                Assert.Empty(model.CaptureDeviceContexts);
                Assert.StartsWith(
                    "This replay sends 2 requests, 1 of them with a method that can change or delete server data",
                    CaptureConnectionView.ConfirmationMessage(ReplaySnapshot.Of(new ReissueRequest(new[] { post, get }), SourceTab)));

                view.ConfirmReplay = message =>
                {
                    confirmations.Add(message);
                    post.UpdateFromDataRecord(new ChangedRow(99, new Dictionary<string, string>
                    {
                        [WebServiceCaptureMethod.FieldKey_RequestPath] = "/changed",
                        [WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders] =
                            $"POST /changed HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\n",
                    }));
                    return true;
                };
                ReplayRunResult run = await view.ReissueFromMenuAsync(new ReissueRequest(new[] { post, get }, Repeat: 3));

                Assert.Equal(2, confirmations.Count);
                Assert.True(run.Result.Succeeded);
                Assert.Equal("/changed", post.Path);
                Assert.Equal(
                    new[] { "POST /create", "GET /read", "POST /create", "GET /read", "POST /create", "GET /read" },
                    server.Requests.Select(request => request.Method + " " + request.Target));
                Assert.Equal(6, ReplaysOf(model).ProxyConnections.Count);
                Assert.Empty(messages);
            }
            finally
            {
                await RemoveAllAsync(model);
            }
        });

        // The tab keeps at most its budget of bodies, counting the rows it already has, across runs. A send whose body does
        // not fit still gets its row, with its status and headers and a note, and the user is told how many bodies were
        // not kept.
        [Fact]
        public Task ReplaysTab_KeepsBodiesWithinItsBudgetAcrossRuns_AndRecordsEverySend() => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok("0123456789"));
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var source = Capture(5, server.Port, "/data");
            CaptureConnectionView view = HostListUiTests.CaptureView(new HostListPolicy(), source);
            view.ReplayTarget = Target(model, table: null, bodyBudgetBytes: 25);
            var messages = new List<(string Text, MessageBoxImage Image)>();
            view.ShowReplayMessage = (text, image) => messages.Add((text, image));
            WebServiceDeviceCaptureController replays = ReplaysOf(model);
            replays.ProxyConnections.Add(Capture(1, server.Port, "/earlier", responseBody: "0123456789"u8.ToArray()));
            try
            {
                ReplayRunResult first = await view.ReissueFromMenuAsync(new ReissueRequest(new[] { source }, Repeat: 2));

                Assert.Equal((2, 1), (first.Result.Value.Items.Count, first.OmittedBodies));
                ProxyConnectionModel kept = replays.ProxyConnections[1];
                ProxyConnectionModel trimmed = replays.ProxyConnections[2];
                Assert.Equal((2L, 3L), (kept.RequestNumber, trimmed.RequestNumber));
                Assert.Equal("0123456789"u8.ToArray(), kept.ResponseBody);
                Assert.False(kept.HasReplayNote);
                Assert.False(kept.ResponseHeaders.ContainsKey(RequestReplayDraft.ResponseBodyNotKeptHeader));
                Assert.Empty(trimmed.ResponseBody);
                Assert.Equal("200", trimmed.Status);
                Assert.StartsWith("HTTP/1.1 200 OK\r\n", trimmed.ResponseLineAndHeaders);
                Assert.Equal("10", trimmed.ResponseHeaders[RequestReplayDraft.ResponseBodyNotKeptHeader]);
                Assert.Equal(Localization.GetLocalizedString("REPLAY_NOTE_RESPONSE_BODY", 10L, 0L), trimmed.ReplayNote);
                Assert.DoesNotContain("NO_IMPL_", trimmed.ReplayNote);
                (string text, MessageBoxImage image) = Assert.Single(messages);
                Assert.StartsWith(
                    "1 request or response bodies were not kept: the XMAT Replays tab keeps at most 0 MiB of request and " +
                        "response bodies, a limit on the bodies it keeps, not on XMAT's memory use.",
                    text);
                Assert.Equal(MessageBoxImage.Warning, image);

                ReplayRunResult second = await view.ReissueFromMenuAsync(new ReissueRequest(new[] { source }));

                Assert.Equal(1, second.OmittedBodies);
                Assert.Equal(4, replays.ProxyConnections.Count);
                Assert.True(replays.ProxyConnections[3].HasReplayNote);
                Assert.Equal(20, RetainedBodyBytes(replays));
            }
            finally
            {
                await RemoveAllAsync(model);
            }
        });

        // A run holds the capture-data operation, so loads, imports, saves, analysis, other replays and closing tabs
        // cannot start while it adds rows; closing XMAT cancels the run and returns only once the run has ended.
        [Fact]
        public Task Reissue_BlocksCaptureDataOperations_AndClosingXmatCancelsAndWaitsForIt() => _application.RunAsync(async () =>
        {
            var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var server = new LoopbackHttpServer(_ =>
            {
                arrived.TrySetResult();
                return new LoopbackResponse(Hang: true);
            });
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                Task<ReplayRunResult> running = CaptureConnectionView.ReissueAsync(
                    ReplaySnapshot.Of(new ReissueRequest(new[] { Capture(1, server.Port, "/slow") }), SourceTab),
                    allowUnsafeMethods: false,
                    Target(model, table: null));
                await arrived.Task.WaitAsync(TimeSpan.FromSeconds(30));

                Assert.False(model.CanModifyCaptureData);
                Assert.False(model.CanAnalyzeCaptures);
                Assert.False(model.TryBeginCaptureDataOperation(out _));
                ReplayRunResult other = await CaptureConnectionView.ReissueAsync(
                    ReplaySnapshot.Of(new ReissueRequest(new[] { Capture(2, server.Port, "/other") }), SourceTab),
                    allowUnsafeMethods: false,
                    Target(model, table: null));
                Assert.True(other.WasBusy);

                Assert.True(await model.CanCloseAsync().WaitAsync(TimeSpan.FromSeconds(30)));

                Assert.True(running.IsCompleted);
                ReplayRunResult run = await running;
                Assert.Equal(OperationOutcome.Cancelled, run.Result.Outcome);
                Assert.False(run.CancelledByUser);
                Assert.False(CaptureConnectionView.IsReissueRunning);
                Assert.True(model.CanModifyCaptureData);
                Assert.Equal(new[] { "/slow" }, server.Requests.Select(request => request.Target));
            }
            finally
            {
                await RemoveAllAsync(model);
            }
        });

        // If something bypasses the run's capture-data operation and closes the Replays tab on the UI thread mid-run, no
        // row is added to the closed tab or its table afterwards: the send that finds it closed is counted and the run
        // stops.
        [Fact]
        public Task ClosingTheReplaysTabDuringARun_StopsTheRun_AndAddsNoRowAfterwards() => _application.RunAsync(async () =>
        {
            var secondArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var server = new LoopbackHttpServer(async request =>
            {
                if (request.Target == "/two")
                {
                    secondArrived.TrySetResult();
                    await release.Task;
                }

                return LoopbackResponse.Ok("reply");
            });
            using SqLtDatabase database = CreateDatabase(out IDataTable table);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            ICaptureDeviceContext replays = model.GetOrAddReplayContext();
            WebServiceDeviceCaptureController controller = UseTable(replays, table);
            try
            {
                Task<ReplayRunResult> running = CaptureConnectionView.ReissueAsync(
                    ReplaySnapshot.Of(
                        new ReissueRequest(new[]
                        {
                            Capture(1, server.Port, "/one"), Capture(2, server.Port, "/two"), Capture(3, server.Port, "/three"),
                        }),
                        SourceTab),
                    allowUnsafeMethods: false,
                    Target(model, table));
                await secondArrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.Single(controller.ProxyConnections);
                Assert.Single(Rows(table, replays.DeviceName));

                await model.RemoveDeviceContextAsync(replays);
                release.TrySetResult();
                ReplayRunResult run = await running.WaitAsync(TimeSpan.FromSeconds(30));

                Assert.Equal(OperationOutcome.Cancelled, run.Result.Outcome);
                Assert.Equal(1, run.NotRecorded);
                Assert.Equal(new[] { "/one", "/two" }, server.Requests.Select(request => request.Target));
                Assert.Empty(controller.ProxyConnections);
                Assert.Empty(Rows(table, replays.DeviceName));
                Assert.True(model.CanModifyCaptureData);
                Assert.NotSame(replays, model.GetOrAddReplayContext());
            }
            finally
            {
                release.TrySetResult();
                await RemoveAllAsync(model);
            }
        });

        [Fact]
        public Task CancelReplay_StopsTheRunningReplay() => _application.RunAsync(async () =>
        {
            var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var server = new LoopbackHttpServer(_ =>
            {
                arrived.TrySetResult();
                return new LoopbackResponse(Hang: true);
            });
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            CaptureConnectionView view = HostListUiTests.CaptureView(new HostListPolicy());
            view.ReplayTarget = Target(model, table: null);
            try
            {
                Task<ReplayRunResult> running = CaptureConnectionView.ReissueAsync(
                    ReplaySnapshot.Of(new ReissueRequest(new[] { Capture(1, server.Port, "/slow") }), SourceTab),
                    allowUnsafeMethods: false,
                    view.ReplayTarget);
                await arrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.False(WebServiceProxyCommands.Reissue.CanExecute(new ReissueRequest(new[] { Capture(2, 80, "/") }), view));
                Assert.True(WebServiceProxyCommands.CancelReissue.CanExecute(null, view));
                WebServiceProxyCommands.CancelReissue.Execute(null, view);

                ReplayRunResult run = await running.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.Equal(OperationOutcome.Cancelled, run.Result.Outcome);
                Assert.True(run.CancelledByUser);
                Assert.False(CaptureConnectionView.IsReissueRunning);
                Assert.Empty(ReplaysOf(model).ProxyConnections);
                Assert.True(model.CanModifyCaptureData);
            }
            finally
            {
                await RemoveAllAsync(model);
            }
        });

        // Cancelling does not unsend: the request in flight, which the server here has received and holds the response of,
        // may have reached the server, so the user is warned; it gets no row, and the rows recorded before it stay, in the
        // tab and in the table.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public Task CancelReplay_WarnsThatTheRequestInFlightMayHaveArrived_AndKeepsTheRowsBeforeIt(bool earlierSendCompleted) => _application.RunAsync(async () =>
        {
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var server = new LoopbackHttpServer(async request =>
            {
                if (request.Target == "/held")
                {
                    held.TrySetResult();
                    await release.Task;
                }

                return LoopbackResponse.Ok("reply");
            });
            using SqLtDatabase database = CreateDatabase(out IDataTable table);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            ICaptureDeviceContext replays = model.GetOrAddReplayContext();
            WebServiceDeviceCaptureController controller = UseTable(replays, table);
            ProxyConnectionModel[] captures = earlierSendCompleted
                ? new[] { Capture(1, server.Port, "/done"), Capture(2, server.Port, "/held"), Capture(3, server.Port, "/after") }
                : new[] { Capture(2, server.Port, "/held") };
            CaptureConnectionView view = HostListUiTests.CaptureView(new HostListPolicy(), captures);
            view.ReplayTarget = Target(model, table);
            var messages = new List<(string Text, MessageBoxImage Image)>();
            view.ShowReplayMessage = (text, image) => messages.Add((text, image));
            try
            {
                Task<ReplayRunResult> running = view.ReissueFromMenuAsync(new ReissueRequest(captures));
                await held.Task.WaitAsync(TimeSpan.FromSeconds(30));
                WebServiceProxyCommands.CancelReissue.Execute(null, view);
                ReplayRunResult run = await running.WaitAsync(TimeSpan.FromSeconds(30));

                string[] kept = earlierSendCompleted ? new[] { "/done" } : Array.Empty<string>();
                Assert.Equal(OperationOutcome.Cancelled, run.Result.Outcome);
                Assert.True(run.CancelledByUser);
                Assert.Equal(kept, controller.ProxyConnections.Select(row => row.Path));
                Assert.Equal(kept, Paths(table, replays.DeviceName));
                Assert.Equal(kept, PathsOf(controller.GetCaptureRecords()));
                Assert.DoesNotContain(server.Requests, request => request.Target == "/after");
                (string text, MessageBoxImage image) = Assert.Single(messages);
                Assert.Equal(
                    Localization.GetLocalizedString("REPLAY_CANCELLED", kept.Length, captures.Length, replays.DeviceName),
                    text);
                Assert.Contains("may already have reached the server; it is not recorded", text);
                Assert.Equal(MessageBoxImage.Warning, image);
            }
            finally
            {
                release.TrySetResult();
                await RemoveAllAsync(model);
            }
        });

        // A body that the tab does not keep leaves, besides the row's note, a header with its size in the recorded head, so
        // the row never passes for one with an empty body; and a request recorded without its body is never sent again,
        // neither reissued nor edited and resent.
        [Fact]
        public Task ReplaysTab_MarksTheBodiesItDidNotKeep_AndNeverResendsARequestWithoutItsBody() => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok("0123456789"));
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                // 6 bytes of bodies: the first send keeps its request body only, the second neither body.
                ReplayRunResult run = await CaptureConnectionView.ReissueAsync(
                    ReplaySnapshot.Of(new ReissueRequest(new[] { Upload(server.Port) }, Repeat: 2), SourceTab),
                    allowUnsafeMethods: true,
                    Target(model, table: null, bodyBudgetBytes: 6));

                Assert.Equal(3, run.OmittedBodies);
                WebServiceDeviceCaptureController replays = ReplaysOf(model);
                ProxyConnectionModel first = replays.ProxyConnections[0];
                ProxyConnectionModel second = replays.ProxyConnections[1];
                Assert.Equal("abcdef"u8.ToArray(), first.RequestBody);
                Assert.False(first.RequestHeaders.ContainsKey(RequestReplayDraft.RequestBodyNotKeptHeader));
                Assert.Empty(first.ResponseBody);
                Assert.Equal("10", first.ResponseHeaders[RequestReplayDraft.ResponseBodyNotKeptHeader]);
                Assert.Empty(second.RequestBody);
                Assert.EndsWith(RequestReplayDraft.RequestBodyNotKeptHeader + ": 6\r\n\r\n", second.RequestLineAndHeaders);
                Assert.Equal("10", second.ResponseHeaders[RequestReplayDraft.ResponseBodyNotKeptHeader]);
                Assert.Equal(Localization.GetLocalizedString("REPLAY_NOTE_BODIES", 6L, 10L, 0L), second.ReplayNote);

                CaptureConnectionView view = HostListUiTests.CaptureView(new HostListPolicy(), first, second);
                view.ReplayTarget = Target(model, table: null);
                Assert.True(WebServiceProxyCommands.EditAndResend.CanExecute(first, view));
                Assert.False(WebServiceProxyCommands.EditAndResend.CanExecute(second, view));
                Assert.False(WebServiceProxyCommands.Reissue.CanExecute(new ReissueRequest(new[] { second }), view));
                Assert.Equal(
                    new[] { first.RequestNumber },
                    ReplaySnapshot.Of(new ReissueRequest(new[] { first, second }), SourceTab).Captures.Select(row => row.RequestNumber));
                Assert.Null(await view.ReissueFromMenuAsync(new ReissueRequest(new[] { second })));
                Assert.Equal(2, server.Requests.Count);
            }
            finally
            {
                await RemoveAllAsync(model);
            }
        });

        // File > Save keeps the tab: the saved capture opens it as an ordinary read-only tab with the same rows, including the
        // headers that mark the bodies not kept, but without where they come from, and new replays do not go there. A
        // Fiddler export of the capture table keeps the marks too.
        [Fact]
        public Task ReplaysTab_SavedOrExported_KeepsItsRowsAndTheMarksOfBodiesNotKept() => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok("0123456789"));
            string directory = FixtureDirectory();
            using SqLtDatabase database = CreateDatabase(out IDataTable table, out IDataTable devices);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher) { ActiveDatabase = database, CaptureDevicesTable = devices };
            var reopened = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            ICaptureDeviceContext replays = model.GetOrAddReplayContext();
            WebServiceDeviceCaptureController controller = UseTable(replays, table);
            try
            {
                await CaptureConnectionView.ReissueAsync(
                    ReplaySnapshot.Of(new ReissueRequest(new[] { Upload(server.Port) }, Repeat: 2), SourceTab),
                    allowUnsafeMethods: true,
                    Target(model, table, bodyBudgetBytes: 6));
                string[] recorded = controller.ProxyConnections.Select(Stored).ToArray();
                Assert.Equal(2, recorded.Length);

                string saved = Path.Join(directory, "replays.xmatcap");
                model.SaveDataCaptures(saved);
                reopened.LoadDataCaptures(saved);

                ICaptureDeviceContext loaded = Assert.Single(reopened.CaptureDeviceContexts);
                Assert.Equal((CaptureAppModel.ReplayContextName, true), (loaded.DeviceName, loaded.IsReadOnly));
                ProxyConnectionsCollection loadedRows = ((WebServiceDeviceCaptureController)loaded.CaptureController).ProxyConnections;
                Assert.Equal(recorded, loadedRows.Select(Stored));
                Assert.All(loadedRows, row => Assert.False(row.IsReplay));
                Assert.Equal("6", loadedRows[1].RequestHeaders[RequestReplayDraft.RequestBodyNotKeptHeader]);
                Assert.False(RequestReplayDraft.CanReplay(loadedRows[1]));
                ICaptureDeviceContext next = reopened.GetOrAddReplayContext();
                Assert.NotSame(loaded, next);
                Assert.Equal(CaptureAppModel.ReplayContextName + " (2)", next.DeviceName);

                // What File > Export does with the capture table.
                string exported = Path.Join(directory, "replays.saz");
                FiddlerSazHandler.CreateSazFile(exported, table.Dataset.DataRecords.Select(record => new ProxyConnectionModel(record)));
                ProxyConnectionModel[] imported = FiddlerSazHandler.ImportSazFile(exported, CancellationToken.None).ToArray();
                Assert.Equal(2, imported.Length);
                Assert.Equal("abcdef"u8.ToArray(), imported[0].RequestBody);
                Assert.Empty(imported[0].ResponseBody);
                Assert.Equal("10", imported[0].ResponseHeaders[RequestReplayDraft.ResponseBodyNotKeptHeader]);
                Assert.Empty(imported[1].RequestBody);
                Assert.Equal("6", imported[1].RequestHeaders[RequestReplayDraft.RequestBodyNotKeptHeader]);
                Assert.Equal("10", imported[1].ResponseHeaders[RequestReplayDraft.ResponseBodyNotKeptHeader]);

                // Closing the tab drops it from the capture devices: a capture saved afterward has no empty Replays tab.
                await model.RemoveDeviceContextAsync(replays);
                Assert.Empty(devices.Dataset.DataRecords);
            }
            finally
            {
                await RemoveAllAsync(reopened);
                await RemoveAllAsync(model);
                TestFolderCleanup.Delete(directory);
            }
        });

        // The tab's name is one that no capture can have: a console named Replays, the tab's former name, added before or
        // after the tab, keeps its name and its rows apart from the tab's, in analysis and in a saved capture; and no tab can
        // be added with the tab's name.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public Task ReplaysTab_NeverSharesRowsWithACaptureNamedReplays_InEitherOrder(bool consoleFirst) => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok("reply"));
            string directory = FixtureDirectory();
            using SqLtDatabase database = CreateDatabase(out IDataTable table, out IDataTable devices);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher) { ActiveDatabase = database, CaptureDevicesTable = devices };
            var reopened = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                ICaptureDeviceContext console = consoleFirst ? AddConsole(model, "Replays", table) : null;
                ICaptureDeviceContext replays = model.GetOrAddReplayContext();
                WebServiceDeviceCaptureController replayController = UseTable(replays, table);
                console ??= AddConsole(model, "Replays", table);
                var consoleController = (WebServiceDeviceCaptureController)console.CaptureController;
                // As the console's capture adds its rows.
                Capture(1, server.Port, "/captured").AddToDataTable(console.DeviceName, table);
                await CaptureConnectionView.ReissueAsync(
                    ReplaySnapshot.Of(new ReissueRequest(new[] { Capture(7, server.Port, "/replayed") }), SourceTab),
                    allowUnsafeMethods: false,
                    Target(model, table));

                Assert.Equal(("Replays", CaptureAppModel.ReplayContextName), (console.DeviceName, replays.DeviceName));
                Assert.Equal(new[] { "/captured" }, PathsOf(consoleController.GetCaptureRecords()));
                Assert.Equal(new[] { "/replayed" }, PathsOf(replayController.GetCaptureRecords()));
                Assert.Throws<ArgumentException>(() => model.AddDeviceContext(
                    DeviceType.XboxConsole, CaptureAppModel.ReplayContextName, CaptureType.WebProxy, false, null, deferPortAllocation: true));
                Assert.Throws<ArgumentException>(() => model.AddDeviceContext(
                    DeviceType.Unknown, CaptureAppModel.ReplayContextName + " (2)", CaptureType.WebProxy, true, null));
                Assert.Equal(2, model.CaptureDeviceContexts.Count);

                string saved = Path.Join(directory, "capture.xmatcap");
                model.SaveDataCaptures(saved);
                reopened.LoadDataCaptures(saved);

                Assert.Equal(new[] { "/captured" }, LoadedPaths(reopened, "Replays"));
                Assert.Equal(new[] { "/replayed" }, LoadedPaths(reopened, CaptureAppModel.ReplayContextName));
            }
            finally
            {
                await RemoveAllAsync(reopened);
                await RemoveAllAsync(model);
                TestFolderCleanup.Delete(directory);
            }
        });

        // The replay feature keeps its tab by reference, and name lookups (loads, imports, new captures) never find it. Once
        // the tab is gone from the tabs, closed or merely removed, the next run records into a new tab and never into the old
        // one, and the capture table holds only the new tab's rows under its name.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public Task ReplaysTab_OnceGoneFromTheTabs_IsNeverWrittenAgain(bool closed) => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok("reply"));
            using SqLtDatabase database = CreateDatabase(out IDataTable table, out IDataTable devices);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher) { CaptureDevicesTable = devices };
            ICaptureDeviceContext gone = model.GetOrAddReplayContext();
            WebServiceDeviceCaptureController goneController = UseTable(gone, table);
            try
            {
                await CaptureConnectionView.ReissueAsync(
                    ReplaySnapshot.Of(new ReissueRequest(new[] { Capture(1, server.Port, "/one") }), SourceTab),
                    allowUnsafeMethods: false,
                    Target(model, table));
                Assert.Equal(new[] { "/one" }, goneController.ProxyConnections.Select(row => row.Path));
                Assert.Same(gone, model.GetOrAddReplayContext());
                Assert.Null(model.GetDeviceContext(DeviceType.Unknown, gone.DeviceName, CaptureType.WebProxy, true));
                if (closed)
                {
                    await model.RemoveDeviceContextAsync(gone);
                }
                else
                {
                    // Not through RemoveDeviceContextAsync, so the tab's rows are still in the capture table.
                    Assert.True(model.CaptureDeviceContexts.Remove(gone));
                }

                ReplayRunResult run = await CaptureConnectionView.ReissueAsync(
                    ReplaySnapshot.Of(new ReissueRequest(new[] { Capture(2, server.Port, "/two") }), SourceTab),
                    allowUnsafeMethods: false,
                    Target(model, table));

                Assert.True(run.Result.Succeeded);
                ICaptureDeviceContext replays = Assert.Single(model.CaptureDeviceContexts);
                Assert.NotSame(gone, replays);
                Assert.Same(replays, model.SelectedDeviceContext);
                Assert.Equal(CaptureAppModel.ReplayContextName, replays.DeviceName);
                Assert.Equal(
                    new[] { "/two" },
                    ((WebServiceDeviceCaptureController)replays.CaptureController).ProxyConnections.Select(row => row.Path));
                Assert.DoesNotContain(goneController.ProxyConnections, row => row.Path == "/two");
                Assert.Equal(new[] { "/two" }, Paths(table, replays.DeviceName));
                // Listed once with the capture devices, so that saving keeps it, and the old tab no longer.
                Assert.Equal(
                    CaptureAppModel.ReplayContextName,
                    Assert.Single(devices.Dataset.DataRecords).Str("DeviceName"));
            }
            finally
            {
                await RemoveAllAsync(model);
            }
        });

        // Only exactly GET, HEAD and OPTIONS are safe, as for ReplayOperation's policy: a lowercase or a custom method is
        // counted and confirmed like any other method that can change server data.
        [Fact]
        public Task Confirmation_CountsLowercaseAndCustomMethodsAsUnsafe() => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            ProxyConnectionModel[] rows =
            {
                Capture(1, server.Port, "/a"),
                Capture(2, server.Port, "/b", method: "get"),
                Capture(3, server.Port, "/c", method: "PURGE"),
                Capture(4, server.Port, "/d", method: "HEAD"),
                Capture(5, server.Port, "/e", method: "OPTIONS"),
            };
            CaptureConnectionView view = HostListUiTests.CaptureView(new HostListPolicy(), rows);
            view.ReplayTarget = Target(model, table: null);
            var confirmations = new List<string>();
            view.ConfirmReplay = message =>
            {
                confirmations.Add(message);
                return false;
            };
            try
            {
                ReplaySnapshot snapshot = ReplaySnapshot.Of(new ReissueRequest(rows, Repeat: 3), SourceTab);
                Assert.Equal((2, 15L, 6L), (snapshot.UnsafeRequests, snapshot.Sends, snapshot.UnsafeSends));

                Assert.Null(await view.ReissueFromMenuAsync(new ReissueRequest(rows, Repeat: 3)));
                Assert.Null(await view.ReissueFromMenuAsync(new ReissueRequest(new[] { rows[1] })));

                Assert.Equal(
                    new[]
                    {
                        Localization.GetLocalizedString("REPLAY_CONFIRM_UNSAFE_REPEATED", 15, 5, 3, 6),
                        Localization.GetLocalizedString("REPLAY_CONFIRM_UNSAFE", 1, 1),
                    },
                    confirmations);
                Assert.Empty(server.Requests);
                Assert.Empty(model.CaptureDeviceContexts);
            }
            finally
            {
                await RemoveAllAsync(model);
            }
        });

        // Windows ending the session (SessionEnding) and Application.Shutdown (Exit, which then tears down the database) cancel
        // a running replay on the UI thread without waiting for it: the request in flight is cancelled, nothing more is sent
        // or added, and no replay starts afterward.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public Task Shutdown_CancelsARunningReplayWithoutWaiting_AndNothingMoreIsSentOrAdded(bool exit) => _application.RunAsync(async () =>
        {
            var secondArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var server = new LoopbackHttpServer(async request =>
            {
                if (request.Target == "/two")
                {
                    secondArrived.TrySetResult();
                    await release.Task;
                }

                return LoopbackResponse.Ok("reply");
            });
            using SqLtDatabase database = CreateDatabase(out IDataTable table);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            ICaptureDeviceContext replays = model.GetOrAddReplayContext();
            WebServiceDeviceCaptureController controller = UseTable(replays, table);
            ReplayRecordingTarget target = Target(model, table);
            try
            {
                Task<ReplayRunResult> running = CaptureConnectionView.ReissueAsync(
                    ReplaySnapshot.Of(
                        new ReissueRequest(new[]
                        {
                            Capture(1, server.Port, "/one"), Capture(2, server.Port, "/two"), Capture(3, server.Port, "/three"),
                        }),
                        SourceTab),
                    allowUnsafeMethods: false,
                    target);
                await secondArrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.Single(controller.ProxyConnections);

                model.BeginShutdown();
                // It returned without waiting: the run can only end once this thread is free.
                Assert.False(running.IsCompleted);
                if (exit)
                {
                    UseTable(replays, null);
                    database.Shutdown();
                }

                ReplayRunResult run = await running.WaitAsync(TimeSpan.FromSeconds(30));

                Assert.Equal(OperationOutcome.Cancelled, run.Result.Outcome);
                Assert.Equal(new[] { "/one", "/two" }, server.Requests.Select(request => request.Target));
                Assert.Equal(new[] { "/one" }, controller.ProxyConnections.Select(row => row.Path));
                if (!exit)
                {
                    Assert.Equal(new[] { "/one" }, Paths(table, replays.DeviceName));
                }

                Assert.True(model.IsShuttingDown);
                Assert.True(model.CanModifyCaptureData);
                Assert.False(target.CanBegin);
                using var cancellation = new CancellationTokenSource();
                Assert.Null(target.TryBegin(cancellation, _ => null));
                ReplayRunResult later = await CaptureConnectionView.ReissueAsync(
                    ReplaySnapshot.Of(new ReissueRequest(new[] { Capture(4, server.Port, "/four") }), SourceTab),
                    allowUnsafeMethods: false,
                    target);
                Assert.True(later.WasBusy);
                Assert.Equal(new[] { "/one", "/two" }, server.Requests.Select(request => request.Target));
            }
            finally
            {
                release.TrySetResult();
                await RemoveAllAsync(model);
            }
        });

        // A row that reaches the UI thread only after shutdown began, because that thread was busy in SessionEnding or Exit
        // while the send waited for it, is not added. A send that comes after shutdown began does not wait for the UI thread
        // at all, which stays blocked here until the send returns. Either send is counted as not recorded and stops its run.
        [Fact]
        public Task Shutdown_AddsNoRowThatArrivesLate_AndNoSendWaitsForTheUiThread() => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok("reply"));
            using SqLtDatabase database = CreateDatabase(out IDataTable table);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var later = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            ICaptureDeviceContext replays = model.GetOrAddReplayContext();
            WebServiceDeviceCaptureController controller = UseTable(replays, table);
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            using var rowReady = new ManualResetEventSlim();
            var target = new ReplayRecordingTarget(
                model,
                action =>
                {
                    rowReady.Set();
                    dispatcher.Invoke(action);
                },
                _ => table);
            try
            {
                Task<ReplayRunResult> running = CaptureConnectionView.ReissueAsync(
                    ReplaySnapshot.Of(
                        new ReissueRequest(new[] { Capture(1, server.Port, "/one"), Capture(2, server.Port, "/two") }),
                        SourceTab),
                    allowUnsafeMethods: false,
                    target);
                Assert.True(rowReady.Wait(TimeSpan.FromSeconds(30)));
                model.BeginShutdown();
                ReplayRunResult run = await running.WaitAsync(TimeSpan.FromSeconds(30));

                Assert.Equal(OperationOutcome.Cancelled, run.Result.Outcome);
                Assert.Equal((0, 1), (run.Recorded, run.NotRecorded));
                Assert.Equal(new[] { "/one" }, server.Requests.Select(request => request.Target));
                Assert.Empty(controller.ProxyConnections);
                Assert.Empty(Rows(table, replays.DeviceName));

                using var cancellation = new CancellationTokenSource();
                using ReplayRecording recording = Target(later, table).TryBegin(cancellation, _ => ReplayProvenance.Composed);
                later.BeginShutdown();
                Assert.True(cancellation.IsCancellationRequested);
                Task send = Task.Run(() => recording.Recorder.Add(Sent(), Answered()));
                Assert.True(send.Wait(TimeSpan.FromSeconds(30)));
                Assert.Equal((0, 1), (recording.Recorder.Recorded, recording.Recorder.NotRecorded));
                Assert.Empty(ReplaysOf(later).ProxyConnections);
                Assert.Empty(Rows(table, recording.Destination.DeviceName));
            }
            finally
            {
                await RemoveAllAsync(model);
                await RemoveAllAsync(later);
            }
        });

        // Once its dispatcher has shut down, as XMAT's has when it exits, the application target's dispatch runs nothing and
        // returns at once, and the recorder counts the send as not recorded and stops the run.
        [Fact]
        public void UiDispatch_RunsNothingOnceItsDispatcherHasShutDown()
        {
            Action<Action> dispatch = CaptureConnectionView.UiDispatch(StoppedDispatcher());
            bool ran = false;
            dispatch(() => ran = true);
            Assert.False(ran);

            var controller = new WebServiceDeviceCaptureController(
                CaptureAppModel.ReplayContextName, DeviceType.Unknown, true, new ProxyPortPool());
            bool runStopped = false;
            var recorder = new ReplayRecorder(controller, null, dispatch, _ => ReplayProvenance.Composed, stop: () => runStopped = true);
            recorder.Add(Sent(), Answered());

            Assert.Equal((0, 1, true), (recorder.Recorded, recorder.NotRecorded, runStopped));
            Assert.Empty(controller.ProxyConnections);
        }

        // The confirmed rows are copies that share the source row's bodies and header dictionaries, which an update replaces
        // and never changes in place. So when the capture updates the source row after the user confirmed, through the update
        // that live captures use, the replay still sends, and verifies against, what was confirmed. Here the confirmed request
        // and response are chunked and the updated ones are not, and both dictionaries were read before the update.
        [Fact]
        public Task Reissue_SendsAndVerifiesWhatWasConfirmed_WhenTheCaptureUpdatesTheRowAfterward() => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok("confirmed response"));
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            ProxyConnectionModel source = Capture(
                3,
                server.Port,
                "/save",
                "Transfer-Encoding: chunked\r\nX-Version: 1\r\n",
                responseBody: "12\r\nconfirmed response\r\n0\r\n\r\n"u8.ToArray(),
                method: "POST",
                requestBody: "e\r\nconfirmed body\r\n0\r\n\r\n"u8.ToArray(),
                responseHeaders: "Transfer-Encoding: chunked\r\n");
            Assert.Equal(("chunked", "chunked"), (source.RequestHeaders["Transfer-Encoding"], source.ResponseHeaders["Transfer-Encoding"]));
            Assert.True(source.HeaderCollectionsMaterialized);
            CaptureConnectionView view = HostListUiTests.CaptureView(new HostListPolicy(), source);
            view.ReplayTarget = Target(model, table: null);
            var messages = new List<string>();
            view.ShowReplayMessage = (message, _) => messages.Add(message);
            view.ConfirmReplay = _ =>
            {
                source.UpdateFromDataRecord(new ChangedRow(99, new Dictionary<string, string>
                {
                    [WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders] =
                        $"POST /save HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\nX-Version: 2\r\n\r\n",
                    [WebServiceCaptureMethod.FieldKey_RequestBody] = Convert.ToBase64String("updated body"u8.ToArray()),
                    [WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders] = "HTTP/1.1 200 OK\r\n\r\n",
                    [WebServiceCaptureMethod.FieldKey_ResponseBody] = Convert.ToBase64String("updated response"u8.ToArray()),
                }));
                return true;
            };
            try
            {
                ReplayRunResult run = await view.ReissueFromMenuAsync(new ReissueRequest(new[] { source }, Verify: true));

                LoopbackRequest sent = Assert.Single(server.Requests);
                Assert.Equal("1", sent.Header("X-Version"));
                Assert.Equal("confirmed body"u8.ToArray(), sent.Body);
                Assert.Equal((1, 0), (run.Result.Value.Matched, run.Result.Value.Mismatched));
                Assert.StartsWith("Replayed 1 requests: 1 matched the captured response", Assert.Single(messages));
                Assert.Equal("2", source.RequestHeaders["X-Version"]);
                Assert.False(source.ResponseHeaders.ContainsKey("Transfer-Encoding"));
                Assert.Equal("updated response"u8.ToArray(), source.ResponseBody);
            }
            finally
            {
                await RemoveAllAsync(model);
            }
        });

        // A request with no captured response, or whose captured response was recorded without its body (an empty body with
        // the marker), cannot be verified (replay.verify.skipped). Whether some or all of a run's requests are such, they
        // count as not verified, neither matched nor differed (a request with no captured response is not taken for one
        // whose status differs), the summary says so and why, and it is a warning.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public Task ReissueAndVerify_CountsRequestsWithoutACapturedResponseOrBodyAsNotVerified_AndWarns(bool someVerified) => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok("reply"));
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            string notKept = RequestReplayDraft.ResponseBodyNotKeptHeader + ": 5\r\n";
            ProxyConnectionModel[] rows = someVerified
                ? new[]
                {
                    Capture(1, server.Port, "/kept", responseBody: "reply"u8.ToArray()),
                    Capture(2, server.Port, "/not-kept", responseHeaders: notKept),
                }
                : new[]
                {
                    Capture(1, server.Port, "/one", responseHeaders: notKept),
                    new ProxyConnectionModel(
                        2, string.Empty, DateTime.UtcNow, TimeSpan.Zero, "http", $"127.0.0.1:{server.Port}", "GET",
                        "/no-response", $"GET /no-response HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\n", [], string.Empty,
                        [], "127.0.0.1"),
                };
            CaptureConnectionView view = HostListUiTests.CaptureView(new HostListPolicy(), rows);
            view.ReplayTarget = Target(model, table: null);
            var messages = new List<(string Text, MessageBoxImage Image)>();
            view.ShowReplayMessage = (text, image) => messages.Add((text, image));
            try
            {
                ReplayRunResult run = await view.ReissueFromMenuAsync(new ReissueRequest(rows, Verify: true));

                int matched = someVerified ? 1 : 0;
                int notVerified = someVerified ? 1 : 2;
                Assert.True(run.Result.Succeeded);
                Assert.Contains(run.Result.Diagnostics, diagnostic => diagnostic.Code == ReplayOperation.VerifySkippedCode);
                Assert.Equal(
                    (matched, 0, 0, notVerified),
                    (run.Result.Value.Matched,
                        run.Result.Value.Mismatched,
                        run.Result.Value.Failed,
                        CaptureConnectionView.NotVerified(run.Result.Value)));
                (string text, MessageBoxImage image) = Assert.Single(messages);
                Assert.Equal(
                    Localization.GetLocalizedString("REPLAY_VERIFY_SUMMARY_NOT_VERIFIED", 2, matched, 0, 0, notVerified),
                    text);
                Assert.Contains(
                    "were not verified: no response was captured for them, or it was recorded without its body", text);
                Assert.Equal(MessageBoxImage.Warning, image);
                Assert.All(ReplaysOf(model).ProxyConnections, row => Assert.Equal(row.Path == "/kept", row.IsVerified));
            }
            finally
            {
                await RemoveAllAsync(model);
            }
        });

        [Fact]
        public void ReplayRow_ShowsWhereItComesFrom_AndWhatItDidNotKeep()
        {
            var exchange = new ReplayExchange
            {
                Outcome = ReplayOutcome.Failed,
                Request = new ReplayMessage("DELETE /item?x=1 HTTP/1.1", new[] { new KeyValuePair<string, string>("Host", "127.0.0.1:8080") }, null, "x"u8.ToArray()),
                RequestUri = new Uri("http://127.0.0.1:8080/item?x=1"),
                Started = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Duration = TimeSpan.FromMilliseconds(25),
            };

            ProxyConnectionModel row = ReplayRecorder.ToRow(3, new ReplayProvenance(ReplaySourceKind.Reissued, "Local PC", 12, 40), exchange);

            Assert.Equal((3L, "", "DELETE", "8080", "/item?x=1"), (row.RequestNumber, row.Status, row.Method, row.Port, row.Path));
            Assert.Equal("DELETE /item?x=1 HTTP/1.1\r\nHost: 127.0.0.1:8080\r\n\r\n", row.RequestLineAndHeaders);
            Assert.Equal("x"u8.ToArray(), row.RequestBody);
            Assert.Equal(("", 0), (row.ResponseLineAndHeaders, row.ResponseBody.Length));
            Assert.Equal(DateTimeKind.Local, row.RequestTime.Kind);
            Assert.Equal(exchange.Started, row.RequestTime.ToUniversalTime());
            Assert.True(row.IsReplay);
            Assert.Equal("Local PC #12", row.ReplaySource);
            Assert.Equal(40L, row.Replay.RecordRowId);
            Assert.False(row.HasReplayNote);
            Assert.False(Capture(1, 80, "/").IsReplay);

            ProxyConnectionModel composed = ReplayRecorder.ToRow(4, ReplayProvenance.Composed, exchange, keepRequestBody: false);

            Assert.Empty(composed.RequestBody);
            Assert.Equal(
                "DELETE /item?x=1 HTTP/1.1\r\nHost: 127.0.0.1:8080\r\nX-XMAT-Request-Body-Not-Kept: 1\r\n\r\n",
                composed.RequestLineAndHeaders);
            Assert.False(RequestReplayDraft.CanReplay(composed));
            Assert.Equal(Localization.GetLocalizedString("REPLAY_NOTE_REQUEST_BODY", 1L, 256L), composed.ReplayNote);
            Assert.Equal(Localization.GetLocalizedString("REPLAY_SOURCE_COMPOSED"), composed.ReplaySource);
            Assert.Equal(
                "Local PC #7, from the composer",
                ReplayProvenance.Of(ReplaySourceKind.Edited, "Local PC", Capture(7, 80, "/")).Describe());
            Assert.All(
                new[] { composed.ReplayNote, composed.ReplaySource, row.ReplaySource },
                text => Assert.DoesNotContain("NO_IMPL_", text));
        }

        private static ProxyConnectionModel Capture(
            long number,
            int port,
            string path,
            string headers = "",
            byte[] responseBody = null,
            string method = "GET",
            byte[] requestBody = null,
            string responseHeaders = "") =>
            new(number, "200", DateTime.UtcNow, TimeSpan.Zero, "http", $"127.0.0.1:{port}", method, path,
                $"{method} {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n{headers}", requestBody ?? [],
                $"HTTP/1.1 200 OK\r\n{responseHeaders}", responseBody ?? Array.Empty<byte>(), "127.0.0.1");

        // A POST with a 6-byte body.
        private static ProxyConnectionModel Upload(int port) =>
            Capture(4, port, "/upload", method: "POST", requestBody: "abcdef"u8.ToArray());

        // A send as ReplayOperation passes it to its sink.
        private static ReplayItemResult Sent() =>
            new(1, null, 1, 1, ReplayOutcome.Completed, 200, TimeSpan.Zero, 5, 0, null, null);

        private static ReplayExchange Answered() => new()
        {
            Outcome = ReplayOutcome.Completed,
            Request = new ReplayMessage(
                "GET / HTTP/1.1", new[] { new KeyValuePair<string, string>("Host", "127.0.0.1:9") }, null, null),
            Response = new ReplayMessage("HTTP/1.1 200 OK", null, null, "reply"u8.ToArray()),
            RequestUri = new Uri("http://127.0.0.1:9/"),
            StatusCode = 200,
            Started = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        // What the views pass: rows are added on the UI thread, synchronously.
        private static Action<Action> UiThread()
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            return action => dispatcher.Invoke(action);
        }

        // A dispatcher that has shut down, as XMAT's has once it exits.
        private static Dispatcher StoppedDispatcher()
        {
            Dispatcher dispatcher = null;
            using var started = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                started.Set();
                Dispatcher.Run();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            Assert.True(started.Wait(TimeSpan.FromSeconds(30)));
            dispatcher.InvokeShutdown();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
            Assert.True(dispatcher.HasShutdownFinished);
            return dispatcher;
        }

        private static ReplayRecordingTarget Target(
            CaptureAppModel model, IDataTable table, long bodyBudgetBytes = ReplayRecorder.DefaultBodyBudgetBytes) =>
            new(model, UiThread(), _ => table, bodyBudgetBytes);

        private static WebServiceDeviceCaptureController ReplaysOf(CaptureAppModel model) =>
            (WebServiceDeviceCaptureController)model.GetOrAddReplayContext().CaptureController;

        // The tab reads the table that its rows go to, as the application's tabs read the capture method's table.
        private static WebServiceDeviceCaptureController UseTable(ICaptureDeviceContext replays, IDataTable table)
        {
            var controller = (WebServiceDeviceCaptureController)replays.CaptureController;
            typeof(WebServiceDeviceCaptureController)
                .GetField("_dataTable", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(controller, table);
            return controller;
        }

        // A console's capture, as File > New adds it and lists it with the capture devices, with its proxy not started; it
        // reads table, which its rows go to.
        private static ICaptureDeviceContext AddConsole(CaptureAppModel model, string name, IDataTable table)
        {
            var console = new CaptureDeviceContextModel(
                DeviceType.XboxConsole,
                name,
                CaptureType.WebProxy,
                false,
                null,
                deferPortAllocation: true,
                hostListPolicy: model.HostListPolicy,
                proxyFactory: () => new WebServiceProxy());
            model.CaptureDeviceContexts.Add(console);
            model.CaptureDevicesTable.AddRow((long)DeviceType.XboxConsole, name, (long)CaptureType.WebProxy);
            UseTable(console, table);
            return console;
        }

        private static long RetainedBodyBytes(WebServiceDeviceCaptureController replays) =>
            replays.ProxyConnections.Sum(row => (long)row.RequestBody.Length + row.ResponseBody.Length);

        private static async Task RemoveAllAsync(CaptureAppModel model)
        {
            foreach (ICaptureDeviceContext context in model.CaptureDeviceContexts.ToList())
            {
                await model.RemoveDeviceContextAsync(context);
            }
        }

        // Same layout as WebServiceCaptureMethod.InitializeDataTables(), in memory.
        private static SqLtDatabase CreateDatabase(out IDataTable table)
        {
            var database = new SqLtDatabase();
            database.Initialize();
            table = database.CreateTable(
                WebServiceCaptureMethod.TableName,
                new Field<string>(WebServiceCaptureMethod.FieldKey_DeviceName),
                new Field<long>(WebServiceCaptureMethod.FieldKey_RequestNumber),
                new Field<long>(WebServiceCaptureMethod.FieldKey_ConnectionId),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestTimestamp),
                new Field<string>(WebServiceCaptureMethod.FieldKey_ResponseTimestamp),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestScheme),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestHost),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestPort),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestMethod),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestPath),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestStatus),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestBody),
                new Field<string>(WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders),
                new Field<string>(WebServiceCaptureMethod.FieldKey_ResponseBody),
                new Field<string>(WebServiceCaptureMethod.FieldKey_ClientIP));
            return database;
        }

        // With the capture devices table too, as CaptureAppModel.InitializeDatabase() makes it, so the database saves as a
        // capture file.
        private static SqLtDatabase CreateDatabase(out IDataTable table, out IDataTable devices)
        {
            SqLtDatabase database = CreateDatabase(out table);
            devices = database.CreateTable(
                "CaptureDevices",
                new Field<long>("DeviceType"),
                new Field<string>("DeviceName"),
                new Field<long>("CaptureType"));
            return database;
        }

        private static string FixtureDirectory()
        {
            string directory = Path.Join(AppContext.BaseDirectory, "ReplayUiFixtures", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static string[] PathsOf(IEnumerable<IDataRecord> records) =>
            records.Select(record => record.Str(WebServiceCaptureMethod.FieldKey_RequestPath)).ToArray();

        // The paths of a device's rows in the table.
        private static string[] Paths(IDataTable table, string deviceName) =>
            PathsOf(table.Dataset.DataRecordsWhen(
                new FieldValue<string>(WebServiceCaptureMethod.FieldKey_DeviceName, deviceName)));

        // The paths of the rows that a tab loaded from a saved capture shows.
        private static string[] LoadedPaths(CaptureAppModel model, string deviceName) =>
            ((WebServiceDeviceCaptureController)Assert.Single(
                    model.CaptureDeviceContexts, context => context.DeviceName == deviceName).CaptureController)
                .ProxyConnections.Select(row => row.Path).ToArray();

        // What the capture table keeps of a row.
        private static string Stored(ProxyConnectionModel row) => string.Join(
            "|",
            row.RequestNumber,
            row.Status,
            row.Method,
            row.Scheme,
            row.Host,
            row.Port,
            row.Path,
            row.RequestLineAndHeaders,
            Convert.ToBase64String(row.RequestBody ?? []),
            row.ResponseLineAndHeaders,
            Convert.ToBase64String(row.ResponseBody ?? []));

        // A device's rows in the table, each with its row id and fields.
        private static string[] Rows(IDataTable table, string deviceName) =>
            table.Dataset
                .DataRecordsWhen(new FieldValue<string>(WebServiceCaptureMethod.FieldKey_DeviceName, deviceName))
                .Select(record => string.Join(
                    "|",
                    record.RowId,
                    record.Int(WebServiceCaptureMethod.FieldKey_RequestNumber),
                    record.Str(WebServiceCaptureMethod.FieldKey_RequestMethod),
                    record.Str(WebServiceCaptureMethod.FieldKey_RequestPath),
                    record.Str(WebServiceCaptureMethod.FieldKey_RequestStatus),
                    record.Str(WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders),
                    record.Str(WebServiceCaptureMethod.FieldKey_RequestBody),
                    record.Str(WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders),
                    record.Str(WebServiceCaptureMethod.FieldKey_ResponseBody)))
                .ToArray();

        private static string Describe(ProxyConnectionModel row) => string.Join(
            "|",
            row.RecordRowId,
            row.RequestNumber,
            row.Status,
            row.Method,
            row.Scheme,
            row.Host,
            row.Port,
            row.Path,
            row.RequestLineAndHeaders,
            Convert.ToBase64String(row.RequestBody),
            row.ResponseLineAndHeaders,
            Convert.ToBase64String(row.ResponseBody),
            row.IsReplay);

        // As a right-click reaches the grid: the row's preview event, raised on the grid with the row as its source.
        private static void RightClick(DataGrid grid, DataGridRow row)
        {
            grid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
            {
                RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent,
                Source = row,
            });
            WpfTestHost.Pump();
        }

        private static ReissueRequest Request(MenuItem item) => Assert.IsType<ReissueRequest>(item.CommandParameter);

        private static MenuItem[] ReissueItems(ContextMenu menu) =>
            Flatten(menu).Where(item => item.Command == WebServiceProxyCommands.Reissue).ToArray();

        private static IEnumerable<MenuItem> Flatten(ItemsControl menu)
        {
            foreach (MenuItem item in menu.Items.OfType<MenuItem>())
            {
                yield return item;
                foreach (MenuItem child in Flatten(item))
                {
                    yield return child;
                }
            }
        }

        // A capture's row as a live capture updates it, with only the given fields.
        private sealed class ChangedRow(int rowId, Dictionary<string, string> values) : IDataRecord
        {
            public int RowId => rowId;

            public long Int(string fieldName) => 0;

            public string Str(string fieldName) => values.TryGetValue(fieldName, out string value) ? value : null;
        }
    }
}
