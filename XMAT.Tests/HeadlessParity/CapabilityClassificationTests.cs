// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows.Input;
using XMAT.Operations.Capabilities;

namespace XMAT.Tests.HeadlessParity
{
    // Test A: every GUI entry point is classified as Supported, Planned or (presentation only) GuiOnly, and the
    // registry lists nothing that no longer exists.
    public class CapabilityClassificationTests
    {
        private const string SyntheticView = "Synthetic/SyntheticView.xaml";

        [Fact]
        public void EveryGuiEntryPoint_IsClassified_AndNoRegistryEntryIsStale()
        {
            ParityReport report = CapabilityClassifier.Classify(
                CapabilityDiscovery.DiscoverProduct(), CapabilityRegistry.All);

            Assert.True(report.IsClean, report.Describe());
        }

        [Fact]
        public void Registry_FollowsTheClassificationRules()
        {
            IReadOnlyList<string> problems = CapabilityRegistryRules.Validate(
                CapabilityRegistry.All, typeof(PublicUtilities).Assembly);

            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }

        [Fact]
        public void Discovery_FindsEachKindOfEntryPoint_AndIgnoresCommentedMarkup()
        {
            IReadOnlyCollection<string> keys = CapabilityDiscovery.DiscoverProduct();

            Assert.Contains(CapabilityKeys.Command("XMAT.AppCommands.Import"), keys);
            Assert.Contains(CapabilityKeys.Command("XMAT.WebServiceCapture.WebServiceProxyCommands.PingDevice"), keys);
            Assert.Contains(CapabilityKeys.WpfCommand("New", "Windows/MainWindow.xaml"), keys);
            Assert.Contains(CapabilityKeys.WpfCommand("Close", "Controls/AllCaptureDevicesTabControl.xaml"), keys);
            Assert.Contains(CapabilityKeys.Click("Windows/MainWindow.xaml", "ToggleDarkMode_Click"), keys);
            Assert.Contains(CapabilityKeys.Window("XMAT.MainWindow"), keys);
            Assert.Contains(CapabilityKeys.EnumValue("XMAT.SharedInterfaces.DeviceType", "GenericProxyDevice"), keys);
            Assert.Contains(CapabilityKeys.EnumValue("XMAT.WebServiceCaptureScriptableEventType", "WebResponse"), keys);
            Assert.Contains(CapabilityKeys.Analyzer("XMAT.XboxLiveCaptureAnalysis.XboxLiveCaptureAnalyzer"), keys);
            Assert.Contains(CapabilityKeys.Preference("XMAT.WebServiceCapture.Models.PreferencesModel", "PromptToDisableOnClose"), keys);
            Assert.Contains(
                CapabilityKeys.Preference("XMAT.WebServiceCapture.WebServiceDeviceCaptureController+ControllerParameters", "ProxyHost"),
                keys);

            // StatsReportView keeps an old button inside an XML comment; commented markup is not an entry point.
            Assert.DoesNotContain(
                CapabilityKeys.Click("Engines/CaptureAnalysis/ReportViews/StatsReportView.xaml", "Button_Click"), keys);
        }

        [Fact]
        public void EmbeddedXaml_CoversTheProductViews()
        {
            List<string> paths = CapabilityDiscovery.ProductXaml().Select(xaml => xaml.Path).ToList();

            Assert.Contains("App.xaml", paths);
            Assert.Contains("Windows/MainWindow.xaml", paths);
            Assert.Contains("Engines/WebServiceProxy/Views/CaptureConnectionView.xaml", paths);
            Assert.DoesNotContain(paths, path => path.StartsWith("bin/", StringComparison.Ordinal) ||
                path.StartsWith("obj/", StringComparison.Ordinal));
        }

        [Fact]
        public void XamlStaticCommands_ResolveToDeclaredRoutedCommands()
        {
            var declared = new HashSet<string>(
                CapabilityDiscovery.DiscoverAssembly(typeof(PublicUtilities).Assembly)
                    .Where(key => key.StartsWith("command:XMAT.", StringComparison.Ordinal)),
                StringComparer.Ordinal);

            List<string> unresolved = CapabilityDiscovery.ProductXaml()
                .SelectMany(xaml => CapabilityDiscovery.DiscoverXaml(xaml.Path, xaml.Text))
                .Where(key => key.StartsWith("command:XMAT.", StringComparison.Ordinal) && !declared.Contains(key))
                .Distinct()
                .ToList();

            Assert.True(unresolved.Count == 0, "XAML names commands that XMAT does not declare: " + string.Join(", ", unresolved));
        }

        [Fact]
        public void NewUnclassifiedEntryPoints_AreReported()
        {
            const string Xaml =
                "<UserControl xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" " +
                "xmlns:local=\"clr-namespace:XMAT.Synthetic;assembly=XboxMultiplayerAnalysisTool\">" +
                "<UserControl.CommandBindings><CommandBinding Command=\"{x:Static local:SyntheticCommands.Run}\" /></UserControl.CommandBindings>" +
                "<StackPanel>" +
                "<Button Click=\"Export_Click\" />" +
                "<Button Command=\"{Binding RunCommand}\" />" +
                "<Button Command=\"Open\" />" +
                "<!-- <Button Click=\"Commented_Click\" /> -->" +
                "</StackPanel></UserControl>";

            IReadOnlyCollection<string> discovered = CapabilityDiscovery.ProductXaml()
                .SelectMany(xaml => CapabilityDiscovery.DiscoverXaml(xaml.Path, xaml.Text))
                .Concat(CapabilityDiscovery.DiscoverXaml(SyntheticView, Xaml))
                .Concat(CapabilityDiscovery.DiscoverAssembly(typeof(PublicUtilities).Assembly))
                .Concat(CapabilityDiscovery.DiscoverAssembly(typeof(SyntheticRoutedCommands).Assembly))
                .ToList();

            ParityReport report = CapabilityClassifier.Classify(discovered, CapabilityRegistry.All);

            Assert.Contains(CapabilityKeys.Command("XMAT.Synthetic.SyntheticCommands.Run"), report.Unclassified);
            Assert.Contains(CapabilityKeys.Click(SyntheticView, "Export_Click"), report.Unclassified);
            Assert.Contains(CapabilityKeys.Dynamic(SyntheticView, "Command={Binding RunCommand}"), report.Unclassified);
            Assert.Contains(CapabilityKeys.WpfCommand("Open", SyntheticView), report.Unclassified);
            Assert.Contains(
                CapabilityKeys.Command(typeof(SyntheticRoutedCommands).FullName + "." + nameof(SyntheticRoutedCommands.Exported)),
                report.Unclassified);
            Assert.DoesNotContain(CapabilityKeys.Click(SyntheticView, "Commented_Click"), report.Unclassified);
            Assert.False(report.IsClean);
        }

        [Fact]
        public void RegistryEntriesForMissingEntryPoints_AreReportedAsStale()
        {
            const string Missing = "click:Windows/RemovedWindow.xaml:Gone_Click";
            var registry = CapabilityRegistry.All
                .Append(Capability.Planned("capture.synthetic", "Synthetic", "S9", "tests", gui: new[] { Missing }))
                .ToList();

            ParityReport report = CapabilityClassifier.Classify(CapabilityDiscovery.DiscoverProduct(), registry);

            Assert.Equal(new[] { Missing }, report.Stale);
            Assert.DoesNotContain(Missing, report.Unclassified);
        }

        [Fact]
        public void RegistryRules_RejectMisclassifiedEntries()
        {
            var registry = new[]
            {
                Capability.GuiOnly("capture.hidden", "Not presentation", "Because", gui: null),
                Capability.Supported("analysis.unbacked", "No operation", "S1", "analyze X",
                    gui: new[] { CapabilityKeys.Command("XMAT.AppCommands.AnalyzeCaptures") }),
                Capability.Supported("analysis.wrong-operation", "Not an operation", "S1", "analyze X",
                    operation: typeof(PublicUtilities).FullName),
                Capability.Planned("Bad Id", "Bad id", "S1", "owner"),
                Capability.Planned("capture.ownerless", "No owner", "S1", owner: null),
            };

            IReadOnlyList<string> problems = CapabilityRegistryRules.Validate(registry, typeof(PublicUtilities).Assembly);

            Assert.Contains(problems, problem => problem.StartsWith("capture.hidden: GuiOnly is for presentation only", StringComparison.Ordinal));
            Assert.Contains(problems, problem => problem.StartsWith("capture.hidden: a GuiOnly reason", StringComparison.Ordinal));
            Assert.Contains(problems, problem => problem.StartsWith("analysis.unbacked: a supported GUI capability needs the shared operation", StringComparison.Ordinal));
            Assert.Contains(problems, problem => problem.StartsWith("analysis.wrong-operation: 'XMAT.PublicUtilities' is not an IOperation", StringComparison.Ordinal));
            Assert.Contains(problems, problem => problem.StartsWith("Bad Id: ids are lower-case", StringComparison.Ordinal));
            Assert.Contains(problems, problem => problem.StartsWith("capture.ownerless: a planned capability needs an owner", StringComparison.Ordinal));
        }

        [Fact]
        public void Discovery_DoesNotRunStaticConstructors()
        {
            // SyntheticRoutedCommands throws from its static constructor; discovery must only read metadata.
            IReadOnlyCollection<string> keys = CapabilityDiscovery.DiscoverAssembly(typeof(SyntheticRoutedCommands).Assembly);

            Assert.Contains(
                CapabilityKeys.Command(typeof(SyntheticRoutedCommands).FullName + "." + nameof(SyntheticRoutedCommands.Exported)),
                keys);
        }
    }

    // A command that no capability classifies. Its static constructor throws so that any discovery code that
    // runs it (by reading the value) fails the tests above.
    internal static class SyntheticRoutedCommands
    {
        internal static readonly RoutedUICommand Exported;

        static SyntheticRoutedCommands()
        {
            Exported = new RoutedUICommand("Exported", nameof(Exported), typeof(SyntheticRoutedCommands));
            throw new InvalidOperationException("Capability discovery must not run static constructors.");
        }
    }
}
