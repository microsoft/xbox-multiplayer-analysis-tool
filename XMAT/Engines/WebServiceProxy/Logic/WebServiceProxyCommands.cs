// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows.Input;

namespace XMAT.WebServiceCapture
{
    public static class WebServiceProxyCommands
    {
        public static readonly RoutedUICommand EditAndResend =
            new("Edit and resend", "EditAndResend", typeof(WebServiceProxyCommands));

        // Reissues captured rows through the shared ReplayOperation; the parameter is a ReissueRequest.
        public static readonly RoutedUICommand Reissue =
            new("Reissue", "Reissue", typeof(WebServiceProxyCommands));
        public static readonly RoutedUICommand CancelReissue =
            new("Cancel replay", "CancelReissue", typeof(WebServiceProxyCommands));
        public static readonly RoutedUICommand ChangePort =
            new("Change Proxy Port", "ChangePort", typeof(WebServiceProxyCommands));
        public static readonly RoutedUICommand DiscardPortChange =
            new("Discard Pending Port", "DiscardPortChange", typeof(WebServiceProxyCommands));
        public static readonly RoutedUICommand CancelPortChange =
            new("Cancel Applying Port", "CancelPortChange", typeof(WebServiceProxyCommands));

        public static readonly RoutedUICommand EnableDevice =
            new("Enable Device", "Enable Device", typeof(WebServiceProxyCommands));

        public static readonly RoutedUICommand DisableDevice =
            new("Disable Device", "Disable Device", typeof(WebServiceProxyCommands));

        public static readonly RoutedUICommand StartStopCapture =
            new("Start/Stop Capture", "Start/Stop Capture", typeof(WebServiceProxyCommands));

        public static readonly RoutedUICommand PingDevice =
            new("Ping Device", "Ping Device", typeof(WebServiceProxyCommands));

        public static readonly RoutedUICommand CheckDevice =
            new("Check Device", "Check Device", typeof(WebServiceProxyCommands));

        // The process items of the request and timeline context menus; each carries the process filter to apply.
        public static readonly RoutedUICommand ShowOnlyProcess =
            new("Show Only Process", "ShowOnlyProcess", typeof(WebServiceProxyCommands));

        public static readonly RoutedUICommand HideProcess =
            new("Hide Process", "HideProcess", typeof(WebServiceProxyCommands));

        public static readonly RoutedUICommand ShowAllProcesses =
            new("Show All Processes", "ShowAllProcesses", typeof(WebServiceProxyCommands));

        public static readonly RoutedUICommand ExportSelectedSessions =
            new("Export Selected Sessions", "ExportSelectedSessions", typeof(WebServiceProxyCommands));

        public static readonly RoutedUICommand ExportVisibleSessions =
            new("Export Visible Sessions", "ExportVisibleSessions", typeof(WebServiceProxyCommands));

        // Delete and Shift+Delete in the capture list: the visible selected rows, or the visible rows that are not selected.
        public static readonly RoutedUICommand DeleteSelectedSessions =
            new("Delete Selected Sessions", "DeleteSelectedSessions", typeof(WebServiceProxyCommands));

        public static readonly RoutedUICommand DeleteUnselectedSessions =
            new("Delete Unselected Sessions", "DeleteUnselectedSessions", typeof(WebServiceProxyCommands));
    }
}
