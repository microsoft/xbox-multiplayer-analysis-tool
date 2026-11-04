// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Net;
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

        // A capture can number two requests of one device alike; naming that number must not send both.
        [Fact]
        public async Task Selection_RefusesANumberThatTwoRequestsOfOneDeviceShare()
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            string capture = ReplayCaptures.WriteXmatcap(
                _directory,
                "capture.xmatcap",
                ("Console A", new[]
                {
                    new CapturedExchange("GET", server.Origin + "/shown", Number: 1),
                    new CapturedExchange("GET", server.Origin + "/hidden", Number: 1),
                }));

            OperationResult<ReplayResult> named = await Execute(
                new ReplayRequest(ReplaySource.FromFile(capture, Selection("1"))));
            OperationResult<ReplayResult> ranged = await Execute(
                new ReplayRequest(ReplaySource.FromFile(capture, Selection("1-2"))));
            OperationResult<ReplayResult> onTheDevice = await Execute(
                new ReplayRequest(ReplaySource.FromFile(capture, Selection("1"), "d1")));
            AssertFailure(named, OperationOutcome.InvalidInput, "replay.request.ambiguous");
            AssertFailure(ranged, OperationOutcome.InvalidInput, "replay.request.ambiguous");
            AssertFailure(onTheDevice, OperationOutcome.InvalidInput, "replay.request.ambiguous");
            Assert.Empty(server.Requests);

            OperationResult<ReplayResult> all = await Execute(
                new ReplayRequest(ReplaySource.FromFile(capture, ReplaySelection.All)));
            Assert.True(all.Succeeded, Describe(all));
            Assert.Equal(new[] { "/shown", "/hidden" }, server.Requests.Select(request => request.Target));
        }

        // A saved Replays tab can hold rows recorded without their request body: naming one is refused and a range skips
        // it, so an empty body never goes out in place of the one that was not kept.
        [Fact]
        public async Task Selection_NeverSendsARequestRecordedWithoutItsBody()
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            string capture = ReplayCaptures.WriteXmatcap(
                _directory,
                "capture.xmatcap",
                ("Replays", new[]
                {
                    new CapturedExchange("GET", server.Origin + "/kept"),
                    new CapturedExchange(
                        "POST", server.Origin + "/omitted", RequestReplayDraft.RequestBodyNotKeptHeader + ": 4096\r\n"),
                }));

            OperationResult<ReplayResult> named = await Execute(
                new ReplayRequest(ReplaySource.FromFile(capture, Selection("2")), new ReplayOptions(AllowUnsafeMethods: true)));
            OperationResult<ReplayResult> all = await Execute(new ReplayRequest(
                ReplaySource.FromFile(capture, ReplaySelection.All), new ReplayOptions(AllowUnsafeMethods: true)));

            AssertFailure(named, OperationOutcome.InvalidInput, "replay.request.unsupported");
            Assert.True(all.Succeeded, Describe(all));
            Assert.Contains(all.Diagnostics, diagnostic => diagnostic.Code == ReplayOperation.SkippedCode);
            Assert.Equal(new[] { "/kept" }, server.Requests.Select(request => request.Target));
        }

        // The plan size is checked before any request is prepared, so a selection that is too large is refused at once,
        // even when one of its requests could not be prepared.
        [Fact]
        public async Task Plan_IsCheckedBeforeAnyRequestIsPrepared()
        {
            ProxyConnectionModel Row(long number, string authority) =>
                new(number, "200", DateTime.UtcNow, TimeSpan.Zero, "http", authority, "GET", "/", "GET / HTTP/1.1\r\n", [],
                    "HTTP/1.1 200 OK\r\n", [], "127.0.0.1");
            // Eleven rows, the last of which cannot be prepared: 11 x 1,000 sends exceed the plan, 11 x 909 do not.
            ProxyConnectionModel[] rows = Enumerable.Range(1, 10).Select(number => Row(number, "127.0.0.1:9"))
                .Append(Row(11, "127.0.0.1:not-a-port")).ToArray();

            OperationResult<ReplayResult> tooLarge = await Execute(new ReplayRequest(
                ReplaySource.FromCaptures(rows), new ReplayOptions(Repeat: 1000)));
            OperationResult<ReplayResult> fits = await Execute(new ReplayRequest(
                ReplaySource.FromCaptures(rows), new ReplayOptions(Repeat: 909)));

            AssertFailure(tooLarge, OperationOutcome.InvalidInput, "replay.plan.too-large");
            AssertFailure(fits, OperationOutcome.InvalidInput, "replay.request.malformed");

            // From a capture file the same holds: the eleventh request, named on its own, cannot be replayed.
            string capture = ReplayCaptures.WriteSaz(
                _directory,
                "capture.saz",
                Enumerable.Range(1, 10).Select(number => new CapturedExchange("GET", $"http://127.0.0.1:9/{number}"))
                    .Append(new CapturedExchange("GET", "http://127.0.0.1:9/socket", "Upgrade: websocket\r\n"))
                    .ToArray());
            OperationResult<ReplayResult> fileTooLarge = await Execute(new ReplayRequest(
                ReplaySource.FromFile(capture, Selection("1-10,11")), new ReplayOptions(Repeat: 1000)));
            OperationResult<ReplayResult> fileFits = await Execute(new ReplayRequest(
                ReplaySource.FromFile(capture, Selection("1-10,11")), new ReplayOptions(Repeat: 909)));

            AssertFailure(fileTooLarge, OperationOutcome.InvalidInput, "replay.plan.too-large");
            AssertFailure(fileFits, OperationOutcome.InvalidInput, "replay.request.unsupported");
        }

        // Preparation honours cancellation too: a run cancelled before it starts sends nothing.
        [Fact]
        public async Task Cancellation_BeforeSendingSendsNothing()
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            string capture = ReplayCaptures.WriteSaz(
                _directory, "capture.saz", new CapturedExchange("GET", server.Origin + "/one"));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            OperationResult<ReplayResult> result = await new ReplayOperation().ExecuteAsync(
                new ReplayRequest(ReplaySource.FromFile(capture, ReplaySelection.All)), OperationContext.None, cancellation.Token);

            AssertFailure(result, OperationOutcome.Cancelled, "replay.cancelled");
            Assert.Equal(0, server.Connections);
        }

        // One request that the HTTP stack could not send keeps every request of the run from connecting.
        [Fact]
        public async Task Check_AHeaderValueThatCannotBeSentStopsTheWholeRun()
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            string capture = ReplayCaptures.WriteXmatcap(
                _directory,
                "capture.xmatcap",
                ("Console A", new[]
                {
                    new CapturedExchange("GET", server.Origin + "/fine"),
                    new CapturedExchange("GET", server.Origin + "/latin", "X-Name: caf\u00e9\r\n"),
                }));

            OperationResult<ReplayResult> result = await Execute(
                new ReplayRequest(ReplaySource.FromFile(capture, ReplaySelection.All)));

            AssertFailure(result, OperationOutcome.InvalidInput, "replay.request.invalid");
            Assert.Equal(0, server.Connections);
        }

        // A send that fails in any other way is that send's result; the run goes on and succeeds.
        [Fact]
        public async Task Run_ContinuesAfterASendFailsInAnUnexpectedWay()
        {
            var operation = new ReplayOperation
            {
                SenderFactory = () => new ReplaySender(new HttpMessageInvoker(new FailingFirstHandler()), ownsInvoker: true),
            };
            var drafts = new[] { "https://replay.test/boom", "https://replay.test/fine" };
            var sink = new RecordingSink();

            OperationResult<ReplayResult> result = await operation.ExecuteAsync(
                new ReplayRequest(
                    ReplaySource.FromCaptures(drafts.Select((url, index) => Row(index + 1, url)).ToArray()), Sink: sink),
                OperationContext.None,
                CancellationToken.None);

            Assert.True(result.Succeeded, Describe(result));
            Assert.Equal(
                new[] { ReplayOutcome.Failed, ReplayOutcome.Completed },
                result.Value.Items.Select(item => item.Outcome));
            Assert.Equal(ReplaySender.SendFailedCode, result.Value.Items[0].Code);
            Assert.Equal(new int?[] { 0, 1 }, result.Value.Items.Select(item => item.SourceIndex));
            Assert.Equal(2, sink.Results.Count);
        }

        // One body edit serves every send as the same bytes; nothing is copied per request.
        [Fact]
        public async Task Edits_ShareOneBodyAcrossEveryRequest()
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            string capture = ReplayCaptures.WriteSaz(
                _directory,
                "capture.saz",
                new CapturedExchange("GET", server.Origin + "/one"),
                new CapturedExchange("GET", server.Origin + "/two"),
                new CapturedExchange("GET", server.Origin + "/three"));
            byte[] body = Enumerable.Range(0, 4096).Select(index => (byte)index).ToArray();
            var sink = new RecordingSink();

            OperationResult<ReplayResult> result = await Execute(new ReplayRequest(
                ReplaySource.FromFile(capture, ReplaySelection.All),
                new ReplayOptions(Repeat: 2),
                new ReplayEdits(Body: body),
                sink));

            Assert.True(result.Succeeded, Describe(result));
            Assert.Equal(6, sink.Exchanges.Count);
            Assert.All(sink.Exchanges, exchange => Assert.Same(body, exchange.Request.Body));
            Assert.All(server.Requests, request => Assert.Equal(body, request.Body));
        }

        // Every send in flight may hold a whole response, so their total is bounded.
        [Theory]
        [InlineData(2, 512, true)]
        [InlineData(3, 512, false)]
        [InlineData(16, 64, true)]
        [InlineData(16, 65, false)]
        public async Task Options_BoundTheResponsesInFlight(int parallelism, int maxResponseMib, bool valid)
        {
            var operation = new ReplayOperation
            {
                SenderFactory = () => new ReplaySender(new HttpMessageInvoker(new FailingFirstHandler()), ownsInvoker: true),
            };

            OperationResult<ReplayResult> result = await operation.ExecuteAsync(
                new ReplayRequest(
                    ReplaySource.FromCaptures(new[] { Row(1, "https://replay.test/") }),
                    new ReplayOptions(Parallelism: parallelism, MaxResponseBytes: maxResponseMib * 1024L * 1024)),
                OperationContext.None,
                CancellationToken.None);

            if (valid)
            {
                Assert.True(result.Succeeded, Describe(result));
                Assert.Equal(ReplayOutcome.Completed, Assert.Single(result.Value.Items).Outcome);
            }
            else
            {
                AssertFailure(result, OperationOutcome.InvalidInput, "replay.options.invalid");
            }
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

        // Verify decodes each body only up to its limit; beyond it, the bodies are compared as stored.
        [Fact]
        public async Task Verify_ComparesBodiesBeyondTheDecodeLimitAsStored()
        {
            byte[] content = new byte[4096];
            await using var server = new LoopbackHttpServer(_ => new LoopbackResponse(Body: content));
            string capture = ReplayCaptures.WriteSaz(
                _directory,
                "capture.saz",
                new CapturedExchange(
                    "GET",
                    server.Origin + "/zeros",
                    ResponseHead: "HTTP/1.1 200 OK\r\nContent-Encoding: gzip\r\n",
                    ResponseBody: ReplayCoreTests.Gzip(content)));
            var request = new ReplayRequest(
                ReplaySource.FromFile(capture, ReplaySelection.All), new ReplayOptions(Verify: true));

            OperationResult<ReplayResult> decoded = await Execute(request);
            OperationResult<ReplayResult> stored = await new ReplayOperation { MaxVerifyDecodedBytes = 1024 }
                .ExecuteAsync(request, OperationContext.None, CancellationToken.None);

            Assert.True(Assert.Single(decoded.Value.Items).Verification.Matches, Describe(decoded));
            Assert.Equal(
                new[] { ReplayVerifier.BodyDifference },
                Assert.Single(stored.Value.Items).Verification.Differences);
        }

        // A captured response that the GUI recorded without its body cannot be compared: verify skips it, the run warns,
        // and it counts neither as a match nor as a mismatch.
        [Fact]
        public async Task Verify_SkipsResponsesRecordedWithoutTheirBody()
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok("{\"ok\":true}"));
            string capture = ReplayCaptures.WriteXmatcap(
                _directory,
                "capture.xmatcap",
                ("XMAT Replays", new[]
                {
                    new CapturedExchange("GET", server.Origin + "/kept", ResponseBody: "{\"ok\":true}"u8.ToArray()),
                    new CapturedExchange(
                        "GET",
                        server.Origin + "/omitted",
                        ResponseHead: "HTTP/1.1 200 OK\r\n" + RequestReplayDraft.ResponseBodyNotKeptHeader + ": 11\r\n"),
                }));

            OperationResult<ReplayResult> result = await Execute(new ReplayRequest(
                ReplaySource.FromFile(capture, ReplaySelection.All), new ReplayOptions(Verify: true)));

            Assert.True(result.Succeeded, Describe(result));
            Assert.True(result.Value.Items[0].Verification.Matches);
            Assert.Null(result.Value.Items[1].Verification);
            Assert.Equal((1, 0), (result.Value.Matched, result.Value.Mismatched));
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == ReplayOperation.VerifySkippedCode);
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

        // A sequence sends one request per number, ascending in each round, with the number in its result; the ids stay
        // the plan order.
        [Fact]
        public async Task Sequence_SendsOneRequestPerNumberInPlanOrder()
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            Assert.True(ReplaySequence.TryParse("08-10", out ReplaySequence sequence));

            OperationResult<ReplayResult> result = await Execute(new ReplayRequest(
                ReplaySource.FromTemplate(new RequestReplayDraft { Url = server.Origin + "/item/#?page=#" }, sequence),
                new ReplayOptions(Repeat: 2)));

            Assert.True(result.Succeeded, Describe(result));
            string[] round = { "/item/08?page=08", "/item/09?page=09", "/item/10?page=10" };
            Assert.Equal(round.Concat(round), server.Requests.Select(request => request.Target));
            Assert.Equal(new long?[] { 8, 9, 10, 8, 9, 10 }, result.Value.Items.Select(item => item.Sequence));
            Assert.Equal(Enumerable.Range(1, 6), result.Value.Items.Select(item => item.Id));
            Assert.Equal(new[] { 1, 1, 1, 2, 2, 2 }, result.Value.Items.Select(item => item.Iteration));
            Assert.All(result.Value.Items, item => Assert.Null(item.RequestNumber));
        }

        // Edits and the unsafe-method policy cover every number before anything is sent, and one body serves every send.
        [Fact]
        public async Task Sequence_AppliesEditsAndPolicyToEveryNumber()
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            Assert.True(ReplaySequence.TryParse("1-3", out ReplaySequence sequence));
            byte[] body = "{\"n\":true}"u8.ToArray();
            var edits = new ReplayEdits(
                Method: "POST", SetHeaders: new[] { new KeyValuePair<string, string>("X-Run", "seq") }, Body: body);
            var template = new RequestReplayDraft { Url = server.Origin + "/n/#" };
            ReplaySource Source() => ReplaySource.FromTemplate(template, sequence);
            var sink = new RecordingSink();

            OperationResult<ReplayResult> denied = await Execute(new ReplayRequest(Source(), Edits: edits));
            int sentWhenDenied = server.Requests.Count;
            OperationResult<ReplayResult> allowed = await Execute(
                new ReplayRequest(Source(), new ReplayOptions(AllowUnsafeMethods: true), edits, sink));

            AssertFailure(denied, OperationOutcome.InvalidInput, "replay.policy.unsafe-method");
            Assert.Equal(0, sentWhenDenied);
            Assert.True(allowed.Succeeded, Describe(allowed));
            Assert.Equal(new[] { "/n/1", "/n/2", "/n/3" }, server.Requests.Select(request => request.Target));
            Assert.All(server.Requests, request => Assert.Equal(("POST", "seq"), (request.Method, request.Header("X-Run"))));
            Assert.All(server.Requests, request => Assert.Equal(body, request.Body));
            Assert.All(sink.Exchanges, exchange => Assert.Same(body, exchange.Request.Body));

            // The edits went to a copy: the caller's template is as it was.
            Assert.Equal(("GET", "", ""), (template.Method, template.HeadersText, template.BodyText));
        }

        // The expanded URLs must fit a total length, checked before any is made: 6 URLs of 15 characters fit 90, 7 do not.
        [Fact]
        public async Task Sequence_UrlsMustFitTheirTotalLength()
        {
            var operation = new ReplayOperation
            {
                MaxSequenceUrlCharacters = 90,
                SenderFactory = () => new ReplaySender(new HttpMessageInvoker(new FailingFirstHandler()), ownsInvoker: true),
            };
            async Task<OperationResult<ReplayResult>> Run(string range)
            {
                Assert.True(ReplaySequence.TryParse(range, out ReplaySequence sequence));
                return await operation.ExecuteAsync(
                    new ReplayRequest(ReplaySource.FromTemplate(new RequestReplayDraft { Url = "http://h.test/#" }, sequence)),
                    OperationContext.None,
                    CancellationToken.None);
            }

            OperationResult<ReplayResult> fits = await Run("1-6");
            OperationResult<ReplayResult> tooLong = await Run("1-7");

            Assert.True(fits.Succeeded, Describe(fits));
            Assert.Equal(6, fits.Value.Items.Count);
            AssertFailure(tooLong, OperationOutcome.InvalidInput, "replay.plan.too-large");
        }

        // A template's own headers and body go with every number, the body as one shared array.
        [Fact]
        public async Task Sequence_KeepsTheTemplatesHeadersAndBodyInEveryRequest()
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            Assert.True(ReplaySequence.TryParse("1-2", out ReplaySequence sequence));
            byte[] body = new byte[] { 0, 1, 2, 255 };
            var template = new RequestReplayDraft
            {
                Method = "PUT",
                Url = server.Origin + "/blob/#",
                HeadersText = "Content-Type: application/octet-stream\r\nX-Template: yes",
            };
            template.SetBodyBytes(body);
            var sink = new RecordingSink();

            OperationResult<ReplayResult> result = await Execute(new ReplayRequest(
                ReplaySource.FromTemplate(template, sequence), new ReplayOptions(AllowUnsafeMethods: true), Sink: sink));

            Assert.True(result.Succeeded, Describe(result));
            Assert.Equal(new[] { "/blob/1", "/blob/2" }, server.Requests.Select(request => request.Target));
            Assert.All(server.Requests, request => Assert.Equal(("PUT", "yes"), (request.Method, request.Header("X-Template"))));
            Assert.All(server.Requests, request => Assert.Equal(body, request.Body));
            Assert.All(sink.Exchanges, exchange => Assert.Same(body, exchange.Request.Body));
            Assert.Equal(server.Origin + "/blob/#", template.Url);
        }

        // The plan counts the numbers times the repeats before the template is expanded: a template whose expansion
        // would be refused shows that too large a plan fails first.
        [Fact]
        public async Task Sequence_PlanIsCheckedBeforeTheTemplateIsExpanded()
        {
            Assert.True(ReplaySequence.TryParse("1-5001", out ReplaySequence sequence));
            ReplaySource Source() => ReplaySource.FromTemplate(new RequestReplayDraft { Url = "http://#.test/" }, sequence);

            OperationResult<ReplayResult> tooLarge = await Execute(new ReplayRequest(Source(), new ReplayOptions(Repeat: 2)));
            OperationResult<ReplayResult> fits = await Execute(new ReplayRequest(Source()));

            AssertFailure(tooLarge, OperationOutcome.InvalidInput, "replay.plan.too-large");
            AssertFailure(fits, OperationOutcome.InvalidInput, ReplayOperation.SequenceInvalidCode);
        }

        // A '#' in the host is refused even when the sequence has one number, which no later origin comparison could
        // catch. The sender is in memory, so nothing could reach a network if the check were missing.
        [Fact]
        public async Task Sequence_RefusesAPlaceholderInTheAuthority_EvenForOneNumber()
        {
            Assert.True(ReplaySequence.TryParse("1-1", out ReplaySequence sequence));
            var operation = new ReplayOperation
            {
                SenderFactory = () => new ReplaySender(new HttpMessageInvoker(new FailingFirstHandler()), ownsInvoker: true),
            };

            OperationResult<ReplayResult> result = await operation.ExecuteAsync(
                new ReplayRequest(ReplaySource.FromTemplate(new RequestReplayDraft { Url = "http://h#.test/" }, sequence)),
                OperationContext.None,
                CancellationToken.None);

            AssertFailure(result, OperationOutcome.InvalidInput, ReplayOperation.SequenceInvalidCode);
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

        private static ProxyConnectionModel Row(long number, string url)
        {
            var uri = new Uri(url);
            return new ProxyConnectionModel(
                number, "200", DateTime.UtcNow, TimeSpan.Zero, uri.Scheme, uri.Authority, "GET", uri.PathAndQuery,
                $"GET {uri.PathAndQuery} HTTP/1.1\r\nHost: {uri.Authority}\r\n", [], "HTTP/1.1 200 OK\r\n", [], "127.0.0.1");
        }

        // Throws something unexpected for /boom and answers everything else, without any network.
        private sealed class FailingFirstHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken) =>
                request.RequestUri.AbsolutePath == "/boom"
                    ? throw new InvalidOperationException("stub failure")
                    : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
        private sealed class FailingSink : IReplayExchangeSink
        {
            public void Add(ReplayItemResult result, ReplayExchange exchange) => throw new IOException("The disk is full.");
        }
    }
}
