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
        internal static bool HasUiAccess(Dispatcher dispatcher) => ApplicationDispatch.HasUiAccess(dispatcher);

        internal static void VerifyUiAccess(Dispatcher dispatcher) => ApplicationDispatch.VerifyUiAccess(dispatcher);

        internal static bool IsUiShuttingDown(Dispatcher dispatcher) =>
            ApplicationDispatch.IsUiShuttingDown(dispatcher);

        internal static Task RunOnUiAsync(Dispatcher dispatcher, Func<Task> action) =>
            ApplicationDispatch.RunOnUiAsync(dispatcher, action);

        internal static Task<T> RunOnUiAsync<T>(Dispatcher dispatcher, Func<Task<T>> action) =>
            ApplicationDispatch.RunOnUiAsync(dispatcher, action);

        internal static Task RunOnUiAsync(Dispatcher dispatcher, Action action)
        {
            return ApplicationDispatch.RunOnUiAsync(dispatcher, action);
        }

        internal static DispatcherOperation<T> QueueOnUi<T>(Dispatcher dispatcher, Func<T> action) =>
            ApplicationDispatch.QueueOnUi(dispatcher, action);

        internal static DispatcherOperation QueueOnUi(Dispatcher dispatcher, Action action) =>
            ApplicationDispatch.QueueOnUi(dispatcher, action);

        internal static void PostToUi(Dispatcher dispatcher, Action action) => ApplicationDispatch.PostToUi(dispatcher, action);

        internal static Dispatcher CaptureUiDispatcher() =>
            ApplicationDispatch.RuntimeDispatcher ?? Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        internal static void RefreshCommands() => CommandManager.InvalidateRequerySuggested();

        internal static bool? ShowFileDialog(CommonDialog dialog, Window owner) => dialog.ShowDialog(owner);

        internal static MessageBoxResult ShowMessage(
            Window owner, string message, string title, MessageBoxButton buttons, MessageBoxImage image) =>
            MessageBox.Show(owner, message, title, buttons, image);
    }
}
