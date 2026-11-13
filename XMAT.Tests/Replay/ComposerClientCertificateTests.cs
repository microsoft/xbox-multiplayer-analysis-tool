// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
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

namespace XMAT.Tests.Replay
{
    // The composer's Client certificate: its controls and their names; its list, read only when it opens, off
    // the UI thread and from public data alone, which marks expired and not-yet-valid certificates; the one confirmation,
    // which names the origin and the certificate; the certificate and the permission that a run uses, as confirmed; the
    // refusals, worded by their codes; the resets of Open request, Load and a drop; and what the Replays tab and request
    // files keep. Certificates are made in memory with keys that no key store keeps, the Personal store is the recording
    // fake of the native calls (RecordingCertificateNative), and runs send to in-memory handlers through senders made for
    // them, so no certificate store, key or server of this PC is reached.
    [Collection(CaptureUiApplicationCollection.Name)]
    public class ComposerClientCertificateTests
    {
        private const string Url = "https://xmat.test:8443/item";
        private const string Origin = "https://xmat.test:8443";
        private const string Endpoint = "127.0.0.2:9443";

        // A template request file, as XMAT saves one: it keeps the method and the HTTP version, and leaves out the URL, the
        // headers and the body.
        private const string TemplateFile =
            "{ \"schema\": \"xmat-request/1\", \"profile\": \"template\", \"method\": \"POST\", \"url\": null, " +
            "\"httpVersion\": \"auto\", \"headers\": null, \"body\": null, \"omitted\": [\"url\", \"headers\", \"body\"] }";

        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

        // The time now, as the window's clock gives it here.
        private static readonly DateTimeOffset TestNow = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

        // The native calls that reading the list may make, as RecordingCertificateNative names them: none is about a key.
        private static readonly string[] ListingCalls = { "open", "list", "metadata", "has-provider-info", "dispose", "close" };

        private static readonly Func<string, bool> NoQuestion =
            message => throw new Xunit.Sdk.XunitException("Nothing needs confirmation here: " + message);

        private readonly CaptureUiApplicationFixture _application;

        public ComposerClientCertificateTests(CaptureUiApplicationFixture application)
        {
            _application = application;
        }

        static ComposerClientCertificateTests()
        {
            Localization.LoadLanguage("en-us");
        }

        // Client certificate is off by default, and its list is available only while it is selected. Both are read with the
        // hint that says where the certificate goes, and each has its name for automation: the check box its content's, the
        // list one of its own, since the check box beside it labels nothing. Showing and selecting them reads nothing.
        [Fact]
        public Task ClientCertificate_IsOffByDefault_AndItsListAvailableOnlyWhileSelected() => _application.RunAsync(() =>
        {
            var native = new RecordingCertificateNative();
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            window.Client.CertificateNative = native;
            try
            {
                var box = Find<CheckBox>(window, "ClientCertificateBox");
                var list = Find<ComboBox>(window, "ClientCertificateList");
                string hint = Words("COMPOSER_CLIENT_CERT_HINT");
                Assert.Equal((false, false, 0), (box.IsChecked == true, list.IsEnabled, list.Items.Count));
                AutomationPeer boxPeer = UIElementAutomationPeer.CreatePeerForElement(box);
                AutomationPeer listPeer = UIElementAutomationPeer.CreatePeerForElement(list);
                Assert.Equal(
                    new[] { "Client certificate:", hint, "Client certificate to offer", hint },
                    new[] { boxPeer.GetName(), boxPeer.GetHelpText(), listPeer.GetName(), listPeer.GetHelpText() });

                box.IsChecked = true;
                Assert.True(list.IsEnabled);
                Assert.Empty(list.Items);
                box.IsChecked = false;
                Assert.False(list.IsEnabled);
                Assert.Empty(native.Calls);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });

        // The list is read when it opens, never before: off the UI thread, from the current user's Personal store opened
        // read-only and only if it exists, with each certificate's public data and whether it says where its key is, never a
        // key; every certificate read is released and the store closed. It then shows each certificate that can be offered,
        // in order, with all of its details, its thumbprint included, in its tooltip. None is chosen for the user, and the
        // one that the user chose stays chosen when the list is read again.
        [Fact]
        public Task TheList_IsReadWhenItOpens_OffTheUiThread_AndNeverTouchesAKey() => _application.RunAsync(async () =>
        {
            using var first = new TestCertificate("CN=Alpha client", TestNow.AddDays(-30), TestNow.AddDays(30));
            using var second = new TestCertificate("CN=Beta client", TestNow.AddDays(-30), TestNow.AddDays(60));
            RecordingCertificateNative native = Native(second, first);
            var threads = new List<int>();
            foreach (string call in ListingCalls)
            {
                native.OnCall[call] = () =>
                {
                    lock (threads)
                        threads.Add(Environment.CurrentManagedThreadId);
                };
            }

            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            window.Client.CertificateNative = native;
            window.Clock = () => TestNow;
            int uiThread = Environment.CurrentManagedThreadId;
            try
            {
                await ShowOffscreenAsync(window);
                var list = Find<ComboBox>(window, "ClientCertificateList");
                Find<CheckBox>(window, "ClientCertificateBox").IsChecked = true;
                Assert.Empty(list.Items);
                Assert.Empty(native.Calls);

                list.IsDropDownOpen = true;
                await WaitUntilAsync(() => Certificates(list).Length == 2);
                list.IsDropDownOpen = false;

                string[] calls = native.Calls.ToArray();
                Assert.Equal("open:" + (int)(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly), calls[0]);
                Assert.Equal("close", calls[^1]);
                Assert.All(calls, call => Assert.Contains(call.Split(':')[0], ListingCalls));
                Assert.Equal(2, calls.Count(call => call.StartsWith("metadata:", StringComparison.Ordinal)));
                Assert.Equal(2, calls.Count(call => call.StartsWith("dispose:", StringComparison.Ordinal)));
                lock (threads)
                {
                    Assert.Equal(calls.Length, threads.Count);
                    Assert.DoesNotContain(uiThread, threads);
                }

                ComboBoxItem[] items = Certificates(list);
                Assert.Equal(
                    new[] { Item(first.Listing, "COMPOSER_CLIENT_CERT_ITEM"), Item(second.Listing, "COMPOSER_CLIENT_CERT_ITEM") },
                    items.Select(item => (string)item.Content));
                Assert.Equal(
                    new[]
                    {
                        RequestComposerWindow.CertificateDetails(first.Listing, TestNow),
                        RequestComposerWindow.CertificateDetails(second.Listing, TestNow),
                    },
                    items.Select(item => (string)item.ToolTip));
                Assert.All(items, item => Assert.Contains(Listed(item).Thumbprint, (string)item.ToolTip, StringComparison.Ordinal));
                Assert.Null(list.SelectedItem);

                list.SelectedItem = items[1];
                await window.ListCertificatesAsync();

                ComboBoxItem kept = Assert.IsType<ComboBoxItem>(list.SelectedItem);
                Assert.NotSame(items[1], kept);
                Assert.Equal(second.Thumbprint, Listed(kept).Thumbprint);
            }
            finally
            {
                window.Close();
            }
        });

        // Each certificate is shown as valid until it expires, as expired once it has, and as not valid yet before it is, at
        // the time now and each at its very limit, with a note in its tooltip that the server may refuse it; its subject and
        // issuer are bounded and escaped, so a character that could reorder the text around it never reaches the list. Until
        // the first reading ends, the list says that it reads, and a list without a certificate that can be offered says so;
        // neither can be chosen.
        [Fact]
        public Task TheList_MarksExpiredAndNotYetValidCertificates_AndShowsTheirNamesEscaped() => _application.RunAsync(async () =>
        {
            using var valid = new TestCertificate("CN=Alpha client", TestNow.AddDays(-1), TestNow);
            using var expired = new TestCertificate("CN=Beta client", TestNow.AddDays(-30), TestNow.AddTicks(-1));
            using var early = new TestCertificate("CN=Gamma \u202Eclient", TestNow.AddTicks(1), TestNow.AddDays(30));
            using var started = new TestCertificate("CN=Delta " + new string('d', 300), TestNow, TestNow.AddDays(1));
            using var release = new ManualResetEventSlim();
            RecordingCertificateNative native = Native(valid, expired, early, started);
            native.OnCall["list"] = () => release.Wait(Patience);
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            window.Client.CertificateNative = native;
            window.Clock = () => TestNow;
            try
            {
                var list = Find<ComboBox>(window, "ClientCertificateList");
                Find<CheckBox>(window, "ClientCertificateBox").IsChecked = true;
                Task reading = window.ListCertificatesAsync();
                ComboBoxItem loading = Assert.IsType<ComboBoxItem>(Assert.Single(list.Items));
                Assert.Equal((Words("COMPOSER_CLIENT_CERT_LOADING"), false), ((string)loading.Content, loading.IsEnabled));
                release.Set();
                await reading.WaitAsync(Patience);

                ComboBoxItem[] items = Certificates(list);
                Assert.Equal(
                    new[]
                    {
                        Item(valid.Listing, "COMPOSER_CLIENT_CERT_ITEM"),
                        Item(expired.Listing, "COMPOSER_CLIENT_CERT_ITEM_EXPIRED"),
                        Item(started.Listing, "COMPOSER_CLIENT_CERT_ITEM"),
                        Item(early.Listing, "COMPOSER_CLIENT_CERT_ITEM_NOT_YET_VALID"),
                    },
                    items.Select(item => (string)item.Content));
                string[] details = items.Select(item => (string)item.ToolTip).ToArray();
                bool[] EndWith(string key) =>
                    details.Select(text => text.EndsWith("\n" + Words(key), StringComparison.Ordinal)).ToArray();
                Assert.Equal(new[] { false, true, false, false }, EndWith("COMPOSER_CLIENT_CERT_EXPIRED_NOTE"));
                Assert.Equal(new[] { false, false, false, true }, EndWith("COMPOSER_CLIENT_CERT_NOT_YET_VALID_NOTE"));
                string shown = string.Join("\n", items.Select(item => (string)item.Content).Concat(details));
                Assert.DoesNotContain("\u202E", shown, StringComparison.Ordinal);
                Assert.DoesNotContain(new string('d', 121), shown, StringComparison.Ordinal);

                window.Client.CertificateNative = new RecordingCertificateNative();
                await window.ListCertificatesAsync();

                ComboBoxItem none = Assert.IsType<ComboBoxItem>(Assert.Single(list.Items));
                Assert.Equal((Words("COMPOSER_CLIENT_CERT_EMPTY"), false), ((string)none.Content, none.IsEnabled));
            }
            finally
            {
                release.Set();
                window.Close();
            }
        });

        // A reading of the list that is under way is not started again when the list opens again, and closing the window
        // stops it: no certificate's public data is read once it was stopped, the store is still closed, and the list never
        // shows what it found.
        [Fact]
        public Task AReadingUnderWay_IsNotStartedAgain_AndClosingTheWindowStopsIt() => _application.RunAsync(async () =>
        {
            using var certificate = new TestCertificate("CN=Alpha client", TestNow.AddDays(-1), TestNow.AddDays(1));
            using var listing = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            RecordingCertificateNative native = Native(certificate);
            native.OnCall["list"] = () =>
            {
                listing.Set();
                release.Wait(Patience);
            };
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            window.Client.CertificateNative = native;
            bool closed = false;
            try
            {
                var list = Find<ComboBox>(window, "ClientCertificateList");
                Find<CheckBox>(window, "ClientCertificateBox").IsChecked = true;
                Task reading = window.ListCertificatesAsync();
                Assert.True(listing.Wait(Patience));

                await window.ListCertificatesAsync();
                Assert.Single(native.Calls, call => call.StartsWith("open:", StringComparison.Ordinal));

                window.Close();
                closed = true;
                release.Set();
                await reading.WaitAsync(Patience);

                Assert.DoesNotContain(native.Calls, call => call.StartsWith("metadata:", StringComparison.Ordinal));
                Assert.Equal("close", native.Calls[^1]);
                ComboBoxItem shown = Assert.IsType<ComboBoxItem>(Assert.Single(list.Items));
                Assert.Equal(Words("COMPOSER_CLIENT_CERT_LOADING"), (string)shown.Content);
            }
            finally
            {
                release.Set();
                if (!closed)
                    window.Close();
            }
        });

        // A client certificate needs the run's one confirmation, which names the origin, as the check of the run found it,
        // and the certificate chosen, by its subject, issuer, validity and thumbprint, in fixed wording, after what
        // else the run needs: a method that may change server data, and another endpoint. Declined, nothing is looked up or
        // sent. Accepted, the run looks up exactly the certificate confirmed, with exactly that origin as its permission, and
        // sends through a sender made for that certificate, origin and endpoint, never the composer's pooled transport; its
        // row says how it was sent and that a certificate was configured.
        [Theory]
        [InlineData(true, "GET", false)]
        [InlineData(false, "GET", false)]
        [InlineData(true, "POST", true)]
        public Task ClientCertificate_NeedsTheOneConfirmation_NamingTheOriginAndTheCertificate(
            bool accept, string method, bool connectTo) => _application.RunAsync(async () =>
        {
            using var chosen = new TestCertificate("CN=Alpha client", TestNow.AddDays(-30), TestNow.AddDays(30));
            RecordingCertificateNative native = Native(chosen);
            using var direct = new InMemoryHandler();
            using var httpClient = new HttpClient(direct);
            using var client = new RequestReplayClient(httpClient) { CertificateNative = native };
            using var certified = new InMemoryHandler();
            var senders = new List<SenderUsed>();
            client.OperationFactory = () => CertifyingOperation(senders, certified);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var questions = new List<string>();
            var window = RequestComposerWindow.ForNewRequest(client, Answer(questions, accept), Recording(model));
            window.Clock = () => TestNow;
            try
            {
                Find<TextBox>(window, "MethodBox").Text = method;
                Find<TextBox>(window, "UrlBox").Text = Url;
                Find<CheckBox>(window, "ConnectToBox").IsChecked = connectTo;
                Find<TextBox>(window, "ConnectToText").Text = Endpoint;
                await ChooseAsync(window, chosen);
                int listed = native.Calls.Count;

                await window.SendAsync();

                string send = Words(method == "GET" ? "COMPOSER_CONFIRM_SEND" : "COMPOSER_CONFIRM_MESSAGE", method, Url);
                string endpoint = connectTo ? "\n\n" + Words("COMPOSER_CONFIRM_CONNECT_TO", Endpoint, Origin) : "";
                string certificate = Words(
                    "COMPOSER_CONFIRM_CLIENT_CERT", RequestComposerWindow.CertificateDetails(chosen.Listing, TestNow), Origin);
                Assert.Equal(send + endpoint + "\n\n" + certificate, Assert.Single(questions));
                Assert.Empty(direct.Requests);
                if (!accept)
                {
                    Assert.Equal(listed, native.Calls.Count);
                    Assert.Empty(senders);
                    Assert.Empty(certified.Requests);
                    Assert.Empty(model.CaptureDeviceContexts);
                    return;
                }

                Assert.Equal(new[] { new SenderUsed(chosen.Thumbprint, Origin, connectTo ? Endpoint : null) }, senders);
                HandledRequest sent = Assert.Single(certified.Requests);
                Assert.Equal(("/item", method), (sent.Uri.PathAndQuery, sent.Method));
                Assert.Equal(
                    (connectTo ? Words("REPLAY_NOTE_CONNECT_TO") + " " : "") + Words("REPLAY_NOTE_CLIENT_CERT") + " " +
                        VersionNote("COMPOSER_HTTP_VERSION_AUTO", "HTTP/1.1"),
                    Assert.Single(Replays(model).ProxyConnections).ReplayNote);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // The question names the origin as the window shows origins, bounded and escaped, however long its host, while the
        // permission that the run gets, and so the sender that it makes, keep the exact origin. Only the certificate's
        // paragraph is checked for it: the first one names the request's own URL, as every question does.
        [Fact]
        public Task ALongOrigin_IsShownBounded_WhileTheRunGetsItWhole() => _application.RunAsync(async () =>
        {
            string origin = "https://" + string.Join(".", Enumerable.Repeat(new string('a', 60), 6)) + ".test:8443";
            using var chosen = new TestCertificate("CN=Alpha client", TestNow.AddDays(-30), TestNow.AddDays(30));
            using var direct = new InMemoryHandler();
            using var httpClient = new HttpClient(direct);
            using var client = new RequestReplayClient(httpClient) { CertificateNative = Native(chosen) };
            using var certified = new InMemoryHandler();
            var senders = new List<SenderUsed>();
            client.OperationFactory = () => CertifyingOperation(senders, certified);
            var questions = new List<string>();
            var window = RequestComposerWindow.ForNewRequest(client, Answer(questions, true));
            window.Clock = () => TestNow;
            try
            {
                Find<TextBox>(window, "UrlBox").Text = origin + "/item";
                await ChooseAsync(window, chosen);

                await window.SendAsync();

                string paragraph = Assert.Single(questions).Split("\n\n")[^1];
                string details = RequestComposerWindow.CertificateDetails(chosen.Listing, TestNow);
                Assert.Equal(Words("COMPOSER_CONFIRM_CLIENT_CERT", details, UntrustedText.Show(origin, 300)), paragraph);
                Assert.DoesNotContain(origin, paragraph, StringComparison.Ordinal);
                Assert.Equal(new[] { new SenderUsed(chosen.Thumbprint, origin, null) }, senders);
                Assert.Single(certified.Requests);
                Assert.Empty(direct.Requests);
            }
            finally
            {
                window.Close();
            }
        });

        // The run uses the certificate and the origin that were confirmed: choosing another certificate and turning Client
        // certificate off while XMAT asks change nothing in it. The next send uses the controls as they are then: directly,
        // and without a question.
        [Fact]
        public Task TheConfirmedCertificate_IsFrozen_AgainstEditsMadeWhileXmatAsks() => _application.RunAsync(async () =>
        {
            using var confirmed = new TestCertificate("CN=Alpha client", TestNow.AddDays(-30), TestNow.AddDays(30));
            using var other = new TestCertificate("CN=Beta client", TestNow.AddDays(-30), TestNow.AddDays(30));
            using var direct = new InMemoryHandler();
            using var httpClient = new HttpClient(direct);
            using var client = new RequestReplayClient(httpClient) { CertificateNative = Native(confirmed, other) };
            using var certified = new InMemoryHandler();
            var senders = new List<SenderUsed>();
            client.OperationFactory = () => CertifyingOperation(senders, certified);
            var questions = new List<string>();
            RequestComposerWindow window = null;
            window = RequestComposerWindow.ForNewRequest(
                client,
                message =>
                {
                    questions.Add(message);
                    var list = Find<ComboBox>(window, "ClientCertificateList");
                    list.SelectedItem = Certificates(list).Single(item => Listed(item).Thumbprint == other.Thumbprint);
                    Find<CheckBox>(window, "ClientCertificateBox").IsChecked = false;
                    return true;
                });
            window.Clock = () => TestNow;
            try
            {
                Find<TextBox>(window, "UrlBox").Text = Url;
                await ChooseAsync(window, confirmed);

                await window.SendAsync();

                Assert.Contains(
                    RequestComposerWindow.CertificateDetails(confirmed.Listing, TestNow),
                    Assert.Single(questions),
                    StringComparison.Ordinal);
                Assert.Equal(new[] { new SenderUsed(confirmed.Thumbprint, Origin, null) }, senders);
                Assert.Single(certified.Requests);
                Assert.Empty(direct.Requests);

                await window.SendAsync();

                Assert.Single(questions);
                Assert.Single(senders);
                Assert.Single(direct.Requests);
            }
            finally
            {
                window.Close();
            }
        });

        // Send's first check refuses, before anything is asked, looked up or sent: Client certificate without a certificate
        // chosen, and, worded by their codes, an http:// URL, to which a certificate is never offered, and Upstream proxy,
        // through which it is never sent.
        [Theory]
        [InlineData("none", Url, "COMPOSER_CLIENT_CERT_NONE")]
        [InlineData("http", "http://xmat.test/item", "COMPOSER_CLIENT_CERT_HTTPS")]
        [InlineData("proxy", Url, "COMPOSER_CLIENT_CERT_PROXY")]
        public Task WhatTheFirstCheckRefuses_IsRefusedBeforeAnythingIsAskedOrLookedUp(
            string refusal, string url, string key) => _application.RunAsync(async () =>
        {
            using var chosen = new TestCertificate("CN=Alpha client", TestNow.AddDays(-30), TestNow.AddDays(30));
            RecordingCertificateNative native = Native(chosen);
            using var direct = new InMemoryHandler();
            using var httpClient = new HttpClient(direct);
            using var client = new RequestReplayClient(httpClient) { CertificateNative = native };
            using var certified = new InMemoryHandler();
            var senders = new List<SenderUsed>();
            client.OperationFactory = () => CertifyingOperation(senders, certified);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion, Recording(model));
            try
            {
                Find<TextBox>(window, "UrlBox").Text = url;
                if (refusal == "none")
                    Find<CheckBox>(window, "ClientCertificateBox").IsChecked = true;
                else
                    await ChooseAsync(window, chosen);
                if (refusal == "proxy")
                {
                    Find<CheckBox>(window, "ProxyBox").IsChecked = true;
                    Find<TextBox>(window, "ProxyText").Text = "http://proxy.xmat.test:8080";
                }

                int listed = native.Calls.Count;

                await window.SendAsync();

                Assert.Equal(ErrorText(key), Status(window));
                Assert.Equal(listed, native.Calls.Count);
                Assert.Empty(senders);
                Assert.Empty(certified.Requests);
                Assert.Empty(direct.Requests);
                Assert.Empty(model.CaptureDeviceContexts);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // What the confirmed run finds when it looks up its certificate and checks its key is said in the composer's words,
        // never the operation's, and nothing is sent or recorded: a certificate no longer in the store, a store that cannot
        // be read, and a key that cannot be used silently.
        [Theory]
        [InlineData("not-found", "COMPOSER_CLIENT_CERT_NOT_FOUND")]
        [InlineData("store", "COMPOSER_CLIENT_CERT_STORE_UNAVAILABLE")]
        [InlineData("key", "COMPOSER_CLIENT_CERT_KEY_UNAVAILABLE")]
        public Task WhatTheRunsLookUpRefuses_IsWordedByItsCode_AndNothingIsSent(
            string refusal, string key) => _application.RunAsync(async () =>
        {
            using var chosen = new TestCertificate("CN=Alpha client", TestNow.AddDays(-30), TestNow.AddDays(30));
            RecordingCertificateNative native = Native(chosen);
            using var direct = new InMemoryHandler();
            using var httpClient = new HttpClient(direct);
            using var client = new RequestReplayClient(httpClient) { CertificateNative = native };
            using var certified = new InMemoryHandler();
            var senders = new List<SenderUsed>();
            client.OperationFactory = () => CertifyingOperation(senders, certified);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var window = RequestComposerWindow.ForNewRequest(client, Answer(new List<string>(), true), Recording(model));
            try
            {
                Find<TextBox>(window, "UrlBox").Text = Url;
                await ChooseAsync(window, chosen);
                switch (refusal)
                {
                    case "not-found":
                        client.CertificateNative = new RecordingCertificateNative();
                        break;
                    case "store":
                        native.Failing.Add("open");
                        break;
                    default:
                        native.Failing.Add("acquire");
                        break;
                }

                await window.SendAsync();

                Assert.Equal(ErrorText(key), Status(window));
                Assert.Empty(senders);
                Assert.Empty(certified.Requests);
                Assert.Empty(direct.Requests);
                Assert.Empty(Replays(model).ProxyConnections);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // A send with a client certificate that gets no response says why in the composer's words, never in the message of
        // the exception that the HTTP stack gave; its row says that a certificate was configured.
        [Fact]
        public Task ASendThatFails_WithAClientCertificate_IsWordedByTheComposer() => _application.RunAsync(async () =>
        {
            const string canary = "CANARY: the TLS handshake with xmat.test failed";
            using var chosen = new TestCertificate("CN=Alpha client", TestNow.AddDays(-30), TestNow.AddDays(30));
            using var direct = new InMemoryHandler();
            using var httpClient = new HttpClient(direct);
            using var client = new RequestReplayClient(httpClient) { CertificateNative = Native(chosen) };
            using var certified = new InMemoryHandler(
                _ => throw new HttpRequestException(HttpRequestError.SecureConnectionError, canary));
            var senders = new List<SenderUsed>();
            client.OperationFactory = () => CertifyingOperation(senders, certified);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var window = RequestComposerWindow.ForNewRequest(client, Answer(new List<string>(), true), Recording(model));
            try
            {
                Find<TextBox>(window, "UrlBox").Text = Url;
                await ChooseAsync(window, chosen);

                await window.SendAsync();

                Assert.StartsWith(ErrorText("COMPOSER_CLIENT_CERT_FAILED"), Status(window));
                Assert.DoesNotContain("canary", Status(window), StringComparison.OrdinalIgnoreCase);
                Assert.Null(window.LastResult);
                Assert.Single(senders);
                Assert.Equal(
                    Words("REPLAY_NOTE_CLIENT_CERT") + " " +
                        Words("REPLAY_NOTE_HTTP_VERSION_NO_RESPONSE", Words("COMPOSER_HTTP_VERSION_AUTO")),
                    Assert.Single(Replays(model).ProxyConnections).ReplayNote);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // Every refusal of a run with a client certificate is said in the composer's words, never in the operation's own
        // message: its transport and origin, its permission, and its certificate as the store and the key check found it.
        // The client-certificate refusal messages and the confirmation's first sentence are pinned here in
        // English, verbatim.
        [Fact]
        public void EveryRefusalOfARunWithACertificate_IsWordedByTheComposer()
        {
            var plan = new ReplayPlanPreview(new RequestReplayDraft { Url = Url }, null, 1, false, false);
            (string Code, string Key)[] refusals =
            {
                (ReplayOperation.ClientCertificateProxyCode, "COMPOSER_CLIENT_CERT_PROXY"),
                (ReplayOperation.ClientCertificateHttpsCode, "COMPOSER_CLIENT_CERT_HTTPS"),
                (ReplayOperation.ClientCertificateOriginInvalidCode, "COMPOSER_CLIENT_CERT_ORIGIN_INVALID"),
                (ReplayOperation.ClientCertificateOriginCountCode, "COMPOSER_CLIENT_CERT_ORIGIN_COUNT"),
                (ReplayOperation.ClientCertificatePolicyCode, "COMPOSER_CLIENT_CERT_POLICY"),
                (ReplayCertificateResolver.NotFoundCode, "COMPOSER_CLIENT_CERT_NOT_FOUND"),
                (ReplayCertificateResolver.AmbiguousCode, "COMPOSER_CLIENT_CERT_AMBIGUOUS"),
                (ReplayCertificateResolver.StoreUnavailableCode, "COMPOSER_CLIENT_CERT_STORE_UNAVAILABLE"),
                (ReplayCertificateResolver.AuthorityCode, "COMPOSER_CLIENT_CERT_CA"),
                (ReplayCertificateResolver.NotClientAuthCode, "COMPOSER_CLIENT_CERT_NOT_CLIENT_AUTH"),
                (ReplayCertificateResolver.KeyUsageCode, "COMPOSER_CLIENT_CERT_KEY_USAGE"),
                (ReplayCertificateResolver.UnsupportedKeyCode, "COMPOSER_CLIENT_CERT_UNSUPPORTED_KEY"),
                (ReplayCertificateResolver.MetadataUnavailableCode, "COMPOSER_CLIENT_CERT_METADATA_UNAVAILABLE"),
                (ReplayCertificateResolver.NoPrivateKeyCode, "COMPOSER_CLIENT_CERT_NO_PRIVATE_KEY"),
                (ReplayCertificateResolver.KeyUnavailableCode, "COMPOSER_CLIENT_CERT_KEY_UNAVAILABLE"),
            };

            Assert.Equal(
                refusals.Select(refusal => ErrorText(refusal.Key)),
                refusals.Select(refusal => RequestComposerWindow.RunRefusalText(
                    new OperationDiagnostic(refusal.Code, OperationDiagnosticSeverity.Error, "The operation's own words."), plan)));
            Assert.Equal(
                new[]
                {
                    "Client-certificate authentication requires a supported HTTPS origin; nothing was sent.",
                    "The certificate store could not be read; nothing was sent.",
                    "The certificate's authentication properties could not be read; nothing was sent.",
                },
                new[]
                {
                    Words("COMPOSER_CLIENT_CERT_ORIGIN_INVALID"),
                    Words("COMPOSER_CLIENT_CERT_STORE_UNAVAILABLE"),
                    Words("COMPOSER_CLIENT_CERT_METADATA_UNAVAILABLE"),
                });
            Assert.StartsWith(
                "Client certificate: it is sent only to a server whose certificate this PC accepts for the origin, and " +
                    "identifies the user to that server. ",
                Words("COMPOSER_CONFIRM_CLIENT_CERT", "the certificate", Origin),
                StringComparison.Ordinal);
        }

        // Open request, Load and a drop each turn Client certificate off and forget the certificate chosen, since neither a
        // request file nor the history keeps one; a drop still keeps Connect to another endpoint, as it is. Changing it, by
        // turning it on or only by choosing a certificate, is an edit that each asks about first, a drop also with only that
        // changed, and declining keeps the request and the choice as they were. Once reset, replacing the request again
        // asks nothing.
        [Theory]
        [InlineData("open", "on")]
        [InlineData("open", "chosen")]
        [InlineData("load", "on")]
        [InlineData("load", "chosen")]
        [InlineData("drop", "on")]
        [InlineData("drop", "chosen")]
        public Task ReplacingTheRequest_ResetsTheClientCertificate_AndAsksFirstWhenItChanged(
            string replacement, string edit) => _application.RunAsync(async () =>
        {
            using var chosen = new TestCertificate("CN=Alpha client", TestNow.AddDays(-30), TestNow.AddDays(30));
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
            window.Client.CertificateNative = Native(chosen);
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
                var box = Find<CheckBox>(window, "ClientCertificateBox");
                if (edit == "chosen")
                {
                    await ChooseAsync(window, chosen);
                    box.IsChecked = false;
                }
                else
                {
                    box.IsChecked = true;
                }

                var changed = CertificateState(window);
                string request = Request(window);

                Assert.False(await ReplaceAsync());

                Assert.Equal(question, Assert.Single(questions));
                Assert.Equal(changed, CertificateState(window));
                Assert.Equal(request, Request(window));

                replace = true;
                Assert.True(await ReplaceAsync());

                Assert.Equal(2, questions.Count);
                Assert.Equal((false, false, (string)null), CertificateState(window));
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

        // Save request as never saves Client certificate: a file with the captured content holds the request and its HTTP
        // version, never the certificate, which stays chosen in the composer.
        [Fact]
        public Task SavingARequest_NeverSavesTheCertificate() => _application.RunAsync(async () =>
        {
            using var chosen = new TestCertificate("CN=Alpha client", TestNow.AddDays(-30), TestNow.AddDays(30));
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            window.Client.CertificateNative = Native(chosen);
            var saved = new List<string>();
            window.ChooseSavePath = () => Path.Join(AppContext.BaseDirectory, "certified.xmat-request.json");
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
                await ChooseAsync(window, chosen);

                await window.SaveRequestAsync();

                string file = Assert.Single(saved);
                Assert.Contains(Url, file, StringComparison.Ordinal);
                Assert.DoesNotContain(chosen.Thumbprint, file, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Alpha", file, StringComparison.Ordinal);
                Assert.Equal((true, true, chosen.Thumbprint), CertificateState(window));
            }
            finally
            {
                window.Close();
            }
        });

        // A confirmed plan gets the permission to offer a client certificate only to the one origin that its preview found
        // for it, and nothing without the user's confirmation.
        [Fact]
        public void ComposerOptions_AllowTheCertificate_OnlyForTheConfirmedPlansOrigin()
        {
            using var client = new RequestReplayClient();
            Assert.True(ReplayClientCertificate.TryParse(new string('A', 40), out ReplayClientCertificate certificate));
            var transport = new ReplayTransportOptions(ReplayHttpVersion.Auto, null, null, certificate);
            ReplayPlanPreview certifying = client.Preview(
                ReplaySource.FromDraft(new RequestReplayDraft { Url = Url }), 0, transport).Value;
            ReplayPlanPreview direct = client.Preview(ReplaySource.FromDraft(new RequestReplayDraft { Url = Url }), 0).Value;
            Assert.True(certifying.UsesClientCertificate);
            Assert.Equal(Origin, certifying.CredentialOrigin.ToString());
            Assert.False(direct.UsesClientCertificate);

            ReplayOptions confirmed = RequestReplayClient.ComposerOptions(0, certifying.Transport, certifying);

            Assert.Equal(certifying.CredentialOrigin, confirmed.AllowClientCertificate);
            Assert.Equal((false, false), (confirmed.AllowProxy, confirmed.AllowConnectTo));
            Assert.Null(RequestReplayClient.ComposerOptions(0, certifying.Transport).AllowClientCertificate);
            Assert.Null(RequestReplayClient.ComposerOptions(0, direct.Transport, direct).AllowClientCertificate);
        }

        // A row's note says that a client certificate was configured for its send, from what the operation recorded, never
        // which one and never whether it was sent, with how the send was routed and the HTTP versions, also for a send that
        // is otherwise direct with the version chosen automatically.
        [Fact]
        public void TransportNote_SaysThatACertificateWasConfigured()
        {
            string certificate = Words("REPLAY_NOTE_CLIENT_CERT");
            Assert.Equal(
                certificate + " " + VersionNote("COMPOSER_HTTP_VERSION_AUTO", "HTTP/1.1"),
                ReplayRecorder.TransportNote(Sent("direct", "auto", "1.1", configured: true)));
            Assert.Equal(
                Words("REPLAY_NOTE_CONNECT_TO") + " " + certificate + " " + VersionNote("COMPOSER_HTTP_VERSION_11", "HTTP/1.1"),
                ReplayRecorder.TransportNote(Sent("connect-to", "1.1", "1.1", configured: true)));
            Assert.Null(ReplayRecorder.TransportNote(Sent("direct", "auto", "1.1", configured: false)));
        }

        // The current user's Personal store as the fake of the native calls holds it: these certificates, each with the
        // public data that its test chose, and a key that signs silently, the first certificate's.
        private static RecordingCertificateNative Native(params TestCertificate[] certificates) =>
            new(certificates.Select(certificate => certificate.Certificate).ToArray())
            {
                Fake = true,
                SigningCertificate = certificates.FirstOrDefault()?.Certificate,
                Metadata = listed => certificates.Single(certificate => certificate.Thumbprint == listed.Thumbprint).Metadata,
            };

        // Chooses a certificate as the user does: turns Client certificate on, opens its list and picks the certificate.
        private static async Task ChooseAsync(RequestComposerWindow window, TestCertificate certificate)
        {
            Find<CheckBox>(window, "ClientCertificateBox").IsChecked = true;
            await window.ListCertificatesAsync();
            var list = Find<ComboBox>(window, "ClientCertificateList");
            list.SelectedItem = Certificates(list).Single(item => Listed(item).Thumbprint == certificate.Thumbprint);
        }

        // The certificates in the list, without what it says instead of certificates.
        private static ComboBoxItem[] Certificates(ComboBox list) =>
            list.Items.OfType<ComboBoxItem>().Where(item => item.Tag is ReplayCertificateListing).ToArray();

        private static ReplayCertificateListing Listed(ComboBoxItem item) => (ReplayCertificateListing)item.Tag;

        // A certificate as the list shows it, its subject and issuer bounded to 120 characters and escaped.
        private static string Item(ReplayCertificateListing listing, string key) => Words(
            key,
            UntrustedText.Show(listing.Subject, 120),
            UntrustedText.Show(listing.Issuer, 120),
            listing.NotAfter.ToLocalTime(),
            listing.NotBefore.ToLocalTime());

        // Client certificate as the composer shows it: selected, its list available, and the thumbprint of the certificate
        // chosen, if any.
        private static (bool, bool, string) CertificateState(Window window)
        {
            var list = Find<ComboBox>(window, "ClientCertificateList");
            return (
                Find<CheckBox>(window, "ClientCertificateBox").IsChecked == true,
                list.IsEnabled,
                list.SelectedItem is ComboBoxItem item ? Listed(item).Thumbprint : null);
        }

        // An operation whose runs with a sender of their own record the certificate, the origin and the endpoint that their
        // sender was made for, and send to the handler, in memory, instead of connecting anywhere.
        private static ReplayOperation CertifyingOperation(List<SenderUsed> senders, HttpMessageHandler handler) => new()
        {
            OwnSenderFactory = setup =>
            {
                lock (senders)
                {
                    senders.Add(new SenderUsed(
                        setup.ClientCertificate?.Thumbprint, setup.CredentialOrigin?.ToString(), setup.ConnectTo?.ToString()));
                }

                return new ReplaySender(new HttpMessageInvoker(handler, disposeHandler: false), ownsInvoker: true, setup);
            },
        };

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

        private static ReplayItemResult Sent(string transport, string version, string responseVersion, bool configured) =>
            new(1, null, null, 1, ReplayOutcome.Completed, 200, TimeSpan.Zero, 5, 0, null, null,
                Transport: transport, HttpVersion: version, ResponseVersion: responseVersion, ClientCertificateConfigured: configured);

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

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            var clock = Stopwatch.StartNew();
            while (!condition())
            {
                Assert.True(clock.Elapsed < Patience, "The condition did not hold in time.");
                await Task.Delay(10);
            }
        }

        // Shows the window off screen and not activated, as opening its list needs.
        private static async Task ShowOffscreenAsync(Window window)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -12000;
            window.Top = -12000;
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }

        private sealed record SenderUsed(string Thumbprint, string Origin, string ConnectTo);

        private sealed record HandledRequest(Uri Uri, string Method);

        // A client certificate made in memory, with a key that no key store keeps, and the public data that the fake reads
        // for it: the subject, issuer and validity that its test chose, for client authentication with an RSA key.
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
