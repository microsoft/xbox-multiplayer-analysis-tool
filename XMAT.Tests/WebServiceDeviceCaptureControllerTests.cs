// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Reflection;
using System.Text.Json;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using XMAT.WebServiceCapture.Proxy;
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

        private static void SetProxy(WebServiceDeviceCaptureController controller, IWebServiceProxy proxy)
        {
            typeof(WebServiceDeviceCaptureController)
                .GetField("_webProxy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(controller, proxy);
        }

        private sealed class FailingProxy : IWebServiceProxy
        {
            private readonly Exception _startFailure;
            private readonly bool _stopThrows;

            public FailingProxy(Exception startFailure, bool stopThrows)
            {
                _startFailure = startFailure;
                _stopThrows = stopThrows;
            }

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

            public Task StartProxyAsync(WebServiceProxyOptions options, CancellationToken cancellationToken = default)
                => Task.FromException(_startFailure);

            public Task StopProxyAsync(IProgress<WebProxyStopProgress> progress = null)
                => _stopThrows ? Task.FromException(new InvalidOperationException("cleanup failed")) : Task.CompletedTask;

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
        public void StoppedController_RejectsConnectionsRequestsAndResponses()
        {
            var controller = CreateController(new PreferencesModel());
            var connection = new SslConnectionRequestEventArgs { AcceptConnection = true };
            var request = new HttpRequestEventArgs { AcceptRequest = true };
            var response = new HttpResponseEventArgs { SendResponse = true };

            InvokeCaptureCallback(
                controller,
                "WebProxy_ReceivedSslConnectionRequestAsync",
                connection);
            InvokeCaptureCallback(
                controller,
                "WebProxy_ReceivedWebRequestAsync",
                request);
            InvokeCaptureCallback(
                controller,
                "WebProxy_ReceivedWebResponseAsync",
                response);

            Assert.False(connection.AcceptConnection);
            Assert.False(request.AcceptRequest);
            Assert.False(response.SendResponse);
        }

        private static void InvokeCaptureCallback<TEventArgs>(
            WebServiceDeviceCaptureController controller,
            string methodName,
            TEventArgs args) where TEventArgs : EventArgs
        {
            var method = typeof(WebServiceDeviceCaptureController).GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            var callback = method.CreateDelegate<EventHandler<TEventArgs>>(controller);
            callback(controller, args);
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
