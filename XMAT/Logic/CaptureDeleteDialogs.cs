// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Threading.Tasks;

namespace XMAT
{
    // What the delete confirmation states, counted from the rows frozen when the command ran: the sessions to delete,
    // the sessions that stay, the selected sessions, the sessions that filters hide (which always stay) and, among those
    // to delete, the sessions without a complete recorded response. Unselected is Shift+Delete.
    internal sealed record CaptureDeletePrompt(
        bool Unselected,
        int Sessions,
        int Kept,
        int Selected,
        int Hidden,
        int Incomplete);

    // The windows of a deletion from a capture tab, so the tab deletes without them in tests.
    internal interface ICaptureDeleteDialogService
    {
        // Cancel is the default answer; true only when the user chose to delete.
        Task<bool> ConfirmDeleteAsync(CaptureDeletePrompt prompt);

        // Says why nothing was deleted, or how many of the confirmed sessions were kept.
        Task ShowDeleteNoticeAsync(string message);
    }
}
