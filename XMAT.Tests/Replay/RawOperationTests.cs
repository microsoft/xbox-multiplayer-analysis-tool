// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using XMAT.Operations;
using XMAT.Operations.Replay;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Replay
{
    // A raw request through ReplayOperation (slice L, contract 2.4 to 2.8; tests 2.12 items 8, 10 and L1's half of 11):
    // every check of stages 2 and 3 in its order, before any resolver, connector or TLS call; the consent's binding;
    // Preview; runs on loopback with their write state, framing and provenance; the provenance origin of every exchange,
    // Parsed included; captured origins restored before the checks; the sends that began, recorded when a run is
    // cancelled; and raw sends interleaved with client-certificate and Windows-authentication runs. Loopback servers and
    // seams only.
    public sealed class RawOperationTests
    {
        private const string OriginChangeMessage =
            "A selected request would go to another origin than the one it was captured or saved for, or first sent to, " +
            "with its headers, and origin changes are not allowed; nothing was sent.";

        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);
        private static readonly ReplayTransportOptions Http11 = new(ReplayHttpVersion.Http11);

        // Each refusal of stages 2 and 3, and, for adjacent checks, the earlier one when both apply.
        [Theory]
        [InlineData("changed", RawCodes.Changed)]
        [InlineData("changed-before-proxy", RawCodes.Changed)]
        [InlineData("proxy", RawCodes.Proxy)]
        [InlineData("proxy-before-client-cert", RawCodes.Proxy)]
        [InlineData("client-cert", RawCodes.ClientCert)]
        [InlineData("windows-auth", RawCodes.WindowsAuth)]
        [InlineData("auto-version", RawCodes.HttpVersion)]
        [InlineData("http2", RawCodes.HttpVersion)]
        [InlineData("redirects", RawCodes.Redirects)]
        [InlineData("verify", RawCodes.UnsupportedOption)]
        [InlineData("edits", RawCodes.UnsupportedOption)]
        [InlineData("connect-to-denied", ReplayOperation.ConnectToDeniedCode)]
        [InlineData("connect-to-http", ReplayOperation.ConnectToHttpsCode)]
        [InlineData("target-denied", RawCodes.TargetDenied)]
        [InlineData("no-consent", RawCodes.PolicyRaw)]
        [InlineData("consent-for-another-image", RawCodes.PolicyRaw)]
        [InlineData("consent-for-another-target", RawCodes.PolicyRaw)]
        [InlineData("unsafe-method", RawCodes.PolicyRawUnsafe)]
        [InlineData("get-with-body", RawCodes.PolicyRawUnsafe)]
        [InlineData("unsafe-before-origin-change", RawCodes.PolicyRawUnsafe)]
        [InlineData("origin-change", "replay.policy.origin-change")]
        [InlineData("origin-change-before-connect-to", "replay.policy.origin-change")]
        [InlineData("connect-to-policy", ReplayOperation.ConnectToPolicyCode)]
        public async Task EachCheck_RefusesBeforeAnyResolverConnectorOrTlsCall(string check, string code)
        {
            ReplayRawTarget http = Target("http://127.0.0.1:9");
            ReplayRawTarget https = Target("https://127.0.0.1:9");
            RawRequestImage get = Image(http, "GET / HTTP/1.1\r\nHost: 127.0.0.1:9\r\n\r\n");
            RawRequestImage httpsGet = Image(https, "GET / HTTP/1.1\r\nHost: 127.0.0.1:9\r\n\r\n");
            RawRequestImage post = Image(http, "POST /p HTTP/1.1\r\nHost: 127.0.0.1:9\r\nContent-Length: 2\r\n\r\nhi");
            RawRequestImage getWithBody = Image(http, "GET / HTTP/1.1\r\nHost: 127.0.0.1:9\r\nContent-Length: 1\r\n\r\nx");
            RawRequestImage denied = Image(Target("http://224.0.0.1:9"), "GET / HTTP/1.1\r\nHost: 224.0.0.1:9\r\n\r\n");
            RawRequestImage named = Image(Target("http://example.test"), "GET / HTTP/1.1\r\nHost: example.test\r\n\r\n");
            RawRequestImage namedHttps = Image(Target("https://example.test"), "GET / HTTP/1.1\r\nHost: example.test\r\n\r\n");
            Assert.True(ReplayProxy.TryParse("http://127.0.0.1:3128", out ReplayProxy proxy));
            Assert.True(ReplayClientCertificate.TryParse(new string('A', 40), out ReplayClientCertificate certificate));
            Assert.True(ReplayConnectTo.TryParse("127.0.0.1:9", out ReplayConnectTo endpoint));
            Assert.True(ReplayConnectTo.TryParse("0.0.0.0:443", out ReplayConnectTo unspecified));
            var captured = new Uri("http://captured.example/a");
            Func<RawRequestImage, CancellationToken, string> wrongDigest = (_, _) => new string('0', 64);

            (RawRequestImage Image, Uri Provenance, ReplayTransportOptions Transport, ReplayOptions Extra, ReplayEdits Edits,
                Func<RawRequestImage, CancellationToken, string> Digest, RawRequestImage Consented) scenario = check switch
            {
                "changed" => (get, null, Http11, null, null, wrongDigest, get),
                "changed-before-proxy" => (get, null, Http11 with { Proxy = proxy }, null, null, wrongDigest, get),
                "proxy" => (get, null, Http11 with { Proxy = proxy }, null, null, null, get),
                "proxy-before-client-cert" => (httpsGet, null, Http11 with { Proxy = proxy, ClientCertificate = certificate }, null, null, null, httpsGet),
                "client-cert" => (httpsGet, null, Http11 with { ClientCertificate = certificate }, null, null, null, httpsGet),
                "windows-auth" => (httpsGet, null, Http11 with { WindowsAuth = true }, null, null, null, httpsGet),
                "auto-version" => (get, null, ReplayTransportOptions.Default, null, null, null, get),
                "http2" => (httpsGet, null, new ReplayTransportOptions(ReplayHttpVersion.Http2), null, null, null, httpsGet),
                "redirects" => (get, null, Http11, new ReplayOptions(MaxRedirects: 1), null, null, get),
                "verify" => (get, null, Http11, new ReplayOptions(Verify: true), null, null, get),
                "edits" => (get, null, Http11, null, new ReplayEdits(Method: "GET"), null, get),
                "connect-to-denied" => (httpsGet, null, Http11 with { ConnectTo = unspecified }, new ReplayOptions(AllowConnectTo: true), null, null, httpsGet),
                "connect-to-http" => (get, null, Http11 with { ConnectTo = endpoint }, new ReplayOptions(AllowConnectTo: true), null, null, get),

                // A denied target has no plan to consent to: Preview refuses it too.
                "target-denied" => (denied, null, Http11, null, null, null, null),
                "no-consent" => (get, null, Http11, null, null, null, null),
                "consent-for-another-image" => (get, null, Http11, null, null, null, post),
                "consent-for-another-target" => (namedHttps, null, Http11, null, null, null, named),
                "unsafe-method" => (post, null, Http11, null, null, null, post),
                "get-with-body" => (getWithBody, null, Http11, null, null, null, getWithBody),
                "unsafe-before-origin-change" => (post, captured, Http11, null, null, null, post),
                "origin-change" => (get, captured, Http11, null, null, null, get),
                "origin-change-before-connect-to" => (httpsGet, captured, Http11 with { ConnectTo = endpoint }, null, null, null, httpsGet),
                "connect-to-policy" => (httpsGet, null, Http11 with { ConnectTo = endpoint }, null, null, null, httpsGet),
                _ => throw new ArgumentOutOfRangeException(nameof(check)),
            };
            var seams = new CountingSeams();
            ReplayOperation operation = seams.Apply(new ReplayOperation());
            operation.RawDigest = scenario.Digest;
            ReplayOptions options = (scenario.Extra ?? new ReplayOptions()) with
            {
                Transport = scenario.Transport,
                AllowRaw = scenario.Consented == null ? null : Consent(scenario.Consented),
            };
            var sink = new RecordingSink();

            OperationResult<ReplayResult> result = await operation.ExecuteAsync(
                new ReplayRequest(ReplaySource.FromRaw(scenario.Image, scenario.Provenance), options, scenario.Edits, sink),
                OperationContext.None,
                CancellationToken.None);

            Assert.Equal(OperationOutcome.InvalidInput, result.Outcome);
            OperationDiagnostic diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal(code, diagnostic.Code);
            Assert.Equal(RawRequest.Message(code) ?? (code == "replay.policy.origin-change" ? OriginChangeMessage : diagnostic.Message), diagnostic.Message);
            Assert.Equal((0, 0, 0), (seams.Factories, seams.Lookups, seams.Connects));
            Assert.Empty(sink.Results);
        }

        // The consent covers the image of the plan that it was given for: the same target and bytes, never another
        // request, nor the same bytes for another target.
        [Fact]
        public void TheConsent_CoversThePlansImageAndTargetOnly()
        {
            const string request = "GET / HTTP/1.1\r\nHost: example.test\r\n\r\n";
            RawRequestImage image = Image(Target("http://example.test"), request);
            ReplayRawConsent consent = Consent(image);

            Assert.True(consent.Covers(image));
            Assert.True(consent.Covers(Image(Target("http://example.test"), request)));
            Assert.False(consent.Covers(Image(Target("https://example.test"), request)));
            Assert.False(consent.Covers(Image(Target("http://example.test"), "GET /other HTTP/1.1\r\nHost: example.test\r\n\r\n")));
            Assert.False(consent.Covers(null));
            Assert.Throws<ArgumentException>(
                () => ReplayRawConsent.For(new ReplayPlanPreview(new RequestReplayDraft(), null, 1, false, false)));
        }

        // Preview checks the frozen values as ExecuteAsync does, sends nothing, and says which consents the run needs; its
        // plan gives back the same source.
        [Fact]
        public void Preview_ChecksTheFrozenValues_AndSaysWhichConsentsTheRunNeeds()
        {
            var seams = new CountingSeams();
            ReplayOperation operation = seams.Apply(new ReplayOperation());
            RawRequestImage image = Image(Target("http://127.0.0.1:9"), "POST /p HTTP/1.1\r\nHost: 127.0.0.1:9\r\nContent-Length: 2\r\n\r\nhi");
            var captured = new Uri("https://captured.example/x");

            OperationResult<ReplayPlanPreview> preview = operation.Preview(
                ReplaySource.FromRaw(image, captured), new ReplayOptions(Repeat: 3, Transport: Http11));

            Assert.True(preview.Succeeded, string.Join(", ", preview.Diagnostics.Select(diagnostic => diagnostic.Code)));
            ReplayPlanPreview plan = preview.Value;
            Assert.Equal((true, 3, true, true), (plan.UsesRaw, plan.Sends, plan.UsesUnsafeMethod, plan.ChangesOrigin));
            Assert.Same(image, plan.Raw);
            Assert.Same(captured, plan.ProvenanceOrigin);
            Assert.Null(plan.Template);
            ReplaySource again = plan.ToSource();
            Assert.Equal((image, captured), (again.Raw, again.ProvenanceOrigin));

            Assert.False(operation.Preview(
                ReplaySource.FromRaw(image, new Uri("http://127.0.0.1:9/elsewhere")), new ReplayOptions(Transport: Http11)).Value.ChangesOrigin);
            Assert.False(operation.Preview(ReplaySource.FromRaw(image), new ReplayOptions(Transport: Http11)).Value.ChangesOrigin);
            OperationResult<ReplayPlanPreview> refused = operation.Preview(ReplaySource.FromRaw(image), new ReplayOptions());
            Assert.Equal((OperationOutcome.InvalidInput, RawCodes.HttpVersion), (refused.Outcome, Assert.Single(refused.Diagnostics).Code));
            Assert.Equal((0, 0, 0), (seams.Factories, seams.Lookups, seams.Connects));
            Assert.Throws<ArgumentException>(() => ReplaySource.FromRaw(image, new Uri("/relative", UriKind.Relative)));
            Assert.Throws<ArgumentException>(
                () => operation.PrepareForExport(new ReplayRequest(ReplaySource.FromRaw(image)), CancellationToken.None));
        }

        // A sink that fails ends the run with its exception, and takes no later send, not even one that began and is still
        // recorded when a run is cancelled.
        [Fact]
        public async Task AFailedSink_EndsTheRun_AndTakesNoLaterRawSend()
        {
            RawRequestImage image = Image(Target("http://127.0.0.1:9"), "GET /x HTTP/1.1\r\nHost: 127.0.0.1:9\r\n\r\n");
            int connects = 0;
            int held = 0;
            var bothHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var answering = new SeamConnection();
            answering.Respond("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");
            var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var operation = new ReplayOperation
            {
                RawSenderFactory = setup => new RawSender(setup with
                {
                    Connector = async (_, token) =>
                    {
                        if (Interlocked.Increment(ref connects) == 1)
                        {
                            await answer.Task;
                            return answering;
                        }

                        if (Interlocked.Increment(ref held) == 2)
                        {
                            bothHeld.TrySetResult();
                        }

                        await Task.Delay(Timeout.Infinite, token);
                        throw new InvalidOperationException("The held setup ended on its own.");
                    },
                }),
            };
            var sink = new FailingSink();

            Task<OperationResult<ReplayResult>> run = operation.ExecuteAsync(
                new ReplayRequest(
                    ReplaySource.FromRaw(image),
                    new ReplayOptions(Repeat: 3, Parallelism: 3, Transport: Http11, AllowRaw: Consent(image)),
                    Sink: sink),
                OperationContext.None,
                CancellationToken.None);
            await bothHeld.Task.WaitAsync(Wait);
            answer.SetResult();

            await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(Wait));
            Assert.Equal(1, sink.Calls);
        }

        // L1-A1: a sink that fails while the run is being cancelled, after the cancellation or before it, still ends the run
        // with its failure, never as cancelled: Parallel.ForEachAsync reports a loop whose token was cancelled as cancelled
        // whatever its bodies threw, and the caller would then take the sends that its sink holds for all that began.
        [Theory]
        [InlineData("cancel-then-fail")]
        [InlineData("fail-then-cancel")]
        public async Task AFailedSink_EndsTheRunWithItsFailure_EvenWhenTheRunIsCancelled(string order)
        {
            RawRequestImage image = Image(Target("http://127.0.0.1:9"), "GET /x HTTP/1.1\r\nHost: 127.0.0.1:9\r\n\r\n");
            bool failFirst = order == "fail-then-cancel";
            using var cancel = new CancellationTokenSource();
            var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thirdEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sink = new SinkOfSecondSend(() =>
            {
                if (!failFirst)
                {
                    cancel.Cancel();
                }

                failed.TrySetResult();
                throw new IOException("The disk is full.");
            });
            ReplayOperation operation = Answering(new ReplayOperation());
            if (failFirst)
            {
                // The third send ends while the second is still before the sink; once the second's sink failed, the third
                // cancels the run and then reaches the sink, which takes it no more.
                operation.RawSendEnded = async id =>
                {
                    if (id == 2)
                    {
                        await thirdEnded.Task;
                    }
                    else if (id == 3)
                    {
                        thirdEnded.TrySetResult();
                        await failed.Task;
                        cancel.Cancel();
                    }
                };
            }

            Task<OperationResult<ReplayResult>> run = operation.ExecuteAsync(
                new ReplayRequest(
                    ReplaySource.FromRaw(image),
                    new ReplayOptions(
                        Repeat: failFirst ? 3 : 2, Parallelism: failFirst ? 2 : 1, Transport: Http11, AllowRaw: Consent(image)),
                    Sink: sink),
                OperationContext.None,
                cancel.Token);

            await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(Wait));
            Assert.True(cancel.IsCancellationRequested);
            Assert.Equal(new[] { 1, 2 }, sink.Given);
        }

        // A sink whose writing was cancelled, as the CLI's is once its finalization passed its bound, takes no later send,
        // not even one that began and waits for the sink, and the run ends as cancelled.
        [Fact]
        public async Task ASinkWhoseWritingWasCancelled_TakesNoLaterSend()
        {
            RawRequestImage image = Image(Target("http://127.0.0.1:9"), "GET /x HTTP/1.1\r\nHost: 127.0.0.1:9\r\n\r\n");
            using var cancel = new CancellationTokenSource();
            var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thirdEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sink = new SinkOfSecondSend(() =>
            {
                second.TrySetResult();
                cancel.Cancel();
                throw new OperationCanceledException();
            });
            ReplayOperation operation = Answering(new ReplayOperation());
            operation.RawSendEnded = async id =>
            {
                if (id == 2)
                {
                    await thirdEnded.Task;
                }
                else if (id == 3)
                {
                    thirdEnded.TrySetResult();
                    await second.Task;
                }
            };

            OperationResult<ReplayResult> result = await operation.ExecuteAsync(
                new ReplayRequest(
                    ReplaySource.FromRaw(image),
                    new ReplayOptions(Repeat: 3, Parallelism: 2, Transport: Http11, AllowRaw: Consent(image)),
                    Sink: sink),
                OperationContext.None,
                cancel.Token).WaitAsync(Wait);

            Assert.Equal(OperationOutcome.Cancelled, result.Outcome);
            Assert.Equal(new[] { 1, 2 }, sink.Given);
        }

        // A raw run on loopback: its image arrives as written, once per repeat and each on its own connection, and every
        // send reports its write state, framing, transport and version, with the target's origin as its provenance. The
        // normalized request is made once for the run, and no exception is kept.
        [Fact]
        public async Task ARawRun_SendsTheImage_AndReportsItsWriteStateFramingAndProvenance()
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok("pong"));
            ReplayRawTarget target = Target(server.Origin);
            RawRequestImage image = Image(target, $"POST /p?q HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\nContent-Length: 4\r\n\r\nping");
            var sink = new RecordingSink();

            OperationResult<ReplayResult> result = await new ReplayOperation().ExecuteAsync(
                new ReplayRequest(
                    ReplaySource.FromRaw(image),
                    new ReplayOptions(Repeat: 2, Parallelism: 2, AllowUnsafeMethods: true, Transport: Http11, AllowRaw: Consent(image)),
                    Sink: sink),
                OperationContext.None,
                CancellationToken.None).WaitAsync(Wait);

            Assert.True(result.Succeeded, Describe(result));
            Assert.Equal(((string)null, (string)null), (result.Value.InputKind, result.Value.InputSha256));
            Assert.Equal(new[] { 1, 2 }, result.Value.Items.Select(item => item.Iteration));
            Assert.All(result.Value.Items, item =>
            {
                Assert.Equal((ReplayOutcome.Completed, 200, (string)null, 4L), (item.Outcome, item.StatusCode, item.Code, item.ResponseBytes));
                Assert.Equal((true, (long?)image.Length, RawWriteState.Complete, "content-length"), (item.Raw, item.RequestBytesWritten, item.RequestWrite, item.ResponseFraming));
                Assert.Equal(("direct", "1.1", "1.1"), (item.Transport, item.HttpVersion, item.ResponseVersion));
            });
            Assert.Equal(2, server.Connections);
            Assert.All(server.Requests, request => Assert.Equal(("POST", "/p?q", "ping"), (request.Method, request.Target, Encoding.ASCII.GetString(request.Body))));
            Assert.All(sink.Exchanges, exchange =>
            {
                Assert.True(exchange.Raw);
                Assert.Equal(target.Origin, exchange.ProvenanceOrigin);
                Assert.Null(exchange.Error);
                Assert.Equal(image.ToMessage().ToBytes(), exchange.Request.ToBytes());
            });
            Assert.Same(sink.Exchanges[0].Request, sink.Exchanges[1].Request);
        }

        // Every exchange carries the origin that its request inherits (contract 2.8), Parsed sends included, and a later
        // send from it to another origin needs consent: captured for A and sent to B, then B again; composed for C, then D;
        // a request file's origin; and a raw request's provenance.
        [Fact]
        public async Task EveryExchange_CarriesTheOriginThatItsRequestInherits_ParsedIncluded()
        {
            await using var b = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            await using var c = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            await using var d = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            var operation = new ReplayOperation();
            const string a = "https://captured.example:8443/a";

            ReplayExchange toB = await SendOneAsync(
                operation,
                new ReplayRequest(
                    ReplaySource.FromCaptures(new[] { Row(1, a) }),
                    new ReplayOptions(AllowOriginChange: true),
                    new ReplayEdits(Origin: b.Origin)));
            Assert.Equal(new Uri(a), toB.ProvenanceOrigin);
            AssertOriginChangeRefused(await Execute(operation, new ReplayRequest(
                ReplaySource.FromDraft(new RequestReplayDraft { Url = b.Origin + "/a" }.WithCapturedOrigin(toB.ProvenanceOrigin)))));
            AssertOriginChangeRefused(await Execute(operation, new ReplayRequest(
                ReplaySource.FromDraft(new RequestReplayDraft { Url = c.Origin + "/a" }.WithCapturedOrigin(toB.ProvenanceOrigin)))));

            ReplayExchange toC = await SendOneAsync(
                operation, new ReplayRequest(ReplaySource.FromDraft(new RequestReplayDraft { Url = c.Origin + "/c" })));
            Assert.True(ReplayAuthority.SameOrigin(new Uri(c.Origin), toC.ProvenanceOrigin));
            AssertOriginChangeRefused(await Execute(operation, new ReplayRequest(
                ReplaySource.FromDraft(new RequestReplayDraft { Url = d.Origin + "/d" }.WithCapturedOrigin(toC.ProvenanceOrigin)))));

            RequestReplayDraft fromFile = RequestReplayDraft.FromRequestFile(new RequestFileContent(
                RequestFileProfile.Content,
                "GET",
                "https://saved.example/item",
                ReplayHttpVersion.Auto,
                new[] { KeyValuePair.Create("Accept", "*/*") },
                Array.Empty<byte>(),
                RequestOmissions.None));
            fromFile.Url = b.Origin + "/item";
            ReplayExchange saved = await SendOneAsync(
                operation, new ReplayRequest(ReplaySource.FromDraft(fromFile), new ReplayOptions(AllowOriginChange: true)));
            Assert.Equal(new Uri("https://saved.example/item"), saved.ProvenanceOrigin);

            RawRequestImage image = Image(Target(d.Origin), $"GET /r HTTP/1.1\r\nHost: 127.0.0.1:{d.Port}\r\n\r\n");
            var inherited = new Uri("https://captured.example/r");
            AssertOriginChangeRefused(await Execute(operation, new ReplayRequest(
                ReplaySource.FromRaw(image, inherited), new ReplayOptions(Transport: Http11, AllowRaw: Consent(image)))));
            ReplayExchange raw = await SendOneAsync(
                operation,
                new ReplayRequest(
                    ReplaySource.FromRaw(image, inherited),
                    new ReplayOptions(AllowOriginChange: true, Transport: Http11, AllowRaw: Consent(image))));
            Assert.Same(inherited, raw.ProvenanceOrigin);
            Assert.Equal(new[] { "/a", "/item" }, b.Requests.Select(request => request.Target));
            Assert.Equal(new[] { "/c" }, c.Requests.Select(request => request.Target));
            Assert.Equal(new[] { "/r" }, d.Requests.Select(request => request.Target));
        }

        // A captured origin given with the captures (Reissue of Replays rows) replaces the row's own before the checks: a
        // send to the row's URL then needs consent, and inherits the restored origin; a null entry keeps the row's own.
        [Fact]
        public async Task CapturedOrigins_ReplaceTheRowsOwn_BeforeTheChecks()
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            ProxyConnectionModel[] rows = { Row(1, server.Origin + "/one"), Row(2, server.Origin + "/two") };
            var first = new Uri("https://first.example/");
            var operation = new ReplayOperation();

            AssertOriginChangeRefused(await Execute(operation, new ReplayRequest(ReplaySource.FromCaptures(rows, new[] { first, null }))));
            Assert.Empty(server.Requests);

            OperationResult<ReplayResult> kept = await Execute(operation, new ReplayRequest(ReplaySource.FromCaptures(rows, new Uri[] { null, null })));
            Assert.True(kept.Succeeded, Describe(kept));

            var sink = new RecordingSink();
            OperationResult<ReplayResult> allowed = await Execute(operation, new ReplayRequest(
                ReplaySource.FromCaptures(rows, new[] { first, null }), new ReplayOptions(AllowOriginChange: true), Sink: sink));
            Assert.True(allowed.Succeeded, Describe(allowed));
            Assert.Same(first, sink.Exchanges[0].ProvenanceOrigin);
            Assert.True(ReplayAuthority.SameOrigin(new Uri(server.Origin), sink.Exchanges[1].ProvenanceOrigin));
            Assert.Equal(4, server.Requests.Count);

            Assert.Throws<ArgumentException>(() => ReplaySource.FromCaptures(rows, new[] { first }));
            Assert.Throws<ArgumentException>(() => ReplaySource.FromCaptures(rows, new[] { first, new Uri("/two", UriKind.Relative) }));
        }

        // A cancellation that a send's Begin precedes means the send never began, and nothing is recorded; one that follows
        // it is that send's, and its exchange is recorded (Y1).
        [Theory]
        [InlineData("before-begin", 0)]
        [InlineData("after-begin", 1)]
        public async Task ACancellationAtBegin_RecordsTheSendOnlyIfItBegan(string point, int recorded)
        {
            using var cancel = new CancellationTokenSource();
            RawRequestImage image = Image(Target("http://127.0.0.1:9"), "GET / HTTP/1.1\r\nHost: 127.0.0.1:9\r\n\r\n");
            var operation = new ReplayOperation
            {
                RawSenderFactory = setup => new RawSender(setup with
                {
                    Connector = async (_, token) =>
                    {
                        await Task.Delay(Timeout.Infinite, token);
                        return Stream.Null;
                    },
                    Hooks = new RawSenderHooks(At: (at, started) =>
                    {
                        if ((point == "before-begin" && at == RawSendPoint.BeforeBegin) ||
                            (point == "after-begin" && at == RawSendPoint.AfterInvoke && started == RawOperation.Connect))
                        {
                            cancel.Cancel();
                        }
                    }),
                }),
            };
            var sink = new RecordingSink();

            OperationResult<ReplayResult> result = await operation.ExecuteAsync(
                new ReplayRequest(ReplaySource.FromRaw(image), new ReplayOptions(Transport: Http11, AllowRaw: Consent(image)), Sink: sink),
                OperationContext.None,
                cancel.Token).WaitAsync(Wait);

            Assert.Equal(OperationOutcome.Cancelled, result.Outcome);
            Assert.Equal(recorded, sink.Results.Count);
            Assert.All(sink.Results, item => Assert.Equal(
                (ReplayOutcome.Failed, RawCodes.SendCancelled, RawWriteState.None), (item.Outcome, item.Code, item.RequestWrite)));
        }

        // Post-attempt publication across sends (X1): three sends in flight, one held in setup, one reading after its write,
        // and one that ended and is held between its end and the sink, then a cancellation. Each send that began is
        // recorded, the ended one included, and the fourth, which never began, is not.
        [Fact]
        public async Task ACancelledRun_RecordsEverySendThatBegan_AndNoneThatDidNot()
        {
            RawRequestImage image = Image(Target("http://127.0.0.1:9"), "GET /x HTTP/1.1\r\nHost: 127.0.0.1:9\r\n\r\n");
            int connects = 0;
            var setupHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var wrote = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ended = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reading = new SeamConnection { AfterWrite = _ => wrote.TrySetResult() };
            var answering = new SeamConnection();
            answering.Respond("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");
            var operation = new ReplayOperation
            {
                RawSenderFactory = setup => new RawSender(setup with
                {
                    Connector = async (_, token) =>
                    {
                        switch (Interlocked.Increment(ref connects))
                        {
                            case 1:
                                setupHeld.TrySetResult();
                                await Task.Delay(Timeout.Infinite, token);
                                throw new InvalidOperationException("The held setup ended on its own.");
                            case 2:
                                return reading;
                            case 3:
                                return answering;
                            default:
                                throw new InvalidOperationException("A fourth send began.");
                        }
                    },
                }),
                RawSendEnded = id =>
                {
                    ended.TrySetResult(id);
                    return release.Task;
                },
            };
            using var cancel = new CancellationTokenSource();
            var sink = new RecordingSink();

            Task<OperationResult<ReplayResult>> run = operation.ExecuteAsync(
                new ReplayRequest(
                    ReplaySource.FromRaw(image),
                    new ReplayOptions(Repeat: 4, Parallelism: 3, Transport: Http11, AllowRaw: Consent(image)),
                    Sink: sink),
                OperationContext.None,
                cancel.Token);
            await Task.WhenAll(setupHeld.Task, wrote.Task, reading.ReadWaiting.Task, ended.Task).WaitAsync(Wait);
            Assert.Empty(sink.Results);
            cancel.Cancel();
            release.SetResult();
            OperationResult<ReplayResult> result = await run.WaitAsync(Wait);
            int endedId = await ended.Task;

            Assert.Equal((OperationOutcome.Cancelled, "replay.cancelled"), (result.Outcome, Assert.Single(result.Diagnostics).Code));
            Assert.Equal(3, Volatile.Read(ref connects));
            Assert.Equal(new[] { 1, 2, 3 }, sink.Results.Select(item => item.Id).Order());
            Assert.Contains(sink.Results, item =>
                item.Id == endedId && (item.Outcome, item.StatusCode, item.RequestWrite) == (ReplayOutcome.Completed, 200, RawWriteState.Complete));
            Assert.Contains(sink.Results, item =>
                (item.Outcome, item.Code, item.RequestWrite) == (ReplayOutcome.Failed, RawCodes.SendCancelled, RawWriteState.None));
            Assert.Contains(sink.Results, item =>
                (item.Outcome, item.Code, item.RequestWrite) == (ReplayOutcome.Failed, RawCodes.SendCancelled, RawWriteState.Complete));
            Assert.All(sink.Exchanges, exchange => Assert.Null(exchange.Error));
        }

        // Raw sends interleaved with client-certificate (K2) and Windows-authentication (K3) runs of one operation, to one
        // server that asks for a certificate and challenges for Windows authentication, one after another and then at the
        // same time: the raw sends carry no certificate and no Authorization, while the K2 and K3 sends carry theirs.
        [Fact]
        public async Task RawSendsInterleavedWithK2AndK3_CarryNoCertificateAndNoAuthorization()
        {
            using var authority = new TestCertificateAuthority();
            using X509Certificate2 client = authority.IssueClient();
            using X509Certificate2 certificate = authority.Issue(address: IPAddress.Loopback);
            await using var server = new LoopbackAuthServer(
                new LoopbackTls(certificate, ClientCertificate: LoopbackClientCertificate.Optional),
                new LoopbackAuthOptions(new[] { "Negotiate" }, "Negotiate"));
            var operation = new ReplayOperation
            {
                CertificateNative = new RecordingCertificateNative(client),
                ServerChainPolicyFactory = authority.Policy,
            };
            Assert.True(ReplayClientCertificate.TryParse(client.Thumbprint, out ReplayClientCertificate clientCertificate));
            ReplayCredentialOrigin origin = ReplayCredentialOrigin.Of(new Uri(server.Origin));
            RawRequestImage image = Image(Target(server.Origin), $"GET /raw HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\n\r\n");

            Task<ReplayItemResult> K2() => RunOneAsync(operation, new ReplayRequest(
                ReplaySource.FromDraft(new RequestReplayDraft { Url = server.Origin + "/k2" }),
                new ReplayOptions(Transport: new ReplayTransportOptions(ClientCertificate: clientCertificate), AllowClientCertificate: origin)));
            Task<ReplayItemResult> K3() => RunOneAsync(operation, new ReplayRequest(
                ReplaySource.FromDraft(new RequestReplayDraft { Url = server.Origin + "/k3" }),
                new ReplayOptions(Transport: new ReplayTransportOptions(WindowsAuth: true), AllowWindowsAuth: origin)));
            Task<ReplayItemResult> Raw() => RunOneAsync(operation, new ReplayRequest(
                ReplaySource.FromRaw(image), new ReplayOptions(Transport: Http11, AllowRaw: Consent(image))));

            var raws = new List<ReplayItemResult> { await Raw() };
            Assert.Equal(401, (await K2()).StatusCode);
            raws.Add(await Raw());
            Assert.Equal(200, (await K3()).StatusCode);
            raws.Add(await Raw());
            ReplayItemResult[] together = await Task.WhenAll(Raw(), K2(), K3(), Raw()).WaitAsync(Wait);
            raws.Add(together[0]);
            raws.Add(together[3]);

            Assert.All(raws, item => Assert.Equal(
                (ReplayOutcome.Completed, 401, true, RawWriteState.Complete), (item.Outcome, item.StatusCode, item.Raw, item.RequestWrite)));
            LoopbackAuthRequest[] rawRequests = server.Requests.Where(request => request.Target == "/raw").ToArray();
            Assert.Equal(5, rawRequests.Length);
            Assert.All(rawRequests, request => Assert.Equal(((string)null, 0), (request.AuthScheme, request.AuthLength)));
            Assert.Contains(server.Requests, request => request.Target == "/k3" && request.AuthScheme == "Negotiate");
            Assert.Equal(
                new[] { client.Thumbprint, client.Thumbprint },
                server.ClientCertificates.Select(offered => offered.Thumbprint));
            Assert.DoesNotContain(
                server.ClientCertificates, offered => rawRequests.Any(request => request.Connection == offered.Connection));
        }

        private static ReplayRawTarget Target(string text)
        {
            Assert.True(ReplayRawTarget.TryParse(text, out ReplayRawTarget target));
            return target;
        }

        private static RawRequestImage Image(ReplayRawTarget target, string request)
        {
            RawImageResult result = RawRequest.FromBytes(target, Encoding.Latin1.GetBytes(request), false, CancellationToken.None);
            Assert.True(result.Code == null, $"{result.Code} at {result.Line}");
            return result.Image;
        }

        // The consent that the GUI and the command line give: for the plan that Preview checked.
        private static ReplayRawConsent Consent(RawRequestImage image)
        {
            OperationResult<ReplayPlanPreview> plan = new ReplayOperation().Preview(
                ReplaySource.FromRaw(image), new ReplayOptions(Transport: Http11));
            Assert.True(plan.Succeeded, string.Join(", ", plan.Diagnostics.Select(diagnostic => diagnostic.Code)));
            return ReplayRawConsent.For(plan.Value);
        }

        private static Task<OperationResult<ReplayResult>> Execute(ReplayOperation operation, ReplayRequest request) =>
            operation.ExecuteAsync(request, OperationContext.None, CancellationToken.None).WaitAsync(Wait);

        private static async Task<ReplayExchange> SendOneAsync(ReplayOperation operation, ReplayRequest request)
        {
            var sink = new RecordingSink();
            OperationResult<ReplayResult> result = await Execute(operation, request with { Sink = sink });
            Assert.True(result.Succeeded, Describe(result));
            return Assert.Single(sink.Exchanges);
        }

        private static async Task<ReplayItemResult> RunOneAsync(ReplayOperation operation, ReplayRequest request)
        {
            OperationResult<ReplayResult> result = await Execute(operation, request);
            Assert.True(result.Succeeded, Describe(result));
            return Assert.Single(result.Value.Items);
        }

        private static void AssertOriginChangeRefused(OperationResult<ReplayResult> result)
        {
            Assert.Equal(OperationOutcome.InvalidInput, result.Outcome);
            OperationDiagnostic diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal(("replay.policy.origin-change", OriginChangeMessage), (diagnostic.Code, diagnostic.Message));
        }

        private static string Describe(OperationResult<ReplayResult> result) =>
            $"{result.Outcome}: {string.Join(", ", result.Diagnostics.Select(diagnostic => diagnostic.Code))}";

        private static ProxyConnectionModel Row(long number, string url)
        {
            var uri = new Uri(url);
            return new ProxyConnectionModel(
                number, "200", DateTime.UtcNow, TimeSpan.Zero, uri.Scheme, uri.Authority, "GET", uri.PathAndQuery,
                $"GET {uri.PathAndQuery} HTTP/1.1\r\nHost: {uri.Authority}\r\n", [], "HTTP/1.1 200 OK\r\n", [], "127.0.0.1");
        }

        // A raw sender factory whose senders count every factory call, lookup and connect, and never reach a network.
        private sealed class CountingSeams
        {
            private int _factories;
            private int _lookups;
            private int _connects;

            internal int Factories => Volatile.Read(ref _factories);

            internal int Lookups => Volatile.Read(ref _lookups);

            internal int Connects => Volatile.Read(ref _connects);

            internal ReplayOperation Apply(ReplayOperation operation)
            {
                operation.RawSenderFactory = setup =>
                {
                    Interlocked.Increment(ref _factories);
                    return new RawSender(setup with
                    {
                        Resolver = (_, _) =>
                        {
                            Interlocked.Increment(ref _lookups);
                            return Task.FromResult(new[] { IPAddress.Loopback });
                        },
                        Connector = (_, _) =>
                        {
                            Interlocked.Increment(ref _connects);
                            return Task.FromException<Stream>(new IOException("No network in this test."));
                        },
                    });
                };
                return operation;
            }
        }

        private sealed class RecordingSink : IReplayExchangeSink
        {
            private readonly object _lock = new();

            internal List<ReplayItemResult> Results { get; } = new();

            internal List<ReplayExchange> Exchanges { get; } = new();

            public void Add(ReplayItemResult result, ReplayExchange exchange)
            {
                lock (_lock)
                {
                    Results.Add(result);
                    Exchanges.Add(exchange);
                }
            }
        }

        // Fails every time it is given a send, and counts the times.
        private sealed class FailingSink : IReplayExchangeSink
        {
            private int _calls;

            internal int Calls => Volatile.Read(ref _calls);

            public void Add(ReplayItemResult result, ReplayExchange exchange)
            {
                Interlocked.Increment(ref _calls);
                throw new IOException("The disk is full.");
            }
        }

        // Records the ids of the sends that it is given, in order, and runs onSecond when it is given the second.
        private sealed class SinkOfSecondSend(Action onSecond) : IReplayExchangeSink
        {
            private readonly List<int> _given = new();

            internal int[] Given
            {
                get
                {
                    lock (_given)
                    {
                        return _given.ToArray();
                    }
                }
            }

            public void Add(ReplayItemResult result, ReplayExchange exchange)
            {
                lock (_given)
                {
                    _given.Add(result.Id);
                }

                if (result.Id == 2)
                {
                    onSecond();
                }
            }
        }

        // Every connect of the operation's raw sends answers 200 at once, on a connection of its own.
        private static ReplayOperation Answering(ReplayOperation operation)
        {
            operation.RawSenderFactory = setup => new RawSender(setup with
            {
                Connector = (_, _) =>
                {
                    var connection = new SeamConnection();
                    connection.Respond("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");
                    return Task.FromResult<Stream>(connection);
                },
            });
            return operation;
        }
    }
}
