// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Windows.Input;

namespace XMAT
{
    public static class AppCommands
    {
        public static readonly RoutedUICommand Import =
            new RoutedUICommand("_Import Captures...", "ImportCaptures", typeof(AppCommands));

        public static readonly RoutedUICommand Export =
            new RoutedUICommand("_Export Captures...", "ExportCaptures", typeof(AppCommands));

        public static readonly RoutedUICommand Preferences =
            new RoutedUICommand("_Preferences", "Preferences", typeof(AppCommands));

        public static readonly RoutedUICommand Exit =
            new RoutedUICommand("E_xit", "Exit", typeof(AppCommands));

        public static readonly RoutedUICommand AnalyzeCaptures =
            new RoutedUICommand("Analy_ze Current Capture", "Analyze Current Capture", typeof(AppCommands), new InputGestureCollection() { new KeyGesture(Key.A, ModifierKeys.Control) });

        public static readonly RoutedUICommand ClearAllCaptures =
            new RoutedUICommand("Clear _All Captures", "Clear All Captures", typeof(AppCommands), new InputGestureCollection() { new KeyGesture(Key.X, ModifierKeys.Control) });

        public static readonly RoutedUICommand CollectLogs =
            new RoutedUICommand("Collect Debug _Logs", "Collect Debug Logs", typeof(AppCommands));

        public static readonly RoutedUICommand ViewGDKXInfo =
           new RoutedUICommand("View _GDK Extensions for Xbox details", "View GDK Extensions for Xbox details", typeof(AppCommands));

        public static readonly RoutedUICommand ExportRootCert = 
            new RoutedUICommand("Export Root Certificate", "ExportRootCert", typeof(AppCommands));
    }
}
