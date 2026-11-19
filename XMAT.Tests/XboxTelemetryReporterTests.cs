// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using XMAT.SharedInterfaces;
using XMAT.Telemetry;
using XMAT.Tests.Proxy;
using Schema = Microsoft.Gaming.Tooling.XMAT;

namespace XMAT.Tests
{
    [Collection(IsolatedProxyTests.Name)]
    public class XboxTelemetryReporterTests
    {
        private const string Collector = "mobile.events.data.microsoft.com";

        [Fact]
        public void ConsentGatesActivationAndRevocationStopsNewEventsWhileAllowingShutdown()
        {
            var client = new FakeClient();
            var creations = 0;
            ITelemetryReporter MakeReporter(TelemetryConfiguration _, IReadOnlyList<KeyValuePair<string, object>> __) =>
                XboxTelemetryReporter.Create(() =>
                {
                    creations++;
                    Assert.True(TelemetryTraffic.IsExcluded(Collector, 443));
                    return client;
                }, (item, _) => item.Kind.ToString());
            var config = new TelemetryConfiguration(true, true, "test-key", TelemetryBuildChannel.Test);
            using (var declined = XmatTelemetryService.Create(config, _ => [], MakeReporter, _ => { },
                TelemetryConsentState.Declined))
            {
                declined.Track(new(TelemetryEventKind.AppLaunched));
                Assert.Equal(0, creations);
            }

            using var accepted = XmatTelemetryService.Create(config, _ => [], MakeReporter, _ => { },
                TelemetryConsentState.Accepted);
            accepted.Track(new(TelemetryEventKind.AppLaunched));
            accepted.Revoke();
            accepted.Track(new(TelemetryEventKind.AppClosed));
            accepted.Dispose();

            Assert.Equal(1, creations);
            Assert.Equal(["AppLaunched", "Shutdown", "Dispose"], client.Calls);
            Assert.False(TelemetryTraffic.IsExcluded(Collector, 443));
        }

        [Fact]
        public void GeneratedPayloadsPreserveEachEventAndCommonFields()
        {
            var session = Guid.NewGuid();
            var operation = Guid.NewGuid();
            var payload = new XboxTelemetryPayload(
            [
                new("ext.app.ver", "0.2"),
                new("ext.os.ver", "10.0.19045.0"),
                new("BuildChannel", "Test"),
                new("ext.xbl.sbx", "RETAIL")
            ]);
            foreach (var kind in Enum.GetValues<TelemetryEventKind>())
            {
                var item = new TelemetryEvent(kind, TelemetryFeature.WebProxyCapture,
                    TelemetryOperationKind.Capture, TelemetryOutcome.Succeeded, TelemetryError.None,
                    new(CaptureType.WebProxy, DeviceType.XboxConsole, CaptureOrigin.User), operation,
                    DurationMs: 42, Count: 3);
                using var document = JsonDocument.Parse(payload.Serialize(item, session));
                var root = document.RootElement;
                Assert.Equal($"Microsoft.Gaming.Tooling.XMAT.{kind}", root.GetProperty("name").GetString());
                Assert.Equal("1.0.0", root.GetProperty("schemaVer").GetString());
                Assert.Equal(session.ToString("N"), root.GetProperty("SessionId").GetString());
                Assert.Equal(operation.ToString("N"), root.GetProperty("OperationId").GetString());
                Assert.Equal("WebProxyCapture", root.GetProperty("Feature").GetString());
                Assert.Equal("WebProxy", root.GetProperty("CaptureMethod").GetString());
                Assert.Equal("XboxConsole", root.GetProperty("DeviceType").GetString());
                Assert.Equal("User", root.GetProperty("CaptureOrigin").GetString());
                Assert.Equal("0.2", root.GetProperty("AppVersion").GetString());
                Assert.Equal("Test", root.GetProperty("BuildChannel").GetString());
                Assert.Equal("RETAIL", root.GetProperty("XboxSandbox").GetString());
                Assert.False(root.TryGetProperty("DeviceLocalId", out _));
                Assert.False(root.TryGetProperty("MachineIdAvailable", out _));
                Assert.Equal(42, root.GetProperty("DurationMs").GetInt64());
                Assert.Equal(3, root.GetProperty("Count").GetInt64());
            }
        }

        [Fact]
        public void GeneratedPayloadOmitsUnavailableOptionalFields()
        {
            var payload = new XboxTelemetryPayload(
            [
                new("ext.app.ver", "0.2"),
                new("ext.os.ver", "10.0.19045.0"),
                new("BuildChannel", "Test")
            ]);
            using var document = JsonDocument.Parse(payload.Serialize(new(TelemetryEventKind.AppReady), Guid.NewGuid()));
            Assert.False(document.RootElement.TryGetProperty("DeviceLocalId", out _));
            Assert.False(document.RootElement.TryGetProperty("MachineIdAvailable", out _));
            Assert.False(document.RootElement.TryGetProperty("XboxSandbox", out _));
            Assert.False(document.RootElement.TryGetProperty("OperationId", out _));
            Assert.False(document.RootElement.TryGetProperty("CaptureMethod", out _));
        }

        [Fact]
        public void GeneratedEnumsCoverEveryApplicationValue()
        {
            AssertNames<TelemetryFeature, Schema.FeatureName>();
            AssertNames<TelemetryOperationKind, Schema.OperationKind>();
            AssertNames<TelemetryOutcome, Schema.Outcome>();
            AssertNames<TelemetryError, Schema.ErrorCategory>();
            AssertNames<TelemetryCountBucket, Schema.ItemCountBucket>();
            AssertNames<TelemetrySizeBucket, Schema.FileSizeBucket>();
            AssertNames<TelemetryStage, Schema.StageName>();
            AssertNames<TelemetryEntryPoint, Schema.EntryPoint>();
            AssertNames<TelemetryInteraction, Schema.Interaction>();
            AssertNames<CaptureType, Schema.CaptureMethod>();
            AssertNames<DeviceType, Schema.CaptureDeviceType>();
            AssertNames<CaptureOrigin, Schema.CaptureOrigin>();
            AssertNames<TelemetryBuildChannel, Schema.BuildChannel>();
        }

        private static void AssertNames<TSource, TSchema>()
            where TSource : struct, Enum where TSchema : struct, Enum =>
            Assert.Equal(Enum.GetNames<TSource>().Order(), Enum.GetNames<TSchema>().Order());

        private sealed class FakeClient : IXboxTelemetryClient
        {
            internal List<string> Calls { get; } = [];

            public void TraceEvent(string payload) => Calls.Add(payload);

            public void Shutdown()
            {
                Assert.True(TelemetryTraffic.IsExcluded(Collector, 443));
                Calls.Add("Shutdown");
            }

            public void Dispose()
            {
                Assert.True(TelemetryTraffic.IsExcluded(Collector, 443));
                Calls.Add("Dispose");
            }
        }
    }
}
