// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XMAT.NetworkTrace;

namespace XMAT
{
    public partial class BlockListView : UserControl
    {
        public BlockListView()
        {
            InitializeComponent();
            DataContext = NetworkUrlBlocker.Instance;
        }

        private void AddUrlBlock_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(NewUrlTextBox.Text))
            {
                NetworkUrlBlocker.Instance.BlockUrl(NewUrlTextBox.Text);
                NewUrlTextBox.Clear();
            }
        }

        private void RemoveUrlBlock_Click(object sender, RoutedEventArgs e)
        {
            if (BlockedUrlsListBox.SelectedItem is string selectedUrl)
            {
                NetworkUrlBlocker.Instance.UnblockUrl(selectedUrl);
            }
        }

        private void ClearUrlBlocks_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Are you sure you want to clear all blocked URLs?",
                                "Confirm", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                NetworkUrlBlocker.Instance.ClearAllBlockedUrls();
            }
        }

        private void NewUrlTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(NewUrlTextBox.Text))
            {
                NetworkUrlBlocker.Instance.BlockUrl(NewUrlTextBox.Text);
                NewUrlTextBox.Clear();
                e.Handled = true;
            }
        }
    }
}