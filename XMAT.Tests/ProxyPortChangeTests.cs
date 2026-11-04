// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using XMAT.Models;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class ProxyPortChangeTests
    {
        private readonly CaptureUiApplicationFixture _application;
        public ProxyPortChangeTests(CaptureUiApplicationFixture application) => _application = application;

        [Fact]
        public void Later_PreservesCurrentHistoryAndBothReservations()
        {
            _application.Run(window =>
            {
                var pool = new ProxyPortPool();
                var controller = Controller(pool, 9000);
                controller.RestoreCurrentPort(false);
                var record = CaptureTimelineTestData.Create();
                controller.ProxyConnections.Add(record);
                JsonElement saved = default;
                controller.QueuePortChange(65535, () => saved = JsonSerializer.SerializeToElement(controller.Parameters));
                Assert.Equal(9000, controller.Port);
                Assert.Equal(65535, controller.PendingPort);
                Assert.False(controller.IsRunning);
                Assert.Same(record, Assert.Single(controller.ProxyConnections));
                Assert.Equal(9000, saved.GetProperty("Port").GetInt32());
                Assert.Equal(65535, saved.GetProperty("PendingPort").GetInt32());
                Assert.Throws<InvalidOperationException>(() => pool.ReservePort(9000, "other"));
                Assert.Throws<InvalidOperationException>(() => pool.ReservePort(65535, "other"));
                controller.DiscardPortChange(() => { });
                using var freed = pool.ReservePort(65535, "other");
                Assert.False(controller.HasPendingPort);
                Assert.Throws<InvalidOperationException>(() => pool.ReservePort(9000, "other"));
            });
        }

        [Fact]
        public void SaveFailure_RestoresPendingStateAndDoesNotLeakNewReservation()
        {
            _application.Run(window =>
            {
                var pool = new ProxyPortPool();
                var controller = Controller(pool, 9000);
                controller.RestoreCurrentPort(false);
                controller.QueuePortChange(9001, () => { });
                Assert.Throws<IOException>(() =>
                    controller.QueuePortChange(9002, () => throw new IOException("save failed")));
                Assert.Equal(9001, controller.PendingPort);
                using var freed = pool.ReservePort(9002, "other");
                Assert.Throws<InvalidOperationException>(() => pool.ReservePort(9001, "other"));
                Assert.Throws<IOException>(() => controller.DiscardPortChange(() => throw new IOException("save failed")));
                Assert.Equal(9001, controller.PendingPort);
                Assert.Throws<InvalidOperationException>(() => pool.ReservePort(9001, "other"));
            });
        }

        [Fact]
        public void Restore_CurrentAssignmentsWinOverPendingAndFreshAllocations()
        {
            _application.Run(window =>
            {
                var pool = new ProxyPortPool();
                pool.Initialize(9000, 9002);
                var fresh = Controller(pool, 0);
                var first = Controller(pool, 9000, 9001);
                var later = Controller(pool, 9001);
                fresh.RestoreCurrentPort(true);
                first.RestoreCurrentPort(true);
                later.RestoreCurrentPort(true);
                first.RestorePendingPort();
                fresh.RestoreCurrentPort(false);
                Assert.Equal(9002, fresh.Port);
                Assert.Equal(9001, first.PendingPort);
                Assert.NotNull(first.PortStatus);
                Assert.Throws<InvalidOperationException>(() => pool.ReservePort(9001, "other"));
                first.DiscardPortChange(() => { });
                Assert.Throws<InvalidOperationException>(() => pool.ReservePort(9001, "still belongs to later"));
            });
        }

        [Fact]
        public void Restore_ProtectsPendingPortBeforeAllocatingForFreshContext()
        {
            _application.Run(window =>
            {
                var pool = new ProxyPortPool();
                pool.Initialize(9000, 9002);
                var saved = Controller(pool, 9000, 9001);
                var fresh = Controller(pool, 0);
                saved.RestoreCurrentPort(true);
                fresh.RestoreCurrentPort(true);
                saved.RestorePendingPort();
                fresh.RestoreCurrentPort(false);
                Assert.Equal(9002, fresh.Port);
                saved.QueuePortChange(9001, () => { });
                Assert.Throws<InvalidOperationException>(() => pool.ReservePort(9000, "other"));
                Assert.Throws<InvalidOperationException>(() => pool.ReservePort(9001, "other"));
            });
        }

        [Fact]
        public void DirectMutationAndRunningChanges_AreRejected()
        {
            _application.Run(window =>
            {
                var controller = Controller(new ProxyPortPool(), 9000);
                controller.RestoreCurrentPort(false);
                Assert.Throws<InvalidOperationException>(() => controller.Port = 9001);
                Assert.Throws<InvalidOperationException>(() =>
                    ((ICaptureMethodParameters)controller.Parameters).DeserializeFrom(
                        JsonSerializer.SerializeToElement(new { Port = 9001 })));
                controller.IsRunning = true;
                Assert.Throws<InvalidOperationException>(() => controller.QueuePortChange(9001, () => { }));
                controller.IsRunning = false;
                using var transition = controller.BeginDeviceTransition();
                Assert.Throws<InvalidOperationException>(() => controller.QueuePortChange(9001, () => { }));
            });
        }

        [Fact]
        public void Apply_BindsRealReplacementAndSavesBeforeEnablingWithoutLosingHistory()
        {
            _application.Run(window =>
            {
                using var oldSocket = new TcpListener(IPAddress.Loopback, 0);
                using var newSocket = new TcpListener(IPAddress.Loopback, 0);
                oldSocket.Start();
                newSocket.Start();
                var oldPort = (ushort)((IPEndPoint)oldSocket.LocalEndpoint).Port;
                var newPort = (ushort)((IPEndPoint)newSocket.LocalEndpoint).Port;
                oldSocket.Stop();
                newSocket.Stop();
                var pool = new ProxyPortPool();
                var device = new Device(new(ProxyConfigurationState.Enabled, new("127.0.0.1", oldPort)));
                var controller = new WebServiceDeviceCaptureController("loopback-change", DeviceType.LocalPC, false, pool, device)
                {
                    Port = oldPort
                };
                typeof(WebServiceDeviceCaptureController).GetField("_webProxy", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(controller, new WebServiceProxy());
                controller.RestoreCurrentPort(false);
                var record = CaptureTimelineTestData.Create();
                controller.ProxyConnections.Add(record);
                controller.QueuePortChange(newPort, () => { });
                var model = new CaptureAppModel();
                model.SelectedDeviceContext = new Context(controller);
                var saved = false;
                device.OnEnable = () =>
                {
                    Assert.True(saved);
                    Assert.True(controller.IsRunning);
                    Assert.Equal(newPort, controller.Port);
                    using var connection = new TcpClient();
                    connection.Connect(IPAddress.Loopback, newPort);
                };
                try
                {
                    Complete(controller.ApplyPortChangeAsync("127.0.0.1", () =>
                    {
                        Assert.Equal(newPort, controller.Port);
                        Assert.Equal(0, controller.PendingPort);
                        Assert.Throws<InvalidOperationException>(() => pool.ReservePort(oldPort, "not yet retired"));
                        saved = true;
                    }));
                    Assert.True(saved);
                    Assert.False(controller.HasPendingPort);
                    Assert.Same(record, Assert.Single(controller.ProxyConnections));
                    Assert.Equal(Localization.GetLocalizedString("PROXY_CAPTURE_RUNNING"), model.StatusBarText1);
                    Assert.Equal(Localization.GetLocalizedString("PROXY_PORT_APPLIED"), model.StatusBarText3);
                    Complete(controller.StopProxyAsync());
                    Assert.Equal(Localization.GetLocalizedString("PROXY_CAPTURE_STOPPED"), model.StatusBarText1);
                    Assert.DoesNotContain("running", model.StatusBarText3, StringComparison.OrdinalIgnoreCase);
                    Assert.Equal(controller.PortDescription, model.StatusBarText2);
                    model.SelectedDeviceContext = null;
                    Assert.Throws<InvalidOperationException>(() => pool.ReservePort(newPort, "other"));
                    using var retired = pool.ReservePort(oldPort, "reusable only after disable/commit");
                }
                finally
                {
                    Complete(controller.CloseAsync());
                }
                using var released = pool.ReservePort(newPort, "closed");
            });
        }

        [Fact]
        public void Applying_DoesNotHoldGlobalGateOrAbandonTargetOnCancellation()
        {
            _application.Run(window =>
            {
                using var portSocket = new TcpListener(IPAddress.Loopback, 0);
                portSocket.Start();
                var port = (ushort)((IPEndPoint)portSocket.LocalEndpoint).Port;
                portSocket.Stop();
                var write = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var device = new Device(new(ProxyConfigurationState.Enabled, new("127.0.0.1", 9000)))
                {
                    DisableWrite = write.Task
                };
                var pool = new ProxyPortPool();
                var controller = new WebServiceDeviceCaptureController("cancel-change", DeviceType.LocalPC, false, pool, device)
                {
                    Port = 9000
                };
                typeof(WebServiceDeviceCaptureController).GetField("_webProxy", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(controller, new WebServiceProxy());
                controller.RestoreCurrentPort(false);
                controller.QueuePortChange(port, () => { });
                var applying = controller.ApplyPortChangeAsync("127.0.0.1", () => throw new InvalidOperationException("must not commit"));
                PumpUntil(() => device.DisableStarted);
                Assert.True(controller.IsTransitioning);
                Assert.True(CaptureAppModel.AppModel.TryBeginCaptureDataOperation(out var otherOperation));
                otherOperation.Dispose();
                controller.CancelPortChange();
                Assert.False(applying.IsCompleted);
                Assert.Throws<InvalidOperationException>(() => ProxyDeviceOperations.AcquireTarget("LocalPC"));
                write.SetResult();
                PumpUntil(() => applying.IsCompleted);
                Assert.ThrowsAny<OperationCanceledException>(() => applying.GetAwaiter().GetResult());
                Assert.Equal(9000, controller.Port);
                Assert.Equal(port, controller.PendingPort);
                Assert.False(controller.IsRunning);
                Assert.Throws<InvalidOperationException>(() => pool.ReservePort(9000, "unsafe reuse"));
                using var target = ProxyDeviceOperations.AcquireTarget("LocalPC");
                Complete(controller.CloseAsync());
            });
        }

        [Fact]
        public void DisableCommand_IsAvailableWhileStoppedButNotDuringTransitionOrForImports()
        {
            _application.Run(window =>
            {
                var controller = Controller(new ProxyPortPool(), 9000);
                var context = new Context(controller);
                var view = new CaptureConnectionView { DataContext = context };
                ((DataGrid)view.FindName("ConnectionList")).IsSynchronizedWithCurrentItem = false;
                WpfTestHost.Show(window, view);
                Assert.True(WebServiceProxyCommands.DisableDevice.CanExecute(null, view));
                Assert.True(WebServiceProxyCommands.ChangePort.CanExecute(null, view));
                using (controller.BeginDeviceTransition())
                {
                    Assert.False(WebServiceProxyCommands.DisableDevice.CanExecute(null, view));
                    Assert.False(WebServiceProxyCommands.ChangePort.CanExecute(null, view));
                }
                context.IsReadOnly = true;
                Assert.False(WebServiceProxyCommands.DisableDevice.CanExecute(null, view));
                Assert.False(WebServiceProxyCommands.ChangePort.CanExecute(null, view));
                view.DataContext = null;
            });
        }

        [Fact]
        public void SettingsReplacement_IsAtomicAndCleansItsTemporaryFileOnFailure()
        {
            var directory = Path.Join(Path.GetTempPath(), "XMAT-settings-test-" + Guid.NewGuid().ToString("N"));
            var path = Path.Join(directory, "settings.json");
            Directory.CreateDirectory(directory);
            try
            {
                CaptureAppSettings.WriteSettingsFile(path, "old");
                File.SetAttributes(path, FileAttributes.ReadOnly);
                Assert.Throws<UnauthorizedAccessException>(() => CaptureAppSettings.WriteSettingsFile(path, "new"));
                Assert.Equal("old", File.ReadAllText(path));
                Assert.Single(Directory.GetFiles(directory));
                File.SetAttributes(path, FileAttributes.Normal);
                CaptureAppSettings.WriteSettingsFile(path, "new");
                Assert.Equal("new", File.ReadAllText(path));
                Assert.Single(Directory.GetFiles(directory));
            }
            finally
            {
                File.SetAttributes(path, FileAttributes.Normal);
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public void InvalidRestoredRange_BlocksAutomaticButNotRememberedOrExplicitPorts()
        {
            var pool = new ProxyPortPool();
            pool.RestoreRange(0, 65535);
            using var remembered = pool.ReservePort(9000, "remembered");
            using var manual = pool.ReservePort(65535, "manual");
            Assert.Throws<InvalidOperationException>(() => pool.ObtainPort("fresh"));
            pool.Initialize(9000, 9002);
            using var next = pool.ObtainPort("fresh");
            Assert.Equal(9001, next.Port);
        }

        [Theory]
        [InlineData("0", "9000")]
        [InlineData("9000", "8999")]
        [InlineData("65536", "65536")]
        public void Preferences_RejectInvalidRawRangeBeforeSaving(string first, string last)
        {
            _application.Run(window =>
            {
                var preferences = new XMAT.WebServiceCapture.Models.PreferencesModel();
                var options = new CaptureOptionsFull { DataContext = preferences };
                WpfTestHost.Show(window, options);
                EditPortRange(options, first, last);
                Assert.Throws<ArgumentException>(options.CommitPortRange);
                Assert.Equal(ProxyPortPool.DefaultFirstPort, preferences.FirstPort);
                Assert.Equal(ProxyPortPool.DefaultLastPort, preferences.LastPort);
                preferences.Serialized();
            });
        }

        [Theory]
        [InlineData("9000", "8889")]
        [InlineData("9000", "9010")]
        public void Preferences_AbandonedPortEdits_DoNotChangeLiveRange(string first, string last)
        {
            _application.Run(window =>
            {
                var preferences = new XMAT.WebServiceCapture.Models.PreferencesModel();
                var options = new CaptureOptionsFull { DataContext = preferences };
                WpfTestHost.Show(window, options);
                EditPortRange(options, first, last);
                window.Content = null;
                WpfTestHost.Pump();

                Assert.Equal(ProxyPortPool.DefaultFirstPort, preferences.FirstPort);
                Assert.Equal(ProxyPortPool.DefaultLastPort, preferences.LastPort);
                preferences.Serialized();

                var reopened = new CaptureOptionsFull { DataContext = preferences };
                WpfTestHost.Show(window, reopened);
                Assert.Equal(preferences.FirstPort.ToString(), ((TextBox)reopened.FindName("StartingPort")).Text);
                Assert.Equal(preferences.LastPort.ToString(), ((TextBox)reopened.FindName("EndingPort")).Text);
            });
        }

        [Fact]
        public void Preferences_ValidPortDraft_IsCommittedOnlyOnSave()
        {
            _application.Run(window =>
            {
                var preferences = new XMAT.WebServiceCapture.Models.PreferencesModel();
                var options = new CaptureOptionsFull { DataContext = preferences };
                WpfTestHost.Show(window, options);
                EditPortRange(options, "9000", "9010");
                Assert.Equal(ProxyPortPool.DefaultFirstPort, preferences.FirstPort);
                Assert.Equal(ProxyPortPool.DefaultLastPort, preferences.LastPort);

                options.CommitPortRange();

                Assert.Equal(9000, preferences.FirstPort);
                Assert.Equal(9010, preferences.LastPort);
            });
        }

        [Theory]
        [InlineData(DeviceType.XboxConsole, false, false)]
        [InlineData(DeviceType.XboxConsole, true, false)]
        [InlineData(DeviceType.XboxConsole, false, true)]
        public void DisableProxying_UpdatesSelectedStatusWithoutFalseConfirmation(
            DeviceType deviceType, bool alreadyDisabled, bool fails)
        {
            _application.Run(window =>
            {
                var device = new Device(alreadyDisabled
                    ? new(ProxyConfigurationState.Disabled)
                    : new(ProxyConfigurationState.Enabled, new("127.0.0.1", 9000)))
                {
                    DisableFailure = fails ? new IOException("disable failed") : null
                };
                var controller = new WebServiceDeviceCaptureController(
                    "127.0.0.1", deviceType, false, new ProxyPortPool(), device)
                { Port = 9000 };
                var model = new CaptureAppModel { SelectedDeviceContext = new Context(controller) };
                var disabling = controller.DisableProxying(_ => true);
                if (fails)
                {
                    Assert.Throws<IOException>(() => Complete(disabling));
                }
                else
                {
                    Complete(disabling);
                    Assert.True(disabling.GetAwaiter().GetResult());
                }

                var expected = fails ? "PROXY_DISABLE_FAILED" :
                    alreadyDisabled ? "PROXY_DISABLED" : "PROXY_DISABLE_REQUESTED";
                Assert.Equal(Localization.GetLocalizedString(expected), controller.PortStatus);
                Assert.Equal(controller.PortStatus, model.StatusBarText3);
                Assert.Equal(!alreadyDisabled, device.DisableStarted);
                Assert.Equal(1, device.ReadCount);
                model.SelectedDeviceContext = null;
            });
        }

        [Fact]
        public void DisableProxying_LocalPc_WaitsForRecoveryBeforeReportingConfirmed()
        {
            _application.Run(window =>
            {
                var recovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var device = new Device(new(ProxyConfigurationState.Enabled, new("127.0.0.1", 9000)))
                {
                    DisableWrite = recovery.Task
                };
                var controller = new WebServiceDeviceCaptureController(
                    "local", DeviceType.LocalPC, false, new ProxyPortPool(), device)
                { Port = 9000 };
                var model = new CaptureAppModel { SelectedDeviceContext = new Context(controller) };
                var disabling = controller.DisableProxying(_ =>
                    throw new InvalidOperationException("A PC proxy must not prompt to restart."));
                try
                {
                    Assert.True(device.DisableStarted);
                    Assert.False(disabling.IsCompleted);
                    Assert.NotEqual(Localization.GetLocalizedString("PROXY_DISABLE_CONFIRMED"), model.StatusBarText3);
                    Assert.Equal(0, device.ReadCount);
                    recovery.SetResult();
                    Complete(disabling);

                    Assert.True(disabling.GetAwaiter().GetResult());
                    Assert.Equal(Localization.GetLocalizedString("PROXY_DISABLE_CONFIRMED"), controller.PortStatus);
                    Assert.Equal(controller.PortStatus, model.StatusBarText3);
                    Assert.Equal(0, device.ReadCount);
                }
                finally
                {
                    recovery.TrySetResult();
                    Complete(disabling);
                    model.SelectedDeviceContext = null;
                }
            });
        }

        [Fact]
        public void DisableProxying_LocalPc_RecoveryFailureDoesNotReportSuccess()
        {
            _application.Run(window =>
            {
                var failure = new IOException("recovery failed");
                var device = new Device(new(ProxyConfigurationState.Enabled, new("127.0.0.1", 9000)))
                {
                    DisableFailure = failure
                };
                var controller = new WebServiceDeviceCaptureController(
                    "local", DeviceType.LocalPC, false, new ProxyPortPool(), device)
                { Port = 9000 };
                var exception = Assert.Throws<IOException>(() => Complete(controller.DisableProxying(_ =>
                    throw new InvalidOperationException("A PC proxy must not prompt to restart."))));

                Assert.Same(failure, exception);
                Assert.True(device.DisableStarted);
                Assert.Equal(0, device.ReadCount);
                Assert.Equal(Localization.GetLocalizedString("PROXY_DISABLE_FAILED"), controller.PortStatus);
            });
        }

        [Fact]
        public void DisableProxying_ForeignXboxEndpoint_RequiresExplicitConfirmation()
        {
            _application.Run(window =>
            {
                var endpoint = new ProxyEndpoint("other.example", 8080);
                var device = new Device(new(ProxyConfigurationState.Enabled, endpoint));
                var controller = new WebServiceDeviceCaptureController(
                    "127.0.0.1", DeviceType.XboxConsole, false, new ProxyPortPool(), device) { Port = 9000 };
                string prompt = null;
                var disabling = controller.DisableProxying(description =>
                {
                    prompt = description;
                    return false;
                });
                Complete(disabling);

                Assert.False(disabling.GetAwaiter().GetResult());
                Assert.Equal(Localization.GetLocalizedString("PROXY_DISABLE_FOREIGN_CONFIRM", endpoint), prompt);
                Assert.False(device.DisableStarted);
            });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void DisableProxying_UnverifiableXboxEndpoint_AllowsExplicitRecovery(bool confirmed)
        {
            _application.Run(window =>
            {
                var device = new Device(new(ProxyConfigurationState.Unknown, Detail: "malformed proxy setting"));
                var controller = new WebServiceDeviceCaptureController(
                    "127.0.0.1", DeviceType.XboxConsole, false, new ProxyPortPool(), device) { Port = 9000 };
                string prompt = null;
                var disabling = controller.DisableProxying(description =>
                {
                    prompt = description;
                    return confirmed;
                });
                Complete(disabling);

                Assert.Equal(confirmed, disabling.GetAwaiter().GetResult());
                Assert.Equal(Localization.GetLocalizedString("PROXY_DISABLE_UNKNOWN_CONFIRM"), prompt);
                Assert.Equal(confirmed, device.DisableStarted);
                Assert.Equal(confirmed ? Localization.GetLocalizedString("PROXY_DISABLE_REQUESTED") : null,
                    controller.PortStatus);
            });
        }

        [Fact]
        public void DisableProxying_UnreachableXbox_DoesNotWrite()
        {
            _application.Run(window =>
            {
                var device = new Device(new(ProxyConfigurationState.Unreachable));
                var controller = new WebServiceDeviceCaptureController(
                    "127.0.0.1", DeviceType.XboxConsole, false, new ProxyPortPool(), device) { Port = 9000 };

                Assert.Throws<InvalidOperationException>(() => Complete(controller.DisableProxying(_ =>
                    throw new InvalidOperationException("An unavailable device must not prompt to disable."))));

                Assert.False(device.DisableStarted);
                Assert.Equal(Localization.GetLocalizedString("PROXY_DISABLE_FAILED"), controller.PortStatus);
            });
        }

        [Fact]
        public void Tests_UseIsolatedSettingsAndLogDirectory()
        {
            Assert.StartsWith("XMAT-UI-Tests-", PublicUtilities.AppName);
            Assert.EndsWith(PublicUtilities.AppName, PublicUtilities.StorageDirectoryPath);
        }

        [Fact]
        public void ReadOnlyContexts_NeverReserveOrAcceptPendingPorts()
        {
            _application.Run(window =>
            {
                var pool = new ProxyPortPool();
                var controller = new WebServiceDeviceCaptureController("import", DeviceType.XboxConsole, true, pool)
                {
                    Port = 9000
                };
                controller.RestoreCurrentPort(false);
                Assert.Throws<InvalidOperationException>(() => controller.QueuePortChange(9001, () => { }));
                using var unclaimed = pool.ReservePort(9000, "writable");
            });
        }

        [Theory]
        [InlineData("1", true)]
        [InlineData("65535", true)]
        [InlineData("0", false)]
        [InlineData("65536", false)]
        [InlineData("-1", false)]
        [InlineData("80.5", false)]
        [InlineData("", false)]
        [InlineData(" 8080", false)]
        public void Dialog_ValidatesExactManualPortRange(string value, bool valid) =>
            Assert.Equal(valid, ProxyPortChangeWindow.TryParsePort(value, out _));

        [Fact]
        public void Dialog_HasExplicitChoicesAndThemeAwareBackground()
        {
            _application.Run(window =>
            {
                var dialog = new ProxyPortChangeWindow(9000, 9001, DeviceType.XboxConsole);
                Assert.Equal("9001", ((TextBox)dialog.FindName("NewPort")).Text);
                Assert.NotNull(dialog.FindName("ApplyButton"));
                Assert.NotNull(dialog.FindName("LaterButton"));
                Assert.NotNull(dialog.FindName("CancelButton"));
                Assert.Equal(Application.Current.FindResource("WindowBackgroundBrush"), dialog.Background);
                dialog.Close();
            });
        }

        [Theory]
        [InlineData(DeviceType.XboxConsole, "CONSOLE.example.", "console.EXAMPLE", true)]
        [InlineData(DeviceType.LocalPC, "Localized name", "Other name", true)]
        [InlineData(DeviceType.XboxConsole, "192.0.2.1", "192.0.2.2", false)]
        [InlineData(DeviceType.GenericProxyDevice, "Generic Device", "Remote Device", false)]
        public void ManagedLookup_PreservesIndependentDeviceKinds(
            DeviceType type, string first, string second, bool expected) =>
            Assert.Equal(expected, CaptureAppModel.SameProxyDevice(type, first, second));

        private static WebServiceDeviceCaptureController Controller(
            ProxyPortPool pool, ushort port, ushort pending = 0)
        {
            var controller = new WebServiceDeviceCaptureController("test", DeviceType.XboxConsole, false, pool);
            ((ICaptureMethodParameters)controller.Parameters).DeserializeFrom(
                JsonSerializer.SerializeToElement(new { Port = port, PendingPort = pending }));
            return controller;
        }

        private static void Complete(Task task)
        {
            PumpUntil(() => task.IsCompleted);
            task.GetAwaiter().GetResult();
        }

        private static void EditPortRange(CaptureOptionsFull options, string first, string last)
        {
            var starting = (TextBox)options.FindName("StartingPort");
            var ending = (TextBox)options.FindName("EndingPort");
            starting.SetCurrentValue(TextBox.TextProperty, first);
            starting.GetBindingExpression(TextBox.TextProperty).UpdateSource();
            ending.SetCurrentValue(TextBox.TextProperty, last);
            ending.GetBindingExpression(TextBox.TextProperty).UpdateSource();
            WpfTestHost.Pump();
        }

        private static void PumpUntil(Func<bool> predicate)
        {
            var watch = Stopwatch.StartNew();
            while (!predicate() && watch.Elapsed < TimeSpan.FromSeconds(20))
            {
                WpfTestHost.Pump();
                Thread.Sleep(1);
            }
            Assert.True(predicate(), "The asynchronous controller operation did not complete.");
        }

        private sealed class Device : IProxyDeviceConfiguration
        {
            private ProxyConfiguration _configuration;
            internal Task DisableWrite { get; set; } = Task.CompletedTask;
            internal bool DisableStarted { get; private set; }
            internal Exception DisableFailure { get; set; }
            internal Action OnEnable { get; set; }
            internal int ReadCount { get; private set; }
            internal Device(ProxyConfiguration configuration) => _configuration = configuration;
            public Task<ProxyConfiguration> ReadAsync()
            {
                ReadCount++;
                return Task.FromResult(_configuration);
            }
            public async Task DisableAsync()
            {
                DisableStarted = true;
                await DisableWrite;
                if (DisableFailure != null)
                {
                    throw DisableFailure;
                }
                _configuration = new(ProxyConfigurationState.Disabled);
            }
            public Task EnableAsync(ProxyEndpoint endpoint)
            {
                OnEnable?.Invoke();
                _configuration = new(ProxyConfigurationState.Enabled, endpoint);
                return Task.CompletedTask;
            }
        }

        private sealed class Context : ICaptureDeviceContext
        {
            public DeviceType DeviceType => DeviceType.XboxConsole;
            public CaptureType CaptureType => CaptureType.WebProxy;
            public string DeviceName => "test";
            public bool IsSelected { get; set; }
            public bool IsReadOnly { get; set; }
            public Color DeviceColor { get; set; }
            public IDeviceCaptureController CaptureController { get; }
            internal Context(IDeviceCaptureController controller) => CaptureController = controller;
        }
    }
}
