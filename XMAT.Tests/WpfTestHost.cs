// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace XMAT.Tests
{
    // WpfTestHost.Run runs each test on the shared WPF application's dispatcher, the one thread that reads the
    // application's resources: a ResourceDictionary fills a lookup cache on first use that is not thread-safe, so a test
    // on a dispatcher thread of its own could corrupt it, and every later UI test would then fail. The classes that use
    // Run also join this collection, which runs alone.
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class WpfTestHostCollection
    {
        public const string Name = "WPF test host";
    }

    internal static class WpfTestHost
    {
        // Runs the test on the shared application's dispatcher; a caller already on it runs the test in place.
        internal static void Run(Action<Window> test) => CaptureUiApplicationFixture.RunOnApplication(test);

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
