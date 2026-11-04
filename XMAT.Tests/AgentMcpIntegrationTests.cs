// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using XMAT.AgentAccess;
using XMAT.Models;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public partial class AgentMcpIntegrationTests
    {
        private readonly CaptureUiApplicationFixture _application;

        public AgentMcpIntegrationTests(CaptureUiApplicationFixture application) =>
            _application = application;

        [Fact]
        public void RealStdioClientRequiresGrantsAndApprovalAndRetriesWithoutRestartingCapture()
        {
            _application.Run(_ =>
            {
                var model = new CaptureAppModel();
                var target = new Context("agent target");
                var human = new Context("human target");
                human.Controller.SetRunning(true);
                model.CaptureDeviceContexts.Add(target);
                model.CaptureDeviceContexts.Add(human);
                model.SelectedDeviceContext = human;
                var operations = new TestOperations(model, Dispatcher.CurrentDispatcher);
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher, operations);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    var tools = Complete(client.ListToolsAsync().AsTask());
                    Assert.Contains(tools, tool => tool.Name == "xmat_set_capture_running");
                    Assert.DoesNotContain(tools, tool => tool.Name.Contains("approve", StringComparison.OrdinalIgnoreCase));
                    Assert.DoesNotContain(tools, tool => tool.Name.Contains("grant", StringComparison.OrdinalIgnoreCase));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var snapshot = Assert.Single(host.Runtime.GetSessions());
                    Assert.Equal("XMAT integration test", snapshot.DeclaredClientName);
                    var session = host.FindSession(snapshot.Id);
                    var denied = Complete(client.CallToolAsync("xmat_list_captures").AsTask());
                    Assert.True(denied.IsError);
                    Assert.Equal("PermissionDenied", Payload(denied).GetProperty("error").GetString());

                    var summary = new AgentPermission(AgentCapability.SummaryRead, AgentScope.Application);
                    Finish(host.Runtime.SetPermissionsAsync(session, [summary]));
                    var listing = Payload(Complete(client.CallToolAsync("xmat_list_captures").AsTask()));
                    var capture = listing.GetProperty("captures").EnumerateArray()
                        .Select(item => item.Deserialize<CaptureDescriptor>(AgentJson.Options))
                        .Single(item => item.DeviceName == "agent target");
                    Finish(host.Runtime.SetPermissionsAsync(session,
                        [summary, new AgentPermission(AgentCapability.CaptureControl, AgentScope.Capture(capture.Id))]));
                    host.PauseRequests();
                    var paused = Complete(client.CallToolAsync("xmat_list_captures").AsTask());
                    Assert.True(paused.IsError);
                    Assert.Equal("OperationUnavailable", Payload(paused).GetProperty("error").GetString());
                    host.ResumeRequests();
                    Assert.Equal(2, host.Runtime.GetSession(session).Permissions.Count);
                    var arguments = new Dictionary<string, object>
                    {
                        ["captureId"] = capture.Id,
                        ["expectedRevision"] = capture.Revision,
                        ["running"] = true,
                        ["idempotencyKey"] = "start-once"
                    };
                    var requested = Payload(Complete(client.CallToolAsync("xmat_set_capture_running", arguments).AsTask()));
                    var operationId = requested.GetProperty("id").GetGuid();
                    Assert.Equal("AwaitingApproval", requested.GetProperty("state").GetString());
                    Assert.Equal(0, operations.StartCount);
                    Assert.True(host.Runtime.Approve(operationId));
                    Finish(host.Runtime.WaitForCompletionAsync(session, operationId));
                    Assert.True(target.Controller.IsRunning);
                    Assert.True(operations.RanOnDispatcher);

                    var replay = Payload(Complete(client.CallToolAsync("xmat_set_capture_running", arguments).AsTask()));
                    Assert.Equal(operationId, replay.GetProperty("id").GetGuid());
                    Assert.Equal(1, operations.StartCount);
                    var result = Payload(Complete(client.CallToolAsync("xmat_get_result",
                        new Dictionary<string, object> { ["operationId"] = operationId }).AsTask()));
                    Assert.True(result.GetProperty("isRunning").GetBoolean());
                    Assert.Same(human, model.SelectedDeviceContext);

                    var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    host.Runtime.Changed += (_, _) =>
                    {
                        if (host.Runtime.GetSession(session).State == AgentSessionState.Disconnected)
                        {
                            disconnected.TrySetResult();
                        }
                    };
                    Finish(client.DisposeAsync().AsTask());
                    client = null;
                    Finish(disconnected.Task);
                    Finish(host.Runtime.EndSessionAsync(session));
                    Assert.False(target.Controller.IsRunning);
                    Assert.True(human.Controller.IsRunning);
                    Assert.Equal(1, operations.StopCount);
                    Assert.Empty(host.Runtime.GetOwnedResources());
                }
                finally
                {
                    if (client != null)
                        Finish(client.DisposeAsync().AsTask());
                    Finish(host.DisposeAsync().AsTask());
                }
            });
        }

        [Fact]
        public void CatalogCursorsAreSessionBoundAndManualChangesInvalidateOldRevisions()
        {
            _application.Run(_ =>
            {
                var model = new CaptureAppModel();
                var target = new Context("first");
                model.CaptureDeviceContexts.Add(target);
                model.CaptureDeviceContexts.Add(new Context("second"));
                var operations = new TestOperations(model, Dispatcher.CurrentDispatcher);
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher, operations);
                McpClient first = null;
                McpClient second = null;
                try
                {
                    Assert.Equal(AgentAccessError.OperationUnavailable,
                        Assert.Throws<AgentAccessException>(host.Enable).Error);
                    host.MarkReady();
                    host.Enable();
                    first = Complete(Connect(host, "first client"));
                    Complete(first.CallToolAsync("xmat_get_session").AsTask());
                    var firstSession = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var summary = new AgentPermission(AgentCapability.SummaryRead, AgentScope.Application);
                    Finish(host.Runtime.SetPermissionsAsync(firstSession, [summary], new AgentLimits(responseRows: 1)));
                    var page = Payload(Complete(first.CallToolAsync("xmat_list_captures").AsTask()));
                    Assert.Single(page.GetProperty("captures").EnumerateArray());
                    Assert.True(page.GetProperty("hasMore").GetBoolean());
                    var cursor = page.GetProperty("nextCursor").GetString();
                    var original = page.GetProperty("captures")[0].Deserialize<CaptureDescriptor>(AgentJson.Options);
                    var next = Payload(Complete(first.CallToolAsync("xmat_list_captures",
                        new Dictionary<string, object> { ["cursor"] = cursor }).AsTask()));
                    Assert.False(next.GetProperty("hasMore").GetBoolean());
                    Assert.NotEqual(original.Id, next.GetProperty("captures")[0].GetProperty("id").GetGuid());

                    second = Complete(Connect(host, "second client"));
                    Complete(second.CallToolAsync("xmat_get_session").AsTask());
                    var secondSession = host.FindSession(host.Runtime.GetSessions()
                        .Single(item => item.DeclaredClientName == "second client").Id);
                    Finish(host.Runtime.SetPermissionsAsync(secondSession, [summary]));
                    var borrowed = Complete(second.CallToolAsync("xmat_list_captures",
                        new Dictionary<string, object> { ["cursor"] = cursor }).AsTask());
                    Assert.True(borrowed.IsError);
                    Assert.Equal("OperationNotFound", Payload(borrowed).GetProperty("error").GetString());

                    Finish(host.Runtime.SetPermissionsAsync(firstSession,
                        [summary, new AgentPermission(AgentCapability.CaptureControl, AgentScope.Capture(original.Id))]));
                    target.Controller.SetRunning(true);
                    target.Controller.SetRunning(false);
                    var stale = Complete(first.CallToolAsync("xmat_set_capture_running", new Dictionary<string, object>
                    {
                        ["captureId"] = original.Id,
                        ["expectedRevision"] = original.Revision,
                        ["running"] = true,
                        ["idempotencyKey"] = "stale"
                    }).AsTask());
                    Assert.True(stale.IsError);
                    Assert.Equal("SourceChanged", Payload(stale).GetProperty("error").GetString());
                    Assert.Equal(0, operations.StartCount);
                }
                finally
                {
                    if (first != null)
                        Finish(first.DisposeAsync().AsTask());
                    if (second != null)
                        Finish(second.DisposeAsync().AsTask());
                    Finish(host.DisposeAsync().AsTask());
                }
            });
        }

        [Fact]
        public void DesktopWindowSupportsMultipleGrantsWithoutStoppingExistingOwnedCaptures()
        {
            _application.Run(owner =>
            {
                WpfTestHost.Show(owner, new Grid());
                var model = new CaptureAppModel();
                var chosen = new Context("chosen");
                model.CaptureDeviceContexts.Add(chosen);
                model.CaptureDeviceContexts.Add(new Context("not chosen"));
                var operations = new TestOperations(model, Dispatcher.CurrentDispatcher);
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher,
                    operations);
                AgentAccessWindow manager = null;
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    manager = AgentAccessWindow.ShowOrActivate(owner, host);
                    WpfTestHost.Pump();
                    Assert.False(host.IsEnabled);
                    Assert.Equal(Localization.GetLocalizedString("AGENT_ACCESS_TITLE"), manager.Title);
                    var enable = (ToggleButton)manager.FindName("EnableAccess");
                    enable.IsChecked = true;
                    enable.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.True(host.IsEnabled);

                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    WpfTestHost.Pump();
                    var sessions = (DataGrid)manager.FindName("Sessions");
                    sessions.SelectedItem = sessions.Items.Cast<AgentSessionSnapshot>().Single();
                    var targets = (ComboBox)manager.FindName("CaptureTarget");
                    targets.SelectedItem = targets.Items.Cast<CaptureDescriptor>().Single(item => item.DeviceName == "chosen");
                    ((CheckBox)manager.FindName("SummaryGrant")).IsChecked = true;
                    ((CheckBox)manager.FindName("ControlGrant")).IsChecked = true;
                    ((Button)manager.FindName("ApplyGrants")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    WpfTestHost.Pump();
                    var session = Assert.Single(host.Runtime.GetSessions());
                    var target = (CaptureDescriptor)targets.SelectedItem;
                    Assert.Contains(new AgentPermission(AgentCapability.CaptureControl, AgentScope.Capture(target.Id)),
                        session.Permissions);
                    Assert.Equal(2, session.Permissions.Count);
                    Assert.DoesNotContain(session.Permissions, permission => permission.Capability == AgentCapability.ScriptExecute);
                    Assert.Same(manager, AgentAccessWindow.ShowOrActivate(owner, host));
                    var connection = host.FindSession(session.Id);
                    var start = Payload(Complete(client.CallToolAsync("xmat_set_capture_running",
                        new Dictionary<string, object>
                        {
                            ["captureId"] = target.Id,
                            ["expectedRevision"] = target.Revision,
                            ["running"] = true,
                            ["idempotencyKey"] = "retain-chosen"
                        }).AsTask())).GetProperty("id").GetGuid();
                    Assert.True(host.Runtime.Approve(start));
                    Finish(host.Runtime.WaitForCompletionAsync(connection, start));
                    Assert.True(chosen.Controller.IsRunning);

                    targets.SelectedItem = targets.Items.Cast<CaptureDescriptor>().Single(item => item.DeviceName == "not chosen");
                    var other = (CaptureDescriptor)targets.SelectedItem;
                    ((CheckBox)manager.FindName("ControlGrant")).IsChecked = true;
                    ((Button)manager.FindName("ApplyGrants")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    CaptureDeviceTestData.PumpUntil(() => host.Runtime.GetSession(connection).Permissions.Contains(
                        new AgentPermission(AgentCapability.CaptureControl, AgentScope.Capture(other.Id))));
                    Assert.Contains(new AgentPermission(AgentCapability.CaptureControl, AgentScope.Capture(target.Id)),
                        host.Runtime.GetSession(connection).Permissions);
                    Assert.True(chosen.Controller.IsRunning);
                    Assert.Equal(0, operations.StopCount);
                    Assert.Equal(target.Id, Assert.Single(host.Runtime.GetOwnedResources()).Resource.Id);

                    ((CheckBox)manager.FindName("ControlGrant")).IsChecked = false;
                    ((Button)manager.FindName("ApplyGrants")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    CaptureDeviceTestData.PumpUntil(() => !host.Runtime.GetSession(connection).Permissions.Contains(
                        new AgentPermission(AgentCapability.CaptureControl, AgentScope.Capture(other.Id))));
                    Assert.Contains(new AgentPermission(AgentCapability.CaptureControl, AgentScope.Capture(target.Id)),
                        host.Runtime.GetSession(connection).Permissions);
                    Assert.True(chosen.Controller.IsRunning);
                    Assert.Equal(0, operations.StopCount);
                }
                finally
                {
                    manager?.Close();
                    if (client != null)
                        Finish(client.DisposeAsync().AsTask());
                    Finish(host.DisposeAsync().AsTask());
                }
            });
        }

        [Fact]
        public void DisconnectRetainsManualListenerForRecoveryAndDoesNotAdoptAlreadyRunningHumanCapture()
        {
            _application.Run(_ =>
            {
                var model = new CaptureAppModel();
                var manual = new Context("manual", DeviceType.GenericProxyDevice);
                var human = new Context("human");
                human.Controller.SetRunning(true);
                model.CaptureDeviceContexts.Add(manual);
                model.CaptureDeviceContexts.Add(human);
                var operations = new TestOperations(model, Dispatcher.CurrentDispatcher);
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher, operations);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var session = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var summary = new AgentPermission(AgentCapability.SummaryRead, AgentScope.Application);
                    Finish(host.Runtime.SetPermissionsAsync(session, [summary]));
                    var captures = Payload(Complete(client.CallToolAsync("xmat_list_captures").AsTask()))
                        .GetProperty("captures").EnumerateArray()
                        .Select(item => item.Deserialize<CaptureDescriptor>(AgentJson.Options)).ToArray();
                    Finish(host.Runtime.SetPermissionsAsync(session, captures.Select(item =>
                        new AgentPermission(AgentCapability.CaptureControl, AgentScope.Capture(item.Id))).Append(summary)));
                    foreach (var target in captures)
                    {
                        var requested = Payload(Complete(client.CallToolAsync("xmat_set_capture_running",
                            new Dictionary<string, object>
                            {
                                ["captureId"] = target.Id,
                                ["expectedRevision"] = target.Revision,
                                ["running"] = true,
                                ["idempotencyKey"] = target.DeviceName
                            }).AsTask()));
                        Finish(host.Runtime.WaitForCompletionAsync(session, requested.GetProperty("id").GetGuid()));
                    }
                    Assert.Equal(1, operations.StartCount);
                    Assert.Single(host.Runtime.GetOwnedResources());
                    Finish(host.RevokeAsync(session.Id));
                    Assert.True(manual.Controller.IsRunning);
                    Assert.True(human.Controller.IsRunning);
                    Assert.Equal(0, operations.StopCount);
                    var recovery = Assert.Single(host.Runtime.GetOwnedResources());
                    Assert.Equal(AgentResourceState.RecoveryRequired, recovery.State);
                    Assert.Equal(captures.Single(item => item.DeviceName == "manual").Id, recovery.Resource.Id);
                }
                finally
                {
                    if (client != null)
                        Finish(client.DisposeAsync().AsTask());
                    Finish(host.DisposeAsync().AsTask());
                }
            });
        }

        [Fact]
        public void DelayedStartReceiptCannotAdoptAHumanRestart()
        {
            _application.Run(_ =>
            {
                var model = new CaptureAppModel();
                var target = new Context("human restarted");
                model.CaptureDeviceContexts.Add(target);
                var operations = new TestOperations(model, Dispatcher.CurrentDispatcher);
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher, operations);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var session = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var capture = Complete(host.GetCapturesAsync()).Single();
                    Finish(host.Runtime.SetPermissionsAsync(session,
                        [new AgentPermission(AgentCapability.CaptureControl, AgentScope.Capture(capture.Id))]));
                    operations.AfterCommit = () =>
                    {
                        operations.AfterCommit = null;
                        target.Controller.SetRunning(false);
                        target.Controller.SetRunning(true);
                    };
                    var request = Payload(Complete(client.CallToolAsync("xmat_set_capture_running",
                        new Dictionary<string, object>
                        {
                            ["captureId"] = capture.Id,
                            ["expectedRevision"] = capture.Revision,
                            ["running"] = true,
                            ["idempotencyKey"] = "restart-race"
                        }).AsTask()));
                    var id = request.GetProperty("id").GetGuid();
                    Assert.True(host.Runtime.Approve(id));
                    Finish(host.Runtime.WaitForCompletionAsync(session, id));
                    Finish(host.RevokeAsync(session.Id));

                    Assert.True(target.Controller.IsRunning);
                    Assert.Equal(0, operations.StopCount);
                    Assert.Empty(host.Runtime.GetOwnedResources());
                }
                finally
                {
                    if (client != null)
                        Finish(client.DisposeAsync().AsTask());
                    Finish(host.DisposeAsync().AsTask());
                }
            });
        }

        [Fact]
        public void NetworkTraceStartDisclosesDataResetAndWaitsForDesktopApproval()
        {
            _application.Run(_ =>
            {
                var model = new CaptureAppModel();
                model.CaptureDeviceContexts.Add(new Context("trace",
                    captureType: CaptureType.NetworkTrace));
                var operations = new TestOperations(model, Dispatcher.CurrentDispatcher);
                var host = new AgentAccessHost(model, Dispatcher.CurrentDispatcher, operations);
                McpClient client = null;
                try
                {
                    host.MarkReady();
                    host.Enable();
                    client = Complete(Connect(host));
                    Complete(client.CallToolAsync("xmat_get_session").AsTask());
                    var session = host.FindSession(host.Runtime.GetSessions().Single().Id);
                    var capture = Complete(host.GetCapturesAsync()).Single();
                    Finish(host.Runtime.SetPermissionsAsync(session,
                        [new AgentPermission(AgentCapability.CaptureControl, AgentScope.Capture(capture.Id))]));
                    var result = Complete(client.CallToolAsync("xmat_set_capture_running",
                        new Dictionary<string, object>
                        {
                            ["captureId"] = capture.Id,
                            ["expectedRevision"] = capture.Revision,
                            ["running"] = true,
                            ["idempotencyKey"] = "unsupported"
                        }).AsTask());
                    Assert.False(result.IsError ?? false);
                    var operation = Payload(result).GetProperty("id").GetGuid();
                    Assert.Equal(AgentOperationState.AwaitingApproval, host.Runtime.GetOperation(session, operation).State);
                    Assert.True(host.Runtime.GetOperation(session, operation).Effects.HasFlag(AgentOperationEffects.DataDeletion));
                    Assert.Equal(0, operations.StartCount);
                    Assert.True(host.Runtime.Deny(operation));
                }
                finally
                {
                    if (client != null)
                        Finish(client.DisposeAsync().AsTask());
                    Finish(host.DisposeAsync().AsTask());
                }
            });
        }

        [Fact]
        public async Task InputMessageBudgetCountsAcrossReadsAndResetsAtLineBoundaries()
        {
            await using var bounded = new BoundedMcpInputStream(
                new MemoryStream(Encoding.UTF8.GetBytes("1234\n12345")), maximumMessageBytes: 4);
            var buffer = new byte[5];
            Assert.Equal(5, await bounded.ReadAsync(buffer));
            Assert.Equal(3, await bounded.ReadAsync(buffer.AsMemory(0, 3)));
            await Assert.ThrowsAsync<IOException>(() => bounded.ReadAsync(buffer, 0, 2, CancellationToken.None));
        }

        [Fact]
        public async Task CompanionRejectsStaleProcessIdentityWithoutWritingToStdout()
        {
            using var current = Process.GetCurrentProcess();
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(BridgePath())
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            foreach (var argument in Arguments(Guid.NewGuid(), current.StartTime.ToUniversalTime().Ticks + 1))
            {
                process.StartInfo.ArgumentList.Add(argument);
            }
            process.Start();
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Equal(3, process.ExitCode);
                Assert.Empty(await stdout);
                Assert.Contains("XMAT_AGENT_INSTANCE_MISMATCH", await stderr);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }

        private static async Task<McpClient> Connect(AgentAccessHost host, string name = "XMAT integration test")
        {
            using var process = Process.GetCurrentProcess();
            var diagnostics = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = BridgePath(),
                Arguments = Arguments(host.InstanceId, process.StartTime.ToUniversalTime().Ticks),
                StandardErrorLines = diagnostics.Enqueue
            });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                return await McpClient.CreateAsync(transport, new McpClientOptions
                {
                    ClientInfo = new Implementation { Name = name, Version = "1.0" }
                }, cancellationToken: timeout.Token);
            }
            catch (Exception exception)
            {
                throw new Xunit.Sdk.XunitException($"{exception}\nBridge stderr: {string.Join("\n", diagnostics)}");
            }
        }

        private static string[] Arguments(Guid instance, long started) =>
        [
            "--instance", instance.ToString("D"),
            "--pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--started", started.ToString(System.Globalization.CultureInfo.InvariantCulture)
        ];

        private static string BridgePath() => Path.Combine(AppContext.BaseDirectory, "AgentBridge", "XMAT.AgentBridge.exe");
        private static JsonElement Payload(CallToolResult result) => Assert.IsType<JsonElement>(result.StructuredContent);

        private static T Complete<T>(Task<T> task)
        {
            var timeout = Stopwatch.StartNew();
            while (!task.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(25))
            {
                WpfTestHost.Pump();
                Thread.Sleep(1);
            }
            Assert.True(task.IsCompleted, "The MCP integration operation did not complete.");
            return task.GetAwaiter().GetResult();
        }

        private static void Finish(Task task) => Complete(Observe(task));
        private static async Task<bool> Observe(Task task) { await task; return true; }

        private sealed class Context(
            string name,
            DeviceType deviceType = DeviceType.LocalPC,
            CaptureType captureType = CaptureType.WebProxy) : ICaptureDeviceContext
        {
            internal Controller Controller { get; } = new();
            public DeviceType DeviceType => deviceType;
            public CaptureType CaptureType => captureType;
            public string DeviceName => name;
            public bool IsSelected { get; set; }
            public bool IsReadOnly { get; set; }
            public Color DeviceColor { get; set; }
            public IDeviceCaptureController CaptureController => Controller;
        }

        private sealed class Controller : IDeviceCaptureController
        {
            public bool IsRunning { get; private set; }
            public ICaptureMethod CaptureMethod => null;
            public object Parameters => null;
            public event PropertyChangedEventHandler PropertyChanged;
            internal void SetRunning(bool value)
            {
                IsRunning = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRunning)));
            }
            public void Initialize() { }
            public void LoadCaptures(IEnumerable<IDataset> tables) { }
            public void ClearAllCaptures() { }
            public Task<bool> CanCloseAsync() => Task.FromResult(true);
            public Task CloseAsync() => Task.CompletedTask;
        }

        private sealed class TestOperations(CaptureAppModel model, Dispatcher dispatcher) : ICaptureOperations
        {
            internal int StartCount { get; private set; }
            internal int StopCount { get; private set; }
            internal bool RanOnDispatcher { get; private set; }
            internal Action AfterCommit { get; set; }
            public Task<CaptureOperationFailure> GetAvailabilityAsync(
                ICaptureDeviceContext target, bool running, CancellationToken cancellationToken = default) =>
                Task.FromResult(CaptureOperationFailure.None);

            public Task<CaptureOperationResult> SetRunningAsync(
                ICaptureDeviceContext target, bool running, IProgress<WebProxyStopProgress> progress = null,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RanOnDispatcher = dispatcher.CheckAccess();
                var controller = (Controller)target.CaptureController;
                var changed = controller.IsRunning != running;
                if (changed)
                {
                    if (running)
                        StartCount++;
                    else
                        StopCount++;
                    controller.SetRunning(running);
                }
                var receipt = new CaptureOperationResult(changed, running, CaptureOperationFailure.None,
                    model.CaptureCatalog.DescribeTarget(target).CaptureEpoch);
                AfterCommit?.Invoke();
                return Task.FromResult(receipt);
            }
        }
    }
}
