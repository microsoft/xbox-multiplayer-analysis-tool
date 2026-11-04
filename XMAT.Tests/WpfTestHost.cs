// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace XMAT.Tests
{
    internal static class WpfTestHost
    {
        internal static void Run(Action<Window> test)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    RunOnCurrentDispatcher(test);
                }
                catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
                {
                    failure = exception;
                }
                finally
                {
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The WPF test dispatcher did not complete.");
            if (failure != null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }

        internal static void RunOnCurrentDispatcher(Action<Window> test)
        {
            var window = new Window
            {
                Width = 700,
                Height = 220,
                Left = -12000,
                Top = -12000,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false
            };
            window.Resources["PrimaryForegroundBrush"] = Brushes.Black;
            window.Resources["SecondaryForegroundBrush"] = Brushes.Gray;
            try
            {
                test(window);
            }
            finally
            {
                window.Close();
                Pump();
            }
        }

        internal static void Show(Window window, FrameworkElement content)
        {
            window.Content = content;
            window.Show();
            Pump();
        }

        internal static void Pump()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }
}
