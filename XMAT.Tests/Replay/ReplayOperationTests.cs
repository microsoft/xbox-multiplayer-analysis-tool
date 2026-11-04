// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using XMAT.Operations;
using XMAT.Operations.Replay;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Replay
{
    // ReplayOperation end to end, in process: capture files and requests that target loopback servers only.
    public sealed class ReplayOperationTests : IDisposable
    {
        private readonly string _directory = Path.Join(
            AppContext.BaseDirectory, "HeadlessTestArtifacts", "Replay", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        [Fact]
        public async Task CaptureFile_ReplaysTheSelectionInPlanOrderAndLeavesTheCaptureUnchanged()
        {
            await using var server = new LoopbackHttpServer(request => LoopbackResponse.Ok("reply " + request.Target));
            string capture = ReplayCaptures.WriteSaz(
                _directory,
                "capture.saz",
                new CapturedExchange("GET", server.Origin + "/one"),
                new CapturedExchange("GET", server.Origin + "/two"),
                new CapturedExchange("POST", server.Origin + "/three", Body: "x"u8.ToArray()));
            string sha256 = Sha256(capture);
            var sink = new RecordingSink();

            OperationResult<ReplayResult> result = await Execute(
                new ReplayRequest(ReplaySource.FromFile(capture, Selection("1-2")), new ReplayOptions(Repeat: 2), Sink: sink));

            Assert.True(result.Succeeded, Describe(result));
            Assert.Equal(("saz", sha256), (result.Value.InputKind, result.Value.InputSha256));
            Assert.Equal(new[] { "/one", "/two", "/one", "/two" }, server.Requests.Select(request => request.Target));
            Assert.Equal(new[] { 1, 2, 3, 4 }, result.Value.Items.Select(item => item.Id));
            Assert.Equal(new long?[] { 1, 2, 1, 2 }, result.Value.Items.Select(item => item.RequestNumber));
            Assert.Equal(new[] { 1, 1, 2, 2 }, result.Value.Items.Select(item => item.Iteration));
            Assert.All(
                result.Value.Items,
                item => Assert.Equal((ReplayOutcome.Completed, 200, "d1"), (item.Outcome, item.StatusCode, item.DeviceId)));
            Assert.Equal(new[] { 1, 2, 3, 4 }, sink.Results.Select(item => item.Id));
            Assert.Equal("reply /two", Encoding.UTF8.GetString(sink.Exchanges[1].Response.Body));
            Assert.Equal(sha256, Sha256(capture));
        }

        [Fact]
        public async Task Policy_DeniesUnsafeMethodsAndOriginChangesBeforeSendingAnything()
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            await using var other = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            string capture = ReplayCaptures.WriteSaz(
                _directory,
                "capture.saz",
                new CapturedExchange("GET", server.Origin + "/one"),
                new CapturedExchange("POST", server.Origin + "/two", Body: "x"u8.ToArray()));

            OperationResult<ReplayResult> unsafeMethod = await Execute(
                new ReplayRequest(ReplaySource.FromFile(capture, Selection("1-2"))));
            OperationResult<ReplayResult> originChange = await Execute(
                new ReplayRequest(ReplaySource.FromFile(capture, Selection("1")), Edits: new ReplayEdits(Origin: other.Origin)));
            OperationResult<ReplayResult> unsafeEdit = await Execute(
                new ReplayRequest(ReplaySource.FromFile(capture, Selection("1")), Edits: new ReplayEdits(Method: "DELETE")));

            AssertFailure(unsafeMethod, OperationOutcome.InvalidInput, "replay.policy.unsafe-method");
            AssertFailure(originChange, OperationOutcome.InvalidInput, "replay.policy.origin-change");
            AssertFailure(unsafeEdit, OperationOutcome.InvalidInput, "replay.policy.unsafe-method");
            Assert.Empty(server.Requests);
            Assert.Empty(other.Requests);

            OperationResult<ReplayResult> allowedMethod = await Execute(new ReplayRequest(
                ReplaySource.FromFile(capture, Selection("1-2")), new ReplayOptions(AllowUnsafeMethods: true)));
            OperationResult<ReplayResult> allowedOrigin = await Execute(new ReplayRequest(
                ReplaySource.FromFile(capture, Selection("1")),
                new ReplayOptions(AllowOriginChange: true),
                new ReplayEdits(Origin: other.Origin)));

            Assert.True(allowedMethod.Succeeded, Describe(allowedMethod));
            Assert.True(allowedOrigin.Succeeded, Describe(allowedOrigin));
            Assert.Equal(new[] { "POST" }, server.Requests.Where(request => request.Target == "/two").Select(request => request.Method));
            Assert.Equal(new[] { "/one" }, other.Requests.Select(request => request.Target));
        }

        [Theory]
        [InlineData("9", null, "replay.request.not-found")]
        [InlineData("1,9", null, "replay.request.not-found")]
        [InlineData("20-30", null, "replay.request.not-found")]
        [InlineData("2", null, "replay.request.unsupported")]
        [InlineData("1", "d2", "replay.device.not-found")]
        public async Task Selection_FailsBeforeSendingAnything(string selection, string device, string code)
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            string capture = WriteCaptureWithTunnel(server);

            OperationResult<ReplayResult> result = await Execute(
                new ReplayRequest(ReplaySource.FromFile(capture, Selection(selection), device)));

            AssertFailure(result, OperationOutcome.InvalidInput, code);
            Assert.Empty(server.Requests);
        }

        [Fact]
        public async Task Selection_SkipsWhatCannotBeReplayedInARangeWithAWarning()
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            string capture = WriteCaptureWithTunnel(server);

            OperationResult<ReplayResult> result = await Execute(
                new ReplayRequest(ReplaySource.FromFile(capture, Selection("1-3"))));

            Assert.True(result.Succeeded, Describe(result));
            Assert.Equal(new[] { ReplayOperation.SkippedCode }, result.Diagnostics.Select(diagnostic => diagnostic.Code));
            Assert.Equal(new[] { "/one", "/three" }, server.Requests.Select(request => request.Target));
        }

        [Fact]
        public async Task Selection_NeedsADeviceWhenANumberIsOnSeveral()
        {
            await using var server = new LoopbackHttpServer(request => LoopbackResponse.Ok());
            string capture = ReplayCaptures.WriteXmatcap(
                _directory,
                "capture.xmatcap",
                ("Console A", new[] { new CapturedExchange("GET", server.Origin + "/a1") }),
                ("Console B", new[] { new CapturedExchange("GET", server.Origin + "/b1") }));

            OperationResult<ReplayResult> ambiguous = await Execute(
                new ReplayRequest(ReplaySource.FromFile(capture, Selection("1"))));
            OperationResult<ReplayResult> second = await Execute(
                new ReplayRequest(ReplaySource.FromFile(capture, Selection("1"), "d2")));
            OperationResult<ReplayResult> all = await Execute(
                new ReplayRequest(ReplaySource.FromFile(capture, ReplaySelection.All)));

            AssertFailure(ambiguous, OperationOutcome.InvalidInput, "replay.request.ambiguous");
            Assert.True(second.Succeeded, Describe(second));
            Assert.Equal("xmatcap", second.Value.InputKind);
            Assert.Equal("d2", Assert.Single(second.Value.Items).DeviceId);
            Assert.Equal(new[] { "d1", "d2" }, all.Value.Items.Select(item => item.DeviceId));
            Assert.Equal(new[] { "/b1", "/a1", "/b1" }, server.Requests.Select(request => request.Target));
        }

        [Fact]
        public async Task Edits_ApplyToEveryRequestBeforeItIsSent()
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            string capture = ReplayCaptures.WriteSaz(
                _directory,
                "capture.saz",
                new CapturedExchange(
                    "GET",
                    server.Origin + "/one",
                    "Accept: text/plain\r\nIf-None-Match: \"v1\"\r\nIf-Modified-Since: Thu, 01 Jan 2026 00:00:00 GMT\r\n" +
                        "X-Remove: me\r\nX-Keep: kept\r\n"),
                new CapturedExchange("GET", server.Origin + "/two?q=1"));

            OperationResult<ReplayResult> result = await Execute(new ReplayRequest(
                ReplaySource.FromFile(capture, Selection("1,2")),
                new ReplayOptions(AllowUnsafeMethods: true),
                new ReplayEdits(
                    Method: "PUT",
                    SetHeaders: new[] { new KeyValuePair<string, string>("Accept", "application/json") },
                    RemoveHeaders: new[] { "x-remove" },
                    Body: "edited"u8.ToArray(),
                    Unconditional: true)));

            Assert.True(result.Succeeded, Describe(result));
            Assert.Equal(new[] { "/one", "/two?q=1" }, server.Requests.Select(request => request.Target));
            Assert.All(server.Requests, request =>
            {
                Assert.Equal("PUT", request.Method);
                Assert.Equal("application/json", request.Header("Accept"));
                Assert.Equal("edited"u8.ToArray(), request.Body);
                Assert.Null(request.Header("X-Remove"));
                Assert.Null(request.Header("If-None-Match"));
                Assert.Null(request.Header("If-Modified-Since"));
            });
            Assert.Equal("kept", server.Requests[0].Header("X-Keep"));
        }

        [Fact]
        public async Task Edits_ANewUrlNeedsExactlyOneRequest()
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            string capture = ReplayCaptures.WriteSaz(
                _directory,
                "capture.saz",
                new CapturedExchange("GET", server.Origin + "/one"),
                new CapturedExchange("GET", server.Origin + "/two"));

            OperationResult<ReplayResult> two = await Execute(new ReplayRequest(
                ReplaySource.FromFile(capture, Selection("1-2")), Edits: new ReplayEdits(Url: server.Origin + "/new")));
            OperationResult<ReplayResult> one = await Execute(new ReplayRequest(
                ReplaySource.FromFile(capture, Selection("2")), Edits: new ReplayEdits(Url: server.Origin + "/new?x=1")));
            OperationResult<ReplayResult> invalid = await Execute(new ReplayRequest(
                ReplaySource.FromFile(capture, Selection("2")), Edits: new ReplayEdits(Method: "BAD METHOD")));

            AssertFailure(two, OperationOutcome.InvalidInput, "replay.edit.url-needs-one-request");
            AssertFailure(invalid, OperationOutcome.InvalidInput, "replay.request.invalid");
            Assert.True(one.Succeeded, Describe(one));
            Assert.Equal(new[] { "/new?x=1" }, server.Requests.Select(request => request.Target));
        }

        [Fact]
        public async Task Verify_ComparesStatusAndDecodedBodyWithTheCapturedResponse()
        {
            await using var server = new LoopbackHttpServer(request => request.Target switch
            {
                "/status" => new LoopbackResponse(404, "Not Found", Body: "{\"ok\":true}"u8.ToArray()),
                "/body" => LoopbackResponse.Ok("{\"ok\":false}"),
                _ => LoopbackResponse.Ok("{\"ok\":true}"),
            });
            const string Json = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n";
            byte[] content = "{\"ok\":true}"u8.ToArray();
            string capture = ReplayCaptures.WriteSaz(
                _directory,
                "capture.saz",
                new CapturedExchange("GET", server.Origin + "/same", ResponseHead: Json, ResponseBody: content),
                new CapturedExchange("GET", server.Origin + "/status", ResponseHead: Json, ResponseBody: content),
                new CapturedExchange("GET", server.Origin + "/body", ResponseHead: Json, ResponseBody: content),
                new CapturedExchange(
                    "GET",
                    server.Origin + "/chunked",
                    ResponseHead: Json + "Transfer-Encoding: chunked\r\n",
                    ResponseBody: "b\r\n{\"ok\":true}\r\n0\r\n\r\n"u8.ToArray()));

            OperationResult<ReplayResult> result = await Execute(new ReplayRequest(
                ReplaySource.FromFile(capture, ReplaySelection.All), new ReplayOptions(Verify: true)));

            Assert.True(result.Succeeded, Describe(result));
            Assert.Equal(
                new[] { "", "status", "body", "" },
                result.Value.Items.Select(item => string.Join(",", item.Verification.Differences)));
            Assert.Equal((2, 2), (result.Value.Matched, result.Value.Mismatched));
        }

        [Fact]
        public async Task Parallelism_SendsConcurrentlyAndStillReportsInPlanOrder()
        {
            var bothArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int arrived = 0;
            await using var server = new LoopbackHttpServer(async request =>
            {
                if (Interlocked.Increment(ref arrived) == 2)
                {
                    bothArrived.TrySetResult();
                }

                // Only a concurrent second request releases the first one.
                await bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
                return LoopbackResponse.Ok(request.Target);
            });
            string capture = ReplayCaptures.WriteSaz(
                _directory,
                "capture.saz",
                new CapturedExchange("GET", server.Origin + "/one"),
                new CapturedExchange("GET", server.Origin + "/two"));
            var sink = new RecordingSink();

            OperationResult<ReplayResult> result = await Execute(new ReplayRequest(
                ReplaySource.FromFile(capture, ReplaySelection.All), new ReplayOptions(Parallelism: 2), Sink: sink));

            Assert.True(result.Succeeded, Describe(result));
            Assert.All(result.Value.Items, item => Assert.Equal(ReplayOutcome.Completed, item.Outcome));
            Assert.Equal(new[] { 1, 2 }, result.Value.Items.Select(item => item.Id));
            Assert.Equal(new[] { "/one", "/two" }, result.Value.Items.Select(item => sink.BodyOf(item.Id)));
        }

        [Fact]
        public async Task Cancellation_EndsTheRunAsCancelled()
        {
            var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var server = new LoopbackHttpServer(_ =>
            {
                arrived.TrySetResult();
                return new LoopbackResponse(Hang: true);
            });
            string capture = ReplayCaptures.WriteSaz(
                _directory, "capture.saz", new CapturedExchange("GET", server.Origin + "/one"));
            using var cancellation = new CancellationTokenSource();

            Task<OperationResult<ReplayResult>> running = new ReplayOperation().ExecuteAsync(
                new ReplayRequest(ReplaySource.FromFile(capture, Selection("1"))), OperationContext.None, cancellation.Token);
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
            cancellation.Cancel();

            AssertFailure(await running.WaitAsync(TimeSpan.FromSeconds(30)), OperationOutcome.Cancelled, "replay.cancelled");
        }

        [Fact]
        public async Task SinkFailure_PropagatesToTheCaller()
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());

            await Assert.ThrowsAsync<IOException>(() => Execute(new ReplayRequest(
                ReplaySource.FromDraft(new RequestReplayDraft { Url = server.Origin + "/" }),
                Sink: new FailingSink())));
        }

        [Fact]
        public async Task Draft_IsSentOnceWithoutCaptureFields()
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());

            OperationResult<ReplayResult> result = await Execute(new ReplayRequest(
                ReplaySource.FromDraft(new RequestReplayDraft { Url = server.Origin + "/composed" }),
                new ReplayOptions(Verify: true)));

            Assert.True(result.Succeeded, Describe(result));
            ReplayItemResult item = Assert.Single(result.Value.Items);
            Assert.Equal((null, null, null), (item.DeviceId, item.RequestNumber, item.Verification));
            Assert.Equal((ReplayOutcome.Completed, 200), (item.Outcome, item.StatusCode));
            Assert.Null(result.Value.InputKind);
            Assert.Equal("/composed", Assert.Single(server.Requests).Target);
        }

        [Fact]
        public async Task Captures_SkipRowsThatCannotBeReplayed()
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            string authority = $"127.0.0.1:{server.Port}";
            var replayable = new ProxyConnectionModel(
                4, "200", DateTime.UtcNow, TimeSpan.Zero, "http", authority, "GET", "/gui",
                $"GET /gui HTTP/1.1\r\nHost: {authority}\r\n", [], "HTTP/1.1 200 OK\r\n", [], "127.0.0.1");
            var tunnel = new ProxyConnectionModel(
                5, "200", DateTime.UtcNow, TimeSpan.Zero, "http", authority, "CONNECT", authority,
                $"CONNECT {authority} HTTP/1.1\r\n", [], "HTTP/1.1 200 OK\r\n", [], "127.0.0.1");

            OperationResult<ReplayResult> result = await Execute(
                new ReplayRequest(ReplaySource.FromCaptures(new[] { tunnel, replayable })));

            Assert.True(result.Succeeded, Describe(result));
            Assert.Equal(new[] { ReplayOperation.SkippedCode }, result.Diagnostics.Select(diagnostic => diagnostic.Code));
            Assert.Equal(4, Assert.Single(result.Value.Items).RequestNumber);
            Assert.Equal("/gui", Assert.Single(server.Requests).Target);
        }

        [Fact]
        public async Task CaptureInput_FailuresKeepTheAnalysisCodes()
        {
            string missing = Path.Join(_directory, "missing.saz");

            OperationResult<ReplayResult> result = await Execute(
                new ReplayRequest(ReplaySource.FromFile(missing, ReplaySelection.All)));

            AssertFailure(result, OperationOutcome.InvalidInput, "analysis.input.not-found");
        }

        private string WriteCaptureWithTunnel(LoopbackHttpServer server) =>
            ReplayCaptures.WriteSaz(
                _directory,
                "capture.saz",
                new CapturedExchange("GET", server.Origin + "/one"),
                new CapturedExchange("CONNECT", server.Origin + "/", ResponseHead: "HTTP/1.1 200 Connection Established\r\n"),
                new CapturedExchange("GET", server.Origin + "/three"));

        private static Task<OperationResult<ReplayResult>> Execute(ReplayRequest request) =>
            new ReplayOperation().ExecuteAsync(request, OperationContext.None, CancellationToken.None);

        private static ReplaySelection Selection(string text)
        {
            Assert.True(ReplaySelection.TryParse(text, out ReplaySelection selection));
            return selection;
        }

        private static void AssertFailure(OperationResult<ReplayResult> result, OperationOutcome outcome, string code)
        {
            Assert.Equal(outcome, result.Outcome);
            Assert.Null(result.Value);
            Assert.Equal(code, Assert.Single(result.Diagnostics).Code);
        }

        private static string Describe(OperationResult<ReplayResult> result) =>
            $"{result.Outcome}: {string.Join(", ", result.Diagnostics.Select(diagnostic => diagnostic.Code))}";

        private static string Sha256(string file) =>
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();

        private sealed class RecordingSink : IReplayExchangeSink
        {
            internal List<ReplayItemResult> Results { get; } = new();

            internal List<ReplayExchange> Exchanges { get; } = new();

            public void Add(ReplayItemResult result, ReplayExchange exchange)
            {
                Results.Add(result);
                Exchanges.Add(exchange);
            }

            internal string BodyOf(int id) =>
                Encoding.UTF8.GetString(Exchanges[Results.FindIndex(result => result.Id == id)].Response.Body);
        }

        private sealed class FailingSink : IReplayExchangeSink
        {
            public void Add(ReplayItemResult result, ReplayExchange exchange) => throw new IOException("The disk is full.");
        }
    }
}
