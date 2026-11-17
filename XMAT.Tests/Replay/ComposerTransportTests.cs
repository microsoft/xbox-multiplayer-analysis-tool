// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using XMAT.Models;
using XMAT.Operations.Replay;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Replay
{
    // The composer's transport options: the HTTP version and Connect to another endpoint, their consent,
    // their freezing and their notes in the Replays tab. Requests go to in-memory handlers or loopback servers only; a
    // connect-to run's sender is replaced by one over an in-memory handler, so no TLS or address is ever reached.
    [Collection(CaptureUiApplicationCollection.Name)]
    public class ComposerTransportTests
    {
        private const string Url = "https://xmat.test/item";
        private const string Origin = "https://xmat.test";
        private const string Endpoint = "127.0.0.1:8443";

        private readonly CaptureUiApplicationFixture _application;

        public ComposerTransportTests(CaptureUiApplicationFixture application)
        {
            _application = application;
        }

        static ComposerTransportTests()
        {
            Localization.LoadLanguage("en-us");
        }

        // By default the HTTP version is Automatic and Connect to another endpoint is off, with its address disabled. Copy
        // as cURL copies a request with Automatic or HTTP/1.1 only, without Connect to another endpoint, and says why
        // otherwise.
        [Fact]
        public Task TransportOptions_AreAutomaticAndDirectByDefault_AndCopyAsCurlNeedsThem() => _application.RunAsync(() =>
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            try
            {
                var version = Find<ComboBox>(window, "HttpVersionBox");
                var connectTo = Find<CheckBox>(window, "ConnectToBox");
                var endpoint = Find<TextBox>(window, "ConnectToText");
                var copy = Find<Button>(window, "CopyCurlButton");
                Assert.Equal(
                    new object[] { ReplayHttpVersion.Auto, ReplayHttpVersion.Http11, ReplayHttpVersion.Http2 },
                    version.Items.Cast<ComboBoxItem>().Select(item => item.Tag));
                Assert.Equal(
                    new[] { "COMPOSER_HTTP_VERSION_AUTO", "COMPOSER_HTTP_VERSION_11", "COMPOSER_HTTP_VERSION_2" }
                        .Select(key => Localization.GetLocalizedString(key)),
                    version.Items.Cast<ComboBoxItem>().Select(item => (string)item.Content));
                Assert.Equal(0, version.SelectedIndex);
                Assert.False(connectTo.IsChecked == true);
                Assert.False(endpoint.IsEnabled);

                Find<TextBox>(window, "UrlBox").Text = Url;
                Assert.True(copy.IsEnabled);

                version.SelectedIndex = 1;
                Assert.True(copy.IsEnabled);
                version.SelectedIndex = 2;
                Assert.False(copy.IsEnabled);
                Assert.Equal(Localization.GetLocalizedString("COMPOSER_CURL_HTTP2"), copy.ToolTip);
                version.SelectedIndex = 0;
                Assert.True(copy.IsEnabled);

                connectTo.IsChecked = true;
                Assert.True(endpoint.IsEnabled);
                Assert.False(copy.IsEnabled);
                Assert.Equal(Localization.GetLocalizedString("COMPOSER_CURL_CONNECT_TO"), copy.ToolTip);
                connectTo.IsChecked = false;
                Assert.False(endpoint.IsEnabled);
                Assert.True(copy.IsEnabled);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });

        // The HTTP version reaches the request as chosen: Automatic as before, HTTP/2 when the server offers it or a lower
        // version; HTTP/1.1 only and HTTP/2 only exactly, never another. A send with an explicit version has a note in the
        // Replays tab with the version requested and the one used; a send with the default transport has none.
        [Theory]
        [InlineData(0, "2.0", HttpVersionPolicy.RequestVersionOrLower, null, null)]
        [InlineData(1, "1.1", HttpVersionPolicy.RequestVersionExact, "COMPOSER_HTTP_VERSION_11", "HTTP/1.1")]
        [InlineData(2, "2.0", HttpVersionPolicy.RequestVersionExact, "COMPOSER_HTTP_VERSION_2", "HTTP/2")]
        public Task HttpVersion_ReachesTheRequestAsChosen_AndTheRowSaysSo(
            int choice, string version, HttpVersionPolicy policy, string requestedKey, string used) => _application.RunAsync(async () =>
        {
            using var direct = new RecordingHandler();
            using var httpClient = new HttpClient(direct);
            using var client = new RequestReplayClient(httpClient);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion, Recording(model));
            try
            {
                Find<TextBox>(window, "UrlBox").Text = Url;
                Find<ComboBox>(window, "HttpVersionBox").SelectedIndex = choice;

                await window.SendAsync();

                RecordedRequest sent = Assert.Single(direct.Requests);
                Assert.Equal((Version.Parse(version), policy), (sent.Version, sent.Policy));
                ProxyConnectionModel row = Assert.Single(Replays(model).ProxyConnections);
                Assert.Equal(requestedKey == null ? null : VersionNote(requestedKey, used), row.ReplayNote);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // Over a loopback server, HTTP/1.1 only is sent and recorded as HTTP/1.1.
        [Fact]
        public Task Http11Only_IsSentAndRecordedAsHttp11() => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok("over HTTP/1.1"));
            using var client = new RequestReplayClient();
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion, Recording(model));
            try
            {
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/one";
                Find<ComboBox>(window, "HttpVersionBox").SelectedIndex = 1;

                await window.SendAsync();

                Assert.Equal("/one", Assert.Single(server.Requests).Target);
                ProxyConnectionModel row = Assert.Single(Replays(model).ProxyConnections);
                Assert.StartsWith("GET /one HTTP/1.1\r\n", row.RequestLineAndHeaders);
                Assert.StartsWith("HTTP/1.1 200", row.ResponseLineAndHeaders);
                Assert.Equal(VersionNote("COMPOSER_HTTP_VERSION_11", "HTTP/1.1"), row.ReplayNote);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // HTTP/2 only needs an HTTPS URL: with an http:// URL nothing is sent or recorded, and the status says why.
        [Fact]
        public Task Http2Only_WithAnHttpUrl_IsRefusedBeforeAnythingIsSent() => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            using var client = new RequestReplayClient();
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion, Recording(model));
            try
            {
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/two";
                Find<ComboBox>(window, "HttpVersionBox").SelectedIndex = 2;

                await window.SendAsync();

                Assert.Equal(ErrorText("COMPOSER_HTTP_VERSION_HTTPS"), Status(window));
                Assert.Equal(0, server.Connections);
                Assert.Empty(model.CaptureDeviceContexts);
                Assert.Null(window.LastResult);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // Connect to another endpoint needs the run's one confirmation, which names the URL's origin and the endpoint, for
        // one request or one per number, and also covers a method that may change server data. Declined, nothing is sent;
        // accepted, the requests go through a sender for that endpoint, never the composer's direct transport, with the URL
        // unchanged, and their rows say how they were sent.
        [Theory]
        [InlineData(true, false, "GET", Endpoint)]
        [InlineData(false, false, "GET", Endpoint)]
        [InlineData(true, true, "GET", "[::1]:8443")]
        [InlineData(true, false, "POST", Endpoint)]
        public Task ConnectTo_NeedsTheOneConfirmation_NamingTheOriginAndTheEndpoint(
            bool accept, bool numbered, string method, string endpoint) => _application.RunAsync(async () =>
        {
            using var direct = new RecordingHandler();
            using var httpClient = new HttpClient(direct);
            using var client = new RequestReplayClient(httpClient);
            using var connected = new RecordingHandler();
            var targets = new List<string>();
            client.OperationFactory = () => ConnectingOperation(targets, connected);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var questions = new List<string>();
            var window = RequestComposerWindow.ForNewRequest(client, Answer(questions, accept), Recording(model));
            window.ChooseSequence = () => "1-3";
            try
            {
                string url = numbered ? Origin + "/n/#" : Url;
                Find<TextBox>(window, "MethodBox").Text = method;
                Find<TextBox>(window, "UrlBox").Text = url;
                Find<CheckBox>(window, "ConnectToBox").IsChecked = true;
                Find<TextBox>(window, "ConnectToText").Text = " " + endpoint + " ";

                await window.SendAsync();

                string send = numbered
                    ? Localization.GetLocalizedString("COMPOSER_CONFIRM_SEND_SEQUENCE", 3, method, "1", "3", url)
                    : Localization.GetLocalizedString(
                        method == "GET" ? "COMPOSER_CONFIRM_SEND" : "COMPOSER_CONFIRM_MESSAGE", method, url);
                Assert.Equal(
                    send + "\n\n" + Localization.GetLocalizedString("COMPOSER_CONFIRM_CONNECT_TO", endpoint, Origin),
                    Assert.Single(questions));
                Assert.Empty(direct.Requests);
                if (!accept)
                {
                    Assert.Empty(targets);
                    Assert.Empty(connected.Requests);
                    Assert.Empty(model.CaptureDeviceContexts);
                    return;
                }

                Assert.NotEmpty(targets);
                Assert.All(targets, target => Assert.Equal(endpoint, target));
                Assert.Equal(
                    numbered ? new[] { "/n/1", "/n/2", "/n/3" } : new[] { "/item" },
                    connected.Requests.Select(request => request.Uri.PathAndQuery));
                Assert.All(connected.Requests, request => Assert.Equal(("xmat.test", method), (request.Uri.Host, request.Method)));
                Assert.Equal(
                    Enumerable.Repeat(ConnectToNote("COMPOSER_HTTP_VERSION_AUTO", "HTTP/2"), numbered ? 3 : 1),
                    Replays(model).ProxyConnections.Select(row => row.ReplayNote));
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // The question names the URL's origin for Connect to another endpoint as the check found it, bounded and escaped
        // there: a long host is cut short, and a zero-width character that the host keeps is shown as \uXXXX, so the origin
        // cannot pass for another one. The run is allowed, and connects to the endpoint with the URL whole, exactly as
        // typed.
        [Theory]
        [InlineData("long")]
        [InlineData("unseen")]
        public Task TheQuestion_ShowsTheOriginBoundedAndEscaped_AndTheRunKeepsTheUrlWhole(
            string kind) => _application.RunAsync(async () =>
        {
            string host = kind == "long" ? string.Join(".", Enumerable.Repeat(new string('a', 60), 6)) + ".test" : "x\u200By.test";
            string origin = "https://" + host;
            string url = origin + "/item";
            using var direct = new RecordingHandler();
            using var httpClient = new HttpClient(direct);
            using var client = new RequestReplayClient(httpClient);
            using var connected = new RecordingHandler();
            var targets = new List<string>();
            client.OperationFactory = () => ConnectingOperation(targets, connected);
            var questions = new List<string>();
            var window = RequestComposerWindow.ForNewRequest(client, Answer(questions, true));
            try
            {
                Find<TextBox>(window, "UrlBox").Text = url;
                Find<CheckBox>(window, "ConnectToBox").IsChecked = true;
                Find<TextBox>(window, "ConnectToText").Text = Endpoint;

                await window.SendAsync();

                string question = Assert.Single(questions);
                string paragraph = question.Split("\n\n")[^1];
                Assert.Equal(
                    Localization.GetLocalizedString("COMPOSER_CONFIRM_SEND", "GET", UntrustedText.Show(url, 512)) + "\n\n" +
                        Localization.GetLocalizedString("COMPOSER_CONFIRM_CONNECT_TO", Endpoint, UntrustedText.Show(origin, 300)),
                    question);
                Assert.DoesNotContain(origin, paragraph, StringComparison.Ordinal);
                Assert.DoesNotContain("\u200B", question, StringComparison.Ordinal);
                Assert.Equal(new[] { Endpoint }, targets);
                Assert.Equal(new Uri(url).AbsoluteUri, Assert.Single(connected.Requests).Uri.AbsoluteUri);
                Assert.Empty(direct.Requests);
            }
            finally
            {
                window.Close();
            }
        });

        // What is sent is the transport that was checked and confirmed: changing the controls while XMAT asks changes
        // nothing in that send. The Replays tab's notes come from what each send recorded, never from the controls as they
        // are afterwards, and the next send uses the controls as they are then.
        [Fact]
        public Task Transport_IsFrozenWhenConfirmed_AndRowNotesComeFromWhatWasRecorded() => _application.RunAsync(async () =>
        {
            using var direct = new RecordingHandler();
            using var httpClient = new HttpClient(direct);
            using var client = new RequestReplayClient(httpClient);
            using var connected = new RecordingHandler();
            var targets = new List<string>();
            client.OperationFactory = () => ConnectingOperation(targets, connected);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            RequestComposerWindow window = null;
            int questions = 0;
            window = RequestComposerWindow.ForNewRequest(
                client,
                _ =>
                {
                    questions++;
                    Find<TextBox>(window, "ConnectToText").Text = "127.0.0.2:9443";
                    Find<CheckBox>(window, "ConnectToBox").IsChecked = false;
                    Find<ComboBox>(window, "HttpVersionBox").SelectedIndex = 2;
                    return true;
                },
                Recording(model));
            try
            {
                Find<TextBox>(window, "UrlBox").Text = Url;
                Find<CheckBox>(window, "ConnectToBox").IsChecked = true;
                Find<TextBox>(window, "ConnectToText").Text = Endpoint;

                await window.SendAsync();

                Assert.Equal(1, questions);
                Assert.Equal(new[] { Endpoint }, targets.Distinct());
                RecordedRequest sent = Assert.Single(connected.Requests);
                Assert.Equal(
                    (HttpVersion.Version20, HttpVersionPolicy.RequestVersionOrLower), (sent.Version, sent.Policy));
                Assert.Empty(direct.Requests);
                ProxyConnectionModel first = Assert.Single(Replays(model).ProxyConnections);
                string firstNote = ConnectToNote("COMPOSER_HTTP_VERSION_AUTO", "HTTP/2");
                Assert.Equal(firstNote, first.ReplayNote);

                // The controls now say HTTP/2 only, directly: the next send uses them, and needs no confirmation.
                await window.SendAsync();

                Assert.Equal(1, questions);
                RecordedRequest second = Assert.Single(direct.Requests);
                Assert.Equal((HttpVersion.Version20, HttpVersionPolicy.RequestVersionExact), (second.Version, second.Policy));
                Assert.Single(connected.Requests);
                Assert.Equal(2, Replays(model).ProxyConnections.Count);
                Assert.Equal(firstNote, first.ReplayNote);
                Assert.Equal(VersionNote("COMPOSER_HTTP_VERSION_2", "HTTP/2"), Replays(model).ProxyConnections[1].ReplayNote);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // The endpoint is an IP address and port only, and Connect to another endpoint an HTTPS-only option: anything else
        // is refused before anything is asked or sent, and the status says what is wrong. Off, the text is not read.
        [Theory]
        [InlineData("xmat.test:443", Url, "COMPOSER_CONNECT_TO_INVALID")]
        [InlineData("https://127.0.0.1:8443/", Url, "COMPOSER_CONNECT_TO_INVALID")]
        [InlineData("127.0.0.1", Url, "COMPOSER_CONNECT_TO_INVALID")]
        [InlineData("127.0.0.1:65536", Url, "COMPOSER_CONNECT_TO_INVALID")]
        [InlineData("::1:8443", Url, "COMPOSER_CONNECT_TO_INVALID")]
        [InlineData("", Url, "COMPOSER_CONNECT_TO_INVALID")]
        [InlineData("0.0.0.0:443", Url, "COMPOSER_CONNECT_TO_DENIED")]
        [InlineData("[ff02::1]:443", Url, "COMPOSER_CONNECT_TO_DENIED")]
        [InlineData(Endpoint, "http://xmat.test/item", "COMPOSER_CONNECT_TO_HTTPS")]
        public Task ConnectTo_RefusesAnythingButAnAddressAndPortForAnHttpsUrl(string endpoint, string url, string reason) => _application.RunAsync(async () =>
        {
            using var direct = new RecordingHandler();
            using var httpClient = new HttpClient(direct);
            using var client = new RequestReplayClient(httpClient);
            using var connected = new RecordingHandler();
            var targets = new List<string>();
            client.OperationFactory = () => ConnectingOperation(targets, connected);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion, Recording(model));
            try
            {
                Find<TextBox>(window, "UrlBox").Text = url;
                Find<CheckBox>(window, "ConnectToBox").IsChecked = true;
                Find<TextBox>(window, "ConnectToText").Text = endpoint;

                await window.SendAsync();

                Assert.Equal(ErrorText(reason), Status(window));
                Assert.Empty(targets);
                Assert.Empty(connected.Requests);
                Assert.Empty(direct.Requests);
                Assert.Empty(model.CaptureDeviceContexts);

                Find<CheckBox>(window, "ConnectToBox").IsChecked = false;
                Find<TextBox>(window, "UrlBox").Text = Url;
                await window.SendAsync();

                Assert.Single(direct.Requests);
                Assert.Empty(targets);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // Fiddler's control headers are not applied: a request that has one is not sent until the user deletes it.
        [Theory]
        [InlineData("Fiddler-Host: 127.0.0.1")]
        [InlineData("X-OverrideHost: 127.0.0.1")]
        [InlineData("X-IgnoreCertCNMismatch: true")]
        [InlineData("X-OverrideGateway: 127.0.0.1:8888")]
        public Task ControlHeaders_AreRefused_UntilTheUserDeletesThem(string header) => _application.RunAsync(async () =>
        {
            using var direct = new RecordingHandler();
            using var httpClient = new HttpClient(direct);
            using var client = new RequestReplayClient(httpClient);
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion);
            try
            {
                Find<TextBox>(window, "UrlBox").Text = Url;
                Find<TextBox>(window, "HeadersBox").Text = "X-Kept: yes\r\n" + header;

                await window.SendAsync();

                Assert.Equal(ErrorText("COMPOSER_CONTROL_HEADER"), Status(window));
                Assert.Empty(direct.Requests);

                Find<TextBox>(window, "HeadersBox").Text = "X-Kept: yes";
                await window.SendAsync();

                Assert.Single(direct.Requests);
            }
            finally
            {
                window.Close();
            }
        });

        // A confirmed plan gets the permissions that its preview said it needs, and no other: Connect to another endpoint
        // only for a plan that uses it, and nothing without the user's confirmation. The options carry the transport given.
        [Fact]
        public void ComposerOptions_GiveAConfirmedPlanThePermissionsThatItNeeds_AndNoOther()
        {
            using var client = new RequestReplayClient();
            Assert.True(ReplayConnectTo.TryParse(Endpoint, out ReplayConnectTo target));
            var transport = new ReplayTransportOptions(ReplayHttpVersion.Http11, target);
            ReplayPlanPreview connecting = client.Preview(
                ReplaySource.FromDraft(new RequestReplayDraft { Url = Url }), 0, transport).Value;
            ReplayPlanPreview direct = client.Preview(ReplaySource.FromDraft(new RequestReplayDraft { Url = Url }), 0).Value;
            Assert.True(connecting.UsesConnectTo);
            Assert.False(direct.UsesConnectTo);

            ReplayOptions confirmed = RequestReplayClient.ComposerOptions(3, connecting.Transport, connecting);

            Assert.Equal(
                (3, false, false, true),
                (confirmed.MaxRedirects, confirmed.AllowUnsafeMethods, confirmed.AllowOriginChange, confirmed.AllowConnectTo));
            Assert.Equal(connecting.Transport, confirmed.Transport);
            Assert.False(RequestReplayClient.ComposerOptions(0, connecting.Transport).AllowConnectTo);
            Assert.False(RequestReplayClient.ComposerOptions(0, direct.Transport, direct).AllowConnectTo);
        }

        // A row's note says how its send went with the transport options, from what the operation recorded: nothing for
        // the default transport, the version requested and the one used, or that no response came, and Connect to another
        // endpoint; after the note on bodies that the tab did not keep.
        [Fact]
        public void TransportNote_ComesFromTheRecordedSend()
        {
            Assert.Null(ReplayRecorder.TransportNote(null));
            Assert.Null(ReplayRecorder.TransportNote(Sent("direct", "auto", "2")));
            Assert.Equal(VersionNote("COMPOSER_HTTP_VERSION_11", "HTTP/1.1"), ReplayRecorder.TransportNote(Sent("direct", "1.1", "1.1")));
            Assert.Equal(VersionNote("COMPOSER_HTTP_VERSION_11", "HTTP/1.0"), ReplayRecorder.TransportNote(Sent("direct", "1.1", "1.0")));
            Assert.Equal(
                Localization.GetLocalizedString(
                    "REPLAY_NOTE_HTTP_VERSION_NO_RESPONSE", Localization.GetLocalizedString("COMPOSER_HTTP_VERSION_2")),
                ReplayRecorder.TransportNote(Sent("direct", "2", null)));
            Assert.Equal(
                ConnectToNote("COMPOSER_HTTP_VERSION_AUTO", "HTTP/2"),
                ReplayRecorder.TransportNote(Sent("connect-to", "auto", "2")));

            ProxyConnectionModel row = ReplayRecorder.ToRow(
                1, ReplayProvenance.Composed, Answered(), keepResponseBody: false, sent: Sent("connect-to", "1.1", "1.1"));
            Assert.Equal(
                Localization.GetLocalizedString("REPLAY_NOTE_RESPONSE_BODY", 5L, 256L) + "\n" +
                    ConnectToNote("COMPOSER_HTTP_VERSION_11", "HTTP/1.1"),
                row.ReplayNote);
            Assert.Null(ReplayRecorder.ToRow(2, ReplayProvenance.Composed, Answered(), sent: Sent("direct", "auto", "1.1")).ReplayNote);
        }

        // A send that names no requested version asked for Automatic; a requested version that replay does not name is
        // shown as Automatic, and a direct send with one still has a note.
        [Fact]
        public void TransportNote_ShowsAMissingOrUnnamedRequestedVersionAsAutomatic()
        {
            Assert.Null(ReplayRecorder.TransportNote(Sent("direct", null, "2")));
            Assert.Equal(
                ConnectToNote("COMPOSER_HTTP_VERSION_AUTO", "HTTP/1.1"), ReplayRecorder.TransportNote(Sent("connect-to", null, "1.1")));
            Assert.Equal(VersionNote("COMPOSER_HTTP_VERSION_AUTO", "HTTP/2"), ReplayRecorder.TransportNote(Sent("direct", "3", "2")));
            Assert.Equal(
                ConnectToNote("COMPOSER_HTTP_VERSION_AUTO", "HTTP/2"), ReplayRecorder.TransportNote(Sent("connect-to", "AUTO", "2")));
        }

        private static readonly Func<string, bool> NoQuestion =
            message => throw new Xunit.Sdk.XunitException("Nothing needs confirmation here: " + message);

        // Records each question and gives the answer.
        private static Func<string, bool> Answer(List<string> questions, bool answer) => message =>
        {
            questions.Add(message);
            return answer;
        };

        // An operation whose connect-to runs record their endpoint and send to the handler, in memory, instead of
        // connecting anywhere.
        private static ReplayOperation ConnectingOperation(List<string> targets, RecordingHandler handler) => new()
        {
            OwnSenderFactory = setup =>
            {
                targets.Add(setup.ConnectTo.ToString());
                return new ReplaySender(new HttpMessageInvoker(handler, disposeHandler: false), ownsInvoker: true, setup);
            },
        };

        private static string VersionNote(string requestedKey, string used) =>
            Localization.GetLocalizedString(
                "REPLAY_NOTE_HTTP_VERSION", Localization.GetLocalizedString(requestedKey), used);

        private static string ConnectToNote(string requestedKey, string used) =>
            Localization.GetLocalizedString("REPLAY_NOTE_CONNECT_TO") + " " + VersionNote(requestedKey, used);

        private static string ErrorText(string key) =>
            Localization.GetLocalizedString("COMPOSER_ERROR", Localization.GetLocalizedString(key));

        private static ReplayItemResult Sent(string transport, string version, string responseVersion) =>
            new(1, null, null, 1, ReplayOutcome.Completed, 200, TimeSpan.Zero, 5, 0, null, null,
                Transport: transport, HttpVersion: version, ResponseVersion: responseVersion);

        private static ReplayExchange Answered() => new()
        {
            Outcome = ReplayOutcome.Completed,
            Request = new ReplayMessage(
                "GET / HTTP/1.1", new[] { new KeyValuePair<string, string>("Host", "xmat.test") }, null, null),
            Response = new ReplayMessage("HTTP/1.1 200 OK", null, null, "reply"u8.ToArray()),
            RequestUri = new Uri(Url),
            StatusCode = 200,
            Started = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

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

        private sealed record RecordedRequest(Uri Uri, string Method, Version Version, HttpVersionPolicy Policy);

        // Answers every request in memory with the version that it asked for, and keeps what each asked.
        private sealed class RecordingHandler : HttpMessageHandler
        {
            private readonly List<RecordedRequest> _requests = new();

            internal IReadOnlyList<RecordedRequest> Requests
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
                    _requests.Add(new RecordedRequest(
                        request.RequestUri, request.Method.Method, request.Version, request.VersionPolicy));
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Version = request.Version,
                    Content = new ByteArrayContent("in memory"u8.ToArray()),
                    RequestMessage = request,
                });
            }
        }
    }
}
