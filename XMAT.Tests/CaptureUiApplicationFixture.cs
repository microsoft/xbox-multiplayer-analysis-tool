// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace XMAT.Tests
{
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class CaptureUiApplicationCollection : ICollectionFixture<CaptureUiApplicationFixture>
    {
        public const string Name = "Capture UI application";
    }

    public sealed class CaptureUiApplicationFixture : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new();
        private Application _application;
        private Exception _failure;

        public CaptureUiApplicationFixture()
        {
            _thread = new Thread(() =>
            {
                try
                {
                    _application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    var appName = $"XMAT-UI-Tests-{Guid.NewGuid():N}";
                    _application.Resources["appName"] = appName;
                    _application.Resources[typeof(DataGridColumnHeader)] = new Style(typeof(DataGridColumnHeader));
                    _application.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri(
                            "pack://application:,,,/XboxMultiplayerAnalysisTool;component/Themes/LightTheme.xaml")
                    });
                    var storage = Path.Join(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", appName);
                    Assert.Equal(storage, PublicUtilities.StorageDirectoryPath);
                    var log = (Logger)typeof(PublicUtilities).GetField(
                        "_appLog", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                    // Other test classes can still use the process-wide application log.
                    AppDomain.CurrentDomain.ProcessExit += (sender, args) =>
                    {
                        log.CloseLog();
                        Directory.Delete(storage, true);
                    };
                    _ready.Set();
                    _application.Run();
                }
                catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
                {
                    _failure = exception;
                    _ready.Set();
                }
            })
            { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            Assert.True(_ready.Wait(TimeSpan.FromSeconds(30)), "The WPF test application did not start.");
            if (_failure != null)
            {
                ExceptionDispatchInfo.Capture(_failure).Throw();
            }
        }

        internal void Run(Action<Window> test) =>
            _application.Dispatcher.Invoke(() => WpfTestHost.RunOnCurrentDispatcher(test));

        // Async UI tests share this application's dispatcher and lifetime instead of creating another Application.
        internal Task RunAsync(Func<Task> test) =>
            _application.Dispatcher.InvokeAsync(test).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(30));

        public void Dispose()
        {
            _application.Dispatcher.Invoke(_application.Shutdown);
            Assert.True(_thread.Join(TimeSpan.FromSeconds(30)), "The WPF test application did not stop.");
            _ready.Dispose();
            if (_failure != null)
            {
                ExceptionDispatchInfo.Capture(_failure).Throw();
            }
        }
    }
}
