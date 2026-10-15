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

            Assert.Same(record, loadedRecord);
            Assert.Equal(2, dataset.FilteredReadCount);
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

        private sealed class TestDataset : IDataset
        {
            private readonly IDataRecord _record;

            public IEnumerable<IFieldDefinition> FieldDefs => Array.Empty<IFieldDefinition>();
            public IEnumerable<IDataRecord> DataRecords => new[] { _record };
            public int FilteredReadCount { get; private set; }

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
