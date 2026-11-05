// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;

namespace XMAT
{
    // The windows of a deletion from a capture list (CaptureConnectionView).
    public partial class MainWindow
    {
        // The delete confirmation: its text states the counts of the prompt, and Cancel is the default answer.
        internal static (string Text, string Caption, MessageBoxButton Buttons, MessageBoxImage Icon, MessageBoxResult DefaultResult)
            DeleteConfirmation(CaptureDeletePrompt prompt) => default;
    }
}
