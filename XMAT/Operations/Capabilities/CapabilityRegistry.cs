// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using static XMAT.Operations.Capabilities.CapabilityKeys;

namespace XMAT.Operations.Capabilities
{
    // Every user-facing capability, how the GUI reaches it and whether it runs headless. The parity tests fail when a
    // GUI entry point (routed command, XAML Command/Click/CommandBinding, dialog window, device or capture type, script
    // event, analyzer or preference) is missing here or listed but no longer exists. GuiOnly is only for presentation.
    // docs/cli.md shows this table between its xmat-capabilities markers.
    internal static class CapabilityRegistry
    {
        internal const string AnalyzeCaptureOperation = "XMAT.Operations.Analysis.AnalyzeCaptureOperation";

        private const string Owner = "XMAT-021";
        private const string MainWindowXaml = "Windows/MainWindow.xaml";
        private const string DeviceTabsXaml = "Controls/AllCaptureDevicesTabControl.xaml";
        private const string AnalysisTabsXaml = "Controls/CaptureAnalysisControl.xaml";
        private const string PreferencesXaml = "Windows/PreferencesWindow.xaml";
        private const string AnalyzerChoiceXaml = "Windows/CaptureAnalyzerWindow.xaml";
        private const string AddDeviceXaml = "Windows/AddDeviceWindow.xaml";
        private const string DisableOnCloseXaml = "Windows/DisableDeviceOnCloseWindow.xaml";
        private const string SourceAddressXaml = "Engines/WebServiceProxy/Windows/SourceAddressWindow.xaml";
        private const string PortChangeXaml = "Engines/WebServiceProxy/Windows/ProxyPortChangeWindow.xaml";
        private const string BlockListXaml = "Engines/WebServiceProxy/Views/BlockListView.xaml";
        private const string BypassListXaml = "Engines/WebServiceProxy/Views/BypassListView.xaml";
        private const string HostListManagerXaml = "Engines/WebServiceProxy/Windows/HostListManagerWindow.xaml";
        private const string SanitizedExportXaml = "Windows/SanitizedExportProgressWindow.xaml";

        private const string AppCommandsPrefix = "XMAT.AppCommands.";
        private const string ProxyCommandsPrefix = "XMAT.WebServiceCapture.WebServiceProxyCommands.";
        private const string ScriptCommandsPrefix = "XMAT.ScriptCommands.";
        private const string NavigationCommandsPrefix = "XMAT.XboxLiveCaptureAnalysis.CaptureAnalysisCommands.";
        private const string DeviceTypeEnum = "XMAT.SharedInterfaces.DeviceType";
        private const string CaptureTypeEnum = "XMAT.SharedInterfaces.CaptureType";
        private const string ScriptEventEnum = "XMAT.WebServiceCaptureScriptableEventType";
        private const string ProxyPreferences = "XMAT.WebServiceCapture.Models.PreferencesModel";
        private const string DeviceParameters = "XMAT.WebServiceCapture.WebServiceDeviceCaptureController+ControllerParameters";
        private const string NetworkAnalysisPreferences =
            "XMAT.NetworkTraceCaptureAnalysis.Models.NetworkTraceAnalysisOptionsModel";

        internal static IReadOnlyList<Capability> All { get; } = new[]
        {
            // Analysis
            Capability.Supported(
                "analysis.xbox-live", "Analyze Xbox Live service calls", "S1",
                cli: "analyze INPUT --out DIR [--device ID] [--fail-on findings] [--max-input-mib N] " +
                    "[--max-data-mib N] [--max-record-mib N] [--max-records N]",
                operation: AnalyzeCaptureOperation,
                gui: new[]
                {
                    Command(AppCommandsPrefix + "AnalyzeCaptures"),
                    Analyzer("XMAT.XboxLiveCaptureAnalysis.XboxLiveCaptureAnalyzer"),
                    Window("XMAT.CaptureAnalyzerWindow"),
                    Click(AnalyzerChoiceXaml, "OkButton_Click"),
                }),
            Capability.Planned(
                "analysis.network-trace", "Analyze network traces", "S8b", Owner,
                gui: new[]
                {
                    Command(AppCommandsPrefix + "AnalyzeCaptures"),
                    Analyzer("XMAT.NetworkTraceCaptureAnalysis.NetworkTraceCaptureAnalyzer"),
                    Window("XMAT.CaptureAnalyzerWindow"),
                    Click(AnalyzerChoiceXaml, "OkButton_Click"),
                    Preference(NetworkAnalysisPreferences, "PacketsPerSecondThreshold"),
                    Preference(NetworkAnalysisPreferences, "MaximumMTU"),
                    Preference(NetworkAnalysisPreferences, "DuplicatePacketWindow"),
                }),
            // XMAT-008 exports the HTML report from the GUI; xmat analyze --out writes metadata JSON, not this report.
            Capability.Planned(
                "analysis.html-report", "Write the analysis HTML report", "S3", "XMAT-008",
                gui: new[] { Command(AppCommandsPrefix + "ExportAnalysisReport") }),
            Capability.Planned(
                "analysis.compatibility", "Analyze with the LTA rule set, custom rules or other thresholds", "S1b", Owner),

            // Capture files
            Capability.Supported(
                "capture-file.read-xmatcap", "Read XMAT capture files (.xmatcap)", "S1",
                cli: "analyze INPUT.xmatcap --out DIR",
                operation: AnalyzeCaptureOperation,
                gui: new[] { WpfCommand("Open", MainWindowXaml) }),
            Capability.Supported(
                "capture-file.import-saz", "Read Fiddler archives (.saz)", "S1",
                cli: "analyze INPUT.saz --out DIR",
                operation: AnalyzeCaptureOperation,
                gui: new[]
                {
                    Command(AppCommandsPrefix + "Import"),
                    EnumValue("XMAT.Models.ImportType", "Fiddler"),
                    EnumValue(DeviceTypeEnum, "Unknown"),
                }),
            Capability.Planned(
                "capture-file.import-etl", "Read network traces (.etl)", "S8b", Owner,
                gui: new[]
                {
                    Command(AppCommandsPrefix + "Import"),
                    EnumValue("XMAT.Models.ImportType", "NetTrace"),
                    EnumValue(DeviceTypeEnum, "Unknown"),
                }),
            Capability.Planned(
                "capture-file.export-saz", "Export captures as Fiddler archives (.saz)", "S2", Owner,
                gui: new[]
                {
                    Command(AppCommandsPrefix + "Export"),
                    EnumValue("XMAT.Models.ExportType", "Fiddler"),
                }),
            // XMAT-019 implements sanitized export in the GUI; its headless support is not done yet.
            Capability.Planned(
                "capture-file.sanitize", "Sanitize a capture into a Fiddler archive", "S2", Owner,
                gui: new[]
                {
                    Command(AppCommandsPrefix + "ExportSanitized"),
                    Window("XMAT.SanitizedExportProgressWindow"),
                    Click(SanitizedExportXaml, "CancelExport_Click"),
                }),
            Capability.Planned(
                "capture-file.save-xmatcap", "Save captures (.xmatcap)", "S5", Owner,
                gui: new[] { WpfCommand("Save", MainWindowXaml) }),

            // Capture
            Capability.Planned(
                "capture.add-device", "Choose the device and capture method", "S5", Owner,
                gui: new[]
                {
                    WpfCommand("New", MainWindowXaml),
                    WpfCommand("New", DeviceTabsXaml),
                    Window("XMAT.AddDeviceWindow"),
                    Click(AddDeviceXaml, "OkButton_Click"),
                    EnumValue(CaptureTypeEnum, "WebProxy"),
                }),
            Capability.Planned(
                "capture.generic", "Capture an explicit-proxy (generic) device", "S5", Owner,
                gui: new[] { EnumValue(DeviceTypeEnum, "GenericProxyDevice") }),
            Capability.Planned(
                "capture.local-pc", "Capture this PC", "S6", Owner,
                gui: new[] { EnumValue(DeviceTypeEnum, "LocalPC") }),
            Capability.Planned(
                "capture.xbox", "Capture an Xbox development kit", "S7", Owner,
                gui: new[] { EnumValue(DeviceTypeEnum, "XboxConsole") }),
            Capability.Planned(
                "capture.stop", "Stop a running capture", "S5", Owner,
                gui: new[]
                {
                    Command(ProxyCommandsPrefix + "StartStopCapture"),
                    Window("XMAT.WebProxyStopProgressWindow"),
                    WpfCommand("Close", DeviceTabsXaml),
                }),
            Capability.Planned(
                "capture.clear", "Clear captured data", "S8a", Owner,
                gui: new[] { Command(AppCommandsPrefix + "ClearAllCaptures") }),
            Capability.Planned(
                "capture.network-trace", "Capture low-level network traces", "S8b", Owner,
                gui: new[]
                {
                    Command("XMAT.NetworkTrace.NetworkTraceCommands.StartStopCapture"),
                    EnumValue(CaptureTypeEnum, "NetworkTrace"),
                }),
            Capability.Planned(
                "capture.scripts", "Run capture scripts", "S8a", Owner,
                gui: new[]
                {
                    Command(ScriptCommandsPrefix + "LoadScript"),
                    EnumValue(ScriptEventEnum, "SslConnectionRequest"),
                    EnumValue(ScriptEventEnum, "WebRequest"),
                    EnumValue(ScriptEventEnum, "WebResponse"),
                }),
            Capability.Planned(
                "script.validate", "Validate a capture script without running it", "S8a", Owner,
                gui: new[] { Command(ScriptCommandsPrefix + "ValidateScript") }),
            // The lists last only for the session, but they change what the proxy captures, so they are not
            // presentation.
            Capability.Planned(
                "capture.host-lists", "Block or bypass hosts during capture", "S8a", Owner,
                gui: new[]
                {
                    Command(AppCommandsPrefix + "ManageHostLists"),
                    Window("XMAT.WebServiceCapture.HostListManagerWindow"),
                    Click(HostListManagerXaml, "Close_Click"),
                    Click(BlockListXaml, "AddUrlBlock_Click"),
                    Click(BlockListXaml, "RemoveSelected_Click"),
                    Click(BlockListXaml, "ClearAll_Click"),
                    Click(BypassListXaml, "AddBypass_Click"),
                    Click(BypassListXaml, "RemoveSelected_Click"),
                    Click(BypassListXaml, "ClearAll_Click"),
                }),

            // WebSocket handshakes are kept from the first capture slice on; message payloads are archived later.
            Capability.Planned(
                "capture.websocket-handshakes",
                "Record WebSocket handshakes and report observed frames without their payloads",
                "S4",
                Owner),
            Capability.Planned(
                "capture.websocket-payloads", "Archive WebSocket message payloads", "S8a", Owner),

            // Headless runs
            Capability.Planned("run.records", "List, inspect, recover or clean up headless runs", "S4", Owner),

            // Device proxy configuration
            Capability.Planned(
                "proxy.port", "Choose the proxy port", "S5", Owner,
                gui: new[]
                {
                    Command(ProxyCommandsPrefix + "ChangePort"),
                    Command(ProxyCommandsPrefix + "DiscardPortChange"),
                    Command(ProxyCommandsPrefix + "CancelPortChange"),
                    Window("XMAT.WebServiceCapture.ProxyPortChangeWindow"),
                    Click(PortChangeXaml, "Apply_Click"),
                    Click(PortChangeXaml, "Later_Click"),
                    Preference(ProxyPreferences, "FirstPort"),
                    Preference(ProxyPreferences, "LastPort"),
                    Preference(DeviceParameters, "Port"),
                    Preference(DeviceParameters, "PendingPort"),
                }),
            Capability.Planned(
                "proxy.enable", "Enable a device's proxy for a bounded foreground session", "S6", Owner,
                gui: new[] { Command(ProxyCommandsPrefix + "EnableDevice") }),
            Capability.Planned(
                "proxy.disable",
                "Disable a device's proxy: stop an owned run or recover an attributable one, never a blind reset",
                "S6",
                Owner,
                gui: new[] { Command(ProxyCommandsPrefix + "DisableDevice") }),
            Capability.Planned(
                "proxy.status", "Check a device's proxy status", "S6", Owner,
                gui: new[] { Command(ProxyCommandsPrefix + "CheckDevice") }),
            Capability.Planned("proxy.recover", "Recover proxy settings after a crash", "S6", Owner),
            Capability.Planned(
                "proxy.on-exit", "Restore an Xbox's proxy when capture ends", "S7", Owner,
                gui: new[]
                {
                    Command(AppCommandsPrefix + "Exit"),
                    Window("XMAT.DisableDeviceOnCloseWindow"),
                    Click(DisableOnCloseXaml, "OkButton_Click"),
                    Preference(ProxyPreferences, "PromptToDisableOnClose"),
                }),
            Capability.Planned(
                "xbox.source-address", "Choose the PC address a console sends traffic to", "S7", Owner,
                gui: new[]
                {
                    Window("XMAT.WebServiceCapture.SourceAddressWindow"),
                    Click(SourceAddressXaml, "OkButton_Click"),
                    Preference(DeviceParameters, "ProxyHost"),
                }),
            Capability.Planned(
                "device.status", "Ping an Xbox development kit or read its status (read-only)", "S7", Owner,
                gui: new[] { Command(ProxyCommandsPrefix + "PingDevice") }),

            // Certificates, diagnostics and environment
            Capability.Planned(
                "certificate.export", "Show or export the existing root certificate; never creates one", "S5", Owner,
                gui: new[] { Command(AppCommandsPrefix + "ExportRootCert") }),
            Capability.Planned(
                "diagnostics.logs", "Collect debug logs", "S8a", Owner,
                gui: new[] { Command(AppCommandsPrefix + "CollectLogs") }),
            Capability.Planned(
                "environment.gdk", "Show GDK for Xbox details", "S7", Owner,
                gui: new[] { Command(AppCommandsPrefix + "ViewGDKXInfo") }),
            Capability.Supported(
                "cli.doctor", "Check what a command needs before running it", "S1",
                cli: "doctor --for analyze"),

            // Presentation only
            Capability.GuiOnly(
                "view.main-window", "Main window", "Presentation: hosts the GUI adapters of every capability",
                gui: new[] { Window("XMAT.MainWindow") }),
            Capability.GuiOnly(
                "view.theme", "Light and dark theme", "Presentation: colors only",
                gui: new[]
                {
                    Click(MainWindowXaml, "ToggleDarkMode_Click"),
                    Preference("XMAT.Models.AppPreferencesModel", "Theme"),
                }),
            Capability.GuiOnly(
                "view.language", "Display language",
                "Presentation: the command line emits stable codes and English diagnostics",
                gui: new[] { Preference("XMAT.Models.AppPreferencesModel", "Language") }),
            Capability.GuiOnly(
                "view.preferences-editor", "Preferences window",
                "Presentation: edits saved GUI preferences; headless runs take per-run options instead",
                gui: new[]
                {
                    Command(AppCommandsPrefix + "Preferences"),
                    Window("XMAT.PreferencesWindow"),
                    WpfCommand("Save", PreferencesXaml),
                }),
            Capability.GuiOnly(
                "view.analysis-navigation", "Report navigation and analysis tabs",
                "Presentation: moves between report views and tabs",
                gui: new[]
                {
                    Command(NavigationCommandsPrefix + "GoToId"),
                    Command(NavigationCommandsPrefix + "GoToEndpoint"),
                    Command(NavigationCommandsPrefix + "GoToEndpointBfr"),
                    Command(NavigationCommandsPrefix + "GoToEndpointBdr"),
                    Command(NavigationCommandsPrefix + "GoToEndpointCfr"),
                    Command(NavigationCommandsPrefix + "GoToEndpointPdr"),
                    Command(NavigationCommandsPrefix + "GoToEndpointRcr"),
                    Command(NavigationCommandsPrefix + "GoToEndpointSbdr"),
                    Command(NavigationCommandsPrefix + "GoToEndpointTcr"),
                    WpfCommand("Close", AnalysisTabsXaml),
                }),
            Capability.GuiOnly(
                "view.script-editor", "Script editor",
                "Presentation: editor buffer, save, revert and error navigation; headless scripts are files",
                gui: new[]
                {
                    Command(ScriptCommandsPrefix + "SaveScript"),
                    Command(ScriptCommandsPrefix + "RevertScript"),
                    Command(ScriptCommandsPrefix + "ScriptErrorSelected"),
                }),
            Capability.GuiOnly(
                "view.controls", "Control templates", "Presentation: scroll bar commands inside control templates",
                gui: new[]
                {
                    WpfCommand("ScrollBar.PageUpCommand", "App.xaml"),
                    WpfCommand("ScrollBar.PageDownCommand", "App.xaml"),
                    WpfCommand("ScrollBar.PageLeftCommand", "App.xaml"),
                    WpfCommand("ScrollBar.PageRightCommand", "App.xaml"),
                }),
            Capability.GuiOnly(
                "view.capture-viewers", "Capture viewers",
                "Presentation: filters, timeline and the request, response, WebSocket and packet views"),
        };
    }
}
