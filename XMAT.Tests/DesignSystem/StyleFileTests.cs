// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using System.Xml.Linq;
using XMAT.Tests.HeadlessParity;

namespace XMAT.Tests.DesignSystem
{
    // The new look's style files each load on their own: a StaticResource reference, BasedOn included, names a resource
    // defined in the same file or in a dictionary the file merges. Looks/NewLook.xaml can then merge the files in any
    // order, and a file can be loaded by itself. Themes don't count: they are swapped while the app runs, so their
    // colors are taken with DynamicResource.
    public class StyleFileTests
    {
        private const string ComponentPrefix = "/XboxMultiplayerAnalysisTool;component/";
        private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        private static readonly Regex StaticReference =
            new(@"\{StaticResource\s+(?:ResourceKey=)?(\{[^{}]*\}|[^{}\s,]+)\s*\}", RegexOptions.CultureInvariant);

        [Fact]
        public void StyleFiles_TakeStaticResourcesFromTheirOwnFile()
        {
            Dictionary<string, XDocument> product = CapabilityDiscovery.ProductXaml()
                .Where(xaml => xaml.Path.EndsWith(".xaml", StringComparison.Ordinal))
                .ToDictionary(xaml => xaml.Path, xaml => XDocument.Parse(xaml.Text), StringComparer.Ordinal);
            string[] files = product.Keys
                .Where(path => path.StartsWith("Styles/", StringComparison.Ordinal) || path == "Looks/NewLook.xaml")
                .ToArray();
            Assert.Contains("Styles/Buttons.xaml", files);

            var problems = new List<string>();
            foreach (string file in files)
            {
                HashSet<string> available = Keys(product, file, new HashSet<string>(StringComparer.Ordinal));
                foreach (XAttribute attribute in product[file].Descendants().Attributes())
                {
                    foreach (Match reference in StaticReference.Matches(attribute.Value))
                    {
                        string key = reference.Groups[1].Value;
                        if (!available.Contains(key))
                        {
                            problems.Add(
                                $"{file}: {{StaticResource {key}}} names a resource defined outside the file. Define it " +
                                "in the file, keep a style next to the style it is based on, or take theme and spacing " +
                                "values with DynamicResource (docs/design-system.md).");
                        }
                    }
                }
            }

            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }

        // The keys FILE defines, and those of the dictionaries it merges other than themes.
        private static HashSet<string> Keys(Dictionary<string, XDocument> product, string file, HashSet<string> seen)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (!seen.Add(file))
            {
                return keys;
            }

            XElement root = product[file].Root;
            foreach (XElement element in root.Descendants().Where(IsResource))
            {
                string key = (string)element.Attribute(Xaml + "Key");
                string targetType = (string)element.Attribute("TargetType");
                if (key != null)
                {
                    keys.Add(key);
                }
                else if (element.Name == Presentation + "Style" && targetType != null)
                {
                    keys.Add(targetType.StartsWith("{x:Type ", StringComparison.Ordinal)
                        ? targetType
                        : $"{{x:Type {targetType}}}");
                }
            }

            foreach (string source in root.Elements(Presentation + "ResourceDictionary.MergedDictionaries").Elements()
                .Select(dictionary => (string)dictionary.Attribute("Source"))
                .Where(source => source != null && !source.EndsWith("Theme.xaml", StringComparison.Ordinal)))
            {
                string merged = source.StartsWith(ComponentPrefix, StringComparison.Ordinal)
                    ? source[ComponentPrefix.Length..]
                    : source;
                Assert.True(product.ContainsKey(merged), $"{file} merges {source}, which XMAT.Tests does not embed.");
                keys.UnionWith(Keys(product, merged, seen));
            }

            return keys;
        }

        // A resource is an entry of a dictionary or of an element's Resources; a style set as a property value, such as
        // a TextBlock.Style inside a template, is not.
        private static bool IsResource(XElement element) =>
            element.Parent.Name == Presentation + "ResourceDictionary" ||
            element.Parent.Name.LocalName.EndsWith(".Resources", StringComparison.Ordinal);
    }
}
