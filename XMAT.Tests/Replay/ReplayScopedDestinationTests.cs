// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Http;
using XMAT.Operations;
using XMAT.Operations.Replay;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Replay
{
    public class ReplayScopedDestinationTests
    {
        [Theory]
        [InlineData("http://[fe80::1%12]/")]
        [InlineData("http://[fe80::1%Ethernet]/")]
        [InlineData("http://[fe80::1%unknown-interface]/")]
        [InlineData("http://[fe80::1%2518]/")]
        [InlineData("http://[fe80::1%0]/")]
        public async Task PreviewAndExecute_RefuseEveryScopedIpv6DestinationBeforeASender(string url)
        {
            int sends = 0;
            int senders = 0;
            ReplayOperation operation = Operation(() => sends++, () => senders++);
            ReplaySource source = ReplaySource.FromDraft(new RequestReplayDraft { Method = "POST", Url = url });

            OperationResult<ReplayPlanPreview> preview = operation.Preview(source);
            OperationResult<ReplayResult> run = await operation.ExecuteAsync(
                new ReplayRequest(source), OperationContext.None, CancellationToken.None);

            AssertScoped(preview);
            AssertScoped(run);
            Assert.Equal(0, sends);
            Assert.Equal(0, senders);
        }

        [Fact]
        public async Task MixedCaptureSelection_RefusesAllBeforeTheFirstSend()
        {
            ProxyConnectionModel ordinary = Capture("example.test:80", "/ordinary");
            ProxyConnectionModel scoped = Capture("[fe80::1%12]:8080", "/scoped");
            Assert.True(RequestReplayDraft.CanReplay(scoped));
            int sends = 0;
            int senders = 0;
            ReplayOperation operation = Operation(() => sends++, () => senders++);

            OperationResult<ReplayResult> result = await operation.ExecuteAsync(
                new ReplayRequest(ReplaySource.FromCaptures(new[] { ordinary, scoped })),
                OperationContext.None,
                CancellationToken.None);

            AssertScoped(result);
            Assert.Equal(0, sends);
            Assert.Equal(0, senders);
        }

        [Fact]
        public void ReissueAdmission_RefusesTheWholeMixedSelectionWithAbsoluteTargetPrecedence()
        {
            ProxyConnectionModel structuredScopedButAbsoluteUnscoped =
                Capture("[fe80::1%12]:8080", "http://example.test/absolute");
            ProxyConnectionModel absoluteScoped =
                Capture("example.test:80", "http://[fe80::2%25Ethernet]/absolute");

            Assert.Null(ReplayOperation.CheckCaptureDestinations(
                new[] { structuredScopedButAbsoluteUnscoped }));
            OperationDiagnostic diagnostic = ReplayOperation.CheckCaptureDestinations(
                new[] { Capture("example.test:80", "/ordinary"), absoluteScoped });

            Assert.NotNull(diagnostic);
            Assert.Equal(ReplayDestination.ScopedIpv6Code, diagnostic.Code);
            Assert.Equal(ReplayDestination.ScopedIpv6Message, diagnostic.Message);
        }

        [Fact]
        public async Task ReissueAdmission_IgnoresAScopedRowThatReplayWouldSkip()
        {
            ProxyConnectionModel ordinary = Capture("example.test:80", "/ordinary");
            ProxyConnectionModel scopedButUnsupported =
                Capture("[fe80::1%12]:8080", "/scoped", "Upgrade: websocket\r\n");
            Assert.False(RequestReplayDraft.CanReplay(scopedButUnsupported));
            ProxyConnectionModel[] selection = { ordinary, scopedButUnsupported };
            OperationDiagnostic diagnostic = ReplayOperation.CheckCaptureDestinations(selection);
            int sends = 0;
            OperationResult<ReplayResult> run = await Operation(() => sends++).ExecuteAsync(
                new ReplayRequest(ReplaySource.FromCaptures(selection)),
                OperationContext.None,
                CancellationToken.None);

            Assert.Null(diagnostic);
            Assert.True(run.Succeeded, Describe(run));
            Assert.Equal(1, sends);
            Assert.Equal(ReplayOperation.SkippedCode, Assert.Single(run.Diagnostics).Code);

            Assert.Null(ReplayOperation.CheckCaptureDestinations(new[] { scopedButUnsupported }));
            int ineligibleSends = 0;
            OperationResult<ReplayResult> ineligible = await Operation(() => ineligibleSends++).ExecuteAsync(
                new ReplayRequest(ReplaySource.FromCaptures(new[] { scopedButUnsupported })),
                OperationContext.None,
                CancellationToken.None);

            Assert.Equal(OperationOutcome.InvalidInput, ineligible.Outcome);
            Assert.Equal("replay.request.none", Assert.Single(ineligible.Diagnostics).Code);
            Assert.Equal(0, ineligibleSends);
        }

        // One destination decision for every use of a captured row: Reissue's admission before its question, History's
        // status, the run and the URL that it makes agree on rows that send, rows refused as scoped and rows refused as
        // malformed, beyond the decision's limit included. Sol's RB-A1 rows, which History's former bounded checks could not
        // read, so that Reissue asked about them before the run refused them, are among the scoped ones.
        [Theory]
        [InlineData("structured", "sent")]
        [InlineData("absolute over a scoped stored host", "sent")]
        [InlineData("refused head over a stored host", "sent")]
        [InlineData("structured scoped", "scoped")]
        [InlineData("absolute scoped", "scoped")]
        [InlineData("refused head over a scoped stored host", "scoped")]
        [InlineData("stored zone beyond 255", "scoped")]
        [InlineData("absolute zone beyond 512", "scoped")]
        [InlineData("stored authority that makes no URL", "malformed")]
        [InlineData("refused head over a stored authority that makes no URL", "malformed")]
        [InlineData("head beyond the limit", "malformed")]
        [InlineData("leading whitespace beyond the limit", "malformed")]
        [InlineData("stored host beyond the limit", "malformed")]
        public async Task EveryUseOfARow_AgreesOnItsDestination(string row, string expected)
        {
            ProxyConnectionModel capture = Row(row);
            Assert.True(RequestReplayDraft.CanReplay(capture));
            int sends = 0;

            OperationDiagnostic admission = ReplayOperation.CheckCaptureDestinations(new[] { capture });
            ReplayCaptureDestinationStatus status = ReplayDestination.ClassifyCaptureDestination(capture);
            OperationResult<ReplayResult> run = await Operation(() => sends++).ExecuteAsync(
                new ReplayRequest(ReplaySource.FromCaptures(new[] { capture })),
                OperationContext.None,
                CancellationToken.None);

            switch (expected)
            {
                case "sent":
                    Assert.Null(admission);
                    Assert.Equal(ReplayCaptureDestinationStatus.Loadable, status);
                    Assert.True(run.Succeeded, Describe(run));
                    Assert.Equal(1, sends);
                    Assert.False(ReplayDestination.IsScopedIpv6(RequestReplayDraft.CaptureUrl(capture)));
                    break;
                case "scoped":
                    Assert.Equal(ReplayDestination.ScopedIpv6Code, admission?.Code);
                    Assert.Equal(ReplayCaptureDestinationStatus.ScopedIpv6, status);
                    AssertScoped(run);
                    Assert.Equal(0, sends);
                    Assert.True(ReplayDestination.IsScopedIpv6(RequestReplayDraft.CaptureUrl(capture)));
                    break;
                default:
                    Assert.Equal(
                        (ReplayOperation.RequestMalformedCode, OperationDiagnosticSeverity.Error,
                            ReplayOperation.RequestMalformedMessage),
                        (admission?.Code, admission?.Severity, admission?.Message));
                    Assert.Equal(ReplayCaptureDestinationStatus.Unreadable, status);
                    Assert.Equal(OperationOutcome.InvalidInput, run.Outcome);
                    Assert.Equal(ReplayOperation.RequestMalformedCode, Assert.Single(run.Diagnostics).Code);
                    Assert.Equal(0, sends);
                    Assert.Throws<UriFormatException>(() => RequestReplayDraft.CaptureUrl(capture));
                    break;
            }
        }

        // A malformed destination refuses the whole plan before a scoped one, in Reissue's admission as in the run, whose
        // gathering refuses it before its checks would find the scoped one. On its own, the scoped row keeps its own refusal.
        [Fact]
        public async Task AMalformedDestination_IsRefusedBeforeAScopedOne()
        {
            ProxyConnectionModel scoped = Row("structured scoped");
            ProxyConnectionModel malformed = Row("head beyond the limit");
            foreach (ProxyConnectionModel[] selection in new[] { new[] { scoped, malformed }, new[] { malformed, scoped } })
            {
                int sends = 0;

                OperationDiagnostic admission = ReplayOperation.CheckCaptureDestinations(selection);
                OperationResult<ReplayResult> run = await Operation(() => sends++).ExecuteAsync(
                    new ReplayRequest(ReplaySource.FromCaptures(selection)),
                    OperationContext.None,
                    CancellationToken.None);

                Assert.Equal(ReplayOperation.RequestMalformedCode, admission?.Code);
                Assert.Equal(ReplayOperation.RequestMalformedCode, Assert.Single(run.Diagnostics).Code);
                Assert.Equal(0, sends);
            }

            Assert.Equal(ReplayDestination.ScopedIpv6Code, ReplayOperation.CheckCaptureDestinations(new[] { scoped })?.Code);
        }

        // A row that the run skips is not refused for its destination, however malformed: the rest is admitted and sent.
        [Fact]
        public async Task ReissueAdmission_IgnoresAMalformedRowThatReplayWouldSkip()
        {
            ProxyConnectionModel upgrade = Stored(
                new string(' ', ReplayDestination.MaxCaptureDestinationChars) + "http://example.test/p",
                "example.test",
                "80",
                "Upgrade: websocket\r\n");
            Assert.False(RequestReplayDraft.CanReplay(upgrade));
            Assert.Equal(CaptureDestination.OverLimit, ReplayDestination.ReadCaptureDestination(upgrade));
            ProxyConnectionModel[] selection = { Row("structured"), upgrade };
            int sends = 0;

            OperationDiagnostic admission = ReplayOperation.CheckCaptureDestinations(selection);
            OperationResult<ReplayResult> run = await Operation(() => sends++).ExecuteAsync(
                new ReplayRequest(ReplaySource.FromCaptures(selection)),
                OperationContext.None,
                CancellationToken.None);

            Assert.Null(admission);
            Assert.True(run.Succeeded, Describe(run));
            Assert.Equal(1, sends);
            Assert.Equal(ReplayOperation.SkippedCode, Assert.Single(run.Diagnostics).Code);
        }

        [Theory]
        [InlineData("proxy")]
        [InlineData("connect-to")]
        public async Task ScopedDestination_IsRefusedWithOtherTransportOptions(string transportKind)
        {
            Assert.True(ReplayProxy.TryParse("http://127.0.0.1:3128", out ReplayProxy proxy));
            Assert.True(ReplayConnectTo.TryParse("127.0.0.1:8443", out ReplayConnectTo connectTo));
            bool throughProxy = transportKind == "proxy";
            var options = new ReplayOptions(
                AllowConnectTo: !throughProxy,
                Transport: new ReplayTransportOptions(
                    ConnectTo: throughProxy ? null : connectTo,
                    Proxy: throughProxy ? proxy : null),
                AllowProxy: throughProxy);
            int sends = 0;
            ReplayOperation operation = Operation(() => sends++);

            OperationResult<ReplayResult> result = await operation.ExecuteAsync(
                new ReplayRequest(
                    ReplaySource.FromDraft(new RequestReplayDraft { Url = "https://[fe80::1%12]/item" }),
                    options),
                OperationContext.None,
                CancellationToken.None);

            AssertScoped(result);
            Assert.Equal(0, sends);
        }

        [Fact]
        public async Task ScopedOriginEdit_IsKeptForExportAndRefusedForSending()
        {
            int sends = 0;
            ReplayOperation operation = Operation(() => sends++);
            ReplayRequest Request() =>
                new(
                    ReplaySource.FromDraft(new RequestReplayDraft { Url = "http://example.test/items/1" }),
                    Edits: new ReplayEdits(Origin: "http://[fe80::1%0]/"));

            OperationResult<ReplayExport> export = operation.PrepareForExport(Request(), CancellationToken.None);
            OperationResult<ReplayResult> send = await operation.ExecuteAsync(
                Request(), OperationContext.None, CancellationToken.None);

            Assert.True(export.Succeeded, Describe(export));
            Assert.Equal("http://[fe80::1%0]/items/1", Assert.Single(export.Value.Items).Draft.Url);
            AssertScoped(send);
            Assert.Equal(0, sends);
        }

        [Fact]
        public async Task ScopedSequence_IsRefusedInPreviewAndBeforeTheFirstSend()
        {
            Assert.True(ReplaySequence.TryParse("1-2", out ReplaySequence sequence));
            ReplaySource source = ReplaySource.FromTemplate(
                new RequestReplayDraft { Url = "http://[fe80::1%25Ethernet]/items/#?next=#" },
                sequence);
            int sends = 0;
            ReplayOperation operation = Operation(() => sends++);

            OperationResult<ReplayPlanPreview> preview = operation.Preview(source);
            OperationResult<ReplayResult> run = await operation.ExecuteAsync(
                new ReplayRequest(source), OperationContext.None, CancellationToken.None);

            AssertScoped(preview);
            AssertScoped(run);
            Assert.Equal(0, sends);
        }

        [Fact]
        public async Task OmittedRequestFilePart_PrecedesScopedDestinationForSend()
        {
            RequestReplayDraft draft = RequestReplayDraft.FromRequestFile(new RequestFileContent(
                RequestFileProfile.Content,
                "GET",
                "http://[fe80::1%12]/item",
                ReplayHttpVersion.Auto,
                null,
                Array.Empty<byte>(),
                new RequestOmissions(false, false, true, false)));
            int sends = 0;

            OperationResult<ReplayResult> result = await Operation(() => sends++).ExecuteAsync(
                new ReplayRequest(ReplaySource.FromDraft(draft)),
                OperationContext.None,
                CancellationToken.None);

            Assert.Equal(RequestFileCodes.RequestIncomplete, Assert.Single(result.Diagnostics).Code);
            Assert.Equal(0, sends);
        }

        [Fact]
        public async Task ExplicitUnscopedReplacement_StillNeedsOriginChangeConsent()
        {
            ProxyConnectionModel scoped = Capture("[fe80::1%12]:8080", "/item");
            ReplayRequest Request(bool allow) =>
                new(
                    ReplaySource.FromCaptures(new[] { scoped }),
                    new ReplayOptions(AllowOriginChange: allow),
                    new ReplayEdits(Url: "http://example.test/item"));
            int sends = 0;
            ReplayOperation operation = Operation(() => sends++);

            OperationResult<ReplayResult> refused = await operation.ExecuteAsync(
                Request(allow: false), OperationContext.None, CancellationToken.None);
            OperationResult<ReplayResult> allowed = await operation.ExecuteAsync(
                Request(allow: true), OperationContext.None, CancellationToken.None);

            Assert.Equal("replay.policy.origin-change", Assert.Single(refused.Diagnostics).Code);
            Assert.True(allowed.Succeeded, Describe(allowed));
            Assert.Equal(1, sends);
        }

        [Fact]
        public async Task PercentEscapesOutsideAnIpv6Host_RemainSendable()
        {
            int sends = 0;
            ReplayOperation operation = Operation(() => sends++);
            var source = ReplaySource.FromDraft(
                new RequestReplayDraft { Url = "http://example.test/items/%25?q=%2518" });

            OperationResult<ReplayPlanPreview> preview = operation.Preview(source);
            OperationResult<ReplayResult> run = await operation.ExecuteAsync(
                new ReplayRequest(source), OperationContext.None, CancellationToken.None);

            Assert.True(preview.Succeeded);
            Assert.True(run.Succeeded, Describe(run));
            Assert.Equal(1, sends);
        }

        [Fact]
        public async Task RedirectToAScopedDestination_StopsAfterTheFirstHop()
        {
            int sends = 0;
            using var sender = new ReplaySender(
                new HttpMessageInvoker(new RedirectHandler(() => sends++)), ownsInvoker: true);

            ReplayExchange exchange = await sender.SendAsync(
                new RequestReplayDraft { Url = "http://example.test/start" },
                ReplaySendOptions.Default with { MaxRedirects = 1 },
                CancellationToken.None);

            Assert.Equal(1, sends);
            Assert.Equal(
                (ReplayOutcome.Completed, HttpStatusCode.Redirect, ReplayDestination.ScopedIpv6Code, 0),
                (exchange.Outcome, (HttpStatusCode)exchange.StatusCode, exchange.Code, exchange.Redirects));
        }

        private static ReplayOperation Operation(Action sent, Action created = null)
        {
            ReplaySender Sender()
            {
                created?.Invoke();
                return new ReplaySender(new HttpMessageInvoker(new OkHandler(sent)), ownsInvoker: true);
            }
            return new ReplayOperation
            {
                SenderFactory = Sender,
                OwnSenderFactory = _ => Sender(),
            };
        }

        private static ProxyConnectionModel Capture(string authority, string path, string headers = "") =>
            new(
                1, "200", DateTime.UnixEpoch, TimeSpan.Zero, "http", authority, "GET", path,
                $"GET {path} HTTP/1.1\r\n{headers}\r\n", Array.Empty<byte>(),
                "HTTP/1.1 200 OK\r\n\r\n", Array.Empty<byte>(), "127.0.0.1");

        // A captured row with this target and stored host and port, set as a capture file stores them, without the parsing
        // of a host-and-port string.
        private static ProxyConnectionModel Stored(string path, string host, string port, string headers = "")
        {
            ProxyConnectionModel capture = new(
                1, "200", DateTime.UnixEpoch, TimeSpan.Zero, "http", "example.test:80", "GET", path,
                $"GET /p HTTP/1.1\r\n{headers}\r\n", Array.Empty<byte>(),
                "HTTP/1.1 200 OK\r\n\r\n", Array.Empty<byte>(), "127.0.0.1");
            typeof(ProxyConnectionModel).GetProperty(nameof(ProxyConnectionModel.Host)).SetValue(capture, host);
            typeof(ProxyConnectionModel).GetProperty(nameof(ProxyConnectionModel.Port)).SetValue(capture, port);
            return capture;
        }

        private static ProxyConnectionModel Row(string name)
        {
            const int Limit = ReplayDestination.MaxCaptureDestinationChars;
            return name switch
            {
                "structured" => Stored("/p", "example.test", "80"),
                "absolute over a scoped stored host" => Stored("http://example.test/p", "fe80::1%12", "8080"),
                "refused head over a stored host" => Stored("http://example.test\\p", "example.test", "80"),
                "structured scoped" => Stored("/p", "fe80::1%12", "8080"),
                "absolute scoped" => Stored("http://[fe80::1%12]/p", "example.test", "80"),
                "refused head over a scoped stored host" => Stored("http://example.test\\p", "fe80::1%12", "8080"),
                "stored zone beyond 255" => Stored("/p", "fe80::1%" + new string('z', 300), "8080"),
                "absolute zone beyond 512" => Stored("http://[fe80::1%" + new string('z', 600) + "]/p", "example.test", "80"),
                "stored authority that makes no URL" => Stored("/p", "bad\u0007host", "x"),
                "refused head over a stored authority that makes no URL" =>
                    Stored("http://[fe80::1%12/p", "bad\u0007host", "x"),
                "head beyond the limit" =>
                    Stored("http://" + ReplayCaptures.LongDnsName(Limit) + "/p", "example.test", "80"),
                "leading whitespace beyond the limit" =>
                    Stored(new string(' ', Limit) + "http://example.test/p", "example.test", "80"),
                "stored host beyond the limit" => Stored("/p", ReplayCaptures.LongDnsName(Limit + 1), "80"),
                _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
            };
        }

        private static void AssertScoped<T>(OperationResult<T> result)
        {
            Assert.Equal(OperationOutcome.InvalidInput, result.Outcome);
            OperationDiagnostic diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal(OperationDiagnosticSeverity.Error, diagnostic.Severity);
            Assert.Equal(ReplayDestination.ScopedIpv6Code, diagnostic.Code);
            Assert.Equal(ReplayDestination.ScopedIpv6Message, diagnostic.Message);
        }

        private static string Describe<T>(OperationResult<T> result) =>
            string.Join(", ", result.Diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message));

        private sealed class OkHandler(Action sent) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                sent();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Array.Empty<byte>()),
                });
            }
        }

        private sealed class RedirectHandler(Action sent) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                sent();
                var response = new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    Content = new ByteArrayContent(Array.Empty<byte>()),
                };
                response.Headers.TryAddWithoutValidation("Location", "http://[fe80::1%12]/next");
                return Task.FromResult(response);
            }
        }
    }
}
