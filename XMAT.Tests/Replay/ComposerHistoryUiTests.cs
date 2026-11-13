// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using XMAT.Models;
using XMAT.Operations.Replay;
using XMAT.Operations.Storage;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Replay
{
    // The composer's history: the requests that the Replays tab recorded, in the History menu of every composer
    // that records, with Load, Show in Replays and Clear history. What Load loads, resets and asks; the work that it runs off
    // the UI thread and how that stops; how each action finds its entry again; and what the menu keeps, and for how long.
    // Requests go to loopback servers only, and each test records into its own application model.
    [Collection(CaptureUiApplicationCollection.Name)]
    public class ComposerHistoryUiTests
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

        // When the recorded sends started, in UTC as each exchange keeps it; the history shows it as this PC's local time.
        private static readonly DateTime SentAt = new(2026, 1, 1, 9, 30, 15, DateTimeKind.Utc);

        private static readonly Func<string, bool> NoQuestion =
            message => throw new Xunit.Sdk.XunitException("Nothing needs confirmation here: " + message);

        // A template request file, as XMAT saves one: it keeps the method and the HTTP version, and leaves out the URL, the
        // headers and the body.
        private const string TemplateFile =
            "{ \"schema\": \"xmat-request/1\", \"profile\": \"template\", \"method\": \"POST\", \"url\": null, " +
            "\"httpVersion\": \"auto\", \"headers\": null, \"body\": null, \"omitted\": [\"url\", \"headers\", \"body\"] }";

        private readonly CaptureUiApplicationFixture _application;

        public ComposerHistoryUiTests(CaptureUiApplicationFixture application)
        {
            _application = application;
        }

        static ComposerHistoryUiTests()
        {
            Localization.LoadLanguage("en-us");
        }

        // Load, from the menu, gives the editors what Edit and resend gives them for the entry's row, and a send then
        // comes from that row, as edited from the Replays tab. The transport options go back to their defaults before the
        // request becomes the baseline, so loading again asks nothing. Inspect session stays, the previous response is
        // cleared, and nothing is sent or read.
        [Fact]
        public Task Load_GivesEditAndResendsRequest_ResetsTheTransportIntoTheBaseline_AndSendsNothing() => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(request => LoopbackResponse.Ok("reply " + request.Target));
            using var client = new RequestReplayClient();
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            ReplayRecordingTarget target = Recording(model);
            var questions = new List<string>();
            var window = RequestComposerWindow.ForNewRequest(client, Answer(questions, true), target);
            window.ChooseRequestFile = () => throw new Xunit.Sdk.XunitException("Load reads no file.");
            window.ChooseBodyFile = () => throw new Xunit.Sdk.XunitException("Load reads no file.");
            RequestComposerWindow editAndResend = null;
            try
            {
                // A response to clear, then a recorded request to load, and transport options to reset.
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/before";
                await window.SendAsync();
                Assert.NotNull(window.LastResult);
                ProxyConnectionModel row = Assert.Single(
                    Record(target, Exchange(server.Port, "/items/7?page=2", "POST", "name=value"u8.ToArray())));
                editAndResend = new RequestComposerWindow(row, confirm: NoQuestion);
                Find<CheckBox>(window, "InspectSessionBox").IsChecked = true;
                ChooseVersion(window, ReplayHttpVersion.Http11);
                Find<CheckBox>(window, "ConnectToBox").IsChecked = true;
                Find<TextBox>(window, "ConnectToText").Text = "192.0.2.10:443";
                Find<CheckBox>(window, "FollowRedirectsBox").IsChecked = true;
                Find<ComboBox>(window, "RedirectLimitBox").SelectedItem = 3;
                ProxyConnectionModel[] recorded = Replays(model).ProxyConnections.ToArray();
                string loaded = Localization.GetLocalizedString(
                    "COMPOSER_HISTORY_LOADED", CaptureAppModel.ReplayContextName, row.RequestNumber);

                window.OpenHistoryView();
                MenuItem load = Child(Entries(window)[0], "HistoryLoad");
                load.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, load));
                await WaitUntilAsync(() => Status(window) == loaded);

                Assert.Equal(
                    new[]
                    {
                        Localization.GetLocalizedString(
                            "COMPOSER_REPLACE_EDITED_HISTORY", CaptureAppModel.ReplayContextName, row.RequestNumber),
                    },
                    questions);
                Assert.Equal(Request(editAndResend), Request(window));
                Assert.Equal((ReplayHttpVersion.Auto, false, "", false, 10), Transport(window));
                Assert.True(Find<CheckBox>(window, "InspectSessionBox").IsChecked);
                Assert.Null(window.LastResult);
                Assert.Single(server.Requests);
                Assert.Equal(recorded, Replays(model).ProxyConnections);

                // The options as reset are the baseline: loading again asks nothing.
                await window.LoadFromHistoryAsync(Snapshot(model)[0].Entry);
                Assert.Single(questions);
                Assert.Equal(loaded, Status(window));

                await window.SendAsync();

                var sent = server.Requests[^1];
                Assert.Equal(
                    ("POST", "/items/7?page=2", "canary", "name=value"),
                    (sent.Method, sent.Target, sent.Header("X-Token"), Encoding.UTF8.GetString(sent.Body)));
                Assert.Equal(
                    ReplayProvenance.Of(ReplaySourceKind.Edited, CaptureAppModel.ReplayContextName, row),
                    Replays(model).ProxyConnections[^1].Replay);
            }
            finally
            {
                editAndResend?.Close();
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // A body larger than the editor shows is prepared off the UI thread, held as the row's exact bytes and shown by
        // the row and its size, never as text or Base64; Copy as cURL is unavailable for it, and a send sends those bytes.
        [Fact]
        public Task ALargeBody_IsPreparedOffTheUiThread_HeldBehindItsRow_AndSentAsItsExactBytes() => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            using var client = new RequestReplayClient();
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            ReplayRecordingTarget target = Recording(model);
            byte[] body = Enumerable.Range(0, 70_000).Select(index => (byte)('a' + index % 26)).ToArray();
            Record(target, Exchange(server.Port, "/upload", "POST", body));
            var window = RequestComposerWindow.ForNewRequest(client, _ => true, target);
            var prepared = new List<(bool OnUiThread, int MaxShownBodyBytes)>();
            window.PrepareHistoryDraft = (capture, maxShownBodyBytes, token) =>
            {
                prepared.Add((window.Dispatcher.CheckAccess(), maxShownBodyBytes));
                return RequestReplayDraft.FromCapture(capture, maxShownBodyBytes, token);
            };
            try
            {
                await window.LoadFromHistoryAsync(Assert.Single(Snapshot(model)).Entry);

                Assert.Equal(new[] { (false, 64 * 1024) }, prepared);
                var placeholder = Find<TextBlock>(window, "BodyFileText");
                Assert.Equal(Visibility.Visible, placeholder.Visibility);
                Assert.Equal(
                    $"The body is the {body.Length:N0} bytes of XMAT Replays #1, not shown here: the composer shows a body of " +
                        "at most 64 KiB from the Replays tab. Exactly these bytes are sent and saved. Upload File replaces " +
                        "them and Clear body removes them.",
                    placeholder.Text);
                Assert.Equal(Visibility.Collapsed, Find<TextBox>(window, "BodyBox").Visibility);
                Assert.Equal("", Find<TextBox>(window, "BodyBox").Text);
                Assert.False(Find<CheckBox>(window, "Base64Box").IsEnabled);
                Assert.Equal(
                    Localization.GetLocalizedString("COMPOSER_COPY_CURL_LARGE_BODY"), Find<Button>(window, "CopyCurlButton").ToolTip);
                Assert.Empty(server.Requests);

                await window.SendAsync();

                Assert.Equal(body, Assert.Single(server.Requests).Body);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // The request is prepared off the UI thread while the window is busy. Cancel request, or closing the window,
        // stops that, and a request prepared by then arrives late and is dropped; a request that cannot be prepared says why.
        // Either way the request in the editors stays as it was, and nothing is asked.
        [Theory]
        [InlineData("cancel")]
        [InlineData("close")]
        [InlineData("late")]
        [InlineData("fail")]
        public Task Load_ThatIsCancelledClosedOrFails_ChangesNothing_AndDropsALateRequest(string stop) => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            ReplayRecordingTarget target = Recording(model);
            Record(target, Exchange(9, "/late"));
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion, recording: target);
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool closed = false;
            window.PrepareHistoryDraft = (capture, maxShownBodyBytes, token) =>
            {
                started.TrySetResult(window.Dispatcher.CheckAccess());
                if (stop == "fail")
                    throw new FormatException("The captured request contains malformed chunked framing.");
                bool cancelled = token.WaitHandle.WaitOne(Patience);
                if (stop != "late")
                    token.ThrowIfCancellationRequested();
                Assert.True(cancelled);
                // Returns normally once cancelled, as work does that last checked its token before Cancel request.
                return RequestReplayDraft.FromCapture(capture, maxShownBodyBytes, CancellationToken.None);
            };
            try
            {
                string before = Editors(window);

                Task loading = window.LoadFromHistoryAsync(Assert.Single(Snapshot(model)).Entry);

                Assert.False(await started.Task.WaitAsync(Patience));
                if (stop != "fail")
                {
                    Assert.Equal(
                        Localization.GetLocalizedString("COMPOSER_HISTORY_LOADING", CaptureAppModel.ReplayContextName, 1L),
                        Status(window));
                    Assert.False(Find<Grid>(window, "RequestEditor").IsEnabled);
                    Assert.True(Find<Button>(window, "CancelSendButton").IsEnabled);
                    if (stop == "close")
                    {
                        window.Close();
                        closed = true;
                    }
                    else
                    {
                        window.CancelSend();
                    }
                }

                await loading.WaitAsync(Patience);

                Assert.Equal(before, Editors(window));
                if (!closed)
                {
                    Assert.Equal(
                        stop == "fail"
                            ? Localization.GetLocalizedString(
                                "COMPOSER_HISTORY_LOAD_FAILED", "The captured request contains malformed chunked framing.")
                            : Localization.GetLocalizedString("COMPOSER_HISTORY_LOAD_CANCELLED"),
                        Status(window));
                    Assert.True(Find<Grid>(window, "RequestEditor").IsEnabled);
                }
            }
            finally
            {
                if (!closed)
                {
                    window.Close();
                }

                await RemoveAllAsync(model);
            }
        });

        // The entry's row is found again once its request is prepared, and again after the user agreed to replace the
        // edited request: an entry whose tab was closed, or that Clear history or 200 newer requests took out of the
        // history meanwhile, is not loaded. Found gone once prepared, nothing is asked. The request in the editors, its
        // transport options and the response stay as they were.
        [Theory]
        [InlineData("preparing", "tab")]
        [InlineData("preparing", "clear")]
        [InlineData("preparing", "evict")]
        [InlineData("asking", "tab")]
        [InlineData("asking", "clear")]
        [InlineData("asking", "evict")]
        public Task AnEntryLeavingTheHistory_WhilePreparedOrAskedAbout_IsNotLoaded(string phase, string gone) => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok("first"));
            using var client = new RequestReplayClient();
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            ReplayRecordingTarget target = Recording(model);
            var questions = new List<string>();
            ICaptureDeviceContext replays = null;
            void TakeOut()
            {
                if (gone == "tab")
                {
                    // As closing the tab does for the history: the tab is no longer one of the model's.
                    Assert.True(model.CaptureDeviceContexts.Remove(replays));
                }
                else if (gone == "clear")
                {
                    model.ReplayHistory.Clear();
                }
                else
                {
                    Record(target, Enumerable.Range(1, ReplayHistory.MaxEntries)
                        .Select(number => Exchange(server.Port, "/newer/" + number))
                        .ToArray());
                }
            }

            var window = RequestComposerWindow.ForNewRequest(
                client,
                message =>
                {
                    questions.Add(message);
                    if (phase == "asking")
                        TakeOut();
                    return true;
                },
                target);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var proceed = new ManualResetEventSlim();
            if (phase == "preparing")
            {
                window.PrepareHistoryDraft = (capture, maxShownBodyBytes, token) =>
                {
                    started.TrySetResult();
                    Assert.True(proceed.Wait(Patience));
                    return RequestReplayDraft.FromCapture(capture, maxShownBodyBytes, token);
                };
            }

            try
            {
                // The entry is the window's own send, whose response it shows; the request and a transport option are then
                // edited, so that loading asks before it replaces them.
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/first";
                await window.SendAsync();
                replays = model.GetOrAddReplayContext();
                ReplayHistoryEntry entry = Assert.Single(Snapshot(model)).Entry;
                object response = window.LastResult;
                Assert.NotNull(response);
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/edited";
                ChooseVersion(window, ReplayHttpVersion.Http11);
                string before = Editors(window);

                Task loading = window.LoadFromHistoryAsync(entry);
                if (phase == "preparing")
                {
                    await started.Task.WaitAsync(Patience);
                    TakeOut();
                    proceed.Set();
                }

                await loading.WaitAsync(Patience);

                Assert.Equal(Localization.GetLocalizedString("COMPOSER_HISTORY_GONE"), Status(window));
                Assert.Equal(
                    phase == "asking"
                        ? new[]
                        {
                            Localization.GetLocalizedString(
                                "COMPOSER_REPLACE_EDITED_HISTORY", CaptureAppModel.ReplayContextName, 1L),
                        }
                        : Array.Empty<string>(),
                    questions);
                Assert.Equal(before, Editors(window));
                Assert.Same(response, window.LastResult);
                Assert.Single(server.Requests);
            }
            finally
            {
                proceed.Set();
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // Load is refused while the window is busy, as while it prepares another load, and says why. Before it replaces
        // an edited request, the window asks, without a confirm in its own message box, with Yes and No, as a warning, and
        // with No as the answer that Enter gives; No loads nothing. The question's English wording is pinned here.
        [Fact]
        public Task Load_IsRefusedWhileBusy_AndAsksBeforeReplacingEdits_NoByDefault() => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            ReplayRecordingTarget target = Recording(model);
            Record(target, Exchange(9, "/first"), Exchange(9, "/second"));
            var window = RequestComposerWindow.ForNewRequest(recording: target);
            var shown = new List<(string, MessageBoxButton, MessageBoxImage, MessageBoxResult)>();
            window.ShowQuestion = (message, buttons, image, defaultResult) =>
            {
                shown.Add((message, buttons, image, defaultResult));
                return MessageBoxResult.No;
            };
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var proceed = new ManualResetEventSlim();
            int prepared = 0;
            window.PrepareHistoryDraft = (capture, maxShownBodyBytes, token) =>
            {
                Interlocked.Increment(ref prepared);
                started.TrySetResult();
                Assert.True(proceed.Wait(Patience));
                return RequestReplayDraft.FromCapture(capture, maxShownBodyBytes, token);
            };
            try
            {
                IReadOnlyList<ReplayHistoryPreview> history = Snapshot(model);
                Task loading = window.LoadFromHistoryAsync(history[1].Entry);
                await started.Task.WaitAsync(Patience);

                Task refused = window.LoadFromHistoryAsync(history[0].Entry);

                Assert.True(refused.IsCompleted);
                Assert.Equal(Localization.GetLocalizedString("COMPOSER_HISTORY_BUSY"), Status(window));
                Assert.Equal(1, Volatile.Read(ref prepared));
                proceed.Set();
                await loading.WaitAsync(Patience);
                Assert.Equal(
                    Localization.GetLocalizedString("COMPOSER_HISTORY_LOADED", CaptureAppModel.ReplayContextName, 1L),
                    Status(window));
                Assert.Equal("http://127.0.0.1:9/first", Find<TextBox>(window, "UrlBox").Text);
                Assert.Empty(shown);

                Find<TextBox>(window, "UrlBox").Text = "http://127.0.0.1:9/edited";
                string before = Editors(window);
                await window.LoadFromHistoryAsync(history[0].Entry);

                string question = Localization.GetLocalizedString(
                    "COMPOSER_REPLACE_EDITED_HISTORY", CaptureAppModel.ReplayContextName, 2L);
                Assert.Equal(
                    new[] { (question, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) }, shown);
                Assert.Equal(
                    "Replace the current request with request #2 of the XMAT Replays tab? Your edits to it and to its " +
                        "transport options will be lost.",
                    question);
                Assert.Equal(before, Editors(window));
                Assert.Equal(Localization.GetLocalizedString("COMPOSER_HISTORY_LOAD_CANCELLED"), Status(window));
            }
            finally
            {
                proceed.Set();
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // Changing only a transport option, which Load resets, or only supplying a part that a request file left out, as
        // the empty part that the editors already show, is an edit too, although the request in the editors reads as it
        // did: Load asks first, and declining keeps the option or the supplied part, and everything else, as it was.
        [Theory]
        [InlineData("version")]
        [InlineData("connect")]
        [InlineData("limit")]
        [InlineData("omission")]
        public Task ChangingOnlyATransportOptionOrSupplyingAPart_IsAnEdit_ThatLoadAsksAbout(string edit) => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            ReplayRecordingTarget target = Recording(model);
            Record(target, Exchange(9, "/history"));
            var questions = new List<string>();
            var window = RequestComposerWindow.ForNewRequest(confirm: Answer(questions, false), recording: target);
            window.ChooseRequestFile = () => Path.Join(AppContext.BaseDirectory, "template.xmat-request.json");
            window.ReadRequestFile = (_, _) => Task.FromResult(RequestFileReader.Read(Encoding.UTF8.GetBytes(TemplateFile)));
            try
            {
                if (edit == "omission")
                {
                    await window.OpenRequestAsync();
                    Assert.Empty(questions);
                }

                string request = Request(window);
                string unedited = Editors(window);
                switch (edit)
                {
                    case "version":
                        ChooseVersion(window, ReplayHttpVersion.Http11);
                        break;
                    case "connect":
                        Find<CheckBox>(window, "ConnectToBox").IsChecked = true;
                        break;
                    case "limit":
                        Find<ComboBox>(window, "RedirectLimitBox").SelectedItem = 3;
                        break;
                    default:
                        window.SupplyHeaders(none: true);
                        Assert.Equal(unedited, Editors(window));
                        Assert.Equal(
                            Localization.GetLocalizedString(
                                "COMPOSER_OMITTED",
                                string.Join(
                                    ", ",
                                    Localization.GetLocalizedString("COMPOSER_PART_URL"),
                                    Localization.GetLocalizedString("COMPOSER_PART_BODY"))),
                            Find<TextBlock>(window, "OmissionsText").Text);
                        break;
                }

                Assert.Equal(request, Request(window));
                string edited = Editors(window);
                string omitted = Find<TextBlock>(window, "OmissionsText").Text;

                await window.LoadFromHistoryAsync(Assert.Single(Snapshot(model)).Entry);

                Assert.Equal(
                    Localization.GetLocalizedString("COMPOSER_REPLACE_EDITED_HISTORY", CaptureAppModel.ReplayContextName, 1L),
                    Assert.Single(questions));
                Assert.Equal(Localization.GetLocalizedString("COMPOSER_HISTORY_LOAD_CANCELLED"), Status(window));
                Assert.Equal(edited, Editors(window));
                Assert.Equal(omitted, Find<TextBlock>(window, "OmissionsText").Text);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // A request that the Replays tab recorded without its body cannot be loaded: its Load is unavailable and says
        // why, also to automation and while disabled, and Load refuses it without preparing anything. Show in Replays is
        // still offered, and selects it.
        [Fact]
        public Task ARequestRecordedWithoutItsBody_CannotBeLoaded_ButIsShownInReplays() => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            var keepsNoBody = new ReplayRecordingTarget(model, action => dispatcher.Invoke(action), bodyBudgetBytes: 0);
            ProxyConnectionModel row = Assert.Single(Record(keepsNoBody, Exchange(9, "/upload", "POST", "body"u8.ToArray())));
            Assert.False(RequestReplayDraft.CanReplay(row));
            ICaptureDeviceContext other = AddTab(model, "Other");
            model.SelectedDeviceContext = other;
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion, recording: Recording(model));
            window.PrepareHistoryDraft = (_, _, _) =>
                throw new Xunit.Sdk.XunitException("A request that cannot be sent again is not prepared.");
            try
            {
                window.OpenHistoryView();
                MenuItem entry = Assert.Single(Entries(window));
                MenuItem load = Child(entry, "HistoryLoad");
                MenuItem show = Child(entry, "HistoryShow");
                string reason = Localization.GetLocalizedString("COMPOSER_HISTORY_CANNOT_LOAD");

                Assert.True(entry.IsEnabled);
                Assert.False(load.IsEnabled);
                Assert.Equal(reason, load.ToolTip);
                Assert.Equal(reason, AutomationProperties.GetHelpText(load));
                Assert.True(ToolTipService.GetShowOnDisabled(load));
                Assert.True(show.IsEnabled);
                string before = Editors(window);

                await window.LoadFromHistoryAsync((ReplayHistoryEntry)entry.Tag);

                Assert.Equal(Localization.GetLocalizedString("COMPOSER_HISTORY_UNSUPPORTED"), Status(window));
                Assert.Equal(before, Editors(window));

                show.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, show));

                Assert.Same(model.GetOrAddReplayContext(), model.SelectedDeviceContext);
                Assert.Same(row, Replays(model).SelectedConnectionModel);
                Assert.Equal(
                    Localization.GetLocalizedString("COMPOSER_HISTORY_SHOWN", CaptureAppModel.ReplayContextName, 1L),
                    Status(window));
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // Clear history in one composer empties the history of every composer: another composer's open menu then says
        // that the history is empty, with no entry and no Clear history, and the entries that it listed no longer load or
        // show anything. The Replays tab keeps its requests.
        [Fact]
        public Task ClearHistory_EmptiesAnotherComposersOpenMenu_AndKeepsTheReplays() => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            ReplayRecordingTarget target = Recording(model);
            ProxyConnectionModel[] rows = Record(target, Exchange(9, "/one"), Exchange(9, "/two"));
            ICaptureDeviceContext other = AddTab(model, "Other");
            model.SelectedDeviceContext = other;
            var clearing = RequestComposerWindow.ForNewRequest(confirm: NoQuestion, recording: target);
            var viewing = RequestComposerWindow.ForNewRequest(confirm: NoQuestion, recording: target);
            viewing.PrepareHistoryDraft = (_, _, _) =>
                throw new Xunit.Sdk.XunitException("An entry that the history no longer has is not prepared.");
            try
            {
                var menu = Find<ContextMenu>(viewing, "HistoryMenu");
                viewing.OpenHistoryView();
                MenuItem[] entries = Entries(viewing);
                Assert.Equal(2, entries.Length);
                Assert.Single(menu.Items.OfType<MenuItem>(), item => item.Name == "HistoryClear");
                string before = Editors(viewing);

                clearing.ClearHistory();
                await SettleAsync();

                Assert.Equal(Localization.GetLocalizedString("COMPOSER_HISTORY_CLEARED"), Status(clearing));
                MenuItem empty = Assert.IsType<MenuItem>(Assert.Single(menu.Items));
                Assert.Equal("History is empty", empty.Header);
                Assert.False(empty.IsEnabled);
                Assert.Equal(rows, Replays(model).ProxyConnections);

                await viewing.LoadFromHistoryAsync((ReplayHistoryEntry)entries[1].Tag);
                Assert.Equal(Localization.GetLocalizedString("COMPOSER_HISTORY_GONE"), Status(viewing));
                Assert.Equal(before, Editors(viewing));
                viewing.ShowInReplays((ReplayHistoryEntry)entries[0].Tag);
                Assert.Equal(Localization.GetLocalizedString("COMPOSER_HISTORY_SHOW_GONE"), Status(viewing));
                Assert.Same(other, model.SelectedDeviceContext);
            }
            finally
            {
                clearing.Close();
                viewing.Close();
                await RemoveAllAsync(model);
            }
        });

        // Clear history in the composer that loaded a request forgets every entry, that request's too, and keeps the
        // request as it was loaded and edited since: its editors, its body held as bytes, its transport options, Inspect
        // session and the response. Clearing asks and sends nothing, and a send afterwards still sends the held bytes, with
        // the edits, as edited from the row that the request was loaded from.
        [Fact]
        public Task ClearHistory_KeepsTheRequestThatThisComposerLoaded_WithItsHeldBodyAndResponse() => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok("stored"));
            using var client = new RequestReplayClient();
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            ReplayRecordingTarget target = Recording(model);
            byte[] body = Enumerable.Range(0, 70_000).Select(index => (byte)('a' + index % 26)).ToArray();
            ProxyConnectionModel row = Assert.Single(Record(target, Exchange(server.Port, "/upload", "POST", body)));
            var questions = new List<string>();
            var window = RequestComposerWindow.ForNewRequest(client, Answer(questions, true), target);
            var menu = Find<ContextMenu>(window, "HistoryMenu");
            try
            {
                await window.LoadFromHistoryAsync(Assert.Single(Snapshot(model)).Entry);
                await window.SendAsync();
                Find<TextBox>(window, "HeadersBox").Text += "\r\nX-Edited: kept";
                ChooseVersion(window, ReplayHttpVersion.Http11);
                Find<CheckBox>(window, "InspectSessionBox").IsChecked = true;
                string before = Editors(window);
                object response = window.LastResult;
                Assert.NotNull(response);
                Assert.Equal(Visibility.Visible, Find<TextBlock>(window, "BodyFileText").Visibility);
                int asked = questions.Count;
                window.OpenHistoryView();
                Assert.Equal(2, Entries(window).Length);

                window.ClearHistory();
                await SettleAsync();

                Assert.Equal(Localization.GetLocalizedString("COMPOSER_HISTORY_CLEARED"), Status(window));
                Assert.Empty(Snapshot(model));
                Assert.Equal(
                    Localization.GetLocalizedString("COMPOSER_HISTORY_EMPTY"),
                    Assert.IsType<MenuItem>(Assert.Single(menu.Items)).Header);
                Assert.Equal(before, Editors(window));
                Assert.Same(response, window.LastResult);
                Assert.Equal(asked, questions.Count);
                Assert.Single(server.Requests);

                await window.SendAsync();

                var sent = server.Requests[^1];
                Assert.Equal(2, server.Requests.Count);
                Assert.Equal(body, sent.Body);
                Assert.Equal("kept", sent.Header("X-Edited"));
                Assert.Equal(
                    ReplayProvenance.Of(ReplaySourceKind.Edited, CaptureAppModel.ReplayContextName, row),
                    Replays(model).ProxyConnections[^1].Replay);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // Show in Replays selects the entry's own row in its own tab, by reference: never the row with the same number
        // in another tab of the same name, as a saved capture can have. Once the row has left its tab, it says so and
        // selects nothing.
        [Fact]
        public Task ShowInReplays_SelectsTheEntrysOwnRow_AndSaysSoOnceItIsGone() => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            ReplayRecordingTarget target = Recording(model);
            ProxyConnectionModel[] rows = Record(target, Exchange(9, "/one"), Exchange(9, "/two"));
            ICaptureDeviceContext replays = model.GetOrAddReplayContext();
            ICaptureDeviceContext twin = AddTab(
                model, replays.DeviceName, new ProxyConnectionModel(rows[0]), new ProxyConnectionModel(rows[1]));
            ICaptureDeviceContext other = AddTab(model, "Other");
            model.SelectedDeviceContext = other;
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion, recording: target);
            try
            {
                ReplayHistoryEntry first = Snapshot(model)[1].Entry;

                window.ShowInReplays(first);

                Assert.Same(replays, model.SelectedDeviceContext);
                Assert.Same(rows[0], ControllerOf(replays).SelectedConnectionModel);
                Assert.Null(ControllerOf(twin).SelectedConnectionModel);
                Assert.Equal(
                    Localization.GetLocalizedString("COMPOSER_HISTORY_SHOWN", CaptureAppModel.ReplayContextName, 1L),
                    Status(window));

                model.SelectedDeviceContext = other;
                ControllerOf(replays).SelectedConnectionModel = null;
                Assert.True(ControllerOf(replays).ProxyConnections.Remove(rows[0]));

                window.ShowInReplays(first);

                Assert.Equal(Localization.GetLocalizedString("COMPOSER_HISTORY_SHOW_GONE"), Status(window));
                Assert.Same(other, model.SelectedDeviceContext);
                Assert.Null(ControllerOf(replays).SelectedConnectionModel);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // Loading grants nothing, and the frozen rules of sending and exporting apply to a loaded request: an unsafe send
        // asks; a same-origin Copy as cURL is its one click, with the warning beside it; Save as cURL script asks for the
        // content every time; and an export to another origin asks about that origin first. Declining sends nothing,
        // leaves the clipboard as it was and saves nothing.
        [Fact]
        public Task ALoadedRequest_GrantsNothing_SendAndExportsAskAsTheyAlwaysDo() => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            using var client = new RequestReplayClient();
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            ReplayRecordingTarget target = Recording(model);
            Record(target, Exchange(server.Port, "/items", "POST", "a=1"u8.ToArray()));
            var steps = new List<string>();
            var answers = new Queue<bool>(new[] { false, false, false, false });
            var window = RequestComposerWindow.ForNewRequest(
                client,
                message =>
                {
                    steps.Add(message);
                    return answers.Dequeue();
                },
                target);
            string clipboard = "sentinel";
            var published = new List<string>();
            window.CopyText = text => clipboard = text;
            window.ChooseCurlScriptPath = () =>
            {
                steps.Add("where");
                return Path.Join(AppContext.BaseDirectory, "loaded.ps1");
            };
            window.PublishCurlScript = (path, _, _) =>
            {
                published.Add(path);
                return PrivateFilePublication.Published;
            };
            try
            {
                await window.LoadFromHistoryAsync(Assert.Single(Snapshot(model)).Entry);
                Assert.Empty(steps);

                await window.SendAsync();
                await window.CopyAsCurlAsync();
                string copied = clipboard;
                await window.SaveCurlScriptAsync();
                Find<TextBox>(window, "UrlBox").Text = "http://127.0.0.2:9/items";
                await window.CopyAsCurlAsync();
                await window.SaveCurlScriptAsync();

                string origin = Localization.GetLocalizedString("COMPOSER_CURL_ORIGIN", "http://127.0.0.2:9");
                Assert.Equal(
                    new[]
                    {
                        Localization.GetLocalizedString("COMPOSER_CONFIRM_MESSAGE", "POST", server.Origin + "/items"),
                        "where",
                        Localization.GetLocalizedString("COMPOSER_SAVE_CURL_CONSENT", 3),
                        origin,
                        "where",
                        origin,
                    },
                    steps);
                Assert.Contains(server.Origin + "/items", copied);
                Assert.Equal(copied, clipboard);
                Assert.Equal(Visibility.Visible, Find<TextBlock>(window, "CurlWarningText").Visibility);
                Assert.Empty(server.Requests);
                Assert.Empty(published);
                Assert.Empty(answers);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // Closing the menu ends its subscription to the history, and so does closing the window with the menu open: the
        // window is then collected, while the history, which the model keeps, lives on.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public Task ClosingTheMenuOrTheWindow_EndsItsSubscription_SoTheWindowIsCollected(bool withMenuOpen) => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            ReplayRecordingTarget target = Recording(model);
            try
            {
                Record(target, Exchange(9, "/one"));

                WeakReference window = OpenThenClose(target, withMenuOpen);
                await GcTestSupport.CollectAsync(window);

                Assert.False(window.IsAlive);
                Assert.Single(Snapshot(model));
            }
            finally
            {
                await RemoveAllAsync(model);
            }
        });

        // History is a button with an access key, reached with Tab, that Enter, Space and automation open as a menu
        // above it. The menu, its entries, newest first, and their Load, Show in Replays and Clear history are named for
        // automation, by the entries' previews or by localized words and reasons. Closing the menu, or the window while the
        // menu is open, leaves it closed and empty. A composer that records nothing has no history.
        [Fact]
        public Task History_IsReachedByKeyboard_AndNamedForAutomation() => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            var keepsNoBody = new ReplayRecordingTarget(model, action => dispatcher.Invoke(action), bodyBudgetBytes: 0);
            Record(keepsNoBody, Exchange(9, "/upload", "POST", "body"u8.ToArray(), answered: false));
            ReplayRecordingTarget target = Recording(model);
            Record(target, Exchange(9, "/items?page=2"));
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion, recording: target);
            var unrecorded = RequestComposerWindow.ForNewRequest(confirm: NoQuestion);
            var menu = Find<ContextMenu>(window, "HistoryMenu");
            bool closed = false;
            try
            {
                Assert.Equal(Visibility.Collapsed, Find<Button>(unrecorded, "HistoryButton").Visibility);
                unrecorded.OpenHistoryView();
                Assert.Empty(Find<ContextMenu>(unrecorded, "HistoryMenu").Items);

                await ShowOffscreenAsync(window);
                var button = Find<Button>(window, "HistoryButton");
                AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(button);
                var invoke = (IInvokeProvider)peer.GetPattern(PatternInterface.Invoke);
                Assert.Equal(Visibility.Visible, button.Visibility);
                Assert.True(button.IsTabStop);
                Assert.True(peer.IsKeyboardFocusable());
                Assert.Equal("Histor_y", button.Content);
                Assert.Equal("History", peer.GetName());
                Assert.Equal(Localization.GetLocalizedString("COMPOSER_HISTORY_MENU"), AutomationProperties.GetName(menu));

                invoke.Invoke();
                await WaitUntilAsync(() => menu.IsOpen && Entries(window).Length == 2);

                Assert.Same(button, menu.PlacementTarget);
                MenuItem[] entries = Entries(window);
                Assert.Equal(
                    new[]
                    {
                        $"XMAT Replays #2, {SentAt.ToLocalTime():HH:mm:ss}: GET http://127.0.0.1:9/items?page=2, HTTP 200",
                        $"XMAT Replays #1, {SentAt.ToLocalTime():HH:mm:ss}: POST http://127.0.0.1:9/upload, no response",
                    },
                    entries.Select(AutomationProperties.GetName));
                Assert.All(entries, entry => Assert.Equal(AutomationProperties.GetName(entry), entry.ToolTip));
                Assert.All(
                    entries,
                    entry => Assert.Equal(
                        new[] { "_Load", "_Show in Replays" }, entry.Items.OfType<MenuItem>().Select(item => (string)item.Header)));
                Assert.Equal(
                    Localization.GetLocalizedString("COMPOSER_HISTORY_LOAD_HINT"),
                    AutomationProperties.GetHelpText(Child(entries[0], "HistoryLoad")));
                Assert.Equal(
                    Localization.GetLocalizedString("COMPOSER_HISTORY_CANNOT_LOAD"),
                    AutomationProperties.GetHelpText(Child(entries[1], "HistoryLoad")));
                Assert.Equal("_Clear history", Assert.Single(menu.Items.OfType<MenuItem>(), item => item.Name == "HistoryClear").Header);

                menu.IsOpen = false;
                await WaitUntilAsync(() => menu.Items.Count == 0);

                invoke.Invoke();
                await WaitUntilAsync(() => menu.IsOpen && Entries(window).Length == 2);
                window.Close();
                closed = true;

                Assert.False(menu.IsOpen);
                Assert.Empty(menu.Items);
            }
            finally
            {
                if (!closed)
                {
                    menu.IsOpen = false;
                    window.Close();
                }

                unrecorded.Close();
                await RemoveAllAsync(model);
            }
        });

        // A change to the history while the menu is open queues one rebuild, never one within the change. Closing the
        // menu drops that rebuild: the closed menu stays empty, and a menu opened again before the old rebuild would have run
        // shows the current history, which the old rebuild never changes. A rebuild shows the history as it is when it runs,
        // never as it was when it was queued: after Clear history, no entry survives.
        [Fact]
        public Task AQueuedRebuild_ChangesNoLaterMenu_AndShowsTheHistoryAsItIsWhenItRuns() => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            ReplayRecordingTarget target = Recording(model);
            Record(target, Exchange(9, "/first"));
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion, recording: target);
            var menu = Find<ContextMenu>(window, "HistoryMenu");
            try
            {
                window.OpenHistoryView();
                Assert.Single(Entries(window));

                // Queued, not rebuilt within the change; then the menu closes and opens again before the rebuild runs.
                Record(target, Exchange(9, "/second"));
                Assert.Single(Entries(window));
                window.CloseHistoryView();
                Assert.Empty(menu.Items);
                window.OpenHistoryView();
                object[] reopened = menu.Items.Cast<object>().ToArray();
                Assert.Equal(2, Entries(window).Length);
                await SettleAsync();
                Assert.Equal(reopened, menu.Items.Cast<object>());

                // Queued, then the menu closes and stays closed.
                Record(target, Exchange(9, "/third"));
                window.CloseHistoryView();
                await SettleAsync();
                Assert.Empty(menu.Items);

                // Queued, then the history changes again before the rebuild runs, which shows it as it is then.
                window.OpenHistoryView();
                Assert.Equal(3, Entries(window).Length);
                Record(target, Exchange(9, "/fourth"));
                model.ReplayHistory.Clear();
                Assert.Equal(3, Entries(window).Length);
                await SettleAsync();
                Assert.Equal(
                    Localization.GetLocalizedString("COMPOSER_HISTORY_EMPTY"),
                    Assert.IsType<MenuItem>(Assert.Single(menu.Items)).Header);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // Closing the menu, or the window with the menu open, aborts the rebuild that a change queued before the
        // dispatcher runs it: that very operation is aborted, not left queued for its own checks to stop once it runs, so it
        // no longer keeps the closed window alive. The closed menu stays empty, and the window is collected.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public Task ClosingTheMenuOrTheWindow_AbortsTheQueuedRebuild_SoTheWindowIsCollected(bool closeWindow) => _application.RunAsync(async () =>
        {
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            ReplayRecordingTarget target = Recording(model);
            try
            {
                Record(target, Exchange(9, "/first"));

                (DispatcherOperationStatus queued, DispatcherOperationStatus closed, ContextMenu menu, WeakReference window) =
                    QueueRebuildThenClose(target, closeWindow);

                Assert.Equal(DispatcherOperationStatus.Pending, queued);
                Assert.Equal(DispatcherOperationStatus.Aborted, closed);
                await SettleAsync();
                Assert.Empty(menu.Items);

                // The menu would keep its window alive.
                menu = null;
                await GcTestSupport.CollectAsync(window);
                Assert.False(window.IsAlive);
            }
            finally
            {
                await RemoveAllAsync(model);
            }
        });

        // A composer whose history menu was open, closed in either order with its menu; only a weak reference to it is
        // returned, so that the caller keeps nothing of it.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference OpenThenClose(ReplayRecordingTarget target, bool withMenuOpen)
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion, recording: target);
            window.OpenHistoryView();
            Assert.Single(Entries(window));
            if (!withMenuOpen)
            {
                window.CloseHistoryView();
                Assert.Empty(Find<ContextMenu>(window, "HistoryMenu").Items);
            }

            window.Close();
            return new WeakReference(window);
        }

        // A composer whose open history menu has a rebuild queued, by a row recorded on the UI thread, and that is closed
        // before the dispatcher runs it: by closing the menu, then the window, or by closing the window with the menu open.
        // Returns the status of that very rebuild while queued and once closed, and the menu, which the caller lets go of
        // before it collects; the window only by a weak reference, and the rebuild not at all, which would keep it alive.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static (DispatcherOperationStatus Queued, DispatcherOperationStatus Closed, ContextMenu Menu, WeakReference Window)
            QueueRebuildThenClose(ReplayRecordingTarget target, bool closeWindow)
        {
            var window = RequestComposerWindow.ForNewRequest(confirm: NoQuestion, recording: target);
            window.OpenHistoryView();
            Record(target, Exchange(9, "/queued"));
            DispatcherOperation rebuild = Assert.IsAssignableFrom<DispatcherOperation>(
                typeof(RequestComposerWindow)
                    .GetField("_historyRebuild", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(window));
            DispatcherOperationStatus queued = rebuild.Status;
            if (closeWindow)
            {
                window.Close();
            }
            else
            {
                window.CloseHistoryView();
            }

            DispatcherOperationStatus closed = rebuild.Status;
            if (!closeWindow)
            {
                window.Close();
            }

            return (queued, closed, Find<ContextMenu>(window, "HistoryMenu"), new WeakReference(window));
        }

        // Records sends in the model's Replays tab, as Replay and the composer do, which puts each in the history; their rows.
        private static ProxyConnectionModel[] Record(ReplayRecordingTarget target, params ReplayExchange[] exchanges)
        {
            using var cancellation = new CancellationTokenSource();
            using ReplayRecording recording = target.TryBegin(cancellation, _ => ReplayProvenance.Composed);
            Assert.NotNull(recording);
            var rows = new ProxyConnectionModel[exchanges.Length];
            for (int index = 0; index < exchanges.Length; index++)
            {
                recording.Recorder.Add(Sent(), exchanges[index]);
                rows[index] = recording.Recorder.LastRecorded;
            }

            return rows;
        }

        // A send as the Replays tab records it: the request that XMAT sent to the loopback port, with a credential, and its
        // response, or none.
        private static ReplayExchange Exchange(
            int port, string target, string method = "GET", byte[] body = null, bool answered = true) => new()
        {
            Outcome = answered ? ReplayOutcome.Completed : ReplayOutcome.Failed,
            Request = new ReplayMessage(
                $"{method} {target} HTTP/1.1",
                new[]
                {
                    new KeyValuePair<string, string>("Host", $"127.0.0.1:{port}"),
                    new KeyValuePair<string, string>("X-Token", "canary"),
                },
                null,
                body),
            Response = answered ? new ReplayMessage("HTTP/1.1 200 OK", null, null, "reply"u8.ToArray()) : null,
            RequestUri = new Uri($"http://127.0.0.1:{port}{target}"),
            StatusCode = answered ? 200 : 0,
            Started = SentAt,
        };

        // A send as ReplayOperation passes it to its sink.
        private static ReplayItemResult Sent() =>
            new(1, null, null, 1, ReplayOutcome.Completed, 200, TimeSpan.Zero, 5, 0, null, null);

        // Records into the model's Replays tab, on this test's UI thread.
        private static ReplayRecordingTarget Recording(CaptureAppModel model)
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            return new ReplayRecordingTarget(model, action => dispatcher.Invoke(action));
        }

        private static IReadOnlyList<ReplayHistoryPreview> Snapshot(CaptureAppModel model) => model.ReplayHistory.Snapshot();

        // The entries that the window's history menu lists, in its order.
        private static MenuItem[] Entries(Window window) =>
            Find<ContextMenu>(window, "HistoryMenu").Items.OfType<MenuItem>().Where(item => item.Tag is ReplayHistoryEntry).ToArray();

        private static MenuItem Child(MenuItem entry, string name) =>
            entry.Items.OfType<MenuItem>().Single(item => item.Name == name);

        // The request as the editors show it.
        private static string Request(Window window) => string.Join(
            "|",
            Find<TextBox>(window, "MethodBox").Text,
            Find<TextBox>(window, "UrlBox").Text,
            Find<TextBox>(window, "HeadersBox").Text,
            Find<TextBox>(window, "BodyBox").Text,
            Find<CheckBox>(window, "Base64Box").IsChecked,
            Find<TextBlock>(window, "BodyFileText").Text);

        // The transport options as the composer shows them: the HTTP version, Connect to another endpoint and its endpoint,
        // and Follow same-origin redirects and the most redirects.
        private static (ReplayHttpVersion, bool, string, bool, int) Transport(Window window) => (
            Assert.IsType<ReplayHttpVersion>(
                Assert.IsType<ComboBoxItem>(Find<ComboBox>(window, "HttpVersionBox").SelectedItem).Tag),
            Find<CheckBox>(window, "ConnectToBox").IsChecked == true,
            Find<TextBox>(window, "ConnectToText").Text,
            Find<CheckBox>(window, "FollowRedirectsBox").IsChecked == true,
            Assert.IsType<int>(Find<ComboBox>(window, "RedirectLimitBox").SelectedItem));

        // Everything that a load that does not happen leaves as it was: the request, the transport options and Inspect
        // session.
        private static string Editors(Window window) =>
            string.Join("|", Request(window), Transport(window), Find<CheckBox>(window, "InspectSessionBox").IsChecked);

        // Chooses an HTTP version as the user does.
        private static void ChooseVersion(Window window, ReplayHttpVersion version)
        {
            var box = Find<ComboBox>(window, "HttpVersionBox");
            box.SelectedItem = box.Items.Cast<ComboBoxItem>().Single(item => version.Equals(item.Tag));
        }

        // Records each question and gives the answer.
        private static Func<string, bool> Answer(List<string> questions, bool answer) => message =>
        {
            questions.Add(message);
            return answer;
        };

        private static T Find<T>(Window window, string name) where T : class =>
            Assert.IsType<T>(window.FindName(name));

        private static string Status(Window window) => Find<TextBlock>(window, "StatusText").Text;

        private static WebServiceDeviceCaptureController ControllerOf(ICaptureDeviceContext context) =>
            (WebServiceDeviceCaptureController)context.CaptureController;

        private static WebServiceDeviceCaptureController Replays(CaptureAppModel model) =>
            ControllerOf(model.GetOrAddReplayContext());

        // A read-only tab with these rows, as a capture loaded from a file adds one, whatever its name.
        private static ICaptureDeviceContext AddTab(CaptureAppModel model, string name, params ProxyConnectionModel[] rows)
        {
            var context = new CaptureDeviceContextModel(
                DeviceType.Unknown, name, CaptureType.WebProxy, true, null, hostListPolicy: model.HostListPolicy);
            foreach (ProxyConnectionModel row in rows)
            {
                ControllerOf(context).ProxyConnections.Add(row);
            }

            model.CaptureDeviceContexts.Add(context);
            return context;
        }

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

        // Lets the dispatcher run everything queued above idle priority, a queued rebuild included.
        private static async Task SettleAsync() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        // Shows the window off screen, activated, as a menu that it opens needs.
        private static async Task ShowOffscreenAsync(Window window)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -12000;
            window.Top = -12000;
            window.ShowInTaskbar = false;
            window.Show();
            await SettleAsync();
        }
    }
}
