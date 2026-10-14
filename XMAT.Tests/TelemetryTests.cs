// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using XMAT.SharedInterfaces;
using XMAT.Telemetry;

namespace XMAT.Tests
{
    public class TelemetryTests
    {
        [Fact]
        public void DisabledBuildDoesNotReadMetadataOrCreateTransport()
        {
            using var service = XmatTelemetryService.Create(new(false, false, "", TelemetryBuildChannel.Local),
                _ => throw new InvalidOperationException("Metadata must not be read."),
                (_, _) => throw new InvalidOperationException("Transport must not be created."),
                _ => throw new InvalidOperationException("Valid disabled builds require no failure diagnostic."));
            service.Ready();
            service.Use(TelemetryFeature.Scripting);
            using var operation = service.Begin(TelemetryOperationKind.Capture);
            operation.ItemReceived();
            operation.Complete();
            Assert.False(service.IsEnabled);
        }

        [Theory]
        [InlineData(false, "a-b", "Public")]
        [InlineData(true, "", "Public")]
        [InlineData(true, "invalid", "Public")]
        [InlineData(true, "a-b;Password=secret", "Public")]
        [InlineData(true, "a-b", "Local")]
        public void InvalidEnabledConfigurationFailsClosed(bool approved, string key, string channel)
        {
            List<string> diagnostics = [];
            using var service = XmatTelemetryService.Create(new(true, approved, key, Enum.Parse<TelemetryBuildChannel>(channel)),
                _ => throw new InvalidOperationException("Must not read metadata."),
                (_, _) => throw new InvalidOperationException("Must not create transport."), diagnostics.Add);
            Assert.False(service.IsEnabled);
            Assert.Equal(["InvalidConfiguration"], diagnostics);
        }

        [Fact]
        public void InitializationFailureDoesNotExposeExceptionOrReadMoreData()
        {
            List<string> diagnostics = [];
            using var service = XmatTelemetryService.Create(new(true, true, "a-b", TelemetryBuildChannel.Test),
                _ => throw new IOException(@"C:\Users\private-user\token.txt"),
                (_, _) => throw new InvalidOperationException("No transport."), diagnostics.Add);
            Assert.False(service.IsEnabled);
            Assert.Equal(["InitializationFailed"], diagnostics);
        }

        [Fact]
        public void InvalidEnumsAndNegativeMeasurementsAreRejected()
        {
            var reporter = new RecordingReporter();
            List<string> diagnostics = [];
            using var service = new XmatTelemetryService(reporter, diagnostics.Add);
            service.Track(new((TelemetryEventKind)999));
            service.Track(new(TelemetryEventKind.Counter, DurationMs: -1));
            service.Track(new(TelemetryEventKind.Counter, Context: new((CaptureType)999, DeviceType.LocalPC, CaptureOrigin.User)));
            Assert.Empty(reporter.Events);
            Assert.Equal(3, diagnostics.Count);
            Assert.All(diagnostics, x => Assert.Equal("InvalidEvent", x));
        }

        [Fact]
        public void SubmissionFailureIsDiagnosedWithoutExceptionContents()
        {
            List<string> diagnostics = [];
            using var service = new XmatTelemetryService(new ThrowingReporter(), diagnostics.Add);
            service.Track(new(TelemetryEventKind.AppLaunched));
            Assert.Equal(["SubmissionFailed"], diagnostics);
        }

        [Fact]
        public void SessionIdsAreFreshAndReadinessAndFailuresAreDeduplicated()
        {
            var firstReporter = new RecordingReporter();
            using var first = new XmatTelemetryService(firstReporter, _ => { });
            using var second = new XmatTelemetryService(new RecordingReporter(), _ => { });
            Assert.NotEqual(first.SessionId, second.SessionId);
            first.Ready();
            first.Ready();
            var error = new IOException(@"secret token and C:\Users\private-user\file.txt");
            first.UnhandledFailure(error);
            first.UnhandledFailure(error);
            Assert.Single(firstReporter.Events, x => x.Item.Kind == TelemetryEventKind.AppReady);
            var failure = Assert.Single(firstReporter.Events, x => x.Item.Kind == TelemetryEventKind.UnhandledFailure);
            var json = JsonSerializer.Serialize(failure.Item.Attributes(failure.Session));
            Assert.DoesNotContain("secret", json);
            Assert.DoesNotContain("private-user", json);
            Assert.DoesNotContain("IOException", json);
            Assert.Equal(TelemetryError.Io, failure.Item.Error);
        }

        [Fact]
        public void CaptureEmitsOneFirstDataEventAndReportsObservedCountBucket()
        {
            var reporter = new RecordingReporter();
            using var service = new XmatTelemetryService(reporter, _ => { });
            using var capture = service.Begin(TelemetryOperationKind.Capture, TelemetryFeature.WebProxyCapture,
                new(CaptureType.WebProxy, DeviceType.XboxConsole, CaptureOrigin.Restored));
            for (var i = 0; i < 12; i++)
            {
                capture.ItemReceived();
            }
            capture.Complete();
            capture.Complete();
            Assert.Single(reporter.Events, x => x.Item.Kind == TelemetryEventKind.CaptureFirstData);
            var completed = Assert.Single(reporter.Events, x => x.Item.Kind == TelemetryEventKind.OperationCompleted).Item;
            Assert.Equal(TelemetryOutcome.Succeeded, completed.Outcome);
            Assert.Equal(TelemetryCountBucket.ElevenToHundred, completed.ItemCount);
            Assert.Equal(CaptureOrigin.Restored, completed.Context.Value.Origin);
        }

        [Fact]
        public void EmptyCaptureAndCancelledOperationAreNotReportedAsSuccess()
        {
            var reporter = new RecordingReporter();
            using var service = new XmatTelemetryService(reporter, _ => { });
            using (var capture = service.Begin(TelemetryOperationKind.Capture))
            {
                capture.Complete();
            }
            using (var operation = service.Begin(TelemetryOperationKind.ImportSaz))
            {
                operation.Fail(new OperationCanceledException("Do not export this."));
            }
            var results = reporter.Events.Where(x => x.Item.Kind == TelemetryEventKind.OperationCompleted).Select(x => x.Item).ToArray();
            Assert.Equal(TelemetryOutcome.NoData, results[0].Outcome);
            Assert.Equal(TelemetryOutcome.Cancelled, results[1].Outcome);
            Assert.Equal(TelemetryError.Cancelled, results[1].Error);
        }

        [Fact]
        public void ConcurrentOperationsKeepTheirOwnIdsAndFeatureCounts()
        {
            var reporter = new RecordingReporter();
            using var service = new XmatTelemetryService(reporter, _ => { });
            Parallel.For(0, 100, _ =>
            {
                using var operation = service.Begin(TelemetryOperationKind.Analysis, TelemetryFeature.XboxLiveAnalysis);
                operation.MarkStage(TelemetryStage.Validate);
                operation.Complete(items: 10);
            });
            service.Dispose();
            service.Dispose();
            var starts = reporter.Events.Where(x => x.Item.Kind == TelemetryEventKind.OperationStarted).ToArray();
            var ends = reporter.Events.Where(x => x.Item.Kind == TelemetryEventKind.OperationCompleted).ToArray();
            Assert.Equal(100, starts.Length);
            Assert.Equal(100, ends.Length);
            Assert.Equal(100, starts.Select(x => x.Item.OperationId).Distinct().Count());
            Assert.All(starts, start => Assert.Single(ends, end => end.Item.OperationId == start.Item.OperationId));
            Assert.Single(reporter.Events, x => x.Item.Kind == TelemetryEventKind.FeatureUsed);
            Assert.Equal(100, Assert.Single(reporter.Events, x => x.Item.Kind == TelemetryEventKind.Counter).Item.Count);
            Assert.Single(reporter.Events, x => x.Item.Kind == TelemetryEventKind.AppClosed);
            Assert.Equal(1, reporter.DisposeCount);
        }

        [Fact]
        public void ScriptRuntimeFailuresAreAggregatedRatherThanSentPerRequest()
        {
            var reporter = new RecordingReporter();
            using var service = new XmatTelemetryService(reporter, _ => { });
            for (var i = 0; i < 1000; i++)
            {
                service.ScriptFailure(TelemetryFeature.ResponseScript);
            }
            Assert.Empty(reporter.Events);
            service.Dispose();
            var failure = Assert.Single(reporter.Events, x => x.Item.Kind == TelemetryEventKind.Counter);
            Assert.Equal(1000, failure.Item.Count);
            Assert.Equal(TelemetryOutcome.Failed, failure.Item.Outcome);
        }

        [Theory]
        [InlineData(-1, "Unknown")]
        [InlineData(0, "Zero")]
        [InlineData(10, "OneToTen")]
        [InlineData(11, "ElevenToHundred")]
        [InlineData(100, "ElevenToHundred")]
        [InlineData(101, "HundredToThousand")]
        [InlineData(1000, "HundredToThousand")]
        [InlineData(1001, "ThousandToTenThousand")]
        [InlineData(10000, "ThousandToTenThousand")]
        [InlineData(10001, "AboveTenThousand")]
        public void CountBucketsHaveExactBoundaries(long count, string expected)
        {
            Assert.Equal(expected, TelemetryBuckets.Items(count).ToString());
        }

        [Theory]
        [InlineData(-1, "Unknown")]
        [InlineData(0, "Empty")]
        [InlineData(1_048_575, "BelowOneMb")]
        [InlineData(1_048_576, "OneToTenMb")]
        [InlineData(10_485_760, "TenToHundredMb")]
        [InlineData(104_857_600, "HundredMbToGb")]
        [InlineData(1_073_741_824, "AboveOneGb")]
        public void SizeBucketsHaveExactBoundaries(long count, string expected)
        {
            Assert.Equal(expected, TelemetryBuckets.Size(count).ToString());
        }

        [Fact]
        public void SandboxIsBoundedAndNoFallbackMachineIdentityIsInvented()
        {
            List<string> diagnostics = [];
            Assert.Equal("RETAIL", TelemetryEnvironmentProvider.NormalizeSandbox(null, diagnostics.Add));
            Assert.Equal("MY-TEST-SANDBOX", TelemetryEnvironmentProvider.NormalizeSandbox("MY-TEST-SANDBOX", diagnostics.Add));
            Assert.Equal("Unknown", TelemetryEnvironmentProvider.NormalizeSandbox(new string('x', 129), diagnostics.Add));
            Assert.Equal("Unknown", TelemetryEnvironmentProvider.NormalizeSandbox("bad\nvalue", diagnostics.Add));
            Assert.Equal(2, diagnostics.Count);
        }

        private sealed class RecordingReporter : ITelemetryReporter
        {
            internal ConcurrentQueue<(TelemetryEvent Item, Guid Session)> Events { get; } = new();
            internal int DisposeCount { get; private set; }
            public void Send(TelemetryEvent item, Guid sessionId) => Events.Enqueue((item, sessionId));
            public void Dispose() => DisposeCount++;
        }

        private sealed class ThrowingReporter : ITelemetryReporter
        {
            public void Send(TelemetryEvent item, Guid sessionId) => throw new IOException("Private path, hostname, and token.");
            public void Dispose() { }
        }
    }
}
