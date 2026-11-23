// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.IO;
using System.Security;
using XMAT.Telemetry;

namespace XMAT.Tests
{
    public class TelemetryConsentTests
    {
        [Theory]
        [InlineData(null, "Missing")]
        [InlineData(0, "Declined")]
        [InlineData(1, "Accepted")]
        [InlineData(2, "Invalid")]
        [InlineData(-1, "Invalid")]
        [InlineData("1", "Invalid")]
        [InlineData(1L, "Invalid")]
        public void RegistryValuesRequireAnExplicitDwordOne(object value, string expectedState)
        {
            var expected = Enum.Parse<TelemetryConsentState>(expectedState);
            List<string> diagnostics = [];
            var store = new TelemetryConsentStore(() => value, _ => throw new InvalidOperationException(), diagnostics.Add);
            Assert.Equal(expected, store.Read());
            Assert.Equal(expected == TelemetryConsentState.Invalid ? ["ConsentInvalid"] : [], diagnostics);
        }

        [Theory]
        [InlineData("io")]
        [InlineData("access")]
        [InlineData("security")]
        public void RegistryReadFailuresFailClosedWithoutLeakingDetails(string failure)
        {
            Exception error = failure switch
            {
                "io" => new IOException("private path"),
                "access" => new UnauthorizedAccessException("private user"),
                _ => new SecurityException("private details")
            };
            List<string> diagnostics = [];
            var store = new TelemetryConsentStore(() => throw error, _ => { }, diagnostics.Add);
            Assert.Equal(TelemetryConsentState.Unreadable, store.Read());
            Assert.Equal(["ConsentReadFailed"], diagnostics);
        }

        [Fact]
        public void FirstRunCancelDoesNotPersistOrStartAndExistingChoicesDoNotPrompt()
        {
            var writes = new List<int>();
            var starts = 0;
            foreach (var state in new object[] { null, 0, 1, "invalid" })
            {
                var preferences = new TelemetryConsentPreferences(
                    new(() => state, writes.Add, _ => { }), () => { }, () => starts++);
                var prompts = 0;
                Assert.True(preferences.PromptIfMissing(() => { prompts++; return null; }));
                Assert.Equal(state == null ? 1 : 0, prompts);
                Assert.Equal(state == null ? TelemetryConsentState.Missing :
                    state is int value ? (value == 1 ? TelemetryConsentState.Accepted : TelemetryConsentState.Declined) :
                    TelemetryConsentState.Invalid, preferences.State);
            }
            Assert.Empty(writes);
            Assert.Equal(0, starts);
        }

        [Fact]
        public void PreferencesPersistBothChoicesAndStopBeforeWritingOrStarting()
        {
            List<string> calls = [];
            object saved = null;
            var preferences = new TelemetryConsentPreferences(new(() => saved, value =>
            {
                calls.Add($"Write{value}");
                saved = value;
            }, _ => { }), () => calls.Add("Stop"), () => calls.Add("Start"));
            Assert.True(preferences.PromptIfMissing(() => false));
            Assert.Equal(TelemetryConsentState.Declined, preferences.State);
            Assert.True(preferences.Apply(true));
            Assert.Equal(TelemetryConsentState.Accepted, preferences.State);
            Assert.True(preferences.Apply(null));
            Assert.Equal(TelemetryConsentState.Accepted, preferences.State);
            Assert.True(preferences.Apply(false));
            Assert.Equal(TelemetryConsentState.Declined, preferences.State);
            Assert.Equal(["Stop", "Write0", "Stop", "Write1", "Start", "Stop", "Write0"], calls);
            Assert.Equal(TelemetryConsentState.Declined, new TelemetryConsentStore(() => saved, _ => { }, _ => { }).Read());
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void FailedPreferenceWriteStopsCollectionAndCannotOptIn(bool accepted)
        {
            var stops = 0;
            var starts = 0;
            List<string> diagnostics = [];
            var preferences = new TelemetryConsentPreferences(
                new(() => 1, _ => throw new UnauthorizedAccessException("secret"), diagnostics.Add),
                () => stops++, () => starts++);
            Assert.False(preferences.Apply(accepted));
            Assert.Equal(TelemetryConsentState.Unreadable, preferences.State);
            Assert.Equal(1, stops);
            Assert.Equal(0, starts);
            Assert.Equal(["ConsentWriteFailed"], diagnostics);
        }

        [Theory]
        [InlineData("Missing")]
        [InlineData("Declined")]
        [InlineData("Invalid")]
        [InlineData("Unreadable")]
        public void NoMetadataClientOrBufferedUsageBeforeConsent(string state)
        {
            using var service = XmatTelemetryService.Create(new(TelemetryBuildChannel.Test),
                _ => throw new InvalidOperationException("Metadata read before consent"),
                (_, _) => throw new InvalidOperationException("Client created before consent"),
                _ => throw new InvalidOperationException("Unexpected diagnostic"), Enum.Parse<TelemetryConsentState>(state));
            service.Track(new(TelemetryEventKind.AppLaunched));
            service.Use(TelemetryFeature.Scripting);
            service.ScriptFailure(TelemetryFeature.ResponseScript);
            service.Ready();
            service.UnhandledFailure(new IOException("secret"));
            using var operation = service.Begin(TelemetryOperationKind.Capture);
            operation.ItemReceived();
            service.Revoke();
            Assert.False(service.IsEnabled);
        }

        [Fact]
        public void AcceptedConsentCreatesReporterAndRevocationStopsNewEvents()
        {
            var reporter = new QueueReporter();
            using var service = XmatTelemetryService.Create(new(TelemetryBuildChannel.Test),
                _ => [], (_, _) => reporter, _ => { }, TelemetryConsentState.Accepted);
            Assert.True(service.IsEnabled);
            using var operation = service.Begin(TelemetryOperationKind.Capture, TelemetryFeature.WebProxyCapture);
            service.ScriptFailure(TelemetryFeature.ResponseScript);
            Assert.NotEmpty(reporter.Pending);
            service.Revoke();
            var pending = reporter.Pending.Count;
            service.Revoke();
            operation.ItemReceived();
            operation.Complete();
            service.Ready();
            service.Track(new(TelemetryEventKind.Counter));
            service.Dispose();
            Assert.Equal(pending, reporter.Pending.Count);
            Assert.Equal(1, reporter.RevokeCount);
            Assert.Equal(0, reporter.FlushCount);
            using var next = XmatTelemetryService.Create(new(TelemetryBuildChannel.Test),
                _ => [], (_, _) => reporter, _ => { }, TelemetryConsentState.Declined);
            Assert.False(next.IsEnabled);
            Assert.Equal(pending, reporter.Pending.Count);
        }

        private sealed class QueueReporter : ITelemetryReporter
        {
            internal List<TelemetryEvent> Pending { get; } = [];
            internal int RevokeCount { get; private set; }
            internal int FlushCount { get; private set; }
            public void Send(TelemetryEvent item, Guid sessionId) => Pending.Add(item);
            public void Revoke()
            {
                RevokeCount++;
            }
            public void Dispose() => FlushCount++;
        }
    }
}
