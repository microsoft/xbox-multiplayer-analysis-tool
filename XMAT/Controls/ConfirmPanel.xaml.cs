// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace XMAT
{
    // A confirmation's content: Heading asks the question, such as "Delete 12 sessions?", Message says what the action
    // does, Details lists the counts that matter, and ConfirmText labels the action's button, such as "Delete 12
    // sessions". IsDestructive makes that button red, for an action that can't be undone. Cancel is the default answer,
    // so Enter and Esc both cancel. Answered says which button was chosen.
    public partial class ConfirmPanel : UserControl
    {
        private bool _isDestructive;

        public ConfirmPanel()
        {
            InitializeComponent();
            CancelButton.Content = Localization.GetLocalizedString("CANCEL");
        }

        // Raised with true when the action's button is chosen.
        internal event EventHandler<bool> Answered;

        public string Heading
        {
            get => HeadingText.Text;
            set => HeadingText.Text = value;
        }

        public string Message
        {
            get => MessageText.Text;
            set => MessageText.Text = value;
        }

        public IReadOnlyList<string> Details
        {
            get => DetailList.ItemsSource as IReadOnlyList<string>;
            set
            {
                DetailList.ItemsSource = value;
                DetailList.Visibility = value is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public string ConfirmText
        {
            get => ConfirmButton.Content as string;
            set => ConfirmButton.Content = value;
        }

        public bool IsDestructive
        {
            get => _isDestructive;
            set
            {
                _isDestructive = value;
                if (value)
                {
                    ConfirmButton.SetResourceReference(StyleProperty, "DangerButtonStyle");
                }
                else
                {
                    ConfirmButton.ClearValue(StyleProperty);
                }
            }
        }

        private void Confirm_Click(object sender, RoutedEventArgs e) => Answered?.Invoke(this, true);
    }
}
