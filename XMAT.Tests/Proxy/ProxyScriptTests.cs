// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
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
                TimeSpan? scriptExecutionDeadline = null)
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

                ProxyTestHarness proxy = await ProxyTestHarness.StartAsync();
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

            public string CapturedStatus(string method) =>
                Assert.Single(
                    _table.Dataset.DataRecords,
                    record => record.Str(WebServiceCaptureMethod.FieldKey_RequestMethod) == method)
                .Str(WebServiceCaptureMethod.FieldKey_RequestStatus);

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
                    new Field<string>(WebServiceCaptureMethod.FieldKey_DeviceName),
                    new Field<long>(WebServiceCaptureMethod.FieldKey_RequestNumber),
                    new Field<long>(WebServiceCaptureMethod.FieldKey_ConnectionId),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_RequestTimestamp),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_ResponseTimestamp),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_RequestScheme),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_RequestHost),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_RequestPort),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_RequestMethod),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_RequestPath),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_RequestStatus),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_RequestBody),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_ResponseBody),
                    new Field<string>(WebServiceCaptureMethod.FieldKey_ClientIP));
        }
    }
}
