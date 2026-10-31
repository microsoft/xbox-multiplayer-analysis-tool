// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace XMAT.Tests
{
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class CaptureUiApplicationCollection : ICollectionFixture<CaptureUiApplicationFixture>
    {
        public const string Name = "Capture UI application";
    }

    // The test process's WPF application, with its own storage folder under %APPDATA%\Microsoft. When the test assembly
    // finishes, IsolatedApplicationTestFramework calls ShutDownHostAsync to stop it and delete that folder.
    public sealed class CaptureUiApplicationFixture : IDisposable
    {
        internal const string StoragePrefix = "XMAT-UI-Tests-";

        private static readonly object HostLock = new();
        private static CaptureUiApplicationFixture _host;
        private readonly CaptureUiApplicationFixture _shared;
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new();
        private Application _application;
        private Exception _failure;
        private string _storage;
        private Logger _appLog;
        private volatile bool _stopped;

        public CaptureUiApplicationFixture() : this(false)
        {
        }

        private CaptureUiApplicationFixture(bool createHost)
        {
            if (!createHost)
            {
                _shared = GetOrCreateHost();
                _ready.Dispose();
                return;
            }
            _thread = new Thread(() =>
            {
                try
                {
                    _application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    var appName = $"{StoragePrefix}{Guid.NewGuid():N}";
                    _application.Resources["appName"] = appName;
                    _application.Resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
                    Localization.LoadLanguage("en-us");
                    _application.Resources[typeof(DataGridColumnHeader)] = new Style(typeof(DataGridColumnHeader));
                    _application.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri(
                            "pack://application:,,,/XboxMultiplayerAnalysisTool;component/Themes/LightTheme.xaml")
                    });
                    _storage = Path.Join(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", appName);
                    Assert.Equal(_storage, PublicUtilities.StorageDirectoryPath);
                    // Other test classes can still use the process-wide application log until the run ends.
                    _appLog = (Logger)typeof(PublicUtilities).GetField(
                        "_appLog", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
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
            // Only a fallback, for example after discovery alone: vstest can end the test host 100 ms after the session
            // ends, which is too soon for anything slower.
            AppDomain.CurrentDomain.ProcessExit += (sender, args) => DeleteStorageAtExit();
            if (!_ready.Wait(TimeSpan.FromSeconds(30)))
            {
                _failure = new TimeoutException("The WPF test application did not start.");
            }
        }

        // Stops the application, closes the application log and deletes this run's storage folder before the test
        // assembly reports that it finished. Throws if the application does not stop or the folder cannot be deleted.
        internal static async Task ShutDownHostAsync()
        {
            CaptureUiApplicationFixture host;
            lock (HostLock)
            {
                host = _host;
            }

            if (host == null)
            {
                return;
            }

            host._stopped = true;
            host._application?.Dispatcher.BeginInvoke(new Action(host._application.Shutdown));
            var stopped = host._thread.Join(TimeSpan.FromSeconds(30));
            host._appLog?.CloseLog();
            if (host._storage != null && !await TryDeleteStorageAsync(host._storage, TimeSpan.FromSeconds(10)))
            {
                var stillOpen = Directory.EnumerateFiles(host._storage, "*", SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(host._storage, path));
                throw new IOException(
                    $"The test application's storage folder {host._storage} could not be deleted. Files still open: " +
                    string.Join(", ", stillOpen));
            }

            if (!stopped)
            {
                throw new TimeoutException("The WPF test application did not stop.");
            }
        }

        // Deletes a test run's storage folder, retrying while its files are still being closed. Returns false if the
        // folder is still there after the timeout. Refuses any folder that is not named like a run's storage folder.
        internal static async Task<bool> TryDeleteStorageAsync(string folder, TimeSpan timeout)
        {
            if (!Path.GetFileName(folder).StartsWith(StoragePrefix, StringComparison.Ordinal))
            {
                throw new ArgumentException("Only a test run's storage folder can be deleted.", nameof(folder));
            }

            var clock = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    if (Directory.Exists(folder))
                    {
                        Directory.Delete(folder, recursive: true);
                    }

                    return true;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    if (clock.Elapsed >= timeout)
                    {
                        return false;
                    }
                }

                await Task.Delay(100);
            }
        }

        private static CaptureUiApplicationFixture GetOrCreateHost()
        {
            CaptureUiApplicationFixture host;
            lock (HostLock)
            {
                // A host that failed to start is kept, so the run still cleans it up and never starts a second one.
                host = _host ??= new CaptureUiApplicationFixture(true);
            }

            host.ThrowIfFailed();
            return host;
        }

        internal void Run(Action<Window> test)
        {
            var host = _shared ?? this;
            if (host._stopped)
            {
                // A stopped dispatcher would skip the test without running it.
                throw new InvalidOperationException(
                    "The WPF test application has stopped; run the tests in a new process.");
            }

            host._application.Dispatcher.Invoke(() => WpfTestHost.RunOnCurrentDispatcher(test));
        }

        public void Dispose()
        {
            (_shared ?? this).ThrowIfFailed();
        }

        private void ThrowIfFailed()
        {
            if (_failure != null)
            {
                ExceptionDispatchInfo.Capture(_failure).Throw();
            }
        }

        private void DeleteStorageAtExit()
        {
            try
            {
                _appLog?.CloseLog();
                if (_storage != null && Directory.Exists(_storage))
                {
                    Directory.Delete(_storage, recursive: true);
                }
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                // Nothing can report a failure while the process exits.
            }
        }
    }
}
