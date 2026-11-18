// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using XMAT.Models;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class RequestComposerWindowTests
    {
        private readonly CaptureUiApplicationFixture _application;

        public RequestComposerWindowTests(CaptureUiApplicationFixture application)
        {
            _application = application;
        }

        static RequestComposerWindowTests()
        {
            Localization.LoadLanguage("en-us");
        }

        [Fact]
        public Task Window_UsesTheSharedApplicationDispatcher() => OnDispatcher(() =>
        {
            Assert.NotNull(Application.Current);
            Assert.True(Application.Current.Dispatcher.CheckAccess());
            return Task.CompletedTask;
        });

        // XMAT exits when its last window closes, so a window registered by a failed launch would keep it running.
        [Fact]
        public Task Window_FailedLaunchLeavesNoWindowBehind() => OnDispatcher(() =>
        {
            int windows = Application.Current.Windows.Count;

            Assert.Throws<ArgumentException>(() => new RequestComposerWindow(
                RequestReplayDraftTests.Capture("CONNECT", "example.com:443", [])));
            Assert.Throws<ArgumentNullException>(() => new RequestComposerWindow(null));

            Assert.Equal(windows, Application.Current.Windows.Count);
            return Task.CompletedTask;
        });

        [Fact]
        public Task Window_ComposesANewRequestFromNothing() => OnDispatcher(async () =>
        {
            using var handler = new StubHandler(async (request, token) =>
            {
                Assert.Equal("POST", request.Method.Method);
                Assert.Equal("https://example.com/new?x=1", request.RequestUri.AbsoluteUri);
                Assert.Equal("one", string.Join(",", request.Headers.GetValues("X-Test")));
                Assert.Equal("{}", await request.Content.ReadAsStringAsync(token));
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            });
            using var httpClient = new HttpClient(handler);
            using var client = new RequestReplayClient(httpClient);
            int confirmations = 0;
            var window = RequestComposerWindow.ForNewRequest(client, _ => { ++confirmations; return true; });
            try
            {
                Assert.Equal("GET", Find<TextBox>(window, "MethodBox").Text);
                Assert.Equal("", Find<TextBox>(window, "UrlBox").Text);
                await window.SendAsync();
                Assert.Equal(0, handler.CallCount);
                Assert.Contains("Request failed:", Find<TextBlock>(window, "StatusText").Text);

                Find<TextBox>(window, "MethodBox").Text = "POST";
                Find<TextBox>(window, "UrlBox").Text = "https://example.com/new?x=1";
                Find<TextBox>(window, "HeadersBox").Text = "X-Test: one";
                Find<TextBox>(window, "BodyBox").Text = "{}";
                await window.SendAsync();

                Assert.Equal(1, confirmations);
                Assert.Equal("202", window.LastResult.Response.Status);
            }
            finally
            {
                window.Close();
            }
        });

        [Fact]
        public Task Window_PrefillsEditorsAndHonorsDeclinedConfirmation() => OnDispatcher(async () =>
        {
            int confirmations = 0;
            using var handler = new StubHandler((_, _) => throw new Xunit.Sdk.XunitException("Must not send."));
            using var httpClient = new HttpClient(handler);
            using var client = new RequestReplayClient(httpClient);
            var capture = RequestReplayDraftTests.Capture("POST", "/original", "original"u8.ToArray());
            var window = new RequestComposerWindow(capture, client, _ => { ++confirmations; return false; });
            try
            {
                Assert.Equal("POST", Find<TextBox>(window, "MethodBox").Text);
                Assert.Equal("https://example.com:8443/original", Find<TextBox>(window, "UrlBox").Text);
                Assert.Equal("original", Find<TextBox>(window, "BodyBox").Text);
                await window.SendAsync();
                Assert.Equal(1, confirmations);
                Assert.Equal(0, handler.CallCount);
                Assert.Null(window.LastResult);
                Assert.True(Find<Button>(window, "SendButton").IsEnabled);
            }
            finally
            {
                window.Close();
            }
        });

        [Fact]
        public Task Window_SendsEditsAndShowsAnIndependentResponse() => OnDispatcher(async () =>
        {
            using var handler = new StubHandler(async (request, token) =>
            {
                Assert.Equal("PUT", request.Method.Method);
                Assert.Equal("https://example.com/edited", request.RequestUri.AbsoluteUri);
                Assert.Equal("edited", await request.Content.ReadAsStringAsync(token));
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("new response") };
            });
            using var httpClient = new HttpClient(handler);
            using var client = new RequestReplayClient(httpClient);
            var capture = RequestReplayDraftTests.Capture("POST", "/original", "original"u8.ToArray());
            var window = new RequestComposerWindow(capture, client, _ => true);
            try
            {
                Find<TextBox>(window, "MethodBox").Text = "PUT";
                Find<TextBox>(window, "UrlBox").Text = "https://example.com/edited";
                Find<TextBox>(window, "BodyBox").Text = "edited";
                await window.SendAsync();

                Assert.Equal("201", window.LastResult.Response.Status);
                Assert.Contains("201", Find<TextBlock>(window, "StatusText").Text);
                Assert.Equal("new response"u8.ToArray(), Find<RequestResponseView>(window, "ResponseView").BodyBytes);
                Assert.Equal("old response"u8.ToArray(), capture.ResponseBody);
                Assert.True(Find<Button>(window, "SendButton").IsEnabled);
                Assert.False(Find<Button>(window, "CancelSendButton").IsEnabled);
            }
            finally
            {
                window.Close();
            }
        });

        [Fact]
        public Task Window_CancelStopsSendingAndPreventsConcurrentSends() => OnDispatcher(async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var handler = new StubHandler(async (_, token) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
            using var httpClient = new HttpClient(handler);
            using var client = new RequestReplayClient(httpClient);
            var window = new RequestComposerWindow(
                RequestReplayDraftTests.Capture("GET", "/", []), client, _ => true);
            try
            {
                Task sending = window.SendAsync();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(Find<Button>(window, "SendButton").IsEnabled);
                Assert.False(Find<Grid>(window, "RequestEditor").IsEnabled);
                Assert.True(Find<Button>(window, "CancelSendButton").IsEnabled);
                await window.SendAsync();
                Assert.Equal(1, handler.CallCount);
                window.CancelSend();
                await sending.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.Null(window.LastResult);
                Assert.True(Find<Button>(window, "SendButton").IsEnabled);
                Assert.False(Find<Button>(window, "CancelSendButton").IsEnabled);
            }
            finally
            {
                window.Close();
            }
        });

        [Fact]
        public Task Window_CloseCancelsTheInFlightRequest() => OnDispatcher(async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var handler = new StubHandler(async (_, token) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
            using var httpClient = new HttpClient(handler);
            using var client = new RequestReplayClient(httpClient);
            var window = new RequestComposerWindow(
                RequestReplayDraftTests.Capture("GET", "/", []), client, _ => true);
            Task sending = window.SendAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            window.Close();
            await sending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(window.LastResult);
        });

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public Task Window_ReportsValidationOrTransportErrorsWithoutSendingSuccess(bool invalidInput) => OnDispatcher(async () =>
        {
            using var handler = new StubHandler((_, _) => throw new HttpRequestException("connection failed"));
            using var httpClient = new HttpClient(handler);
            using var client = new RequestReplayClient(httpClient);
            var window = new RequestComposerWindow(
                RequestReplayDraftTests.Capture("GET", "/", []), client, _ => true);
            try
            {
                if (invalidInput)
                    Find<TextBox>(window, "UrlBox").Text = "not a URL";
                await window.SendAsync();
                Assert.Null(window.LastResult);
                Assert.Contains("Request failed:", Find<TextBlock>(window, "StatusText").Text);
                Assert.Equal(invalidInput ? 0 : 1, handler.CallCount);
                Assert.True(Find<Button>(window, "SendButton").IsEnabled);
            }
            finally
            {
                window.Close();
            }
        });

        [Theory]
        [InlineData("one\ntwo\r\nthree")]
        [InlineData("caf\u00e9")]
        public Task Window_PreservesUneditedTextBytes(string body) => OnDispatcher(async () =>
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(body);
            using var handler = new StubHandler(async (request, token) =>
            {
                Assert.Equal(bytes, await request.Content.ReadAsByteArrayAsync(token));
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
            using var httpClient = new HttpClient(handler);
            using var client = new RequestReplayClient(httpClient);
            var window = new RequestComposerWindow(
                RequestReplayDraftTests.Capture("POST", "/", bytes), client, _ => true);
            try
            {
                await window.SendAsync();
                Assert.NotNull(window.LastResult);
            }
            finally
            {
                window.Close();
            }
        });

        [Fact]
        public Task Window_RendersAndSendsBinaryBodyWithoutModification() => OnDispatcher(async () =>
        {
            byte[] bytes = [0, 255, 128, 13, 10];
            using var handler = new StubHandler(async (request, token) =>
            {
                Assert.Equal(bytes, await request.Content.ReadAsByteArrayAsync(token));
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
            using var httpClient = new HttpClient(handler);
            using var client = new RequestReplayClient(httpClient);
            var window = new RequestComposerWindow(
                RequestReplayDraftTests.Capture("POST", "/", bytes), client, _ => true);
            try
            {
                window.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.True(window.IsVisible);
                Assert.True(Find<Grid>(window, "RequestEditor").ActualWidth > 0);
                Assert.True(Find<CheckBox>(window, "Base64Box").IsChecked);
                await window.SendAsync();
                Assert.NotNull(window.LastResult);
            }
            finally
            {
                window.Close();
            }
        });

        // Edit and resend and Compose a New Request record every send in the model's Replays tab, with where it comes from;
        // the captured row is unchanged.
        [Fact]
        public Task Window_RecordsEachSendInTheReplaysTab_WithWhereItComesFrom() => OnDispatcher(async () =>
        {
            using var handler = new StubHandler((_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("made") }));
            using var httpClient = new HttpClient(handler);
            using var client = new RequestReplayClient(httpClient);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            ReplayRecordingTarget recording = Recording(model);
            var capture = new ProxyConnectionModel(7, "200", DateTime.UtcNow, TimeSpan.Zero, "http", "127.0.0.1:9", "POST",
                "/original", "POST /original HTTP/1.1\r\n\r\n", "original"u8.ToArray(), "HTTP/1.1 200 OK\r\n\r\n",
                "old response"u8.ToArray(), "127.0.0.1");
            var edited = new RequestComposerWindow(capture, client, _ => true, recording, "Local PC");
            var composed = RequestComposerWindow.ForNewRequest(client, _ => true, recording);
            try
            {
                await edited.SendAsync();
                Find<TextBox>(composed, "UrlBox").Text = "http://127.0.0.1:9/new";
                await composed.SendAsync();

                var replays = (WebServiceDeviceCaptureController)model.GetOrAddReplayContext().CaptureController;
                Assert.Equal(
                    new[] { "Local PC #7, from the composer", "a request composed from scratch" },
                    replays.ProxyConnections.Select(row => row.ReplaySource));
                ProxyConnectionModel resent = replays.ProxyConnections[0];
                Assert.Equal(
                    (ReplaySourceKind.Edited, 7L, capture.RecordRowId),
                    (resent.Replay.Kind, resent.Replay.RequestNumber.Value, resent.Replay.RecordRowId.Value));
                Assert.Equal(("201", "POST", "/original"), (resent.Status, resent.Method, resent.Path));
                Assert.Equal("original"u8.ToArray(), resent.RequestBody);
                Assert.Equal("made"u8.ToArray(), replays.ProxyConnections[1].ResponseBody);
                Assert.Equal(ReplaySourceKind.Composed, replays.ProxyConnections[1].Replay.Kind);
                Assert.EndsWith("Recorded in the XMAT Replays tab.", Find<TextBlock>(edited, "StatusText").Text);
                Assert.True(model.CanModifyCaptureData);
                Assert.Equal("original"u8.ToArray(), capture.RequestBody);
                Assert.Equal("old response"u8.ToArray(), capture.ResponseBody);
                Assert.False(capture.IsReplay);
            }
            finally
            {
                edited.Close();
                composed.Close();
                await RemoveAllAsync(model);
            }
        });

        // While another capture-data operation runs, such as an import or a replay, the composer sends nothing, since it
        // could not record the send, and says so.
        [Fact]
        public Task Window_DoesNotSendWhileXmatIsBusy() => OnDispatcher(async () =>
        {
            using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
            using var httpClient = new HttpClient(handler);
            using var client = new RequestReplayClient(httpClient);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var window = new RequestComposerWindow(
                new ProxyConnectionModel(7, "200", DateTime.UtcNow, TimeSpan.Zero, "http", "127.0.0.1:9", "GET", "/",
                    "GET / HTTP/1.1\r\n\r\n", [], "HTTP/1.1 200 OK\r\n\r\n", [], "127.0.0.1"),
                client,
                _ => true,
                Recording(model),
                "Local PC");
            try
            {
                Assert.True(model.TryBeginCaptureDataOperation(out IDisposable import));
                await window.SendAsync();

                Assert.Equal(0, handler.CallCount);
                Assert.Equal(Localization.GetLocalizedString("COMPOSER_BUSY"), Find<TextBlock>(window, "StatusText").Text);
                Assert.True(Find<Button>(window, "SendButton").IsEnabled);
                Assert.Empty(model.CaptureDeviceContexts);

                import.Dispose();
                await window.SendAsync();

                Assert.Equal(1, handler.CallCount);
                var replays = (WebServiceDeviceCaptureController)model.GetOrAddReplayContext().CaptureController;
                Assert.Single(replays.ProxyConnections);
                Assert.True(model.CanModifyCaptureData);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // A send that the user cancels gets no row in the Replays tab, though the server may have received it: the status
        // says both.
        [Fact]
        public Task Window_CancelledSend_SaysItMayHaveReachedTheServer_AndIsNotRecorded() => OnDispatcher(async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var handler = new StubHandler(async (_, token) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
            using var httpClient = new HttpClient(handler);
            using var client = new RequestReplayClient(httpClient);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var window = new RequestComposerWindow(LoopbackGet(), client, _ => true, Recording(model), "Local PC");
            try
            {
                Task sending = window.SendAsync();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                window.CancelSend();
                await sending.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.Equal(
                    Localization.GetLocalizedString("COMPOSER_CANCELLED_NOT_RECORDED", CaptureAppModel.ReplayContextName),
                    Find<TextBlock>(window, "StatusText").Text);
                Assert.Null(window.LastResult);
                var replays = (WebServiceDeviceCaptureController)model.GetOrAddReplayContext().CaptureController;
                Assert.Empty(replays.ProxyConnections);
                Assert.True(model.CanModifyCaptureData);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // Exiting XMAT without the main window's Closing (Windows ending the session, Application.Shutdown) cancels a send in
        // progress without waiting for it and records nothing more; no send starts afterward.
        [Fact]
        public Task Window_ShutdownCancelsTheSendInProgress_AndNoSendStartsAfterward() => OnDispatcher(async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var handler = new StubHandler(async (_, token) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
            using var httpClient = new HttpClient(handler);
            using var client = new RequestReplayClient(httpClient);
            var model = new CaptureAppModel(Dispatcher.CurrentDispatcher);
            var window = new RequestComposerWindow(LoopbackGet(), client, _ => true, Recording(model), "Local PC");
            try
            {
                Task sending = window.SendAsync();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                model.BeginShutdown();
                Assert.False(sending.IsCompleted);
                await sending.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.Null(window.LastResult);
                var replays = (WebServiceDeviceCaptureController)model.GetOrAddReplayContext().CaptureController;
                Assert.Empty(replays.ProxyConnections);

                await window.SendAsync();

                Assert.Equal(1, handler.CallCount);
                Assert.Equal(Localization.GetLocalizedString("COMPOSER_BUSY"), Find<TextBlock>(window, "StatusText").Text);
                Assert.Empty(replays.ProxyConnections);
            }
            finally
            {
                window.Close();
                await RemoveAllAsync(model);
            }
        });

        // The request's fields, and the HTTP version, are named for automation as their labels read without the access
        // key, before anything has made the labels' automation peers, as a screen reader that starts at the focused field
        // finds them: a TextBox or ComboBox takes no name from a Label's Target, and the one that LabeledBy gives needs the
        // label's peer. All five are asked before any is checked. Each label still targets its field, for its access key,
        // and labels it for automation.
        [Fact]
        public Task Window_NamesTheRequestsFieldsForAutomation_AsTheirLabelsRead() => OnDispatcher(async () =>
        {
            var window = RequestComposerWindow.ForNewRequest();
            try
            {
                (string Label, string Field, string Name)[] fields =
                {
                    ("MethodLabel", "MethodBox", "Method"),
                    ("UrlLabel", "UrlBox", "URL"),
                    ("HeadersLabel", "HeadersBox", "Headers (one Name: value per line)"),
                    ("BodyLabel", "BodyBox", "Body"),
                    ("HttpVersionLabel", "HttpVersionBox", "HTTP version"),
                };

                Assert.All(fields, field => Assert.Null(UIElementAutomationPeer.FromElement(Find<Label>(window, field.Label))));
                Assert.Equal(
                    fields.Select(field => field.Name).ToArray(),
                    fields.Select(field => UIElementAutomationPeer.CreatePeerForElement(Field(window, field.Field)).GetName())
                        .ToArray());

                // A label's Target names a field that comes after it in the window, which its binding finds once the
                // window's dispatcher has run.
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.All(fields, field =>
                {
                    var label = Find<Label>(window, field.Label);
                    Assert.Same(Field(window, field.Field), label.Target);
                    Assert.Same(label, AutomationProperties.GetLabeledBy(Field(window, field.Field)));
                });
            }
            finally
            {
                window.Close();
            }
        });

        [Fact]
        public Task CaptureCommand_IsAvailableForHttpIncludingReadOnlyImports() => OnDispatcher(async () =>
        {
            var view = new CaptureConnectionView();
            var capture = RequestReplayDraftTests.Capture("GET", "/", []);
            Assert.True(WebServiceProxyCommands.EditAndResend.CanExecute(capture, view));
            Assert.False(WebServiceProxyCommands.EditAndResend.CanExecute(null, view));
            Assert.False(WebServiceProxyCommands.EditAndResend.CanExecute(
                RequestReplayDraftTests.Capture("CONNECT", "example.com:443", []), view));
            var grid = Assert.IsType<DataGrid>(view.FindName("ConnectionList"));
            Assert.Empty(grid.Items);
            grid.ItemsSource = new[] { capture };
            // Let initial bindings settle before simulating selection on the populated grid.
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            grid.SelectedItem = capture;
            Assert.Same(capture, grid.SelectedItem);
            var menu = grid.ContextMenu;
            menu.PlacementTarget = grid;
            try
            {
                menu.IsOpen = true;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Same(capture, grid.SelectedItem);
                var item = Assert.IsType<MenuItem>(Assert.Single(menu.Items.Cast<object>()));
                Assert.Same(grid, item.CommandTarget);
                Assert.Same(capture, item.CommandParameter);
                Assert.True(item.IsEnabled);
            }
            finally
            {
                menu.IsOpen = false;
            }
        });

        [Fact]
        public Task CaptureCommand_RightClickPreservesTheCaptureSelectionBinding() => OnDispatcher(async () =>
        {
            var view = new CaptureConnectionView();
            var grid = Assert.IsType<DataGrid>(view.FindName("ConnectionList"));
            var first = RequestReplayDraftTests.Capture("GET", "/first", []);
            var second = RequestReplayDraftTests.Capture("GET", "/second", []);
            grid.ItemsSource = new[] { first, second };
            var selection = new Selection { Selected = first };
            grid.SetBinding(DataGrid.SelectedItemProperty,
                new Binding(nameof(Selection.Selected)) { Source = selection, Mode = BindingMode.TwoWay });
            grid.Measure(new Size(600, 400));
            grid.Arrange(new Rect(0, 0, 600, 400));
            grid.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var binding = BindingOperations.GetBindingExpression(grid, DataGrid.SelectedItemProperty);
            Assert.NotNull(binding);
            var row = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromIndex(1));
            grid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
            {
                RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent,
                Source = row
            });
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            Assert.Same(second, grid.SelectedItem);
            Assert.Same(binding, BindingOperations.GetBindingExpression(grid, DataGrid.SelectedItemProperty));
            Assert.Same(second, selection.Selected);
        });

        public sealed class Selection
        {
            public ProxyConnectionModel Selected { get; set; }
        }

        private static T Find<T>(Window window, string name) where T : class =>
            Assert.IsType<T>(window.FindName(name));

        // A field of the window: a TextBox, or a ComboBox for the HTTP version.
        private static Control Field(Window window, string name) => Assert.IsAssignableFrom<Control>(window.FindName(name));

        // A captured GET to the discard port on this machine; tests that send it stub the transport.
        private static ProxyConnectionModel LoopbackGet() =>
            new(7, "200", DateTime.UtcNow, TimeSpan.Zero, "http", "127.0.0.1:9", "GET", "/", "GET / HTTP/1.1\r\n\r\n", [],
                "HTTP/1.1 200 OK\r\n\r\n", [], "127.0.0.1");

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

        private Task OnDispatcher(Func<Task> action) => _application.RunAsync(action);

        private sealed class StubHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
        {
            internal int CallCount;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                ++CallCount;
                return callback(request, cancellationToken);
            }
        }
    }
}
