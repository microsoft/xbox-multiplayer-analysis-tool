// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using XMAT.Operations;
using XMAT.Operations.Inspection;
using XMAT.SharedInterfaces;
using XMAT.Tests.Replay;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Tests
{
    // Body previews in the views (Slice G): each input is previewed once, and a preview is shown only while it is the
    // latest of the view's current input and the view is loaded; each kind of message gets its framing; empty and
    // not-kept bodies and the notices are shown apart from the body; the composer's response and WebSocket messages.
    // The tests complete previews themselves (ControlledPreviews), or let the operation run them in memory or over
    // loopback.
    [Collection(CaptureUiApplicationCollection.Name)]
    public class BodyPreviewViewTests
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

        private readonly CaptureUiApplicationFixture _application;

        public BodyPreviewViewTests(CaptureUiApplicationFixture application)
        {
            _application = application;
        }

        static BodyPreviewViewTests()
        {
            Localization.LoadLanguage("en-us");
        }

        private static string Pending => Localization.GetLocalizedString("BODY_PREVIEW_PENDING");

        // The headers, body and framing that one selection sets make one preview. A change cancels the preview in
        // progress at once, and only the latest preview is shown: an earlier one that completes anyway is dropped, also
        // when it was of the same input as the latest.
        [Fact]
        public Task ChangingTheInputs_PreviewsEachOnce_AndShowsOnlyTheLatestPreview() => _application.RunAsync(async () =>
        {
            var previews = new ControlledPreviews();
            var view = new RequestResponseView { PreviewRunner = previews.Run };
            Window host = await ShowAsync(view);
            try
            {
                Dictionary<string, string> firstHeaders = Headers("Content-Type: text/plain");
                byte[] firstBody = "first"u8.ToArray();
                SetInputs(view, firstHeaders, firstBody);
                await SettleAsync();
                PreviewCall first = Assert.Single(previews.Calls);
                Assert.Equal(("text/plain", BodyFraming.Advisory), (first.Request.ContentType, first.Request.Framing));
                Assert.Equal((Pending, string.Empty), (Message(view, "BodyMessage"), BodyText(view)));

                SetInputs(view, Headers("Content-Type: text/plain"), "second"u8.ToArray());
                Assert.True(first.Token.IsCancellationRequested);
                await SettleAsync();
                SetInputs(view, firstHeaders, firstBody);
                await SettleAsync();

                Assert.Equal(3, previews.Calls.Count);
                Assert.Equal(
                    new[] { true, true, false }, previews.Calls.Select(call => call.Token.IsCancellationRequested));
                previews.Complete(2, Preview("latest"));
                await SettleAsync();
                Assert.Equal(("latest", (string)null), (BodyText(view), Message(view, "BodyMessage")));

                previews.Complete(0, Preview("an earlier preview of the same input"));
                previews.Complete(1, Preview("an earlier preview of another input"));
                await SettleAsync();
                Assert.Equal("latest", BodyText(view));
            }
            finally
            {
                host.Close();
            }
        });

        // A preview whose input is gone when it completes is dropped, although no newer preview started: here the
        // selection was cleared, so the view goes on showing nothing.
        [Fact]
        public Task APreviewOfAnInputThatIsGone_IsDropped() => _application.RunAsync(async () =>
        {
            var previews = new ControlledPreviews();
            var view = new RequestResponseView { PreviewRunner = previews.Run };
            Window host = await ShowAsync(view);
            try
            {
                SetInputs(view, Headers("Content-Type: text/plain"), "first"u8.ToArray());
                await SettleAsync();
                PreviewCall call = Assert.Single(previews.Calls);

                SetInputs(view, null, null);
                await SettleAsync();
                Assert.True(call.Token.IsCancellationRequested);
                previews.Complete(0, Preview("first"));
                await SettleAsync();

                Assert.Single(previews.Calls);
                Assert.Equal(((string)null, string.Empty), (Message(view, "BodyMessage"), BodyText(view)));
                Assert.Empty(Roots(view, "JsonTree"));
            }
            finally
            {
                host.Close();
            }
        });

        // Unloading the view cancels its preview, and a result that arrives afterwards is never shown; loaded again,
        // the view previews its input again. Loaded again while its preview runs, or while it shows the preview of its
        // input, the view starts no other preview.
        [Fact]
        public Task UnloadingTheView_CancelsItsPreview_AndDropsALateResult() => _application.RunAsync(async () =>
        {
            var previews = new ControlledPreviews();
            var view = new RequestResponseView { PreviewRunner = previews.Run };
            Window host = await ShowAsync(view);
            try
            {
                SetInputs(view, Headers("Content-Type: text/plain"), "body"u8.ToArray());
                await SettleAsync();
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                await SettleAsync();
                PreviewCall first = Assert.Single(previews.Calls);
                Assert.False(first.Token.IsCancellationRequested);

                host.Content = null;
                await SettleAsync();
                Assert.True(first.Token.IsCancellationRequested);
                previews.Complete(0, Preview("late"));
                await SettleAsync();
                Assert.Equal((Pending, string.Empty), (Message(view, "BodyMessage"), BodyText(view)));

                host.Content = view;
                await SettleAsync();
                Assert.Equal(2, previews.Calls.Count);
                previews.Complete(1, Preview("shown"));
                await SettleAsync();
                Assert.Equal("shown", BodyText(view));

                host.Content = null;
                await SettleAsync();
                host.Content = view;
                await SettleAsync();
                Assert.Equal(2, previews.Calls.Count);
                Assert.Equal("shown", BodyText(view));
            }
            finally
            {
                host.Close();
            }
        });

        // A captured row's bodies are previewed as stored, their chunked framing decoded only when it parses
        // (Advisory); a Replays-tab row's as the HTTP stack delivered them (Decoded). A row whose headers change, as
        // when its response arrives, is previewed again with them.
        [Fact]
        public Task Rows_ArePreviewedWithTheFramingOfTheirKind_AndAgainWhenTheirHeadersChange() => _application.RunAsync(async () =>
        {
            var controller = new WebServiceDeviceCaptureController("Test device", DeviceType.Unknown, true);
            ProxyConnectionModel captured = Row(
                1, "Transfer-Encoding: chunked\r\nContent-Type: text/plain", "5\r\nhello\r\n0\r\n\r\n"u8.ToArray());
            ProxyConnectionModel replayed = Row(2, "Content-Type: text/plain", "hello"u8.ToArray());
            replayed.Replay = ReplayProvenance.Composed;
            controller.ProxyConnections.Add(captured);
            controller.ProxyConnections.Add(replayed);
            var view = new CaptureConnectionView { AutoScroll = false };
            var grid = Find<DataGrid>(view, "ConnectionList");
            grid.IsSynchronizedWithCurrentItem = false;
            var requests = new ControlledPreviews();
            var responses = new ControlledPreviews();
            Find<RequestResponseView>(view, "RequestViewControl").PreviewRunner = requests.Run;
            Find<RequestResponseView>(view, "ResponseViewControl").PreviewRunner = responses.Run;
            view.DataContext = new TestCaptureDeviceContext(controller);
            Window host = await ShowAsync(view);
            try
            {
                grid.SelectedItem = captured;
                await SettleAsync();
                BodyPreviewRequest stored = Assert.Single(responses.Calls).Request;
                Assert.Equal(
                    (BodyFraming.Advisory, "chunked", "text/plain"),
                    (stored.Framing, stored.TransferEncoding, stored.ContentType));
                Assert.Equal(BodyFraming.Advisory, Assert.Single(requests.Calls).Request.Framing);

                grid.SelectedItem = replayed;
                await SettleAsync();
                Assert.Equal(2, responses.Calls.Count);
                Assert.Equal(BodyFraming.Decoded, responses.Calls[1].Request.Framing);
                Assert.Equal(BodyFraming.Decoded, requests.Calls[1].Request.Framing);

                replayed.UpdateFromDataRecord(new Record(new Dictionary<string, string>
                {
                    [WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders] =
                        "HTTP/1.1 200 OK\r\nContent-Type: application/json",
                }));
                await SettleAsync();
                Assert.Equal(3, responses.Calls.Count);
                Assert.True(responses.Calls[1].Token.IsCancellationRequested);
                BodyPreviewRequest changed = responses.Calls[2].Request;
                Assert.Equal(("application/json", BodyFraming.Decoded), (changed.ContentType, changed.Framing));
                Assert.Equal("hello"u8.ToArray(), changed.Body.ToArray());
            }
            finally
            {
                host.Close();
            }
        });

        // With the headers and framing unchanged, a new body alone, as when the selected row's response body arrives,
        // cancels the preview in progress and is previewed once; its preview is shown, never the earlier body's,
        // whether that one was still running or already shown.
        [Fact]
        public Task ANewBodyAlone_IsPreviewedOnce_AndReplacesTheEarlierBodysPreview() => _application.RunAsync(async () =>
        {
            var previews = new ControlledPreviews();
            var view = new RequestResponseView { PreviewRunner = previews.Run };
            Window host = await ShowAsync(view);
            try
            {
                Dictionary<string, string> headers = Headers("Content-Type: text/plain");
                SetInputs(view, headers, "first"u8.ToArray());
                await SettleAsync();
                PreviewCall first = Assert.Single(previews.Calls);

                view.BodyBytes = "second"u8.ToArray();
                Assert.True(first.Token.IsCancellationRequested);
                Assert.Equal((Pending, string.Empty), (Message(view, "BodyMessage"), BodyText(view)));
                await SettleAsync();
                Assert.Equal(2, previews.Calls.Count);
                BodyPreviewRequest second = previews.Calls[1].Request;
                Assert.Equal(("text/plain", BodyFraming.Advisory), (second.ContentType, second.Framing));
                Assert.Equal("second"u8.ToArray(), second.Body.ToArray());

                previews.Complete(0, Preview("first"));
                await SettleAsync();
                Assert.Equal((Pending, string.Empty), (Message(view, "BodyMessage"), BodyText(view)));
                previews.Complete(1, Preview("second"));
                await SettleAsync();
                Assert.Equal("second", BodyText(view));

                // A body that arrives after the earlier one's preview was shown replaces it at once, and is previewed
                // once too.
                view.BodyBytes = "third"u8.ToArray();
                Assert.Equal((Pending, string.Empty), (Message(view, "BodyMessage"), BodyText(view)));
                await SettleAsync();
                Assert.Equal(3, previews.Calls.Count);
                Assert.Equal("third"u8.ToArray(), previews.Calls[2].Request.Body.ToArray());
                previews.Complete(2, Preview("third"));
                await SettleAsync();
                Assert.Equal("third", BodyText(view));
                Assert.Same(headers, view.HeaderDictionary);
                Assert.False(view.BodyIsDecoded);
            }
            finally
            {
                host.Close();
            }
        });

        // An empty body, and a body that XMAT did not keep, say so instead of showing an empty body. Notices are shown
        // in the message bars of both tabs, never in the body.
        [Fact]
        public Task EmptyAndNotKeptBodies_SayWhy_AndNoticesStayApartFromTheBody() => _application.RunAsync(async () =>
        {
            var previews = new ControlledPreviews();
            var view = new RequestResponseView { PreviewRunner = previews.Run };
            Window host = await ShowAsync(view);
            try
            {
                SetInputs(view, Headers(RequestReplayDraft.ResponseBodyNotKeptHeader + ": 2048"), Array.Empty<byte>());
                await SettleAsync();
                Assert.True(previews.Calls[0].Request.NotRetained);
                previews.Complete(0, Preview(null, BodyPreviewState.NotRetained));
                await SettleAsync();
                string notKept = Localization.GetLocalizedString("BODY_PREVIEW_NOT_RETAINED");
                Assert.Equal(
                    (notKept, notKept, string.Empty),
                    (Message(view, "BodyMessage"), Message(view, "JsonMessage"), BodyText(view)));

                SetInputs(view, Headers("Content-Type: text/plain"), Array.Empty<byte>());
                await SettleAsync();
                Assert.False(previews.Calls[1].Request.NotRetained);
                previews.Complete(1, Preview(null, BodyPreviewState.Empty));
                await SettleAsync();
                string empty = Localization.GetLocalizedString("BODY_PREVIEW_EMPTY");
                Assert.Equal((empty, empty), (Message(view, "BodyMessage"), Message(view, "JsonMessage")));

                SetInputs(view, Headers("Content-Type: application/octet-stream"), new byte[] { 0, 1, 2 });
                await SettleAsync();
                previews.Complete(
                    2,
                    Preview(
                        "00 01 02", BodyPreviewState.Truncated, BodyPreviewNotice.Binary, BodyPreviewNotice.TextLimit));
                await SettleAsync();
                BodyPreviewLimits limits = BodyPreviewLimits.Default;
                string notices = Localization.GetLocalizedString("BODY_PREVIEW_NOTICE_BINARY") + Environment.NewLine +
                    Localization.GetLocalizedString(
                        "BODY_PREVIEW_NOTICE_TEXT_LIMIT",
                        limits.MaxTextChars,
                        limits.MaxTextLines,
                        limits.MaxByteDumpBytes);
                Assert.Equal(("00 01 02", notices), (BodyText(view), Message(view, "BodyMessage")));
                Assert.Equal(
                    notices + Environment.NewLine + Localization.GetLocalizedString("BODY_PREVIEW_NO_JSON"),
                    Message(view, "JsonMessage"));
            }
            finally
            {
                host.Close();
            }
        });

        // The JSON tree expands the root of a single document only: no other node, and none of several documents.
        [Fact]
        public Task JsonTree_ExpandsOnlyTheRootOfASingleDocument() => _application.RunAsync(async () =>
        {
            var previews = new ControlledPreviews();
            var view = new RequestResponseView { PreviewRunner = previews.Run };
            Window host = await ShowAsync(view);
            try
            {
                SetInputs(view, Headers("Content-Type: application/json"), "{}"u8.ToArray());
                await SettleAsync();
                previews.Complete(
                    0, Preview("{}", documents: Node("[root]", Node("items", Node("0=1")), Node("name=xmat"))));
                await SettleAsync();
                JsonTreeViewItem root = Assert.Single(Roots(view, "JsonTree"));
                Assert.Equal(("[root]", true), (root.PropertyDescriptor, root.AutoExpandJson));
                Assert.Equal(
                    new[] { "items", "name=xmat" }, root.JsonTreeViewItems.Select(child => child.PropertyDescriptor));
                Assert.All(root.JsonTreeViewItems, child => Assert.False(child.AutoExpandJson));
                Assert.False(root.JsonTreeViewItems[0].JsonTreeViewItems[0].AutoExpandJson);
                Assert.Null(Message(view, "JsonMessage"));

                SetInputs(view, Headers("Content-Type: application/json"), "1 2"u8.ToArray());
                await SettleAsync();
                previews.Complete(1, Preview("1 2", documents: new[] { Node("[root]=1"), Node("[root]=2") }));
                await SettleAsync();
                List<JsonTreeViewItem> roots = Roots(view, "JsonTree");
                Assert.Equal(2, roots.Count);
                Assert.All(roots, document => Assert.False(document.AutoExpandJson));
            }
            finally
            {
                host.Close();
            }
        });

        // The UI thread keeps running while a preview runs on the thread pool, and shows the preview when it ends.
        [Fact]
        public Task TheDispatcherStaysResponsive_WhileAPreviewRuns() => _application.RunAsync(async () =>
        {
            using var release = new ManualResetEventSlim();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var view = new RequestResponseView
            {
                PreviewRunner = (request, token) => Task.Run(
                    () =>
                    {
                        started.TrySetResult();
                        release.Wait(token);
                        return OperationResult<BodyPreview>.Success(Preview("done"));
                    },
                    token),
            };
            Window host = await ShowAsync(view);
            try
            {
                SetInputs(view, Headers("Content-Type: text/plain"), "body"u8.ToArray());
                await started.Task.WaitAsync(Patience);

                bool ran = false;
                await Dispatcher.CurrentDispatcher.InvokeAsync(() => ran = true).Task.WaitAsync(Patience);
                Assert.True(ran);
                Assert.Equal(Pending, Message(view, "BodyMessage"));

                release.Set();
                await WaitUntilAsync(() => BodyText(view) == "done");
            }
            finally
            {
                release.Set();
                host.Close();
            }
        });

        // The composer previews its response as the HTTP stack delivered it, already de-chunked (Decoded): a body that
        // itself looks like chunked framing is shown as it is, never de-chunked again.
        [Fact]
        public Task ComposerResponse_IsPreviewedAsDelivered_AndNeverDechunkedAgain() => _application.RunAsync(async () =>
        {
            const string framed = "5\r\nhello\r\n0\r\n\r\n";
            await using var server = new LoopbackHttpServer(_ => new LoopbackResponse(
                Headers: "Content-Type: text/plain\r\n", Body: Encoding.ASCII.GetBytes(framed), Chunked: true));
            using var client = new RequestReplayClient();
            RequestComposerWindow window = RequestComposerWindow.ForNewRequest(client, NoQuestion);
            var response = Find<RequestResponseView>(window, "ResponseView");
            var operation = response.PreviewRunner;
            var previews = new ControlledPreviews();
            response.PreviewRunner = previews.Run;
            try
            {
                await ShowOffscreenAsync(window);
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/framed";
                await window.SendAsync();
                await SettleAsync();

                BodyPreviewRequest request = Assert.Single(previews.Calls).Request;
                Assert.Equal(
                    (BodyFraming.Decoded, (string)null, "text/plain"),
                    (request.Framing, request.TransferEncoding, request.ContentType));
                Assert.Equal(Encoding.ASCII.GetBytes(framed), request.Body.ToArray());

                response.PreviewRunner = operation;
                await window.SendAsync();
                await WaitUntilAsync(() => BodyText(response) == framed);
            }
            finally
            {
                window.Close();
            }
        });

        // Closing the composer cancels its response's preview at once, and a result that arrives afterwards is not
        // shown.
        [Fact]
        public Task ClosingTheComposer_CancelsItsResponsePreviewAtOnce() => _application.RunAsync(async () =>
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok("reply"));
            using var client = new RequestReplayClient();
            RequestComposerWindow window = RequestComposerWindow.ForNewRequest(client, NoQuestion);
            var response = Find<RequestResponseView>(window, "ResponseView");
            var previews = new ControlledPreviews();
            response.PreviewRunner = previews.Run;
            bool closed = false;
            try
            {
                await ShowOffscreenAsync(window);
                Find<TextBox>(window, "UrlBox").Text = server.Origin + "/reply";
                await window.SendAsync();
                await SettleAsync();
                PreviewCall call = Assert.Single(previews.Calls);

                window.Close();
                closed = true;
                Assert.True(call.Token.IsCancellationRequested);
                previews.Complete(0, Preview("late"));
                await SettleAsync();
                Assert.Equal(string.Empty, BodyText(response));
            }
            finally
            {
                if (!closed)
                {
                    window.Close();
                }
            }
        });

        // A WebSocket message is previewed once, as captured, for its Text, JSON and Hex tabs; selecting another
        // message previews that one instead.
        [Fact]
        public Task WebSocketMessage_IsPreviewedOnceForItsTextJsonAndHexTabs() => _application.RunAsync(async () =>
        {
            var previews = new ControlledPreviews();
            var messages = new WebSocketMessagesCollection();
            WebSocketMessageModel text = Frame(WebSocketMessageType.Text, "{\"a\":1}"u8.ToArray());
            WebSocketMessageModel binary = Frame(WebSocketMessageType.Binary, new byte[] { 1, 2 });
            messages.AddMessage(text);
            messages.AddMessage(binary);
            var view = new WebSocketMessagesView { ItemsSource = messages, PreviewRunner = previews.Run };
            Window host = await ShowAsync(view);
            try
            {
                view.SelectedItem = text;
                await SettleAsync();
                PreviewCall call = Assert.Single(previews.Calls);
                Assert.Equal(text.PreviewRequest(), call.Request);
                previews.Complete(
                    0, Preview("{\"a\":1}", documents: Node("[root]", Node("a=1")), hex: "7B 22 61 22 3A 31 7D"));
                await SettleAsync();

                Assert.Single(previews.Calls);
                Assert.Equal("{\"a\":1}", Find<TextBox>(view, "PayloadTextBox").Text);
                Assert.Equal("7B 22 61 22 3A 31 7D", Find<TextBox>(view, "PayloadHexBox").Text);
                Assert.Equal("[root]", Assert.Single(Roots(view, "PayloadJsonTree")).PropertyDescriptor);
                Assert.Equal(
                    ((string)null, (string)null), (Message(view, "PreviewMessage"), Message(view, "JsonMessage")));

                view.SelectedItem = binary;
                await SettleAsync();
                Assert.Equal(2, previews.Calls.Count);
                Assert.Equal(binary.PreviewRequest(), previews.Calls[1].Request);
                Assert.Equal(
                    (Pending, string.Empty, string.Empty),
                    (Message(view, "PreviewMessage"), Find<TextBox>(view, "PayloadTextBox").Text,
                        Find<TextBox>(view, "PayloadHexBox").Text));
                Assert.Empty(Roots(view, "PayloadJsonTree"));
            }
            finally
            {
                host.Close();
            }
        });

        // A message captured only in part says so, and shows no JSON tree even when its start is JSON.
        [Fact]
        public Task TruncatedWebSocketMessage_SaysItWasCut_AndShowsNoJsonTree() => _application.RunAsync(async () =>
        {
            var messages = new WebSocketMessagesCollection();
            WebSocketMessageModel cut = Frame(WebSocketMessageType.Text, "{\"a\":1}"u8.ToArray(), truncated: true);
            messages.AddMessage(cut);
            var view = new WebSocketMessagesView { ItemsSource = messages };
            Window host = await ShowAsync(view);
            try
            {
                view.SelectedItem = cut;
                string storedPrefix = Localization.GetLocalizedString("BODY_PREVIEW_NOTICE_STORED_PREFIX", 7L);
                await WaitUntilAsync(() => Message(view, "PreviewMessage")?.Contains(storedPrefix) == true);

                Assert.Empty(Roots(view, "PayloadJsonTree"));
                Assert.Equal(Localization.GetLocalizedString("BODY_PREVIEW_NO_JSON"), Message(view, "JsonMessage"));
                Assert.Contains("{\"a\":1}", Find<TextBox>(view, "PayloadTextBox").Text);
            }
            finally
            {
                host.Close();
            }
        });

        private static readonly Func<string, bool> NoQuestion =
            message => throw new Xunit.Sdk.XunitException("Nothing needs confirmation here: " + message);

        // A window off screen that shows the view, loaded.
        private static async Task<Window> ShowAsync(FrameworkElement content)
        {
            var host = new Window
            {
                Width = 1100,
                Height = 700,
                Left = -12000,
                Top = -12000,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Content = content,
            };
            host.Show();
            await SettleAsync();
            return host;
        }

        private static async Task ShowOffscreenAsync(Window window)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -12000;
            window.Top = -12000;
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            window.Show();
            await SettleAsync();
        }

        // Lets the dispatcher run everything queued down to idle: bindings, loading, refreshes and completions.
        private static async Task SettleAsync() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            var clock = Stopwatch.StartNew();
            while (!condition())
            {
                Assert.True(clock.Elapsed < Patience, "The condition did not hold in time.");
                await Task.Delay(10);
            }
        }

        // The inputs that a selection sets, in one dispatcher turn.
        private static void SetInputs(RequestResponseView view, Dictionary<string, string> headers, byte[] body)
        {
            view.HeaderDictionary = headers;
            view.BodyBytes = body;
            view.BodyIsDecoded = false;
        }

        private static Dictionary<string, string> Headers(params string[] lines)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines)
            {
                int colon = line.IndexOf(':');
                headers[line[..colon]] = line[(colon + 1)..].Trim();
            }

            return headers;
        }

        private static ProxyConnectionModel Row(int number, string responseHeaders, byte[] responseBody) =>
            new(number, "200", DateTime.UtcNow, TimeSpan.Zero, "https", "example.test:443", "GET", "/item",
                "GET /item HTTP/1.1\r\nHost: example.test", Array.Empty<byte>(),
                "HTTP/1.1 200 OK\r\n" + responseHeaders, responseBody, "127.0.0.1");

        private static WebSocketMessageModel Frame(WebSocketMessageType type, byte[] payload, bool truncated = false) =>
            new(new WebSocketMessageEventArgs { MessageType = type, Message = payload, PayloadTruncated = truncated });

        private static BodyPreview Preview(
            string text,
            BodyPreviewState state = BodyPreviewState.Complete,
            params string[] notices) =>
            new(state, text == null ? BodyPreviewTextKind.None : BodyPreviewTextKind.Text, text,
                Array.Empty<BodyPreviewNode>(), null, notices, text?.Length ?? 0, text?.Length ?? 0, text?.Length ?? 0,
                text == null ? null : "utf-8");

        private static BodyPreview Preview(string text, BodyPreviewNode[] documents, string hex = null) =>
            Preview(text) with { JsonDocuments = documents, Hex = hex };

        private static BodyPreview Preview(string text, BodyPreviewNode documents, string hex = null) =>
            Preview(text, new[] { documents }, hex);

        private static BodyPreviewNode Node(string label, params BodyPreviewNode[] children) => new(label, children);

        private static string BodyText(RequestResponseView view) => Find<TextBox>(view, "BodyTextBox").Text;

        // A message bar's text, or null while it is hidden.
        private static string Message(FrameworkElement view, string name)
        {
            var bar = Find<TextBlock>(view, name);
            return bar.Visibility == Visibility.Visible ? bar.Text : null;
        }

        private static List<JsonTreeViewItem> Roots(FrameworkElement view, string name) =>
            (Find<TreeView>(view, name).ItemsSource ?? Array.Empty<JsonTreeViewItem>())
                .Cast<JsonTreeViewItem>()
                .ToList();

        private static T Find<T>(FrameworkElement view, string name) where T : class =>
            Assert.IsType<T>(view.FindName(name));

        // Previews that complete when the test says so.
        private sealed class ControlledPreviews
        {
            internal List<PreviewCall> Calls { get; } = new();

            internal Task<OperationResult<BodyPreview>> Run(BodyPreviewRequest request, CancellationToken token)
            {
                var call = new PreviewCall(request, token);
                Calls.Add(call);
                return call.Completion.Task;
            }

            internal void Complete(int index, BodyPreview preview) =>
                Calls[index].Completion.SetResult(OperationResult<BodyPreview>.Success(preview));
        }

        private sealed record PreviewCall(BodyPreviewRequest Request, CancellationToken Token)
        {
            internal TaskCompletionSource<OperationResult<BodyPreview>> Completion { get; } = new();
        }

        // A capture row's update as a running capture sends it, with only the given fields.
        private sealed class Record(Dictionary<string, string> values) : IDataRecord
        {
            public int RowId => 99;

            public long Int(string fieldName) => 0;

            public string Str(string fieldName) => values.TryGetValue(fieldName, out string value) ? value : null;
        }

        private sealed class TestCaptureDeviceContext : ICaptureDeviceContext
        {
            internal TestCaptureDeviceContext(IDeviceCaptureController captureController)
            {
                CaptureController = captureController;
            }

            public DeviceType DeviceType => DeviceType.Unknown;

            public CaptureType CaptureType => CaptureType.WebProxy;

            public string DeviceName => "Test device";

            public bool IsSelected { get; set; }

            public bool IsReadOnly { get; set; }

            public System.Drawing.Color DeviceColor { get; set; }

            public IDeviceCaptureController CaptureController { get; }
        }
    }
}
