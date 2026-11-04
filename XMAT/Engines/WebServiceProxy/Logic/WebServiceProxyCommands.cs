// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows.Input;

namespace XMAT.WebServiceCapture
{
    public static class WebServiceProxyCommands
    {
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
    }
}
