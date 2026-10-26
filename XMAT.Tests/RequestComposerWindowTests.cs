// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests
{
    public class RequestComposerWindowTests
    {
        static RequestComposerWindowTests()
        {
            Localization.LoadLanguage("en-us");
        }

        [Fact]
        public Task Window_PrefillsEditorsAndHonorsDeclinedConfirmation() => OnDispatcher(async () =>
        {
            int confirmations = 0;
            using var handler = new StubHandler((_, _) => throw new Xunit.Sdk.XunitException("Must not send."));
            using var httpClient = new HttpClient(handler);
            using var client = new RequestReplayClient(httpClient);
            var capture = RequestReplayDraftTests.Capture("POST", "/original", "original"u8.ToArray());
            var window = new RequestComposerWindow(capture, client, (_, _) => { ++confirmations; return false; });
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
            var window = new RequestComposerWindow(capture, client, (_, _) => true);
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
                RequestReplayDraftTests.Capture("GET", "/", []), client, (_, _) => true);
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
                RequestReplayDraftTests.Capture("GET", "/", []), client, (_, _) => true);
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
                RequestReplayDraftTests.Capture("GET", "/", []), client, (_, _) => true);
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
                RequestReplayDraftTests.Capture("POST", "/", bytes), client, (_, _) => true);
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
                RequestReplayDraftTests.Capture("POST", "/", bytes), client, (_, _) => true);
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

        private static Task OnDispatcher(Func<Task> action)
        {
            // WPF needs an STA dispatcher that keeps pumping while asynchronous sends resume on the UI thread.
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                if (Application.Current == null)
                {
                    var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    application.Resources["appName"] = $"XMAT.RequestComposer.Tests-{Environment.ProcessId}";
                }
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                dispatcher.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        await action();
                        completion.SetResult();
                    }
                    catch (Exception exception)
                    {
                        completion.SetException(exception);
                    }
                    finally
                    {
                        dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    }
                }));
                Dispatcher.Run();
            })
            { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }

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
