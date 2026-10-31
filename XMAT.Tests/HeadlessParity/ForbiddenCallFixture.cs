// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace XMAT.Tests.HeadlessParity
{
    // Code that the IL scanner self-test inspects; nothing here runs. Each method uses GUI-only APIs in a different
    // compiled shape, and the scanner must attribute every call to the method written here.
    internal static class ForbiddenCallFixture
    {
        internal static void Direct() => PublicUtilities.SafeInvoke(Nothing);

        internal static Action InLambda() => () => PublicUtilities.SafeInvoke(Nothing);

        internal static async Task InAsyncMethod()
        {
            await Task.Yield();
            PublicUtilities.SafeInvoke(Nothing);
            PublicUtilities.SafeInvoke(Nothing);
        }

        internal static object InLocalFunction()
        {
            return Current();

            static object Current() => Application.Current;
        }

        internal static IEnumerable<int> InIterator()
        {
            yield return 1;
            CommandManager.InvalidateRequerySuggested();
        }

        internal static Action AsDelegate() => CommandManager.InvalidateRequerySuggested;

        internal static void Dialogs(Window window, Microsoft.Win32.CommonDialog dialog)
        {
            MessageBox.Show("text");
            window.ShowDialog();
            dialog.ShowDialog();
        }

        internal static bool Dispatching() => Dispatcher.CurrentDispatcher.CheckAccess();

        private static void Nothing()
        {
        }
    }

    // UI types may use these APIs; nothing here may be reported.
    internal sealed class ForbiddenCallUiFixture : FrameworkElement
    {
        internal static object Current() => Application.Current;

        internal void Run() => PublicUtilities.SafeInvoke(() => MessageBox.Show("text"));
    }
}

namespace XMAT.Operations.ParityFixtures
{
    // A WPF type in a headless namespace: its base class must not exempt it, so its call is reported and the type itself
    // is rejected. Nothing here runs.
    internal sealed class HeadlessUiFixture : FrameworkElement
    {
        internal static void Forbidden() => PublicUtilities.SafeInvoke(() => { });
    }
}
