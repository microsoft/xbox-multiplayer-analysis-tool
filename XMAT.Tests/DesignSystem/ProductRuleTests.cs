// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using XMAT.Tests.HeadlessParity;

namespace XMAT.Tests.DesignSystem
{
    // The product follows the design system's rules (XMAT.DesignSystem/RuleCatalog.cs), read from its source. Each
    // failure starts with the rule's ID, names the file and says what to do instead. A use a rule allows for a reason is
    // listed here with that reason. The lists only shrink: a new use fails, and so does a listed one the product no
    // longer has.
    public class ProductRuleTests
    {
        // COLOR-1's exceptions, as "file|use": colors that aren't a view's own color.
        private static readonly string[] ColorValueExceptions =
        {
            // Measure text: FormattedText needs a brush, but draws nothing.
            "Controls/ColumnSummaryView.cs|Brushes.Black",
            "Controls/HistogramView.cs|Brushes.Black",
            // Default values of inherited Foreground properties, which the call rate chart's styles set.
            "Engines/CaptureAnalysis/ReportViews/Controls/CallRateAxis.cs|Brushes.Black",
            "Engines/CaptureAnalysis/ReportViews/Controls/CallRateLimitLabel.cs|Brushes.Black",
            // Shadows, which are black at an opacity, until shadows become tokens.
            "Controls/MenuShadow.cs|Colors.Black",
            "Styles/AnalysisCharts.xaml|Color=\"Black\"",
            "Styles/DataViz.xaml|Color=\"Black\"",
            // Colors that come from data, such as each device's color, and text worked out to read on them.
            "Logic/ColorToBrushConverter.cs|Color.FromRgb(",
            "Logic/ColorToBrushConverter.cs|new SolidColorBrush(",
            "Logic/SelectionForegroundConverter.cs|Color.FromArgb(",
            "Logic/SelectionForegroundConverter.cs|Colors.Black",
            "Logic/SelectionForegroundConverter.cs|Colors.White",
            "Logic/SelectionForegroundConverter.cs|new SolidColorBrush(",
            "Models/CaptureDeviceContextModel.cs|Color.FromArgb(",
        };

        private static readonly Regex PaletteStep = new(
            @"\{(?:Static|Dynamic)Resource\s+(?:ResourceKey=)?(?:Gray|Blue|Green|Amber|Red)[0-9]{1,2}\s*\}",
            RegexOptions.CultureInvariant);

        private static readonly Regex HexColor = new(
            "^#(?:[0-9A-Fa-f]{3,4}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$", RegexOptions.CultureInvariant);

        // A black or white overlay, which themes write as values too (ColorRoleTests).
        private static readonly Regex Overlay = new("^#[0-9A-Fa-f]{2}(?:000000|FFFFFF|ffffff)$", RegexOptions.CultureInvariant);

        private static readonly Regex CodeColorValue = new(
            @"\b(?:Brushes|Colors)\.(?!Transparent\b)[A-Z]\w*|new SolidColorBrush\(|\bColor\.From(?:Argb|Rgb|ScRgb)\(|" +
            @"ColorConverter\.ConvertFromString\(|""(?:Gray|Blue|Green|Amber|Red)[0-9]{1,2}""",
            RegexOptions.CultureInvariant);

        private static readonly Regex StaticReference = new(
            @"\{StaticResource\s+(?:ResourceKey=)?(?<key>[\w.]+)\s*\}", RegexOptions.CultureInvariant);

        // Named colors, such as White, as XAML writes them.
        private static readonly HashSet<string> ColorNames = typeof(System.Windows.Media.Colors)
            .GetProperties()
            .Select(property => property.Name)
            .Where(name => name != "Transparent")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        private static readonly string[] BrushProperties = { "Color", "Brush", "Background", "Foreground", "Fill", "Stroke" };

        [Fact]
        public void Views_TakeColorsFromRoles()
        {
            var found = new SortedSet<string>(StringComparer.Ordinal);
            foreach ((string file, string text) in ProductFiles(".xaml"))
            {
                foreach (string use in XamlColorValues(text))
                {
                    found.Add($"{file}|{use}");
                }
            }

            foreach ((string file, string text) in ProductFiles(".cs"))
            {
                foreach (Match use in CodeColorValue.Matches(text))
                {
                    found.Add($"{file}|{use.Value}");
                }
            }

            string[] added = found.Except(ColorValueExceptions, StringComparer.Ordinal).ToArray();
            string[] gone = ColorValueExceptions.Except(found, StringComparer.Ordinal).ToArray();
            Assert.True(added.Length == 0 && gone.Length == 0, string.Join(Environment.NewLine,
                added.Select(use => $"COLOR-1: {Describe(use)}, a color written in. Take the color from a role or a " +
                    "component color with DynamicResource (docs/design-system.md).")
                .Concat(gone.Select(use => $"COLOR-1: {Describe(use)} no longer, so remove \"{use}\" from " +
                    $"{nameof(ProductRuleTests)}.{nameof(ColorValueExceptions)}."))));
        }

        [Fact]
        public void Views_TakeThemeColorsWithDynamicResource()
        {
            HashSet<string> themeColors = ThemeColorKeys();
            Assert.Contains("SurfacePanelBrush", themeColors);
            var found = new List<string>();
            foreach ((string file, string text) in ProductFiles(".xaml", includeOriginalLook: true))
            {
                foreach (Match reference in StaticReference.Matches(text))
                {
                    if (themeColors.Contains(reference.Groups["key"].Value))
                    {
                        found.Add($"COLOR-2: {file} takes {reference.Groups["key"].Value} with {reference.Value}, " +
                            "so it keeps its first color when the theme changes. Use DynamicResource.");
                    }
                }
            }

            Assert.True(found.Count == 0, string.Join(Environment.NewLine, found));
        }

        [Fact]
        public void IconButtons_HaveANameAndATooltip()
        {
            var found = new List<string>();
            int iconButtons = 0;
            foreach ((string file, string text) in ProductFiles(".xaml", includeOriginalLook: true))
            {
                XDocument document = XDocument.Parse(text, LoadOptions.SetLineInfo);
                foreach (XElement button in document.Descendants().Where(IsButton).Where(ShowsOnlyAnIcon))
                {
                    iconButtons++;
                    bool named = HasAttribute(button, "AutomationProperties.Name") ||
                        HasAttribute(button, "AutomationProperties.LabeledBy");
                    bool tooltip = HasAttribute(button, "ToolTip") || HasAttribute(button, "ToolTipService.ToolTip") ||
                        button.Elements().Any(child => child.Name.LocalName.EndsWith(".ToolTip", StringComparison.Ordinal));
                    if (!named || !tooltip)
                    {
                        found.Add($"BUTTON-2: {file}, line {((IXmlLineInfo)button).LineNumber}: this " +
                            $"{button.Name.LocalName} shows only an icon, so give it a ToolTip and an " +
                            "AutomationProperties.Name that say what it does.");
                    }
                }
            }

            // The check finds the icon buttons it is for, such as New capture and the tabs' close buttons.
            Assert.True(iconButtons >= 4, $"Found {iconButtons} icon buttons; the check no longer recognizes them.");
            Assert.True(found.Count == 0, string.Join(Environment.NewLine, found));
        }

        // XAML and C# files of the product, as paths under XMAT. The themes define colors, and the original look's own
        // resources keep its original colors until it goes, so the color checks leave them out.
        private static IEnumerable<(string File, string Text)> ProductFiles(string extension, bool includeOriginalLook = false)
        {
            string root = Path.Combine(RepositoryPaths.Root, "XMAT");
            foreach (string path in Directory.EnumerateFiles(root, "*" + extension, SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (relative.Split('/').Any(part => part is "bin" or "obj") ||
                    relative.StartsWith("Themes/", StringComparison.Ordinal) ||
                    (!includeOriginalLook && relative == "Looks/OriginalLook.xaml"))
                {
                    continue;
                }

                yield return (relative, File.ReadAllText(path));
            }
        }

        // Colors a XAML file writes in: palette steps, and hex or named colors given to a brush or color property,
        // directly or with a style's setter.
        private static IEnumerable<string> XamlColorValues(string text)
        {
            foreach (Match step in PaletteStep.Matches(text))
            {
                yield return step.Value;
            }

            foreach (XElement element in XDocument.Parse(text).Descendants())
            {
                foreach (XAttribute attribute in element.Attributes())
                {
                    string name = attribute.Name.LocalName;
                    if (IsBrushProperty(name) && IsColorValue(attribute.Value))
                    {
                        yield return $"{name}=\"{attribute.Value}\"";
                    }
                }

                if (element.Name.LocalName == "Setter" &&
                    IsBrushProperty((string)element.Attribute("Property") ?? string.Empty) &&
                    IsColorValue((string)element.Attribute("Value") ?? string.Empty))
                {
                    yield return $"{element.Attribute("Property").Value}=\"{element.Attribute("Value").Value}\"";
                }
            }
        }

        private static bool IsBrushProperty(string name)
        {
            string property = name[(name.LastIndexOf('.') + 1)..];
            return BrushProperties.Any(suffix => property.EndsWith(suffix, StringComparison.Ordinal));
        }

        private static bool IsColorValue(string value) =>
            (HexColor.IsMatch(value) && !Overlay.IsMatch(value)) || ColorNames.Contains(value);

        // The keys of every theme's colors, new look and original.
        private static HashSet<string> ThemeColorKeys()
        {
            XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
            string themes = Path.Combine(RepositoryPaths.Root, "XMAT", "Themes");
            return Directory.EnumerateFiles(themes, "*Theme.xaml", SearchOption.AllDirectories)
                .SelectMany(path => XDocument.Load(path).Descendants())
                .Select(element => (string)element.Attribute(xaml + "Key"))
                .Where(key => key != null && !key.StartsWith('{'))
                .ToHashSet(StringComparer.Ordinal);
        }

        // A button element; a property element such as Track.IncreaseRepeatButton holds one but isn't one.
        private static bool IsButton(XElement element)
        {
            string name = element.Name.LocalName;
            return name.EndsWith("Button", StringComparison.Ordinal) && name != "RadioButton" && !name.Contains('.');
        }

        // Whether a button shows an icon and no words: an icon button style, an icon font glyph, or content that draws
        // shapes without any text.
        private static bool ShowsOnlyAnIcon(XElement button)
        {
            if (Regex.IsMatch((string)button.Attribute("Style") ?? string.Empty, "Icon|TabClose"))
            {
                return true;
            }

            string content = (string)button.Attribute("Content");
            if (content != null)
            {
                return IsGlyph(content);
            }

            XElement[] parts = button.Elements().Where(child => !child.Name.LocalName.Contains('.')).ToArray();
            if (parts.Length == 0)
            {
                string words = string.Concat(button.Nodes().OfType<XText>().Select(node => node.Value)).Trim();
                return words.Length > 0 && IsGlyph(words);
            }

            return !parts.SelectMany(part => part.DescendantsAndSelf()).Any(HasWords);
        }

        private static bool HasWords(XElement element)
        {
            string text = (string)element.Attribute("Text") ?? (string)element.Attribute("Content");
            if (text != null && (text.StartsWith('{') || !IsGlyph(text)))
            {
                return true;
            }

            string words = string.Concat(element.Nodes().OfType<XText>().Select(node => node.Value)).Trim();
            return words.Length > 0 && !IsGlyph(words);
        }

        // One or two characters of an icon font, which keeps its glyphs in Unicode's private use area.
        private static bool IsGlyph(string text)
        {
            string trimmed = text.Trim();
            return trimmed.Length is > 0 and <= 2 && trimmed.All(character => character is >= '\uE000' and <= '\uF8FF');
        }

        private static bool HasAttribute(XElement element, string name) =>
            !string.IsNullOrWhiteSpace((string)element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == name));

        private static string Describe(string use)
        {
            int separator = use.IndexOf('|', StringComparison.Ordinal);
            return $"{use[..separator]} uses {use[(separator + 1)..]}";
        }
    }
}
