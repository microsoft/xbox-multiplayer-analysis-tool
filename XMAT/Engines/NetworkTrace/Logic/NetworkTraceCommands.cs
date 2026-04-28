// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows.Input;

namespace XMAT.NetworkTrace
{
    public static class NetworkTraceCommands
    {
        public static readonly RoutedUICommand StartStopCapture =
            new RoutedUICommand("Start/Stop Capture", "Start/Stop Capture", typeof(NetworkTraceCommands));
    }
}
