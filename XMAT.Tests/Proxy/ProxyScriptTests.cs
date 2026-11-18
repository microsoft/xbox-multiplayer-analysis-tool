// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using XMAT.DataAbstractionLayer;
using XMAT.Scripting;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Proxy;
using static XboxClient.XboxClientConnection;

namespace XMAT.Tests.Proxy
{
    // Capture scripts run against the real proxy pipeline. A script's decision and its
    // changes must take effect whether or not the script awaits before making them.
    [Collection(IsolatedProxyTests.Name)]
    public class ProxyScriptTests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task WebRequestScript_ContinueFalse_DoesNotForwardRequest(bool tls, bool awaitsFirst)
        {
            await using var origin = await LoopbackOrigin.StartAsync(useTls: tls);
            await using var capture = await ScriptedCapture.StartAsync();
            await capture.EnableScriptAsync(
                WebServiceCaptureScriptableEventType.WebRequest,
                Script(awaitsFirst, "Params.Continue = false;"));

            Exception failure = await Record.ExceptionAsync(
                () => capture.Client.GetStringAsync(origin.Url("/blocked")));
            await capture.DrainCallbacksAsync();

            Assert.Empty(origin.Requests);
            Assert.IsAssignableFrom<HttpRequestException>(failure);
        }

        [Theory]
        [InlineData("http11", 0, false)]
        [InlineData("http11", 2, false)]
        [InlineData("http11", 5, false)]
        [InlineData("http11", 29, false)]
        [InlineData("https11", 0, false)]
        [InlineData("https11", 2, false)]
        [InlineData("https11", 5, false)]
        [InlineData("https11", 29, false)]
        [InlineData("https2", 0, false)]
        [InlineData("https2", 2, false)]
        [InlineData("https2", 5, false)]
        [InlineData("https2", 29, false)]
        [InlineData("http11", 0, true)]
        [InlineData("http11", 2, true)]
        [InlineData("http11", 5, true)]
        [InlineData("http11", 29, true)]
        public async Task WebRequestScript_EditedBody_ReachesOriginWithCorrectFraming(
            string protocol,
            int editedLength,
            bool chunked)
        {
            await using var origin = await LoopbackOrigin.StartAsync(useTls: protocol != "http11");
            await using var capture = await ScriptedCapture.StartAsync();
            byte[] editedBody = Enumerable.Range(0, editedLength)
                .Select(index => (byte)(index * 31 + 7)).ToArray();
            await capture.EnableScriptAsync(
                WebServiceCaptureScriptableEventType.WebRequest,
                "if (Params.Request.Method == \"POST\") { " +
                $"Params.Request.BodyBytes = System.Convert.FromBase64String(\"{Convert.ToBase64String(editedBody)}\"); }}");
            using var request = new HttpRequestMessage(HttpMethod.Post, origin.Url("/edited-body"))
            {
                Version = protocol == "https2" ? HttpVersion.Version20 : HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = new ByteArrayContent("short"u8.ToArray())
            };
            request.Content.Headers.ContentType = new("application/octet-stream");
            request.Headers.TransferEncodingChunked = chunked;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            using HttpResponseMessage response = await capture.Client.SendAsync(request, timeout.Token);
            await capture.DrainCallbacksAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(request.Version, response.Version);
            ObservedRequest received = Assert.Single(origin.Requests);
            Assert.Equal(editedBody, received.Body);
            Assert.Equal("application/octet-stream", received.Headers["Content-Type"]);
            if (chunked)
            {
                // A script can replace the body, but forwarding keeps the upload's original chunked framing.
                Assert.Contains(
                    "chunked",
                    received.Headers.GetValueOrDefault("Transfer-Encoding") ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase);
                Assert.False(received.Headers.ContainsKey("Content-Length"));
            }
            else
            {
                Assert.Equal(editedLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    received.Headers["Content-Length"]);
                Assert.False(received.Headers.ContainsKey("Transfer-Encoding"));
            }
            IDataRecord row = capture.CapturedRow("POST");
            Assert.Equal(editedBody, Convert.FromBase64String(
                row.Str(WebServiceCaptureMethod.FieldKey_RequestBody)));
            string capturedHeaders = row.Str(WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders);
            if (chunked)
            {
                Assert.DoesNotContain("Transfer-Encoding:", capturedHeaders, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Content-Length:", capturedHeaders, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                // Capture keeps the client's framing headers, as on the HTTPS path.
                Assert.Contains("Content-Length: 5", capturedHeaders, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Theory]
        [InlineData("https11")]
        [InlineData("https2")]
        public async Task InterceptedHttps_SlowBodyBeyondKestrelGrace_IsForwarded(string protocol)
        {
            var policy = ProxyRequestReadPolicy.Default with
            {
                BodyInactivityTimeout = TimeSpan.FromSeconds(8),
                BodyTotalTimeout = TimeSpan.FromSeconds(12),
            };
            await using var origin = await LoopbackOrigin.StartAsync(useTls: true);
            await using var capture = await ScriptedCapture.StartAsync(requestPolicy: policy);
            byte[] body = "slow-upload-payload"u8.ToArray();
            using var request = new HttpRequestMessage(HttpMethod.Post, origin.Url("/slow-body"))
            {
                Version = protocol == "https2" ? HttpVersion.Version20 : HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = new DelayedBodyContent(
                    body,
                    chunkSize: body.Length,
                    initialDelay: TimeSpan.FromSeconds(6),
                    interChunkDelay: TimeSpan.Zero)
            };
            using HttpResponseMessage response = await capture.Client.SendAsync(request)
                .WaitAsync(TimeSpan.FromSeconds(15));
            await capture.DrainCallbacksAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(request.Version, response.Version);
            Assert.Equal(body, Assert.Single(origin.Requests).Body);
            Assert.Equal(body, Assert.Single(capture.CapturedRequests, item => item.Method == "POST").BodyBytes);
        }

        [Theory]
        [InlineData("https11")]
        [InlineData("https2")]
        public async Task InterceptedHttps_BodyInactivityTimeout_Returns408AndLogsLimit(string protocol)
        {
            var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var policy = ProxyRequestReadPolicy.Default with
            {
                BodyInactivityTimeout = TimeSpan.FromMilliseconds(350),
                BodyTotalTimeout = TimeSpan.FromSeconds(3),
            };
            await using var origin = await LoopbackOrigin.StartAsync(useTls: true);
            await using var capture = await ScriptedCapture.StartAsync(
                requestPolicy: policy,
                logObserver: (_, _, message) => logs.Enqueue(message));
            if (protocol == "https11")
            {
                string responseHead = await SendHttps11PartialBodyAsync(
                    capture.ProxyPort,
                    origin.Port,
                    "incomplete"u8.ToArray(),
                    bodyBytesToSend: 1,
                    delayBetweenBytes: TimeSpan.Zero);
                Assert.StartsWith(
                    "HTTP/1.1 408 Request Timeout\r\n",
                    responseHead,
                    StringComparison.Ordinal);
                await capture.DrainCallbacksAsync();
                await WaitForAsync(() => !logs.IsEmpty);
                Assert.Empty(origin.Requests);
                Assert.DoesNotContain(capture.CapturedRequests, item => item.Method == "POST");
                Assert.Contains("request-body inactivity limit", Assert.Single(logs), StringComparison.Ordinal);
                return;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, origin.Url("/idle-timeout"))
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = new DelayedBodyContent(
                    "incomplete"u8.ToArray(),
                    chunkSize: 1,
                    initialDelay: TimeSpan.Zero,
                    interChunkDelay: TimeSpan.FromSeconds(1))
            };

            using HttpResponseMessage response = await capture.Client.SendAsync(request)
                .WaitAsync(TimeSpan.FromSeconds(5));
            await capture.DrainCallbacksAsync();

            Assert.Equal(HttpStatusCode.RequestTimeout, response.StatusCode);
            Assert.Empty(origin.Requests);
            Assert.DoesNotContain(capture.CapturedRequests, item => item.Method == "POST");
            string log = Assert.Single(logs);
            Assert.Contains("request-body inactivity limit", log, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("https11")]
        [InlineData("https2")]
        public async Task InterceptedHttps_BodyTotalTimeout_Returns408DespiteProgress(string protocol)
        {
            var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var policy = ProxyRequestReadPolicy.Default with
            {
                BodyInactivityTimeout = TimeSpan.FromMilliseconds(500),
                BodyTotalTimeout = TimeSpan.FromMilliseconds(800),
            };
            await using var origin = await LoopbackOrigin.StartAsync(useTls: true);
            await using var capture = await ScriptedCapture.StartAsync(
                requestPolicy: policy,
                logObserver: (_, _, message) => logs.Enqueue(message));
            if (protocol == "https11")
            {
                string responseHead = await SendHttps11PartialBodyAsync(
                    capture.ProxyPort,
                    origin.Port,
                    "slow-trickle"u8.ToArray(),
                    bodyBytesToSend: 6,
                    delayBetweenBytes: TimeSpan.FromMilliseconds(100));
                Assert.StartsWith(
                    "HTTP/1.1 408 Request Timeout\r\n",
                    responseHead,
                    StringComparison.Ordinal);
                await capture.DrainCallbacksAsync();
                await WaitForAsync(() => !logs.IsEmpty);
                Assert.Empty(origin.Requests);
                Assert.DoesNotContain(capture.CapturedRequests, item => item.Method == "POST");
                Assert.Contains("total request-body time limit", Assert.Single(logs), StringComparison.Ordinal);
                return;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, origin.Url("/total-timeout"))
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = new DelayedBodyContent(
                    "slow-trickle"u8.ToArray(),
                    chunkSize: 1,
                    initialDelay: TimeSpan.Zero,
                    interChunkDelay: TimeSpan.FromMilliseconds(100))
            };

            using HttpResponseMessage response = await capture.Client.SendAsync(request)
                .WaitAsync(TimeSpan.FromSeconds(5));
            await capture.DrainCallbacksAsync();

            Assert.Equal(HttpStatusCode.RequestTimeout, response.StatusCode);
            Assert.Empty(origin.Requests);
            Assert.DoesNotContain(capture.CapturedRequests, item => item.Method == "POST");
            string log = Assert.Single(logs);
            Assert.Contains("total request-body time limit", log, StringComparison.Ordinal);
        }

        [Fact]
        public async Task InterceptedHttps2_ConcurrentBodies_EnforcesAggregateBudgetAndReleasesReservation()
        {
            var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
            byte[] body = Enumerable.Range(0, 512)
                .Select(index => (byte)(index % 251))
                .ToArray();
            var policy = ProxyRequestReadPolicy.Default with
            {
                MaxBodyBytes = body.Length,
                MaxAggregateBufferedBytes = 64 * 1024,
            };
            var budget = new ProxyRequestBufferBudget(
                policy with { MaxAggregateBufferedBytes = 24 * 1024 });
            await using var origin = await LoopbackOrigin.StartAsync(useTls: true);
            await using var capture = await ScriptedCapture.StartAsync(
                requestPolicy: policy,
                requestBufferBudget: budget,
                logObserver: (_, _, message) => logs.Enqueue(message));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var heldContent = new GatedBodyContent(body);
            using var heldRequest = new HttpRequestMessage(
                HttpMethod.Post,
                origin.Url("/budget-accepted"))
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = heldContent,
            };
            Task<HttpResponseMessage> heldResponseTask =
                capture.Client.SendAsync(heldRequest, timeout.Token);

            long heldReservation;
            try
            {
                await heldContent.FirstByteWritten.WaitAsync(timeout.Token);
                await WaitForAsync(
                    () => budget.ReservedBytes > 0,
                    TimeSpan.FromSeconds(5));
                heldReservation = budget.ReservedBytes;
                Assert.False(heldResponseTask.IsCompleted);
                // H2 staging retains its read buffer plus up to three body-sized copies.
                Assert.InRange(
                    checked(heldReservation + (body.LongLength * 3)),
                    1L,
                    budget.MaximumBytes);
                Assert.True(
                    checked(heldReservation * 2) > budget.MaximumBytes);

                using var competingRequest = new HttpRequestMessage(
                    HttpMethod.Post,
                    origin.Url("/budget-rejected"))
                {
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                    Content = new ByteArrayContent(body),
                };
                using HttpResponseMessage competingResponse =
                    await capture.Client.SendAsync(competingRequest, timeout.Token);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, competingResponse.StatusCode);
                Assert.Equal(HttpVersion.Version20, competingResponse.Version);
            }
            finally
            {
                heldContent.Release();
            }

            using HttpResponseMessage heldResponse =
                await heldResponseTask.WaitAsync(timeout.Token);
            await capture.DrainCallbacksAsync();
            await WaitForAsync(() => budget.ReservedBytes == 0);
            await WaitForAsync(() => !logs.IsEmpty);

            Assert.Equal(HttpStatusCode.OK, heldResponse.StatusCode);
            Assert.Equal(HttpVersion.Version20, heldResponse.Version);
            Assert.True(budget.MaximumBytes < policy.MaxAggregateBufferedBytes);
            Assert.Equal(1L, budget.RejectedReservations);
            Assert.InRange(budget.PeakReservedBytes, heldReservation, budget.MaximumBytes);
            Assert.Equal(0L, budget.ReservedBytes);
            Assert.Contains(
                $"the {budget.MaximumBytes} byte aggregate request-buffer budget",
                Assert.Single(logs),
                StringComparison.Ordinal);
            ObservedRequest forwarded = Assert.Single(origin.Requests);
            Assert.Equal("/budget-accepted", forwarded.Path);
            Assert.Equal(body, forwarded.Body);
            ClientRequest captured = Assert.Single(
                capture.CapturedRequests,
                item => item.Method == "POST");
            Assert.Equal("/budget-accepted", captured.Path);
            Assert.Equal("HTTP/2", captured.Version);
            Assert.Equal(body, captured.BodyBytes);
        }

        [Theory]
        [InlineData("https11")]
        [InlineData("https2")]
        public async Task InterceptedHttps_OversizedBody_Returns413AndLogsLimit(string protocol)
        {
            var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var policy = ProxyRequestReadPolicy.Default with { MaxBodyBytes = 16 };
            await using var origin = await LoopbackOrigin.StartAsync(useTls: true);
            await using var capture = await ScriptedCapture.StartAsync(
                requestPolicy: policy,
                logObserver: (_, _, message) => logs.Enqueue(message));
            using var request = new HttpRequestMessage(HttpMethod.Post, origin.Url("/oversized"))
            {
                Version = protocol == "https2" ? HttpVersion.Version20 : HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = new ByteArrayContent(new byte[17])
            };

            using HttpResponseMessage response = await capture.Client.SendAsync(request)
                .WaitAsync(TimeSpan.FromSeconds(5));
            await capture.DrainCallbacksAsync();

            Assert.Equal(413, (int)response.StatusCode);
            Assert.Empty(origin.Requests);
            Assert.DoesNotContain(capture.CapturedRequests, item => item.Method == "POST");
            string log = Assert.Single(logs);
            Assert.Contains("request-body size limit", log, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("https11")]
        [InlineData("https2")]
        public async Task InterceptedHttps_BodyAtLimit_IsForwarded(string protocol)
        {
            const int maxBodyBytes = 16;
            byte[] body = Enumerable.Range(0, maxBodyBytes)
                .Select(index => (byte)index)
                .ToArray();
            var policy = ProxyRequestReadPolicy.Default with { MaxBodyBytes = maxBodyBytes };
            await using var origin = await LoopbackOrigin.StartAsync(useTls: true);
            await using var capture = await ScriptedCapture.StartAsync(requestPolicy: policy);
            using var request = new HttpRequestMessage(HttpMethod.Post, origin.Url("/at-limit"))
            {
                Version = protocol == "https2" ? HttpVersion.Version20 : HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = new ByteArrayContent(body),
            };

            using HttpResponseMessage response = await capture.Client.SendAsync(request)
                .WaitAsync(TimeSpan.FromSeconds(5));
            await capture.DrainCallbacksAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(body, Assert.Single(origin.Requests).Body);
            Assert.Equal(
                body,
                Assert.Single(capture.CapturedRequests, item => item.Method == "POST").BodyBytes);
        }

        [Theory]
        [InlineData("https11")]
        [InlineData("https2")]
        public async Task InterceptedHttps_StreamedBodyOverLimit_Returns413(string protocol)
        {
            var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var policy = ProxyRequestReadPolicy.Default with { MaxBodyBytes = 16 };
            await using var origin = await LoopbackOrigin.StartAsync(useTls: true);
            await using var capture = await ScriptedCapture.StartAsync(
                requestPolicy: policy,
                logObserver: (_, _, message) => logs.Enqueue(message));
            using var request = new HttpRequestMessage(HttpMethod.Post, origin.Url("/streamed-oversized"))
            {
                Version = protocol == "https2" ? HttpVersion.Version20 : HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = new UnknownLengthBodyContent(
                    new byte[17],
                    firstChunkSize: 16,
                    interChunkDelay: TimeSpan.FromMilliseconds(25))
            };
            Assert.Null(request.Content.Headers.ContentLength);

            using HttpResponseMessage response = await capture.Client.SendAsync(request)
                .WaitAsync(TimeSpan.FromSeconds(5));
            await capture.DrainCallbacksAsync();

            Assert.Equal(413, (int)response.StatusCode);
            Assert.Empty(origin.Requests);
            Assert.DoesNotContain(capture.CapturedRequests, item => item.Method == "POST");
            Assert.Contains("request-body size limit", Assert.Single(logs), StringComparison.Ordinal);
        }

        [Fact]
        public async Task InterceptedHttps_ExpectContinueAndCompressedBody_ArePreserved()
        {
            await using var origin = await LoopbackOrigin.StartAsync(useTls: true);
            await using var capture = await ScriptedCapture.StartAsync();
            byte[] originalBody = Enumerable.Repeat((byte)'x', 4096).ToArray();
            byte[] compressedBody;
            using (var buffer = new MemoryStream())
            {
                using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
                    gzip.Write(originalBody);
                compressedBody = buffer.ToArray();
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, origin.Url("/expect-gzip"))
            {
                Content = new ByteArrayContent(compressedBody)
            };
            request.Content.Headers.ContentEncoding.Add("gzip");
            request.Headers.ExpectContinue = true;

            using HttpResponseMessage response = await capture.Client.SendAsync(request)
                .WaitAsync(TimeSpan.FromSeconds(10));
            await capture.DrainCallbacksAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            ObservedRequest received = Assert.Single(origin.Requests);
            Assert.Equal("gzip", received.Headers["Content-Encoding"]);
            Assert.Equal(originalBody, Decompress(received.Body));
            Assert.Equal(compressedBody, Assert.Single(
                capture.CapturedRequests,
                item => item.Method == "POST").BodyBytes);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task WebResponseScript_ChangedStatus_IsSentAndCaptured(bool tls, bool awaitsFirst)
        {
            await using var origin = await LoopbackOrigin.StartAsync(useTls: tls);
            await using var capture = await ScriptedCapture.StartAsync();
            await capture.EnableScriptAsync(
                WebServiceCaptureScriptableEventType.WebResponse,
                Script(
                    awaitsFirst,
                    "if (Params.Request.Method != \"CONNECT\") { Params.Response.Status = \"503\"; }"));

            using HttpResponseMessage response = await capture.Client.GetAsync(origin.Url("/status"));
            await capture.DrainCallbacksAsync();

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("503", capture.CapturedStatus("GET"));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SslConnectionScript_ContinueFalse_RejectsTunnel(bool awaitsFirst)
        {
            await using var origin = await LoopbackOrigin.StartAsync(useTls: true);
            await using var capture = await ScriptedCapture.StartAsync();
            await capture.EnableScriptAsync(
                WebServiceCaptureScriptableEventType.SslConnectionRequest,
                Script(awaitsFirst, "Params.Continue = false;"));

            Exception failure = await Record.ExceptionAsync(
                () => capture.Client.GetStringAsync(origin.Url("/tunnel")));
            await capture.DrainCallbacksAsync();

            Assert.Empty(origin.Requests);
            Assert.IsAssignableFrom<HttpRequestException>(failure);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task SslConnectionScript_EnabledDuringCapture_BlocksExistingTunnelAndReconnects(
            bool http2,
            bool bypass)
        {
            await using var origin = await LoopbackOrigin.StartAsync(useTls: true);
            await using var capture = await ScriptedCapture.StartAsync();
            if (bypass)
            {
                capture.Controller.BypassList.Add("127.0.0.1");
                capture.Proxy.BypassList = capture.Controller.BypassList;
            }
            var connects = new System.Collections.Concurrent.ConcurrentQueue<string>();
            capture.Proxy.ReceivedSslConnectionRequest += (_, args) =>
                connects.Enqueue(args.Request.Host);

            async Task<string> GetAsync(string url)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url)
                {
                    Version = http2 ? HttpVersion.Version20 : HttpVersion.Version11,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact
                };
                using var response = await capture.Client.SendAsync(request);
                return await response.Content.ReadAsStringAsync();
            }

            string blockedUrl = origin.Url("/blocked");
            string allowedUrl = origin.Url("/allowed").Replace("127.0.0.1", "localhost");
            Assert.Equal("origin-ok", await GetAsync(blockedUrl));
            Assert.Equal("origin-ok", await GetAsync(allowedUrl));
            int recordsBeforeEnable = capture.CapturedRecordCount;
            await capture.EnableScriptAsync(
                WebServiceCaptureScriptableEventType.SslConnectionRequest,
                "if (Params.Request.Host == \"127.0.0.1\") { Params.Continue = false; }");

            Assert.Equal(recordsBeforeEnable, capture.CapturedRecordCount);
            Exception failure = await Record.ExceptionAsync(() => GetAsync(blockedUrl));
            Assert.IsAssignableFrom<HttpRequestException>(failure);
            Assert.Equal("origin-ok", await GetAsync(allowedUrl));
            Assert.Single(connects, host => host == "localhost");
            Assert.Single(origin.Requests, request => request.Path == "/blocked");

            capture.Controller.Scripts[WebServiceCaptureScriptableEventType.SslConnectionRequest]
                .DisableScript();
            Assert.Equal("origin-ok", await GetAsync(blockedUrl));
            Assert.Equal(2, origin.Requests.Count(request => request.Path == "/blocked"));
        }

        [Fact]
        public async Task SslConnectionScript_RecheckUsesCopyAndIgnoresDisabledScriptResult()
        {
            await using var origin = await LoopbackOrigin.StartAsync(useTls: true);
            await using var capture = await ScriptedCapture.StartAsync();
            Assert.Equal("origin-ok", await capture.Client.GetStringAsync(origin.Url("/before")));
            var connects = new System.Collections.Concurrent.ConcurrentQueue<string>();
            capture.Proxy.ReceivedSslConnectionRequest += (_, args) => connects.Enqueue(args.Request.Host);
            string key = $"live-ssl-{Guid.NewGuid():N}";
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            AppDomain.CurrentDomain.SetData(key + "-started", started);
            AppDomain.CurrentDomain.SetData(key + "-release", release.Task);
            Task activation = null;
            try
            {
                activation = capture.EnableScriptAsync(
                    WebServiceCaptureScriptableEventType.SslConnectionRequest,
                    $"var started = (System.Threading.Tasks.TaskCompletionSource<bool>)System.AppDomain.CurrentDomain.GetData(\"{key}-started\"); " +
                    $"var release = (System.Threading.Tasks.Task)System.AppDomain.CurrentDomain.GetData(\"{key}-release\"); " +
                    "Params.Request.Host = \"changed.invalid\"; Params.Request.Headers[\"X-Recheck\"] = \"changed\"; " +
                    "started.TrySetResult(true); await release; Params.Continue = false;");
                await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.False(activation.IsCompleted);
                capture.Controller.Scripts[WebServiceCaptureScriptableEventType.SslConnectionRequest]
                    .DisableScript();
                release.TrySetResult(true);
                await activation.WaitAsync(TimeSpan.FromSeconds(10));

                Assert.Equal("origin-ok", await capture.Client.GetStringAsync(origin.Url("/after")));
                Assert.Empty(connects);
                Assert.All(origin.Requests, request => Assert.False(request.Headers.ContainsKey("X-Recheck")));
            }
            finally
            {
                release.TrySetResult(true);
                if (activation != null)
                    await activation.WaitAsync(TimeSpan.FromSeconds(10));
                AppDomain.CurrentDomain.SetData(key + "-started", null);
                AppDomain.CurrentDomain.SetData(key + "-release", null);
            }
        }

        [Theory]
        [InlineData("disable")]
        [InlineData("edit")]
        [InlineData("recompile")]
        [InlineData("replace")]
        public async Task SslConnectionScript_InvalidatedAtAbortHandoff_PreservesExistingTunnel(
            string invalidation)
        {
            await using var origin = await LoopbackOrigin.StartAsync(useTls: true);
            await using var capture = await ScriptedCapture.StartAsync();
            Assert.Equal("origin-ok", await capture.Client.GetStringAsync(origin.Url("/before")));
            var connects = new System.Collections.Concurrent.ConcurrentQueue<string>();
            capture.Proxy.ReceivedSslConnectionRequest += (_, args) => connects.Enqueue(args.Request.Host);
            var beforeAbort = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            capture.Proxy.BeforeSslConnectionRecheckAbortAsync = async () =>
            {
                beforeAbort.TrySetResult(true);
                await release.Task;
            };
            string key = $"ssl-handoff-{Guid.NewGuid():N}";
            AppDomain.CurrentDomain.SetData(key, false);
            Task activation = null;
            try
            {
                activation = capture.EnableScriptAsync(
                    WebServiceCaptureScriptableEventType.SslConnectionRequest,
                    $"Params.Continue = System.AppDomain.CurrentDomain.GetData(\"{key}\") is true;");
                await beforeAbort.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.False(activation.IsCompleted);
                ScriptModel model = capture.Controller.Scripts[WebServiceCaptureScriptableEventType.SslConnectionRequest];
                switch (invalidation)
                {
                    case "disable":
                        model.DisableScript();
                        break;
                    case "edit":
                        model.Script = "Params.Continue = true;";
                        break;
                    case "recompile":
                        AppDomain.CurrentDomain.SetData(key, true);
                        Assert.Equal(ScriptActivationResult.Enabled,
                            await model.EnableScriptAsync<WebServiceCaptureScriptParams>());
                        break;
                    case "replace":
                        await capture.EnableScriptAsync(
                            WebServiceCaptureScriptableEventType.SslConnectionRequest,
                            "Params.Continue = true;");
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(invalidation));
                }
                release.TrySetResult(true);
                await activation.WaitAsync(TimeSpan.FromSeconds(15));

                Assert.Equal("origin-ok", await capture.Client.GetStringAsync(origin.Url("/after")));
                Assert.Empty(connects);
            }
            finally
            {
                release.TrySetResult(true);
                if (activation != null)
                    await activation.WaitAsync(TimeSpan.FromSeconds(15));
                capture.Proxy.BeforeSslConnectionRecheckAbortAsync = null;
                AppDomain.CurrentDomain.SetData(key, null);
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task SslConnectionScript_InvalidOrThrowingScript_DoesNotDisconnectActiveTunnel(
            bool compilationError)
        {
            await using var origin = await LoopbackOrigin.StartAsync(useTls: true);
            await using var capture = await ScriptedCapture.StartAsync();
            Assert.Equal("origin-ok", await capture.Client.GetStringAsync(origin.Url("/before")));
            var connects = new System.Collections.Concurrent.ConcurrentQueue<string>();
            capture.Proxy.ReceivedSslConnectionRequest += (_, args) => connects.Enqueue(args.Request.Host);
            ScriptModel model = capture.Controller.Scripts[WebServiceCaptureScriptableEventType.SslConnectionRequest];
            model.Script = compilationError
                ? "this is not valid C#;"
                : "throw new System.InvalidOperationException(\"recheck failure\");";

            Assert.Equal(
                compilationError ? ScriptActivationResult.Failed : ScriptActivationResult.Enabled,
                await model.EnableScriptAsync<WebServiceCaptureScriptParams>());

            Assert.Equal("origin-ok", await capture.Client.GetStringAsync(origin.Url("/after")));
            Assert.Empty(connects);
        }

        [Fact]
        public async Task SslConnectionScript_EnableWhileInitialDecisionAwaits_CannotLeaveApprovedTunnel()
        {
            await using var origin = await LoopbackOrigin.StartAsync(useTls: true);
            await using var capture = await ScriptedCapture.StartAsync();
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var policy = capture.Proxy.SslConnectionRequestPolicyAsync;
            capture.Proxy.SslConnectionRequestPolicyAsync = async (args, token) =>
            {
                bool accepted = await policy(args, token);
                started.TrySetResult(true);
                await release.Task;
                return accepted;
            };
            Task<string> request = capture.Client.GetStringAsync(origin.Url("/racing"));
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await capture.EnableScriptAsync(
                    WebServiceCaptureScriptableEventType.SslConnectionRequest,
                    "Params.Continue = false;");
            }
            finally
            {
                release.TrySetResult(true);
            }
            await Assert.ThrowsAsync<HttpRequestException>(() => request);
            Assert.Empty(origin.Requests);
        }

        [Fact]
        public async Task SslConnectionScript_EnableDisconnectsEstablishedWebSocket()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var origin = await LoopbackOrigin.StartAsync(
                async context =>
                {
                    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
                    var buffer = new byte[32];
                    WebSocketReceiveResult message = await socket.ReceiveAsync(buffer, timeout.Token);
                    await socket.SendAsync(
                        new ArraySegment<byte>(buffer, 0, message.Count),
                        WebSocketMessageType.Text, true, timeout.Token);
                    try
                    {
                        await socket.ReceiveAsync(buffer, timeout.Token);
                    }
                    catch (WebSocketException)
                    {
                        received.TrySetResult(true);
                    }
                },
                useTls: true,
                useWebSockets: true);
            await using var capture = await ScriptedCapture.StartAsync();
            using var client = new ClientWebSocket();
            client.Options.Proxy = new WebProxy($"http://127.0.0.1:{capture.ProxyPort}");
            client.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            await client.ConnectAsync(
                new Uri($"wss://127.0.0.1:{origin.Port}/socket"), timeout.Token);
            await client.SendAsync(
                Encoding.UTF8.GetBytes("ping"), WebSocketMessageType.Text, true, timeout.Token);
            var reply = new byte[32];
            Assert.Equal(4, (await client.ReceiveAsync(reply, timeout.Token)).Count);

            await capture.EnableScriptAsync(
                WebServiceCaptureScriptableEventType.SslConnectionRequest,
                "if (Params.Request.Host == \"127.0.0.1\") { Params.Continue = false; }");
            await Assert.ThrowsAsync<WebSocketException>(
                () => client.ReceiveAsync(reply, timeout.Token));
            await received.Task.WaitAsync(timeout.Token);
        }

        // The BlockList is checked after the request script, so it must wait for the script too.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task WebRequestScript_ThatAllowsRequest_DoesNotBypassBlockList(bool awaitsFirst)
        {
            await using var origin = await LoopbackOrigin.StartAsync();
            await using var capture = await ScriptedCapture.StartAsync();
            capture.Controller.BlockList.Add("127.0.0.1");
            await capture.EnableScriptAsync(
                WebServiceCaptureScriptableEventType.WebRequest,
                Script(awaitsFirst, "Params.Continue = true;"));

            Exception failure = await Record.ExceptionAsync(
                () => capture.Client.GetStringAsync(origin.Url("/blocked-host")));
            await capture.DrainCallbacksAsync();

            Assert.Empty(origin.Requests);
            Assert.IsAssignableFrom<HttpRequestException>(failure);
        }

        // The request row is added after the request script. If the request is forwarded
        // before that, its response arrives first and is never recorded.
        [Fact]
        public async Task WebRequestScript_ThatAwaits_StillCapturesResponse()
        {
            await using var origin = await LoopbackOrigin.StartAsync();
            await using var capture = await ScriptedCapture.StartAsync();
            await capture.EnableScriptAsync(
                WebServiceCaptureScriptableEventType.WebRequest,
                "await System.Threading.Tasks.Task.Delay(250);");

            string body = await capture.Client.GetStringAsync(origin.Url("/slow-script"));
            await capture.DrainCallbacksAsync();

            Assert.Equal("origin-ok", body);
            Assert.Equal("200", capture.CapturedStatus("GET"));
        }

        [Fact]
        public async Task WebResponseScript_ThatAwaits_CanAbortConnectResponse()
        {
            await using var origin = await LoopbackOrigin.StartAsync(useTls: true);
            await using var capture = await ScriptedCapture.StartAsync();
            await capture.EnableScriptAsync(
                WebServiceCaptureScriptableEventType.WebResponse,
                "if (Params.Request.Method == \"CONNECT\") { " +
                "await System.Threading.Tasks.Task.Delay(10); Params.Continue = false; }");

            Exception failure = await Record.ExceptionAsync(
                () => capture.Client.GetStringAsync(origin.Url("/connect-response")));
            await capture.DrainCallbacksAsync();

            Assert.Empty(origin.Requests);
            Assert.IsAssignableFrom<HttpRequestException>(failure);
        }

        [Fact]
        public async Task WebRequestScript_ThatAwaits_BlocksHttp2Request()
        {
            await using var origin = await LoopbackOrigin.StartAsync(useTls: true);
            await using var capture = await ScriptedCapture.StartAsync();
            await capture.EnableScriptAsync(
                WebServiceCaptureScriptableEventType.WebRequest,
                Script(true, "Params.Continue = false;"));
            using var request = Http2Request(origin.Url("/http2-blocked"));

            Exception failure = await Record.ExceptionAsync(
                () => capture.Client.SendAsync(request));
            await capture.DrainCallbacksAsync();

            Assert.Empty(origin.Requests);
            Assert.IsAssignableFrom<HttpRequestException>(failure);
        }

        [Fact]
        public async Task WebResponseScript_ThatAwaits_ChangesHttp2Response()
        {
            await using var origin = await LoopbackOrigin.StartAsync(useTls: true);
            await using var capture = await ScriptedCapture.StartAsync();
            await capture.EnableScriptAsync(
                WebServiceCaptureScriptableEventType.WebResponse,
                Script(
                    true,
                    "if (Params.Request.Method != \"CONNECT\") { Params.Response.Status = \"503\"; }"));
            using var request = Http2Request(origin.Url("/http2-status"));

            using HttpResponseMessage response = await capture.Client.SendAsync(request);
            await capture.DrainCallbacksAsync();

            Assert.Equal(HttpVersion.Version20, response.Version);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("503", capture.CapturedStatus("GET"));
        }

        [Fact]
        public async Task WebRequestScript_ExceptionAfterAwait_FailsOpen()
        {
            await using var origin = await LoopbackOrigin.StartAsync();
            await using var capture = await ScriptedCapture.StartAsync();
            await capture.EnableScriptAsync(
                WebServiceCaptureScriptableEventType.WebRequest,
                "await System.Threading.Tasks.Task.Delay(10); " +
                "throw new System.InvalidOperationException(\"script failure\");");

            string body = await capture.Client.GetStringAsync(origin.Url("/script-error"));
            await capture.DrainCallbacksAsync();

            Assert.Equal("origin-ok", body);
            Assert.Single(origin.Requests);
            Assert.Equal("200", capture.CapturedStatus("GET"));
        }

        [Fact]
        public async Task WebRequestScript_TimeoutAbortsWithoutLateRowUpdate()
        {
            await using var capture = await ScriptedCapture.StartAsync(
                scriptExecutionDeadline: TimeSpan.FromSeconds(2));
            await capture.WarmScriptRunnerAsync(
                WebServiceCaptureScriptableEventType.WebRequest);
            await capture.EnableScriptAsync(
                WebServiceCaptureScriptableEventType.WebRequest,
                "Params.Request.Path = \"/started\"; " +
                "await System.Threading.Tasks.Task.Run(() => { " +
                "System.Threading.Thread.Sleep(4000); Params.Request.Path = \"/late\"; });");
            ClientRequest request = Request("/initial");

            var stopwatch = Stopwatch.StartNew();
            Task<bool> decision = capture.Proxy.RaiseReceivedWebRequestAsync(
                connectionID: 1000,
                request: request,
                cancellationToken: CancellationToken.None);
            await WaitForAsync(
                () => request.Path == "/started",
                TimeSpan.FromMilliseconds(1500));

            await AssertAbortedWithinAsync(
                decision,
                stopwatch,
                TimeSpan.FromSeconds(4));
            await capture.DrainCallbacksAsync();
            await WaitForAsync(
                () => request.Path == "/late",
                TimeSpan.FromSeconds(6));
            await AssertInvariantRemainsAsync(
                () => capture.CapturedRecordCount == 0,
                TimeSpan.FromMilliseconds(750));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task WebRequestScript_CancellationAbortsWithoutLateRowUpdate(
            bool cancelCapture)
        {
            await using var capture = await ScriptedCapture.StartAsync();
            await capture.WarmScriptRunnerAsync(
                WebServiceCaptureScriptableEventType.WebRequest);
            await capture.EnableScriptAsync(
                WebServiceCaptureScriptableEventType.WebRequest,
                "Params.Request.Path = \"/started\"; " +
                "await System.Threading.Tasks.Task.Delay(4000); " +
                "Params.Request.Path = \"/late\";");
            ClientRequest request = Request("/initial");
            using var cancellation = new CancellationTokenSource();

            if (cancelCapture)
            {
                capture.CancelCaptureAfter(TimeSpan.FromSeconds(2));
            }
            else
            {
                cancellation.CancelAfter(TimeSpan.FromSeconds(2));
            }

            var stopwatch = Stopwatch.StartNew();
            Task<bool> decision = capture.Proxy.RaiseReceivedWebRequestAsync(
                connectionID: 1000,
                request: request,
                cancellationToken: cancellation.Token);

            await AssertAbortedWithinAsync(
                decision,
                stopwatch,
                TimeSpan.FromSeconds(4));
            Assert.Equal("/started", request.Path);
            await capture.DrainCallbacksAsync();
            await Task.Delay(TimeSpan.FromSeconds(5));
            Assert.Equal(0, capture.CapturedRecordCount);
        }

        [Fact]
        public async Task WebRequestScript_SynchronousSleepDeadlineAbortsPromptly()
        {
            await using var capture = await ScriptedCapture.StartAsync(
                scriptExecutionDeadline: TimeSpan.FromSeconds(2));
            await capture.WarmScriptRunnerAsync(
                WebServiceCaptureScriptableEventType.WebRequest);
            await capture.EnableScriptAsync(
                WebServiceCaptureScriptableEventType.WebRequest,
                "Params.Request.Path = \"/started\"; " +
                "System.Threading.Thread.Sleep(4000); " +
                "Params.Request.Path = \"/late\";");
            ClientRequest request = Request("/initial");

            var stopwatch = Stopwatch.StartNew();
            Task<bool> decision = capture.Proxy.RaiseReceivedWebRequestAsync(
                connectionID: 1000,
                request: request,
                cancellationToken: CancellationToken.None);
            await WaitForAsync(
                () => request.Path == "/started",
                TimeSpan.FromMilliseconds(1500));

            await AssertAbortedWithinAsync(
                decision,
                stopwatch,
                TimeSpan.FromSeconds(4));
            await capture.DrainCallbacksAsync();
            await WaitForAsync(
                () => request.Path == "/late",
                TimeSpan.FromSeconds(6));
            await AssertInvariantRemainsAsync(
                () => capture.CapturedRecordCount == 0,
                TimeSpan.FromMilliseconds(750));
        }

        [Fact]
        public async Task WebRequestScript_SynchronousSleepCaptureCancellationAbortsPromptly()
        {
            await using var capture = await ScriptedCapture.StartAsync(
                scriptExecutionDeadline: TimeSpan.FromSeconds(10));
            await capture.WarmScriptRunnerAsync(
                WebServiceCaptureScriptableEventType.WebRequest);
            await capture.EnableScriptAsync(
                WebServiceCaptureScriptableEventType.WebRequest,
                "Params.Request.Path = \"/started\"; " +
                "System.Threading.Thread.Sleep(5000); " +
                "Params.Request.Path = \"/late\";");
            ClientRequest request = Request("/initial");
            capture.CancelCaptureAfter(TimeSpan.FromSeconds(2));

            var stopwatch = Stopwatch.StartNew();
            Task<bool> decision = capture.Proxy.RaiseReceivedWebRequestAsync(
                connectionID: 1000,
                request: request,
                cancellationToken: CancellationToken.None);
            await WaitForAsync(
                () => request.Path == "/started",
                TimeSpan.FromMilliseconds(1500));

            await AssertAbortedWithinAsync(
                decision,
                stopwatch,
                TimeSpan.FromSeconds(4));
            await capture.DrainCallbacksAsync();
            await WaitForAsync(
                () => request.Path == "/late",
                TimeSpan.FromSeconds(7));
            await AssertInvariantRemainsAsync(
                () => capture.CapturedRecordCount == 0,
                TimeSpan.FromMilliseconds(750));
        }

        [Fact]
        public async Task SslConnectionScript_DeadlineAbortsWithoutLateRowUpdate()
        {
            await using var capture = await ScriptedCapture.StartAsync(
                scriptExecutionDeadline: TimeSpan.FromSeconds(2));
            await capture.WarmScriptRunnerAsync(
                WebServiceCaptureScriptableEventType.SslConnectionRequest);
            await capture.EnableScriptAsync(
                WebServiceCaptureScriptableEventType.SslConnectionRequest,
                "Params.Request.Path = \"/started\"; " +
                "System.Threading.Thread.Sleep(4000); " +
                "Params.Request.Path = \"/late\";");
            ClientRequest request = ConnectRequest("example.test");

            var stopwatch = Stopwatch.StartNew();
            Task<bool> decision = capture.Proxy.RaiseReceivedSslConnectionRequestAsync(
                connectionID: 1000,
                clientIP: "127.0.0.1",
                request: request,
                cancellationToken: CancellationToken.None);
            await WaitForAsync(
                () => request.Path == "/started",
                TimeSpan.FromMilliseconds(1500));

            await AssertAbortedWithinAsync(
                decision,
                stopwatch,
                TimeSpan.FromSeconds(4));
            await capture.DrainCallbacksAsync();
            await WaitForAsync(
                () => request.Path == "/late",
                TimeSpan.FromSeconds(6));
            await AssertInvariantRemainsAsync(
                () => capture.CapturedRecordCount == 0,
                TimeSpan.FromMilliseconds(750));
        }

        [Fact]
        public async Task WebResponseScript_DeadlineAbortsResponseWithoutLateRowUpdate()
        {
            await using var origin = await LoopbackOrigin.StartAsync();
            await using var capture = await ScriptedCapture.StartAsync(
                scriptExecutionDeadline: TimeSpan.FromSeconds(2));
            await capture.WarmScriptRunnerAsync(
                WebServiceCaptureScriptableEventType.WebResponse);
            await capture.EnableScriptAsync(
                WebServiceCaptureScriptableEventType.WebResponse,
                "if (Params.Request.Method != \"CONNECT\") { " +
                "Params.Response.Status = \"201\"; " +
                "System.Threading.Thread.Sleep(4000); " +
                "Params.Response.Status = \"599\"; }");

            string authority = $"127.0.0.1:{origin.Port}";
            (byte[] responseBytes, TimeSpan elapsed) = await SendSingleProxyRequestAsync(
                capture.ProxyPort,
                $"GET http://{authority}/deadline-response HTTP/1.1\r\n" +
                $"Host: {authority}\r\nConnection: close\r\n\r\n",
                TimeSpan.FromMilliseconds(3500));

            Assert.True(
                elapsed < TimeSpan.FromMilliseconds(3500),
                $"Response decision took {elapsed}.");
            Assert.Empty(responseBytes);
            Assert.Single(origin.Requests);
            await capture.DrainCallbacksAsync();
            await WaitForAsync(
                () => capture.ObservedResponses.Any(response => response.Status == "599"),
                TimeSpan.FromSeconds(6));
            await AssertInvariantRemainsAsync(
                () => capture.CapturedStatus("GET") == string.Empty,
                TimeSpan.FromMilliseconds(750));
        }

        [Fact]
        public async Task WebResponseScript_ConnectDeadlineAbortsTunnelWithoutLateRowUpdate()
        {
            await using var origin = await LoopbackOrigin.StartAsync(useTls: true);
            await using var capture = await ScriptedCapture.StartAsync(
                scriptExecutionDeadline: TimeSpan.FromSeconds(2));
            await capture.WarmScriptRunnerAsync(
                WebServiceCaptureScriptableEventType.WebResponse);
            await capture.EnableScriptAsync(
                WebServiceCaptureScriptableEventType.WebResponse,
                "if (Params.Request.Method == \"CONNECT\") { " +
                "Params.Response.Status = \"201\"; " +
                "System.Threading.Thread.Sleep(4000); " +
                "Params.Response.Status = \"599\"; }");

            string authority = $"127.0.0.1:{origin.Port}";
            (byte[] responseBytes, TimeSpan elapsed) = await SendSingleProxyRequestAsync(
                capture.ProxyPort,
                $"CONNECT {authority} HTTP/1.1\r\n" +
                $"Host: {authority}\r\nConnection: close\r\n\r\n",
                TimeSpan.FromMilliseconds(3500));

            Assert.True(
                elapsed < TimeSpan.FromMilliseconds(3500),
                $"CONNECT decision took {elapsed}.");
            Assert.Empty(responseBytes);
            Assert.Empty(origin.Requests);
            await capture.DrainCallbacksAsync();
            await WaitForAsync(
                () => capture.ObservedResponses.Any(response => response.Status == "599"),
                TimeSpan.FromSeconds(6));
            await AssertInvariantRemainsAsync(
                () => capture.CapturedStatus("CONNECT") == string.Empty,
                TimeSpan.FromMilliseconds(750));
        }

        [Fact]
        public async Task WebRequestScript_AbandonedFaultIsObserved()
        {
            const string marker = "xmat-abandoned-script-fault";
            var capture = await ScriptedCapture.StartAsync(
                scriptExecutionDeadline: TimeSpan.FromSeconds(2));
            var unobserved = 0;
            var handlerAttached = false;

            void OnUnobserved(object sender, UnobservedTaskExceptionEventArgs args)
            {
                if (args.Exception.Flatten().InnerExceptions.Any(
                    exception => exception.Message.Contains(marker, StringComparison.Ordinal)))
                {
                    Interlocked.Exchange(ref unobserved, 1);
                    args.SetObserved();
                }
            }

            try
            {
                await capture.WarmScriptRunnerAsync(
                    WebServiceCaptureScriptableEventType.WebRequest);
                await capture.EnableScriptAsync(
                    WebServiceCaptureScriptableEventType.WebRequest,
                    "Params.Request.Path = \"/started\"; " +
                    "System.Threading.Thread.Sleep(3000); " +
                    $"throw new System.InvalidOperationException(\"{marker}\");");
                TaskScheduler.UnobservedTaskException += OnUnobserved;
                handlerAttached = true;

                await InvokeAbandonedFaultAsync(capture);
                await capture.DrainCallbacksAsync();
                await Task.Delay(TimeSpan.FromSeconds(4));
                Assert.Equal(0, capture.CapturedRecordCount);
                await capture.DisposeAsync();
                capture = null;

                // UnobservedTaskException is raised by finalization of a faulted, unobserved Task.
                // Force bounded collection so this assertion cannot pass only because finalization never ran.
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    await Task.Delay(100);
                }

                Assert.Equal(0, Volatile.Read(ref unobserved));
            }
            finally
            {
                if (handlerAttached)
                {
                    TaskScheduler.UnobservedTaskException -= OnUnobserved;
                }
                if (capture != null)
                {
                    await capture.DisposeAsync();
                }
            }
        }

        [Fact]
        public async Task WebRequestPolicy_PostAwaitRowFailure_AbortsWithoutEscaping()
        {
            await using var capture = await ScriptedCapture.StartAsync();
            await capture.EnableScriptAsync(
                WebServiceCaptureScriptableEventType.WebRequest,
                "await System.Threading.Tasks.Task.Delay(10);");
            capture.BreakCaptureTable();

            bool accepted = await capture.Proxy.RaiseReceivedWebRequestAsync(
                connectionID: 1000,
                request: Request("/row-failure"),
                cancellationToken: CancellationToken.None);

            Assert.False(accepted);
        }

        private static string Script(bool awaitsFirst, string body) =>
            (awaitsFirst ? "await System.Threading.Tasks.Task.Delay(10); " : string.Empty) + body;

        private static HttpRequestMessage Http2Request(string uri) => new(
            HttpMethod.Get,
            uri)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        private static ClientRequest Request(string path) => new()
        {
            RequestNumber = 1,
            Version = "HTTP/1.1",
            Scheme = "http",
            Host = "example.test",
            Port = 80,
            Method = "GET",
            Path = path
        };

        private static ClientRequest ConnectRequest(string host) => new()
        {
            RequestNumber = 1,
            Version = "HTTP/1.1",
            Scheme = "https",
            Host = host,
            Port = 443,
            Method = "CONNECT",
            Path = $"{host}:443"
        };

        private static async Task AssertAbortedWithinAsync(
            Task<bool> decision,
            Stopwatch stopwatch,
            TimeSpan maximum)
        {
            Assert.False(await decision.WaitAsync(maximum));
            stopwatch.Stop();
            Assert.True(
                stopwatch.Elapsed < maximum,
                $"Policy decision took {stopwatch.Elapsed}.");
        }

        private static async Task AssertInvariantRemainsAsync(
            Func<bool> invariant,
            TimeSpan duration)
        {
            var stopwatch = Stopwatch.StartNew();
            do
            {
                Assert.True(invariant());
                await Task.Delay(25);
            }
            while (stopwatch.Elapsed < duration);
            Assert.True(invariant());
        }

        private static async Task<(byte[] Response, TimeSpan Elapsed)>
            SendSingleProxyRequestAsync(
                int proxyPort,
                string request,
                TimeSpan maximum)
        {
            using var timeout = new CancellationTokenSource(maximum);
            using var client = new TcpClient();
            await client.ConnectAsync(
                IPAddress.Loopback,
                proxyPort,
                timeout.Token);
            using NetworkStream stream = client.GetStream();
            byte[] requestBytes = Encoding.ASCII.GetBytes(request);
            var stopwatch = Stopwatch.StartNew();
            await stream.WriteAsync(requestBytes, timeout.Token);
            await stream.FlushAsync(timeout.Token);

            using var response = new MemoryStream();
            var buffer = new byte[1024];
            while (true)
            {
                int read = await stream.ReadAsync(buffer, timeout.Token);
                if (read == 0)
                {
                    break;
                }
                response.Write(buffer, 0, read);
            }

            stopwatch.Stop();
            return (response.ToArray(), stopwatch.Elapsed);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static async Task InvokeAbandonedFaultAsync(
            ScriptedCapture capture)
        {
            ClientRequest request = Request("/abandoned-fault");
            var stopwatch = Stopwatch.StartNew();
            Task<bool> decision = capture.Proxy.RaiseReceivedWebRequestAsync(
                connectionID: 1000,
                request: request,
                cancellationToken: CancellationToken.None);
            await WaitForAsync(
                () => request.Path == "/started",
                TimeSpan.FromMilliseconds(1500));
            await AssertAbortedWithinAsync(
                decision,
                stopwatch,
                TimeSpan.FromSeconds(4));
        }

        private static async Task WaitForAsync(
            Func<bool> condition,
            TimeSpan? maximum = null)
        {
            using var timeout = new CancellationTokenSource(
                maximum ?? TimeSpan.FromSeconds(3));
            while (!condition())
            {
                await Task.Delay(10, timeout.Token);
            }
        }

        private static byte[] Decompress(byte[] body)
        {
            using var input = new MemoryStream(body);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }

        private static async Task<string> SendHttps11PartialBodyAsync(
            int proxyPort,
            int originPort,
            byte[] body,
            int bodyBytesToSend,
            TimeSpan delayBetweenBytes)
        {
            Assert.InRange(bodyBytesToSend, 1, body.Length - 1);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
            NetworkStream network = client.GetStream();
            const string targetHost = "127.0.0.1";
            string authority = $"{targetHost}:{originPort}";
            await network.WriteAsync(
                Encoding.ASCII.GetBytes($"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n\r\n"),
                timeout.Token);
            string connectResponse = await ReadHeadAsync(network, timeout.Token);
            Assert.StartsWith("HTTP/1.1 200 ", connectResponse, StringComparison.Ordinal);

            using var tls = new System.Net.Security.SslStream(network, leaveInnerStreamOpen: false, (_, _, _, _) => true);
            await tls.AuthenticateAsClientAsync(new System.Net.Security.SslClientAuthenticationOptions
            {
                TargetHost = targetHost,
                ApplicationProtocols = [System.Net.Security.SslApplicationProtocol.Http11],
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.None,
            }, timeout.Token);
            Assert.True(tls.IsAuthenticated);
            Assert.Equal(
                System.Net.Security.SslApplicationProtocol.Http11,
                tls.NegotiatedApplicationProtocol);
            await tls.WriteAsync(
                Encoding.ASCII.GetBytes(
                    $"POST /incomplete HTTP/1.1\r\nHost: {authority}\r\n" +
                    $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n"),
                timeout.Token);

            for (int index = 0; index < bodyBytesToSend; index++)
            {
                await tls.WriteAsync(body.AsMemory(index, 1), timeout.Token);
                await tls.FlushAsync(timeout.Token);
                if (index + 1 < bodyBytesToSend && delayBetweenBytes > TimeSpan.Zero)
                    await Task.Delay(delayBetweenBytes, timeout.Token);
            }

            return await ReadHeadAsync(tls, timeout.Token);
        }

        private static async Task<string> ReadHeadAsync(Stream stream, CancellationToken cancellationToken)
        {
            const int maximumHeadBytes = 16 * 1024;
            byte[] header = new byte[maximumHeadBytes];
            int length = 0;
            while (length < header.Length)
            {
                int searchStart = Math.Max(0, length - 3);
                int read = await stream.ReadAsync(
                    header.AsMemory(length),
                    cancellationToken);
                if (read == 0)
                {
                    throw new EndOfStreamException(
                        "The HTTP response ended before its header block was complete.");
                }
                length += read;
                for (int index = searchStart; index <= length - 4; index++)
                {
                    if (header[index] == '\r' &&
                        header[index + 1] == '\n' &&
                        header[index + 2] == '\r' &&
                        header[index + 3] == '\n')
                    {
                        return Encoding.ASCII.GetString(header, 0, index + 4);
                    }
                }
            }
            throw new InvalidDataException(
                $"The HTTP response header exceeded {maximumHeadBytes} bytes.");
        }

        private sealed class DelayedBodyContent(
            byte[] body,
            int chunkSize,
            TimeSpan initialDelay,
            TimeSpan interChunkDelay) : HttpContent
        {
            protected override bool TryComputeLength(out long length)
            {
                length = body.Length;
                return true;
            }

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext context) =>
                SerializeToStreamAsync(stream, context, CancellationToken.None);

            protected override async Task SerializeToStreamAsync(
                Stream stream,
                TransportContext context,
                CancellationToken cancellationToken)
            {
                if (initialDelay > TimeSpan.Zero)
                    await Task.Delay(initialDelay, cancellationToken);

                for (int offset = 0; offset < body.Length; offset += chunkSize)
                {
                    int count = Math.Min(chunkSize, body.Length - offset);
                    await stream.WriteAsync(body.AsMemory(offset, count), cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    if (offset + count < body.Length && interChunkDelay > TimeSpan.Zero)
                        await Task.Delay(interChunkDelay, cancellationToken);
                }
            }
        }

        private sealed class GatedBodyContent(byte[] body) : HttpContent
        {
            private readonly TaskCompletionSource _firstByteWritten =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _release =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal Task FirstByteWritten => _firstByteWritten.Task;

            internal void Release() =>
                _release.TrySetResult();

            protected override bool TryComputeLength(out long length)
            {
                length = body.Length;
                return true;
            }

            protected override Task SerializeToStreamAsync(
                Stream stream,
                TransportContext context) =>
                SerializeToStreamAsync(stream, context, CancellationToken.None);

            protected override async Task SerializeToStreamAsync(
                Stream stream,
                TransportContext context,
                CancellationToken cancellationToken)
            {
                await stream.WriteAsync(body.AsMemory(0, 1), cancellationToken);
                await stream.FlushAsync(cancellationToken);
                _firstByteWritten.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
                await stream.WriteAsync(body.AsMemory(1), cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
        }

        private sealed class UnknownLengthBodyContent(
            byte[] body,
            int firstChunkSize,
            TimeSpan interChunkDelay) : HttpContent
        {
            protected override bool TryComputeLength(out long length)
            {
                length = 0;
                return false;
            }

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext context) =>
                SerializeToStreamAsync(stream, context, CancellationToken.None);

            protected override async Task SerializeToStreamAsync(
                Stream stream,
                TransportContext context,
                CancellationToken cancellationToken)
            {
                await stream.WriteAsync(body.AsMemory(0, firstChunkSize), cancellationToken);
                await stream.FlushAsync(cancellationToken);
                await Task.Delay(interChunkDelay, cancellationToken);
                await stream.WriteAsync(body.AsMemory(firstChunkSize), cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
        }

        // Attaches a capture controller's policy hooks to the in-process proxy with an
        // in-memory capture table.
        private sealed class ScriptedCapture : IAsyncDisposable
        {
            private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

            private readonly ProxyTestHarness _proxy;
            private readonly SqLtDatabase _database;
            private readonly IDataTable _table;
            private readonly AsyncOperationTracker _callbacks;
            private readonly CancellationTokenSource _captureCancellation;

            public WebServiceDeviceCaptureController Controller { get; }
            public HttpClient Client { get; }
            public WebServiceProxy Proxy => _proxy.Proxy;
            public int ProxyPort => _proxy.Port;
            public int CapturedRecordCount => _table.Dataset.DataRecords.Count();
            public IReadOnlyCollection<ServerResponse> ObservedResponses => _proxy.CapturedResponses;
            public IReadOnlyCollection<ClientRequest> CapturedRequests => _proxy.CapturedRequests;

            private ScriptedCapture(
                ProxyTestHarness proxy,
                WebServiceDeviceCaptureController controller,
                SqLtDatabase database,
                IDataTable table,
                AsyncOperationTracker callbacks,
                CancellationTokenSource captureCancellation)
            {
                _proxy = proxy;
                Controller = controller;
                _database = database;
                _table = table;
                _callbacks = callbacks;
                _captureCancellation = captureCancellation;
                Client = proxy.CreateClient();
            }

            public static async Task<ScriptedCapture> StartAsync(
                TimeSpan? scriptExecutionDeadline = null,
                ProxyRequestReadPolicy requestPolicy = null,
                ProxyRequestBufferBudget requestBufferBudget = null,
                Action<int, LogLevel, string> logObserver = null)
            {
                var controller = new WebServiceDeviceCaptureController(
                    "script-test",
                    DeviceType.GenericProxyDevice,
                    false,
                    new ProxyPortPool(),
                    scriptExecutionDeadline: scriptExecutionDeadline);
                var database = new SqLtDatabase();
                database.Initialize();
                IDataTable table = CreateConnectionsTable(database);
                typeof(WebServiceDeviceCaptureController)
                    .GetField("_dataTable", PrivateInstance)
                    .SetValue(controller, table);
                var callbacks = (AsyncOperationTracker)typeof(WebServiceDeviceCaptureController)
                    .GetField("_captureCallbacks", PrivateInstance)
                    .GetValue(controller);
                callbacks.StartAccepting();
                var captureCancellation = new CancellationTokenSource();
                typeof(WebServiceDeviceCaptureController)
                    .GetField("_captureCallbackCancellationTokenSource", PrivateInstance)
                    .SetValue(controller, captureCancellation);

                ProxyTestHarness proxy = await ProxyTestHarness.StartAsync(
                    requestPolicy: requestPolicy,
                    requestBufferBudget: requestBufferBudget,
                    logObserver: logObserver);
                typeof(WebServiceDeviceCaptureController)
                    .GetField("_webProxy", PrivateInstance)
                    .SetValue(controller, proxy.Proxy);
                proxy.Proxy.SslConnectionRequestPolicyAsync = Policy<SslConnectionRequestEventArgs>(
                    controller, "WebProxy_ReceivedSslConnectionRequestAsync");
                proxy.Proxy.WebRequestPolicyAsync = Policy<HttpRequestEventArgs>(
                    controller, "WebProxy_ReceivedWebRequestAsync");
                proxy.Proxy.WebResponsePolicyAsync = Policy<HttpResponseEventArgs>(
                    controller, "WebProxy_ReceivedWebResponseAsync");
                return new ScriptedCapture(
                    proxy,
                    controller,
                    database,
                    table,
                    callbacks,
                    captureCancellation);
            }

            public async Task EnableScriptAsync(
                WebServiceCaptureScriptableEventType eventType,
                string script)
            {
                ScriptModel model = Controller.Scripts[eventType];
                model.Script = script;
                Assert.Equal(
                    ScriptActivationResult.Enabled,
                    await model.EnableScriptAsync<WebServiceCaptureScriptParams>());
            }

            public async Task WarmScriptRunnerAsync(
                WebServiceCaptureScriptableEventType eventType)
            {
                ScriptModel model = Controller.Scripts[eventType];
                model.Script = "Params.Continue = true;";
                Assert.Equal(
                    ScriptActivationResult.Enabled,
                    await model.EnableScriptAsync<WebServiceCaptureScriptParams>());
                var parameters = new WebServiceCaptureScriptParams
                {
                    Request = Request("/warmup"),
                    Response = new ServerResponse
                    {
                        Version = "HTTP/1.1",
                        Status = "200",
                        StatusDescription = "OK"
                    }
                };
                Assert.Same(parameters, await model.RunScriptAsync(parameters));
                model.DisableScript();
            }

            // Waits for active capture policies. Timed-out scripts are intentionally detached.
            public Task DrainCallbacksAsync() =>
                _callbacks.StopAcceptingAndWaitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            public IDataRecord CapturedRow(string method) =>
                Assert.Single(
                    _table.Dataset.DataRecords,
                    record => record.Str(WebServiceCaptureMethod.FieldKey_RequestMethod) == method);

            public string CapturedStatus(string method) =>
                CapturedRow(method).Str(WebServiceCaptureMethod.FieldKey_RequestStatus);

            public void BreakCaptureTable()
            {
                typeof(WebServiceDeviceCaptureController)
                    .GetField("_dataTable", PrivateInstance)
                    .SetValue(Controller, null);
            }

            public void CancelCaptureAfter(TimeSpan delay) =>
                _captureCancellation.CancelAfter(delay);

            public async ValueTask DisposeAsync()
            {
                Client.Dispose();
                try
                {
                    await _captureCancellation.CancelAsync();
                    await DrainCallbacksAsync();
                }
                finally
                {
                    await _proxy.DisposeAsync();
                    _captureCancellation.Dispose();
                    _database.Dispose();
                }
            }

            private static Func<TEventArgs, CancellationToken, Task<bool>> Policy<TEventArgs>(
                WebServiceDeviceCaptureController controller,
                string methodName) where TEventArgs : EventArgs =>
                typeof(WebServiceDeviceCaptureController)
                    .GetMethod(methodName, PrivateInstance)
                    .CreateDelegate<Func<TEventArgs, CancellationToken, Task<bool>>>(controller);

            // Same layout as WebServiceCaptureMethod.InitializeDataTables().
            private static IDataTable CreateConnectionsTable(SqLtDatabase database) =>
                database.CreateTable(
                    WebServiceCaptureMethod.TableName,
                    WebProxyCaptureSchema.CreateFields());
        }
    }
}
