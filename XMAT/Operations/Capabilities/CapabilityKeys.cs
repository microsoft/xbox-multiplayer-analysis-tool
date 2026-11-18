// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

namespace XMAT.Operations.Capabilities
{
    // Builds the keys that identify GUI entry points. The parity tests discover the same keys from the product
    // (commands, XAML, windows, enums, analyzers and preferences), so a new entry point fails the build until a
    // capability classifies it. XAML paths are relative to the XMAT project folder and use '/'.
    internal static class CapabilityKeys
    {
        // A routed command field or property declared in XMAT, for example "XMAT.AppCommands.Import".
        internal static string Command(string fullName) => "command:" + fullName;

        // A WPF built-in command named in a XAML file, for example ("New", "Windows/MainWindow.xaml"). What a
        // built-in command does depends on the view that binds it, so the file is part of the key.
        internal static string WpfCommand(string name, string xamlPath) => $"command:wpf:{name}@{xamlPath}";

        internal static string Click(string xamlPath, string handler) => $"click:{xamlPath}:{handler}";

        // A command or other entry point bound through a markup extension other than x:Static, for example a
        // {Binding}; it cannot be resolved statically, so it needs its own classification.
        internal static string Dynamic(string xamlPath, string description) => $"dynamic:{xamlPath}:{description}";

        internal static string Window(string fullName) => "window:" + fullName;

        internal static string EnumValue(string enumFullName, string value) => $"enum:{enumFullName}.{value}";

        internal static string Analyzer(string fullName) => "analyzer:" + fullName;

        internal static string Preference(string typeFullName, string property) => $"preference:{typeFullName}.{property}";
    }
}
