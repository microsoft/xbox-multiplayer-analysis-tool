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
    public partial class BlockListView : UserControl
    {
        public static readonly DependencyProperty BlockListProperty =
            DependencyProperty.Register(nameof(BlockList), typeof(BlockListModel), typeof(BlockListView),
                new PropertyMetadata(null));

        public BlockListModel BlockList
        {
            get => (BlockListModel)GetValue(BlockListProperty);
            set => SetValue(BlockListProperty, value);
        }

        public bool ShowScopeNote
        {
            get => ScopeText.Visibility == Visibility.Visible;
            set => ScopeText.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        public BlockListView()
        {
            InitializeComponent();
            PatternHelpText.Text = Localization.GetLocalizedString("HOST_LIST_PATTERN_HELP");
            ScopeText.Text = Localization.GetLocalizedString("HOST_LISTS_SCOPE");
        }

        private void AddUrlBlock_Click(object sender, RoutedEventArgs e)
        {
            AddPattern();
        }

        private void NewBlockTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                AddPattern();
                e.Handled = true;
            }
        }

        private void AddPattern()
        {
            if (BlockList == null)
                return;

            // Invalid text (such as a standalone host:port) stays in the box with the reason.
            if (!HostListEntry.TryNormalize(NewBlockTextBox.Text, out string entry, out string error))
            {
                EntryErrorText.Text = error ?? string.Empty;
                return;
            }

            if (BlockList.Add(entry))
            {
                NewBlockTextBox.Clear();
                EntryErrorText.Text = string.Empty;
            }
            else
            {
                EntryErrorText.Text = Localization.GetLocalizedString("HOST_LIST_ALREADY_LISTED", entry);
            }
        }

        private void RemoveSelected_Click(object sender, RoutedEventArgs e)
        {
            if (BlockList == null)
                return;

            var selectedItems = new System.Collections.Generic.List<object>();
            foreach (var item in BlockedUrlList.SelectedItems)
            {
                selectedItems.Add(item);
            }
            foreach (var s in selectedItems.OfType<string>())
            {
                BlockList.Remove(s);
            }
        }

        private void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            BlockList?.Clear();
        }
    }
}
