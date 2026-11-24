// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Drawing;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using NetworkTraceCaptureControls;
using XMAT.DataAbstractionLayer;
using XMAT.Models;
using XMAT.NetworkTrace;
using XMAT.NetworkTrace.Models;
using XMAT.NetworkTrace.NTDE;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class NetworkTraceCaptureControllerTests
    {
        // A packet as a trace engine reports it: a NetBIOS name query.
        private const string NetBiosPacket = """
            {"event":{"processId":4,"threadId":308,"timestamp":"2026-01-01T12:00:00Z"},
             "packet":{"mediaType":"ethernet","flags":["start","end","receive"],
             "data":"////////xJ3tqEMCCABFAABOT/wAAIARZlDAqAEDwKgB/wCJAIkAOvm7/+ABEAABAAAAAAAAIEVKRUlGRUVQRUtFRUVPRUtGQkNBQ0FDQUNBQ0FDQUFBAAAgAAE="}}
            """;

        private readonly CaptureUiApplicationFixture _application;

        public NetworkTraceCaptureControllerTests(CaptureUiApplicationFixture application)
        {
            _application = application;
        }

        [Fact]
        public void IsCapturing_UpdatesIsRunning()
        {
            var controller = new NetworkTraceCaptureController(
                "Test device",
                DeviceType.LocalPC);

            controller.IsCapturing = true;

            Assert.True(controller.IsRunning);

            controller.IsCapturing = false;

            Assert.False(controller.IsRunning);
        }

        [Fact]
        public async Task CanCloseAsync_ReturnsFalseWhileCaptureIsRunning()
        {
            var controller = new NetworkTraceCaptureController(
                "Test device",
                DeviceType.LocalPC)
            {
                IsCapturing = true
            };

            Assert.False(await controller.CanCloseAsync());

            controller.IsCapturing = false;

            Assert.True(await controller.CanCloseAsync());
        }

        [Theory]
        [InlineData("start")]
        [InlineData("stop")]
        [InlineData("download")]
        public async Task CaptureFailure_ResetsStateAndAllowsClosing(string phase)
        {
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var engine = new TestTraceEngine { PendingPhase = phase, PendingOperation = pending.Task };
            var controller = new NetworkTraceCaptureController("Test device", DeviceType.LocalPC)
            {
                _networkTraceEngine = engine
            };
            var expected = new InvalidOperationException("Trace operation failed.");
            if (phase != "start")
            {
                await controller.StartCapture(null);
            }

            var operation = phase == "start" ? controller.StartCapture(null) : controller.StopCapture(null);
            Assert.False(operation.IsCompleted);
            Assert.True(controller.IsRunning);
            Assert.False(await controller.CanCloseAsync());

            pending.SetException(expected);
            var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => operation);

            Assert.Same(expected, actual);
            Assert.False(controller.IsCapturing);
            Assert.False(controller.IsRunning);
            Assert.Equal(DeviceStatusEnum.Idle, controller.DeviceStatus);
            Assert.True(await controller.CanCloseAsync());
            Assert.Equal(0, engine.SubscriberCount);

            engine.PendingPhase = null;
            await controller.StartCapture(null);
            Assert.True(controller.IsRunning);
            Assert.Equal(DeviceStatusEnum.Tracing, controller.DeviceStatus);
            Assert.Equal(1, engine.SubscriberCount);
            await controller.StopCapture(null);
            Assert.True(await controller.CanCloseAsync());
            Assert.Equal(0, engine.SubscriberCount);
        }

        [Fact]
        public async Task Capture_CanRestartWithOneEventSubscription()
        {
            var engine = new TestTraceEngine();
            var controller = new NetworkTraceCaptureController("Test device", DeviceType.LocalPC)
            {
                _networkTraceEngine = engine
            };

            for (int capture = 0; capture < 2; capture++)
            {
                await controller.StartCapture(null);
                Assert.True(controller.IsCapturing);
                Assert.Equal(DeviceStatusEnum.Tracing, controller.DeviceStatus);
                Assert.Equal(1, engine.SubscriberCount);
                Assert.False(await controller.CanCloseAsync());

                await controller.StopCapture(null);
                Assert.False(controller.IsCapturing);
                Assert.Equal(DeviceStatusEnum.Idle, controller.DeviceStatus);
                Assert.Equal(0, engine.SubscriberCount);
                Assert.True(await controller.CanCloseAsync());
            }
        }

        [Theory]
        [InlineData("start", false)]
        [InlineData("start", true)]
        [InlineData("stop", false)]
        [InlineData("stop", true)]
        [InlineData("download", false)]
        [InlineData("download", true)]
        [InlineData("disconnect", true)]
        [InlineData("complete", false)]
        public async Task ConsoleCleanup_BlocksRestartButNotClose(string phase, bool disconnectFails)
        {
            var pendingDisconnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var expected = new InvalidOperationException("Trace operation failed.");
            var disconnectError = new IOException("Disconnect failed.");
            var engine = new TestTraceEngine
            {
                PendingPhase = phase,
                PendingOperation = phase is "start" or "stop" or "download"
                    ? Task.FromException(expected)
                    : Task.CompletedTask
            };
            var disconnected = new List<INetworkTraceEngine>();
            var controller = new NetworkTraceCaptureController("Test device", DeviceType.XboxConsole, traceEngine =>
            {
                disconnected.Add(traceEngine);
                return disconnected.Count == 1 ? pendingDisconnect.Task : Task.CompletedTask;
            })
            {
                _networkTraceEngine = engine,
                NetworkTracePackets = new NetworkTracePacketsCollection()
            };
            var appModel = new CaptureAppModel();
            appModel.CaptureDeviceContexts.Add(new TestCaptureDeviceContext(controller));
            Assert.True(appModel.TryBeginCaptureDataOperation(out var operationLease));
            using var lease = operationLease;
            Assert.False(await appModel.CanCloseAsync());
            if (phase != "start")
            {
                await controller.StartCapture(null, lease);
                lease.Dispose();
            }

            var operation = phase == "start" ? controller.StartCapture(null, lease) : controller.StopCapture(null);

            Assert.False(operation.IsCompleted);
            Assert.Equal(DeviceStatusEnum.Disconnecting, controller.DeviceStatus);
            Assert.False(controller.IsCapturing);
            Assert.False(controller.IsRunning);
            Assert.True(await controller.CanCloseAsync());
            Assert.False(appModel.IsCaptureDataOperationRunning);
            Assert.True(await appModel.CanCloseAsync());
            Assert.Null(controller._networkTraceEngine);
            Assert.Equal(0, engine.SubscriberCount);
            Assert.Same(engine, Assert.Single(disconnected));
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StartCapture(null));
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StopCapture(null));
            NetworkTraceCaptureView view = null;
            if (phase == "start" && !disconnectFails)
            {
                _application.Run(window =>
                {
                    view = new NetworkTraceCaptureView
                    {
                        DataContext = new TestCaptureDeviceContext(controller)
                    };
                    WpfTestHost.Show(window, view);
                    Assert.False(NetworkTraceCommands.StartStopCapture.CanExecute(null, view));
                });
            }

            if (disconnectFails)
            {
                pendingDisconnect.SetException(disconnectError);
            }
            else
            {
                pendingDisconnect.SetResult();
            }

            if (phase == "complete")
            {
                await operation;
            }
            else if (phase == "disconnect")
            {
                Assert.Same(disconnectError, await Assert.ThrowsAsync<IOException>(() => operation));
            }
            else
            {
                Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => operation));
            }
            Assert.Equal(DeviceStatusEnum.Idle, controller.DeviceStatus);

            _application.Run(window =>
            {
                if (view != null)
                {
                    WpfTestHost.Show(window, view);
                    Assert.True(NetworkTraceCommands.StartStopCapture.CanExecute(null, view));
                }
                var nextEngine = new TestTraceEngine();
                controller._networkTraceEngine = nextEngine;
                controller.StartCapture(null).GetAwaiter().GetResult();
                Assert.Same(nextEngine, controller._networkTraceEngine);
                Assert.Equal(1, nextEngine.SubscriberCount);
                controller.StopCapture(null).GetAwaiter().GetResult();
                Assert.Equal(new INetworkTraceEngine[] { engine, nextEngine }, disconnected);
            });
        }

        // In the new look, a quiet divider separates the packets from their data, as it separates the traffic from the
        // inspector in a web proxy capture.
        [Fact]
        public void PacketSplitter_DrawsTheNewLooksColumnDivider()
        {
            _application.Run(window =>
            {
                System.Windows.ResourceDictionary look = ThemeManager.CreateLook(newLook: true);
                ThemeManager.LoadTheme(look, "Light", newLook: true, highContrast: false);
                window.Resources = look;
                var controller = new NetworkTraceCaptureController("Test device", DeviceType.LocalPC)
                {
                    NetworkTracePackets = new NetworkTracePacketsCollection()
                };
                var view = new NetworkTraceCaptureView { DataContext = new TestCaptureDeviceContext(controller) };
                WpfTestHost.Show(window, view);

                var splitter = (GridSplitter)view.FindName("PacketSplitter");
                var line = Assert.IsType<Border>(splitter.Template.FindName("RestLine", splitter));
                Assert.Equal(((System.Windows.Media.SolidColorBrush)look["ColumnDividerBrush"]).Color,
                    Assert.IsType<System.Windows.Media.SolidColorBrush>(line.Background).Color);
                Assert.True(line.ActualHeight > 0, "The divider has no height.");
            });
        }

        // In the new look, as in the original, the packet table builds rows only for the packets in view, so a long
        // trace shows as soon as it's read instead of freezing the window.
        [Fact]
        public void PacketTable_BuildsRowsOnlyForThePacketsInView_InTheNewLook()
        {
            _application.Run(window =>
            {
                using var database = new SqLtDatabase();
                database.Initialize();
                NetworkTraceCaptureMethod.Method.Initialize(new CaptureAppModel { ActiveDatabase = database });
                System.Windows.ResourceDictionary look = ThemeManager.CreateLook(newLook: true);
                ThemeManager.LoadTheme(look, "Light", newLook: true, highContrast: false);
                window.Resources = look;
                window.Height = 400;
                var controller = new NetworkTraceCaptureController("Test device", DeviceType.LocalPC)
                {
                    _networkTraceEngine = new TestTraceEngine { PacketOnDownload = NetBiosPacket, PacketCount = 200 }
                };
                controller.Initialize();
                var view = new NetworkTraceCaptureView { DataContext = new TestCaptureDeviceContext(controller) };
                WpfTestHost.Show(window, view);

                NetworkTraceCommands.StartStopCapture.Execute(null, view);
                NetworkTraceCommands.StartStopCapture.Execute(null, view);
                WpfTestHost.Pump();

                var grid = (DataGrid)view.FindName("PacketList");
                Assert.Equal(200, grid.Items.Count);
                int rows = Rows(grid);
                Assert.True(rows < 50, $"The table built {rows} rows for 200 packets.");
            });
        }

        // The packet table summarizes the columns whose values repeat, protocols by name, and choosing a PID, TID,
        // protocol or address sets that filter to the value alone, as in a web proxy capture's traffic table.
        [Fact]
        public void ColumnSummaries_CoverRepeatedValues_AndChoosingOneFiltersThePackets()
        {
            _application.Run(window =>
            {
                using var database = new SqLtDatabase();
                database.Initialize();
                NetworkTraceCaptureMethod.Method.Initialize(new CaptureAppModel { ActiveDatabase = database });
                var controller = new NetworkTraceCaptureController("Test device", DeviceType.LocalPC)
                {
                    _networkTraceEngine = new TestTraceEngine
                    {
                        PacketOnDownload = NetBiosPacket,
                        PacketCount = 2,
                        OtherPacketsOnDownload = new[] { NetBiosPacket.Replace("\"processId\":4", "\"processId\":7", StringComparison.Ordinal) },
                    }
                };
                controller.Initialize();
                var view = new NetworkTraceCaptureView { DataContext = new TestCaptureDeviceContext(controller) };
                WpfTestHost.Show(window, view);
                NetworkTraceCommands.StartStopCapture.Execute(null, view);
                NetworkTraceCommands.StartStopCapture.Execute(null, view);
                WpfTestHost.Pump();

                var grid = (DataGrid)view.FindName("PacketList");
                Assert.Equal(
                    new[] { "PROTO", "SRC IP", "DEST IP", "SRC MAC", "DEST MAC", "TYPE", "PID", "TID" },
                    grid.Columns.Where(column => ColumnSummary.Describe(grid, column) != null).Select(column => (string)column.Header));
                ColumnSummaryColumn protocol = ColumnSummary.Describe(grid, grid.Columns[1]);
                Assert.Equal("UDP", protocol.ValueOf(grid.Items[0]));
                Assert.Equal("3 packets", protocol.RowCountText(3));
                Assert.Equal("Process ID", ColumnSummary.Title(grid.Columns[13]));

                ColumnSummaryColumn processId = ColumnSummary.Describe(grid, grid.Columns[13]);
                var seven = grid.Items.Cast<NetworkTracePacketDataModel>().Single(packet => packet.ProcessId == 7);
                var entry = new ColumnSummaryValue("7", 1, seven);
                Assert.True(processId.CanSelect(entry));
                processId.Select(entry);
                WpfTestHost.Pump();

                Assert.True(((CheckBoxFilters)view.FindName("PidFilters")).IsFilterEnabled);
                Assert.Equal(new[] { "7" }, controller.PidFilterList.Where(item => item.IsChecked).Select(item => item.Text));
                Assert.Equal(new[] { seven }, grid.Items.Cast<NetworkTracePacketDataModel>());
            });
        }

        private static int Rows(System.Windows.DependencyObject parent)
        {
            int rows = 0;
            for (int child = 0; child < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); child++)
            {
                System.Windows.DependencyObject element = System.Windows.Media.VisualTreeHelper.GetChild(parent, child);
                rows += (element is DataGridRow ? 1 : 0) + Rows(element);
            }

            return rows;
        }
        [Fact]
        public void DeviceStatusChange_RefreshesCaptureCommandAvailability()
        {
            _application.Run(window =>
            {
                var controller = new NetworkTraceCaptureController("Test device", DeviceType.LocalPC)
                {
                    NetworkTracePackets = new NetworkTracePacketsCollection()
                };
                var view = new NetworkTraceCaptureView
                {
                    DataContext = new TestCaptureDeviceContext(controller)
                };
                WpfTestHost.Show(window, view);
                var toolbar = ((Grid)view.Content).Children.OfType<ToolBar>().Single();
                var button = toolbar.Items.OfType<ToggleButton>().Single();
                Assert.True(button.IsEnabled);

                controller.DeviceStatus = DeviceStatusEnum.Disconnecting;
                WpfTestHost.Pump();
                Assert.False(button.IsEnabled);

                controller.DeviceStatus = DeviceStatusEnum.Idle;
                WpfTestHost.Pump();
                Assert.True(button.IsEnabled);
            });
        }

        [Fact]
        public void PartialDownloadFailure_KeepsPacketsFiltersAndRetryInSync()
        {
            _application.Run(window =>
            {
                using var database = new SqLtDatabase();
                database.Initialize();
                NetworkTraceCaptureMethod.Method.Initialize(new CaptureAppModel { ActiveDatabase = database });
                var expected = new IOException("Download incomplete.");
                var engine = new TestTraceEngine
                {
                    PendingPhase = "download",
                    PendingOperation = Task.FromException(expected),
                    PacketOnDownload = NetBiosPacket
                };
                var controller = new NetworkTraceCaptureController("Test device", DeviceType.LocalPC)
                {
                    _networkTraceEngine = engine
                };
                controller.Initialize();
                var packets = controller.NetworkTracePackets;
                var reportedErrors = new List<Exception>();
                var view = new NetworkTraceCaptureView(reportedErrors.Add)
                {
                    DataContext = new TestCaptureDeviceContext(controller)
                };
                WpfTestHost.Show(window, view);
                var grid = (DataGrid)view.FindName("PacketList");

                NetworkTraceCommands.StartStopCapture.Execute(null, view);
                Assert.Equal(DeviceStatusEnum.Tracing, controller.DeviceStatus);
                NetworkTraceCommands.StartStopCapture.Execute(null, view);
                WpfTestHost.Pump();

                Assert.Same(expected, Assert.Single(reportedErrors));
                Assert.Equal(DeviceStatusEnum.Idle, controller.DeviceStatus);
                Assert.Same(packets, controller.NetworkTracePackets);
                var packet = Assert.Single(packets);
                Assert.Same(packet, Assert.Single(view.NetworkTracePackets));
                Assert.Same(grid.Items, controller.FilteredItems);
                Assert.Same(packet, Assert.Single(controller.FilteredItems.Cast<NetworkTracePacketDataModel>()));
                Assert.Equal("4", Assert.Single(controller.PidFilterList).Text);
                Assert.Equal("308", Assert.Single(controller.TidFilterList).Text);
                Assert.True(controller.HaveTraces);

                engine.PendingPhase = null;
                NetworkTraceCommands.StartStopCapture.Execute(null, view);
                Assert.Equal(DeviceStatusEnum.Tracing, controller.DeviceStatus);
                NetworkTraceCommands.StartStopCapture.Execute(null, view);
                WpfTestHost.Pump();

                Assert.Single(reportedErrors);
                Assert.Same(packets, controller.NetworkTracePackets);
                var nextPacket = Assert.Single(packets);
                Assert.NotSame(packet, nextPacket);
                Assert.Same(nextPacket, Assert.Single(view.NetworkTracePackets));
                Assert.Same(nextPacket, Assert.Single(controller.FilteredItems.Cast<NetworkTracePacketDataModel>()));
                Assert.Single(controller.PidFilterList);
                Assert.Equal(0, engine.SubscriberCount);
            });
        }

        private sealed class TestCaptureDeviceContext(IDeviceCaptureController controller) : ICaptureDeviceContext
        {
            public DeviceType DeviceType { get; set; } = DeviceType.LocalPC;
            public CaptureType CaptureType => CaptureType.NetworkTrace;
            public string DeviceName => "Test device";
            public bool IsSelected { get; set; }
            public bool IsReadOnly { get; set; }
            public Color DeviceColor { get; set; }
            public IDeviceCaptureController CaptureController => controller;
        }

        private sealed class TestTraceEngine : INetworkTraceEngine
        {
            internal string PendingPhase { get; set; }
            internal Task PendingOperation { get; set; } = Task.CompletedTask;
            internal int SubscriberCount => EventRecordAvailable?.GetInvocationList().Length ?? 0;
            internal string PacketOnDownload { get; set; }
            internal int PacketCount { get; set; } = 1;
            internal string[] OtherPacketsOnDownload { get; set; } = Array.Empty<string>();

            public event EventHandler<string> EventRecordAvailable;

            public Task StartPacketTraceAsync() =>
                PendingPhase == "start" ? PendingOperation : Task.CompletedTask;

            public Task StopPacketTraceAsync() =>
                PendingPhase == "stop" ? PendingOperation : Task.CompletedTask;

            public Task GetAllEventsAsync()
            {
                for (int packet = 0; PacketOnDownload != null && packet < PacketCount; packet++)
                {
                    EventRecordAvailable?.Invoke(this, PacketOnDownload);
                }

                foreach (string packet in OtherPacketsOnDownload)
                {
                    EventRecordAvailable?.Invoke(this, packet);
                }
                return PendingPhase == "download" ? PendingOperation : Task.CompletedTask;
            }
        }
    }
}
