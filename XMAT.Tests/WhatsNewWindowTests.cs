// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class WhatsNewWindowTests
    {
        private readonly CaptureUiApplicationFixture _application;

        public WhatsNewWindowTests(CaptureUiApplicationFixture application) => _application = application;

        // A page needs its title, description, place and picture description, and a picture for each theme.
        [Fact]
        public void EveryPage_HasItsTextAndAPictureForEachTheme()
        {
            _application.Run(_ =>
            {
                Assert.Equal(WhatsNewWindow.PageIds.Length, WhatsNewWindow.PageIds.Distinct().Count());
                foreach (string id in WhatsNewWindow.PageIds)
                {
                    WhatsNewPage page = WhatsNewPage.Create(id);
                    Assert.All(new[] { page.Title, page.Body, page.Where, page.ImageDescription }, text =>
                    {
                        Assert.False(string.IsNullOrWhiteSpace(text), $"{id} is missing text.");
                        Assert.DoesNotContain("NO_IMPL_", text, StringComparison.Ordinal);
                    });
                    foreach (string theme in new[] { "Light", "Dark" })
                    {
                        Uri picture = WhatsNewWindow.ImageUri(id, theme);
                        using var stream = Application.GetResourceStream(picture)?.Stream;
                        Assert.True(stream != null, $"{picture} is missing.");
                    }
                }
            });
        }

        [Fact]
        public void Pages_TurnWithBackNextTheArrowKeysAndTheTopics_AndNextBecomesDoneOnTheLastPage()
        {
            _application.Run(_ =>
            {
                var window = new WhatsNewWindow { Left = -12000, Top = -12000, ShowActivated = false };
                bool closed = false;
                window.Closed += (_, _) => closed = true;
                window.Show();
                try
                {
                    WpfTestHost.Pump();
                    int count = WhatsNewWindow.PageIds.Length;
                    Assert.Equal(0, window.PageIndex);
                    Assert.False(window.BackButton.IsEnabled);
                    Assert.Equal($"1 of {count}", window.Counter.Text);
                    Assert.Equal($"Page 1 of {count}: The new look", AutomationProperties.GetName(window.Counter));
                    Assert.Equal(window.Pages[0].ImageDescription, AutomationProperties.GetName(window.PageImage));
                    Assert.NotNull(window.PageImage.Source);

                    window.NextButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Equal(1, window.PageIndex);
                    Assert.True(window.BackButton.IsEnabled);
                    Press(window, Key.Right);
                    Assert.Equal(2, window.PageIndex);
                    Press(window, Key.PageUp);
                    Assert.Equal(1, window.PageIndex);
                    window.BackButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Equal(0, window.PageIndex);
                    Press(window, Key.Left);
                    Assert.Equal(0, window.PageIndex);

                    window.Topics.SelectedIndex = count - 1;
                    WpfTestHost.Pump();
                    Assert.Equal(window.Pages[count - 1].Title, window.PageTitle.Text);
                    Assert.Equal(Localization.GetLocalizedString("WHATS_NEW_DONE"), window.NextButton.Content);
                    Press(window, Key.Right);
                    Assert.Equal(count - 1, window.PageIndex);
                    Assert.False(closed);
                    window.NextButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.True(closed);
                }
                finally
                {
                    if (!closed)
                    {
                        window.Close();
                    }
                }
            });
        }

        [Fact]
        public void ThePicture_FollowsTheTheme()
        {
            _application.Run(_ =>
            {
                string previous = ThemeManager.CurrentTheme;
                var window = new WhatsNewWindow { Left = -12000, Top = -12000, ShowActivated = false };
                window.Show();
                try
                {
                    foreach (string theme in new[] { "Dark", "Light" })
                    {
                        ThemeManager.PrepareTheme(new ResourceDictionary(), theme, newLook: true, highContrast: false);
                        ThemeManager.RaiseThemeChanged();
                        WpfTestHost.Pump();
                        Assert.Equal(WhatsNewWindow.ImageUri("new-look", theme), ((BitmapImage)window.PageImage.Source).UriSource);
                    }
                }
                finally
                {
                    window.Close();
                    ThemeManager.PrepareTheme(new ResourceDictionary(), previous, newLook: true, highContrast: false);
                }
            });
        }

        private static void Press(Window window, Key key)
        {
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, key)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            });
            WpfTestHost.Pump();
        }
    }
}
