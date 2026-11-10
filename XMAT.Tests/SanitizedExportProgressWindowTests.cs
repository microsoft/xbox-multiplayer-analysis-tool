// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public sealed class SanitizedExportProgressWindowTests
    {
        private readonly CaptureUiApplicationFixture _application;

        public SanitizedExportProgressWindowTests(CaptureUiApplicationFixture application)
        {
            _application = application;
        }

        [Theory]
        [InlineData("button")]
        [InlineData("escape")]
        [InlineData("close")]
        public void Cancellation_IsRequestedOnceAndTheDialogWaitsForTheWorker(string trigger)
        {
            _application.Run(owner =>
            {
                var worker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var requests = 0;
                var dialog = Window(worker.Task, () => requests++);
                try
                {
                    dialog.Show();
                    WpfTestHost.Pump();
                    var cancel = (Button)dialog.FindName("CancelExport");
                    Press(dialog, cancel, trigger);

                    Assert.Equal(1, requests);
                    Assert.True(dialog.IsVisible);
                    Assert.False(worker.Task.IsCompleted);
                    Assert.False(cancel.IsEnabled);
                    Assert.Equal(Localization.GetLocalizedString("SANITIZED_EXPORT_CANCELLING"),
                        ((TextBlock)dialog.FindName("StatusText")).Text);
                    dialog.Close();
                    Assert.Equal(1, requests);
                    Assert.True(dialog.IsVisible);

                    worker.SetCanceled();
                    WpfTestHost.Pump();
                    Assert.False(dialog.IsVisible);
                }
                finally
                {
                    worker.TrySetResult();
                    WpfTestHost.Pump();
                }
            });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void WorkerCompletion_ClosesTheDialogWithoutRequestingCancellation(bool failed)
        {
            _application.Run(owner =>
            {
                var worker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var requests = 0;
                var dialog = Window(worker.Task, () => requests++);
                try
                {
                    dialog.Show();
                    WpfTestHost.Pump();
                    Assert.True(dialog.IsVisible);
                    if (failed)
                    {
                        worker.SetException(new IOException("Simulated export error."));
                    }
                    else
                    {
                        worker.SetResult();
                    }
                    WpfTestHost.Pump();

                    Assert.False(dialog.IsVisible);
                    Assert.Equal(0, requests);
                }
                finally
                {
                    worker.TrySetResult();
                    WpfTestHost.Pump();
                }
            });
        }

        [Fact]
        public void Texts_TitleStatusDetailsAndNotice_AreShownAsGiven_AndTheCountLineAppearsWithTheFirstCount()
        {
            _application.Run(owner =>
            {
                var worker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var dialog = Window(worker.Task, () => { }, notice: "Copy notice");
                var bare = Window(worker.Task, () => { });
                try
                {
                    dialog.Show();
                    bare.Show();
                    WpfTestHost.Pump();
                    var count = (TextBlock)dialog.FindName("CountText");
                    var notice = (TextBlock)dialog.FindName("NoticeText");
                    Assert.Equal("Export title", dialog.Title);
                    Assert.Equal("Export status", ((TextBlock)dialog.FindName("StatusText")).Text);
                    Assert.Equal("Export details", ((TextBlock)dialog.FindName("DetailsText")).Text);
                    Assert.Equal(("Copy notice", Visibility.Visible), (notice.Text, notice.Visibility));
                    Assert.Equal(Visibility.Collapsed, ((TextBlock)bare.FindName("NoticeText")).Visibility);
                    Assert.Equal(Visibility.Collapsed, count.Visibility);

                    dialog.ShowCount(1234, 5678);

                    Assert.Equal(Visibility.Visible, count.Visibility);
                    Assert.Equal(Localization.GetLocalizedString("EXPORT_PROGRESS_COUNT", 1234, 5678), count.Text);
                    Assert.EndsWith("rows processed.", count.Text, StringComparison.Ordinal);
                }
                finally
                {
                    worker.TrySetResult();
                    WpfTestHost.Pump();
                }
            });
        }

        // Once the publication has started, Cancel, Escape and closing the window request nothing, and the window still
        // waits for the worker.
        [Theory]
        [InlineData("button")]
        [InlineData("escape")]
        [InlineData("close")]
        public void EndCancellation_TurnsCancelOff_AndTheWindowStillWaitsForTheWorker(string trigger)
        {
            _application.Run(owner =>
            {
                var worker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var requests = 0;
                var dialog = Window(worker.Task, () => requests++);
                try
                {
                    dialog.Show();
                    WpfTestHost.Pump();
                    var cancel = (Button)dialog.FindName("CancelExport");
                    var status = (TextBlock)dialog.FindName("StatusText");

                    dialog.EndCancellation();
                    Assert.False(cancel.IsEnabled);
                    Press(dialog, cancel, trigger);

                    Assert.Equal(0, requests);
                    Assert.Equal("Export status", status.Text);
                    Assert.True(dialog.IsVisible);
                    worker.SetResult();
                    WpfTestHost.Pump();
                    Assert.False(dialog.IsVisible);
                }
                finally
                {
                    worker.TrySetResult();
                    WpfTestHost.Pump();
                }
            });
        }

        private static SanitizedExportProgressWindow Window(Task operation, Action cancel, string notice = null) =>
            new(operation, cancel, "Export title", "Export status", "Export details", notice)
            {
                Left = -12000,
                Top = -12000,
                WindowStartupLocation = WindowStartupLocation.Manual
            };

        private static void Press(SanitizedExportProgressWindow dialog, Button cancel, string trigger)
        {
            switch (trigger)
            {
                case "button":
                    cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    break;
                case "escape":
                    dialog.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
                        PresentationSource.FromVisual(dialog), Environment.TickCount, Key.Escape)
                    {
                        RoutedEvent = Keyboard.PreviewKeyDownEvent
                    });
                    break;
                default:
                    dialog.Close();
                    break;
            }
        }
    }
}
