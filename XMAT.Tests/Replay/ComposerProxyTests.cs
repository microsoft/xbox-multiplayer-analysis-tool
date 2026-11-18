// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;
using XMAT.Models;
using XMAT.Operations;
using XMAT.Operations.Replay;
using XMAT.Operations.Storage;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using FrozenSend = XMAT.WebServiceCapture.RequestComposerWindow.FrozenSend;

namespace XMAT.Tests.Replay
{
    // The composer's Upstream proxy: its controls and their names; the one confirmation, which names the proxy
    // and says what it receives; the proxy that a run sends through, as checked and confirmed; the refusals before anything
    // is asked or sent; failures worded by their codes, never by an exception's message; the resets of Open request, Load
    // and a drop; and what the Replays tab, the history and request files keep: never the proxy. Runs send to in-memory
    // handlers through senders made for the proxy, or through a loopback proxy, so nothing outside this PC is reached.
    [Collection(CaptureUiApplicationCollection.Name)]
    public class ComposerProxyTests
    {
        private const string Url = "http://xmat.test/item";
        private const string Proxy = "http://proxy.xmat.test:8080";

        // What a failing HTTP stack says, naming the proxy, which the composer never shows for a send through one.
        private const string CanaryMessage = "CANARY: the proxy http://proxy.xmat.test:8080/ failed";

        // A template request file, as XMAT saves one: it keeps the method and the HTTP version, and leaves out the URL, the
        // headers and the body.
        private const string TemplateFile =
            "{ \"schema\": \"xmat-request/1\", \"profile\": \"template\", \"method\": \"POST\", \"url\": null, " +
            "\"httpVersion\": \"auto\", \"headers\": null, \"body\": null, \"omitted\": [\"url\", \"headers\", \"body\"] }";

        private static readonly Func<string, bool> NoQuestion =
            message => throw new Xunit.Sdk.XunitException("Nothing needs confirmation here: " + message);

        private readonly CaptureUiApplicationFixture _application;

        public ComposerProxyTests(CaptureUiApplicationFixture application)
        {
            _application = application;
        }

        static ComposerProxyTests()
        {
            Localization.LoadLanguage("en-us");
        }

        // Upstream proxy is off by default, and its text is available only while it is selected. Both are read with the
        // hint that says what the proxy receives, and each has its name for automation: the check box its content's, the
        // text box one of its own, since the check box beside it labels nothing.
        [Fact]
        public Task UpstreamProxy_IsOffByDefault_ItsTextAvailableOnlyWhileSelected_AndBothNamedForAutomation() => _application.RunAsync(() =>
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            try
            {
                var proxy = Find<CheckBox>(window, "ProxyBox");
                var text = Find<TextBox>(window, "ProxyText");
                string hint = Words("COMPOSER_PROXY_HINT");
                Assert.Equal((false, false, ""), (proxy.IsChecked == true, text.IsEnabled, text.Text));
                AutomationPeer box = UIElementAutomationPeer.CreatePeerForElement(proxy);
                AutomationPeer field = UIElementAutomationPeer.CreatePeerForElement(text);
                Assert.Equal(
                    new[] { "Upstream proxy:", hint, "Upstream proxy URL", hint },
                    new[] { box.GetName(), box.GetHelpText(), field.GetName(), field.GetHelpText() });

                proxy.IsChecked = true;
                Assert.True(text.IsEnabled);
                proxy.IsChecked = false;
                Assert.False(text.IsEnabled);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });

        // Upstream proxy needs the run's one confirmation, which names the proxy as the check of the run parsed it and says
        // what the proxy receives, for one request or one per number, and also covers a method that may change server data.
        // Declined, nothing is sent; accepted, every request goes through a sender made for that proxy, never the composer's
        // direct transport, with its URL unchanged. The rows say how the requests were sent; neither they nor the history
        // name the proxy.
        [Theory]
        [InlineData(true, false, "GET", " HTTP://Proxy.XMAT.test:8080/ ", Proxy)]
        [InlineData(false, false, "GET", Proxy, Proxy)]
        [InlineData(true, true, "GET", "http://[::1]", "http://[::1]:80")]
        [InlineData(true, false, "POST", "http://192.0.2.20:3128", "http://192.0.2.20:3128")]
        public Task UpstreamProxy_NeedsTheOneConfirmation_NamingTheProxy_AndSendsOnlyThroughIt(
            bool accept, bool numbered, string method, string typed, string proxy) => _application.RunAsync(async () =>
        {
            using var direct = new InMemoryHandler();
            using var httpClient = new HttpClient(direct);
            using var client = new RequestReplayClient(httpClient);
            using var throughProxy = new InMemoryHandler();
            var proxies = new List<string>();
            client.OperationFactory = () => ProxyingOperation(proxies, throughProxy);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var questions = new List<string>();
            var window = RequestComposerWindow.ForNewRequest(client, Answer(questions, accept), Recording(model));
            window.ChooseSequence = () => "1-3";
            try
            {
                string url = numbered ? "http://xmat.test/n/#" : Url;
                Find<TextBox>(window, "MethodBox").Text = method;
                Find<TextBox>(window, "UrlBox").Text = url;
                Find<CheckBox>(window, "ProxyBox").IsChecked = true;
                Find<TextBox>(window, "ProxyText").Text = typed;

                await window.SendAsync();

                string send = numbered
                    ? Words("COMPOSER_CONFIRM_SEND_SEQUENCE", 3, method, "1", "3", url)
                    : Words(method == "GET" ? "COMPOSER_CONFIRM_SEND" : "COMPOSER_CONFIRM_MESSAGE", method, url);
                Assert.Equal(send + "\n\n" + Words("COMPOSER_CONFIRM_PROXY", proxy), Assert.Single(questions));
                Assert.Empty(direct.Requests);
                if (!accept)
                {
                    Assert.Empty(proxies);
                    Assert.Empty(throughProxy.Requests);
                    Assert.Empty(model.CaptureDeviceContexts);
                    return;
                }

                Assert.NotEmpty(proxies);
                Assert.All(proxies, sentThrough => Assert.Equal(proxy, sentThrough));
                Assert.Equal(
                    numbered ? new[] { "/n/1", "/n/2", "/n/3" } : new[] { "/item" },
                    throughProxy.Requests.Select(request => request.Uri.PathAndQuery));
                Assert.All(throughProxy.Requests, request => Assert.Equal(("xmat.test", method), (request.Uri.Host, request.Method)));
                ProxyConnectionModel[] rows = Replays(model).ProxyConnections.ToArray();
                Assert.Equal(
                    Enumerable.Repeat(ProxyNote("COMPOSER_HTTP_VERSION_AUTO", "HTTP/1.1"), numbered ? 3 : 1),
                    rows.Select(row => row.ReplayNote));
                string kept = string.Join(
                    "\n",
                    rows.SelectMany(row => new[] { row.Host, row.Port, row.RequestLineAndHeaders, row.ResponseLineAndHeaders })
                        .Concat(model.ReplayHistory.Snapshot().Select(entry => entry.UrlPreview)));
                Assert.DoesNotContain(proxy["http://".Length..], kept, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // The proxy wording, pinned here in English, once: the confirmation's paragraph about the proxy, with what the
        // proxy receives over HTTP and over HTTPS, that XMAT's own proxy does not check servers'
        // certificates and that only a proxy trusted with the requests' credentials should be used; and the status of an
        // HTTP 407 from the proxy. Elsewhere they are read from en-us.
        [Fact]
        public void TheProxyWording_KeepsTheContractsSentencesVerbatim()
        {
            Assert.Equal(
                "Upstream proxy is selected: XMAT sends every request, and every redirect followed, through the HTTP proxy " +
                    "http://proxy.example:8080, never directly. The proxy receives the whole of every http:// request, its " +
                    "Authorization, Cookie and other headers and its body included. For HTTPS, the unencrypted CONNECT request " +
                    "exposes the destination host and port and, when supplied, User-Agent information that may identify the " +
                    "application/title, SDK or device. If the proxy intercepts HTTPS with a certificate this PC trusts, it also " +
                    "receives the whole request and it\u2014not XMAT\u2014decides whether the server is genuine. XMAT's own " +
                    "proxy does not check servers' certificates. Use only a proxy that you trust with the requests' credentials.",
                Words("COMPOSER_CONFIRM_PROXY", "http://proxy.example:8080"));
            Assert.Equal(
                "HTTP 407 (Proxy Authentication Required) was received. XMAT does not authenticate to proxies and did not " +
                    "retry the request.",
                Words("COMPOSER_PROXY_AUTH_REQUIRED"));
        }

        // What is sent is the proxy that was checked and confirmed: changing its text, or turning it off, while XMAT asks
        // changes nothing in that send, which goes through the confirmed proxy only. The next send uses the controls as they
        // are then: directly, and without a question.
        [Fact]
        public Task TheConfirmedProxy_IsFrozen_AgainstEditsMadeWhileXmatAsks() => _application.RunAsync(async () =>
        {
            using var direct = new InMemoryHandler();
            using var httpClient = new HttpClient(direct);
            using var client = new RequestReplayClient(httpClient);
            using var throughProxy = new InMemoryHandler();
            var proxies = new List<string>();
            client.OperationFactory = () => ProxyingOperation(proxies, throughProxy);
            var questions = new List<string>();
            RequestComposerWindow window = null;
            window = RequestComposerWindow.ForNewRequest(
                client,
                message =>
                {
                    questions.Add(message);
                    Find<TextBox>(window, "ProxyText").Text = "http://other.xmat.test:9090";
                    Find<CheckBox>(window, "ProxyBox").IsChecked = false;
                    return true;
                });
            try
            {
                Find<TextBox>(window, "UrlBox").Text = Url;
                Find<CheckBox>(window, "ProxyBox").IsChecked = true;
                Find<TextBox>(window, "ProxyText").Text = Proxy;

                await window.SendAsync();

                Assert.Equal(
                    Words("COMPOSER_CONFIRM_SEND", "GET", Url) + "\n\n" + Words("COMPOSER_CONFIRM_PROXY", Proxy),
                    Assert.Single(questions));
                Assert.Equal(new[] { Proxy }, proxies);
                Assert.Equal("/item", Assert.Single(throughProxy.Requests).Uri.PathAndQuery);
                Assert.Empty(direct.Requests);

                await window.SendAsync();

                Assert.Single(questions);
                Assert.Single(proxies);
                Assert.Single(throughProxy.Requests);
                Assert.Equal("/item", Assert.Single(direct.Requests).Uri.PathAndQuery);
            }
            finally
            {
                window.Close();
            }
        });

        // A proxy that XMAT does not send through is refused by Send's check before anything is asked or sent, in fixed
        // words that never repeat what was typed: no scheme or another one, user information, a path, query or fragment, port
        // 0 or above 65535, a host name that is not ASCII, ends with a dot, holds a space or ends with a number, an address
        // written in another form, and nothing at all. Off, the text is not read: the request is sent directly.
        [Theory]
        [InlineData("canary-proxy.example:8080")]
        [InlineData("https://canary-proxy.example:8443")]
        [InlineData("socks5://canary-proxy.example:1080")]
        [InlineData("http://user:canary@proxy.example:8080")]
        [InlineData("http://proxy.example:8080/canary")]
        [InlineData("http://proxy.example:8080?canary")]
        [InlineData("http://proxy.example:8080#canary")]
        [InlineData("http://canary-proxy.example:0")]
        [InlineData("http://canary-proxy.example:65536")]
        [InlineData("http://canary-pr\u00f6xy.example:8080")]
        [InlineData("http://canary-proxy.example.:8080")]
        [InlineData("http://canary proxy.example:8080")]
        [InlineData("http://canary.123")]
        [InlineData("http://canary.0x7f")]
        [InlineData("http://127.1")]
        [InlineData("http://0x7f.0.0.1")]
        [InlineData("")]
        public Task AProxyThatIsNotOne_IsRefusedBeforeAnythingIsAskedOrSent_WithoutRepeatingIt(string typed) => _application.RunAsync(async () =>
        {
            using var direct = new InMemoryHandler();
            using var httpClient = new HttpClient(direct);
            using var client = new RequestReplayClient(httpClient);
            using var throughProxy = new InMemoryHandler();
            var proxies = new List<string>();
            client.OperationFactory = () => ProxyingOperation(proxies, throughProxy);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion, Recording(model));
            try
            {
                Find<TextBox>(window, "UrlBox").Text = Url;
                Find<CheckBox>(window, "ProxyBox").IsChecked = true;
                Find<TextBox>(window, "ProxyText").Text = typed;

                await window.SendAsync();

                Assert.Equal(ErrorText("COMPOSER_PROXY_INVALID"), Status(window));
                Assert.DoesNotContain("canary", Status(window), StringComparison.OrdinalIgnoreCase);
                Assert.Empty(proxies);
                Assert.Empty(throughProxy.Requests);
                Assert.Empty(direct.Requests);
                Assert.Empty(model.CaptureDeviceContexts);

                Find<CheckBox>(window, "ProxyBox").IsChecked = false;
                await window.SendAsync();

                Assert.Single(direct.Requests);
                Assert.Empty(proxies);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // Send freezes Upstream proxy as typed, neither trimmed nor parsed, and its first check refuses a proxy that is not
        // one off the UI thread.
        [Fact]
        public Task AProxyThatIsNotOne_IsRefusedByTheFirstCheck_OffTheUiThread_AsTyped() => _application.RunAsync(async () =>
        {
            using var client = new RequestReplayClient();
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion);
            int uiThread = Environment.CurrentManagedThreadId;
            var threads = new List<int>();
            FrozenSend frozen = null;
            window.PrepareSend = (send, token) =>
            {
                lock (threads)
                    threads.Add(Environment.CurrentManagedThreadId);
                frozen = send;
                return RequestComposerWindow.CheckSend(send, client, token);
            };
            try
            {
                Find<TextBox>(window, "UrlBox").Text = Url;
                Find<CheckBox>(window, "ProxyBox").IsChecked = true;
                Find<TextBox>(window, "ProxyText").Text = " proxy.xmat.test:8080 ";

                await window.SendAsync();

                Assert.NotEqual(uiThread, Assert.Single(threads));
                Assert.Equal((true, " proxy.xmat.test:8080 "), (frozen.Proxy, frozen.ProxyText));
                Assert.Equal(ErrorText("COMPOSER_PROXY_INVALID"), Status(window));
            }
            finally
            {
                window.Close();
            }
        });

        // Upstream proxy and Connect to another endpoint cannot be used together: the check of the run refuses them, for one
        // request or, once the user chose the numbers, one per number, before anything is asked or sent.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public Task UpstreamProxyAndConnectTo_AreRefusedTogether_BeforeAnythingIsAskedOrSent(bool numbered) => _application.RunAsync(async () =>
        {
            using var direct = new InMemoryHandler();
            using var httpClient = new HttpClient(direct);
            using var client = new RequestReplayClient(httpClient);
            using var throughProxy = new InMemoryHandler();
            var proxies = new List<string>();
            client.OperationFactory = () => ProxyingOperation(proxies, throughProxy);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion, Recording(model));
            int ranges = 0;
            window.ChooseSequence = () =>
            {
                ranges++;
                return "1-2";
            };
            try
            {
                Find<TextBox>(window, "UrlBox").Text = numbered ? "https://xmat.test/n/#" : "https://xmat.test/item";
                Find<CheckBox>(window, "ConnectToBox").IsChecked = true;
                Find<TextBox>(window, "ConnectToText").Text = "127.0.0.1:8443";
                Find<CheckBox>(window, "ProxyBox").IsChecked = true;
                Find<TextBox>(window, "ProxyText").Text = Proxy;

                await window.SendAsync();

                Assert.Equal(ErrorText("COMPOSER_PROXY_CONNECT_TO"), Status(window));
                Assert.Equal(numbered ? 1 : 0, ranges);
                Assert.Empty(proxies);
                Assert.Empty(throughProxy.Requests);
                Assert.Empty(direct.Requests);
                Assert.Empty(model.CaptureDeviceContexts);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // A send through the proxy that gets no response says why in the words of its code, never in the message of the
        // exception that the HTTP stack gave, which can name the proxy: an HTTP 407, as the response or as a refused tunnel,
        // which is no response and is not retried; a tunnel refused otherwise; a proxy that cannot be reached; any other
        // failure; and a timeout. Its row says how it was sent and holds no such message either. A direct send still says
        // why in the exception's message, as it always did.
        [Theory]
        [InlineData("407", "COMPOSER_PROXY_AUTH_REQUIRED")]
        [InlineData("tunnel-407", "COMPOSER_PROXY_AUTH_REQUIRED")]
        [InlineData("tunnel-502", "COMPOSER_PROXY_TUNNEL_FAILED")]
        [InlineData("connection", "COMPOSER_PROXY_UNREACHABLE")]
        [InlineData("name", "COMPOSER_PROXY_UNREACHABLE")]
        [InlineData("tls", "COMPOSER_PROXY_FAILED")]
        [InlineData("io", "COMPOSER_PROXY_FAILED")]
        [InlineData("timeout", "COMPOSER_TIMEOUT")]
        [InlineData("direct", null)]
        public Task AProxyRunsFailure_IsWordedByItsCode_NeverByAnExceptionsMessage(
            string failure, string key) => _application.RunAsync(async () =>
        {
            Func<HttpRequestMessage, HttpResponseMessage> fail = failure switch
            {
                "407" => request => new HttpResponseMessage(HttpStatusCode.ProxyAuthenticationRequired)
                {
                    Version = HttpVersion.Version11,
                    Content = new StringContent(CanaryMessage),
                    RequestMessage = request,
                },
                "tunnel-407" => _ => throw Refused(HttpRequestError.ProxyTunnelError, HttpStatusCode.ProxyAuthenticationRequired),
                "tunnel-502" => _ => throw Refused(HttpRequestError.ProxyTunnelError, HttpStatusCode.BadGateway),
                "connection" => _ => throw Refused(HttpRequestError.ConnectionError),
                "name" => _ => throw Refused(HttpRequestError.NameResolutionError),
                "tls" or "direct" => _ => throw Refused(HttpRequestError.SecureConnectionError),
                "io" => _ => throw new IOException(CanaryMessage),
                _ => _ => throw new TaskCanceledException(CanaryMessage),
            };
            bool proxied = failure != "direct";
            using var direct = new InMemoryHandler(proxied ? null : fail);
            using var httpClient = new HttpClient(direct);
            using var client = new RequestReplayClient(httpClient);
            using var throughProxy = new InMemoryHandler(proxied ? fail : null);
            var proxies = new List<string>();
            client.OperationFactory = () => ProxyingOperation(proxies, throughProxy);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var window = RequestComposerWindow.ForNewRequest(client, Answer(new List<string>(), true), Recording(model));
            try
            {
                Find<TextBox>(window, "UrlBox").Text = Url;
                Find<CheckBox>(window, "ProxyBox").IsChecked = proxied;
                Find<TextBox>(window, "ProxyText").Text = Proxy;

                await window.SendAsync();

                Assert.Null(window.LastResult);
                ProxyConnectionModel row = Assert.Single(Replays(model).ProxyConnections);
                if (!proxied)
                {
                    Assert.StartsWith(Words("COMPOSER_ERROR", CanaryMessage), Status(window));
                    return;
                }

                Assert.StartsWith(key == "COMPOSER_TIMEOUT" ? Words(key) : ErrorText(key), Status(window));
                Assert.DoesNotContain("canary", Status(window), StringComparison.OrdinalIgnoreCase);
                Assert.Single(throughProxy.Requests);
                Assert.Equal(new[] { Proxy }, proxies);
                Assert.Equal(
                    Words("REPLAY_NOTE_PROXY") + " " +
                        Words("REPLAY_NOTE_HTTP_VERSION_NO_RESPONSE", Words("COMPOSER_HTTP_VERSION_AUTO")),
                    row.ReplayNote);
                Assert.DoesNotContain(
                    "canary", row.RequestLineAndHeaders + row.ResponseLineAndHeaders, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // Over a loopback proxy, with the senders that the composer makes for a run in production: a confirmed send reaches
        // the proxy as an absolute-form request, with its headers and body, and the proxy's response is shown. An HTTP 407
        // from the proxy is no response, is not retried, and uses the fixed proxy-refusal message.
        [Fact]
        public Task AConfirmedRun_GoesThroughALoopbackProxy_AndItsHttp407IsNeitherShownNorRetried() => _application.RunAsync(async () =>
        {
            int answers = 0;
            await using var proxy = new LoopbackProxy(_ => Interlocked.Increment(ref answers) == 1
                ? new ProxyAnswer.Respond(LoopbackResponse.Ok("through the proxy"))
                : new ProxyAnswer.Respond(new LoopbackResponse(
                    407, "Proxy Authentication Required", "Proxy-Authenticate: Basic realm=\"canary\"\r\n")));
            using var client = new RequestReplayClient();
            var questions = new List<string>();
            var window = RequestComposerWindow.ForNewRequest(client, Answer(questions, true));
            try
            {
                Find<TextBox>(window, "MethodBox").Text = "POST";
                Find<TextBox>(window, "UrlBox").Text = Url;
                Find<TextBox>(window, "HeadersBox").Text = "Authorization: Bearer canary-token";
                Find<TextBox>(window, "BodyBox").Text = "canary body";
                Find<CheckBox>(window, "ProxyBox").IsChecked = true;
                Find<TextBox>(window, "ProxyText").Text = proxy.Url;

                await window.SendAsync();

                Assert.EndsWith("\n\n" + Words("COMPOSER_CONFIRM_PROXY", proxy.Url), Assert.Single(questions));
                LoopbackRequest sent = Assert.Single(proxy.Requests);
                Assert.Equal(
                    ("POST", Url, "Bearer canary-token", "canary body"),
                    (sent.Method, sent.Target, sent.Header("Authorization"), Encoding.UTF8.GetString(sent.Body)));
                Assert.Equal("through the proxy", Encoding.UTF8.GetString(window.LastResult.Response.BodyBytes));

                await window.SendAsync();

                Assert.Equal(2, questions.Count);
                Assert.Equal(2, proxy.Requests.Count);
                Assert.Null(window.LastResult);
                Assert.Equal(ErrorText("COMPOSER_PROXY_AUTH_REQUIRED"), Status(window));
            }
            finally
            {
                window.Close();
            }
        });

        // Open request, Load and a drop each turn Upstream proxy off and clear its proxy, since neither a request file nor
        // the history keeps one; a drop still keeps Connect to another endpoint, as it is. Changing the proxy, by turning it
        // on or only by typing one, is an edit that each asks about first, a drop also with only that changed, and declining
        // keeps the request and the proxy as they were. The proxy as reset is then part of the request as loaded: replacing
        // it again asks nothing.
        [Theory]
        [InlineData("open", "on")]
        [InlineData("open", "text")]
        [InlineData("load", "on")]
        [InlineData("load", "text")]
        [InlineData("drop", "on")]
        [InlineData("drop", "text")]
        public Task ReplacingTheRequest_ResetsTheProxy_AndAsksFirstWhenItChanged(
            string replacement, string edit) => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            ReplayRecordingTarget target = Recording(model);
            Record(target, Exchange());
            var questions = new List<string>();
            bool replace = false;
            var window = RequestComposerWindow.ForNewRequest(
                confirm: message =>
                {
                    questions.Add(message);
                    return replace;
                },
                recording: target);
            window.ChooseRequestFile = () => Path.Join(AppContext.BaseDirectory, "other.xmat-request.json");
            window.ReadRequestFile = (_, _) => Task.FromResult(RequestFileReader.Read(Encoding.UTF8.GetBytes(TemplateFile)));
            var capture = new ProxyConnectionModel(12, "200", DateTime.UtcNow, TimeSpan.Zero, "http", "127.0.0.1:9", "GET",
                "/dropped", "GET /dropped HTTP/1.1\r\nHost: 127.0.0.1:9\r\n", [], "HTTP/1.1 200 OK\r\n", [], "127.0.0.1");
            string question = replacement switch
            {
                "open" => Words("COMPOSER_REPLACE_EDITED_FILE", "other.xmat-request.json"),
                "load" => Words("COMPOSER_REPLACE_EDITED_HISTORY", CaptureAppModel.ReplayContextName, 1L),
                _ => Words("COMPOSER_REPLACE_EDITED", "Source tab", 12L),
            };

            // Whether the request was replaced.
            async Task<bool> ReplaceAsync()
            {
                switch (replacement)
                {
                    case "open":
                        await window.OpenRequestAsync();
                        return Status(window) != Words("COMPOSER_OPEN_CANCELLED");
                    case "load":
                        await window.LoadFromHistoryAsync(Assert.Single(model.ReplayHistory.Snapshot()).Entry);
                        return Status(window) != Words("COMPOSER_HISTORY_LOAD_CANCELLED");
                    default:
                        using (ComposerSessionDragToken drag = ComposerSessionDrag.Begin(capture, "Source tab"))
                        {
                            return window.DropSession(drag.Data);
                        }
                }
            }

            try
            {
                var connectTo = Find<CheckBox>(window, "ConnectToBox");
                if (replacement == "drop")
                    connectTo.IsChecked = true;
                ChangeProxy(window, edit);
                var changed = ProxyState(window);
                string request = Request(window);

                Assert.False(await ReplaceAsync());

                Assert.Equal(question, Assert.Single(questions));
                Assert.Equal(changed, ProxyState(window));
                Assert.Equal(request, Request(window));

                replace = true;
                Assert.True(await ReplaceAsync());

                Assert.Equal(2, questions.Count);
                Assert.Equal((false, false, ""), ProxyState(window));
                Assert.NotEqual(request, Request(window));
                Assert.Equal(replacement == "drop", connectTo.IsChecked == true);

                Assert.True(await ReplaceAsync());
                Assert.Equal(2, questions.Count);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // Save request as never saves Upstream proxy: a file with the captured content holds the request and its HTTP
        // version, never the proxy, which stays as it was in the composer.
        [Fact]
        public Task SavingARequest_NeverSavesTheProxy() => _application.RunAsync(async () =>
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            var saved = new List<string>();
            window.ChooseSavePath = () => Path.Join(AppContext.BaseDirectory, "proxied.xmat-request.json");
            window.ChooseSaveContent = _ => new RequestSaveChoice(RequestFileProfile.Content);
            window.PublishRequestFile = (_, write, _) =>
            {
                using var stream = new MemoryStream();
                write(stream);
                saved.Add(Encoding.UTF8.GetString(stream.ToArray()));
                return PrivateFilePublication.Published;
            };
            try
            {
                Find<TextBox>(window, "UrlBox").Text = Url;
                Find<CheckBox>(window, "ProxyBox").IsChecked = true;
                Find<TextBox>(window, "ProxyText").Text = "http://canary-proxy.xmat.test:8080";

                await window.SaveRequestAsync();

                string file = Assert.Single(saved);
                Assert.Contains(Url, file);
                Assert.DoesNotContain("canary", file, StringComparison.OrdinalIgnoreCase);
                Assert.Equal((true, true, "http://canary-proxy.xmat.test:8080"), ProxyState(window));
            }
            finally
            {
                window.Close();
            }
        });

        // A confirmed plan gets the permission to send through a proxy only when its preview said it uses one, and nothing
        // without the user's confirmation.
        [Fact]
        public void ComposerOptions_AllowTheProxy_OnlyForAConfirmedPlanThatUsesIt()
        {
            using var client = new RequestReplayClient();
            Assert.True(ReplayProxy.TryParse(Proxy, out ReplayProxy proxy));
            var transport = new ReplayTransportOptions(ReplayHttpVersion.Auto, null, proxy);
            ReplayPlanPreview proxied = client.Preview(
                ReplaySource.FromDraft(new RequestReplayDraft { Url = Url }), 0, transport).Value;
            ReplayPlanPreview direct = client.Preview(ReplaySource.FromDraft(new RequestReplayDraft { Url = Url }), 0).Value;
            Assert.True(proxied.UsesProxy);
            Assert.False(direct.UsesProxy);

            ReplayOptions confirmed = RequestReplayClient.ComposerOptions(0, proxied.Transport, proxied);

            Assert.Equal((true, false), (confirmed.AllowProxy, confirmed.AllowConnectTo));
            Assert.Equal(proxied.Transport, confirmed.Transport);
            Assert.False(RequestReplayClient.ComposerOptions(0, proxied.Transport).AllowProxy);
            Assert.False(RequestReplayClient.ComposerOptions(0, direct.Transport, direct).AllowProxy);
        }

        // Why the operation refused a run is said in the composer's words when it was about the proxy: Upstream proxy with
        // Connect to another endpoint, and a proxy that was not confirmed, never in the operation's own message.
        [Fact]
        public void TheOperationsProxyRefusals_AreWordedByTheComposer()
        {
            var plan = new ReplayPlanPreview(new RequestReplayDraft { Url = Url }, null, 1, false, false);
            Assert.Equal(
                new[] { ErrorText("COMPOSER_PROXY_CONNECT_TO"), ErrorText("COMPOSER_PROXY_POLICY") },
                new[] { ReplayOperation.ProxyConnectToCode, ReplayOperation.ProxyPolicyCode }.Select(code =>
                    RequestComposerWindow.RunRefusalText(
                        new OperationDiagnostic(code, OperationDiagnosticSeverity.Error, "The operation's own words."), plan)));
        }

        // A row's note says that its send went through Upstream proxy, from what the operation recorded, with the HTTP
        // version requested and the one used, or that no response came, and never names the proxy, which the operation does
        // not record.
        [Fact]
        public void TransportNote_SaysThatASendWentThroughTheProxy()
        {
            Assert.Equal(ProxyNote("COMPOSER_HTTP_VERSION_AUTO", "HTTP/2"), ReplayRecorder.TransportNote(Sent("proxy", "auto", "2")));
            Assert.Equal(ProxyNote("COMPOSER_HTTP_VERSION_11", "HTTP/1.1"), ReplayRecorder.TransportNote(Sent("proxy", "1.1", "1.1")));
            Assert.Equal(
                Words("REPLAY_NOTE_PROXY") + " " +
                    Words("REPLAY_NOTE_HTTP_VERSION_NO_RESPONSE", Words("COMPOSER_HTTP_VERSION_2")),
                ReplayRecorder.TransportNote(Sent("proxy", "2", null)));
        }

        // An operation whose runs through a proxy record the proxy that their sender was made for and send to the handler,
        // in memory, instead of connecting to it.
        private static ReplayOperation ProxyingOperation(List<string> proxies, HttpMessageHandler handler) => new()
        {
            OwnSenderFactory = setup =>
            {
                lock (proxies)
                    proxies.Add(setup.Proxy?.ToString());
                return new ReplaySender(new HttpMessageInvoker(handler, disposeHandler: false), ownsInvoker: true, setup);
            },
        };

        private static HttpRequestException Refused(HttpRequestError error, HttpStatusCode? status = null) =>
            new(error, CanaryMessage, null, status);

        // Changes Upstream proxy as the user does: turns it on, or only types a proxy, turning it on to do so and off again.
        private static void ChangeProxy(Window window, string edit)
        {
            var proxy = Find<CheckBox>(window, "ProxyBox");
            proxy.IsChecked = true;
            if (edit == "text")
            {
                Find<TextBox>(window, "ProxyText").Text = Proxy;
                proxy.IsChecked = false;
            }
        }

        // Upstream proxy as the composer shows it: selected, its text available, and its text.
        private static (bool, bool, string) ProxyState(Window window) => (
            Find<CheckBox>(window, "ProxyBox").IsChecked == true,
            Find<TextBox>(window, "ProxyText").IsEnabled,
            Find<TextBox>(window, "ProxyText").Text);

        // The request as the editors show it.
        private static string Request(Window window) => string.Join(
            "|",
            Find<TextBox>(window, "MethodBox").Text,
            Find<TextBox>(window, "UrlBox").Text,
            Find<TextBox>(window, "HeadersBox").Text,
            Find<TextBox>(window, "BodyBox").Text);

        // Records a send in the model's Replays tab, as the composer does, which puts it in the history.
        private static void Record(ReplayRecordingTarget target, ReplayExchange exchange)
        {
            using var cancellation = new CancellationTokenSource();
            using ReplayRecording recording = target.TryBegin(cancellation, _ => ReplayProvenance.Composed);
            Assert.NotNull(recording);
            recording.Recorder.Add(
                new ReplayItemResult(1, null, null, 1, ReplayOutcome.Completed, 200, TimeSpan.Zero, 5, 0, null, null), exchange);
        }

        // A send as the Replays tab records it: a GET to the loopback port and its response.
        private static ReplayExchange Exchange() => new()
        {
            Outcome = ReplayOutcome.Completed,
            Request = new ReplayMessage(
                "GET /history HTTP/1.1", new[] { new KeyValuePair<string, string>("Host", "127.0.0.1:9") }, null, null),
            Response = new ReplayMessage("HTTP/1.1 200 OK", null, null, "reply"u8.ToArray()),
            RequestUri = new Uri("http://127.0.0.1:9/history"),
            StatusCode = 200,
            Started = new DateTime(2026, 1, 1, 9, 30, 15, DateTimeKind.Utc),
        };

        private static ReplayItemResult Sent(string transport, string version, string responseVersion) =>
            new(1, null, null, 1, ReplayOutcome.Completed, 200, TimeSpan.Zero, 5, 0, null, null,
                Transport: transport, HttpVersion: version, ResponseVersion: responseVersion);

        private static string ProxyNote(string requestedKey, string used) =>
            Words("REPLAY_NOTE_PROXY") + " " + Words("REPLAY_NOTE_HTTP_VERSION", Words(requestedKey), used);

        private static Func<string, bool> Answer(List<string> questions, bool answer) => message =>
        {
            questions.Add(message);
            return answer;
        };

        private static string Words(string key, params object[] arguments) => Localization.GetLocalizedString(key, arguments);

        private static string ErrorText(string key) => Words("COMPOSER_ERROR", Words(key));

        private static T Find<T>(Window window, string name) where T : class =>
            Assert.IsType<T>(window.FindName(name));

        private static string Status(Window window) => Find<TextBlock>(window, "StatusText").Text;

        // Records into the model's Replays tab, on this test's UI thread.
        private static ReplayRecordingTarget Recording(CaptureAppModel model)
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            return new ReplayRecordingTarget(model, action => dispatcher.Invoke(action));
        }

        private static WebServiceDeviceCaptureController Replays(CaptureAppModel model) =>
            (WebServiceDeviceCaptureController)model.GetOrAddReplayContext().CaptureController;

        private static async Task RemoveAllAsync(CaptureAppModel model)
        {
            foreach (ICaptureDeviceContext context in model.CaptureDeviceContexts.ToList())
            {
                await model.RemoveDeviceContextAsync(context);
            }
        }

        private sealed record HandledRequest(Uri Uri, string Method);

        // Answers every request in memory, as answer says, or with 200 over HTTP/1.1, and keeps what each asked; an answer
        // that throws fails the request as an HTTP stack would.
        private sealed class InMemoryHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;
            private readonly List<HandledRequest> _requests = new();

            internal InMemoryHandler(Func<HttpRequestMessage, HttpResponseMessage> answer = null)
            {
                _answer = answer ?? (request => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Version = HttpVersion.Version11,
                    Content = new ByteArrayContent("in memory"u8.ToArray()),
                    RequestMessage = request,
                });
            }

            internal IReadOnlyList<HandledRequest> Requests
            {
                get
                {
                    lock (_requests)
                    {
                        return _requests.ToArray();
                    }
                }
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                lock (_requests)
                {
                    _requests.Add(new HandledRequest(request.RequestUri, request.Method.Method));
                }

                try
                {
                    return Task.FromResult(_answer(request));
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
                {
                    return Task.FromException<HttpResponseMessage>(exception);
                }
            }
        }
    }
}
