// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;

namespace XMAT.Tests.DesignSystem
{
    // Each look's resources are a dictionary that merges the look's file, with the theme beside it, so a theme change
    // shows at once: WPF doesn't tell views when the merged dictionaries of a dictionary loaded from a file change.
    [Collection(CaptureUiApplicationCollection.Name)]
    public class LookResourcesTests
    {
        private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        private readonly CaptureUiApplicationFixture _application;

        public LookResourcesTests(CaptureUiApplicationFixture application) => _application = application;

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ATheme_ChosenWhileTheLookIsShown_RecolorsItsViews(bool newLook)
        {
            _application.Run(window =>
            {
                ResourceDictionary look = ThemeManager.CreateLook(newLook);
                ThemeManager.LoadTheme(look, "Light", newLook, highContrast: false);
                window.Resources = look;
                var panel = new Border { Width = 40, Height = 40 };
                panel.SetResourceReference(Border.BackgroundProperty, "SurfacePanelBrush");
                WpfTestHost.Show(window, panel);
                Color light = ((SolidColorBrush)panel.Background).Color;

                ThemeManager.LoadTheme(look, "Dark", newLook, highContrast: false);
                WpfTestHost.Pump();
                Color dark = ((SolidColorBrush)panel.Background).Color;
                Assert.NotEqual(light, dark);
                Assert.Equal(((SolidColorBrush)look["SurfacePanelBrush"]).Color, dark);

                ThemeManager.LoadTheme(look, "Light", newLook, highContrast: false);
                WpfTestHost.Pump();
                Assert.Equal(light, ((SolidColorBrush)panel.Background).Color);
            });
        }

        // The theme a look's file declares goes too, so no theme is left to supply system colors that high contrast
        // leaves to Windows.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void HighContrast_LeavesNoOtherThemeBehind(bool newLook)
        {
            _application.Run(_ =>
            {
                ResourceDictionary look = ThemeManager.CreateLook(newLook);
                ThemeManager.LoadTheme(look, "Dark", newLook, highContrast: true);
                string Theme(ResourceDictionary dictionary) => dictionary.Source?.OriginalString ?? string.Empty;
                IEnumerable<ResourceDictionary> All(ResourceDictionary dictionary) =>
                    dictionary.MergedDictionaries.SelectMany(merged => All(merged).Prepend(merged));

                Assert.Equal(
                    new[] { "/HighContrastTheme.xaml" },
                    All(look).Select(Theme).Where(source => source.EndsWith("Theme.xaml", StringComparison.Ordinal))
                        .Select(source => source[source.LastIndexOf('/')..]));
                Assert.Null(look[SystemColors.WindowBrushKey]);
            });
        }

        [Fact]
        public void AppXaml_MergesTheNewLookRatherThanLoadingIt()
        {
            XDocument app = CaptureChromeTests.ProductMarkup("App.xaml");
            XElement resources = app.Root.Element(Presentation + "Application.Resources")
                .Element(Presentation + "ResourceDictionary");
            Assert.Null(resources.Attribute("Source"));
            Assert.Equal(
                new[] { "Looks/NewLook.xaml" },
                resources.Element(Presentation + "ResourceDictionary.MergedDictionaries").Elements()
                    .Select(dictionary => (string)dictionary.Attribute("Source")));
        }
    }
}
