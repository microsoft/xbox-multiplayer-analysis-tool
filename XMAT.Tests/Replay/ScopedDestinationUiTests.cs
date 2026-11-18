// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using XMAT.DataAbstractionLayer;
using XMAT.Models;
using XMAT.Operations;
using XMAT.Operations.Analysis;
using XMAT.Operations.Replay;
using XMAT.SharedInterfaces;
using XMAT.Tests.Fixtures;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Replay
{
    // The GUI's half of refusing IPv6 zone identifiers: XMAT sends nothing to an IPv6 address with a zone, and exports
    // no cURL command for one. The composer's Send, a numbered URL's before its numbers are asked for, and both cURL exports
    // refuse one before anything is asked, sent, copied, chosen or written, after the refusal of stored text from an
    // unmarked capture, in the words of the shared diagnostic (replay.destination.scoped-ipv6), and leave the request as it
    // was; a redirect to one is not followed, and the status says why; Reissue refuses a selection in which any request
    // would go to one, whole and before its question, after the refusal of stored text from an unmarked capture, and so it
    // refuses, first and in its own words, one in which any request's destination is malformed or exceeds the size limit
    // of the shared destination decision (replay.request.malformed). A request keeps its zone as data: a captured one keeps
    // it in the editors, and replacing its URL with another destination sends there after the usual question about the
    // origin, recorded as edited from the captured one. Requests go to loopback servers only, and each test records into
    // its own application model.
    [Collection(CaptureUiApplicationCollection.Name)]
    public sealed class ScopedDestinationUiTests : IDisposable
    {
        private const string SourceTab = "Source tab";

        private const string Scoped = "http://[fe80::1%12]:9/scoped";

        private static readonly Func<string, bool> NoQuestion =
            message => throw new Xunit.Sdk.XunitException("Nothing needs confirmation here: " + message);

        private readonly CaptureUiApplicationFixture _application;

        private readonly string _directory = Path.Join(
            AppContext.BaseDirectory, "HeadlessTestArtifacts", "ScopedDestinationUi", Guid.NewGuid().ToString("N"));

        public ScopedDestinationUiTests(CaptureUiApplicationFixture application)
        {
            _application = application;
        }

        static ScopedDestinationUiTests()
        {
            Localization.LoadLanguage("en-us");
        }

        public void Dispose() => TestFolderCleanup.Delete(_directory);

        // Send of a captured request to an IPv6 address with a zone is refused before anything is asked or sent, in the
        // shared diagnostic's words, which en-us gives word for word, and leaves the editors as they were, the zone in the
        // URL included. Replacing the URL with another destination is an edit like any other: Send asks about the new
        // origin, sends there, and the row keeps the captured origin, zone and all.
        [Fact]
        public Task Send_ToAScopedDestination_IsRefused_AndAnotherDestinationSendsAfterTheOriginQuestion() =>
            _application.RunAsync(async () =>
            {
                await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
                using var client = new RequestReplayClient();
                var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
                var questions = new List<string>();
                var window = new RequestComposerWindow(
                    Capture(1, "[fe80::1%12]:9", "GET", "/scoped"), client, Answer(questions, true), Recording(model),
                    SourceTab);
                try
                {
                    string before = Request(window);
                    Assert.Equal(Scoped, Find<TextBox>(window, "UrlBox").Text);

                    await window.SendAsync();

                    Assert.Equal(Words("COMPOSER_ERROR", Words("REPLAY_DESTINATION_SCOPED_IPV6")), Status(window));
                    Assert.Equal(Words("COMPOSER_ERROR", ReplayDestination.ScopedIpv6Message), Status(window));
                    Assert.Empty(questions);
                    Assert.Equal(before, Request(window));
                    Assert.Null(window.LastResult);
                    Assert.Empty(model.CaptureDeviceContexts);

                    Find<TextBox>(window, "UrlBox").Text = server.Origin + "/edited";
                    await window.SendAsync();

                    Assert.Equal(new[] { Words("COMPOSER_CONFIRM_MESSAGE", "GET", server.Origin + "/edited") }, questions);
                    Assert.Equal("/edited", Assert.Single(server.Requests).Target);
                    ProxyConnectionModel sent = Assert.Single(Replays(model).ProxyConnections);
                    Assert.Equal(ReplaySourceKind.Edited, sent.Replay.Kind);
                    Assert.Equal(Scoped, sent.Replay.Origin.OriginalString);
                }
                finally
                {
                    window.Close();
                    await RemoveAllAsync(model);
                }
            });

        // A numbered URL: the refusals that no number changes come before its numbers are asked for, in the order
        // that a single send gives them, stored text from an unmarked capture before a destination with an IPv6 zone, and
        // in the same words; nothing is chosen, asked or sent.
        [Fact]
        public Task NumberedSend_IsRefusedBeforeItsNumbersAreAskedFor_StoredTextFirst() => _application.RunAsync(async () =>
        {
            const string NumberedScoped = "http://[fe80::1%12]:9/n/#";
            Func<string> noNumbers = () => throw new Xunit.Sdk.XunitException("No numbers are asked for here.");
            var composed = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            var unmarked = new RequestComposerWindow(
                Unmarked(new CapturedExchange("GET", "http://127.0.0.1:9/item")), confirm: NoQuestion);
            composed.ChooseSequence = noNumbers;
            unmarked.ChooseSequence = noNumbers;
            try
            {
                Find<TextBox>(composed, "UrlBox").Text = NumberedScoped;
                Find<TextBox>(unmarked, "UrlBox").Text = NumberedScoped;

                await composed.SendAsync();
                await unmarked.SendAsync();

                Assert.Equal(Words("COMPOSER_ERROR", Words("REPLAY_DESTINATION_SCOPED_IPV6")), Status(composed));
                Assert.Equal(Words("COMPOSER_ERROR", Words("REPLAY_CAPTURE_TEXT_AMBIGUOUS")), Status(unmarked));
                Assert.Null(composed.LastResult);
                Assert.Null(unmarked.LastResult);
            }
            finally
            {
                composed.Close();
                unmarked.Close();
            }
        });

        // Why the operation refused a run once it was sent, for a destination with an IPv6 zone, is said in the composer's
        // words for the code, never in the operation's message.
        [Fact]
        public void TheOperationsScopedRefusal_IsWordedByTheComposer()
        {
            var plan = new ReplayPlanPreview(new RequestReplayDraft { Url = Scoped }, null, 1, false, false);
            var diagnostic = new OperationDiagnostic(
                ReplayDestination.ScopedIpv6Code, OperationDiagnosticSeverity.Error, "The operation's own words.");

            Assert.Equal(
                Words("COMPOSER_ERROR", Words("REPLAY_DESTINATION_SCOPED_IPV6")),
                RequestComposerWindow.RunRefusalText(diagnostic, plan));
        }

        // Copy as cURL and Save as cURL script refuse a destination with an IPv6 zone before anything is asked, copied,
        // chosen or written, also for a URL that goes to another origin than the request was captured for, which they ask
        // about only once the request is prepared; the editors stay as they were. They say why in the composer's words for
        // the exporter's code, never in its message.
        [Fact]
        public Task CurlExports_OfAScopedDestination_AreRefusedBeforeAnyQuestionOrOutput() => _application.RunAsync(async () =>
        {
            var calls = new List<string>();
            var window = new RequestComposerWindow(Capture(1, "127.0.0.1:9", "GET", "/item"), confirm: Answer(calls, true));
            window.CopyText = _ => calls.Add("copy");
            window.ChooseCurlScriptPath = () =>
            {
                calls.Add("path");
                return null;
            };
            try
            {
                Find<TextBox>(window, "UrlBox").Text = Scoped;
                string before = Request(window);
                string refused = Words("COMPOSER_CURL_INVALID", Words("REPLAY_DESTINATION_SCOPED_IPV6"));

                await window.CopyAsCurlAsync();
                string copied = Status(window);
                await window.SaveCurlScriptAsync();

                Assert.Empty(calls);
                Assert.Equal((refused, refused), (copied, Status(window)));
                Assert.Equal(Words("COMPOSER_CURL_INVALID", ReplayDestination.ScopedIpv6Message), refused);
                Assert.Equal(before, Request(window));

                window.PrepareCurl = (_, _, _, _) =>
                    throw new CurlExportException(ReplayDestination.ScopedIpv6Code, "The exporter's own words.");
                Find<TextBox>(window, "UrlBox").Text = "http://127.0.0.1:9/item";
                await window.CopyAsCurlAsync();
                copied = Status(window);
                await window.SaveCurlScriptAsync();

                Assert.Empty(calls);
                Assert.Equal((refused, refused), (copied, Status(window)));
            }
            finally
            {
                window.Close();
            }
        });

        // A redirect to an IPv6 address with a zone is not followed: the redirect is the result, and the status says why.
        [Fact]
        public Task ARedirectToAScopedDestination_IsNotFollowed_AndTheStatusSaysWhy() => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Redirect(302, "http://[fe80::1%12]/next"));
            using var client = new RequestReplayClient();
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var window = RequestComposerWindow.ForNewRequest(client, NoQuestion, Recording(model));
            try
            {
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/start";
                Find<CheckBox>(window, "FollowRedirectsBox").IsChecked = true;

                await window.SendAsync();

                Assert.Equal("/start", Assert.Single(server.Requests).Target);
                Assert.Equal("302", window.LastResult.Response.Status);
                Assert.Contains(Words("COMPOSER_REDIRECT_SCOPED_IPV6"), Status(window), StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // Reissue of a selection in which one request would go to an IPv6 address with a zone refuses the whole selection
        // before its question, and says why in the composer's words: nothing is sent and no Replays tab is added. A selection
        // that also has a request of an unmarked capture is refused for that first, as before. Without that request, the
        // same selection asks about its unsafe request and sends both.
        [Fact]
        public Task Reissue_OfASelectionWithAScopedDestination_IsRefusedWhole_BeforeItsQuestion() =>
            _application.RunAsync(async () =>
            {
                await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
                string authority = new Uri(server.Origin).Authority;
                ProxyConnectionModel[] rows =
                {
                    Capture(1, authority, "GET", "/read"),
                    Capture(2, authority, "POST", "/create", "body"u8.ToArray()),
                    Capture(3, "[fe80::1%12]:9", "GET", "/scoped"),
                };
                ProxyConnectionModel unmarked = Unmarked(new CapturedExchange("GET", server.Origin + "/unmarked"));
                var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
                CaptureConnectionView view = HostListUiTests.CaptureView(new HostListPolicy(), rows.Append(unmarked).ToArray());
                view.ReplayTarget = Recording(model);
                var messages = new List<(string Text, MessageBoxImage Image)>();
                view.ShowReplayMessage = (text, image) => messages.Add((text, image));
                var questions = new List<string>();
                view.ConfirmReplay = Answer(questions, true);
                try
                {
                    Assert.Null(await view.ReissueFromMenuAsync(new ReissueRequest(rows)));
                    Assert.Null(await view.ReissueFromMenuAsync(new ReissueRequest(new[] { unmarked, rows[2] })));

                    Assert.Empty(questions);
                    Assert.Empty(server.Requests);
                    Assert.Empty(model.CaptureDeviceContexts);
                    Assert.Equal(
                        new[]
                        {
                            (Words("REPLAY_FAILED", Words("REPLAY_DESTINATION_SCOPED_IPV6")), MessageBoxImage.Warning),
                            (Words("REPLAY_CAPTURE_TEXT_AMBIGUOUS"), MessageBoxImage.Warning),
                        },
                        messages);

                    ReplayRunResult run = await view.ReissueFromMenuAsync(new ReissueRequest(rows.Take(2).ToArray()));

                    Assert.True(run.Result.Succeeded);
                    Assert.Single(questions);
                    Assert.Equal(new[] { "/read", "/create" }, server.Requests.Select(request => request.Target));
                }
                finally
                {
                    await RemoveAllAsync(model);
                }
            });

        // Reissue refuses as scoped, before its question, each request that the run would send to an IPv6 address
        // with a zone, also where a look at the history's former bounds alone missed it: a target that only looks absolute,
        // which the run reads as a path at the row's own scoped host; a stored zone longer than 255 characters; and an
        // absolute target whose scoped authority is longer than 512. Each is a marked capture's request, as stored, with a
        // method that would otherwise be asked about.
        [Fact]
        public Task Reissue_OfEachScopedDestinationThatTheRunRefuses_IsRefusedBeforeItsQuestion() =>
            _application.RunAsync(async () =>
            {
                ProxyConnectionModel[] rows =
                {
                    Stored(1, "fe80::1%12", "", "POST", "http://b.test:bad/x"),
                    Stored(2, "fe80::1%" + new string('z', 300), "", "POST", "/x"),
                    Stored(3, "127.0.0.1", "9", "POST", "http://[fe80::1%" + new string('z', 600) + "]/x"),
                };
                var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
                CaptureConnectionView view = HostListUiTests.CaptureView(new HostListPolicy(), rows);
                view.ReplayTarget = Recording(model);
                var messages = new List<(string Text, MessageBoxImage Image)>();
                view.ShowReplayMessage = (text, image) => messages.Add((text, image));
                var questions = new List<string>();
                view.ConfirmReplay = Answer(questions, false);
                try
                {
                    foreach (ProxyConnectionModel row in rows)
                    {
                        Assert.Null(await view.ReissueFromMenuAsync(new ReissueRequest(new[] { row })));
                    }

                    Assert.Empty(questions);
                    Assert.Empty(model.CaptureDeviceContexts);
                    Assert.Equal(
                        Enumerable.Repeat(
                            (Words("REPLAY_FAILED", Words("REPLAY_DESTINATION_SCOPED_IPV6")), MessageBoxImage.Warning),
                            rows.Length).ToArray(),
                        messages);
                }
                finally
                {
                    await RemoveAllAsync(model);
                }
            });

        // A selection in which any request that Reissue would send has a destination that is malformed or exceeds the size
        // limit is refused whole, before its question, in its own words, which en-us gives word for word, never in the
        // zone's: a target whose scheme and authority exceed 8,192 characters, which is neither read further for a zone nor
        // set aside for the row's own host; a stored host over the limit; and a stored port that is no port. That refusal
        // comes before a zone's in the same selection, as in the run, and after the refusal of stored text from an unmarked
        // capture. A request that Reissue skips, a CONNECT tunnel, is no destination however long its host: the rest of that
        // selection asks nothing and is sent.
        [Fact]
        public Task Reissue_OfAMalformedOrOverLimitDestination_IsRefusedInItsOwnWords_BeforeItsQuestion() =>
            _application.RunAsync(async () =>
            {
                await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
                string overLimit = new string('a', 9000);
                ProxyConnectionModel[] malformed =
                {
                    Stored(1, "127.0.0.1", "9", "POST", "http://" + overLimit + "/x"),
                    Stored(2, overLimit, "", "POST", "/x"),
                    Stored(3, "127.0.0.1", "x", "POST", "/x"),
                };
                ProxyConnectionModel scoped = Capture(4, "[fe80::1%12]:9", "GET", "/scoped");
                ProxyConnectionModel unmarked = Unmarked(new CapturedExchange("GET", server.Origin + "/unmarked"));
                ProxyConnectionModel tunnel = Stored(5, overLimit, "443", "CONNECT", overLimit + ":443");
                ProxyConnectionModel read = Capture(6, new Uri(server.Origin).Authority, "GET", "/read");
                var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
                CaptureConnectionView view = HostListUiTests.CaptureView(
                    new HostListPolicy(), malformed.Concat(new[] { scoped, unmarked, tunnel, read }).ToArray());
                view.ReplayTarget = Recording(model);
                var messages = new List<(string Text, MessageBoxImage Image)>();
                view.ShowReplayMessage = (text, image) => messages.Add((text, image));
                var questions = new List<string>();
                view.ConfirmReplay = Answer(questions, false);
                var refused = (Words("REPLAY_FAILED", Words("REPLAY_DESTINATION_MALFORMED")), MessageBoxImage.Warning);
                try
                {
                    foreach (ProxyConnectionModel row in malformed)
                    {
                        Assert.Null(await view.ReissueFromMenuAsync(new ReissueRequest(new[] { row })));
                    }

                    Assert.Null(await view.ReissueFromMenuAsync(new ReissueRequest(new[] { scoped, malformed[0] })));
                    Assert.Null(await view.ReissueFromMenuAsync(new ReissueRequest(new[] { malformed[0], unmarked })));

                    Assert.Empty(questions);
                    Assert.Empty(server.Requests);
                    Assert.Empty(model.CaptureDeviceContexts);
                    Assert.Equal(
                        new[]
                        {
                            refused, refused, refused, refused,
                            (Words("REPLAY_CAPTURE_TEXT_AMBIGUOUS"), MessageBoxImage.Warning),
                        },
                        messages);
                    Assert.Equal(
                        "A selected request's destination is malformed or exceeds the supported size limit. " +
                            "Nothing was sent.",
                        Words("REPLAY_DESTINATION_MALFORMED"));

                    view.ConfirmReplay = NoQuestion;
                    ReplayRunResult run = await view.ReissueFromMenuAsync(new ReissueRequest(new[] { tunnel, read }));

                    Assert.True(run.Result.Succeeded);
                    Assert.Equal("/read", Assert.Single(server.Requests).Target);
                }
                finally
                {
                    await RemoveAllAsync(model);
                }
            });

        // A request as a live capture records it, without a Host line: the URL is the row's own, at authority, which keeps
        // an IPv6 address's zone.
        private static ProxyConnectionModel Capture(long number, string authority, string method, string path, byte[] body = null) =>
            new(number, "200", DateTime.UtcNow, TimeSpan.Zero, "http", authority, method, path,
                $"{method} {path} HTTP/1.1\r\n", body ?? [], "HTTP/1.1 200 OK\r\n", [], "127.0.0.1");

        // A request of a marked capture as XMAT reads its row (ProxyConnectionModel(IDataRecord)), its text literal: the
        // host, port and target as the capture stores them, which no capture's own reading of an authority has checked.
        private static ProxyConnectionModel Stored(long number, string host, string port, string method, string target)
        {
            DateTime now = DateTime.UtcNow;
            var row = new ProxyConnectionModel(new MarkedRecord(number, new Dictionary<string, string>
            {
                [WebServiceCaptureMethod.FieldKey_RequestTimestamp] = ProxyConnectionModel.ToDateTimeString(now),
                [WebServiceCaptureMethod.FieldKey_ResponseTimestamp] = ProxyConnectionModel.ToDateTimeString(now),
                [WebServiceCaptureMethod.FieldKey_RequestScheme] = "http",
                [WebServiceCaptureMethod.FieldKey_RequestHost] = host,
                [WebServiceCaptureMethod.FieldKey_RequestPort] = port,
                [WebServiceCaptureMethod.FieldKey_RequestMethod] = method,
                [WebServiceCaptureMethod.FieldKey_RequestPath] = target,
                [WebServiceCaptureMethod.FieldKey_RequestStatus] = "200",
                [WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders] = $"{method} {target} HTTP/1.1\r\n",
                [WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders] = "HTTP/1.1 200 OK\r\n",
            }));
            Assert.Equal((host, port, target), (row.Host, row.Port ?? string.Empty, row.Path));
            Assert.Equal(CaptureTextSemantics.Literal, row.CaptureTextSource.Semantics);
            return row;
        }

        // A request of an XMAT capture without the text-format marker, as XMAT reads one.
        private ProxyConnectionModel Unmarked(CapturedExchange exchange)
        {
            string path = ReplayCaptures.WriteXmatcap(
                _directory, Guid.NewGuid().ToString("N") + ".xmatcap", ("Device 1", new[] { exchange }));
            HostileSqliteSchemas.SetUserVersion(path, CaptureTextFormat.AmbiguousUserVersion);
            OperationResult<CaptureRecordSet> read = new AnalyzeCaptureOperation().ReadCapture(path, null, CancellationToken.None);
            Assert.True(read.Succeeded);
            ProxyConnectionModel row = Assert.Single(
                Assert.Single(read.Value.Devices).Records.Select(record => new ProxyConnectionModel(record)));
            Assert.Equal(CaptureTextSemantics.AmbiguousUnmarked, row.CaptureTextSource.Semantics);
            return row;
        }

        private static string Words(string key, params object[] args) => Localization.GetLocalizedString(key, args);

        // Records each question and gives the answer.
        private static Func<string, bool> Answer(List<string> questions, bool answer) => message =>
        {
            questions.Add(message);
            return answer;
        };

        private static T Find<T>(Window window, string name) where T : class =>
            Assert.IsType<T>(window.FindName(name));

        private static string Status(Window window) => Find<TextBlock>(window, "StatusText").Text;

        // The request as the editors show it.
        private static string Request(Window window) => string.Join(
            "|",
            Find<TextBox>(window, "MethodBox").Text,
            Find<TextBox>(window, "UrlBox").Text,
            Find<TextBox>(window, "HeadersBox").Text,
            Find<TextBox>(window, "BodyBox").Text);

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

        // A row of a marked capture, as its table gives it, with only these fields.
        private sealed class MarkedRecord(long number, Dictionary<string, string> values)
            : IDataRecord, ICaptureTextSourceProvider
        {
            public int RowId => (int)number;

            public CaptureTextSource CaptureTextSource => global::XMAT.DataAbstractionLayer.CaptureTextSource.Literal;

            public long Int(string fieldName) => fieldName == WebServiceCaptureMethod.FieldKey_RequestNumber ? number : 0;

            public string Str(string fieldName) => values.TryGetValue(fieldName, out string value) ? value : null;
        }
    }
}
