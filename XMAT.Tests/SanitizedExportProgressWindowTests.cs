// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XMAT.Operations;
using XMAT.Operations.Export;

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

        // The window shows the export's latest phase, also one reported before it appeared, until Cancel is asked for.
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
                var dialog = new SanitizedExportProgressWindow(worker.Task, () => requests++)
                {
                    Left = -12000,
                    Top = -12000,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                try
                {
                    var progress = (IProgress<OperationProgress>)(object)dialog;
                    progress.Report(new OperationProgress(ExportCaptureSessionsOperation.ReadPhase, 0.5));
                    progress.Report(new OperationProgress(ExportCaptureSessionsOperation.PublishPhase, 0));
                    dialog.Show();
                    WpfTestHost.Pump();
                    var status = (TextBlock)dialog.FindName("StatusText");
                    Assert.Equal(Localization.GetLocalizedString("EXPORT_PHASE_PUBLISH"), status.Text);
                    Assert.DoesNotContain("NO_IMPL_", status.Text, StringComparison.Ordinal);
                    var cancel = (Button)dialog.FindName("CancelExport");
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

                    progress.Report(new OperationProgress(ExportCaptureSessionsOperation.PublishPhase, 1));
                    WpfTestHost.Pump();
                    Assert.Equal(1, requests);
                    Assert.True(dialog.IsVisible);
                    Assert.False(worker.Task.IsCompleted);
                    Assert.False(cancel.IsEnabled);
                    Assert.Equal(Localization.GetLocalizedString("SANITIZED_EXPORT_CANCELLING"), status.Text);
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
                var dialog = new SanitizedExportProgressWindow(worker.Task, () => requests++)
                {
                    Left = -12000,
                    Top = -12000,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
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
    }
}
