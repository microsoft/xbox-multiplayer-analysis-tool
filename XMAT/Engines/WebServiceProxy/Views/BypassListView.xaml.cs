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

        public BypassListView()
        {
            InitializeComponent();
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

            string text = NewBypassTextBox.Text;
            if (BypassList.Add(text))
            {
                NewBypassTextBox.Clear();
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
