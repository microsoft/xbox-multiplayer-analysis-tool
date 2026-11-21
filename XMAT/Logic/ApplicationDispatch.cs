// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace XMAT
{
    internal static class ApplicationDispatch
    {
        private static Dispatcher _runtimeDispatcher;

        internal static Dispatcher RuntimeDispatcher => Volatile.Read(ref _runtimeDispatcher);
        internal static bool HasUiAccess(Dispatcher dispatcher) => dispatcher == null || dispatcher.CheckAccess();
        internal static void VerifyUiAccess(Dispatcher dispatcher) => dispatcher?.VerifyAccess();
        internal static bool IsUiShuttingDown(Dispatcher dispatcher) =>
            dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished;

        internal static IDisposable BindRuntime(Dispatcher dispatcher)
        {
            ArgumentNullException.ThrowIfNull(dispatcher);
            dispatcher.VerifyAccess();
            if (Interlocked.CompareExchange(ref _runtimeDispatcher, dispatcher, null) != null)
                throw new InvalidOperationException("An application runtime is already bound.");
            return new Binding(dispatcher);
        }

        internal static Task RunOnUiAsync(Dispatcher dispatcher, Func<Task> action) =>
            HasUiAccess(dispatcher) ? action() : dispatcher.InvokeAsync(action).Task.Unwrap();

        internal static Task<T> RunOnUiAsync<T>(Dispatcher dispatcher, Func<Task<T>> action) =>
            HasUiAccess(dispatcher) ? action() : dispatcher.InvokeAsync(action).Task.Unwrap();

        internal static Task RunOnUiAsync(Dispatcher dispatcher, Action action)
        {
            if (!HasUiAccess(dispatcher))
                return dispatcher.InvokeAsync(action).Task;
            action();
            return Task.CompletedTask;
        }

        internal static DispatcherOperation<T> QueueOnUi<T>(Dispatcher dispatcher, Func<T> action) =>
            dispatcher.InvokeAsync(action);

        internal static DispatcherOperation QueueOnUi(Dispatcher dispatcher, Action action) =>
            dispatcher.InvokeAsync(action);

        internal static void PostToUi(Dispatcher dispatcher, Action action) => dispatcher.BeginInvoke(action);

        internal static int RunRuntime(Func<Dispatcher, Task<int>> run)
        {
            ArgumentNullException.ThrowIfNull(run);
            var dispatcher = Dispatcher.CurrentDispatcher;
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var exitCode = 70;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                { exitCode = await run(dispatcher); }
                catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
                { Trace.TraceError("The XMAT runtime could not finish."); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            try
            { Dispatcher.Run(); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            return exitCode;
        }

        private sealed class Binding(Dispatcher dispatcher) : IDisposable
        {
            public void Dispose() => Interlocked.CompareExchange(ref _runtimeDispatcher, null, dispatcher);
        }
    }
}
