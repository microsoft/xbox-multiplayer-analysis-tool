// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace XMAT.Tests.DesignSystem
{
    // The rails of XMAT's color system (docs/design-system.md): views and styles use the 38 roles, the new look's
    // themes give each role a step of the generated palette, and the original look's names stay only where the
    // original look still needs them.
    public class ColorRoleTests
    {
        internal static readonly string[] Roles =
        {
            "SurfaceCanvasBrush", "SurfacePanelBrush", "SurfaceWellBrush", "SurfaceHeaderBrush", "SurfaceStripBrush",
            "SurfaceHoverBrush", "SurfaceControlBrush",
            "TextPrimaryBrush", "TextSecondaryBrush", "TextDisabledBrush", "TextHeadingBrush", "TextControlBrush",
            "BorderSubtleBrush", "BorderDefaultBrush", "BorderControlBrush", "BorderStrongBrush", "BorderStrongHoverBrush",
            "BorderWellBrush",
            "AccentBrush", "AccentFillBrush", "AccentOnFillBrush", "AccentSoftBrush",
            "SuccessFillBrush", "SuccessOnFillBrush", "SuccessTextBrush", "SuccessSoftBrush",
            "WarningFillBrush", "WarningOnFillBrush", "WarningTextBrush", "WarningSoftBrush",
            "ErrorFillBrush", "ErrorOnFillBrush", "ErrorTextBrush", "ErrorSoftBrush",
            "InfoFillBrush", "InfoOnFillBrush", "NeutralFillBrush", "NeutralOnFillBrush"
        };

        // Old names that views shared with the original look still use, and the role each stands in for in the new
        // look's themes. Remove them with the original look.
        private static readonly Dictionary<string, string> NewLookStandIns = new()
        {
            ["MenuBackgroundBrush"] = "SurfaceCanvasBrush",
            ["AltRowBackgroundBrush"] = "SurfaceWellBrush",
            ["CapturePendingForegroundBrush"] = "WarningTextBrush"
        };

        // Roles that the original look's themes define in the color of the old name each replaces.
        private static readonly Dictionary<string, string> OriginalLookStandIns = new()
        {
            ["SurfaceCanvasBrush"] = "WindowBackgroundBrush",
            ["SurfacePanelBrush"] = "PanelBackgroundBrush",
            ["SurfaceHoverBrush"] = "ButtonHoverBackgroundBrush",
            ["TextPrimaryBrush"] = "PrimaryForegroundBrush",
            ["TextSecondaryBrush"] = "SecondaryForegroundBrush",
            ["BorderDefaultBrush"] = "BorderBrush",
            ["AccentFillBrush"] = "ActiveSelectionBrush",
            ["AccentOnFillBrush"] = "ActiveSelectionForegroundBrush"
        };

        // Where the original look's names may still be used outside the themes and Looks/OriginalLook.xaml. This list
        // only shrinks: move a use to a role and remove it here. Remove the list with the original look.
        private static readonly string[] AllowedOldNames =
        {
            "Engines/CaptureAnalysis/ReportViews/Controls/PerEndpointDetails.xaml|MenuBackgroundBrush",
            "Engines/CaptureAnalysis/ReportViews/IssueBadge.cs|IssueErrorBackgroundBrush",
            "Engines/CaptureAnalysis/ReportViews/IssueBadge.cs|IssueErrorForegroundBrush",
            "Engines/CaptureAnalysis/ReportViews/IssueBadge.cs|IssueInfoBackgroundBrush",
            "Engines/CaptureAnalysis/ReportViews/IssueBadge.cs|IssueInfoForegroundBrush",
            "Engines/CaptureAnalysis/ReportViews/IssueBadge.cs|IssueWarningBackgroundBrush",
            "Engines/CaptureAnalysis/ReportViews/IssueBadge.cs|IssueWarningForegroundBrush",
            "Engines/CaptureAnalysis/ReportViews/NetworkTraceAnalysisView.xaml|AltRowBackgroundBrush",
            "Engines/CaptureAnalysis/ReportViews/PerEndpointReportView.xaml|MenuBackgroundBrush",
            "Engines/WebServiceProxy/Views/CaptureConnectionView.xaml|CapturePendingForegroundBrush",
            "Engines/WebServiceProxy/Views/CaptureConnectionView.xaml|MenuBackgroundBrush",
            "Engines/WebServiceProxy/Views/CaptureTimelineView.xaml.cs|MenuBackgroundBrush",
            "Windows/MainWindow.xaml|MenuBackgroundBrush"
        };

        private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        private static string Themes => Path.Combine(HeadlessParity.RepositoryPaths.Root, "XMAT", "Themes");

        [Fact]
        public void EveryTheme_DefinesEveryRole_AndTheNewLookThemesMatch()
        {
            Dictionary<string, string> light = Colors("LightTheme.xaml");
            foreach (string theme in new[] { "LightTheme.xaml", "DarkTheme.xaml", "HighContrastTheme.xaml" })
            {
                Assert.Empty(Roles.Except(Colors(theme).Keys));
            }

            Assert.Equal(light.Keys.OrderBy(key => key), Colors("DarkTheme.xaml").Keys.OrderBy(key => key));
        }

        // A new-look color is a palette step; only the clear and the black and white overlays are written as values.
        [Theory]
        [InlineData("LightTheme.xaml")]
        [InlineData("DarkTheme.xaml")]
        public void NewLookThemes_TakeEveryColorFromThePalette(string theme)
        {
            HashSet<string> steps = Palette().Keys.ToHashSet();
            var allowed = new Regex("^(Transparent|#[0-9A-F]{2}000000|#[0-9A-F]{2}FFFFFF)$");
            foreach (XAttribute color in Document(theme).Descendants().Attributes("Color"))
            {
                Match step = Regex.Match(color.Value, @"^\{StaticResource (\w+)\}$");
                Assert.True(step.Success ? steps.Contains(step.Groups[1].Value) : allowed.IsMatch(color.Value),
                    $"{theme}: {color.Parent.Attribute(Xaml + "Key")?.Value} is {color.Value}, not a palette step.");
            }
        }

        [Fact]
        public void OldNames_StayWhereTheOriginalLookNeedsThem()
        {
            HashSet<string> current = Colors("LightTheme.xaml").Keys.Except(NewLookStandIns.Keys).ToHashSet();
            HashSet<string> old = Colors("Original/LightTheme.xaml").Keys
                .Concat(Colors("HighContrastTheme.xaml").Keys)
                .Where(key => !current.Contains(key))
                .ToHashSet();
            string root = Path.Combine(HeadlessParity.RepositoryPaths.Root, "XMAT");
            var found = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string path in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".xaml", StringComparison.Ordinal) || path.EndsWith(".cs", StringComparison.Ordinal)))
            {
                string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (relative.StartsWith("Themes/", StringComparison.Ordinal) || relative == "Looks/OriginalLook.xaml" ||
                    relative.Split('/').Any(part => part is "bin" or "obj"))
                {
                    continue;
                }

                string text = File.ReadAllText(path);
                foreach (string key in old)
                {
                    // XAML refers to a resource with a markup extension; C# with the key as a string.
                    string pattern = relative.EndsWith(".cs", StringComparison.Ordinal)
                        ? $"\"{key}\""
                        : $@"(Resource\s+(ResourceKey=)?{key}\s*[}},])|(ResourceKey=""{key}"")";
                    if (Regex.IsMatch(text, pattern))
                    {
                        found.Add($"{relative}|{key}");
                    }
                }
            }

            Assert.Equal(AllowedOldNames.OrderBy(use => use, StringComparer.Ordinal), found);
        }

        [Fact]
        public void StandIns_KeepTheColorsTheyStandFor()
        {
            foreach (string theme in new[] { "LightTheme.xaml", "DarkTheme.xaml" })
            {
                Dictionary<string, string> colors = Colors(theme);
                Assert.All(NewLookStandIns, pair => Assert.Equal(colors[pair.Value], colors[pair.Key]));
            }

            foreach (string theme in new[] { "Original/LightTheme.xaml", "Original/DarkTheme.xaml" })
            {
                Dictionary<string, string> colors = Colors(theme);
                Assert.All(OriginalLookStandIns, pair => Assert.Equal(colors[pair.Value], colors[pair.Key]));
            }
        }

        // Text roles meet 4.5:1 on every surface they appear on, and control text on a control's fills.
        [Theory]
        [InlineData("LightTheme.xaml")]
        [InlineData("DarkTheme.xaml")]
        public void TextRoles_AreReadableOnEverySurface(string theme)
        {
            Dictionary<string, string> palette = Palette();
            Dictionary<string, string> colors = Colors(theme);
            string Hex(string role) => palette[Regex.Match(colors[role], @"^\{StaticResource (\w+)\}$").Groups[1].Value];

            var pairs = Roles.Where(role => role.StartsWith("Surface", StringComparison.Ordinal))
                .SelectMany(surface => new[] { ("TextPrimaryBrush", surface), ("TextSecondaryBrush", surface) })
                .Concat(new[]
                {
                    ("TextHeadingBrush", "SurfacePanelBrush"), ("TextControlBrush", "SurfaceControlBrush"),
                    ("TextControlBrush", "SurfaceHoverBrush"), ("TextControlBrush", "AccentSoftBrush"),
                    ("AccentOnFillBrush", "AccentFillBrush")
                })
                .Concat(new[] { "Success", "Warning", "Error" }.SelectMany(status => new[]
                {
                    ($"{status}TextBrush", $"{status}SoftBrush"), ($"{status}TextBrush", "SurfacePanelBrush"),
                    ($"{status}TextBrush", "SurfaceWellBrush")
                }));
            foreach (var (text, surface) in pairs)
            {
                double contrast = Contrast(Hex(text), Hex(surface));
                Assert.True(contrast >= 4.5, $"{theme}: {text} on {surface} is {contrast:F2}:1");
            }
        }

        private static XDocument Document(string theme) => XDocument.Load(Path.Combine(Themes, theme));

        // Each keyed brush's Color attribute, as written.
        private static Dictionary<string, string> Colors(string theme) =>
            Document(theme).Descendants()
                .Where(element => element.Attribute(Xaml + "Key") is { } key && !key.Value.StartsWith('{'))
                .ToDictionary(element => element.Attribute(Xaml + "Key").Value,
                    element => element.Attribute("Color")?.Value ?? element.Name.LocalName);

        private static Dictionary<string, string> Palette() =>
            Document("Palette.xaml").Descendants()
                .Where(element => element.Name.LocalName == "Color")
                .ToDictionary(element => element.Attribute(Xaml + "Key").Value, element => "#" + element.Value[3..]);

        private static double Contrast(string first, string second)
        {
            static double Luminance(string hex)
            {
                static double Linear(string component)
                {
                    double value = int.Parse(component, NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
                    return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
                }

                return 0.2126 * Linear(hex[1..3]) + 0.7152 * Linear(hex[3..5]) + 0.0722 * Linear(hex[5..7]);
            }

            double a = Luminance(first), b = Luminance(second);
            return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
        }
    }
}
