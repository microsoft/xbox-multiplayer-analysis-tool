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
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

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
            ICaptureDeviceContext replays = model.GetOrAddReplayContext(ReplayRecorder.DeviceName);
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
                Assert.Empty(trimmed.ResponseBody);
                Assert.Equal("200", trimmed.Status);
                Assert.StartsWith("HTTP/1.1 200 OK\r\n", trimmed.ResponseLineAndHeaders);
                Assert.Equal(Localization.GetLocalizedString("REPLAY_NOTE_RESPONSE_BODY"), trimmed.ReplayNote);
                Assert.DoesNotContain("NO_IMPL_", trimmed.ReplayNote);
                (string text, MessageBoxImage image) = Assert.Single(messages);
                Assert.StartsWith("1 request or response bodies were not kept: the Replays tab keeps at most", text);
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
            ICaptureDeviceContext replays = model.GetOrAddReplayContext(ReplayRecorder.DeviceName);
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
                Assert.NotSame(replays, model.GetOrAddReplayContext(ReplayRecorder.DeviceName));
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

        // The Replays tab is the one the replay feature created: a tab that merely has its name is never used, name lookups
        // (loads, imports, new captures) never find it, and a closed one is replaced by a new tab.
        [Fact]
        public Task ReplaysTab_IsTheReplayFeaturesOwnTab_NeverOneFoundByName() => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            try
            {
                ICaptureDeviceContext foreign = model.AddDeviceContext(
                    DeviceType.Unknown, ReplayRecorder.DeviceName, CaptureType.WebProxy, true, null);

                ICaptureDeviceContext replays = model.GetOrAddReplayContext(ReplayRecorder.DeviceName);

                Assert.NotSame(foreign, replays);
                Assert.Equal("Replays (2)", replays.DeviceName);
                Assert.True(replays.IsReadOnly);
                Assert.Same(replays, model.GetOrAddReplayContext(ReplayRecorder.DeviceName));
                Assert.Same(foreign, model.GetDeviceContext(DeviceType.Unknown, ReplayRecorder.DeviceName, CaptureType.WebProxy, true));
                Assert.Null(model.GetDeviceContext(DeviceType.Unknown, "Replays (2)", CaptureType.WebProxy, true));
                ICaptureDeviceContext sameName = model.AddDeviceContext(
                    DeviceType.Unknown, "Replays (2)", CaptureType.WebProxy, true, null);
                Assert.NotSame(replays, sameName);

                await model.RemoveDeviceContextAsync(replays);
                ICaptureDeviceContext next = model.GetOrAddReplayContext(ReplayRecorder.DeviceName);

                Assert.NotSame(replays, next);
                Assert.Equal("Replays (3)", next.DeviceName);
                Assert.DoesNotContain(model.CaptureDeviceContexts, context => ReferenceEquals(context, replays));
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
            Assert.Equal("DELETE /item?x=1 HTTP/1.1\r\nHost: 127.0.0.1:8080\r\n", row.RequestLineAndHeaders);
            Assert.Equal("x"u8.ToArray(), row.RequestBody);
            Assert.Equal(("", 0), (row.ResponseLineAndHeaders, row.ResponseBody.Length));
            Assert.Equal(exchange.Started, row.RequestTime);
            Assert.True(row.IsReplay);
            Assert.Equal("Local PC #12", row.ReplaySource);
            Assert.Equal(40L, row.Replay.RecordRowId);
            Assert.False(row.HasReplayNote);
            Assert.False(Capture(1, 80, "/").IsReplay);

            ProxyConnectionModel composed = ReplayRecorder.ToRow(4, ReplayProvenance.Composed, exchange, keepRequestBody: false);

            Assert.Empty(composed.RequestBody);
            Assert.Equal(Localization.GetLocalizedString("REPLAY_NOTE_REQUEST_BODY"), composed.ReplayNote);
            Assert.Equal(Localization.GetLocalizedString("REPLAY_SOURCE_COMPOSED"), composed.ReplaySource);
            Assert.Equal(
                "Local PC #7, from the composer",
                ReplayProvenance.Of(ReplaySourceKind.Edited, "Local PC", Capture(7, 80, "/")).Describe());
            Assert.All(
                new[] { composed.ReplayNote, composed.ReplaySource, row.ReplaySource },
                text => Assert.DoesNotContain("NO_IMPL_", text));
        }

        private static ProxyConnectionModel Capture(
            long number, int port, string path, string headers = "", byte[] responseBody = null, string method = "GET") =>
            new(number, "200", DateTime.UtcNow, TimeSpan.Zero, "http", $"127.0.0.1:{port}", method, path,
                $"{method} {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n{headers}", [], "HTTP/1.1 200 OK\r\n",
                responseBody ?? Array.Empty<byte>(), "127.0.0.1");

        // What the views pass: rows are added on the UI thread, synchronously.
        private static Action<Action> UiThread()
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            return action => dispatcher.Invoke(action);
        }

        private static ReplayRecordingTarget Target(
            CaptureAppModel model, IDataTable table, long bodyBudgetBytes = ReplayRecorder.DefaultBodyBudgetBytes) =>
            new(model, UiThread(), _ => table, bodyBudgetBytes);

        private static WebServiceDeviceCaptureController ReplaysOf(CaptureAppModel model) =>
            (WebServiceDeviceCaptureController)model.GetOrAddReplayContext(ReplayRecorder.DeviceName).CaptureController;

        // The tab reads the table that its rows go to, as the application's tabs read the capture method's table.
        private static WebServiceDeviceCaptureController UseTable(ICaptureDeviceContext replays, IDataTable table)
        {
            var controller = (WebServiceDeviceCaptureController)replays.CaptureController;
            typeof(WebServiceDeviceCaptureController)
                .GetField("_dataTable", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(controller, table);
            return controller;
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
