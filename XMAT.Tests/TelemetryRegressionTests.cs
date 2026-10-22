// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using XMAT.SharedInterfaces;
using XMAT.Telemetry;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Tests
{
    public class TelemetryRegressionTests
    {
        [Fact]
        public void ShutdownCompletesActiveOperationsBeforeSessionClosureExactlyOnce()
        {
            var reporter = new RecordingReporter();
            using var service = new XmatTelemetryService(reporter, _ => { });
            using var capture = service.Begin(TelemetryOperationKind.Capture, TelemetryFeature.WebProxyCapture);
            using var analysis = service.Begin(TelemetryOperationKind.Analysis, TelemetryFeature.XboxLiveAnalysis);
            capture.ItemReceived();
            analysis.MarkStage(TelemetryStage.Validate);
            service.Dispose();
            capture.Complete();
            analysis.Dispose();
            service.Dispose();

            var events = reporter.Events.ToArray();
            var completions = events.Where(x => x.Kind == TelemetryEventKind.OperationCompleted).ToArray();
            Assert.Equal(2, completions.Length);
            Assert.All(completions, x => Assert.Equal(TelemetryOutcome.Cancelled, x.Outcome));
            Assert.Equal(TelemetryCountBucket.OneToTen,
                Assert.Single(completions, x => x.Operation == TelemetryOperationKind.Capture).ItemCount);
            Assert.True(Array.FindLastIndex(events, x => x.Kind == TelemetryEventKind.OperationCompleted)
                < Array.FindIndex(events, x => x.Kind == TelemetryEventKind.SessionSummary));
            Assert.Equal(TelemetryEventKind.AppClosed, events[^1].Kind);
            Assert.Equal(1, reporter.DisposeCount);
            using var afterShutdown = service.Begin(TelemetryOperationKind.Capture);
            afterShutdown.Complete();
            Assert.Equal(events.Length, reporter.Events.Count);
        }

        [Fact]
        public async Task ConcurrentShutdownAndCompletionDoNotLoseOrDuplicateResults()
        {
            var reporter = new RecordingReporter();
            using var service = new XmatTelemetryService(reporter, _ => { });
            var operations = Enumerable.Range(0, 100)
                .Select(_ => service.Begin(TelemetryOperationKind.Capture, TelemetryFeature.NetworkTraceCapture)).ToArray();
            await Task.WhenAll(Task.Run(service.Dispose), Task.Run(() => Parallel.ForEach(operations, operation => operation.Complete())));
            var starts = reporter.Events.Where(x => x.Kind == TelemetryEventKind.OperationStarted).ToArray();
            var completions = reporter.Events.Where(x => x.Kind == TelemetryEventKind.OperationCompleted).ToArray();
            Assert.Equal(100, starts.Length);
            Assert.Equal(100, completions.Length);
            Assert.All(starts, start => Assert.Single(completions, result => result.OperationId == start.OperationId));
            Assert.Equal(TelemetryEventKind.AppClosed, reporter.Events.Last().Kind);
        }

        [Fact]
        public void StoredRequestIsCountedBeforeItsDeferredUiCallback()
        {
            var reporter = new RecordingReporter();
            using var service = new XmatTelemetryService(reporter, _ => { });
            using var capture = service.Begin(TelemetryOperationKind.Capture, TelemetryFeature.WebProxyCapture);
            var controller = new WebServiceDeviceCaptureController("test", DeviceType.LocalPC, true);
            List<Action> pendingUi = [];
            controller.RecordCapturedRow(1, capture, callback =>
            {
                Assert.Single(reporter.Events, x => x.Kind == TelemetryEventKind.CaptureFirstData);
                pendingUi.Add(callback);
            });
            capture.Complete();
            Assert.Single(pendingUi);
            var completed = Assert.Single(reporter.Events, x => x.Kind == TelemetryEventKind.OperationCompleted);
            Assert.Equal(TelemetryOutcome.Succeeded, completed.Outcome);
            Assert.Equal(TelemetryCountBucket.OneToTen, completed.ItemCount);
        }

        [Fact]
        public void OverlappingNavigationCompletionCannotFinishTheNewerOperation()
        {
            var reporter = new RecordingReporter();
            using var service = new XmatTelemetryService(reporter, _ => { });
            var navigation = new TelemetryNavigationTracker(service);
            navigation.Start(1, true);
            navigation.Start(2, true);
            navigation.Complete(1, false);
            Assert.Single(reporter.Events, x => x.Kind == TelemetryEventKind.OperationCompleted);
            navigation.Complete(2, true);
            var starts = reporter.Events.Where(x => x.Kind == TelemetryEventKind.OperationStarted).ToArray();
            var results = reporter.Events.Where(x => x.Kind == TelemetryEventKind.OperationCompleted).ToArray();
            Assert.Equal(2, results.Length);
            Assert.Equal(starts[0].OperationId, results[0].OperationId);
            Assert.Equal(TelemetryOutcome.Cancelled, results[0].Outcome);
            Assert.Equal(starts[1].OperationId, results[1].OperationId);
            Assert.Equal(TelemetryOutcome.Succeeded, results[1].Outcome);
        }

        [Fact]
        public void NavigationRedirectsRetainOneOperationAndUnloadedCompletionsAreIgnored()
        {
            var reporter = new RecordingReporter();
            using var service = new XmatTelemetryService(reporter, _ => { });
            var navigation = new TelemetryNavigationTracker(service);
            navigation.Start(1, true);
            navigation.Start(1, true);
            navigation.Complete(1, true);
            navigation.Complete(1, false);
            Assert.Single(reporter.Events, x => x.Kind == TelemetryEventKind.OperationStarted);
            Assert.Single(reporter.Events, x => x.Kind == TelemetryEventKind.OperationCompleted);
            navigation.Start(2, true);
            navigation.Cancel();
            navigation.Complete(2, true);
            Assert.Equal(2, reporter.Events.Count(x => x.Kind == TelemetryEventKind.OperationCompleted));
            navigation.Start(3, false);
            navigation.Start(3, true);
            navigation.Complete(3, true);
            Assert.Equal(2, reporter.Events.Count(x => x.Kind == TelemetryEventKind.OperationStarted));
        }

        [Theory]
        [InlineData("RequestBody")]
        [InlineData("RequestJson")]
        [InlineData("WebSocketText")]
        [InlineData("TimelineWaterfall")]
        public void VisibleDefaultOrPreselectedViewIsRecordedWhenDataBecomesAvailable(string name)
        {
            List<TelemetryFeature> uses = [];
            var feature = Enum.Parse<TelemetryFeature>(name);
            var usage = new TelemetryViewUsage(uses.Add);
            usage.Update(false, true, feature);
            usage.Update(true, false, feature);
            Assert.Empty(uses);
            usage.Update(true, true, feature);
            usage.Update(true, true, feature);
            Assert.Equal([feature], uses);
            usage.Update(false, true, feature);
            usage.Update(true, true, feature);
            Assert.Equal([feature, feature], uses);
        }

        [Fact]
        public async Task ReplacementImportCannotChangeAnEarlierCancelledResult()
        {
            var path = Path.Combine(Path.GetTempPath(), $"xmat-import-{Guid.NewGuid():N}.saz");
            try
            {
                using (ZipFile.Open(path, ZipArchiveMode.Create))
                { }
                var controller = new WebServiceDeviceCaptureController("import", DeviceType.Unknown, true);
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                var first = Task.Run(() => controller.ImportFromFiddler(path, cancelled.Token));
                await first.WaitAsync(TimeSpan.FromSeconds(10));
                var replacement = Task.Run(() => controller.ImportFromFiddler(path));
                Assert.True(await replacement.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.False(await first);
                Assert.Empty(controller.ProxyConnections);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void CollectorConnectRequestsAndResponsesDoNotReachCaptureOrScriptHandlers()
        {
            var proxy = new WebServiceProxy();
            var count = 0;
            proxy.ReceivedSslConnectionRequest += (_, _) => count++;
            proxy.CompletedSslConnectionRequest += (_, _) => count++;
            proxy.ReceivedWebRequest += (_, _) => count++;
            proxy.ReceivedWebResponse += (_, _) => count++;
            var host = $"collector-{Guid.NewGuid():N}.test";
            var request = new ClientRequest { Host = host, Port = 443, Method = "CONNECT" };
            var endpoint = new Uri($"https://{host}/OneCollector/1.0/");
            using (TelemetryTraffic.Exclude(endpoint))
            {
                using (TelemetryTraffic.Exclude(endpoint))
                {
                    Assert.True(proxy.RaiseReceivedSslConnectionRequest(1, "127.0.0.1", request));
                    proxy.RaiseCompletedSslConnectionRequest(1, request);
                    request.Method = "POST";
                    Assert.True(proxy.RaiseReceivedWebRequest(1, request));
                    Assert.True(proxy.RaiseReceivedWebResponse(1, request, new ServerResponse()));
                    Assert.Equal(0, count);
                }
                Assert.True(TelemetryTraffic.IsExcluded(host.ToUpperInvariant(), 443));
                Assert.False(TelemetryTraffic.IsExcluded(host, 80));
                Assert.False(TelemetryTraffic.IsExcluded($"{host}.other.test", 443));
            }
            Assert.False(TelemetryTraffic.IsExcluded(host, 443));
            Assert.True(proxy.RaiseReceivedWebRequest(1, request));
            Assert.Equal(1, count);
        }

        [Fact]
        public void OccupiedProxyPortFailsSynchronouslyAndCleansUpForRetry()
        {
            var logFile = Path.Combine(Path.GetTempPath(), $"xmat-startup-{Guid.NewGuid():N}.log");
            var proxy = new WebServiceProxy();
            using var occupied = new TcpListener(IPAddress.Any, 0);
            occupied.Start();
            var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
            try
            {
                var options = new WebServiceProxyOptions { Port = port, LogFilePath = logFile };
                Assert.ThrowsAny<IOException>(() => proxy.StartProxy(options));
                Assert.False(proxy.IsProxyEnabled);
                occupied.Stop();
                proxy.StartProxy(options);
                Assert.True(proxy.IsProxyEnabled);
                proxy.StopProxy();
                Assert.False(proxy.IsProxyEnabled);
            }
            finally
            {
                proxy.StopProxy();
                File.Delete(logFile);
            }
        }

        [Fact]
        public async Task ExcludedCollectorConnectIsForwardedWithoutTlsInterceptionOrCapture()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var destination = new TcpListener(IPAddress.Loopback, 0);
            destination.Start();
            var destinationPort = ((IPEndPoint)destination.LocalEndpoint).Port;
            using var excluded = TelemetryTraffic.Exclude(new Uri($"https://127.0.0.1:{destinationPort}/"));
            using var reservation = new TcpListener(IPAddress.Any, 0);
            reservation.Start();
            var proxyPort = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            var log = Path.Combine(Path.GetTempPath(), $"xmat-collector-{Guid.NewGuid():N}.log");
            var proxy = new WebServiceProxy();
            var captured = 0;
            proxy.ReceivedSslConnectionRequest += (_, _) => Interlocked.Increment(ref captured);
            proxy.CompletedSslConnectionRequest += (_, _) => Interlocked.Increment(ref captured);
            proxy.ReceivedWebRequest += (_, _) => Interlocked.Increment(ref captured);
            proxy.ReceivedWebResponse += (_, _) => Interlocked.Increment(ref captured);
            try
            {
                proxy.StartProxy(new() { Port = proxyPort, LogFilePath = log });
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
                using var stream = client.GetStream();
                using var response = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                var headers = Encoding.ASCII.GetBytes(
                    $"CONNECT 127.0.0.1:{destinationPort} HTTP/1.1\r\nHost: 127.0.0.1:{destinationPort}\r\n\r\n");
                await stream.WriteAsync(headers, timeout.Token);
                Assert.Contains("200", await response.ReadLineAsync(timeout.Token));
                while (await response.ReadLineAsync(timeout.Token) is { Length: > 0 })
                { }
                using var upstream = await destination.AcceptTcpClientAsync(timeout.Token);
                using var upstreamStream = upstream.GetStream();
                await stream.WriteAsync("test"u8.ToArray(), timeout.Token);
                var bytes = new byte[4];
                await upstreamStream.ReadExactlyAsync(bytes, timeout.Token);
                Assert.Equal("test", Encoding.ASCII.GetString(bytes));
                Assert.Equal(0, captured);
            }
            finally
            {
                proxy.StopProxy();
                File.Delete(log);
            }
        }

        private sealed class RecordingReporter : ITelemetryReporter
        {
            internal ConcurrentQueue<TelemetryEvent> Events { get; } = new();
            internal int DisposeCount { get; private set; }
            public void Send(TelemetryEvent item, Guid sessionId) => Events.Enqueue(item);
            public void Dispose() => DisposeCount++;
        }
    }
}
