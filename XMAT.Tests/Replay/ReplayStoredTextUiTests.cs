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
    // Interim GUI refusals for stored text: until the GUI asks for a decision bound to a capture's own file, the
    // requests of an XMAT capture without the text-format marker are not opened, dropped, reissued, saved or exported,
    // in words that name only a marked copy, and nothing else happens. Requests of a live capture, a Fiddler archive, a
    // marked capture and the Replays tab work as before, and History Load of a Replays row is never refused. Requests
    // target loopback servers only, and each test records into its own application model.
    [Collection(CaptureUiApplicationCollection.Name)]
    public sealed class ReplayStoredTextUiTests : IDisposable
    {
        private const string SourceTab = "Source tab";

        private static readonly Func<string, bool> NoQuestion =
            message => throw new Xunit.Sdk.XunitException("Nothing needs confirmation here: " + message);

        private readonly CaptureUiApplicationFixture _application;

        private readonly string _directory = Path.Join(
            AppContext.BaseDirectory, "HeadlessTestArtifacts", "ReplayStoredTextUi", Guid.NewGuid().ToString("N"));

        public ReplayStoredTextUiTests(CaptureUiApplicationFixture application)
        {
            _application = application;
        }

        static ReplayStoredTextUiTests()
        {
            Localization.LoadLanguage("en-us");
        }

        public void Dispose() => TestFolderCleanup.Delete(_directory);

        // Edit and resend opens no composer for an unmarked capture's request, and says why; for any other source it
        // opens one, as before.
        [Theory]
        [InlineData("live")]
        [InlineData("saz")]
        [InlineData("marked")]
        [InlineData("unmarked")]
        public Task EditAndResend_RefusesAnUnmarkedCapture_BeforeAComposerOpens(string kind) => _application.RunAsync(async () =>
        {
            ProxyConnectionModel row = Assert.Single(
                Rows(kind, new CapturedExchange("GET", "http://127.0.0.1:9/edit")));
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            CaptureConnectionView view = HostListUiTests.CaptureView(new HostListPolicy(), row);
            view.ReplayTarget = Recording(model);
            var messages = new List<(string Text, MessageBoxImage Image)>();
            view.ShowReplayMessage = (text, image) => messages.Add((text, image));
            RequestComposerWindow[] before = Composers();
            try
            {
                WebServiceProxyCommands.EditAndResend.Execute(row, view);

                RequestComposerWindow[] opened = Composers().Except(before).ToArray();
                if (kind == "unmarked")
                {
                    Assert.Empty(opened);
                    (string text, MessageBoxImage image) = Assert.Single(messages);
                    AssertRefusal("REPLAY_CAPTURE_TEXT_AMBIGUOUS", text);
                    Assert.Equal(MessageBoxImage.Warning, image);
                }
                else
                {
                    Assert.Equal("http://127.0.0.1:9/edit", Url(Assert.Single(opened)));
                    Assert.Empty(messages);
                }
            }
            finally
            {
                foreach (RequestComposerWindow window in Composers().Except(before))
                {
                    window.Close();
                }

                await RemoveAllAsync(model);
            }
        });

        // Reissue of an unmarked capture's requests asks nothing, sends nothing and adds no Replays tab, and says why;
        // for any other source it asks about the unsafe request and sends both, as before.
        [Theory]
        [InlineData("live")]
        [InlineData("saz")]
        [InlineData("marked")]
        [InlineData("unmarked")]
        public Task Reissue_RefusesAnUnmarkedCapture_BeforeItsQuestionOrSend(string kind) => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            ProxyConnectionModel[] rows = Rows(
                kind,
                new CapturedExchange("GET", server.Origin + "/read"),
                new CapturedExchange("POST", server.Origin + "/create", Body: "body"u8.ToArray()));
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            CaptureConnectionView view = HostListUiTests.CaptureView(new HostListPolicy(), rows);
            view.ReplayTarget = Recording(model);
            var messages = new List<(string Text, MessageBoxImage Image)>();
            view.ShowReplayMessage = (text, image) => messages.Add((text, image));
            var questions = new List<string>();
            view.ConfirmReplay = Answer(questions, true);
            try
            {
                ReplayRunResult run = await view.ReissueFromMenuAsync(new ReissueRequest(rows));

                if (kind == "unmarked")
                {
                    Assert.Null(run);
                    Assert.Empty(questions);
                    Assert.Empty(server.Requests);
                    Assert.Empty(model.CaptureDeviceContexts);
                    (string text, MessageBoxImage image) = Assert.Single(messages);
                    AssertRefusal("REPLAY_CAPTURE_TEXT_AMBIGUOUS", text);
                    Assert.Equal(MessageBoxImage.Warning, image);
                }
                else
                {
                    Assert.True(run.Result.Succeeded);
                    Assert.Single(questions);
                    Assert.Equal(new[] { "/read", "/create" }, server.Requests.Select(request => request.Target));
                    Assert.Empty(messages);
                }
            }
            finally
            {
                await RemoveAllAsync(model);
            }
        });

        // A request of an unmarked capture dropped on a composer leaves it as it was, edits, transport options and the
        // last response included, and asks nothing; those edits stay edits that a later drop asks about. A request of
        // any other source is dropped as before, after asking about the edits.
        [Theory]
        [InlineData("live")]
        [InlineData("saz")]
        [InlineData("marked")]
        [InlineData("unmarked")]
        public Task Drop_RefusesAnUnmarkedCapture_BeforeAnythingInTheComposerChanges(string kind) => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok("response"));
            using var client = new RequestReplayClient();
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var questions = new List<string>();
            var window = RequestComposerWindow.ForNewRequest(client, Answer(questions, true), Recording(model));
            try
            {
                ProxyConnectionModel row = Assert.Single(
                    Rows(kind, new CapturedExchange("GET", server.Origin + "/dropped")));
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/before";
                await window.SendAsync();
                RequestReplayResult response = window.LastResult;
                Assert.NotNull(response);
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/edited";
                Find<CheckBox>(window, "WindowsAuthBox").IsChecked = true;

                bool dropped;
                using (ComposerSessionDragToken drag = ComposerSessionDrag.Begin(row, SourceTab))
                {
                    dropped = window.DropSession(drag.Data);
                }

                if (kind == "unmarked")
                {
                    Assert.False(dropped);
                    AssertRefusal("REPLAY_CAPTURE_TEXT_AMBIGUOUS", Status(window));
                    Assert.Empty(questions);
                    Assert.Equal(server.Origin + "/edited", Url(window));
                    Assert.True(Find<CheckBox>(window, "WindowsAuthBox").IsChecked);
                    Assert.Same(response, window.LastResult);

                    ProxyConnectionModel live = Assert.Single(
                        Rows("live", new CapturedExchange("GET", server.Origin + "/live")));
                    using ComposerSessionDragToken next = ComposerSessionDrag.Begin(live, SourceTab);
                    Assert.True(window.DropSession(next.Data));
                    Assert.Single(questions);
                    Assert.Equal(server.Origin + "/live", Url(window));
                }
                else
                {
                    Assert.True(dropped);
                    Assert.Equal(
                        Localization.GetLocalizedString("COMPOSER_DROPPED", SourceTab, row.RequestNumber),
                        Status(window));
                    Assert.Single(questions);
                    Assert.Equal(server.Origin + "/dropped", Url(window));
                    Assert.False(Find<CheckBox>(window, "WindowsAuthBox").IsChecked);
                    Assert.Null(window.LastResult);
                }
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // Save request as, for a request that came from an unmarked capture, inspects nothing, asks nothing and writes
        // nothing; for any other source it inspects the request and asks where, as before.
        [Theory]
        [InlineData("live")]
        [InlineData("saz")]
        [InlineData("marked")]
        [InlineData("unmarked")]
        public Task SaveRequest_RefusesAnUnmarkedCapture_BeforeAnyQuestionOrFileWork(string kind) => _application.RunAsync(async () =>
        {
            ProxyConnectionModel row = Assert.Single(
                Rows(kind, new CapturedExchange("GET", "http://127.0.0.1:9/save")));
            var calls = new List<string>();
            var window = new RequestComposerWindow(row, confirm: NoQuestion);
            window.InspectRequest = (draft, token) =>
            {
                calls.Add("inspect");
                return RequestComposerWindow.InspectForSaving(draft, token);
            };
            window.ChooseSavePath = () =>
            {
                calls.Add("path");
                return null;
            };
            window.ChooseSaveContent = _ => throw new Xunit.Sdk.XunitException("Nothing is asked about content here.");
            window.PublishRequestFile = (_, _, _) => throw new Xunit.Sdk.XunitException("Nothing is written here.");
            try
            {
                string before = Request(window);

                await window.SaveRequestAsync();

                Assert.Equal(before, Request(window));
                if (kind == "unmarked")
                {
                    Assert.Empty(calls);
                    AssertRefusal("COMPOSER_EXPORT_TEXT_AMBIGUOUS", Status(window));
                }
                else
                {
                    Assert.Equal(new[] { "inspect", "path" }, calls);
                }
            }
            finally
            {
                window.Close();
            }
        });

        // Copy as cURL, for a request that came from an unmarked capture, makes no command and leaves the clipboard
        // alone; for any other source it prepares and copies the command, as before.
        [Theory]
        [InlineData("live")]
        [InlineData("saz")]
        [InlineData("marked")]
        [InlineData("unmarked")]
        public Task CopyAsCurl_RefusesAnUnmarkedCapture_BeforeACommandIsMadeOrCopied(string kind) => _application.RunAsync(async () =>
        {
            ProxyConnectionModel row = Assert.Single(
                Rows(kind, new CapturedExchange("GET", "http://127.0.0.1:9/copy")));
            var calls = new List<string>();
            var window = new RequestComposerWindow(row, confirm: NoQuestion);
            window.PrepareCurl = (draft, transport, redirects, token) =>
            {
                calls.Add("prepare");
                return RequestCurlExporter.Prepare(draft, transport, redirects, token);
            };
            window.CopyText = _ => calls.Add("copy");
            try
            {
                await window.CopyAsCurlAsync();

                if (kind == "unmarked")
                {
                    Assert.Empty(calls);
                    AssertRefusal("COMPOSER_EXPORT_TEXT_AMBIGUOUS", Status(window));
                }
                else
                {
                    Assert.Equal(new[] { "prepare", "copy" }, calls);
                }
            }
            finally
            {
                window.Close();
            }
        });

        // Save as cURL script, for a request that came from an unmarked capture, prepares nothing, asks nothing and
        // writes nothing; for any other source it prepares the request and asks where, as before.
        [Theory]
        [InlineData("live")]
        [InlineData("saz")]
        [InlineData("marked")]
        [InlineData("unmarked")]
        public Task SaveCurlScript_RefusesAnUnmarkedCapture_BeforeAnyQuestionOrScriptWork(string kind) => _application.RunAsync(async () =>
        {
            ProxyConnectionModel row = Assert.Single(
                Rows(kind, new CapturedExchange("GET", "http://127.0.0.1:9/script")));
            var calls = new List<string>();
            var window = new RequestComposerWindow(row, confirm: NoQuestion);
            window.PrepareCurl = (draft, transport, redirects, token) =>
            {
                calls.Add("prepare");
                return RequestCurlExporter.Prepare(draft, transport, redirects, token);
            };
            window.ChooseCurlScriptPath = () =>
            {
                calls.Add("path");
                return null;
            };
            window.PublishCurlScript = (_, _, _) => throw new Xunit.Sdk.XunitException("Nothing is written here.");
            try
            {
                await window.SaveCurlScriptAsync();

                if (kind == "unmarked")
                {
                    Assert.Empty(calls);
                    AssertRefusal("COMPOSER_EXPORT_TEXT_AMBIGUOUS", Status(window));
                }
                else
                {
                    Assert.Equal(new[] { "prepare", "path" }, calls);
                }
            }
            finally
            {
                window.Close();
            }
        });

        // History Load is never refused: the Replays tab records every send as Literal, so its row loads even in place
        // of a request that came from an unmarked capture, and the composer then saves and exports it as usual.
        [Fact]
        public Task HistoryLoad_OfAReplaysRow_IsNotRefused_EvenInPlaceOfAnUnmarkedRequest() => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            ReplayRecordingTarget target = Recording(model);
            ProxyConnectionModel unmarked = Assert.Single(
                Rows("unmarked", new CapturedExchange("GET", "http://127.0.0.1:9/old")));
            var calls = new List<string>();
            var window = new RequestComposerWindow(unmarked, confirm: NoQuestion, recording: target);
            window.ChooseSavePath = () =>
            {
                calls.Add("path");
                return null;
            };
            try
            {
                ProxyConnectionModel row = Record(target, new Uri("http://127.0.0.1:9/replayed"));
                Assert.Equal(CaptureTextSemantics.Literal, row.CaptureTextSource.Semantics);

                await window.LoadFromHistoryAsync(Assert.Single(model.ReplayHistory.Snapshot()).Entry);

                Assert.Equal(
                    Localization.GetLocalizedString(
                        "COMPOSER_HISTORY_LOADED", CaptureAppModel.ReplayContextName, row.RequestNumber),
                    Status(window));
                Assert.Equal("http://127.0.0.1:9/replayed", Url(window));

                await window.SaveRequestAsync();

                Assert.Equal(new[] { "path" }, calls);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // A send of a request that came from an unmarked capture, which no composer gets from Edit and resend or a
        // drop, is refused by the operation before anything is asked or sent, in the words of Edit and resend's
        // refusal, never with the command line's flag.
        [Fact]
        public Task Send_OfAnUnmarkedCaptureRequest_IsRefusedInTheGuisWords() => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            using var client = new RequestReplayClient();
            ProxyConnectionModel row = Assert.Single(
                Rows("unmarked", new CapturedExchange("POST", server.Origin + "/send", Body: "body"u8.ToArray())));
            var window = new RequestComposerWindow(row, client, NoQuestion);
            try
            {
                await window.SendAsync();

                Assert.Equal(
                    Localization.GetLocalizedString(
                        "COMPOSER_ERROR", Localization.GetLocalizedString("REPLAY_CAPTURE_TEXT_AMBIGUOUS")),
                    Status(window));
                Assert.DoesNotContain("--accept-stored-text", Status(window), StringComparison.Ordinal);
                Assert.Equal(0, server.Connections);
                Assert.Null(window.LastResult);
            }
            finally
            {
                window.Close();
            }
        });

        // The requests of these exchanges as a tab holds them: from a live capture, as the proxy makes its rows; or
        // read from a Fiddler archive, a marked XMAT capture or an unmarked one, as XMAT reads a capture file. Only an
        // unmarked capture's requests are AmbiguousUnmarked.
        private ProxyConnectionModel[] Rows(string kind, params CapturedExchange[] exchanges)
        {
            ProxyConnectionModel[] rows;
            if (kind == "live")
            {
                rows = exchanges.Select((exchange, index) => Live(exchange, index + 1)).ToArray();
            }
            else
            {
                string name = Guid.NewGuid().ToString("N");
                string path = kind == "saz"
                    ? ReplayCaptures.WriteSaz(_directory, name + ".saz", exchanges)
                    : ReplayCaptures.WriteXmatcap(_directory, name + ".xmatcap", ("Device 1", exchanges));
                if (kind == "unmarked")
                {
                    HostileSqliteSchemas.SetUserVersion(path, CaptureTextFormat.AmbiguousUserVersion);
                }

                OperationResult<CaptureRecordSet> read =
                    new AnalyzeCaptureOperation().ReadCapture(path, null, CancellationToken.None);
                Assert.True(read.Succeeded);
                rows = Assert.Single(read.Value.Devices).Records
                    .Select(record => new ProxyConnectionModel(record))
                    .ToArray();
            }

            CaptureTextSemantics expected =
                kind == "unmarked" ? CaptureTextSemantics.AmbiguousUnmarked : CaptureTextSemantics.Literal;
            Assert.All(rows, row => Assert.Equal(expected, row.CaptureTextSource.Semantics));
            return rows;
        }

        // A request as a live capture's model holds it.
        private static ProxyConnectionModel Live(CapturedExchange exchange, long number)
        {
            var uri = new Uri(exchange.Url);
            string head =
                $"{exchange.Method} {uri.PathAndQuery} HTTP/1.1\r\nHost: {uri.Authority}\r\n{exchange.Headers}";
            return new ProxyConnectionModel(
                number, "200", DateTime.UtcNow, TimeSpan.Zero, uri.Scheme, uri.Authority, exchange.Method,
                uri.PathAndQuery, head, exchange.Body ?? [], "HTTP/1.1 200 OK\r\n", [], "127.0.0.1");
        }

        // A send to url as the Replays tab records it, through the recorder: a Literal row.
        private static ProxyConnectionModel Record(ReplayRecordingTarget target, Uri url)
        {
            using var cancellation = new CancellationTokenSource();
            using ReplayRecording recording = target.TryBegin(cancellation, _ => ReplayProvenance.Composed);
            Assert.NotNull(recording);
            recording.Recorder.Add(
                new ReplayItemResult(1, null, null, 1, ReplayOutcome.Completed, 200, TimeSpan.Zero, 5, 0, null, null),
                new ReplayExchange
                {
                    Outcome = ReplayOutcome.Completed,
                    Request = new ReplayMessage(
                        $"GET {url.PathAndQuery} HTTP/1.1",
                        new[] { new KeyValuePair<string, string>("Host", url.Authority) },
                        null,
                        null),
                    Response = new ReplayMessage("HTTP/1.1 200 OK", null, null, "reply"u8.ToArray()),
                    RequestUri = url,
                    StatusCode = 200,
                    Started = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                });
            return recording.Recorder.LastRecorded;
        }

        // The interim refusal's words: the key's fixed text, which names a marked copy, never the command line's flag.
        private static void AssertRefusal(string key, string shown)
        {
            Assert.Equal(Localization.GetLocalizedString(key), shown);
            Assert.Contains("xmat mark-capture-text", shown, StringComparison.Ordinal);
            Assert.DoesNotContain("--accept-stored-text", shown, StringComparison.Ordinal);
        }

        // Records each question and gives the answer.
        private static Func<string, bool> Answer(List<string> questions, bool answer) => message =>
        {
            questions.Add(message);
            return answer;
        };

        private static RequestComposerWindow[] Composers() =>
            Application.Current.Windows.OfType<RequestComposerWindow>().ToArray();

        private static T Find<T>(Window window, string name) where T : class =>
            Assert.IsType<T>(window.FindName(name));

        private static string Status(Window window) => Find<TextBlock>(window, "StatusText").Text;

        private static string Url(Window window) => Find<TextBox>(window, "UrlBox").Text;

        // The request as the editors show it.
        private static string Request(Window window) => string.Join(
            "|",
            Find<TextBox>(window, "MethodBox").Text,
            Url(window),
            Find<TextBox>(window, "HeadersBox").Text,
            Find<TextBox>(window, "BodyBox").Text);

        // Records into the model's Replays tab, on this test's UI thread.
        private static ReplayRecordingTarget Recording(CaptureAppModel model)
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            return new ReplayRecordingTarget(model, action => dispatcher.Invoke(action));
        }

        private static async Task RemoveAllAsync(CaptureAppModel model)
        {
            foreach (ICaptureDeviceContext context in model.CaptureDeviceContexts.ToList())
            {
                await model.RemoveDeviceContextAsync(context);
            }
        }
    }
}
