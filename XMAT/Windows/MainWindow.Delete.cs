// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Threading.Tasks;
using System.Windows;

namespace XMAT
{
    // The windows of a deletion from a capture list (CaptureConnectionView).
    public partial class MainWindow : ICaptureDeleteDialogService
    {
        // The delete confirmation: its text states the counts of the prompt, and Cancel is the default answer.
        internal static (string Text, string Caption, MessageBoxButton Buttons, MessageBoxImage Icon, MessageBoxResult DefaultResult)
            DeleteConfirmation(CaptureDeletePrompt prompt)
        {
            string text = prompt.Unselected
                ? Localization.GetLocalizedString("CAPTURE_DELETE_CONFIRM_UNSELECTED", prompt.Sessions, prompt.Selected, prompt.Hidden)
                : Localization.GetLocalizedString("CAPTURE_DELETE_CONFIRM_SELECTED", prompt.Sessions, prompt.Kept, prompt.Hidden);
            if (prompt.Incomplete > 0)
            {
                text += "\n\n" + Localization.GetLocalizedString("CAPTURE_DELETE_INCOMPLETE", prompt.Incomplete);
            }

            return (text, Localization.GetLocalizedString("CAPTURE_DELETE_TITLE"),
                MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        }

        Task<bool> ICaptureDeleteDialogService.ConfirmDeleteAsync(CaptureDeletePrompt prompt)
        {
            var dialog = DeleteConfirmation(prompt);
            return Task.FromResult(
                MessageBox.Show(this, dialog.Text, dialog.Caption, dialog.Buttons, dialog.Icon, dialog.DefaultResult) ==
                    MessageBoxResult.OK);
        }

        Task ICaptureDeleteDialogService.ShowDeleteNoticeAsync(string message)
        {
            MessageBox.Show(
                this, message, Localization.GetLocalizedString("CAPTURE_DELETE_TITLE"), MessageBoxButton.OK, MessageBoxImage.Information);
            return Task.CompletedTask;
        }
    }
}
