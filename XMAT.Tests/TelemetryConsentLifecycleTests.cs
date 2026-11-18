// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.IO;
using System.Xml.Linq;
using XMAT.Telemetry;
using XMAT.Tests.Proxy;

namespace XMAT.Tests
{
    [Collection(IsolatedProxyTests.Name)]
    public class TelemetryConsentLifecycleTests
    {
        [Theory]
        [InlineData("1")]
        [InlineData("true")]
        [InlineData("0")]
        public void SuppressionPreventsClientsMetadataAndLocalSampleFiles(string value)
        {
            var previous = Environment.GetEnvironmentVariable("XMAT_SUPPRESS_TELEMETRY");
            try
            {
                Environment.SetEnvironmentVariable("XMAT_SUPPRESS_TELEMETRY", value);
                using var service = XmatTelemetryService.Create(new(true, true, "test-key", TelemetryBuildChannel.Test),
                    _ => throw new InvalidOperationException("Metadata must not be read"),
                    (_, _) => throw new InvalidOperationException("Client must not be constructed"),
                    _ => throw new InvalidOperationException("Unexpected diagnostic"), TelemetryConsentState.Accepted);
                Assert.False(service.IsEnabled);
                List<string> diagnostics = [];
                var path = Path.Combine(Path.GetTempPath(), $"xmat-suppressed-{Guid.NewGuid():N}.jsonl");
                using var sample = XmatTelemetryService.CreateSample(new(false, false, "", TelemetryBuildChannel.Local),
                    path, _ => throw new InvalidOperationException("Sample metadata must not be read"), diagnostics.Add);
                Assert.False(sample.IsEnabled);
                Assert.False(File.Exists(path));
                Assert.Equal(["LocalSampleSuppressed"], diagnostics);
            }
            finally
            {
                Environment.SetEnvironmentVariable("XMAT_SUPPRESS_TELEMETRY", previous);
            }
        }

        [Theory]
        [InlineData(false, false, "Local")]
        [InlineData(true, false, "Test")]
        [InlineData(true, true, "Local")]
        public void AcceptedConsentCannotOverrideBuildGates(bool enabled, bool approved, string channel)
        {
            using var service = XmatTelemetryService.Create(new(enabled, approved, "test-key",
                Enum.Parse<TelemetryBuildChannel>(channel)),
                _ => throw new InvalidOperationException("Metadata must not be read"),
                (_, _) => throw new InvalidOperationException("Client must not be constructed"),
                _ => { }, TelemetryConsentState.Accepted);
            Assert.False(service.IsEnabled);
        }

        [Fact]
        public void RevocationWaitsForInFlightSubmissionThenDiscardsWithoutFlush()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var revoking = new ManualResetEventSlim();
            using var revoked = new ManualResetEventSlim();
            List<string> diagnostics = [];
            var reporter = new BlockingReporter(entered, release);
            using var service = new XmatTelemetryService(reporter, diagnostics.Add);
            var sender = new Thread(() => service.Track(new(TelemetryEventKind.AppLaunched))) { IsBackground = true };
            var revoker = new Thread(() =>
            {
                revoking.Set();
                service.Revoke();
                revoked.Set();
            })
            { IsBackground = true };
            sender.Start();
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                revoker.Start();
                Assert.True(revoking.Wait(TimeSpan.FromSeconds(5)));
                Assert.False(revoked.Wait(TimeSpan.FromMilliseconds(100)));
            }
            finally
            {
                release.Set();
                Assert.True(sender.Join(TimeSpan.FromSeconds(5)));
                if ((revoker.ThreadState & ThreadState.Unstarted) == 0)
                {
                    Assert.True(revoker.Join(TimeSpan.FromSeconds(5)));
                }
            }
            service.Track(new(TelemetryEventKind.AppClosed));
            service.Dispose();
            Assert.True(revoked.IsSet);
            Assert.True(reporter.Discarded);
            Assert.Equal(1, reporter.Sends);
            Assert.False(reporter.Flushed);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void RevocationCannotReturnWhileAnEarlierShutdownIsStillFlushing()
        {
            using var flushing = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var revoking = new ManualResetEventSlim();
            using var revoked = new ManualResetEventSlim();
            var reporter = new ShutdownReporter(flushing, release);
            using var service = new XmatTelemetryService(reporter, _ => { });
            var shutdown = new Thread(service.Dispose) { IsBackground = true };
            var revoker = new Thread(() =>
            {
                revoking.Set();
                service.Revoke();
                revoked.Set();
            })
            { IsBackground = true };
            shutdown.Start();
            try
            {
                Assert.True(flushing.Wait(TimeSpan.FromSeconds(5)));
                revoker.Start();
                Assert.True(revoking.Wait(TimeSpan.FromSeconds(5)));
                Assert.False(revoked.Wait(TimeSpan.FromMilliseconds(100)));
            }
            finally
            {
                release.Set();
                Assert.True(shutdown.Join(TimeSpan.FromSeconds(5)));
                if ((revoker.ThreadState & ThreadState.Unstarted) == 0)
                {
                    Assert.True(revoker.Join(TimeSpan.FromSeconds(5)));
                }
            }
            Assert.True(revoked.IsSet);
        }

        [Fact]
        public void OptionsMenuExposesTheConsentPreference()
        {
            var assembly = typeof(TelemetryConsentLifecycleTests).Assembly;
            using var xaml = assembly.GetManifestResourceStream("XMAT.Tests.MainWindow.xaml");
            var document = XDocument.Load(xaml);
            XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace names = "http://schemas.microsoft.com/winfx/2006/xaml";
            var options = Assert.Single(document.Descendants(presentation + "MenuItem"),
                item => item.Attribute(names + "Name")?.Value == "OptionsMenu");
            Assert.Contains(options.Elements(presentation + "MenuItem"),
                item => item.Attribute("Click")?.Value == "TelemetryPreference_Click");
        }

        private sealed class ShutdownReporter(ManualResetEventSlim flushing, ManualResetEventSlim release) : ITelemetryReporter
        {
            public void Send(TelemetryEvent item, Guid sessionId) { }
            public void Revoke() { }
            public void Dispose()
            {
                flushing.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException();
                }
            }
        }

        private sealed class BlockingReporter(ManualResetEventSlim entered, ManualResetEventSlim release) : ITelemetryReporter
        {
            internal int Sends { get; private set; }
            internal bool Discarded { get; private set; }
            internal bool Flushed { get; private set; }

            public void Send(TelemetryEvent item, Guid sessionId)
            {
                Sends++;
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException();
                }
            }

            public void Revoke() => Discarded = true;
            public void Dispose() => Flushed = true;
        }
    }
}
