// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

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

        public BlockListView()
        {
            InitializeComponent();
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

            string text = NewBlockTextBox.Text;
            if (BlockList.Add(text))
            {
                NewBlockTextBox.Clear();
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
            foreach (var item in selectedItems)
            {
                if (item is string s)
                {
                    BlockList.Remove(s);
                }
            }
        }

        private void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            BlockList?.Clear();
        }
    }
}
