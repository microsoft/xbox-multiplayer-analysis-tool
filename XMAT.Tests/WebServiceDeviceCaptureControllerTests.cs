// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using static XboxClient.XboxClientConnection;

namespace XMAT.Tests
{
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

        private static WebServiceDeviceCaptureController CreateController(PreferencesModel preferences, bool readOnly = false)
        {
            return new WebServiceDeviceCaptureController("test-console", DeviceType.XboxConsole, readOnly)
            {
                CaptureMethod = new TestCaptureMethod(preferences)
            };
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
    }
}
