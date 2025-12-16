// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Windows.Input;

namespace XMAT.NetworkTrace
{
    public static class NetworkTraceCommands
    {
        public static readonly RoutedUICommand StartStopCapture =
            new RoutedUICommand("Start/Stop Capture", "Start/Stop Capture", typeof(NetworkTraceCommands));
        
        public static readonly RoutedUICommand AddUrlBlock =
            new RoutedUICommand("Add URL Block", "Add URL Block", typeof(NetworkTraceCommands));

        public static readonly RoutedUICommand RemoveUrlBlock =
            new RoutedUICommand("Remove URL Block", "Remove URL Block", typeof(NetworkTraceCommands));

        public static readonly RoutedUICommand ClearUrlBlocks =
            new RoutedUICommand("Clear URL Blocks", "Clear URL Blocks", typeof(NetworkTraceCommands));
    }
}
