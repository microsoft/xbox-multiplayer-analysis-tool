// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.Cli;
using XMAT.Operations.Capabilities;

namespace XMAT.Tests.HeadlessParity
{
    // Every export format runs headless: xmat export and the GUI's export entry points (File > Export, File > Export
    // Sanitized and the capture list's row menu) use the shared export operation, and the export command lines, which
    // help and docs/cli.md show, name exactly the options that the parser accepts.
    public class ExportCapabilityTests
    {
        private const string Operation = "XMAT.Operations.Export.ExportCaptureSessionsOperation";
        private const string ProxyCommands = "XMAT.WebServiceCapture.WebServiceProxyCommands.";

        [Fact]
        public void Export_IsSupportedForEveryFormatThroughTheSharedOperation()
        {
            Capability[] export = CliCatalog.CapabilitiesOf(CliCommands.Export).ToArray();
            string[] named = export
                .SelectMany(capability => capability.CliRoute.Split(new[] { ' ', '[', ']' }, StringSplitOptions.RemoveEmptyEntries))
                .Where(token => token.StartsWith("--", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(option => option, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(
                new[] { "capture-file.export-saz", "capture-file.export-xmatcap", "capture-file.sanitize" },
                export.Select(capability => capability.Id));
            Assert.Equal(CliCommandLine.ExportOptionNames.OrderBy(option => option, StringComparer.Ordinal), named);
            Assert.Equal("S10", export[1].Slice);
            Assert.Contains("--format sanitized-saz", export[2].CliRoute, StringComparison.Ordinal);
            foreach (Capability capability in export)
            {
                string fileCommand = capability.Id == "capture-file.sanitize" ? "ExportSanitized" : "Export";
                Assert.Equal(Operation, capability.Operation);
                Assert.Contains(CapabilityKeys.Command("XMAT.AppCommands." + fileCommand), capability.GuiEntryPoints);
                Assert.Contains(CapabilityKeys.Command(ProxyCommands + "ExportSelectedSessions"), capability.GuiEntryPoints);
                Assert.Contains(CapabilityKeys.Command(ProxyCommands + "ExportVisibleSessions"), capability.GuiEntryPoints);
            }
        }
    }
}
