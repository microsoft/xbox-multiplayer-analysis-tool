// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using XMAT.Operations.Replay;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Replay
{
    // Raw mode's editing in the composer: the mode button, the two conversions with
    // their refusals and loss question, the HTTP version, the counts line, the controls that Raw mode disables, Fix
    // Content-Length, and the body, which is never counted on the dispatcher and is built exactly. Nothing is sent: the
    // target is a loopback origin that no test sends to.
    [Collection(CaptureUiApplicationCollection.Name)]
    public class ComposerRawModeTests
    {
        private const string Origin = "http://127.0.0.1:9";

        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

        private static readonly Func<string, bool> NoQuestion =
            message => throw new Xunit.Sdk.XunitException("Nothing needs confirmation here: " + message);

        // The elements that only Raw mode shows, and those that only Parsed mode shows.
        private static readonly string[] RawOnly =
        {
            "RawWarningPanel", "RawTargetLabel", "RawTargetBox", "RawHeadLabel", "RawHeadBox", "RawCountsText",
            "FixContentLengthButton",
        };

        private static readonly string[] ParsedOnly =
        {
            "MethodLabel", "MethodBox", "UrlLabel", "UrlBox", "HeadersLabel", "HeadersBox",
        };

        private readonly CaptureUiApplicationFixture _application;

        public ComposerRawModeTests(CaptureUiApplicationFixture application)
        {
            _application = application;
        }

        static ComposerRawModeTests()
        {
            Localization.LoadLanguage("en-us");
        }

        // The mode button comes first in the editor, named by its content and helped by its hint, and the window starts in
        // Parsed mode. Raw mode shows its warning, its notice, the target origin, the head and the counts line, each named for
        // automation, in place of the method, the URL and the headers; the counts line is not live.
        [Fact]
        public Task TheModeButton_ComesFirst_AndRawModeShowsItsOwnFields() => _application.RunAsync(async () =>
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            try
            {
                var mode = Find<Button>(window, "ModeButton");
                Assert.Same(mode, Find<Grid>(window, "RequestEditor").Children[0]);
                Assert.Equal(RequestComposerWindow.ComposerMode.Parsed, window.Mode);
                Assert.Equal(Words("COMPOSER_MODE_RAW"), mode.Content);
                Assert.Equal(Words("COMPOSER_MODE_RAW_HINT"), mode.ToolTip);
                Assert.Equal(Words("COMPOSER_MODE_RAW_HINT"), AutomationProperties.GetHelpText(mode));
                Assert.Equal(Words("COMPOSER_NOTICE"), Find<TextBlock>(window, "ReplayNotice").Text);
                Assert.All(RawOnly, name => Assert.Equal(Visibility.Collapsed, Element(window, name).Visibility));

                Find<TextBox>(window, "UrlBox").Text = Origin + "/items";
                await window.SwitchModeAsync();

                Assert.Equal(RequestComposerWindow.ComposerMode.Raw, window.Mode);
                Assert.Equal(Words("COMPOSER_RAW_EDITING"), Status(window));
                Assert.Equal(Words("COMPOSER_MODE_PARSED"), mode.Content);
                Assert.Equal(Words("COMPOSER_MODE_PARSED_HINT"), AutomationProperties.GetHelpText(mode));
                Assert.All(RawOnly, name => Assert.Equal(Visibility.Visible, Element(window, name).Visibility));
                Assert.All(ParsedOnly, name => Assert.Equal(Visibility.Collapsed, Element(window, name).Visibility));
                Assert.Equal(Words("COMPOSER_RAW_NOTICE"), Find<TextBlock>(window, "ReplayNotice").Text);
                Assert.Equal(Words("COMPOSER_RAW_WARNING"), Find<TextBlock>(window, "RawWarningText").Text);
                Assert.Equal(
                    Words("COMPOSER_RAW_WARNING_NAME"), AutomationProperties.GetName(Find<Border>(window, "RawWarningPanel")));
                var target = Find<TextBox>(window, "RawTargetBox");
                var head = Find<TextBox>(window, "RawHeadBox");
                Assert.Equal(Words("COMPOSER_RAW_TARGET_FIELD"), AutomationProperties.GetName(target));
                Assert.Equal(Words("COMPOSER_RAW_TARGET_HINT"), AutomationProperties.GetHelpText(target));
                Assert.Equal(Words("COMPOSER_RAW_TARGET_HINT"), target.ToolTip);
                Assert.Equal(Words("COMPOSER_RAW_HEAD_FIELD"), AutomationProperties.GetName(head));
                Assert.Equal(Words("COMPOSER_RAW_HEAD_HINT"), AutomationProperties.GetHelpText(head));
                Assert.Equal(
                    AutomationLiveSetting.Off, AutomationProperties.GetLiveSetting(Find<TextBlock>(window, "RawCountsText")));
                Assert.Equal(Origin, target.Text);
                Assert.Equal("GET /items HTTP/1.1\r\nHost: 127.0.0.1:9\r\nContent-Length: 0", head.Text);
            }
            finally
            {
                window.Close();
            }
        });

        // Each mode's access keys are unique among the elements that it shows: Edit as raw text takes X in Parsed mode, and
        // Raw mode's own elements take N, G, Q and X, which the method, the URL, the headers and that button free.
        [Fact]
        public Task AccessKeys_AreUniqueInEachMode() => _application.RunAsync(async () =>
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            try
            {
                IReadOnlyList<char> parsed = AccessKeys(window);
                Find<TextBox>(window, "UrlBox").Text = Origin + "/items";
                await window.SwitchModeAsync();
                IReadOnlyList<char> raw = AccessKeys(window);

                Assert.Equal(parsed.Distinct().Count(), parsed.Count);
                Assert.Equal(raw.Distinct().Count(), raw.Count);
                Assert.Contains('X', parsed);
                Assert.DoesNotContain('N', parsed);
                Assert.All(new[] { 'N', 'G', 'Q', 'X' }, key => Assert.Contains(key, raw));
                Assert.All(new[] { 'M', 'U', 'H' }, key => Assert.DoesNotContain(key, raw));
            }
            finally
            {
                window.Close();
            }
        });

        // Edit as raw text renders the request as Parsed mode sends it (RawConversion.FromDraft): the target box holds the
        // URL's origin, the request line its path and query, Host comes first, the header lines follow in order and case,
        // with Connection and the field that it nominates left out, and Content-Length is the body's length, in place of the
        // editor's. Fields that only a raw send refuses stay as typed. What changes is listed first, and the body stays.
        [Fact]
        public Task EditAsRawText_RendersTheRequestAsParsedModeSendsIt_AfterListingWhatChanges() =>
            _application.RunAsync(async () =>
            {
                var questions = new List<string>();
                var window = RequestComposerWindow.ForNewRequest(confirm: Answer(questions, true));
                try
                {
                    Find<TextBox>(window, "MethodBox").Text = "POST";
                    Find<TextBox>(window, "UrlBox").Text = Origin + "/items/7?page=2";
                    Find<TextBox>(window, "HeadersBox").Text =
                        "Host: other.example\r\nX-Trace: A\r\nConnection: keep-alive, X-Hop\r\nX-Hop: 1\r\n" +
                        "Content-Length: 99\r\nAccept:  text/plain  \r\nExpect: 100-continue\r\nUpgrade: websocket\r\n" +
                        "X-Twice: 1\r\nx-twice: 2\r\nFiddler-Host: 192.0.2.1\r\nX-Text: caf\u00e9";
                    Find<TextBox>(window, "BodyBox").Text = "hello";

                    await window.SwitchModeAsync();

                    Assert.Equal(
                        Words(
                            "COMPOSER_RAW_CONVERT_QUESTION",
                            string.Join(
                                "\n",
                                Words("COMPOSER_RAW_LOSS_LEFT_OUT", "Connection, X-Hop"),
                                Words("COMPOSER_RAW_LOSS_HOST", "127.0.0.1:9"),
                                Words("COMPOSER_RAW_LOSS_LENGTH", 5L),
                                Words("COMPOSER_RAW_LOSS_SPACING"))),
                        Assert.Single(questions));
                    Assert.Equal(RequestComposerWindow.ComposerMode.Raw, window.Mode);
                    Assert.Equal(Origin, Find<TextBox>(window, "RawTargetBox").Text);
                    Assert.Equal(
                        "POST /items/7?page=2 HTTP/1.1\r\nHost: 127.0.0.1:9\r\nX-Trace: A\r\nContent-Length: 5\r\n" +
                            "Accept: text/plain\r\nExpect: 100-continue\r\nUpgrade: websocket\r\nX-Twice: 1\r\nx-twice: 2\r\n" +
                            "Fiddler-Host: 192.0.2.1\r\nX-Text: caf\u00e9",
                        Find<TextBox>(window, "RawHeadBox").Text);
                    Assert.Equal("hello", Find<TextBox>(window, "BodyBox").Text);
                }
                finally
                {
                    window.Close();
                }
            });

        // The target is written in ASCII, an IDN as its punycode and the scheme's default port left out, and a path or query
        // that the URL escapes is written escaped, which the question says.
        [Theory]
        [InlineData(
            "http://b\u00fccher.example/x", "http://xn--bcher-kva.example", "GET /x HTTP/1.1\r\nHost: xn--bcher-kva.example",
            null)]
        [InlineData("https://127.0.0.1:443/x", "https://127.0.0.1", "GET /x HTTP/1.1\r\nHost: 127.0.0.1", null)]
        [InlineData(
            "http://127.0.0.1:9/a b?q=\u00e9", Origin, "GET /a%20b?q=%C3%A9 HTTP/1.1\r\nHost: 127.0.0.1:9", "/a%20b?q=%C3%A9")]
        public Task EditAsRawText_WritesTheTargetInAscii_AndAnEscapedPathAsSent(
            string url, string target, string requestLineAndHost, string escaped) => _application.RunAsync(async () =>
            {
                var questions = new List<string>();
                var window = RequestComposerWindow.ForNewRequest(confirm: Answer(questions, true));
                try
                {
                    Find<TextBox>(window, "UrlBox").Text = url;

                    await window.SwitchModeAsync();

                    Assert.Equal(
                        escaped == null
                            ? Array.Empty<string>()
                            : new[] { Words("COMPOSER_RAW_CONVERT_QUESTION", Words("COMPOSER_RAW_LOSS_TARGET", escaped)) },
                        questions);
                    Assert.Equal(target, Find<TextBox>(window, "RawTargetBox").Text);
                    Assert.Equal(requestLineAndHost + "\r\nContent-Length: 0", Find<TextBox>(window, "RawHeadBox").Text);
                }
                finally
                {
                    window.Close();
                }
            });

        // Each refusal of Edit as raw text, in its order: each case also sets what the next refusal would refuse, and the
        // earlier one is said. A refusal asks nothing, and leaves the editors, the mode and the HTTP version as they were.
        [Theory]
        [InlineData("omitted", "COMPOSER_RAW_CONVERT_OMITTED")]
        [InlineData("numbered", "COMPOSER_RAW_CONVERT_NUMBERED")]
        [InlineData("http2", "COMPOSER_RAW_CONVERT_HTTP2")]
        [InlineData("redirects", "COMPOSER_RAW_CONVERT_REDIRECTS")]
        [InlineData("proxy", "COMPOSER_RAW_CONVERT_PROXY")]
        [InlineData("certificate", "COMPOSER_RAW_CONVERT_CLIENT_CERT")]
        [InlineData("windows", "COMPOSER_RAW_CONVERT_WINDOWS_AUTH")]
        [InlineData("url", "COMPOSER_RAW_CONVERT_URL")]
        [InlineData("long-url", "COMPOSER_RAW_CONVERT_URL")]
        [InlineData("target", "COMPOSER_RAW_CONVERT_TARGET")]
        [InlineData("method", "COMPOSER_RAW_CONVERT_METHOD")]
        [InlineData("headers", "COMPOSER_RAW_CONVERT_HEADERS")]
        [InlineData("limits", "COMPOSER_RAW_CONVERT_LIMITS")]
        public Task EditAsRawText_RefusesInItsOrder_AndChangesNothing(string refusal, string key) =>
            _application.RunAsync(async () =>
            {
                string directory = FixtureDirectory();
                var questions = new List<string>();
                var window = RequestComposerWindow.ForNewRequest(confirm: Answer(questions, true));
                try
                {
                    string expected = Words(key);
                    var url = Find<TextBox>(window, "UrlBox");
                    url.Text = Origin + "/items";
                    switch (refusal)
                    {
                        case "omitted":
                            string path = Path.Join(directory, "template.xmat-request.json");
                            File.WriteAllText(
                                path,
                                "{ \"schema\": \"xmat-request/1\", \"profile\": \"template\", \"method\": \"GET\", " +
                                "\"url\": null, \"httpVersion\": \"auto\", \"headers\": null, \"body\": null, " +
                                "\"omitted\": [\"url\", \"headers\", \"body\"] }");
                            window.ChooseRequestFile = () => path;
                            await window.OpenRequestAsync();
                            url.Text = Origin + "/items/#";
                            expected = Words(
                                key, RequestComposerWindow.PendingParts(new RequestOmissions(false, true, true, true)));
                            break;
                        case "numbered":
                            url.Text = Origin + "/items/#";
                            ChooseVersion(window, ReplayHttpVersion.Http2);
                            break;
                        case "http2":
                            ChooseVersion(window, ReplayHttpVersion.Http2);
                            Find<CheckBox>(window, "FollowRedirectsBox").IsChecked = true;
                            break;
                        case "redirects":
                            Find<CheckBox>(window, "FollowRedirectsBox").IsChecked = true;
                            Find<CheckBox>(window, "ProxyBox").IsChecked = true;
                            break;
                        case "proxy":
                            Find<CheckBox>(window, "ProxyBox").IsChecked = true;
                            Find<CheckBox>(window, "ClientCertificateBox").IsChecked = true;
                            break;
                        case "certificate":
                            Find<CheckBox>(window, "ClientCertificateBox").IsChecked = true;
                            Find<CheckBox>(window, "WindowsAuthBox").IsChecked = true;
                            break;
                        case "windows":
                            Find<CheckBox>(window, "WindowsAuthBox").IsChecked = true;
                            url.Text = "ftp://127.0.0.1/x";
                            break;
                        case "url":
                            url.Text = "ftp://127.0.0.1/x";
                            Find<TextBox>(window, "MethodBox").Text = "GE T";
                            break;
                        case "long-url":
                            // A URL longer than a request file holds, refused before it is parsed.
                            url.Text = Origin + "/" + new string('a', RequestFileLimits.MaxUrlChars);
                            break;
                        case "target":
                            url.Text = "http://example.123/x";
                            Find<TextBox>(window, "MethodBox").Text = "GE T";
                            break;
                        case "method":
                            Find<TextBox>(window, "MethodBox").Text = "GE T";
                            Find<TextBox>(window, "HeadersBox").Text = "No colon";
                            break;
                        case "headers":
                            Find<TextBox>(window, "HeadersBox").Text = "No colon";
                            Find<TextBox>(window, "MethodBox").Text = new string('A', RawRequest.MaxMethodChars + 1);
                            break;
                        case "limits":
                            Find<TextBox>(window, "MethodBox").Text = new string('A', RawRequest.MaxMethodChars + 1);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(refusal));
                    }

                    // Opening the template over the URL typed above asked whether to replace it; the conversion asks nothing.
                    questions.Clear();
                    (string, string, string, string) editors = Editors(window);
                    ReplayHttpVersion version = SelectedVersion(window);

                    await window.SwitchModeAsync();

                    Assert.Equal(expected, Status(window));
                    Assert.Empty(questions);
                    Assert.Equal(RequestComposerWindow.ComposerMode.Parsed, window.Mode);
                    Assert.Equal(editors, Editors(window));
                    Assert.Equal(version, SelectedVersion(window));
                    Assert.Equal("", Find<TextBox>(window, "RawHeadBox").Text);
                }
                finally
                {
                    window.Close();
                    Directory.Delete(directory, recursive: true);
                }
            });

        // The loss question's answer decides: No keeps everything as it was; Yes changes the editors, the mode and the HTTP
        // version selector in one step.
        [Fact]
        public Task TheLossQuestion_NoChangesNothing_YesChangesEverythingInOneStep() => _application.RunAsync(async () =>
        {
            var questions = new List<string>();
            bool answer = false;
            var window = RequestComposerWindow.ForNewRequest(confirm: message =>
            {
                questions.Add(message);
                return answer;
            });
            try
            {
                Find<TextBox>(window, "UrlBox").Text = Origin + "/items";
                Find<TextBox>(window, "HeadersBox").Text = "Host: other.example";
                (string, string, string, string) editors = Editors(window);

                await window.SwitchModeAsync();

                Assert.Single(questions);
                Assert.Equal(Words("COMPOSER_CONVERT_CANCELLED"), Status(window));
                Assert.Equal(RequestComposerWindow.ComposerMode.Parsed, window.Mode);
                Assert.Equal(editors, Editors(window));
                Assert.Equal("", Find<TextBox>(window, "RawHeadBox").Text);
                Assert.Equal(ReplayHttpVersion.Auto, SelectedVersion(window));
                Assert.True(Find<ComboBox>(window, "HttpVersionBox").IsEnabled);

                answer = true;
                await window.SwitchModeAsync();

                Assert.Equal(2, questions.Count);
                Assert.Equal(RequestComposerWindow.ComposerMode.Raw, window.Mode);
                Assert.Equal(
                    "GET /items HTTP/1.1\r\nHost: 127.0.0.1:9\r\nContent-Length: 0", Find<TextBox>(window, "RawHeadBox").Text);
                Assert.Equal(ReplayHttpVersion.Http11, SelectedVersion(window));
                Assert.False(Find<ComboBox>(window, "HttpVersionBox").IsEnabled);
                Assert.Equal(Words("COMPOSER_RAW_EDITING"), Status(window));
            }
            finally
            {
                window.Close();
            }
        });

        // Cancel request, or closing the window, applies nothing once the conversion has given its result but before the
        // window takes it. The UI thread waits while the conversion's worker finishes, so the stop, queued as that worker
        // posts the conversion's continuation, runs ahead of the continuation, whichever thread is quicker.
        [Theory]
        [InlineData("cancel")]
        [InlineData("close")]
        public Task AStoppedConversion_AppliesNothing_EvenWhenItsResultCameFirst(string stop) => _application.RunAsync(async () =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            using var queued = new ManualResetEventSlim();
            int worker = 0;
            bool closed = false;
            bool stopped = false;
            void Stop()
            {
                stopped = true;
                if (stop == "close")
                {
                    closed = true;
                    window.Close();
                }
                else
                {
                    window.CancelSend();
                }
            }

            // The first operation that the conversion's worker posts once it counted the body is the conversion's
            // continuation; the UI thread runs nothing until the stop is queued ahead of it.
            DispatcherHookEventHandler posted = null;
            posted = (_, e) =>
            {
                if (Environment.CurrentManagedThreadId == worker && e.Operation.Priority == DispatcherPriority.Normal)
                {
                    dispatcher.Hooks.OperationPosted -= posted;
                    dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(Stop));
                    queued.Set();
                }
            };
            try
            {
                Find<TextBox>(window, "UrlBox").Text = Origin + "/items";
                (string, string, string, string) editors = Editors(window);
                window.CountBody = (draft, token) =>
                {
                    worker = Environment.CurrentManagedThreadId;
                    dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => queued.Wait(Patience)));
                    dispatcher.Hooks.OperationPosted += posted;
                    return draft.TryCountBody(token);
                };

                await window.SwitchModeAsync();

                Assert.True(stopped);
                Assert.Equal(RequestComposerWindow.ComposerMode.Parsed, window.Mode);
                Assert.Equal(editors, Editors(window));
                Assert.Equal("", Find<TextBox>(window, "RawHeadBox").Text);
                if (!closed)
                {
                    Assert.Equal(Words("COMPOSER_CONVERT_CANCELLED"), Status(window));
                }
            }
            finally
            {
                dispatcher.Hooks.OperationPosted -= posted;
                if (!closed)
                {
                    window.Close();
                }
            }
        });

        // The loss question names at most 20 fields, each cut to 100 characters, then how many more there are.
        [Fact]
        public Task TheLossQuestion_NamesAtMost20Fields_ThenHowManyMore() => _application.RunAsync(async () =>
        {
            var questions = new List<string>();
            var window = RequestComposerWindow.ForNewRequest(confirm: Answer(questions, false));
            try
            {
                string longName = "X-" + new string('n', 120);
                string[] nominated = new[] { longName }.Concat(Enumerable.Range(0, 20).Select(index => "X-" + index)).ToArray();
                Find<TextBox>(window, "UrlBox").Text = Origin + "/items";
                Find<TextBox>(window, "HeadersBox").Text =
                    "Connection: " + string.Join(", ", nominated) + string.Concat(nominated.Select(name => "\r\n" + name + ": 1"));

                await window.SwitchModeAsync();

                string shown = string.Join(
                    ", ",
                    new[] { "Connection", string.Concat(longName.AsSpan(0, 100), "\u2026") }.Concat(
                        Enumerable.Range(0, 18).Select(index => "X-" + index)));
                Assert.Equal(
                    Words(
                        "COMPOSER_RAW_CONVERT_QUESTION",
                        Words("COMPOSER_RAW_LOSS_LEFT_OUT", Words("COMPOSER_CONVERT_NAMES_MORE", shown, 2))),
                    Assert.Single(questions));
                Assert.Equal(RequestComposerWindow.ComposerMode.Parsed, window.Mode);
            }
            finally
            {
                window.Close();
            }
        });

        // A conversion, or a fix, is applied only to the editors that it was made from: an edit made meanwhile, while XMAT
        // asks or while the work runs off the UI thread, which only a test can make, is kept, and nothing is applied. The
        // edit is to the body in Parsed mode, and to the head in Raw mode.
        [Theory]
        [InlineData("raw")]
        [InlineData("parsed")]
        [InlineData("fix")]
        public Task AnEditMadeMeanwhile_IsKept_AndNothingIsApplied(string work) => _application.RunAsync(async () =>
        {
            const string EditedBody = "edited meanwhile";
            const string AddedLine = "\r\nX-Edited: meanwhile";
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            bool armed = false;
            Action edit = null;
            var window = RequestComposerWindow.ForNewRequest(confirm: _ =>
            {
                Assert.True(armed, "Only the conversion asks.");
                edit();
                return true;
            });
            try
            {
                var body = Find<TextBox>(window, "BodyBox");
                var head = Find<TextBox>(window, "RawHeadBox");
                body.Text = "hello";
                if (work == "raw")
                {
                    Find<TextBox>(window, "MethodBox").Text = "POST";
                    Find<TextBox>(window, "UrlBox").Text = Origin + "/x";
                    Find<TextBox>(window, "HeadersBox").Text = "Content-Length: 99";
                    edit = () => body.Text = EditedBody;
                }
                else
                {
                    await EnterRawModeAsync(window, "POST");
                    edit = () => head.Text += AddedLine;
                }

                if (work == "fix")
                {
                    head.Text = head.Text.Replace("Content-Length: 5", "Content-Length: 1", StringComparison.Ordinal);
                    window.CountBody = (draft, token) =>
                    {
                        if (!dispatcher.CheckAccess())
                        {
                            dispatcher.Invoke(edit);
                        }

                        return draft.TryCountBody(token);
                    };
                }

                (string Body, string Head) before = (body.Text, head.Text);
                armed = true;

                await (work == "fix" ? window.FixContentLengthAsync() : window.SwitchModeAsync());

                Assert.Equal(Words(work == "fix" ? "COMPOSER_RAW_LENGTH_UNCHANGED" : "COMPOSER_CONVERT_CHANGED"), Status(window));
                Assert.Equal(
                    work == "raw" ? RequestComposerWindow.ComposerMode.Parsed : RequestComposerWindow.ComposerMode.Raw,
                    window.Mode);
                Assert.Equal(
                    work == "raw" ? (EditedBody, before.Head) : (before.Body, before.Head + AddedLine),
                    (body.Text, head.Text));
            }
            finally
            {
                window.Close();
            }
        });

        // Edit as parsed reads the raw request back (RawRequest.ParseForEditor, then RawConversion.ToDraft): a Content-Length
        // body keeps its bytes and its editor, and a request that loses nothing asks nothing, so the editors are as they were
        // before Edit as raw text, and HTTP/1.1 only comes back with them.
        [Fact]
        public Task EditAsParsed_ReadsARequestThatLosesNothing_AsItWas() => _application.RunAsync(async () =>
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            try
            {
                Find<TextBox>(window, "MethodBox").Text = "POST";
                Find<TextBox>(window, "UrlBox").Text = Origin + "/items";
                Find<TextBox>(window, "HeadersBox").Text = "X-Trace: A";
                Find<TextBox>(window, "BodyBox").Text = "hello";
                ChooseVersion(window, ReplayHttpVersion.Http11);
                (string, string, string, string) editors = Editors(window);

                await window.SwitchModeAsync();
                Assert.Equal(
                    "POST /items HTTP/1.1\r\nHost: 127.0.0.1:9\r\nX-Trace: A\r\nContent-Length: 5",
                    Find<TextBox>(window, "RawHeadBox").Text);
                await window.SwitchModeAsync();

                Assert.Equal(RequestComposerWindow.ComposerMode.Parsed, window.Mode);
                Assert.Equal(Words("COMPOSER_PARSED_EDITING"), Status(window));
                Assert.Equal(editors, Editors(window));
                Assert.Equal(ReplayHttpVersion.Http11, SelectedVersion(window));
                Assert.True(Find<ComboBox>(window, "HttpVersionBox").IsEnabled);
                Assert.Equal(("", ""), (Find<TextBox>(window, "RawTargetBox").Text, Find<TextBox>(window, "RawHeadBox").Text));
            }
            finally
            {
                window.Close();
            }
        });

        // What Edit as parsed cannot keep is listed first: a chunked body, which becomes its decoded bytes; the fields that
        // Parsed mode keeps but never sends, Connection close and Proxy-Authorization among them; an absolute-form target;
        // a path that the URL writes otherwise; and HTTP version Automatic, under which the connection may use HTTP/2.
        [Fact]
        public Task EditAsParsed_ListsWhatItLoses_ThenDecodesAChunkedBody() => _application.RunAsync(async () =>
        {
            var questions = new List<string>();
            var window = RequestComposerWindow.ForNewRequest(confirm: Answer(questions, true));
            try
            {
                await EnterRawModeAsync(window, "POST");
                questions.Clear();
                Find<TextBox>(window, "RawHeadBox").Text =
                    "POST http://127.0.0.1:9/a/../b HTTP/1.1\r\nHost: 127.0.0.1:9\r\nTransfer-Encoding: chunked\r\n" +
                    "Connection: close\r\nProxy-Authorization: Basic eA==";
                Find<TextBox>(window, "BodyBox").Text = "5\r\nhello\r\n0\r\n\r\n";

                await window.SwitchModeAsync();

                Assert.Equal(
                    Words(
                        "COMPOSER_PARSED_CONVERT_QUESTION",
                        string.Join(
                            "\n",
                            Words("COMPOSER_PARSED_LOSS_CHUNKED"),
                            Words("COMPOSER_PARSED_LOSS_ABSOLUTE"),
                            Words("COMPOSER_PARSED_LOSS_TARGET", "/b"),
                            Words("COMPOSER_PARSED_LOSS_NOT_SENT", "Connection, Proxy-Authorization"),
                            Words("COMPOSER_PARSED_LOSS_VERSION"))),
                    Assert.Single(questions));
                Assert.Equal(RequestComposerWindow.ComposerMode.Parsed, window.Mode);
                Assert.Equal(
                    ("POST", Origin + "/a/../b", "Connection: close\r\nProxy-Authorization: Basic eA==", "hello"),
                    Editors(window));
                Assert.False(Find<CheckBox>(window, "Base64Box").IsChecked == true);
                Assert.Equal(ReplayHttpVersion.Auto, SelectedVersion(window));
            }
            finally
            {
                window.Close();
            }
        });

        // Every refusal of Edit as parsed keeps Raw mode and the raw request as they are, and says what refused it in its
        // code's words, with the head's line for a fault in the head: the limits, the head's syntax, a lone CR, an empty line
        // inside the head, a character outside ASCII such as U+FEFF, the version, a Host that names another origin, an
        // absolute-form target of another origin, Content-Length with Transfer-Encoding, a chunk extension, and a target
        // origin that is no origin.
        [Theory]
        [InlineData("limits", RawCodes.InputTooLarge)]
        [InlineData("syntax", RawCodes.Invalid)]
        [InlineData("lone-cr", RawCodes.Invalid)]
        [InlineData("empty-line", RawCodes.Invalid)]
        [InlineData("bom", RawCodes.Invalid)]
        [InlineData("version", RawCodes.Version)]
        [InlineData("host", RawCodes.Host)]
        [InlineData("absolute", RawCodes.Target)]
        [InlineData("framing", RawCodes.Framing)]
        [InlineData("extension", RawCodes.Trailer)]
        [InlineData("origin", null)]
        public Task EditAsParsed_RefusesWhatItCannotRead_AndKeepsRawMode(string refusal, string code) =>
            _application.RunAsync(async () =>
            {
                var window = RequestComposerWindow.ForNewRequest(confirm: Answer(new List<string>(), true));
                try
                {
                    await EnterRawModeAsync(window, "POST");
                    string head = refusal switch
                    {
                        "limits" => "POST /x HTTP/1.1\r\nHost: 127.0.0.1:9" +
                            string.Concat(Enumerable.Repeat("\r\nX-Field: 1", RawRequest.MaxFieldLines)),
                        "syntax" => "POST /x HTTP/1.1\r\nHost: 127.0.0.1:9\r\nno colon here",
                        "lone-cr" => "POST /x HTTP/1.1\rHost: 127.0.0.1:9\r\nContent-Length: 5",
                        "empty-line" => "POST /x HTTP/1.1\r\n\r\nHost: 127.0.0.1:9\r\nContent-Length: 5",
                        "bom" => "\ufeffPOST /x HTTP/1.1\r\nHost: 127.0.0.1:9\r\nContent-Length: 5",
                        "version" => "POST /x HTTP/1.0\r\nHost: 127.0.0.1:9",
                        "host" => "POST /x HTTP/1.1\r\nHost: other.example",
                        "absolute" => "POST http://other.example/x HTTP/1.1\r\nHost: 127.0.0.1:9",
                        "framing" => "POST /x HTTP/1.1\r\nHost: 127.0.0.1:9\r\nContent-Length: 5\r\nTransfer-Encoding: chunked",
                        "extension" => "POST /x HTTP/1.1\r\nHost: 127.0.0.1:9\r\nTransfer-Encoding: chunked",
                        _ => "POST /x HTTP/1.1\r\nHost: 127.0.0.1:9\r\nContent-Length: 5",
                    };
                    string body = refusal == "extension" ? "5;x=1\r\nhello\r\n0\r\n\r\n" : "hello";
                    string target = refusal == "origin" ? Origin + "/x" : Origin;
                    Find<TextBox>(window, "RawTargetBox").Text = target;
                    Find<TextBox>(window, "RawHeadBox").Text = head;
                    Find<TextBox>(window, "BodyBox").Text = body;
                    string expected;
                    if (code == null)
                    {
                        expected = Words("COMPOSER_RAW_ORIGIN");
                    }
                    else
                    {
                        Assert.True(ReplayRawTarget.TryParse(target, out ReplayRawTarget parsedTarget));
                        RawEditorParseResult read = RawRequest.ParseForEditor(
                            head, Encoding.UTF8.GetBytes(body), parsedTarget, CancellationToken.None);
                        Assert.Equal(code, read.Code);
                        if (code == RawCodes.Invalid)
                        {
                            Assert.Equal(refusal switch { "lone-cr" or "bom" => 1, "empty-line" => 2, _ => 3 }, read.Line);
                        }

                        expected = RawSendWording.CodeText(code) +
                            (read.Line == null ? "" : " " + Words("COMPOSER_RAW_AT_LINE", read.Line.Value));
                    }

                    await window.SwitchModeAsync();

                    Assert.Equal(Words("COMPOSER_PARSED_CONVERT_REFUSED", expected), Status(window));
                    Assert.Equal(RequestComposerWindow.ComposerMode.Raw, window.Mode);
                    Assert.Equal(
                        (target, head, body),
                        (Find<TextBox>(window, "RawTargetBox").Text, Find<TextBox>(window, "RawHeadBox").Text,
                            Find<TextBox>(window, "BodyBox").Text));
                }
                finally
                {
                    window.Close();
                }
            });

        // Fields that only a raw send refuses are converted, and Parsed mode's own checks then apply to them: Expect, Upgrade,
        // a repeated field, a Fiddler control field, and a HEAD request with a body.
        [Fact]
        public Task EditAsParsed_ConvertsFieldsThatOnlyARawSendRefuses() => _application.RunAsync(async () =>
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: Answer(new List<string>(), true));
            try
            {
                await EnterRawModeAsync(window, "POST");
                Find<TextBox>(window, "RawHeadBox").Text =
                    "HEAD /x HTTP/1.1\r\nHost: 127.0.0.1:9\r\nExpect: 100-continue\r\nUpgrade: websocket\r\nX-Twice: 1\r\n" +
                    "X-Twice: 2\r\nFiddler-Host: 192.0.2.1\r\nContent-Length: 5";
                Find<TextBox>(window, "BodyBox").Text = "hello";

                await window.SwitchModeAsync();

                Assert.Equal(RequestComposerWindow.ComposerMode.Parsed, window.Mode);
                Assert.Equal(
                    ("HEAD", Origin + "/x",
                        "Expect: 100-continue\r\nUpgrade: websocket\r\nX-Twice: 1\r\nX-Twice: 2\r\nFiddler-Host: 192.0.2.1", "hello"),
                    Editors(window));
            }
            finally
            {
                window.Close();
            }
        });

        // A body that the window holds as bytes keeps them through both conversions, as the same array, never copied: Fix
        // Content-Length's check gets that array after Edit as raw text, and again after a round trip through Parsed mode.
        [Fact]
        public Task AHeldBody_IsTheSameArray_ThroughBothConversions() => _application.RunAsync(async () =>
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            var bodies = new List<ReadOnlyMemory<byte>>();
            window.PrepareRaw = (headText, body, target, token, limits) =>
            {
                bodies.Add(body);
                return RawRequest.Prepare(headText, body, target, token, limits);
            };
            try
            {
                byte[] held = new byte[70_000];
                ChooseVersion(window, ReplayHttpVersion.Http11);
                Find<TextBox>(window, "MethodBox").Text = "POST";
                Find<TextBox>(window, "UrlBox").Text = Origin + "/x";
                window.ChooseBodyFile = () => Path.Join(AppContext.BaseDirectory, "held.bin");
                window.ReadBodyFile = (_, _) => Task.FromResult(new ReplayBodyRead(held, null));
                await window.UploadBodyAsync();

                await window.SwitchModeAsync();
                Assert.Equal(
                    "POST /x HTTP/1.1\r\nHost: 127.0.0.1:9\r\nContent-Length: 70000", Find<TextBox>(window, "RawHeadBox").Text);
                await window.FixContentLengthAsync();
                await window.SwitchModeAsync();
                Assert.Equal(RequestComposerWindow.ComposerMode.Parsed, window.Mode);
                await window.SwitchModeAsync();
                await window.FixContentLengthAsync();

                Assert.Equal(Words("COMPOSER_RAW_FIXED_LENGTH", 70_000L), Status(window));
                Assert.Equal(2, bodies.Count);
                Assert.All(bodies, body =>
                {
                    Assert.True(MemoryMarshal.TryGetArray(body, out ArraySegment<byte> segment));
                    Assert.Same(held, segment.Array);
                });
            }
            finally
            {
                window.Close();
            }
        });

        // Raw mode shows HTTP/1.1 only, disabled, with the reason, whatever was chosen for Parsed mode, which comes back with
        // Parsed mode: Automatic with the question's word that the connection may then use HTTP/2.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public Task TheHttpVersion_IsHttp11OnlyInRawMode_AndTheParsedChoiceComesBack(bool automatic) =>
            _application.RunAsync(async () =>
            {
                var questions = new List<string>();
                var window = RequestComposerWindow.ForNewRequest(confirm: Answer(questions, true));
                try
                {
                    ReplayHttpVersion chosen = automatic ? ReplayHttpVersion.Auto : ReplayHttpVersion.Http11;
                    var box = Find<ComboBox>(window, "HttpVersionBox");
                    ChooseVersion(window, chosen);
                    Find<TextBox>(window, "UrlBox").Text = Origin + "/items";

                    await window.SwitchModeAsync();

                    Assert.Empty(questions);
                    Assert.Equal(ReplayHttpVersion.Http11, SelectedVersion(window));
                    Assert.False(box.IsEnabled);
                    Assert.Equal(Words("COMPOSER_RAW_OFF_HTTP_VERSION"), box.ToolTip);
                    Assert.True(ToolTipService.GetShowOnDisabled(box));

                    await window.SwitchModeAsync();

                    Assert.Equal(
                        automatic
                            ? new[] { Words("COMPOSER_PARSED_CONVERT_QUESTION", Words("COMPOSER_PARSED_LOSS_VERSION")) }
                            : Array.Empty<string>(),
                        questions);
                    Assert.Equal(chosen, SelectedVersion(window));
                    Assert.True(box.IsEnabled);
                    Assert.Equal(Words("COMPOSER_HTTP_VERSION_HINT"), box.ToolTip);
                }
                finally
                {
                    window.Close();
                }
            });

        // A drop asks in Raw mode, which is always an edit: declining keeps the raw request exactly, and accepting loads the
        // dropped request in Parsed mode, with the raw editors cleared and the HTTP version chosen for Parsed mode back.
        [Fact]
        public Task ADropAsksInRawMode_ThenLoadsInParsedMode() => _application.RunAsync(async () =>
        {
            var questions = new List<string>();
            bool answer = false;
            var window = RequestComposerWindow.ForNewRequest(confirm: message =>
            {
                questions.Add(message);
                return answer;
            });
            try
            {
                await EnterRawModeAsync(window, "GET");
                string head = Find<TextBox>(window, "RawHeadBox").Text;
                ProxyConnectionModel row = new(
                    7, "200", DateTime.UtcNow, TimeSpan.Zero, "http", "127.0.0.1:9", "GET", "/dropped",
                    "GET /dropped HTTP/1.1\r\nHost: 127.0.0.1:9\r\n", [], "HTTP/1.1 200 OK\r\n", [], "127.0.0.1");

                using (ComposerSessionDragToken drag = ComposerSessionDrag.Begin(row, "Source tab"))
                {
                    Assert.False(window.DropSession(drag.Data));
                }

                Assert.Equal(Words("COMPOSER_REPLACE_EDITED", "Source tab", 7L), Assert.Single(questions));
                Assert.Equal(RequestComposerWindow.ComposerMode.Raw, window.Mode);
                Assert.Equal(head, Find<TextBox>(window, "RawHeadBox").Text);

                answer = true;
                using (ComposerSessionDragToken drag = ComposerSessionDrag.Begin(row, "Source tab"))
                {
                    Assert.True(window.DropSession(drag.Data));
                }

                Assert.Equal(2, questions.Count);
                Assert.Equal(RequestComposerWindow.ComposerMode.Parsed, window.Mode);
                Assert.Equal(Origin + "/dropped", Find<TextBox>(window, "UrlBox").Text);
                Assert.Equal(("", ""), (Find<TextBox>(window, "RawTargetBox").Text, Find<TextBox>(window, "RawHeadBox").Text));
                Assert.Equal(ReplayHttpVersion.Auto, SelectedVersion(window));
                Assert.True(Find<ComboBox>(window, "HttpVersionBox").IsEnabled);
            }
            finally
            {
                window.Close();
            }
        });

        // The controls of Raw mode: the options that it never uses are disabled, each with its reason, also while
        // disabled; Connect to another endpoint, Inspect session and the body's own buttons stay; Fix Content-Length shows.
        // Parsed mode's controls come back with it. The cURL exports, and Save request as, are refused in Raw mode in their
        // own tests (CurlExportUiTests, RequestFileUiTests).
        [Fact]
        public Task TheControls_FollowRawModesRules_AndComeBackWithParsedMode() => _application.RunAsync(async () =>
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            try
            {
                ChooseVersion(window, ReplayHttpVersion.Http11);
                await EnterRawModeAsync(window, "GET");

                (string Name, string Reason)[] off =
                {
                    ("HttpVersionBox", "COMPOSER_RAW_OFF_HTTP_VERSION"),
                    ("FollowRedirectsBox", "COMPOSER_RAW_OFF_REDIRECTS"),
                    ("RedirectLimitBox", "COMPOSER_RAW_OFF_REDIRECTS"),
                    ("ProxyBox", "COMPOSER_RAW_OFF_PROXY"),
                    ("ProxyText", "COMPOSER_RAW_OFF_PROXY"),
                    ("ClientCertificateBox", "COMPOSER_RAW_OFF_CLIENT_CERT"),
                    ("ClientCertificateList", "COMPOSER_RAW_OFF_CLIENT_CERT"),
                    ("WindowsAuthBox", "COMPOSER_RAW_OFF_WINDOWS_AUTH"),
                    ("BuildFormButton", "COMPOSER_RAW_OFF_BUILD_FORM"),
                    ("SaveRequestButton", "COMPOSER_SAVE_RAW"),
                };
                foreach ((string name, string reason) in off)
                {
                    FrameworkElement element = Element(window, name);
                    Assert.False(element.IsEnabled, name);
                    Assert.Equal(Words(reason), element.ToolTip);
                    Assert.True(ToolTipService.GetShowOnDisabled(element), name);
                }

                foreach (string name in new[]
                    { "ConnectToBox", "InspectSessionBox", "UploadFileButton", "ClearBodyButton", "Base64Box", "ModeButton" })
                {
                    Assert.True(Element(window, name).IsEnabled, name);
                }

                Assert.Equal(Visibility.Visible, Element(window, "FixContentLengthButton").Visibility);

                // Work that makes the window busy for a while leaves Save request as disabled in Raw mode.
                await window.FixContentLengthAsync();
                Assert.False(Element(window, "SaveRequestButton").IsEnabled);

                await window.SwitchModeAsync();

                Assert.Equal(RequestComposerWindow.ComposerMode.Parsed, window.Mode);
                foreach (string name in new[]
                    {
                        "HttpVersionBox", "FollowRedirectsBox", "ProxyBox", "ClientCertificateBox", "WindowsAuthBox",
                        "BuildFormButton", "SaveRequestButton",
                    })
                {
                    Assert.True(Element(window, name).IsEnabled, name);
                }

                Assert.False(Element(window, "RedirectLimitBox").IsEnabled);
                Assert.Equal(Words("COMPOSER_FOLLOW_REDIRECTS_HINT"), Element(window, "FollowRedirectsBox").ToolTip);
                Assert.Equal(Words("COMPOSER_BUILD_FORM_HINT"), Element(window, "BuildFormButton").ToolTip);
                Assert.Equal(Words("COMPOSER_SAVE_REQUEST_HINT"), Element(window, "SaveRequestButton").ToolTip);
                Assert.Equal(Visibility.Collapsed, Element(window, "FixContentLengthButton").Visibility);
            }
            finally
            {
                window.Close();
            }
        });

        // In Raw mode, Upload File, Clear body and Base64 change only the body, never the head: each says that Content-Length
        // is unchanged.
        [Fact]
        public Task TheBodysOwnControls_SayThatContentLengthIsUnchanged() => _application.RunAsync(async () =>
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            try
            {
                await EnterRawModeAsync(window, "POST");
                string head = Find<TextBox>(window, "RawHeadBox").Text;
                string unchanged = Words("COMPOSER_RAW_LENGTH_UNCHANGED");
                window.ChooseBodyFile = () => Path.Join(AppContext.BaseDirectory, "body.bin");
                window.ReadBodyFile = (_, _) => Task.FromResult(new ReplayBodyRead(new byte[] { 1, 2, 3 }, null));

                await window.UploadBodyAsync();
                Assert.Equal(Words("COMPOSER_UPLOADED", "body.bin", 3L) + " " + unchanged, Status(window));

                window.ClearBody();
                Assert.Equal(unchanged, Status(window));

                // The status that Clear body left is replaced first, so that only the Base64 toggle's own words can pass.
                Find<TextBox>(window, "BodyBox").Text = "text";
                Find<TextBlock>(window, "StatusText").Text = "-";
                Find<CheckBox>(window, "Base64Box").IsChecked = true;
                Assert.Equal(unchanged, Status(window));
                Assert.Equal("dGV4dA==", Find<TextBox>(window, "BodyBox").Text);
                Assert.Equal(head, Find<TextBox>(window, "RawHeadBox").Text);
            }
            finally
            {
                window.Close();
            }
        });

        // The counts line under the head says what is sent, after every edit, without being announced: the head's bytes and
        // field lines against Raw mode's limits, the line breaks typed as LF that are sent as CRLF, the body's bytes and a
        // text body's line breaks by kind, which are sent as they are; that the head is over a limit, also by its length
        // alone; characters outside ASCII; a body that is not valid; a held body by its length; and a body of more than 64 Ki
        // characters as one that is counted later.
        [Fact]
        public Task TheCountsLine_SaysWhatIsSent() => _application.RunAsync(async () =>
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            try
            {
                await EnterRawModeAsync(window, "POST");
                var head = Find<TextBox>(window, "RawHeadBox");
                var body = Find<TextBox>(window, "BodyBox");
                var counts = Find<TextBlock>(window, "RawCountsText");

                head.Text = "GET /x HTTP/1.1\nHost: 127.0.0.1:9\n";
                body.Text = "";
                await SettleAsync();
                RawHeadCounts counted = RawRequest.CountHead(head.Text);
                Assert.Equal((38L, 1, 2), (counted.HeadBytes, counted.FieldLines, counted.LineFeedsToConvert));
                Assert.Equal(
                    Words(
                        "COMPOSER_RAW_COUNTS_LF", 38L, RawRequest.MaxHeadBytes, 1, RawRequest.MaxFieldLines, 2,
                        Words("COMPOSER_RAW_COUNTS_BODY", 0L)),
                    counts.Text);

                head.Text = "GET /caf\u00e9 HTTP/1.1\r\nHost: 127.0.0.1:9";
                body.Text = "a\r\nb\nc";
                await SettleAsync();
                counted = RawRequest.CountHead(head.Text);
                Assert.Equal(
                    Words(
                        "COMPOSER_RAW_COUNTS", counted.HeadBytes, RawRequest.MaxHeadBytes, 1, RawRequest.MaxFieldLines,
                        Words("COMPOSER_RAW_COUNTS_BODY", 6L) + " " + Words("COMPOSER_RAW_BODY_BREAKS", 1, 1, 0)) +
                        " " + Words("COMPOSER_RAW_COUNTS_NON_ASCII"),
                    counts.Text);

                string over = Words(
                    "COMPOSER_RAW_COUNTS_OVER", RawRequest.MaxHeadBytes, RawRequest.MaxRequestLineBytes, RawRequest.MaxFieldLines);
                head.Text = "GET /x HTTP/1.1" + string.Concat(Enumerable.Repeat("\r\nX-Field: 1", RawRequest.MaxFieldLines + 1));
                await SettleAsync();
                Assert.Equal(over, counts.Text);
                head.Text = new string('a', RawRequest.MaxHeadBytes + 1);
                await SettleAsync();
                Assert.Equal(over, counts.Text);

                head.Text = "GET /x HTTP/1.1\r\nHost: 127.0.0.1:9";
                Find<CheckBox>(window, "Base64Box").IsChecked = true;
                body.Text = "%%%";
                await SettleAsync();
                Assert.EndsWith(Words("COMPOSER_RAW_COUNTS_BODY_INVALID"), counts.Text, StringComparison.Ordinal);

                body.Text = new string('a', 64 * 1024 + 4);
                await SettleAsync();
                Assert.EndsWith(Words("COMPOSER_RAW_COUNTS_BODY_LATER"), counts.Text, StringComparison.Ordinal);

                window.ChooseBodyFile = () => Path.Join(AppContext.BaseDirectory, "held.bin");
                window.ReadBodyFile = (_, _) => Task.FromResult(new ReplayBodyRead(new byte[70_000], null));
                await window.UploadBodyAsync();
                await SettleAsync();
                Assert.EndsWith(Words("COMPOSER_RAW_COUNTS_BODY", 70_000L), counts.Text, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });

        // A body of 8 Mi characters, text or Base64, is never counted on the dispatcher: Edit as raw text, Fix
        // Content-Length and Edit as parsed each count it off the UI thread, while the dispatcher runs a probe that the test
        // queues once the work has begun; the counts line says that such a body is counted later.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public Task ALargeBody_IsNeverCountedOnTheDispatcher(bool base64) => _application.RunAsync(async () =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            var counts = new List<(bool OnDispatcher, bool Probed)>();
            using var probed = new ManualResetEventSlim();
            var window = RequestComposerWindow.ForNewRequest(confirm: Answer(new List<string>(), true));
            window.CountBody = (draft, token) =>
            {
                bool onDispatcher = dispatcher.CheckAccess();
                bool ran = probed.Wait(onDispatcher ? TimeSpan.FromSeconds(1) : Patience);
                lock (counts)
                {
                    counts.Add((onDispatcher, ran));
                }

                return draft.TryCountBody(token);
            };
            try
            {
                Find<TextBox>(window, "MethodBox").Text = "POST";
                Find<TextBox>(window, "UrlBox").Text = Origin + "/items";
                Find<CheckBox>(window, "Base64Box").IsChecked = base64;
                Find<TextBox>(window, "BodyBox").Text = base64
                    ? string.Concat(Enumerable.Repeat("QUJD", 2 * 1024 * 1024))
                    : new string('a', 8 * 1024 * 1024);
                long length = base64 ? 6 * 1024 * 1024 : 8 * 1024 * 1024;

                await ProbedAsync(dispatcher, probed, window.SwitchModeAsync);
                Assert.Equal(RequestComposerWindow.ComposerMode.Raw, window.Mode);
                await SettleAsync();
                Assert.EndsWith(
                    Words("COMPOSER_RAW_COUNTS_BODY_LATER"),
                    Find<TextBlock>(window, "RawCountsText").Text,
                    StringComparison.Ordinal);

                await ProbedAsync(dispatcher, probed, window.FixContentLengthAsync);
                Assert.Equal(Words("COMPOSER_RAW_FIXED_LENGTH", length), Status(window));

                await ProbedAsync(dispatcher, probed, window.SwitchModeAsync);
                Assert.Equal(RequestComposerWindow.ComposerMode.Parsed, window.Mode);
                Assert.Equal(new[] { (false, true), (false, true), (false, true) }, counts);
            }
            finally
            {
                window.Close();
            }
        });

        // Cancel request while a large body is being counted stops the conversion, whose count sees the cancel, and changes
        // nothing.
        [Fact]
        public Task CancelWhileTheBodyIsCounted_ChangesNothing() => _application.RunAsync(async () =>
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            using var counting = new ManualResetEventSlim();
            bool cancelSeen = false;
            window.CountBody = (draft, token) =>
            {
                counting.Set();
                cancelSeen = token.WaitHandle.WaitOne(Patience);
                return draft.TryCountBody(token);
            };
            try
            {
                Find<TextBox>(window, "UrlBox").Text = Origin + "/items";
                Find<TextBox>(window, "BodyBox").Text = new string('a', 8 * 1024 * 1024);
                (string, string, string, string) editors = Editors(window);

                Task conversion = window.SwitchModeAsync();
                await Task.Run(() => Assert.True(counting.Wait(Patience)));
                window.CancelSend();
                await conversion;

                Assert.True(cancelSeen);
                Assert.Equal(Words("COMPOSER_CONVERT_CANCELLED"), Status(window));
                Assert.Equal(RequestComposerWindow.ComposerMode.Parsed, window.Mode);
                Assert.Equal(editors, Editors(window));
            }
            finally
            {
                window.Close();
            }
        });

        // A body that is not valid as its editor reads it is refused by every path that counts it, each in its own words, and
        // nothing changes: Base64 with a character outside its alphabet, with '=' before its end or with a last group of
        // fewer than four characters, and text with half of a surrogate pair.
        [Theory]
        [InlineData("outside the alphabet")]
        [InlineData("padding before the end")]
        [InlineData("short last group")]
        [InlineData("half of a surrogate pair")]
        public Task AnInvalidBody_IsRefusedInEachPathsOwnWords_AndChangesNothing(string invalid) =>
            _application.RunAsync(async () =>
            {
                (string text, bool base64) = invalid switch
                {
                    "outside the alphabet" => ("QUJD%", true),
                    "padding before the end" => ("QU=D", true),
                    "short last group" => ("QUJDQQ", true),
                    _ => ("a" + '\ud800' + "b", false),
                };
                var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
                try
                {
                    Find<TextBox>(window, "MethodBox").Text = "POST";
                    Find<TextBox>(window, "UrlBox").Text = Origin + "/items";
                    Find<CheckBox>(window, "Base64Box").IsChecked = base64;
                    var body = Find<TextBox>(window, "BodyBox");
                    body.Text = text;
                    (string, string, string, string) editors = Editors(window);

                    await window.SwitchModeAsync();

                    Assert.Equal(Words(base64 ? "COMPOSER_CONVERT_BODY_BASE64" : "COMPOSER_CONVERT_BODY_TEXT"), Status(window));
                    Assert.Equal(RequestComposerWindow.ComposerMode.Parsed, window.Mode);
                    Assert.Equal(editors, Editors(window));

                    body.Text = "";
                    await window.SwitchModeAsync();
                    Assert.Equal(RequestComposerWindow.ComposerMode.Raw, window.Mode);
                    body.Text = text;
                    string head = Find<TextBox>(window, "RawHeadBox").Text;

                    await window.FixContentLengthAsync();
                    Assert.Equal(Words(base64 ? "COMPOSER_RAW_FIX_BODY_BASE64" : "COMPOSER_RAW_FIX_BODY_TEXT"), Status(window));
                    Assert.Equal(head, Find<TextBox>(window, "RawHeadBox").Text);

                    await window.SwitchModeAsync();
                    Assert.Equal(Words(base64 ? "COMPOSER_CONVERT_BODY_BASE64" : "COMPOSER_CONVERT_BODY_TEXT"), Status(window));
                    Assert.Equal(RequestComposerWindow.ComposerMode.Raw, window.Mode);
                    Assert.Equal(head, Find<TextBox>(window, "RawHeadBox").Text);
                }
                finally
                {
                    window.Close();
                }
            });

        // A cancel while the body's bytes are built, after each slice in turn, stops Fix Content-Length, which leaves
        // the head as it was, and Edit as parsed, which leaves Raw mode and everything in it as they were.
        [Theory]
        [InlineData("fix", 0)]
        [InlineData("fix", 1)]
        [InlineData("fix", 2)]
        [InlineData("fix", 3)]
        [InlineData("parsed", 0)]
        [InlineData("parsed", 1)]
        [InlineData("parsed", 2)]
        [InlineData("parsed", 3)]
        public Task ACancelWhileTheBodyIsBuilt_ChangesNothing(string action, int slice) => _application.RunAsync(async () =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            try
            {
                ChooseVersion(window, ReplayHttpVersion.Http11);
                Find<TextBox>(window, "BodyBox").Text = "0123456789";
                await EnterRawModeAsync(window, "POST");
                var built = new List<int>();
                window.BodySliceChars = 3;
                window.AfterBodySlice = index =>
                {
                    built.Add(index);
                    if (index == slice)
                    {
                        dispatcher.Invoke(window.CancelSend);
                    }
                };
                string head = Find<TextBox>(window, "RawHeadBox").Text;
                Find<TextBox>(window, "RawHeadBox").Text =
                    head.Replace("Content-Length: 10", "Content-Length: 1", StringComparison.Ordinal);
                head = Find<TextBox>(window, "RawHeadBox").Text;

                if (action == "fix")
                {
                    await window.FixContentLengthAsync();
                    Assert.Equal(Words("COMPOSER_RAW_LENGTH_UNCHANGED"), Status(window));
                }
                else
                {
                    await window.SwitchModeAsync();
                    Assert.Equal(Words("COMPOSER_CONVERT_CANCELLED"), Status(window));
                }

                Assert.Equal(Enumerable.Range(0, slice + 1), built);
                Assert.Equal(RequestComposerWindow.ComposerMode.Raw, window.Mode);
                Assert.Equal(head, Find<TextBox>(window, "RawHeadBox").Text);
                Assert.Equal("0123456789", Find<TextBox>(window, "BodyBox").Text);
            }
            finally
            {
                window.Close();
            }
        });

        // Fix Content-Length sets only the value of the one Content-Length field, keeping its name, the spaces and tabs
        // around the value and the head's line breaks, to the body's length in bytes: of text as UTF-8, of Base64 as decoded,
        // and of a held body, which is checked as it is held, never copied. It then checks the whole request again, as a send
        // would, and says what is still wrong, with an invalid target origin and a held body over Raw mode's limit said
        // without preparing anything; no image outlives that check.
        [Fact]
        public Task FixContentLength_SetsOnlyTheValue_AndChecksTheRequestAgain() => _application.RunAsync(async () =>
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            var images = new List<WeakReference>();
            var bodies = new List<ReadOnlyMemory<byte>>();
            window.PrepareRaw = (headText, body, target, token, limits) =>
            {
                RawImageResult prepared = RawRequest.Prepare(headText, body, target, token, limits);
                images.Add(new WeakReference(prepared.Image));
                bodies.Add(body);
                return prepared;
            };
            try
            {
                await EnterRawModeAsync(window, "POST");
                var head = Find<TextBox>(window, "RawHeadBox");
                var body = Find<TextBox>(window, "BodyBox");
                const string Typed = "POST /x HTTP/1.1\nHost: 127.0.0.1:9\ncontent-length:\t 3  \nX-A: b";

                head.Text = Typed;
                body.Text = "h\u00e9llo";
                await window.FixContentLengthAsync();
                Assert.Equal(Typed.Replace("\t 3  ", "\t 6  ", StringComparison.Ordinal), head.Text);
                Assert.Equal(Words("COMPOSER_RAW_FIXED_LENGTH", 6L), Status(window));

                head.Text = Typed;
                Find<CheckBox>(window, "Base64Box").IsChecked = true;
                body.Text = "QUJD";
                await window.FixContentLengthAsync();
                Assert.Equal(Typed, head.Text);
                Assert.Equal(Words("COMPOSER_RAW_FIXED_LENGTH", 3L), Status(window));

                byte[] held = new byte[70_000];
                window.ChooseBodyFile = () => Path.Join(AppContext.BaseDirectory, "held.bin");
                window.ReadBodyFile = (_, _) => Task.FromResult(new ReplayBodyRead(held, null));
                await window.UploadBodyAsync();
                await window.FixContentLengthAsync();
                Assert.Equal(Typed.Replace("\t 3  ", "\t 70000  ", StringComparison.Ordinal), head.Text);
                Assert.Equal(Words("COMPOSER_RAW_FIXED_LENGTH", 70_000L), Status(window));
                Assert.True(MemoryMarshal.TryGetArray(bodies[^1], out ArraySegment<byte> segment));
                Assert.Same(held, segment.Array);

                head.Text = Typed + "\nExpect: 100-continue";
                await window.FixContentLengthAsync();
                Assert.True(ReplayRawTarget.TryParse(Origin, out ReplayRawTarget origin));
                RawImageResult remaining = RawRequest.Prepare(head.Text, held, origin, CancellationToken.None);
                Assert.Equal(RawCodes.Expect, remaining.Code);
                Assert.Equal(
                    Words(
                        "COMPOSER_RAW_FIXED_LENGTH_INVALID",
                        70_000L,
                        Words("COMPOSER_RAW_EXPECT") + " " + Words("COMPOSER_RAW_AT_LINE", remaining.Line.Value)),
                    Status(window));

                head.Text = Typed;
                Find<TextBox>(window, "RawTargetBox").Text = Origin + "/x";
                int prepares = bodies.Count;
                await window.FixContentLengthAsync();
                Assert.Equal(
                    Words("COMPOSER_RAW_FIXED_LENGTH_INVALID", 70_000L, Words("COMPOSER_RAW_ORIGIN")), Status(window));
                Assert.Equal(prepares, bodies.Count);

                // A held body over Raw mode's limit is reported without being prepared.
                byte[] over = new byte[RawRequest.MaxEntityBytes + RawRequest.MaxChunkFramingBytes + 1];
                window.ReadBodyFile = (_, _) => Task.FromResult(new ReplayBodyRead(over, null));
                await window.UploadBodyAsync();
                head.Text = Typed;
                Find<TextBox>(window, "RawTargetBox").Text = Origin;
                await window.FixContentLengthAsync();
                Assert.Equal(
                    Words(
                        "COMPOSER_RAW_FIXED_LENGTH_INVALID", over.LongLength, RawSendWording.CodeText(RawCodes.InputTooLarge)),
                    Status(window));
                Assert.Equal(prepares, bodies.Count);

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Assert.DoesNotContain(images, image => image.IsAlive);
            }
            finally
            {
                window.Close();
            }
        });

        // Fix Content-Length needs exactly one Content-Length field and no Transfer-Encoding: otherwise it changes nothing,
        // and says so in its code's words.
        [Theory]
        [InlineData("POST /x HTTP/1.1\r\nHost: 127.0.0.1:9")]
        [InlineData("POST /x HTTP/1.1\r\nHost: 127.0.0.1:9\r\nContent-Length: 1\r\nContent-Length: 2")]
        [InlineData("POST /x HTTP/1.1\r\nHost: 127.0.0.1:9\r\nContent-Length: 1\r\nTransfer-Encoding: chunked")]
        public Task FixContentLength_RefusesWithoutExactlyOneContentLength(string typed) => _application.RunAsync(async () =>
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            try
            {
                await EnterRawModeAsync(window, "POST");
                Find<TextBox>(window, "RawHeadBox").Text = typed;
                Find<TextBox>(window, "BodyBox").Text = "hello";

                await window.FixContentLengthAsync();

                Assert.Equal(Words("COMPOSER_RAW_FIX_CONTENT_LENGTH"), Status(window));
                Assert.Equal(typed, Find<TextBox>(window, "RawHeadBox").Text);
            }
            finally
            {
                window.Close();
            }
        });

        // The fixed conversion messages, word for word: the URL's length and the candidate field
        // names that preparation allows.
        [Fact]
        public void TheConversionTexts_TakeTheDeltasWords()
        {
            Assert.Equal(
                "Not converted: enter an absolute HTTP or HTTPS URL of at most 65,536 characters, without a user name or " +
                    "fragment, first.",
                Words("COMPOSER_RAW_CONVERT_URL"));
            Assert.Equal(
                "Not converted: Raw mode allows a head of at most 64 KiB, a request line of at most 16 KiB, 256 field lines, " +
                    "a method of at most 64 characters and a body of at most 64 MiB. Preparation also allows at most 1,024 " +
                    "distinct candidate field names totaling 256 KiB, before Connection nominations are applied.",
                Words("COMPOSER_RAW_CONVERT_LIMITS"));
        }

        // Enters Raw mode as the user does, from a request to the loopback origin without headers, which loses nothing.
        private static async Task EnterRawModeAsync(RequestComposerWindow window, string method)
        {
            Find<TextBox>(window, "MethodBox").Text = method;
            Find<TextBox>(window, "UrlBox").Text = Origin + "/x";
            Find<TextBox>(window, "HeadersBox").Text = "";
            await window.SwitchModeAsync();
            Assert.Equal(RequestComposerWindow.ComposerMode.Raw, window.Mode);
        }

        // Starts the window's work, which must count the body (CountBody), then queues the probe that the count waits for: a
        // count on the dispatcher cannot see it run.
        private static async Task ProbedAsync(Dispatcher dispatcher, ManualResetEventSlim probed, Func<Task> work)
        {
            probed.Reset();
            Task running = work();
            _ = dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(probed.Set));
            await running.WaitAsync(Patience * 3);
        }

        // The access keys of the elements that the window shows: each label's, button's and check box's underlined character,
        // in upper case. The walk starts below the window, which stays collapsed as it is never shown here.
        private static IReadOnlyList<char> AccessKeys(Window window)
        {
            var keys = new List<char>();
            var pending = new Stack<DependencyObject>();
            pending.Push((DependencyObject)window.Content);
            while (pending.Count > 0)
            {
                DependencyObject element = pending.Pop();
                if (element is UIElement { Visibility: not Visibility.Visible })
                {
                    continue;
                }

                if (element is ContentControl { Content: string content } && AccessKey(content) is char key)
                {
                    keys.Add(char.ToUpperInvariant(key));
                }

                foreach (object child in LogicalTreeHelper.GetChildren(element))
                {
                    if (child is DependencyObject dependency)
                    {
                        pending.Push(dependency);
                    }
                }
            }

            return keys;
        }

        // The character after the first '_' that is not doubled, which WPF underlines; null for none.
        private static char? AccessKey(string text)
        {
            for (int index = 0; index + 1 < text.Length; index++)
            {
                if (text[index] != '_')
                {
                    continue;
                }

                if (text[index + 1] != '_')
                {
                    return text[index + 1];
                }

                index++;
            }

            return null;
        }

        private static string Words(string key, params object[] args) =>
            args.Length == 0 ? Localization.GetLocalizedString(key) : Localization.GetLocalizedString(key, args);

        // Records each question and gives the answer.
        private static Func<string, bool> Answer(List<string> questions, bool answer) => message =>
        {
            questions.Add(message);
            return answer;
        };

        private static async Task SettleAsync() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        private static T Find<T>(Window window, string name) where T : class =>
            Assert.IsType<T>(window.FindName(name));

        private static FrameworkElement Element(Window window, string name) =>
            Assert.IsAssignableFrom<FrameworkElement>(window.FindName(name));

        private static string Status(Window window) => Find<TextBlock>(window, "StatusText").Text;

        // The method, URL, headers and body as the Parsed editors show them.
        private static (string, string, string, string) Editors(Window window) => (
            Find<TextBox>(window, "MethodBox").Text,
            Find<TextBox>(window, "UrlBox").Text,
            Find<TextBox>(window, "HeadersBox").Text,
            Find<TextBox>(window, "BodyBox").Text);

        // The HTTP version that the selector shows.
        private static ReplayHttpVersion SelectedVersion(Window window) =>
            Assert.IsType<ReplayHttpVersion>(Assert.IsType<ComboBoxItem>(Find<ComboBox>(window, "HttpVersionBox").SelectedItem).Tag);

        // Chooses an HTTP version as the user does.
        private static void ChooseVersion(Window window, ReplayHttpVersion version)
        {
            var box = Find<ComboBox>(window, "HttpVersionBox");
            box.SelectedItem = box.Items.Cast<ComboBoxItem>().Single(item => version.Equals(item.Tag));
        }

        private static string FixtureDirectory()
        {
            string directory = Path.Join(AppContext.BaseDirectory, "ComposerRawModeFixtures", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }
    }
}
