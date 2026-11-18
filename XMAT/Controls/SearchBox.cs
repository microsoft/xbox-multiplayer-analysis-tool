// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace XMAT
{
    // A text box for finding things in a list: a search icon at its start, a hint while it's empty, and at its end a
    // summary, such as "12 of 340", or a problem with what was typed, and a button that clears it. Escape clears it too.
    // A problem replaces the summary and is announced to screen readers; the summary, which can change every second, is
    // not. Styles/Inputs.xaml draws it in the new look.
    [TemplatePart(Name = "PART_Clear", Type = typeof(ButtonBase))]
    public class SearchBox : TextBox
    {
        public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
            nameof(Hint), typeof(string), typeof(SearchBox));

        public static readonly DependencyProperty SummaryProperty = DependencyProperty.Register(
            nameof(Summary), typeof(string), typeof(SearchBox));

        public static readonly DependencyProperty ProblemProperty = DependencyProperty.Register(
            nameof(Problem), typeof(string), typeof(SearchBox));

        private ButtonBase _clear;

        public static string ClearText => Localization.GetLocalizedString("SEARCH_BOX_CLEAR");

        public string Hint
        {
            get => (string)GetValue(HintProperty);
            set => SetValue(HintProperty, value);
        }

        public string Summary
        {
            get => (string)GetValue(SummaryProperty);
            set => SetValue(SummaryProperty, value);
        }

        public string Problem
        {
            get => (string)GetValue(ProblemProperty);
            set => SetValue(ProblemProperty, value);
        }

        public override void OnApplyTemplate()
        {
            if (_clear != null)
            {
                _clear.Click -= Clear_Click;
            }

            base.OnApplyTemplate();
            _clear = GetTemplateChild("PART_Clear") as ButtonBase;
            if (_clear != null)
            {
                _clear.Click += Clear_Click;
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape && !string.IsNullOrEmpty(Text))
            {
                Clear();
                e.Handled = true;
                return;
            }

            base.OnKeyDown(e);
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            Clear();
            Focus();
        }
    }
}
