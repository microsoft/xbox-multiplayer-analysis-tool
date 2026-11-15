// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using XMAT.DesignSystem;
using XMAT.Tests.HeadlessParity;

namespace XMAT.Tests.DesignSystem
{
    // Every style of the new look appears on a design system page, so the window shows every component the app has.
    // Styles that no page shows yet are listed in UncoveredStyles.txt, which only shrinks: a new style comes with an
    // example on its component's page (docs/design-system.md). Menus, dropdowns and More menus are opened to find the
    // styles of their rows.
    [Collection(CaptureUiApplicationCollection.Name)]
    public class StoryCoverageTests
    {
        private static readonly string ListPath =
            Path.Combine(RepositoryPaths.Root, "XMAT.Tests", "DesignSystem", "UncoveredStyles.txt");

        private readonly CaptureUiApplicationFixture _application;

        public StoryCoverageTests(CaptureUiApplicationFixture application) => _application = application;

        [Fact]
        public void EveryStyle_AppearsOnAPage_OrIsListedAsNotYetShown()
        {
            _application.Run(_ =>
            {
                DesignSystemWindow window = DesignSystemWindowTests.Open("Light");
                try
                {
                    Dictionary<Style, string> names = StyleNames(window.Resources);
                    var shown = new HashSet<string>(StringComparer.Ordinal);
                    for (int index = 0; index < window.Stories.Count; index++)
                    {
                        window.ShowPage(index);
                        WpfTestHost.Pump();
                        foreach (Style style in StylesShownBy(window.ShownStory))
                        {
                            if (names.TryGetValue(style, out string name))
                            {
                                shown.Add(name);
                            }
                        }
                    }

                    string[] notShown = names.Values.Where(name => !shown.Contains(name)).Distinct()
                        .OrderBy(name => name, StringComparer.Ordinal).ToArray();
                    string[] listed = File.ReadAllLines(ListPath)
                        .Where(line => line.Length > 0 && !line.StartsWith('#')).ToArray();
                    string[] unlisted = notShown.Except(listed).ToArray();
                    string[] stale = listed.Except(notShown).ToArray();
                    Assert.True(unlisted.Length == 0,
                        "These styles appear on no design system page. Show each on its component's page in " +
                        "XMAT/DesignSystem/Stories (docs/design-system.md):" + Lines(unlisted));
                    Assert.True(stale.Length == 0,
                        "These styles are on a page now, or no longer exist. Remove them from " +
                        "XMAT.Tests/DesignSystem/UncoveredStyles.txt:" + Lines(stale));
                }
                finally
                {
                    window.Close();
                }
            });
        }

        // The styles of STORY's elements, the styles they are based on, and their focus outlines, with the rows of
        // its menus, dropdowns and More menus opened one at a time.
        private static HashSet<Style> StylesShownBy(Story story)
        {
            var styles = new HashSet<Style>();
            Collect(story, styles);
            foreach (DependencyObject element in Tree(story).ToList())
            {
                switch (element)
                {
                    case ComboBox { IsEnabled: true } dropdown:
                        dropdown.IsDropDownOpen = true;
                        WpfTestHost.Pump();
                        Collect(dropdown, styles);
                        dropdown.IsDropDownOpen = false;
                        break;
                    case MenuItem { Role: MenuItemRole.TopLevelHeader } menu:
                        menu.IsSubmenuOpen = true;
                        WpfTestHost.Pump();
                        Collect(menu, styles);
                        menu.IsSubmenuOpen = false;
                        break;
                    case TabOverflowButton { IsVisible: true } more:
                        more.IsChecked = true;
                        WpfTestHost.Pump();
                        Assert.NotNull(more.Menu);
                        Collect(more.Menu, styles);
                        more.IsChecked = false;
                        break;
                    case FrameworkElement { ContextMenu: ContextMenu contextMenu } owner:
                        contextMenu.PlacementTarget = owner;
                        contextMenu.IsOpen = true;
                        WpfTestHost.Pump();
                        Collect(contextMenu, styles);
                        contextMenu.IsOpen = false;
                        break;
                }
            }

            WpfTestHost.Pump();
            return styles;
        }

        private static void Collect(DependencyObject root, HashSet<Style> styles)
        {
            foreach (DependencyObject element in Tree(root).Prepend(root))
            {
                if (element is FrameworkElement frameworkElement)
                {
                    for (Style style = frameworkElement.Style; style != null; style = style.BasedOn)
                    {
                        styles.Add(style);
                    }

                    for (Style style = frameworkElement.FocusVisualStyle; style != null; style = style.BasedOn)
                    {
                        styles.Add(style);
                    }
                }
            }
        }

        // ROOT's visual tree, with the content of its open popups.
        private static IEnumerable<DependencyObject> Tree(DependencyObject root)
        {
            foreach (DependencyObject child in DesignSystemWindowTests.Descendants(root))
            {
                yield return child;
                if (child is Popup { IsOpen: true, Child: UIElement content })
                {
                    yield return content;
                    foreach (DependencyObject inPopup in Tree(content))
                    {
                        yield return inPopup;
                    }
                }
            }
        }

        // Each style of the new look's files, named by its file and key, such as
        // "Styles/Buttons.xaml: {x:Type Button}".
        private static Dictionary<Style, string> StyleNames(ResourceDictionary look)
        {
            var names = new Dictionary<Style, string>();
            Dictionary<object, string> staticKeys = StaticResourceKeys();
            ResourceDictionary newLook = look.MergedDictionaries
                .Single(dictionary => FileOf(dictionary) == "Looks/NewLook.xaml");
            Add(newLook);
            return names;

            void Add(ResourceDictionary dictionary)
            {
                string file = FileOf(dictionary);
                if (file != null && !file.StartsWith("Themes/", StringComparison.Ordinal))
                {
                    foreach (object key in dictionary.Keys)
                    {
                        if (dictionary[key] is Style style)
                        {
                            string name = key switch
                            {
                                string text => text,
                                Type type => $"{{x:Type {type.Name}}}",
                                _ => staticKeys.TryGetValue(key, out string staticName) ? staticName : key.ToString(),
                            };
                            names[style] = $"{file}: {name}";
                        }
                    }
                }

                foreach (ResourceDictionary merged in dictionary.MergedDictionaries)
                {
                    Add(merged);
                }
            }
        }

        private static string FileOf(ResourceDictionary dictionary)
        {
            string source = dictionary.Source?.OriginalString;
            int component = source?.IndexOf("component/", StringComparison.Ordinal) ?? -1;
            return component < 0 ? null : source[(component + "component/".Length)..];
        }

        // WPF's own resource keys, such as ToolBar.ButtonStyleKey, by name.
        private static Dictionary<object, string> StaticResourceKeys()
        {
            var keys = new Dictionary<object, string>();
            foreach (Type type in typeof(ToolBar).Assembly.GetExportedTypes())
            {
                foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Static)
                    .Where(property => typeof(ResourceKey).IsAssignableFrom(property.PropertyType)))
                {
                    keys.TryAdd(property.GetValue(null), $"{{x:Static {type.Name}.{property.Name}}}");
                }
            }

            return keys;
        }

        private static string Lines(IEnumerable<string> names) =>
            string.Concat(names.Select(name => Environment.NewLine + "  " + name));
    }
}
