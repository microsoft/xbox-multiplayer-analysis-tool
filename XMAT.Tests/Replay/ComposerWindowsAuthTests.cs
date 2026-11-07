// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
using PlanQuestion = XMAT.WebServiceCapture.RequestComposerWindow.PlanQuestion;

namespace XMAT.Tests.Replay
{
    // The composer's Automatically authenticate with Windows (slice K3), alone and with Client certificate (contract 2.10):
    // its control and its name; the one confirmation, which says whose credentials answer, names the origin and says that
    // the run uses HTTP/1.1 only; the permission that a run gets, as confirmed, and the sender that it makes; the refusals
    // and the failures, worded by their codes; the resets of Open request, Load and a drop; what the Replays tab, request
    // files and the cURL exports keep; and that no token or challenge data of a send reaches what the window shows or the
    // Replays tab keeps. Runs send to in-memory handlers through senders made for them, which answer no challenge, and the
    // Personal store is the recording fake of the native calls (RecordingCertificateNative), so no Windows credential,
    // certificate store, key or server of this PC is reached.
    [Collection(CaptureUiApplicationCollection.Name)]
    public class ComposerWindowsAuthTests
    {
        private const string Url = "https://xmat.test:8443/item";
        private const string Origin = "https://xmat.test:8443";
        private const string Endpoint = "127.0.0.2:9443";

        // The sentence and the line of the confirmation that the K3 rulings bind (D-K3-12, D-K3-13), verbatim.
        private const string Sentence =
            "Windows authentication: XMAT answers the origin's challenge with the Windows credentials that it runs with, " +
            "which may be a service or machine account's; Negotiate may fall back to NTLM, which a recipient can relay or " +
            "attack offline; a DNS attacker can steer the Kerberos service name through CNAMEs, while HTTPS still proves the " +
            "host.";

        private const string Http11Line = "Windows authentication uses HTTP/1.1 only for this run.";

        // A template request file, as XMAT saves one: it keeps the method and the HTTP version, and leaves out the URL, the
        // headers and the body.
        private const string TemplateFile =
            "{ \"schema\": \"xmat-request/1\", \"profile\": \"template\", \"method\": \"POST\", \"url\": null, " +
            "\"httpVersion\": \"auto\", \"headers\": null, \"body\": null, \"omitted\": [\"url\", \"headers\", \"body\"] }";

        // The time now, as the window's clock gives it here.
        private static readonly DateTimeOffset TestNow = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

        private static readonly Func<string, bool> NoQuestion =
            message => throw new Xunit.Sdk.XunitException("Nothing needs confirmation here: " + message);

        private readonly CaptureUiApplicationFixture _application;

        public ComposerWindowsAuthTests(CaptureUiApplicationFixture application)
        {
            _application = application;
        }

        static ComposerWindowsAuthTests()
        {
            Localization.LoadLanguage("en-us");
        }

        // Automatically authenticate with Windows is off by default and reached with the keyboard, also with its access key,
        // Alt+W, which no other control or menu of the window uses. It is read with the hint that says whose credentials
        // answer which server, and its content names it for automation.
        [Fact]
        public Task WindowsAuthentication_IsOffByDefault_AndNamedForAutomation() => _application.RunAsync(() =>
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            try
            {
                var box = Find<CheckBox>(window, "WindowsAuthBox");
                AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(box);
                Assert.Equal(
                    (false, true, true, "Automatically authenticate with Windows", Words("COMPOSER_WINDOWS_AUTH_HINT")),
                    (box.IsChecked == true, box.Focusable, box.IsTabStop, peer.GetName(), peer.GetHelpText()));
                Assert.Single(AccessKeys(window), key => key == 'W');
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });

        // Windows authentication needs the run's one confirmation, which says whose credentials answer, in the rulings'
        // words, names the origin, as the check of the run found it, and says that the run uses HTTP/1.1 only, after what
        // the run sends, for one request or one per number. Declined, nothing is sent or recorded. Accepted, the run has
        // exactly that origin as its permission, and sends through a sender made for Windows authentication and that
        // origin, never the composer's pooled transport, asking for exactly HTTP/1.1, while the HTTP version chosen stays
        // Automatic; each row says that Windows authentication was configured, and that HTTP/1.1 only was requested.
        [Theory]
        [InlineData(true, false)]
        [InlineData(false, false)]
        [InlineData(true, true)]
        public Task WindowsAuthentication_NeedsTheOneConfirmation_NamingTheOrigin(
            bool accept, bool numbered) => _application.RunAsync(async () =>
        {
            using var direct = new InMemoryHandler();
            using var httpClient = new HttpClient(direct);
            using var client = new RequestReplayClient(httpClient);
            using var authenticating = new InMemoryHandler();
            var senders = new List<SenderUsed>();
            client.OperationFactory = () => OwnSenderOperation(senders, authenticating);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var questions = new List<string>();
            var window = RequestComposerWindow.ForNewRequest(client, Answer(questions, accept), Recording(model));
            window.ChooseSequence = () => "1-2";
            try
            {
                string url = numbered ? Url + "/#" : Url;
                Find<TextBox>(window, "UrlBox").Text = url;
                Find<CheckBox>(window, "WindowsAuthBox").IsChecked = true;

                await window.SendAsync();

                string send = numbered
                    ? Words("COMPOSER_CONFIRM_SEND_SEQUENCE", 2, "GET", "1", "2", url)
                    : Words("COMPOSER_CONFIRM_SEND", "GET", url);
                Assert.Equal(send + "\n\n" + Words("COMPOSER_CONFIRM_WINDOWS_AUTH", Origin), Assert.Single(questions));
                Assert.Empty(direct.Requests);
                if (!accept)
                {
                    Assert.Empty(senders);
                    Assert.Empty(authenticating.Requests);
                    Assert.Empty(model.CaptureDeviceContexts);
                    return;
                }

                Assert.Equal(new[] { new SenderUsed(null, Origin, null, true) }, senders);
                string[] sent = numbered ? new[] { Url + "/1", Url + "/2" } : new[] { Url };
                Assert.Equal(sent.Select(Exact), authenticating.Requests);
                Assert.Equal(ReplayHttpVersion.Auto, SelectedVersion(window));
                string note = Words("REPLAY_NOTE_WINDOWS_AUTH") + " " + VersionNote("COMPOSER_HTTP_VERSION_11", "HTTP/1.1");
                Assert.Equal(
                    Enumerable.Repeat(note, sent.Length), Replays(model).ProxyConnections.Select(row => row.ReplayNote));
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // The question names the origin as the window shows origins, bounded, however long its host, while the permission
        // that the run gets, and so the sender that it makes, keep the exact origin. Only the paragraph of Windows
        // authentication is checked for it: the first one names the request's own URL, as every question does.
        [Fact]
        public Task ALongOrigin_IsShownBounded_WhileTheRunAnswersItWhole() => _application.RunAsync(async () =>
        {
            string origin = "https://" + string.Join(".", Enumerable.Repeat(new string('a', 60), 6)) + ".test:8443";
            using var direct = new InMemoryHandler();
            using var httpClient = new HttpClient(direct);
            using var client = new RequestReplayClient(httpClient);
            using var authenticating = new InMemoryHandler();
            var senders = new List<SenderUsed>();
            client.OperationFactory = () => OwnSenderOperation(senders, authenticating);
            var questions = new List<string>();
            var window = RequestComposerWindow.ForNewRequest(client, Answer(questions, true));
            try
            {
                Find<TextBox>(window, "UrlBox").Text = origin + "/item";
                Find<CheckBox>(window, "WindowsAuthBox").IsChecked = true;

                await window.SendAsync();

                string paragraph = Assert.Single(questions).Split("\n\n")[^1];
                Assert.Equal(Words("COMPOSER_CONFIRM_WINDOWS_AUTH", UntrustedText.Show(origin, 300)), paragraph);
                Assert.DoesNotContain(origin, paragraph, StringComparison.Ordinal);
                Assert.Equal(new[] { new SenderUsed(null, origin, null, true) }, senders);
                Assert.Single(authenticating.Requests);
                Assert.Empty(direct.Requests);
            }
            finally
            {
                window.Close();
            }
        });

        // The run answers the origin that was confirmed: turning the option off and typing another URL while XMAT asks change
        // nothing in it. The next send uses the controls as they are then: directly, and without a question.
        [Fact]
        public Task TheConfirmedOrigin_IsFrozen_AgainstEditsMadeWhileXmatAsks() => _application.RunAsync(async () =>
        {
            const string elsewhere = "https://other.xmat.test/elsewhere";
            using var direct = new InMemoryHandler();
            using var httpClient = new HttpClient(direct);
            using var client = new RequestReplayClient(httpClient);
            using var authenticating = new InMemoryHandler();
            var senders = new List<SenderUsed>();
            client.OperationFactory = () => OwnSenderOperation(senders, authenticating);
            var questions = new List<string>();
            RequestComposerWindow window = null;
            window = RequestComposerWindow.ForNewRequest(
                client,
                message =>
                {
                    questions.Add(message);
                    Find<CheckBox>(window, "WindowsAuthBox").IsChecked = false;
                    Find<TextBox>(window, "UrlBox").Text = elsewhere;
                    return true;
                });
            try
            {
                Find<TextBox>(window, "UrlBox").Text = Url;
                Find<CheckBox>(window, "WindowsAuthBox").IsChecked = true;

                await window.SendAsync();

                Assert.EndsWith(Words("COMPOSER_CONFIRM_WINDOWS_AUTH", Origin), Assert.Single(questions), StringComparison.Ordinal);
                Assert.Equal(new[] { new SenderUsed(null, Origin, null, true) }, senders);
                Assert.Equal(new[] { Exact(Url) }, authenticating.Requests);
                Assert.Empty(direct.Requests);

                await window.SendAsync();

                Assert.Single(questions);
                Assert.Single(senders);
                Assert.Equal(new Uri(elsewhere), Assert.Single(direct.Requests).Uri);
            }
            finally
            {
                window.Close();
            }
        });

        // Send's check refuses, worded by the code of the operation's check, before anything is asked, looked up or sent:
        // Windows authentication with Upstream proxy, with Connect to another endpoint, for an http:// URL, with HTTP/2 only,
        // and for a request with its own Authorization header, whatever the casing of its name. With Client certificate too,
        // the first refusal in the order that the operation checks is the one that is said: the certificate's through a
        // proxy, and Windows authentication's with another endpoint, which a certificate alone may use.
        [Theory]
        [InlineData("proxy", false, "COMPOSER_WINDOWS_AUTH_PROXY")]
        [InlineData("connect-to", false, "COMPOSER_WINDOWS_AUTH_CONNECT_TO")]
        [InlineData("http", false, "COMPOSER_WINDOWS_AUTH_HTTPS")]
        [InlineData("http2", false, "COMPOSER_WINDOWS_AUTH_HTTP2")]
        [InlineData("authorization", false, "COMPOSER_WINDOWS_AUTH_AUTHORIZATION")]
        [InlineData("proxy", true, "COMPOSER_CLIENT_CERT_PROXY")]
        [InlineData("connect-to", true, "COMPOSER_WINDOWS_AUTH_CONNECT_TO")]
        public Task WhatTheCheckRefuses_IsWordedByItsCode_BeforeAnythingIsAsked(
            string refusal, bool certificate, string key) => _application.RunAsync(async () =>
        {
            using var chosen = new TestCertificate("CN=Alpha client", TestNow.AddDays(-30), TestNow.AddDays(30));
            RecordingCertificateNative native = Native(chosen);
            using var direct = new InMemoryHandler();
            using var httpClient = new HttpClient(direct);
            using var client = new RequestReplayClient(httpClient) { CertificateNative = native };
            using var authenticating = new InMemoryHandler();
            var senders = new List<SenderUsed>();
            client.OperationFactory = () => OwnSenderOperation(senders, authenticating);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion, Recording(model));
            try
            {
                Find<TextBox>(window, "UrlBox").Text = refusal == "http" ? "http://xmat.test/item" : Url;
                Find<CheckBox>(window, "WindowsAuthBox").IsChecked = true;
                if (certificate)
                    await ChooseAsync(window, chosen);
                switch (refusal)
                {
                    case "proxy":
                        Find<CheckBox>(window, "ProxyBox").IsChecked = true;
                        Find<TextBox>(window, "ProxyText").Text = "http://proxy.xmat.test:8080";
                        break;
                    case "connect-to":
                        Find<CheckBox>(window, "ConnectToBox").IsChecked = true;
                        Find<TextBox>(window, "ConnectToText").Text = Endpoint;
                        break;
                    case "http2":
                        ChooseVersion(window, ReplayHttpVersion.Http2);
                        break;
                    case "authorization":
                        Find<TextBox>(window, "HeadersBox").Text = "authorization: Basic eA==";
                        break;
                }

                int listed = native.Calls.Count;

                await window.SendAsync();

                Assert.Equal(ErrorText(key), Status(window));
                Assert.Equal(listed, native.Calls.Count);
                Assert.Empty(senders);
                Assert.Empty(authenticating.Requests);
                Assert.Empty(direct.Requests);
                Assert.Empty(model.CaptureDeviceContexts);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // Every refusal of a run with Windows authentication is said in the composer's words, never in the operation's own
        // message: its transport, its origin, its HTTP version, its request and its permission. The texts that the K3
        // rulings bind, and the sentence and the line of the confirmation, are pinned here in English, verbatim.
        [Fact]
        public void EveryRefusalOfARunWithWindowsAuthentication_IsWordedByTheComposer()
        {
            var plan = new ReplayPlanPreview(new RequestReplayDraft { Url = Url }, null, 1, false, false);
            (string Code, string Key, string Text)[] refusals =
            {
                (ReplayOperation.WindowsAuthProxyCode, "COMPOSER_WINDOWS_AUTH_PROXY",
                    "Windows authentication cannot be used through a proxy; nothing was sent."),
                (ReplayOperation.WindowsAuthConnectToCode, "COMPOSER_WINDOWS_AUTH_CONNECT_TO",
                    "Windows authentication cannot be used with another endpoint than the URL's host; nothing was sent."),
                (ReplayOperation.WindowsAuthHttpsCode, "COMPOSER_WINDOWS_AUTH_HTTPS",
                    "Windows authentication answers only HTTPS URLs, so the server must prove that it is the URL's host; " +
                        "nothing was sent."),
                (ReplayOperation.WindowsAuthOriginInvalidCode, "COMPOSER_WINDOWS_AUTH_ORIGIN_INVALID",
                    "Windows authentication requires a supported HTTPS origin; nothing was sent."),
                (ReplayOperation.WindowsAuthOriginCountCode, "COMPOSER_WINDOWS_AUTH_ORIGIN_COUNT",
                    "Windows authentication answers one origin per run, and the requests of this run do not share one; " +
                        "nothing was sent."),
                (ReplayOperation.WindowsAuthHttp2Code, "COMPOSER_WINDOWS_AUTH_HTTP2",
                    "Windows authentication needs HTTP/1.1, so it cannot be used with HTTP/2 only; nothing was sent."),
                (ReplayOperation.WindowsAuthAuthorizationCode, "COMPOSER_WINDOWS_AUTH_AUTHORIZATION",
                    "A request that includes its own Authorization header cannot also use Windows authentication; nothing " +
                        "was sent."),
                (ReplayOperation.WindowsAuthPolicyCode, "COMPOSER_WINDOWS_AUTH_POLICY",
                    "Windows authentication answers the server's challenge with the Windows credentials that XMAT runs " +
                        "with. Answering this run's origin was not allowed; nothing was sent."),
            };

            Assert.Equal(
                refusals.Select(refusal => ErrorText(refusal.Key)),
                refusals.Select(refusal => RequestComposerWindow.RunRefusalText(
                    new OperationDiagnostic(refusal.Code, OperationDiagnosticSeverity.Error, "The operation's own words."), plan)));
            Assert.Equal(refusals.Select(refusal => refusal.Text), refusals.Select(refusal => Words(refusal.Key)));
            string paragraph = Words("COMPOSER_CONFIRM_WINDOWS_AUTH", Origin);
            Assert.StartsWith(Sentence + " ", paragraph, StringComparison.Ordinal);
            Assert.EndsWith("\n" + Http11Line, paragraph, StringComparison.Ordinal);
            Assert.Contains(Origin, paragraph, StringComparison.Ordinal);
        }

        // The question's paragraph names the origin as the check of the run made its text, off the UI thread
        // (PlanQuestion.Of), for a plan with Windows authentication alone, which describes no certificate. The window only
        // puts the texts together, and never reads the plan's origin itself.
        [Fact]
        public void TheQuestion_NamesTheOriginThatTheCheckMade()
        {
            Assert.True(ReplayCredentialOrigin.TryParse(Origin, out ReplayCredentialOrigin origin));
            var plan = new ReplayPlanPreview(
                new RequestReplayDraft { Method = "GET", Url = Url },
                null,
                1,
                false,
                false,
                new ReplayTransportOptions(ReplayHttpVersion.Http11, null, null, null, WindowsAuth: true),
                origin);
            var send = new FrozenSend(
                plan.Template, false, 0, ReplayHttpVersion.Auto, false, "", false, false, "", false, null, WindowsAuth: true);

            PlanQuestion made = PlanQuestion.Of(plan, send);

            Assert.Equal((UntrustedText.Show(Origin, 300), (string)null), (made.CredentialOrigin, made.Certificate));
            Assert.Equal(
                Words("COMPOSER_CONFIRM_SEND", made.Method, made.Url) + "\n\n" +
                    Words("COMPOSER_CONFIRM_WINDOWS_AUTH", "shown credential origin"),
                RequestComposerWindow.ConfirmationMessage(plan, 0, made with { CredentialOrigin = "shown credential origin" }));
        }

        // Open request, Load and a drop each turn Automatically authenticate with Windows off, since neither a request file
        // nor the history keeps it; a drop still keeps Connect to another endpoint, as it is. Turning it on is an edit that
        // each asks about first, a drop also with only that changed, and declining keeps the request and the option as they
        // were. Once reset, replacing the request again asks nothing.
        [Theory]
        [InlineData("open")]
        [InlineData("load")]
        [InlineData("drop")]
        public Task ReplacingTheRequest_TurnsWindowsAuthenticationOff_AndAsksFirstWhenItWasOn(
            string replacement) => _application.RunAsync(async () =>
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
                var box = Find<CheckBox>(window, "WindowsAuthBox");
                box.IsChecked = true;
                string request = Request(window);

                Assert.False(await ReplaceAsync());

                Assert.Equal(question, Assert.Single(questions));
                Assert.True(box.IsChecked == true);
                Assert.Equal(request, Request(window));

                replace = true;
                Assert.True(await ReplaceAsync());

                Assert.Equal(2, questions.Count);
                Assert.False(box.IsChecked == true);
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

        // A send with Windows authentication that gets no response says why in the composer's words, never in the message of
        // the exception that the HTTP stack gave, which the operation does not keep; with Client certificate too, the words
        // say where both go. Its row says what was configured, and that HTTP/1.1 only was requested.
        [Theory]
        [InlineData(false, "COMPOSER_WINDOWS_AUTH_FAILED")]
        [InlineData(true, "COMPOSER_CLIENT_CERT_WINDOWS_AUTH_FAILED")]
        public Task ASendThatFails_WithWindowsAuthentication_IsWordedByTheComposer(
            bool certificate, string key) => _application.RunAsync(async () =>
        {
            const string canary = "CANARY: the TLS handshake with xmat.test failed";
            using var chosen = new TestCertificate("CN=Alpha client", TestNow.AddDays(-30), TestNow.AddDays(30));
            using var direct = new InMemoryHandler();
            using var httpClient = new HttpClient(direct);
            using var client = new RequestReplayClient(httpClient) { CertificateNative = Native(chosen) };
            using var failing = new InMemoryHandler(
                _ => throw new HttpRequestException(HttpRequestError.SecureConnectionError, canary));
            var senders = new List<SenderUsed>();
            client.OperationFactory = () => OwnSenderOperation(senders, failing);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var window = RequestComposerWindow.ForNewRequest(client, Answer(new List<string>(), true), Recording(model));
            window.Clock = () => TestNow;
            try
            {
                Find<TextBox>(window, "UrlBox").Text = Url;
                Find<CheckBox>(window, "WindowsAuthBox").IsChecked = true;
                if (certificate)
                    await ChooseAsync(window, chosen);

                await window.SendAsync();

                Assert.True(
                    Status(window).StartsWith(ErrorText(key), StringComparison.Ordinal),
                    "The status line does not say the failure in the composer's words.");
                Assert.False(
                    Status(window).Contains("canary", StringComparison.OrdinalIgnoreCase),
                    "The status line shows the message of the HTTP stack's exception.");
                Assert.Null(window.LastResult);
                Assert.Single(senders);
                Assert.Equal(
                    (certificate ? Words("REPLAY_NOTE_CLIENT_CERT") + " " : "") + Words("REPLAY_NOTE_WINDOWS_AUTH") + " " +
                        Words("REPLAY_NOTE_HTTP_VERSION_NO_RESPONSE", Words("COMPOSER_HTTP_VERSION_11")),
                    Assert.Single(Replays(model).ProxyConnections).ReplayNote);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // A confirmed send with Windows authentication shows and keeps no token and no challenge data. The handler does what
        // the HTTP stack does: it puts a Negotiate token in the live request's Authorization header, then answers with a
        // Negotiate challenge that carries another token, through a sender made from the run's own setup, so the
        // operation's clearing and sanitizing apply as when it sends. Neither token reaches what the window shows or keeps
        // (the response view's raw text, headers and body, the response kept for it, and the status line) or what the
        // Replays tab keeps (the row's request and response, as the recorder made them, the values of its table and its
        // note, the history entry and the request that Load takes from it, and the row that Inspect session selects, which
        // is that very row). The recorded request has no Authorization header, and the recorded challenge is exactly
        // "Negotiate". A failure never shows a token: each check says only where it found one.
        [Fact]
        public Task ASendWithWindowsAuthentication_ShowsAndKeepsNoTokenOrChallengeData() => _application.RunAsync(async () =>
        {
            const string requestToken = "WEFNQVRDQU5BUlkx";
            const string challengeToken = "WEFNQVRDQU5BUlky";
            using var direct = new InMemoryHandler();
            using var httpClient = new HttpClient(direct);
            using var client = new RequestReplayClient(httpClient);
            using var authenticating = new InMemoryHandler(request =>
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Negotiate " + requestToken);
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Version = HttpVersion.Version11,
                    Content = new ByteArrayContent("authenticated"u8.ToArray()),
                    RequestMessage = request,
                };
                response.Headers.TryAddWithoutValidation("WWW-Authenticate", "Negotiate " + challengeToken);
                return response;
            });
            var senders = new List<SenderUsed>();
            client.OperationFactory = () => OwnSenderOperation(senders, authenticating);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var questions = new List<string>();
            var window = RequestComposerWindow.ForNewRequest(client, Answer(questions, true), Recording(model));

            // Says where a token was found, never the token or the text that holds it.
            void NoToken(string place, string text)
            {
                Assert.False((text ?? "").Contains(requestToken, StringComparison.Ordinal), "The request's token is in " + place);
                Assert.False((text ?? "").Contains(challengeToken, StringComparison.Ordinal), "The challenge's token is in " + place);
            }

            try
            {
                Find<TextBox>(window, "UrlBox").Text = Url;
                Find<CheckBox>(window, "WindowsAuthBox").IsChecked = true;
                Find<CheckBox>(window, "InspectSessionBox").IsChecked = true;

                await window.SendAsync();

                Assert.Equal(
                    Words("COMPOSER_CONFIRM_SEND", "GET", Url) + "\n\n" + Words("COMPOSER_CONFIRM_WINDOWS_AUTH", Origin),
                    Assert.Single(questions));
                Assert.Equal(new[] { new SenderUsed(null, Origin, null, true) }, senders);
                Assert.Equal(new[] { Exact(Url) }, authenticating.Requests);
                var view = Find<RequestResponseView>(window, "ResponseView");
                Assert.True(
                    view.FirstLineAndHeaders.StartsWith("HTTP/1.1 200", StringComparison.Ordinal), "No response is shown.");
                NoToken("the response view's raw text", view.FirstLineAndHeaders);
                NoToken("the response view's headers", Lines(view.HeaderDictionary));
                NoToken("the response view's body", Encoding.UTF8.GetString(view.BodyBytes));
                NoToken("the response kept for the window", window.LastResult.Response.ToString());
                NoToken("the status line", Status(window));
                Assert.True(
                    view.HeaderDictionary.TryGetValue("WWW-Authenticate", out string shown) && shown == "Negotiate",
                    "The challenge that the response view shows is not exactly Negotiate.");

                ProxyConnectionModel row = Assert.Single(Replays(model).ProxyConnections);
                NoToken("the recorded request", row.RequestLineAndHeaders + Encoding.UTF8.GetString(row.RequestBody));
                NoToken("the recorded request's headers", Lines(row.RequestHeaders));
                NoToken("the recorded response", row.ResponseLineAndHeaders + Encoding.UTF8.GetString(row.ResponseBody));
                NoToken("the recorded response's headers", Lines(row.ResponseHeaders));
                NoToken("the values of the tab's table", string.Join("\n", row.ToDataTableValues(CaptureAppModel.ReplayContextName)));
                NoToken("the row's note", row.ReplayNote);
                Assert.False(row.HasRequestHeader("Authorization"), "The recorded request has an Authorization header.");
                Assert.False(
                    row.RequestLineAndHeaders.Contains("Authorization", StringComparison.OrdinalIgnoreCase),
                    "The recorded request's head names Authorization.");
                Assert.True(
                    row.ResponseHeaders.TryGetValue("WWW-Authenticate", out string recorded) && recorded == "Negotiate",
                    "The recorded challenge is not exactly Negotiate.");
                Assert.True(
                    row.ResponseLineAndHeaders.Contains("\r\nWWW-Authenticate: Negotiate\r\n", StringComparison.Ordinal),
                    "The recorded response's head does not keep the challenge as exactly Negotiate.");
                Assert.True(
                    row.ReplayNote == Words("REPLAY_NOTE_WINDOWS_AUTH") + " " + VersionNote("COMPOSER_HTTP_VERSION_11", "HTTP/1.1"),
                    "The row's note says more than that Windows authentication was configured.");
                Assert.Same(model.GetOrAddReplayContext(), model.SelectedDeviceContext);
                Assert.Same(row, Replays(model).SelectedConnectionModel);

                ReplayHistoryPreview entry = Assert.Single(model.ReplayHistory.Snapshot());
                Assert.True(
                    entry.Entry.TryGet(out _, out ProxyConnectionModel historyRow) && ReferenceEquals(row, historyRow),
                    "The history entry is not the recorded row.");
                NoToken(
                    "the history entry",
                    string.Join("\n", entry.TabNamePreview, entry.MethodPreview, entry.UrlPreview, entry.StatusPreview));

                await window.LoadFromHistoryAsync(entry.Entry);

                Assert.True(
                    Status(window) == Words("COMPOSER_HISTORY_LOADED", CaptureAppModel.ReplayContextName, row.RequestNumber),
                    "Load did not load the recorded request.");
                NoToken("the request that Load took from the history", Request(window));
                Assert.False(
                    Find<TextBox>(window, "HeadersBox").Text.Contains("Authorization", StringComparison.OrdinalIgnoreCase),
                    "The request that Load took from the history has an Authorization header.");
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // Client certificate and Automatically authenticate with Windows together need the run's one confirmation, with both
        // paragraphs, for the one origin, the certificate's first. Accepted, the run gets both permissions, for that origin,
        // and sends through one sender made for the certificate confirmed and for Windows authentication, for that origin,
        // asking for exactly HTTP/1.1; its row says that both were configured, and that HTTP/1.1 only was requested.
        [Fact]
        public Task BothCredentials_NeedTheOneConfirmation_ForTheOneOrigin() => _application.RunAsync(async () =>
        {
            using var chosen = new TestCertificate("CN=Alpha client", TestNow.AddDays(-30), TestNow.AddDays(30));
            using var direct = new InMemoryHandler();
            using var httpClient = new HttpClient(direct);
            using var client = new RequestReplayClient(httpClient) { CertificateNative = Native(chosen) };
            using var authenticating = new InMemoryHandler();
            var senders = new List<SenderUsed>();
            client.OperationFactory = () => OwnSenderOperation(senders, authenticating);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var questions = new List<string>();
            var window = RequestComposerWindow.ForNewRequest(client, Answer(questions, true), Recording(model));
            window.Clock = () => TestNow;
            try
            {
                Find<TextBox>(window, "UrlBox").Text = Url;
                await ChooseAsync(window, chosen);
                Find<CheckBox>(window, "WindowsAuthBox").IsChecked = true;

                await window.SendAsync();

                string details = RequestComposerWindow.CertificateDetails(chosen.Listing, TestNow);
                Assert.Equal(
                    Words("COMPOSER_CONFIRM_SEND", "GET", Url) + "\n\n" + Words("COMPOSER_CONFIRM_CLIENT_CERT", details, Origin) +
                        "\n\n" + Words("COMPOSER_CONFIRM_WINDOWS_AUTH", Origin),
                    Assert.Single(questions));
                Assert.Equal(new[] { new SenderUsed(chosen.Thumbprint, Origin, null, true) }, senders);
                Assert.Equal(new[] { Exact(Url) }, authenticating.Requests);
                Assert.Empty(direct.Requests);
                Assert.Equal(
                    Words("REPLAY_NOTE_CLIENT_CERT") + " " + Words("REPLAY_NOTE_WINDOWS_AUTH") + " " +
                        VersionNote("COMPOSER_HTTP_VERSION_11", "HTTP/1.1"),
                    Assert.Single(Replays(model).ProxyConnections).ReplayNote);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // A confirmed plan gets the permission to answer with Windows authentication only when it uses it, and only for the
        // one origin that its preview found: never without the user's confirmation, nor for a plan with a client
        // certificate alone. A plan with both gets both permissions, for its one origin. Its preview asks for exactly
        // HTTP/1.1 when the version is chosen automatically.
        [Fact]
        public void ComposerOptions_AllowWindowsAuthentication_OnlyForTheConfirmedPlansOrigin()
        {
            using var client = new RequestReplayClient();
            Assert.True(ReplayClientCertificate.TryParse(new string('A', 40), out ReplayClientCertificate certificate));
            ReplayPlanPreview windows = Plan(null, true);
            ReplayPlanPreview certified = Plan(certificate, false);
            ReplayPlanPreview both = Plan(certificate, true);
            ReplayPlanPreview direct = Plan(null, false);
            Assert.Equal(
                (true, Origin, ReplayHttpVersion.Http11),
                (windows.UsesWindowsAuth, windows.CredentialOrigin.ToString(), windows.Transport.HttpVersion));

            ReplayOptions confirmed = RequestReplayClient.ComposerOptions(0, windows.Transport, windows);
            ReplayOptions bothConfirmed = RequestReplayClient.ComposerOptions(0, both.Transport, both);

            Assert.Equal(windows.CredentialOrigin, confirmed.AllowWindowsAuth);
            Assert.Null(confirmed.AllowClientCertificate);
            Assert.Equal((false, false), (confirmed.AllowProxy, confirmed.AllowConnectTo));
            Assert.Null(RequestReplayClient.ComposerOptions(0, windows.Transport).AllowWindowsAuth);
            Assert.Null(RequestReplayClient.ComposerOptions(0, certified.Transport, certified).AllowWindowsAuth);
            Assert.Null(RequestReplayClient.ComposerOptions(0, direct.Transport, direct).AllowWindowsAuth);
            Assert.Equal(
                (both.CredentialOrigin, both.CredentialOrigin),
                (bothConfirmed.AllowClientCertificate, bothConfirmed.AllowWindowsAuth));

            ReplayPlanPreview Plan(ReplayClientCertificate chosen, bool windowsAuth) => client.Preview(
                ReplaySource.FromDraft(new RequestReplayDraft { Url = Url }),
                0,
                new ReplayTransportOptions(ReplayHttpVersion.Auto, null, null, chosen, windowsAuth)).Value;
        }

        // A row's note says that Windows authentication was configured for its send, from what the operation recorded,
        // never whether a challenge was answered, which the scheme label that its request carried does not prove: the note
        // is the same with either label or none, and a label without the option makes no note. With Client certificate
        // too, both are said, then the HTTP versions.
        [Fact]
        public void TransportNote_SaysThatWindowsAuthenticationWasConfigured_AndNeverTheLabelSeen()
        {
            string windows = Words("REPLAY_NOTE_WINDOWS_AUTH");
            string exact = VersionNote("COMPOSER_HTTP_VERSION_11", "HTTP/1.1");
            Assert.Equal(
                new[] { windows + " " + exact, windows + " " + exact, windows + " " + exact },
                new[] { null, "NTLM", "Negotiate" }.Select(
                    observed => ReplayRecorder.TransportNote(Sent("1.1", false, true, observed))));
            Assert.Equal(
                windows + " " + VersionNote("COMPOSER_HTTP_VERSION_AUTO", "HTTP/1.1"),
                ReplayRecorder.TransportNote(Sent("auto", false, true)));
            Assert.Equal(
                Words("REPLAY_NOTE_CLIENT_CERT") + " " + windows + " " + exact,
                ReplayRecorder.TransportNote(Sent("1.1", true, true, "Negotiate")));
            Assert.Null(ReplayRecorder.TransportNote(Sent("auto", false, false, "NTLM")));
        }

        // Save request as never saves Automatically authenticate with Windows: a file with the captured content holds the
        // request and its HTTP version, never the option, which stays on in the composer.
        [Fact]
        public Task SavingARequest_NeverSavesWindowsAuthentication() => _application.RunAsync(async () =>
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            var saved = new List<string>();
            window.ChooseSavePath = () => Path.Join(AppContext.BaseDirectory, "windows.xmat-request.json");
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
                Find<CheckBox>(window, "WindowsAuthBox").IsChecked = true;

                await window.SaveRequestAsync();

                string file = Assert.Single(saved);
                Assert.Contains(Url, file, StringComparison.Ordinal);
                Assert.DoesNotContain("windows", file, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("auth", file, StringComparison.OrdinalIgnoreCase);
                Assert.True(Find<CheckBox>(window, "WindowsAuthBox").IsChecked == true);
            }
            finally
            {
                window.Close();
            }
        });

        // The current user's Personal store as the fake of the native calls holds it: this certificate, with the public data
        // that its test chose, and a key that signs silently.
        private static RecordingCertificateNative Native(TestCertificate certificate) =>
            new(certificate.Certificate)
            {
                Fake = true,
                SigningCertificate = certificate.Certificate,
                Metadata = _ => certificate.Metadata,
            };

        // Chooses a certificate as the user does: turns Client certificate on, opens its list and picks the certificate.
        private static async Task ChooseAsync(RequestComposerWindow window, TestCertificate certificate)
        {
            Find<CheckBox>(window, "ClientCertificateBox").IsChecked = true;
            await window.ListCertificatesAsync();
            var list = Find<ComboBox>(window, "ClientCertificateList");
            list.SelectedItem = list.Items.OfType<ComboBoxItem>()
                .Single(item => item.Tag is ReplayCertificateListing listed && listed.Thumbprint == certificate.Thumbprint);
        }

        private static void ChooseVersion(Window window, ReplayHttpVersion version)
        {
            var box = Find<ComboBox>(window, "HttpVersionBox");
            box.SelectedItem = box.Items.Cast<ComboBoxItem>().Single(item => version.Equals(item.Tag));
        }

        private static ReplayHttpVersion SelectedVersion(Window window) =>
            (ReplayHttpVersion)Assert.IsType<ComboBoxItem>(Find<ComboBox>(window, "HttpVersionBox").SelectedItem).Tag;

        // The access keys of the window's own controls and menus, each the character after the '_' of its text.
        private static IEnumerable<char> AccessKeys(DependencyObject node)
        {
            foreach (object child in LogicalTreeHelper.GetChildren(node))
            {
                if (child is not DependencyObject element)
                    continue;
                string text = element switch
                {
                    ContentControl { Content: string content } => content,
                    HeaderedItemsControl { Header: string header } => header,
                    _ => null,
                };
                int marker = text?.IndexOf('_') ?? -1;
                if (marker >= 0 && marker + 1 < text.Length)
                    yield return char.ToUpperInvariant(text[marker + 1]);
                foreach (char key in AccessKeys(element))
                    yield return key;
            }
        }

        // An operation whose runs with a sender of their own record what their sender was made for: the certificate, the
        // origin, the endpoint and Windows authentication; and send to the handler, in memory, instead of connecting
        // anywhere.
        private static ReplayOperation OwnSenderOperation(List<SenderUsed> senders, HttpMessageHandler handler) => new()
        {
            OwnSenderFactory = setup =>
            {
                lock (senders)
                {
                    senders.Add(new SenderUsed(
                        setup.ClientCertificate?.Thumbprint,
                        setup.CredentialOrigin?.ToString(),
                        setup.ConnectTo?.ToString(),
                        setup.WindowsAuth));
                }

                return new ReplaySender(new HttpMessageInvoker(handler, disposeHandler: false), ownsInvoker: true, setup);
            },
        };

        // A GET to the URL, asking for exactly HTTP/1.1, as a run with Windows authentication sends it.
        private static HandledRequest Exact(string url) =>
            new(new Uri(url), "GET", HttpVersion.Version11, HttpVersionPolicy.RequestVersionExact);

        // Headers as lines of name and value, to look in.
        private static string Lines(IEnumerable<KeyValuePair<string, string>> headers) =>
            headers == null ? "" : string.Join("\n", headers.Select(header => header.Key + ": " + header.Value));

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

        private static ReplayItemResult Sent(string version, bool certificate, bool windowsAuth, string observed = null) =>
            new(1, null, null, 1, ReplayOutcome.Completed, 200, TimeSpan.Zero, 5, 0, null, null,
                HttpVersion: version, ResponseVersion: "1.1", ClientCertificateConfigured: certificate,
                WindowsAuthConfigured: windowsAuth, AuthHeaderSchemeObserved: observed);

        private static string VersionNote(string requestedKey, string used) =>
            Words("REPLAY_NOTE_HTTP_VERSION", Words(requestedKey), used);

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

        private sealed record SenderUsed(string Thumbprint, string Origin, string ConnectTo, bool WindowsAuth);

        private sealed record HandledRequest(Uri Uri, string Method, Version Version, HttpVersionPolicy VersionPolicy);

        // A client certificate made in memory, with a key that no key store keeps, and the public data that the fake reads
        // for it: the subject and validity that its test chose, for client authentication with an RSA key.
        private sealed class TestCertificate : IDisposable
        {
            internal TestCertificate(string subject, DateTimeOffset notBefore, DateTimeOffset notAfter)
            {
                using RSA key = RSA.Create(2048);
                var request = new CertificateRequest("CN=XMAT test client", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                Certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
                Metadata = new ReplayCertificateMetadata(
                    Certificate.Thumbprint, subject, "CN=XMAT test authority", notBefore, notAfter, false, true, true,
                    ReplayKeyAlgorithm.Rsa);
            }

            internal X509Certificate2 Certificate { get; }

            internal ReplayCertificateMetadata Metadata { get; }

            internal string Thumbprint => Metadata.Thumbprint;

            internal ReplayCertificateListing Listing =>
                new(Metadata.Thumbprint, Metadata.Subject, Metadata.Issuer, Metadata.NotBefore, Metadata.NotAfter);

            public void Dispose() => Certificate.Dispose();
        }

        // Answers every request in memory, as answer says, or with 200 over HTTP/1.1, and keeps what each asked for: where
        // it went, its method and the HTTP version that it asked for, and how; an answer that throws fails the request as an
        // HTTP stack would.
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
                    _requests.Add(new HandledRequest(
                        request.RequestUri, request.Method.Method, request.Version, request.VersionPolicy));
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
