// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;

namespace XMAT
{
    // The windows of a deletion from a capture list (CaptureConnectionView).
    public partial class MainWindow : ICaptureDeleteDialogService
    {
        // The original look's delete confirmation: its text states the counts of the prompt, and Cancel is the default
        // answer.
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

        // The new look's: a question that says how many sessions it deletes, the sessions that stay, and a red button
        // that says the action again. Cancel is the default answer (DIALOG-1).
        internal static ConfirmPanel DeleteConfirmationPanel(CaptureDeletePrompt prompt)
        {
            static string Count(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
            bool one = prompt.Sessions == 1;
            var details = new List<string>();
            if (prompt.Unselected)
            {
                details.Add(Localization.GetLocalizedString("CAPTURE_DELETE_STAY_SELECTED", Count(prompt.Selected)));
                details.Add(Localization.GetLocalizedString("CAPTURE_DELETE_STAY_HIDDEN", Count(prompt.Hidden)));
            }
            else
            {
                details.Add(Localization.GetLocalizedString("CAPTURE_DELETE_STAY", Count(prompt.Kept), Count(prompt.Hidden)));
            }

            if (prompt.Incomplete > 0)
            {
                details.Add(Localization.GetLocalizedString("CAPTURE_DELETE_INCOMPLETE", Count(prompt.Incomplete)));
            }

            string question = prompt.Unselected
                ? (one ? "CAPTURE_DELETE_UNSELECTED_QUESTION_ONE" : "CAPTURE_DELETE_UNSELECTED_QUESTION")
                : (one ? "CAPTURE_DELETE_QUESTION_ONE" : "CAPTURE_DELETE_QUESTION");
            return new ConfirmPanel
            {
                Heading = Localization.GetLocalizedString(question, Count(prompt.Sessions)),
                Message = Localization.GetLocalizedString("CAPTURE_DELETE_MESSAGE"),
                Details = details,
                ConfirmText = Localization.GetLocalizedString(one ? "CAPTURE_DELETE_ACTION_ONE" : "CAPTURE_DELETE_ACTION", Count(prompt.Sessions)),
                IsDestructive = true,
            };
        }

        Task<bool> ICaptureDeleteDialogService.ConfirmDeleteAsync(CaptureDeletePrompt prompt)
        {
            if (LookState.Current.IsNewLook)
            {
                var window = new ConfirmWindow(DeleteConfirmationPanel(prompt), Localization.GetLocalizedString("CAPTURE_DELETE_TITLE"))
                {
                    Owner = this,
                };
                return Task.FromResult(window.ShowDialog() == true);
            }

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
