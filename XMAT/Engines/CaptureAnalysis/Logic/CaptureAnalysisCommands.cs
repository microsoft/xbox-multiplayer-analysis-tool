// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Windows.Input;

namespace XMAT.XboxLiveCaptureAnalysis
{
    public static class CaptureAnalysisCommands
    {
        public static readonly RoutedUICommand GoToId =
            new RoutedUICommand("View Request Id", "GoToId", typeof(CaptureAnalysisCommands));

        public static readonly RoutedUICommand GoToEndpoint =
            new RoutedUICommand("Endpoint Details", "GoToEndpoint", typeof(CaptureAnalysisCommands));

        public static readonly RoutedUICommand GoToEndpointBfr =
            new RoutedUICommand("Endpoint Batch Frequency Rule Details", "GoToEndpointBfr", typeof(CaptureAnalysisCommands));

        public static readonly RoutedUICommand GoToEndpointBdr =
            new RoutedUICommand("Endpoint Burst Detection Rule Details", "GoToEndpointBdr", typeof(CaptureAnalysisCommands));

        public static readonly RoutedUICommand GoToEndpointCfr =
            new RoutedUICommand("Endpoint Call Frequency Rule Details", "GoToEndpointCfr", typeof(CaptureAnalysisCommands));

        public static readonly RoutedUICommand GoToEndpointPdr =
            new RoutedUICommand("Endpoint Polling Detection Rule Details", "GoToEndpointPdr", typeof(CaptureAnalysisCommands));

        public static readonly RoutedUICommand GoToEndpointRcr =
            new RoutedUICommand("Endpoint Repeated Calls Rule Details", "GoToEndpointRcr", typeof(CaptureAnalysisCommands));

        public static readonly RoutedUICommand GoToEndpointSbdr =
            new RoutedUICommand("Endpoint Small Batch Detection Rule Details", "GoToEndpointSbdr", typeof(CaptureAnalysisCommands));

        public static readonly RoutedUICommand GoToEndpointTcr =
            new RoutedUICommand("Endpoint Throttled Calls Rule Details", "GoToEndpointTcr", typeof(CaptureAnalysisCommands));
    }
}
