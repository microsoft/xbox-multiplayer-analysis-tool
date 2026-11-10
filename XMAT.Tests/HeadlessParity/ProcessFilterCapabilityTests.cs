// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.Operations.Capabilities;

namespace XMAT.Tests.HeadlessParity
{
    // The process filter's GUI entry points are the product's own: the editor's buttons, the proxy commands that the
    // row and timeline menus run, and the analyzer selection that offers the filtered analysis. Discovery finds each of
    // them, and the registry classifies exactly these under capture.process-filter, so no key is invented or missing.
    public class ProcessFilterCapabilityTests
    {
        private const string CaptureViewXaml = "Engines/WebServiceProxy/Views/CaptureConnectionView.xaml";
        private const string ProxyCommands = "XMAT.WebServiceCapture.WebServiceProxyCommands.";

        private static readonly string[] EntryPoints =
        {
            CapabilityKeys.Click(CaptureViewXaml, "ApplyProcessFilter_Click"),
            CapabilityKeys.Click(CaptureViewXaml, "HideSvchost_Click"),
            CapabilityKeys.Click(CaptureViewXaml, "ShowAllProcesses_Click"),
            CapabilityKeys.Command(ProxyCommands + "ShowOnlyProcess"),
            CapabilityKeys.Command(ProxyCommands + "HideProcess"),
            CapabilityKeys.Command(ProxyCommands + "ShowAllProcesses"),
            CapabilityKeys.Command("XMAT.AppCommands.AnalyzeCaptures"),
            CapabilityKeys.Window("XMAT.CaptureAnalyzerWindow"),
        };

        [Fact]
        public void ProcessFilter_ClassifiesExactlyItsDiscoveredEntryPoints()
        {
            Capability capability = Assert.Single(
                CapabilityRegistry.All,
                candidate => candidate.Id == "capture.process-filter");
            IReadOnlyCollection<string> discovered = CapabilityDiscovery.DiscoverProduct();

            Assert.Equal(
                EntryPoints.OrderBy(key => key, StringComparer.Ordinal),
                capability.GuiEntryPoints.OrderBy(key => key, StringComparer.Ordinal));
            Assert.All(EntryPoints, key => Assert.Contains(key, discovered));
        }
    }
}
