// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace XMAT
{
    public partial class App
    {
        internal static bool HasUiAccess(Dispatcher dispatcher) => dispatcher == null || dispatcher.CheckAccess();

        internal static void VerifyUiAccess(Dispatcher dispatcher) => dispatcher?.VerifyAccess();

        internal static bool IsUiShuttingDown(Dispatcher dispatcher) =>
            dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished;

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

        internal static Dispatcher CaptureUiDispatcher() => Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        internal static void RefreshCommands() => CommandManager.InvalidateRequerySuggested();

        internal static bool? ShowFileDialog(CommonDialog dialog, Window owner) => dialog.ShowDialog(owner);

        internal static MessageBoxResult ShowMessage(
            Window owner, string message, string title, MessageBoxButton buttons, MessageBoxImage image) =>
            MessageBox.Show(owner, message, title, buttons, image);
    }
}
