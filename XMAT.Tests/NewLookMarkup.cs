// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Xml.Linq;
using XMAT.Tests.HeadlessParity;

namespace XMAT.Tests
{
    // The new look's own resources as one document: the component style files Looks/NewLook.xaml merges, then
    // Looks/NewLook.xaml itself, so every style comes after the styles it is based on. Tests that take single styles
    // parse them from here, so moving a style between these files doesn't change what they find. The other
    // dictionaries Looks/NewLook.xaml merges (themes, strings, spacing, motion, the toggle switch and the charts) are
    // loaded by the tests that need them.
    internal static class NewLookMarkup
    {
        internal static readonly string[] Files =
        {
            "Styles/Menus.xaml",
            "Styles/Buttons.xaml",
            "Styles/Toolbar.xaml",
            "Styles/Inputs.xaml",
            "Styles/Tabs.xaml",
            "Styles/ChoiceCards.xaml",
            "Styles/ScrollBars.xaml",
            "Styles/Status.xaml",
            "Looks/NewLook.xaml",
        };

        private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        private static readonly Lazy<XDocument> Merged = new(Load);

        internal static XDocument Document => Merged.Value;

        private static XDocument Load()
        {
            Dictionary<string, string> sources = CapabilityDiscovery.ProductXaml()
                .Where(xaml => Files.Contains(xaml.Path, StringComparer.Ordinal))
                .ToDictionary(xaml => xaml.Path, xaml => xaml.Text, StringComparer.Ordinal);
            var root = new XElement(Presentation + "ResourceDictionary");
            foreach (string file in Files)
            {
                Assert.True(sources.ContainsKey(file), $"XMAT.Tests does not embed {file}.");
                XElement dictionary = XDocument.Parse(sources[file]).Root;
                foreach (XAttribute declaration in
                    dictionary.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
                {
                    XAttribute existing = root.Attribute(declaration.Name);
                    Assert.True(existing == null || existing.Value == declaration.Value,
                        $"{file} maps {declaration.Name.LocalName} to {declaration.Value}, not {existing?.Value}.");
                    root.SetAttributeValue(declaration.Name, declaration.Value);
                }

                root.Add(dictionary.Elements()
                    .Where(element => element.Name != Presentation + "ResourceDictionary.MergedDictionaries"));
            }

            return new XDocument(root);
        }
    }
}
