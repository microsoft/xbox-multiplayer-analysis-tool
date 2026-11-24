// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using XMAT.WebServiceCapture.Models;

namespace XMAT
{
    public partial class WebProxyStopProgressWindow : Window
    {
        internal WebProxyStopProgressWindow(
            Task operationTask,
            WebProxyStopProgressModel progressModel)
        {
            _operationTask = operationTask ??
                throw new ArgumentNullException(nameof(operationTask));
            _progressModel = progressModel ??
                throw new ArgumentNullException(nameof(progressModel));
            DataContext = _progressModel;

            InitializeComponent();
            Title = Localization.GetLocalizedString("WEBCAP_STOP_TITLE");

            _elapsedTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _elapsedTimer.Tick += (sender, args) =>
                _progressModel.RefreshElapsed();
            _elapsedTimer.Start();
        }

        internal static async Task<bool> ShouldShowAsync(
            Task operationTask,
            TimeSpan delay)
        {
            ArgumentNullException.ThrowIfNull(operationTask);
            var completed = await Task.WhenAny(
                operationTask,
                Task.Delay(delay));
            return completed != operationTask && !operationTask.IsCompleted;
        }

        private async void Window_ContentRendered(
            object sender,
            EventArgs e)
        {
            try
            {
                await _operationTask;
            }
            catch
            {
                // The command handler observes and reports the operation error.
            }
            finally
            {
                _elapsedTimer.Stop();
                _canClose = true;
                Close();
            }
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (!_canClose)
            {
                e.Cancel = true;
            }
        }

        private readonly Task _operationTask;
        private readonly WebProxyStopProgressModel _progressModel;
        private readonly DispatcherTimer _elapsedTimer;
        private bool _canClose;
    }
}
