// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using XMAT.Models;

namespace XMAT
{
    // Shows what changed in XMAT's UI, a page at a time: a picture of each change, what it does and where to find it.
    // The topics on the left list every page; Back, Next, the arrow keys and Page Up and Page Down turn pages. New
    // features and fixes are in the release notes, which the link above the topics opens.
    public partial class WhatsNewWindow : Window
    {
        // The pages in order. Each has WHATS_NEW_<ID>_TITLE, _BODY, _WHERE and _IMAGE strings, the last describing
        // the picture for screen readers, and a picture for each theme in Resources/WhatsNew.
        internal static readonly string[] PageIds =
        {
            "new-look", "colors", "tabs", "search", "filters", "status-rows", "overview", "column-summaries", "volume",
            "edit-and-resend", "delete", "resizing", "comfort-view", "context-menus", "accessibility",
        };

        // What's new opens by itself the first time XMAT starts with an edition it hasn't opened. Raise the edition
        // when the pages change, so people see the new ones once.
        internal const int Edition = 2;

        internal static readonly Uri ReleaseNotesUri = new("https://github.com/microsoft/xbox-multiplayer-analysis-tool/releases");

        private readonly IReadOnlyList<WhatsNewPage> _pages = PageIds.Select(WhatsNewPage.Create).ToList();

        public WhatsNewWindow()
        {
            InitializeComponent();
            Title = Localization.GetLocalizedString("WHATS_NEW_WINDOW_TITLE");
            Heading.Text = Localization.GetLocalizedString("WHATS_NEW_HEADING");
            // The sentence has the link where it has {0}.
            const char Link = '\u0001';
            string[] subheading = Localization.GetLocalizedString("WHATS_NEW_SUBHEADING", Link).Split(Link);
            SubheadingStart.Text = subheading[0];
            ReleaseNotesText.Text = Localization.GetLocalizedString("WHATS_NEW_RELEASE_NOTES");
            SubheadingEnd.Text = subheading.Length > 1 ? subheading[1] : string.Empty;
            ReleaseNotesLink.ToolTip = ReleaseNotesUri.AbsoluteUri;
            AutomationProperties.SetHelpText(ReleaseNotesLink, Localization.GetLocalizedString("WHATS_NEW_RELEASE_NOTES_HELP"));
            AutomationProperties.SetName(Topics, Localization.GetLocalizedString("WHATS_NEW_TOPICS"));
            BackButton.Content = Localization.GetLocalizedString("WHATS_NEW_BACK");
            foreach (WhatsNewPage _ in _pages)
            {
                PageDots.Children.Add(new Border { Height = 6, Margin = new Thickness(0, 0, 4, 0), CornerRadius = new CornerRadius(3) });
            }

            Topics.ItemsSource = _pages;
            Topics.SelectedIndex = 0;
            ThemeManager.ThemeChanged += ThemeChanged;
            Closed += (_, _) => ThemeManager.ThemeChanged -= ThemeChanged;
            Loaded += (_, _) => NextButton.Focus();
        }

        internal IReadOnlyList<WhatsNewPage> Pages => _pages;

        internal int PageIndex => Topics.SelectedIndex;

        // Opens a link in the browser. Tests replace it, so they open none.
        internal Action<Uri> OpenLink { get; set; } = OpenInBrowser;

        // Whether What's new opens by itself at this start: it hasn't yet for this edition.
        internal static bool OpensAtStartup(AppPreferencesModel preferences) => preferences.WhatsNewSeen < Edition;

        // The picture of each page in a theme: Light pictures in the Light theme and high contrast, Dark ones in Dark.
        internal static Uri ImageUri(string pageId, string theme) =>
            new($"pack://application:,,,/XboxMultiplayerAnalysisTool;component/Resources/WhatsNew/{pageId}-{(theme == "Dark" ? "dark" : "light")}.png");

        internal void ShowPage(int index) => Topics.SelectedIndex = Math.Clamp(index, 0, _pages.Count - 1);

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);
            switch (e.Key)
            {
                case Key.Escape:
                    Close();
                    e.Handled = true;
                    break;
                case Key.Left or Key.PageUp:
                    ShowPage(PageIndex - 1);
                    e.Handled = true;
                    break;
                case Key.Right or Key.PageDown:
                    ShowPage(PageIndex + 1);
                    e.Handled = true;
                    break;
            }
        }

        private void Topics_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Topics.SelectedItem is not WhatsNewPage page)
            {
                return;
            }

            int index = PageIndex;
            Topics.ScrollIntoView(page);
            PageTitle.Text = page.Title;
            PageBody.Text = page.Body;
            PageWhere.Text = Localization.GetLocalizedString("WHATS_NEW_WHERE", page.Where);
            AutomationProperties.SetName(PageImage, page.ImageDescription);
            ShowImage();
            for (int dot = 0; dot < PageDots.Children.Count; dot++)
            {
                var border = (Border)PageDots.Children[dot];
                border.Width = dot == index ? 16 : 6;
                border.SetResourceReference(Border.BackgroundProperty, dot == index ? "AccentFillBrush" : "BorderSubtleBrush");
            }

            Counter.Text = Localization.GetLocalizedString("WHATS_NEW_COUNTER", index + 1, _pages.Count);
            AutomationProperties.SetName(
                Counter, Localization.GetLocalizedString("WHATS_NEW_PAGE_NAME", index + 1, _pages.Count, page.Title));
            BackButton.IsEnabled = index > 0;
            NextButton.Content = Localization.GetLocalizedString(index == _pages.Count - 1 ? "WHATS_NEW_DONE" : "WHATS_NEW_NEXT");
        }

        private void ShowImage()
        {
            if (Topics.SelectedItem is WhatsNewPage page)
            {
                PageImage.Source = new BitmapImage(ImageUri(page.Id, ThemeManager.CurrentTheme));
            }
        }

        private void ThemeChanged(object sender, EventArgs e) => ShowImage();

        private void ReleaseNotes_Click(object sender, RoutedEventArgs e) => OpenLink(ReleaseNotesUri);

        private static void OpenInBrowser(Uri uri)
        {
            try
            {
                using Process browser = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                PublicUtilities.AppLog(LogLevel.ERROR, $"Could not open {uri} in the browser: {ex.Message}");
            }
        }

        private void Back_Click(object sender, RoutedEventArgs e) => ShowPage(PageIndex - 1);

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            if (PageIndex == _pages.Count - 1)
            {
                Close();
            }
            else
            {
                ShowPage(PageIndex + 1);
            }
        }
    }

    internal sealed record WhatsNewPage(string Id, string Title, string Body, string Where, string ImageDescription)
    {
        internal static WhatsNewPage Create(string id)
        {
            string key = "WHATS_NEW_" + id.Replace('-', '_').ToUpperInvariant();
            return new WhatsNewPage(
                id,
                Localization.GetLocalizedString(key + "_TITLE"),
                Localization.GetLocalizedString(key + "_BODY"),
                Localization.GetLocalizedString(key + "_WHERE"),
                Localization.GetLocalizedString(key + "_IMAGE"));
        }

        public override string ToString() => Title;
    }
}
