// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
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

    // The test process's WPF application, with its own storage folder under %APPDATA%\Microsoft. When the test assembly
    // finishes, IsolatedApplicationTestFramework calls ShutDownHostAsync to stop it and delete that folder. Run and
    // RunAsync run each test on its dispatcher within UiTestDispatcher.Budget.
    public sealed class CaptureUiApplicationFixture : IDisposable
    {
        internal const string StoragePrefix = "XMAT-UI-Tests-";

        private static readonly object HostLock = new();
        private static CaptureUiApplicationFixture _host;
        private readonly CaptureUiApplicationFixture _shared;
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new();
        private Application _application;
        private UiTestDispatcher _ui;
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
                    CaptureChromeTests.AddButtonStyles(_application.Resources);
                    _storage = Path.Join(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", appName);
                    Assert.Equal(_storage, PublicUtilities.StorageDirectoryPath);
                    // Other test classes can still use the process-wide application log until the run ends.
                    _appLog = (Logger)typeof(PublicUtilities).GetField(
                        "_appLog", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                    _ui = new UiTestDispatcher(_application.Dispatcher, UiTestDispatcher.Budget);
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
        // assembly reports that it finished. Throws if the application does not stop, the folder cannot be deleted, or
        // a test that exceeded its budget has not ended. The failure names that test, and the first test to exceed its
        // budget if that is another one, ahead of any folder that could not be deleted.
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
            var application = host._application;
            if (application != null)
            {
                _ = application.Dispatcher.BeginInvoke(new Action(application.Shutdown));
            }
            var stopped = host._thread.Join(TimeSpan.FromSeconds(30));
            host._appLog?.CloseLog();
            var deleted = host._storage == null || await TryDeleteStorageAsync(host._storage, TimeSpan.FromSeconds(10));
            var notEnded = host._ui != null && host._ui.Blocking.Length > 0;
            if (stopped && deleted && !notEnded)
            {
                return;
            }

            var failure = new List<string>();
            if (!stopped)
            {
                failure.Add("The WPF test application did not stop.");
            }

            if (host._ui?.Report() is string overruns)
            {
                failure.Add(overruns);
            }

            if (!deleted)
            {
                var stillOpen = Directory.EnumerateFiles(host._storage, "*", SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(host._storage, path));
                failure.Add(
                    $"The test application's storage folder {host._storage} could not be deleted. Files still open: " +
                    string.Join(", ", stillOpen));
            }

            var message = string.Join(" ", failure);
            if (!stopped)
            {
                throw new TimeoutException(message);
            }

            if (!deleted)
            {
                throw new IOException(message);
            }

            throw new UiTestBudgetException(message);
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

        // Runs a test on the shared application's dispatcher, starting the application first if this process has not.
        internal static void RunOnApplication(
            Action<Window> test,
            [CallerMemberName] string caller = null,
            [CallerFilePath] string file = null,
            [CallerLineNumber] int line = 0)
        {
            GetOrCreateHost().Run(test, caller, file, line);
        }
        // Runs a UI test on this application's dispatcher, as Dispatcher.Invoke would, within UiTestDispatcher.Budget.
        // A budget failure names the test with the call that reached the fixture (UiTestName).
        internal void Run(
            Action<Window> test,
            [CallerMemberName] string caller = null,
            [CallerFilePath] string file = null,
            [CallerLineNumber] int line = 0)
        {
            TestDispatcher().Run(() => WpfTestHost.RunOnCurrentDispatcher(test), UiTestName.Find(caller, file, line));
        }

        // Async UI tests share this application's dispatcher and lifetime instead of creating another Application. The
        // budget lasts until the test's whole body has ended.
        internal Task RunAsync(
            Func<Task> test,
            [CallerMemberName] string caller = null,
            [CallerFilePath] string file = null,
            [CallerLineNumber] int line = 0)
        {
            return TestDispatcher().RunAsync(test, UiTestName.Find(caller, file, line));
        }

        public void Dispose()
        {
            (_shared ?? this).ThrowIfFailed();
        }

        private UiTestDispatcher TestDispatcher()
        {
            var host = _shared ?? this;
            if (host._stopped)
            {
                // A stopped dispatcher would skip the test without running it.
                throw new InvalidOperationException(
                    "The WPF test application has stopped; run the tests in a new process.");
            }

            return host._ui;
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

    // Runs UI test callbacks on one dispatcher, each within a budget. Run queues its callback at Send priority and
    // RunAsync at Normal, as Dispatcher.Invoke and Dispatcher.InvokeAsync do, and Run on the dispatcher's own thread
    // runs it inline. A callback's own exceptions come out as they were thrown. When a budget expires,
    // UiTestBudgetException fails the test: a callback that has not started is aborted, and one that has started cannot
    // be stopped, so until it ends, every later call fails at once without queueing its callback. An async callback
    // ends when its whole body does.
    internal sealed class UiTestDispatcher
    {
        // Each UI test's budget, from its call to Run or RunAsync to the end of its callback.
        internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

        private readonly Dispatcher _dispatcher;
        private readonly TimeSpan _budget;
        private readonly List<UiTestOverrun> _blocking = new();
        private UiTestOverrun _first;

        internal UiTestDispatcher(Dispatcher dispatcher, TimeSpan budget)
        {
            ArgumentNullException.ThrowIfNull(dispatcher);
            _dispatcher = dispatcher;
            _budget = budget;
        }

        // The first callback in this run that exceeded its budget, kept after it has ended.
        internal UiTestOverrun FirstOverrun => Volatile.Read(ref _first);

        // The callbacks that exceeded their budgets and have not ended. While there are any, later calls fail at once.
        internal UiTestOverrun[] Blocking
        {
            get
            {
                lock (_blocking)
                {
                    return _blocking.ToArray();
                }
            }
        }

        internal void Run(Action callback, string test)
        {
            if (_dispatcher.CheckAccess())
            {
                // Queueing it and then waiting would wait for this very thread.
                _dispatcher.Invoke(callback);
                return;
            }

            ThrowIfBlocked();
            var operation = _dispatcher.InvokeAsync(callback, DispatcherPriority.Send);
            if (Task.WaitAny(new Task[] { operation.Task }, _budget) < 0)
            {
                throw Overrun(operation, operation.Task, test);
            }

            operation.Task.GetAwaiter().GetResult();
        }

        internal Task RunAsync(Func<Task> callback, string test)
        {
            ThrowIfBlocked();
            var operation = _dispatcher.InvokeAsync(callback, DispatcherPriority.Normal);
            return WithinBudgetAsync(operation, operation.Task.Unwrap(), test);
        }

        // For the run's report: the callbacks that exceeded their budgets and have not ended, and the first callback to
        // exceed its budget if it is not one of them. Null if no callback has exceeded its budget.
        internal string Report()
        {
            var first = FirstOverrun;
            if (first == null)
            {
                return null;
            }

            var blocking = Blocking;
            var report = new List<string>();
            if (blocking.Length > 0)
            {
                report.Add($"Exceeded the UI test budget of {Seconds(_budget)} and not ended: {Names(blocking)}.");
            }

            if (!blocking.Contains(first))
            {
                report.Add($"The first test to exceed the UI test budget of {Seconds(_budget)} was {first.Test}" +
                    (first.LateFault == null
                        ? "."
                        : $"; it later ended with {first.LateFault.GetType().FullName}: {first.LateFault.Message}"));
            }

            return string.Join(" ", report);
        }

        private async Task WithinBudgetAsync(DispatcherOperation operation, Task body, string test)
        {
            using var expiry = new CancellationTokenSource();
            if (await Task.WhenAny(body, Task.Delay(_budget, expiry.Token)).ConfigureAwait(false) != body)
            {
                throw Overrun(operation, body, test);
            }

            expiry.Cancel();
            await body.ConfigureAwait(false);
        }

        private void ThrowIfBlocked()
        {
            var blocking = Blocking;
            if (blocking.Length == 0)
            {
                return;
            }

            throw new UiTestBudgetException(
                $"Not run: the test dispatcher is still running {Names(blocking)}, which exceeded the UI test budget " +
                $"of {Seconds(_budget)}.");
        }

        // Fails a test whose callback exceeded its budget. A callback that has not started is aborted. One that has
        // started blocks later calls until its work ends, which then clears its own block, and only that.
        private Exception Overrun(DispatcherOperation operation, Task work, string test)
        {
            var overrun = new UiTestOverrun(test, started: !operation.Abort());
            Interlocked.CompareExchange(ref _first, overrun, null);
            if (overrun.Started)
            {
                lock (_blocking)
                {
                    _blocking.Add(overrun);
                }

                _ = work.ContinueWith(
                    ended => End(overrun, ended),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            return new UiTestBudgetException(overrun.Started
                ? $"{test} did not end within the UI test budget of {Seconds(_budget)}. It had started and was still " +
                    "running, and cannot be stopped; until it ends, later UI tests fail at once."
                : $"{test} did not start within the UI test budget of {Seconds(_budget)}, because the test " +
                    "dispatcher was busy. It was aborted and will not run.");
        }

        private void End(UiTestOverrun overrun, Task work)
        {
            // Reading the fault observes it, so a late failure is never left unobserved.
            var fault = work.Exception?.InnerException;
            lock (_blocking)
            {
                _blocking.Remove(overrun);
            }

            overrun.End(fault);
        }

        private static string Names(IEnumerable<UiTestOverrun> overruns) =>
            string.Join(" and ", overruns.Select(overrun => overrun.Test));

        private static string Seconds(TimeSpan budget) =>
            string.Create(CultureInfo.InvariantCulture, $"{budget.TotalSeconds:0.###} s");
    }

    // A UI test callback that exceeded its budget.
    internal sealed class UiTestOverrun
    {
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal UiTestOverrun(string test, bool started)
        {
            Test = test;
            Started = started;
            if (!started)
            {
                _ended.SetResult();
            }
        }

        internal string Test { get; }

        // False if the callback was aborted before it started.
        internal bool Started { get; }

        // Completes once the callback has ended and no longer blocks later calls.
        internal Task Ended => _ended.Task;

        // What the callback threw when it ended after its budget, if anything.
        internal Exception LateFault { get; private set; }

        internal void End(Exception fault)
        {
            LateFault = fault;
            _ended.SetResult();
        }
    }

    // The UI test fixture's own failure: a test exceeded its budget, or the dispatcher is still running one that did.
    // Not a TimeoutException, so a test's own TimeoutException is never taken for it, nor it for one.
    internal sealed class UiTestBudgetException : Exception
    {
        internal UiTestBudgetException(string message)
            : base(message)
        {
        }
    }

    // Names the test that calls Run or RunAsync for a budget failure: the nearest xUnit test method on the calling
    // stack, where an async test's state machine stands for its method, with the call that reached the fixture. The
    // caller alone would name a helper, such as CaptureExportDriver.RunAsync. A test is not on the stack when an async
    // helper calls after its own first await; the call alone is named then.
    internal static class UiTestName
    {
        private static readonly ConcurrentDictionary<Type, MethodBase> AsyncOrigins = new();

        internal static string Find(string caller, string file, int line)
        {
            var call = $"{caller} at {Path.GetFileName(file)}:{line.ToString(CultureInfo.InvariantCulture)}";
            try
            {
                foreach (var frame in new StackTrace(1, false).GetFrames())
                {
                    var method = Origin(frame.GetMethod());
                    if (method?.DeclaringType != null && method.IsDefined(typeof(FactAttribute), inherit: true))
                    {
                        return $"{method.DeclaringType.FullName}.{method.Name} ({call})";
                    }
                }
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                // The call still identifies the test.
            }

            return call;
        }

        private static MethodBase Origin(MethodBase method)
        {
            var type = method?.DeclaringType;
            if (type?.DeclaringType == null || !typeof(IAsyncStateMachine).IsAssignableFrom(type))
            {
                return method;
            }

            return AsyncOrigins.GetOrAdd(type, stateMachine => stateMachine.DeclaringType
                .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly)
                .FirstOrDefault(candidate =>
                    candidate.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType == stateMachine));
        }
    }
}
