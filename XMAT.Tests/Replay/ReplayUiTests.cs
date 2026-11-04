// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using XMAT.Operations;
using XMAT.Operations.Replay;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Tests.Replay
{
    // The GUI's Replay menu and its Replays tab rows, with requests that target loopback servers only.
    [Collection(CaptureUiApplicationCollection.Name)]
    public class ReplayUiTests
    {
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

        [Fact]
        public Task Reissue_RecordsEachSendInTheReplaysTabWithTheRequestItReplays() => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(request => LoopbackResponse.Ok("reply " + request.Target));
            var controller = new WebServiceDeviceCaptureController(
                ReplayRecorder.DeviceName, DeviceType.Unknown, true, new ProxyPortPool());
            var recorder = new ReplayRecorder(controller, table: null, UiThread());
            var first = Capture(7, server.Port, "/one", responseBody: "reply /one"u8.ToArray());
            var second = Capture(9, server.Port, "/two", "If-None-Match: \"v1\"\r\n", "stale"u8.ToArray());

            OperationResult<ReplayResult> result = await CaptureConnectionView.ReissueAsync(
                new ReissueRequest(new[] { first, second }, Repeat: 2, Unconditional: true, Verify: true),
                allowUnsafeMethods: false,
                recorder);

            Assert.True(result.Succeeded);
            Assert.Null(ReplayRecorder.Current);
            Assert.Equal(new long[] { 1, 2, 3, 4 }, controller.ProxyConnections.Select(row => row.RequestNumber));
            Assert.Equal(new long?[] { 7, 9, 7, 9 }, controller.ProxyConnections.Select(row => row.ReplayOf));
            ProxyConnectionModel replayed = controller.ProxyConnections[0];
            Assert.Equal(
                ("200", "GET", "http", "127.0.0.1", server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "/one"),
                (replayed.Status, replayed.Method, replayed.Scheme, replayed.Host, replayed.Port, replayed.Path));
            Assert.StartsWith("GET /one HTTP/1.1\r\nHost: 127.0.0.1:", replayed.RequestLineAndHeaders);
            Assert.Equal("reply /one"u8.ToArray(), replayed.ResponseBody);
            Assert.All(server.Requests, request => Assert.Null(request.Header("If-None-Match")));
            Assert.Equal((2, 2), (result.Value.Matched, result.Value.Mismatched));
            string summary = CaptureConnectionView.VerifySummary(result.Value);
            Assert.StartsWith("Replayed 4 requests: 2 matched the captured response, 2 differed and 0 got no complete response.", summary);
            Assert.Contains("Request #9: body", summary);
            Assert.DoesNotContain("Request #7", summary);
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
            var controller = new WebServiceDeviceCaptureController(
                ReplayRecorder.DeviceName, DeviceType.Unknown, true, new ProxyPortPool());
            CaptureConnectionView view = HostListUiTests.CaptureView(new HostListPolicy());

            Task<OperationResult<ReplayResult>> running = CaptureConnectionView.ReissueAsync(
                new ReissueRequest(new[] { Capture(1, server.Port, "/slow") }),
                allowUnsafeMethods: false,
                new ReplayRecorder(controller, table: null, UiThread()));
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(WebServiceProxyCommands.Reissue.CanExecute(new ReissueRequest(new[] { Capture(2, 80, "/") }), view));
            Assert.True(WebServiceProxyCommands.CancelReissue.CanExecute(null, view));
            WebServiceProxyCommands.CancelReissue.Execute(null, view);

            OperationResult<ReplayResult> result = await running.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(OperationOutcome.Cancelled, result.Outcome);
            Assert.Null(ReplayRecorder.Current);
            Assert.Empty(controller.ProxyConnections);
        });

        [Fact]
        public void ReplayRow_WithoutAResponse_HasNoStatus()
        {
            var exchange = new ReplayExchange
            {
                Outcome = ReplayOutcome.Failed,
                Request = new ReplayMessage("DELETE /item?x=1 HTTP/1.1", new[] { new KeyValuePair<string, string>("Host", "127.0.0.1:8080") }, null, "x"u8.ToArray()),
                RequestUri = new Uri("http://127.0.0.1:8080/item?x=1"),
                Started = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Duration = TimeSpan.FromMilliseconds(25),
            };

            ProxyConnectionModel row = ReplayRecorder.ToRow(3, 12, exchange);

            Assert.Equal((3L, 12L, "", "DELETE", "8080", "/item?x=1"), (row.RequestNumber, row.ReplayOf.Value, row.Status, row.Method, row.Port, row.Path));
            Assert.Equal("DELETE /item?x=1 HTTP/1.1\r\nHost: 127.0.0.1:8080\r\n", row.RequestLineAndHeaders);
            Assert.Equal("x"u8.ToArray(), row.RequestBody);
            Assert.Equal(("", 0), (row.ResponseLineAndHeaders, row.ResponseBody.Length));
            Assert.Equal(exchange.Started, row.RequestTime);
            Assert.True(row.IsReplay);
            Assert.False(Capture(1, 80, "/").IsReplay);
        }

        private static ProxyConnectionModel Capture(
            long number, int port, string path, string headers = "", byte[] responseBody = null) =>
            new(number, "200", DateTime.UtcNow, TimeSpan.Zero, "http", $"127.0.0.1:{port}", "GET", path,
                $"GET {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n{headers}", [], "HTTP/1.1 200 OK\r\n",
                responseBody ?? Array.Empty<byte>(), "127.0.0.1");

        // What the view passes the recorder: run on the UI thread, synchronously.
        private static Action<Action> UiThread()
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            return action => dispatcher.Invoke(action);
        }

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
    }
}
