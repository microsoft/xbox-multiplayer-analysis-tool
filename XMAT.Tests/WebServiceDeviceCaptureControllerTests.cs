// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;
using XMAT.DataAbstractionLayer;
using XMAT.Operations.Analysis;
using XMAT.Operations.Captures;
using XMAT.Operations.Export;
using XMAT.Operations.Processes;
using XMAT.SharedInterfaces;
using XMAT.Tests.Proxy;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using XMAT.WebServiceCapture.Proxy;
using static XboxClient.XboxClientConnection;

namespace XMAT.Tests
{
    [Collection(IsolatedProxyTests.Name)]
    public class WebServiceDeviceCaptureControllerTests
    {
        [Fact]
        public async Task CanCloseAsync_WithPromptDisabled_SkipsProxyCheckAndPrompt()
        {
            var preferences = new PreferencesModel { PromptToDisableOnClose = false };
            var controller = CreateController(preferences);
            var operations = new CloseOperations();

            Assert.True(await operations.CanCloseAsync(controller));
            Assert.Equal(0, operations.ProxyCheckCount);
            Assert.Equal(0, operations.PromptCount);
            Assert.Equal(0, operations.DisableCount);
        }

        [Fact]
        public async Task CanCloseAsync_WithReadOnlyCapture_SkipsProxyCheckAndPrompt()
        {
            var controller = CreateController(new PreferencesModel(), readOnly: true);
            var operations = new CloseOperations();

            Assert.True(await operations.CanCloseAsync(controller));
            Assert.Equal(0, operations.ProxyCheckCount);
            Assert.Equal(0, operations.PromptCount);
            Assert.Equal(0, operations.DisableCount);
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public async Task CanCloseAsync_UsesCurrentPreferenceForExistingController(bool initialValue, bool updatedValue)
        {
            var preferences = new PreferencesModel { PromptToDisableOnClose = initialValue };
            var controller = CreateController(preferences);
            var operations = new CloseOperations();

            preferences.PromptToDisableOnClose = updatedValue;

            Assert.True(await operations.CanCloseAsync(controller));
            Assert.Equal(updatedValue ? 1 : 0, operations.ProxyCheckCount);
            Assert.Equal(updatedValue ? 1 : 0, operations.PromptCount);
            Assert.Equal(0, operations.DisableCount);
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public async Task CanCloseAsync_IgnoresLegacyDevicePreference(bool globalValue, bool legacyValue)
        {
            var preferences = new PreferencesModel { PromptToDisableOnClose = globalValue };
            var controller = CreateController(preferences);
            var serializedParameters = JsonSerializer.SerializeToElement(new
            {
                Port = (ushort)8080,
                PromptToDisableOnClose = legacyValue
            });
            ((ICaptureMethodParameters)controller.Parameters).DeserializeFrom(serializedParameters);
            var operations = new CloseOperations();

            Assert.True(await operations.CanCloseAsync(controller));
            Assert.Equal((ushort)8080, controller.Port);
            Assert.Equal(globalValue ? 1 : 0, operations.PromptCount);
            Assert.Equal(0, operations.DisableCount);
        }

        [Theory]
        [InlineData(EProxyEnabledCheckResult.ProxyDisabled)]
        [InlineData(EProxyEnabledCheckResult.ConsoleUnreachable)]
        [InlineData(EProxyEnabledCheckResult.DeviceIsXbox_NoGDKX)]
        [InlineData(EProxyEnabledCheckResult.ProxyingGenericDevice)]
        public async Task CanCloseAsync_WithoutEnabledProxy_DoesNotPrompt(EProxyEnabledCheckResult proxyState)
        {
            var controller = CreateController(new PreferencesModel());
            var operations = new CloseOperations { ProxyState = proxyState };

            Assert.True(await operations.CanCloseAsync(controller));
            Assert.Equal(1, operations.ProxyCheckCount);
            Assert.Equal(0, operations.PromptCount);
            Assert.Equal(0, operations.DisableCount);
        }

        [Fact]
        public async Task CanCloseAsync_WhenConfirmed_DisablesProxy()
        {
            var controller = CreateController(new PreferencesModel());
            var operations = new CloseOperations { Confirmation = true };

            Assert.True(await operations.CanCloseAsync(controller));
            Assert.Equal(1, operations.ProxyCheckCount);
            Assert.Equal(1, operations.PromptCount);
            Assert.Equal(1, operations.DisableCount);
        }

        [Theory]
        [InlineData(DeviceType.LocalPC, "foreign.example", 8080)]
        [InlineData(DeviceType.LocalPC, "proxy.example", 8081)]
        [InlineData(DeviceType.XboxConsole, "foreign.example", 8080)]
        [InlineData(DeviceType.XboxConsole, "proxy.example", 8081)]
        public async Task CanCloseAsync_ForeignEndpoint_DoesNotPromptOrDisable(
            DeviceType deviceType, string host, int port)
        {
            var controller = CreateController(new PreferencesModel(), deviceType: deviceType,
                configuration: new(ProxyConfigurationState.Enabled, new(host, (ushort)port)));
            var operations = new CloseOperations { Confirmation = true };

            Assert.True(await operations.CanCloseAsync(controller));

            Assert.Equal(1, operations.ProxyCheckCount);
            Assert.Equal(0, operations.PromptCount);
            Assert.Equal(0, operations.DisableCount);
        }

        [Theory]
        [InlineData((int)ProxyConfigurationState.Disabled)]
        [InlineData((int)ProxyConfigurationState.Unknown)]
        [InlineData((int)ProxyConfigurationState.Unreachable)]
        public async Task CanCloseAsync_EndpointNoLongerKnownEnabled_DoesNotPromptOrDisable(int state)
        {
            var controller = CreateController(new PreferencesModel(),
                configuration: new((ProxyConfigurationState)state));
            var operations = new CloseOperations { Confirmation = true };

            Assert.True(await operations.CanCloseAsync(controller));

            Assert.Equal(0, operations.PromptCount);
            Assert.Equal(0, operations.DisableCount);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(null)]
        public async Task CanCloseAsync_WhenDeclinedOrDismissed_LeavesProxyEnabled(bool? confirmation)
        {
            var controller = CreateController(new PreferencesModel());
            var operations = new CloseOperations { Confirmation = confirmation };

            Assert.True(await operations.CanCloseAsync(controller));
            Assert.Equal(1, operations.PromptCount);
            Assert.Equal(0, operations.DisableCount);
        }

        [Fact]
        public async Task CanCloseAsync_PreferenceEditedInPromptAppliesToNextClose()
        {
            var preferences = new PreferencesModel();
            var controller = CreateController(preferences);
            var operations = new CloseOperations();

            Assert.True(await controller.CanCloseAsync(
                operations.CheckProxyEnabledAsync,
                () =>
                {
                    preferences.PromptToDisableOnClose = false;
                    return false;
                },
                operations.DisableProxyAsync));

            Assert.True(await operations.CanCloseAsync(controller));
            Assert.Equal(1, operations.ProxyCheckCount);
            Assert.Equal(0, operations.PromptCount);
            Assert.Equal(0, operations.DisableCount);
        }

        [Fact]
        public async Task CanCloseAsync_WaitsForProxyToBeDisabled()
        {
            var controller = CreateController(new PreferencesModel());
            var operations = new CloseOperations { Confirmation = true };
            var disabled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var closeTask = controller.CanCloseAsync(
                operations.CheckProxyEnabledAsync,
                operations.ConfirmDisableProxy,
                () => disabled.Task);

            Assert.False(closeTask.IsCompleted);
            Assert.Equal(1, operations.PromptCount);

            disabled.SetResult();

            Assert.True(await closeTask);
        }

        [Fact]
        public async Task CanCloseAsync_PropagatesProxyCheckErrors()
        {
            var controller = CreateController(new PreferencesModel());
            var operations = new CloseOperations();
            var expected = new InvalidOperationException("Proxy check failed.");

            var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => controller.CanCloseAsync(
                () => Task.FromException<EProxyEnabledCheckResult>(expected),
                operations.ConfirmDisableProxy,
                operations.DisableProxyAsync));

            Assert.Same(expected, actual);
            Assert.Equal(0, operations.PromptCount);
            Assert.Equal(0, operations.DisableCount);
        }

        [Theory]
        [InlineData((int)ProxyConfigurationState.Disabled)]
        [InlineData((int)ProxyConfigurationState.Enabled)]
        [InlineData((int)ProxyConfigurationState.Unknown)]
        public async Task DisableProxying_LocalPcDelegatesEndpointClassificationToRecovery(
            int configurationState)
        {
            var state = (ProxyConfigurationState)configurationState;
            var configuration = state == ProxyConfigurationState.Enabled
                ? new ProxyConfiguration(
                    ProxyConfigurationState.Enabled,
                    new ProxyEndpoint("foreign.example", 3128))
                : new ProxyConfiguration(state);
            var device = new TestDeviceConfiguration(configuration);
            var controller = CreateController(
                new PreferencesModel(),
                deviceType: DeviceType.LocalPC,
                deviceConfiguration: device);

            Assert.True(await controller.DisableProxying(
                _ => throw new InvalidOperationException("Local PC must not prompt.")));

            Assert.Equal(0, device.ReadCount);
            Assert.Equal(0, device.ParameterlessDisableCount);
            Assert.Equal(1, device.EndpointDisableCount);
            Assert.Equal(new ProxyEndpoint("127.0.0.1", 8080), device.DisabledEndpoint);
            Assert.Equal(
                Localization.GetLocalizedString("PROXY_DISABLE_CONFIRMED"),
                controller.PortStatus);
        }

        [Fact]
        public async Task DisableProxying_LocalPcFailureSetsStatusAndRethrows()
        {
            var expected = new InvalidOperationException("Recovery disable failed.");
            var device = new TestDeviceConfiguration(
                new ProxyConfiguration(ProxyConfigurationState.Unknown))
            {
                EndpointDisableFailure = expected
            };
            var controller = CreateController(
                new PreferencesModel(),
                deviceType: DeviceType.LocalPC,
                deviceConfiguration: device);

            var actual = await Assert.ThrowsAsync<InvalidOperationException>(
                () => controller.DisableProxying(_ => true));

            Assert.Same(expected, actual);
            Assert.Equal(0, device.ReadCount);
            Assert.Equal(0, device.ParameterlessDisableCount);
            Assert.Equal(1, device.EndpointDisableCount);
            Assert.Equal(
                Localization.GetLocalizedString("PROXY_DISABLE_FAILED"),
                controller.PortStatus);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task EnableDeviceProxyingOrRollBackAsync_DeviceSetupFailure_StopsListener(bool throws)
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            ushort port = (ushort)((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            var controller = new WebServiceDeviceCaptureController("rollback-test", DeviceType.XboxConsole, false)
            {
                Port = port
            };
            var proxy = new WebServiceProxy();
            typeof(WebServiceDeviceCaptureController)
                .GetField("_webProxy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(controller, proxy);

            await controller.StartProxyAsync(false);
            Assert.True(await CanConnectAsync(port));

            Func<Task<bool>> enable = throws
                ? () => throw new InvalidOperationException("device setup failed")
                : () => Task.FromResult(false);
            if (throws)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => controller.EnableDeviceProxyingOrRollBackAsync(enable));
            }
            else
            {
                await controller.EnableDeviceProxyingOrRollBackAsync(enable);
            }

            Assert.False(await CanConnectAsync(port));
            Assert.False(controller.IsRunning);
            Assert.Equal(Localization.GetLocalizedString("PROXY_ENABLE_FAILED"), controller.PortStatus);
        }

        [Fact]
        public async Task EnableDeviceProxyingOrRollBackAsync_XboxFailureStopsWithoutProbe()
        {
            using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            ushort port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var device = new TestDeviceConfiguration(
                new ProxyConfiguration(ProxyConfigurationState.Unknown));
            var controller = new WebServiceDeviceCaptureController(
                "xbox-fallback", DeviceType.XboxConsole, false, new ProxyPortPool(), device)
            {
                Port = port
            };
            SetProxy(controller, new WebServiceProxy());
            var expected = new IOException("Xbox enable failed.");
            try
            {
                await controller.StartProxyAsync(false);
                Assert.True(await CanConnectAsync(port));

                var actual = await Assert.ThrowsAsync<IOException>(
                    () => controller.EnableDeviceProxyingOrRollBackAsync(
                        () => Task.FromException<bool>(expected)));

                Assert.Same(expected, actual);
                Assert.Equal(0, device.ReadCount);
                Assert.False(controller.IsRunning);
                Assert.False(await CanConnectAsync(port));
                Assert.Equal(
                    Localization.GetLocalizedString("PROXY_ENABLE_FAILED"),
                    controller.PortStatus);
            }
            finally
            {
                if (controller.IsRunning)
                {
                    await controller.CloseAsync();
                }
            }
        }

        [Fact]
        public async Task EnableDeviceProxyingOrRollBackAsync_DeviceNeutralUncertaintyKeepsListener()
        {
            var device = new TestDeviceConfiguration(
                new ProxyConfiguration(ProxyConfigurationState.Unknown));
            var (controller, port) = await StartLocalPcControllerAsync(device);
            var inner = new IOException("Windows proxy write may have succeeded.");
            var expected = new ProxyActivationUncertainException(
                "Windows proxy setup is uncertain.",
                inner);
            try
            {
                var actual = await Assert.ThrowsAsync<ProxyActivationUncertainException>(
                    () => controller.EnableDeviceProxyingOrRollBackAsync(
                        () => throw expected));

                Assert.Same(expected, actual);
                Assert.Same(inner, actual.InnerException);
                Assert.True(controller.IsRunning);
                Assert.True(await CanConnectAsync(port));
                Assert.Equal(expected.Message, controller.PortStatus);
                Assert.Equal(0, device.ReadCount);
            }
            finally
            {
                await controller.CloseAsync();
            }
        }

        [Theory]
        [InlineData((int)ProxyConfigurationState.Disabled, null, true, null, null, false)]
        [InlineData((int)ProxyConfigurationState.Unknown, null, true, null, null, true)]
        [InlineData((int)ProxyConfigurationState.Unreachable, null, true, null, null, true)]
        [InlineData((int)ProxyConfigurationState.Enabled, null, true, null, null, true)]
        [InlineData((int)ProxyConfigurationState.Enabled, "127.0.0.1", true, null, null, true)]
        [InlineData((int)ProxyConfigurationState.Enabled, "192.0.2.10", true, null, "192.0.2.10", true)]
        [InlineData((int)ProxyConfigurationState.Enabled, "::ffff:192.0.2.10", true, null, "192.0.2.10", true)]
        [InlineData((int)ProxyConfigurationState.Enabled, "198.51.100.20", true, "198.51.100.20", null, true)]
        [InlineData((int)ProxyConfigurationState.Enabled, "unresolved.example", true, null, null, true)]
        [InlineData((int)ProxyConfigurationState.Enabled, "198.51.100.20", true, null, null, false)]
        [InlineData((int)ProxyConfigurationState.Enabled, "127.0.0.1", false, null, null, false)]
        public async Task EnableDeviceProxyingOrRollBackAsync_LocalPcClassifiesListenerUse(
            int stateValue,
            string host,
            bool samePort,
            string proxyHost,
            string localAddress,
            bool retainListener)
        {
            ProxyConfiguration configuration = (ProxyConfigurationState)stateValue ==
                ProxyConfigurationState.Enabled
                ? new ProxyConfiguration(
                    ProxyConfigurationState.Enabled,
                    host == null ? null : new ProxyEndpoint(host, 1))
                : new ProxyConfiguration((ProxyConfigurationState)stateValue);
            var device = new TestDeviceConfiguration(configuration);
            IEnumerable<IPAddress> Addresses() => localAddress == null
                ? Array.Empty<IPAddress>()
                : new[] { IPAddress.Parse(localAddress) };
            var (controller, port) = await StartLocalPcControllerAsync(
                device,
                Addresses,
                proxyHost);
            if (configuration.Endpoint != null)
            {
                configuration = configuration with
                {
                    Endpoint = configuration.Endpoint with
                    {
                        Port = samePort ? port : (ushort)(port == UInt16.MaxValue ? port - 1 : port + 1)
                    }
                };
                device.Configuration = configuration;
            }
            var expected = new InvalidOperationException("Local PC enable failed.");
            try
            {
                var actual = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => controller.EnableDeviceProxyingOrRollBackAsync(
                        () => Task.FromException<bool>(expected)));

                Assert.Same(expected, actual);
                Assert.Equal(1, device.ReadCount);
                Assert.Equal(retainListener, controller.IsRunning);
                Assert.Equal(retainListener, await CanConnectAsync(port));
                Assert.Equal(
                    Localization.GetLocalizedString(
                        retainListener
                            ? "PROXY_ENABLE_LISTENER_KEPT"
                            : "PROXY_ENABLE_FAILED"),
                    controller.PortStatus);
            }
            finally
            {
                if (controller.IsRunning)
                {
                    await controller.CloseAsync();
                }
            }
        }

        [Theory]
        [InlineData("timeout")]
        [InlineData("io")]
        [InlineData("ownership")]
        [InlineData("win32")]
        [InlineData("recovery")]
        [InlineData("false")]
        public async Task EnableDeviceProxyingOrRollBackAsync_LocalPcRetainsForRepresentativeFailures(
            string failureKind)
        {
            var device = new TestDeviceConfiguration(
                new ProxyConfiguration(ProxyConfigurationState.Unknown));
            var (controller, port) = await StartLocalPcControllerAsync(device);
            Exception expected = failureKind switch
            {
                "timeout" => new TimeoutException("Enable timed out."),
                "io" => new IOException("Settings could not be read."),
                "ownership" => new InvalidOperationException("Another process owns the lease."),
                "win32" => new Win32Exception(5, "Access denied."),
                "recovery" => new ProxyRecoveryException("Recovery guidance.", new IOException("Recovery failed.")),
                _ => null
            };
            try
            {
                if (expected == null)
                {
                    await controller.EnableDeviceProxyingOrRollBackAsync(
                        () => Task.FromResult(false));
                }
                else
                {
                    Exception actual = await Record.ExceptionAsync(
                        () => controller.EnableDeviceProxyingOrRollBackAsync(
                            () => Task.FromException<bool>(expected)));
                    Assert.Same(expected, actual);
                }

                Assert.Equal(1, device.ReadCount);
                Assert.True(controller.IsRunning);
                Assert.True(await CanConnectAsync(port));
                Assert.Equal(
                    Localization.GetLocalizedString("PROXY_ENABLE_LISTENER_KEPT"),
                    controller.PortStatus);
            }
            finally
            {
                await controller.CloseAsync();
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task EnableDeviceProxyingOrRollBackAsync_LocalPcProbeFailureRetainsListener(
            bool addressFailure)
        {
            var device = new TestDeviceConfiguration(
                new ProxyConfiguration(
                    ProxyConfigurationState.Enabled,
                    new ProxyEndpoint("198.51.100.20", 1)));
            if (!addressFailure)
            {
                device.ReadAsyncOverride = () =>
                    Task.FromException<ProxyConfiguration>(
                        new IOException("Probe read failed."));
            }
            IEnumerable<IPAddress> Addresses() => addressFailure
                ? throw new IOException("Address enumeration failed.")
                : Array.Empty<IPAddress>();
            var (controller, port) = await StartLocalPcControllerAsync(
                device,
                Addresses);
            device.Configuration = device.Configuration with
            {
                Endpoint = device.Configuration.Endpoint with { Port = port }
            };
            var expected = new IOException("Enable failed.");
            try
            {
                var actual = await Assert.ThrowsAsync<IOException>(
                    () => controller.EnableDeviceProxyingOrRollBackAsync(
                        () => Task.FromException<bool>(expected)));

                Assert.Same(expected, actual);
                Assert.Equal(1, device.ReadCount);
                Assert.True(controller.IsRunning);
                Assert.True(await CanConnectAsync(port));
                Assert.Equal(
                    Localization.GetLocalizedString("PROXY_ENABLE_LISTENER_KEPT"),
                    controller.PortStatus);
            }
            finally
            {
                await controller.CloseAsync();
            }
        }

        [Fact]
        public async Task EnableDeviceProxyingOrRollBackAsync_LocalPcProbeTimeoutIgnoresLateResult()
        {
            var probe = new TaskCompletionSource<ProxyConfiguration>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var device = new TestDeviceConfiguration(
                new ProxyConfiguration(ProxyConfigurationState.Unknown))
            {
                ReadAsyncOverride = () => probe.Task
            };
            var (controller, port) = await StartLocalPcControllerAsync(
                device,
                enableFailureProbeTimeout: TimeSpan.FromMilliseconds(100));
            var expected = new IOException("Enable failed.");
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var actual = await Assert.ThrowsAsync<IOException>(
                    () => controller.EnableDeviceProxyingOrRollBackAsync(
                        () => Task.FromException<bool>(expected)));
                stopwatch.Stop();

                Assert.Same(expected, actual);
                Assert.Equal(1, device.ReadCount);
                Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
                Assert.True(controller.IsRunning);
                Assert.True(await CanConnectAsync(port));
                Assert.Equal(
                    Localization.GetLocalizedString("PROXY_ENABLE_LISTENER_KEPT"),
                    controller.PortStatus);

                probe.SetResult(new ProxyConfiguration(
                    ProxyConfigurationState.Disabled));
                await Task.Delay(200);

                Assert.True(controller.IsRunning);
                Assert.True(await CanConnectAsync(port));
                Assert.Equal(
                    Localization.GetLocalizedString("PROXY_ENABLE_LISTENER_KEPT"),
                    controller.PortStatus);
            }
            finally
            {
                await controller.CloseAsync();
            }
        }

        [Fact]
        public async Task EnableDeviceProxyingOrRollBackAsync_LocalPcAddressEnumerationTimeoutRetainsListener()
        {
            var addressStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseAddressEnumeration = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            IEnumerable<IPAddress> Addresses()
            {
                addressStarted.TrySetResult();
                releaseAddressEnumeration.Task.GetAwaiter().GetResult();
                return Array.Empty<IPAddress>();
            }
            var device = new TestDeviceConfiguration(
                new ProxyConfiguration(
                    ProxyConfigurationState.Enabled,
                    new ProxyEndpoint("198.51.100.20", 1)));
            var (controller, port) = await StartLocalPcControllerAsync(
                device,
                Addresses,
                enableFailureProbeTimeout: TimeSpan.FromMilliseconds(500));
            device.Configuration = device.Configuration with
            {
                Endpoint = device.Configuration.Endpoint with { Port = port }
            };
            var expected = new IOException("Enable failed.");
            try
            {
                var stopwatch = Stopwatch.StartNew();
                Task<Exception> failure = Record.ExceptionAsync(
                    () => controller.EnableDeviceProxyingOrRollBackAsync(
                        () => Task.FromException<bool>(expected)));
                await addressStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Exception actual = await failure.WaitAsync(TimeSpan.FromSeconds(2));
                stopwatch.Stop();

                Assert.Same(expected, actual);
                Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
                Assert.Equal(1, device.ReadCount);
                Assert.True(controller.IsRunning);
                Assert.True(await CanConnectAsync(port));
                Assert.Equal(
                    Localization.GetLocalizedString("PROXY_ENABLE_LISTENER_KEPT"),
                    controller.PortStatus);

                releaseAddressEnumeration.TrySetResult();
                await Task.Delay(200);

                Assert.True(controller.IsRunning);
                Assert.True(await CanConnectAsync(port));
                Assert.Equal(
                    Localization.GetLocalizedString("PROXY_ENABLE_LISTENER_KEPT"),
                    controller.PortStatus);
            }
            finally
            {
                releaseAddressEnumeration.TrySetResult();
                await controller.CloseAsync();
            }
        }

        [Fact]
        public void GetProxyOperationErrorMessage_PreservesRecoveryGuidance()
        {
            var inner = new IOException("Low-level failure.");

            Assert.Equal(
                "Activation guidance.",
                CaptureUtilities.GetProxyOperationErrorMessage(
                    new ProxyActivationUncertainException(
                        "Activation guidance.",
                        inner)));
            Assert.Equal(
                "Recovery guidance.",
                CaptureUtilities.GetProxyOperationErrorMessage(
                    new ProxyRecoveryException(
                        "Recovery guidance.",
                        inner)));
            Assert.Equal(
                inner.Message,
                CaptureUtilities.GetProxyOperationErrorMessage(
                    new InvalidOperationException(
                        "Generic outer message.",
                        inner)));
        }

        [Theory]
        [InlineData("certificate")]
        [InlineData("configuration")]
        [InlineData("reboot")]
        public async Task XboxSetupWriteFailure_RetainsListenerOnlyWhenDeviceMayUseIt(string phase)
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = (ushort)((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var controller = new WebServiceDeviceCaptureController(
                "xbox-write-failure", DeviceType.XboxConsole, false, new ProxyPortPool())
            {
                Port = port
            };
            SetProxy(controller, new WebServiceProxy());
            var expected = new IOException("native operation failed");
            byte[] copiedConfiguration = null;
            string temporaryFile = null;
            var rebootAttempted = false;
            try
            {
                await controller.StartProxyAsync(false);
                Task Enable() => controller.EnableDeviceProxyingOrRollBackAsync(() =>
                    CaptureUtilities.EnableXboxProxyAsync("127.0.0.1", port, () => true,
                        (source, destination) =>
                        {
                            if (destination.EndsWith("ProxyAddress.txt", StringComparison.Ordinal))
                            {
                                temporaryFile = source;
                                copiedConfiguration = File.ReadAllBytes(source);
                                if (phase == "configuration")
                                {
                                    throw new TargetInvocationException(expected);
                                }
                            }
                            else if (phase == "certificate")
                            {
                                throw new TargetInvocationException(expected);
                            }
                        },
                        () =>
                        {
                            rebootAttempted = true;
                            throw new TargetInvocationException(expected);
                        }));

                if (phase == "certificate")
                {
                    Assert.Same(expected, await Assert.ThrowsAsync<IOException>(Enable));
                    Assert.Null(copiedConfiguration);
                    Assert.False(controller.IsRunning);
                    Assert.False(await CanConnectAsync(port));
                    Assert.Equal(Localization.GetLocalizedString("PROXY_ENABLE_FAILED"), controller.PortStatus);
                }
                else
                {
                    var failure = await Assert.ThrowsAsync<ProxyActivationUncertainException>(Enable);
                    Assert.Same(expected, failure.InnerException);
                    Assert.Equal(CaptureUtilities.EncodeXboxProxyAddress("127.0.0.1", port), copiedConfiguration);
                    Assert.True(controller.IsRunning);
                    Assert.True(await CanConnectAsync(port));
                    Assert.Equal(failure.Message, controller.PortStatus);
                    Assert.False(File.Exists(temporaryFile));
                }
                Assert.Equal(phase == "reboot", rebootAttempted);
            }
            finally
            {
                await controller.CloseAsync();
            }
        }

        [Theory]
        [InlineData(false, false, 1)]
        [InlineData(true, true, 0)]
        public async Task EnableXboxProxyOrReportFailureAsync_ReportsFailureAsFalse(
            bool enabled,
            bool expected,
            int expectedFailureReports)
        {
            int reports = 0;

            var result = await WebServiceDeviceCaptureController.EnableXboxProxyOrReportFailureAsync(
                () => Task.FromResult(enabled),
                () => reports++);

            Assert.Equal(expected, result);
            Assert.Equal(expectedFailureReports, reports);
        }

        [Fact]
        public async Task XboxSetupFailure_RollsBackTheListener()
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            ushort port = (ushort)((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            var controller = new WebServiceDeviceCaptureController("xbox-rollback-test", DeviceType.XboxConsole, false)
            {
                Port = port
            };
            typeof(WebServiceDeviceCaptureController)
                .GetField("_webProxy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(controller, new WebServiceProxy());
            await controller.StartProxyAsync(false);
            Assert.True(await CanConnectAsync(port));

            await controller.EnableDeviceProxyingOrRollBackAsync(
                () => WebServiceDeviceCaptureController.EnableXboxProxyOrReportFailureAsync(
                    () => Task.FromResult(false),
                    () => { }));

            Assert.False(await CanConnectAsync(port));
        }

        [Fact]
        public async Task FailedStart_RaisesIsRunningSoTheToggleResets()
        {
            var controller = new WebServiceDeviceCaptureController("toggle-reset", DeviceType.XboxConsole, false);
            SetProxy(controller, new FailingProxy(new InvalidOperationException("bind failed"), stopThrows: false));
            var raised = new List<string>();
            controller.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StartProxyAsync(false));

            Assert.False(controller.IsRunning);
            Assert.Contains(nameof(WebServiceDeviceCaptureController.IsRunning), raised);
        }

        [Fact]
        public async Task FailedStart_WithFailingCleanup_RethrowsOriginalAndStaysRestartable()
        {
            var controller = new WebServiceDeviceCaptureController("cleanup-fails", DeviceType.XboxConsole, false);
            var startFailure = new InvalidOperationException("original startup failure");
            SetProxy(controller, new FailingProxy(startFailure, stopThrows: true));

            var first = await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StartProxyAsync(false));
            var second = await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StartProxyAsync(false));

            Assert.Same(startFailure, first);
            Assert.Same(startFailure, second);
        }

        [Theory]
        [InlineData(StopAttempt.AsyncFailure, false, true, typeof(InvalidOperationException))]
        [InlineData(StopAttempt.AsyncFailure, true, true, typeof(TimeoutException))]
        [InlineData(StopAttempt.SyncFailure, false, true, typeof(InvalidOperationException))]
        [InlineData(StopAttempt.Success, true, false, typeof(TimeoutException))]
        public async Task StopFailureLifecycle_IsRetryableAndIsolatesOtherRuns(
            StopAttempt attempt,
            bool callbackTimeout,
            bool expectRunAdmissionAfterFailure,
            Type expectedException)
        {
            var controller = CreateController(new PreferencesModel());
            var proxy = new FailingProxy(
                new InvalidOperationException("unused start failure"),
                stopThrows: false,
                stopFailures: attempt == StopAttempt.AsyncFailure ? 1 : 0,
                synchronousStopFailures: attempt == StopAttempt.SyncFailure ? 1 : 0);
            await using ClientProcessResolver resolver = CreateStopResolver();
            SetProxy(controller, proxy);
            controller.ProcessResolverOverride = resolver;
            if (callbackTimeout)
            {
                controller.CaptureCallbackDrainTimeoutOverride = TimeSpan.FromMilliseconds(10);
            }
            Guid run = Guid.NewGuid();
            Guid otherRun = Guid.NewGuid();
            resolver.SealRun(otherRun);
            CaptureRowRegistry registry = RegistryOf(controller);
            registry.StartRun(run);
            SetPrivateField(controller, "_activeRun", run);
            controller.IsRunning = true;
            IDisposable callback = null;
            if (callbackTimeout)
            {
                AsyncOperationTracker callbacks = CallbacksOf(controller);
                callbacks.StartAccepting();
                Assert.True(callbacks.TryBegin(out callback));
            }

            Exception failure;
            using (callback)
            {
                failure = await Record.ExceptionAsync(() => controller.StopProxyAsync());
            }

            Assert.NotNull(failure);
            Assert.Equal(expectedException, failure.GetType());
            Assert.Equal(expectRunAdmissionAfterFailure, await AdmitsAttributionAsync(resolver, run, 50310));
            Assert.False(await AdmitsAttributionAsync(resolver, otherRun, 50311));
            Assert.True(controller.IsRunning);
            Assert.Equal(run, ActiveRunOf(controller));

            await controller.StopProxyAsync();

            Assert.False(controller.IsRunning);
            Assert.Equal(Guid.Empty, ActiveRunOf(controller));
            Assert.True(await AdmitsAttributionAsync(resolver, run, 50312));
            Assert.False(await AdmitsAttributionAsync(resolver, otherRun, 50313));
            Assert.Equal(2, proxy.StopCount);
        }

        public enum StopAttempt
        {
            AsyncFailure,
            SyncFailure,
            Success,
        }

        private static ClientProcessResolver CreateStopResolver() =>
            new(
                new ProbeOwnerProvider(),
                new MissingIdentityProvider(),
                new AlwaysLocalAddressProvider(),
                new SystemClientProcessResolverClock(),
                ClientProcessResolverOptions
                    .Default with
                    {
                        DrainBudget = TimeSpan.FromSeconds(5),
                    });

        // Whether RUN admits attribution from a valid local connection; an admitted probe is drained before returning.
        private static async Task<bool> AdmitsAttributionAsync(ClientProcessResolver resolver, Guid run, int clientPort)
        {
            bool admitted = resolver.TryEnqueue(new ClientProcessContext(
                run,
                clientPort,
                new IPEndPoint(IPAddress.Loopback, clientPort),
                new IPEndPoint(IPAddress.Loopback, 8080),
                DateTimeOffset.UtcNow,
                Stopwatch.GetTimestamp()));
            await resolver.DrainAsync(run);
            return admitted;
        }

        private static CaptureRowRegistry RegistryOf(WebServiceDeviceCaptureController controller) =>
            (CaptureRowRegistry) typeof(WebServiceDeviceCaptureController).GetField("_captureRows",
            BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);

        private static AsyncOperationTracker CallbacksOf(WebServiceDeviceCaptureController controller) =>
            (AsyncOperationTracker) typeof(WebServiceDeviceCaptureController).GetField("_captureCallbacks",
            BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);

        private static Guid ActiveRunOf(WebServiceDeviceCaptureController controller) =>
            (Guid)typeof(WebServiceDeviceCaptureController).GetField("_activeRun", BindingFlags.Instance |
            BindingFlags.NonPublic).GetValue(controller);

        private static void SetProxy(WebServiceDeviceCaptureController controller, IWebServiceProxy proxy)
        {
            typeof(WebServiceDeviceCaptureController)
                .GetField("_webProxy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(controller, proxy);
        }

        // Owns the four AdmitsAttributionAsync probe ports, so probes never take the retry path.
        private sealed class ProbeOwnerProvider : ITcpOwnerTableProvider
        {
            public IReadOnlyList<TcpOwnerRow> Snapshot(System.Net.Sockets.AddressFamily addressFamily)
            {
                _ = addressFamily;
                return Enumerable.Range(50310, 4)
                    .Select(clientPort => new TcpOwnerRow(
                        new IPEndPoint(IPAddress.Loopback, clientPort),
                        new IPEndPoint(IPAddress.Loopback, 8080),
                        clientPort))
                    .ToArray();
            }
        }

        private sealed class MissingIdentityProvider : IProcessIdentityProvider
        {
            public ProcessIdentityQueryStatus Query(int processId, out ProcessIdentityData identity)
            {
                _ = processId;
                identity = null;
                return ProcessIdentityQueryStatus.Missing;
            }

            public IReadOnlyDictionary<int, ProcessIdentityData> Snapshot() => new Dictionary<int,
                    ProcessIdentityData>();
        }

        private sealed class AlwaysLocalAddressProvider : ILocalAddressProvider
        {
            public bool IsLocal(IPAddress address)
            {
                _ = address;
                return true;
            }
        }

        internal sealed class FailingProxy : IWebServiceProxy
        {
            private readonly Exception _startFailure;
            private int _remainingStopFailures;
            private int _remainingSynchronousStopFailures;

            public FailingProxy(
                Exception startFailure,
                bool stopThrows,
                int stopFailures = -1,
                int synchronousStopFailures = 0)
            {
                _startFailure = startFailure;
                _remainingStopFailures = stopFailures >= 0 ? stopFailures : stopThrows ? int.MaxValue : 0;
                _remainingSynchronousStopFailures = synchronousStopFailures;
            }

            internal int StopCount { get; private set; }

#pragma warning disable CS0067
            public event EventHandler<InitialConnectionEventArgs> ReceivedInitialConnection;
            public event EventHandler<SslConnectionRequestEventArgs> ReceivedSslConnectionRequest;
            public event EventHandler<SslConnectionCompletionEventArgs> CompletedSslConnectionRequest;
            public event EventHandler<ConnectionFailureEventArgs> FailedSslConnectionRequest;
            public event EventHandler<HttpRequestEventArgs> ReceivedWebRequest;
            public event EventHandler<HttpResponseEventArgs> ReceivedWebResponse;
            public event EventHandler<WebSocketOpenedEventArgs> WebSocketOpened;
            public event EventHandler<WebSocketMessageEventArgs> WebSocketMessage;
            public event EventHandler<WebSocketClosedEventArgs> WebSocketClosed;
            public event EventHandler<ConnectionClosedEventArgs> ConnectionClosed;
            public event EventHandler ProxyStopped;
#pragma warning restore CS0067

            public bool IsProxyEnabled => false;
            public BypassListModel BypassList { get; set; }
            public Func<SslConnectionRequestEventArgs, CancellationToken, Task<bool>> SslConnectionRequestPolicyAsync { get; set; }
            public Func<HttpRequestEventArgs, CancellationToken, Task<bool>> WebRequestPolicyAsync { get; set; }
            public Func<HttpResponseEventArgs, CancellationToken, Task<bool>> WebResponsePolicyAsync { get; set; }

            public Task StartProxyAsync(WebServiceProxyOptions options, CancellationToken cancellationToken = default)
                => Task.FromException(_startFailure);

            public Task StopProxyAsync(IProgress<WebProxyStopProgress> progress = null)
            {
                _ = progress;
                StopCount++;
                if (_remainingSynchronousStopFailures > 0)
                {
                    _remainingSynchronousStopFailures--;
                    throw new InvalidOperationException("synchronous cleanup failed");
                }
                if (_remainingStopFailures > 0)
                {
                    if (_remainingStopFailures != int.MaxValue)
                    {
                        _remainingStopFailures--;
                    }
                    return Task.FromException(new InvalidOperationException("cleanup failed"));
                }
                return Task.CompletedTask;
            }

            public void Reset()
            {
            }
        }

        private static async Task<bool> CanConnectAsync(ushort port)
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await tcp.ConnectAsync(System.Net.IPAddress.Loopback, port, cts.Token);
                return true;
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException)
            {
                return false;
            }
        }

        private static async Task<(WebServiceDeviceCaptureController Controller, ushort Port)>
            StartLocalPcControllerAsync(
                TestDeviceConfiguration device,
                Func<IEnumerable<IPAddress>> getClassifierIp4Addresses = null,
                string proxyHost = null,
                TimeSpan? enableFailureProbeTimeout = null)
        {
            using var listener = new System.Net.Sockets.TcpListener(
                IPAddress.Loopback,
                0);
            listener.Start();
            ushort port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var controller = new WebServiceDeviceCaptureController(
                "local-pc-fallback",
                DeviceType.LocalPC,
                false,
                new ProxyPortPool(),
                device,
                getClassifierIp4Addresses: getClassifierIp4Addresses ??
                    (() => Array.Empty<IPAddress>()),
                enableFailureProbeTimeout: enableFailureProbeTimeout)
            {
                Port = port
            };
            if (proxyHost != null)
            {
                ((ICaptureMethodParameters)controller.Parameters).DeserializeFrom(
                    JsonSerializer.SerializeToElement(new { ProxyHost = proxyHost }));
            }
            SetProxy(controller, new WebServiceProxy());
            await controller.StartProxyAsync(false);
            Assert.True(await CanConnectAsync(port));
            return (controller, port);
        }

        private static WebServiceDeviceCaptureController CreateController(
            PreferencesModel preferences, bool readOnly = false,
            DeviceType deviceType = DeviceType.XboxConsole, ProxyConfiguration configuration = null,
            TestDeviceConfiguration deviceConfiguration = null)
        {
            var device = deviceConfiguration ?? new TestDeviceConfiguration(configuration ??
                new(ProxyConfigurationState.Enabled, new("proxy.example", 8080)));
            var controller = new WebServiceDeviceCaptureController(
                "test-console", deviceType, readOnly, new ProxyPortPool(),
                device)
            {
                CaptureMethod = new TestCaptureMethod(preferences)
            };
            ((ICaptureMethodParameters)controller.Parameters).DeserializeFrom(
                JsonSerializer.SerializeToElement(new { Port = (ushort)8080, ProxyHost = "proxy.example" }));
            return controller;
        }

        private sealed class TestDeviceConfiguration(ProxyConfiguration configuration) : IProxyDeviceConfiguration
        {
            internal ProxyConfiguration Configuration { get; set; } = configuration;
            internal Func<Task<ProxyConfiguration>> ReadAsyncOverride { get; set; }
            internal int ReadCount { get; private set; }
            internal int ParameterlessDisableCount { get; private set; }
            internal int EndpointDisableCount { get; private set; }
            internal ProxyEndpoint DisabledEndpoint { get; private set; }
            internal Exception EndpointDisableFailure { get; init; }

            public Task<ProxyConfiguration> ReadAsync()
            {
                ReadCount++;
                return ReadAsyncOverride?.Invoke() ??
                    Task.FromResult(Configuration);
            }

            public Task DisableAsync()
            {
                ParameterlessDisableCount++;
                return Task.CompletedTask;
            }

            public Task DisableAsync(ProxyEndpoint previous)
            {
                EndpointDisableCount++;
                DisabledEndpoint = previous;
                return EndpointDisableFailure == null
                    ? Task.CompletedTask
                    : Task.FromException(EndpointDisableFailure);
            }

            public Task EnableAsync(ProxyEndpoint endpoint) => throw new NotSupportedException();
        }

        private sealed class TestCaptureMethod : ICaptureMethod
        {
            public ICaptureMethodParameters PreferencesModel { get; }

            public TestCaptureMethod(PreferencesModel preferences)
            {
                PreferencesModel = preferences;
            }

            public void Initialize(ICaptureAppModel appModel) => throw new NotSupportedException();
            public void Shutdown() => throw new NotSupportedException();
            public bool OwnsDataTable(string tableName) => throw new NotSupportedException();
        }

        private sealed class CloseOperations
        {
            public EProxyEnabledCheckResult ProxyState { get; set; } = EProxyEnabledCheckResult.ProxyEnabled;
            public bool? Confirmation { get; set; } = false;
            public int ProxyCheckCount { get; private set; }
            public int PromptCount { get; private set; }
            public int DisableCount { get; private set; }

            public Task<bool> CanCloseAsync(WebServiceDeviceCaptureController controller)
            {
                return controller.CanCloseAsync(CheckProxyEnabledAsync, ConfirmDisableProxy, DisableProxyAsync);
            }

            public Task<EProxyEnabledCheckResult> CheckProxyEnabledAsync()
            {
                ProxyCheckCount++;
                return Task.FromResult(ProxyState);
            }

            public bool? ConfirmDisableProxy()
            {
                PromptCount++;
                return Confirmation;
            }

            public Task DisableProxyAsync()
            {
                DisableCount++;
                return Task.CompletedTask;
            }
        }

        [Fact]
        public void GetCaptureRecords_UsesLoadedCaptureDataset()
        {
            const string deviceName = "Saved Capture";
            var record = CreateAnalyzableRecord(deviceName);
            var dataset = new TestDataset(record);
            var controller = new WebServiceDeviceCaptureController(
                deviceName,
                DeviceType.Unknown,
                true);

            controller.LoadCaptureDataset(dataset);

            var loadedRecord = Assert.Single(controller.GetCaptureRecords());
            dataset.RejectFilteredReads = true;
            using ICaptureExportSnapshot snapshot =
                controller.CreateExportSnapshot("d1", AnalysisLimits.Default);

            Assert.Same(record, loadedRecord);
            Assert.Equal(2, dataset.FilteredReadCount);
            Assert.Single(snapshot.Sessions);
        }

        [Fact]
        public void BeginTransition_TracksAndReleasesTransitionState()
        {
            var controller = new WebServiceDeviceCaptureController(
                "Test device",
                DeviceType.Unknown,
                true);

            Assert.True(controller.CanBeginTransition);

            using (controller.BeginTransition())
            {
                Assert.True(controller.IsTransitioning);
                Assert.False(controller.CanBeginTransition);
                Assert.Throws<InvalidOperationException>(() => controller.BeginTransition());
            }

            Assert.False(controller.IsTransitioning);
            Assert.True(controller.CanBeginTransition);

            controller.IsRunning = true;

            Assert.False(controller.CanBeginTransition);
            Assert.Throws<InvalidOperationException>(() => controller.BeginTransition());
            Assert.True(controller.CanBeginStopTransition);

            using (controller.BeginStopTransition())
            {
                Assert.True(controller.IsTransitioning);
                Assert.False(controller.CanBeginStopTransition);
                Assert.Throws<InvalidOperationException>(
                    () => controller.BeginStopTransition());
            }

            Assert.False(controller.IsTransitioning);
            Assert.True(controller.CanBeginStopTransition);
        }

        [Fact]
        public async Task StoppedController_RejectsConnectionsRequestsAndResponses()
        {
            var controller = CreateController(new PreferencesModel());
            Assert.False(await InvokeCapturePolicyAsync(
                controller,
                "WebProxy_ReceivedSslConnectionRequestAsync",
                new SslConnectionRequestEventArgs()));
            Assert.False(await InvokeCapturePolicyAsync(
                controller,
                "WebProxy_ReceivedWebRequestAsync",
                new HttpRequestEventArgs()));
            Assert.False(await InvokeCapturePolicyAsync(
                controller,
                "WebProxy_ReceivedWebResponseAsync",
                new HttpResponseEventArgs()));
        }

        [Fact]
        public void SetProcessFilter_EquivalentCanonicalValuesDoesNotNotifyAgain()
        {
            var controller = CreateController(new PreferencesModel());
            var first = new ProcessFilterSpec(ImmutableArray.Create("game.exe"), ImmutableArray.Create("svchost.exe"));
            var equivalent = new ProcessFilterSpec(
                ImmutableArray.Create("game.exe"),
                ImmutableArray.Create("svchost.exe"));
            var notifications = 0;
            controller.PropertyChanged +=
                (_, args) =>
                {
                    if (args.PropertyName == nameof(WebServiceDeviceCaptureController.ProcessFilter))
                    {
                        notifications++;
                    }
                };

            controller.SetProcessFilter(first);
            controller.SetProcessFilter(equivalent);

            Assert.Equal(1, notifications);
            Assert.Same(first, controller.ProcessFilter);
        }

        [Fact]
        public void ClearAllCaptures_PreservesFilterAndMonotonicRunNumbers()
        {
            var controller = CreateController(new PreferencesModel());
            var proxy = new WebServiceProxy();
            SetProxy(controller, proxy);
            proxy.Reset();
            int firstConnection = proxy.GetNextConnectionID();
            int firstRequest = proxy.GetNextRequestID();
            var filter = new ProcessFilterSpec(ImmutableArray.Create("game.exe"), ImmutableArray.Create("svchost.exe"));
            controller.SetProcessFilter(filter);

            controller.ClearAllCaptures();

            Assert.Same(filter, controller.ProcessFilter);
            Assert.Equal(firstConnection + 1, proxy.GetNextConnectionID());
            Assert.Equal(firstRequest + 1, proxy.GetNextRequestID());

            proxy.Reset();
            Assert.Equal(1000, proxy.GetNextConnectionID());
            Assert.Equal(1, proxy.GetNextRequestID());
        }

        [Fact]
        public async Task NaturalAttributionRunMismatch_PreservesPolicyUnresolvedRowAndActiveRun()
        {
            using var database = new SqLtDatabase();
            database.Initialize();
            IDataTable table = CreateConnectionsTable(database);
            var controller = CreateController(new PreferencesModel());
            SetPrivateField(controller, "_dataTable", table);
            AsyncOperationTracker callbacks = CallbacksOf(controller);
            CaptureRowRegistry registry = RegistryOf(controller);
            Guid activeRun = Guid.NewGuid();
            Guid staleRun = Guid.NewGuid();
            registry.StartRun(activeRun);
            SetPrivateField(controller, "_activeRun", activeRun);
            var process = new ClientProcessContext(
                staleRun,
                1003,
                new IPEndPoint(IPAddress.Loopback, 50303),
                new IPEndPoint(IPAddress.Loopback, 8080),
                DateTimeOffset.UtcNow,
                Stopwatch.GetTimestamp());
            var request =
                new ClientRequest
                {
                    RequestNumber = 4,
                    Version = "HTTP/1.1",
                    Scheme = "http",
                    Host = "example.test",
                    Port = 80,
                    Method = "GET",
                    Path = "/natural-run-mismatch",
                };
            callbacks.StartAccepting();
            try
            {
                bool accepted =
                    await InvokeCapturePolicyAsync(
                        controller,
                        "WebProxy_ReceivedWebRequestAsync",
                        new HttpRequestEventArgs
                        {
                            ConnectionID = 1003,
                            Timestamp = DateTime.Now,
                            Request = request,
                            ProcessContext = process,
                        });
                IDataRecord row = Assert.Single(table.Dataset.DataRecords);
                bool keyed = registry.TryGet(process, request.RequestNumber, out CaptureRowKey key);
                ClientProcessInfo recorded = ClientProcessRecordCodec.Read(row);

                Assert.True(
                    accepted &&
                        keyed &&
                        key.SourceId != Guid.Empty &&
                        recorded.Status == ClientProcessStatus.Unresolved &&
                        ActiveRunOf(controller) == activeRun,
                    $"accepted={accepted}; keyed={keyed}; key={key}; process={recorded}; current={ActiveRunOf(controller)}.");
            }
            finally
            {
                await callbacks.StopAcceptingAndWaitAsync();
            }
        }

        private static Task<bool> InvokeCapturePolicyAsync<TEventArgs>(
            WebServiceDeviceCaptureController controller,
            string methodName,
            TEventArgs args) where TEventArgs : EventArgs
        {
            var method = typeof(WebServiceDeviceCaptureController).GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            var policy = method.CreateDelegate<
                Func<TEventArgs, CancellationToken, Task<bool>>>(controller);
            return policy(args, CancellationToken.None);
        }

        [Fact]
        public void ClearAllCaptures_DoesNotUseActiveTableForLoadedDataset()
        {
            const string deviceName = "Saved Capture";
            var dataset = new TestDataset(CreateAnalyzableRecord(deviceName));
            var controller = new WebServiceDeviceCaptureController(
                deviceName,
                DeviceType.Unknown,
                true);
            controller.LoadCaptureDataset(dataset);

            controller.ClearAllCaptures();

            Assert.Empty(controller.ProxyConnections);
            Assert.Empty(controller.GetCaptureRecords());
        }

        [Fact]
        public void ExactRowLookup_DoesNotReturnTheNextRowAfterDeletion()
        {
            using var database = new SqLtDatabase();
            database.Initialize();
            IDataTable table = CreateConnectionsTable(database);
            long deleted = AddConnectionRow(table, requestNumber: 1, connectionId: 1000);
            _ = AddConnectionRow(table, requestNumber: 2, connectionId: 1001);
            table.RemoveRowsWhere(new FieldValue<long>(WebServiceCaptureMethod.FieldKey_RequestNumber, 1));
            var controller = CreateController(new PreferencesModel());
            SetPrivateField(controller, "_dataTable", table);

            IDataRecord record = (IDataRecord)typeof(WebServiceDeviceCaptureController).GetMethod("DataRecordFromRow",
                BindingFlags.Instance | BindingFlags.NonPublic).Invoke(controller, new object[] { deleted });

            Assert.Null(record);
        }

        [Fact]
        public void ConnectCompletion_MissingRowIsANormalNoOp()
        {
            using var database = new SqLtDatabase();
            database.Initialize();
            IDataTable table = CreateConnectionsTable(database);
            var controller = CreateController(new PreferencesModel());
            SetPrivateField(controller, "_dataTable", table);
            var callbacks = (AsyncOperationTracker)
                typeof(WebServiceDeviceCaptureController).GetField("_captureCallbacks", BindingFlags.Instance |
                BindingFlags.NonPublic).GetValue(controller);
            callbacks.StartAccepting();
            var completion =
                new SslConnectionCompletionEventArgs
                {
                    ConnectionID = 1000,
                    Request =
                        new ClientRequest
                        {
                            RequestNumber = 1,
                        },
                };

            Exception exception =
                Record.Exception(
                    () => typeof(
                            WebServiceDeviceCaptureController)
                        .GetMethod(
                            "WebProxy_CompletedSslConnectionRequest",
                            BindingFlags.Instance |
                            BindingFlags.NonPublic)
                        .Invoke(
                            controller,
                            new object[]
                            {
                                null,
                                completion,
                            }));

            Assert.Null(exception);
        }

        private static IDataRecord CreateAnalyzableRecord(string deviceName)
        {
            var requestTime = DateTime.UtcNow;
            return new TestDataRecord(
                1,
                new Dictionary<string, long>
                {
                    [WebServiceCaptureMethod.FieldKey_RequestNumber] = 1,
                    [WebServiceCaptureMethod.FieldKey_ConnectionId] = 1
                },
                new Dictionary<string, string>
                {
                    [WebServiceCaptureMethod.FieldKey_DeviceName] = deviceName,
                    [WebServiceCaptureMethod.FieldKey_RequestTimestamp] = requestTime.ToString("o"),
                    [WebServiceCaptureMethod.FieldKey_ResponseTimestamp] = requestTime.AddMilliseconds(100).ToString("o"),
                    [WebServiceCaptureMethod.FieldKey_RequestScheme] = "https",
                    [WebServiceCaptureMethod.FieldKey_RequestHost] = "profile.xboxlive.com",
                    [WebServiceCaptureMethod.FieldKey_RequestPort] = "443",
                    [WebServiceCaptureMethod.FieldKey_RequestMethod] = "GET",
                    [WebServiceCaptureMethod.FieldKey_RequestPath] = "/users/me",
                    [WebServiceCaptureMethod.FieldKey_RequestStatus] = "200",
                    [WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders] =
                        "GET /users/me HTTP/1.1\r\n" +
                        "Host: profile.xboxlive.com\r\n" +
                        "x-xbl-api-build-version: adk\r\n\r\n",
                    [WebServiceCaptureMethod.FieldKey_RequestBody] = string.Empty,
                    [WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders] = string.Empty,
                    [WebServiceCaptureMethod.FieldKey_ResponseBody] = string.Empty,
                    [WebServiceCaptureMethod.FieldKey_ClientIP] = "10.0.0.1"
                });
        }

        private static IDataTable CreateConnectionsTable(SqLtDatabase database) => database.CreateTable(
            WebServiceCaptureMethod.TableName,
            WebProxyCaptureSchema.CreateFields());

        private static long AddConnectionRow(IDataTable table, long requestNumber, long connectionId) => table.AddRow(
            "test-console",
            requestNumber,
            connectionId,
            "2026-11-03T20:00:00.0000000Z",
            string.Empty,
            "https",
            "example.test",
            "443",
            "GET",
            "/",
            string.Empty,
            "GET / HTTP/1.1",
            string.Empty,
            string.Empty,
            string.Empty,
            "test-console",
            string.Empty,
            0L,
            string.Empty,
            string.Empty);

        private static void SetPrivateField(object target, string name, object value)
        {
            typeof(WebServiceDeviceCaptureController).GetField(name, BindingFlags.Instance |
                BindingFlags.NonPublic).SetValue(target, value);
        }

        private sealed class TestDataset : IDataset
        {
            private readonly IDataRecord _record;

            public IEnumerable<IFieldDefinition> FieldDefs => Array.Empty<IFieldDefinition>();
            public IEnumerable<IDataRecord> DataRecords => new[] { _record };
            public int FilteredReadCount { get; private set; }
            internal bool RejectFilteredReads { get; set; }

            internal TestDataset(IDataRecord record)
            {
                _record = record;
            }

            public IEnumerable<IDataRecord> DataRecordsFrom(long firstRowId)
            {
                return DataRecords;
            }

            public IEnumerable<IDataRecord> DataRecordsFrom(long firstRowId, int limit)
            {
                return DataRecords.Take(limit);
            }

            public IEnumerable<IDataRecord> DataRecordsWhen(params IFieldValue[] fieldNamesAndValues)
            {
                if (RejectFilteredReads)
                {
                    throw new InvalidOperationException("The loaded payload was materialized while freezing metadata.");
                }
                FilteredReadCount++;

                var deviceFilter = fieldNamesAndValues.FirstOrDefault(
                    field => field.Name == WebServiceCaptureMethod.FieldKey_DeviceName);
                return deviceFilter?.Value as string == _record.Str(WebServiceCaptureMethod.FieldKey_DeviceName)
                    ? DataRecords
                    : Array.Empty<IDataRecord>();
            }
        }

        private sealed class TestDataRecord : IDataRecord
        {
            private readonly IReadOnlyDictionary<string, long> _integerValues;
            private readonly IReadOnlyDictionary<string, string> _stringValues;

            public int RowId { get; }

            internal TestDataRecord(
                int rowId,
                IReadOnlyDictionary<string, long> integerValues,
                IReadOnlyDictionary<string, string> stringValues)
            {
                RowId = rowId;
                _integerValues = integerValues;
                _stringValues = stringValues;
            }

            public long Int(string fieldName)
            {
                return _integerValues.TryGetValue(fieldName, out var value) ? value : 0;
            }

            public string Str(string fieldName)
            {
                return _stringValues.TryGetValue(fieldName, out var value) ? value : string.Empty;

            }
        }
    }
}
