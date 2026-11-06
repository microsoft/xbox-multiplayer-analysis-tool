// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using XMAT.Operations.Replay;

namespace XMAT.WebServiceCapture
{
    // What saving a request asks about: Pending, the parts that its request file left out and that are not supplied yet,
    // which only a template can keep; Recognized, what XMAT recognizes as sensitive in the content, by name only; and
    // ChangedOrigin, the URL's origin when it is not the one that the request was captured or loaded for, else null.
    internal sealed record RequestSaveQuestion(
        RequestOmissions Pending, RequestFileRecognized Recognized, string ChangedOrigin = null);

    // How to save a request: a template, or with its captured content; OriginChangeAllowed only when the user allowed that
    // content to be saved for a changed origin.
    internal sealed record RequestSaveChoice(RequestFileProfile Profile, bool OriginChangeAllowed = false);

    /// <summary>
    /// Asks how to save one request, every time: as a template, which keeps only its shape, unless the user includes its
    /// captured content for this save. Including it is never the default and never remembered. The window lists what XMAT
    /// recognizes as sensitive in it, by name, and a URL that goes to another origin needs its own permission.
    /// </summary>
    public partial class SaveRequestWindow : Window
    {
        // The window lists at most this many recognized names, each cut to this many characters as shown (UntrustedText),
        // and an origin of at most this many.
        internal const int MaxShownNames = 20;
        internal const int MaxShownNameChars = 64;
        private const int MaxShownOriginChars = 300;

        private readonly RequestSaveQuestion _question;

        internal SaveRequestWindow(RequestSaveQuestion question)
        {
            ArgumentNullException.ThrowIfNull(question);
            _question = question;
            InitializeComponent();
            Title = Localization.GetLocalizedString("SAVE_REQUEST_TITLE");
            TemplateText.Text = Localization.GetLocalizedString("SAVE_REQUEST_TEMPLATE");
            IncludeContentBox.Content = Localization.GetLocalizedString("SAVE_REQUEST_INCLUDE_CONTENT");
            ContentText.Text = Localization.GetLocalizedString("SAVE_REQUEST_CONTENT");
            ShowRecognized(question.Recognized);
            AllowOriginBox.Content = Localization.GetLocalizedString("SAVE_REQUEST_ALLOW_ORIGIN");
            SaveButton.Content = Localization.GetLocalizedString("SAVE_REQUEST_SAVE");
            CancelButton.Content = Localization.GetLocalizedString("SAVE_REQUEST_CANCEL");

            // A request that still leaves out a part can be saved only as a template.
            if (question.Pending.Any)
            {
                PendingText.Text = Localization.GetLocalizedString(
                    "SAVE_REQUEST_PENDING", RequestComposerWindow.PendingParts(question.Pending));
                PendingText.Visibility = Visibility.Visible;
                IncludeContentBox.IsEnabled = false;
            }

            if (question.ChangedOrigin != null)
            {
                OriginText.Text = Localization.GetLocalizedString(
                    "SAVE_REQUEST_ORIGIN", UntrustedText.Show(question.ChangedOrigin, MaxShownOriginChars));
                OriginPanel.Visibility = Visibility.Visible;
            }

            IncludeContentBox.IsChecked = false;
            Update();
        }

        // What Save saves as the window shows it now: a template unless the content is included; null while the content is
        // included without what it needs, every part supplied and, for a changed origin, its own permission.
        internal RequestSaveChoice Choice
        {
            get
            {
                if (IncludeContentBox.IsChecked != true)
                    return new RequestSaveChoice(RequestFileProfile.Template);
                if (_question.Pending.Any)
                    return null;
                if (_question.ChangedOrigin == null)
                    return new RequestSaveChoice(RequestFileProfile.Content);
                return AllowOriginBox.IsChecked == true
                    ? new RequestSaveChoice(RequestFileProfile.Content, OriginChangeAllowed: true)
                    : null;
            }
        }

        // How the user chose to save the request, or null when they cancelled. Every call asks again, from a template.
        internal static RequestSaveChoice Ask(Window owner, RequestSaveQuestion question)
        {
            var dialog = new SaveRequestWindow(question) { Owner = owner };
            return dialog.ShowDialog() == true ? dialog.Choice : null;
        }

        // The permission for another origin goes with the content: leaving the content out takes it back.
        private void Update()
        {
            bool content = IncludeContentBox.IsChecked == true;
            if (!content)
                AllowOriginBox.IsChecked = false;
            AllowOriginBox.IsEnabled = content;
            SaveButton.IsEnabled = Choice != null;
        }

        // The names that XMAT recognizes as sensitive, each once and never with its value, one per line: a header's name or
        // a query parameter's, shown escaped and cut (UntrustedText), since a parameter's name is whatever its URL decodes
        // to. At most MaxShownNames of them, in an area that scrolls, then how many more there are; and the size of a body,
        // which XMAT does not inspect.
        private void ShowRecognized(RequestFileRecognized recognized)
        {
            List<(string Key, string Name)> names = (recognized?.HeaderNames ?? Array.Empty<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name => (Key: "SAVE_REQUEST_NAME_HEADER", Name: name))
                .Concat((recognized?.QueryParameterNames ?? Array.Empty<string>())
                    .Distinct(StringComparer.Ordinal)
                    .Select(name => (Key: "SAVE_REQUEST_NAME_PARAMETER", Name: name)))
                .ToList();
            NamesList.ItemsSource = names
                .Take(MaxShownNames)
                .Select(entry =>
                    Localization.GetLocalizedString(entry.Key, UntrustedText.Show(entry.Name, MaxShownNameChars)))
                .ToList();
            NamesScroll.Visibility = names.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            RecognizedText.Text = Localization.GetLocalizedString(
                names.Count == 0 ? "SAVE_REQUEST_SENSITIVE_NONE" : "SAVE_REQUEST_SENSITIVE_NAMES");
            if (names.Count > MaxShownNames)
            {
                MoreNamesText.Text =
                    Localization.GetLocalizedString("SAVE_REQUEST_SENSITIVE_MORE", names.Count - MaxShownNames);
                MoreNamesText.Visibility = Visibility.Visible;
            }

            if (recognized?.BodyBytesNotInspected > 0)
            {
                BodyNotInspectedText.Text = Localization.GetLocalizedString(
                    "SAVE_REQUEST_BODY_NOT_INSPECTED", recognized.BodyBytesNotInspected);
                BodyNotInspectedText.Visibility = Visibility.Visible;
            }
        }

        private void Choice_Changed(object sender, RoutedEventArgs e) => Update();

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (Choice != null)
                DialogResult = true;
        }
    }
}
