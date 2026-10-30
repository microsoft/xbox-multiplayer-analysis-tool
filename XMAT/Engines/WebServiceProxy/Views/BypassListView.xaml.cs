// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XMAT.WebServiceCapture.Models;

namespace XMAT.WebServiceCapture
{
    public partial class BypassListView : UserControl
    {
        public static readonly DependencyProperty BypassListProperty =
            DependencyProperty.Register(nameof(BypassList), typeof(BypassListModel), typeof(BypassListView),
                new PropertyMetadata(null));

        public BypassListModel BypassList
        {
            get => (BypassListModel)GetValue(BypassListProperty);
            set => SetValue(BypassListProperty, value);
        }

        public bool ShowScopeNote
        {
            get => ScopeText.Visibility == Visibility.Visible;
            set => ScopeText.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        public BypassListView()
        {
            InitializeComponent();
            BypassTimingText.Text = Localization.GetLocalizedString("HOST_LIST_BYPASS_TIMING");
            PatternHelpText.Text = Localization.GetLocalizedString("HOST_LIST_PATTERN_HELP");
            ScopeText.Text = Localization.GetLocalizedString("HOST_LISTS_SCOPE");
        }

        private void AddBypass_Click(object sender, RoutedEventArgs e)
        {
            AddPattern();
        }

        private void NewBypassTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                AddPattern();
                e.Handled = true;
            }
        }

        private void AddPattern()
        {
            if (BypassList == null)
                return;

            // Invalid text (such as a standalone host:port) stays in the box with the reason.
            if (!HostListEntry.TryNormalize(NewBypassTextBox.Text, out string entry, out string error))
            {
                EntryErrorText.Text = error ?? string.Empty;
                return;
            }

            if (BypassList.Add(entry))
            {
                NewBypassTextBox.Clear();
                EntryErrorText.Text = string.Empty;
            }
            else
            {
                EntryErrorText.Text = Localization.GetLocalizedString("HOST_LIST_ALREADY_LISTED", entry);
            }
        }

        private void RemoveSelected_Click(object sender, RoutedEventArgs e)
        {
            if (BypassList == null)
                return;

            var selectedItems = new System.Collections.Generic.List<object>();
            foreach (var item in BypassedUrlList.SelectedItems)
            {
                selectedItems.Add(item);
            }
            foreach (var s in selectedItems.OfType<string>())
            {
                BypassList.Remove(s);
            }
        }

        private void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            BypassList?.Clear();
        }
    }
}
