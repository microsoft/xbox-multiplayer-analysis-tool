// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using System.Net.WebSockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using XMAT.Telemetry;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Tests
{
    public class TelemetryViewTests
    {
        [Theory]
        [InlineData(1, "RequestBody")]
        [InlineData(2, "RequestJson")]
        public Task ResponseSelectedBeforeArrivalIsRecordedAfterBindingUpdates(int tab, string feature)
        {
            return OnStaThread(() =>
            {
                List<TelemetryFeature> uses = [];
                var response = new RequestResponseView(uses.Add);
                ((TabControl)response.FindName("RequestTabs")).SelectedIndex = tab;
                WithWindow(response, window =>
                {
                    Assert.Empty(uses);
                    response.FirstLineAndHeaders = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\r\n";
                    response.BodyText = "{}";
                    response.BodyBytes = "{}"u8.ToArray();
                    DrainDispatcher(window);
                    Assert.Equal([Enum.Parse<TelemetryFeature>(feature)], uses);
                });
            });
        }

        [Fact]
        public Task OpeningWebSocketsWithAPreselectedPayloadRecordsTheDefaultTextView()
        {
            return OnStaThread(() =>
            {
                List<TelemetryFeature> uses = [];
                var message = new WebSocketMessageModel(new WebSocketMessageEventArgs
                {
                    MessageType = WebSocketMessageType.Text,
                    Message = "{}"u8.ToArray()
                });
                var view = new WebSocketMessagesView(uses.Add)
                {
                    SelectedItem = message,
                    ItemsSource = new ObservableCollection<WebSocketMessageModel> { message },
                    Visibility = Visibility.Hidden
                };
                WithWindow(view, window =>
                {
                    Assert.Empty(uses);
                    view.Visibility = Visibility.Visible;
                    DrainDispatcher(window);
                    Assert.Equal([TelemetryFeature.WebSocketText], uses);
                });
            });
        }

        [Fact]
        public Task PopulatedDefaultWaterfallIsRecordedWithoutChangingItsMode()
        {
            return OnStaThread(() =>
            {
                List<TelemetryFeature> uses = [];
                var view = new CaptureTimelineView(uses.Add);
                WithWindow(view, window =>
                {
                    Assert.Empty(uses);
                    view.ItemsSource = new ObservableCollection<ProxyConnectionModel>
                    {
                        new(1, "200", DateTime.Now, TimeSpan.FromMilliseconds(10), "https", "example.test:443",
                            "GET", "/", "GET / HTTP/1.1\r\n\r\n", [], "HTTP/1.1 200 OK\r\n\r\n", [], "")
                    };
                    DrainDispatcher(window);
                    Assert.Equal([TelemetryFeature.TimelineWaterfall], uses);
                    Assert.NotEmpty(((Canvas)view.FindName("TimelineCanvas")).Children.Cast<object>());
                });
            });
        }

        private static Task OnStaThread(Action action)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                    completion.SetResult();
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
                finally
                {
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
            })
            { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }

        private static void WithWindow(UserControl view, Action<Window> action)
        {
            var window = new Window
            {
                Content = view,
                Width = 300,
                Height = 200,
                Left = -10000,
                Top = -10000,
                ShowInTaskbar = false,
                ShowActivated = false
            };
            try
            {
                window.Show();
                DrainDispatcher(window);
                action(window);
            }
            finally
            {
                window.Close();
            }
        }

        private static void DrainDispatcher(Window window) =>
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }
}
