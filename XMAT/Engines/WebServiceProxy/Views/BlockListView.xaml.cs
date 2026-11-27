// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
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

        // The new look's labels for controls that both looks show; the original look's are in the XAML.
        public static string AddButtonText => Localization.GetLocalizedString("HOST_LIST_ADD");
        public static string ListHeadingText => Localization.GetLocalizedString("HOST_LIST_BLOCKED_HEADING");
        public static string RemoveButtonText => Localization.GetLocalizedString("HOST_LIST_REMOVE_SELECTED");
        public static string ClearButtonText => Localization.GetLocalizedString("HOST_LIST_CLEAR_ALL");

        // The original look's scope note, and the scope sentences of the new look's description, follow this setting.
        // The Host Lists window turns them off, since its intro states the scope.
        public bool ShowScopeNote
        {
            get => ScopeText.Visibility == Visibility.Visible;
            set
            {
                ScopeText.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
                DescriptionText.Text = Localization.GetLocalizedString(
                    value ? "HOST_LIST_BLOCK_DESCRIPTION" : "HOST_LIST_BLOCK_SUMMARY");
            }
        }

        public BlockListView()
        {
            InitializeComponent();
            PatternHelpText.Text = Localization.GetLocalizedString("HOST_LIST_PATTERN_HELP");
            ScopeText.Text = Localization.GetLocalizedString("HOST_LISTS_SCOPE");
            System.Windows.Automation.AutomationProperties.SetName(NewBlockTextBox, Localization.GetLocalizedString("A11Y_BLOCK_ENTRY_NAME"));

            DescriptionText.Text = Localization.GetLocalizedString("HOST_LIST_BLOCK_DESCRIPTION");
            EntryHintText.Text = Localization.GetLocalizedString("HOST_LIST_ENTRY_HINT");
            EntryExampleText.Text = Localization.GetLocalizedString("HOST_LIST_ENTRY_EXAMPLES");
            System.Windows.Automation.AutomationProperties.SetHelpText(NewBlockTextBox, EntryExampleText.Text);
            HostExampleText.Text = Localization.GetLocalizedString("HOST_LIST_BLOCK_EXAMPLE_HOST");
            SubdomainsExampleText.Text = Localization.GetLocalizedString("HOST_LIST_BLOCK_EXAMPLE_SUBDOMAINS");
            AddressesExampleText.Text = Localization.GetLocalizedString("HOST_LIST_BLOCK_EXAMPLE_ADDRESSES");
            MatchingRules.Header = Localization.GetLocalizedString("HOST_LIST_MATCHING");
            HostListText.ShowWithCode(HostRuleText, Localization.GetLocalizedString("HOST_LIST_RULE_HOST"));
            HostListText.ShowWithCode(PortRuleText, Localization.GetLocalizedString("HOST_LIST_RULE_PORT"));
            HostListText.ShowWithCode(PatternRuleText, Localization.GetLocalizedString("HOST_LIST_RULE_PATTERN"));
            HostListText.ShowWithCode(LiteralRuleText, Localization.GetLocalizedString("HOST_LIST_RULE_LITERAL"));
            HostListText.ShowWithCode(Ipv6RuleText, Localization.GetLocalizedString("HOST_LIST_RULE_IPV6"));
            BlockedEmptyText.Text = Localization.GetLocalizedString("HOST_LIST_BLOCK_EMPTY");
            ((INotifyCollectionChanged)BlockedUrlList.Items).CollectionChanged +=
                (_, _) => BlockedCountText.Text = HostListText.Count(BlockedUrlList.Items.Count);
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

            bool hadFocus = RemoveSelectedButton.IsKeyboardFocused;
            var selectedItems = new System.Collections.Generic.List<object>();
            foreach (var item in BlockedUrlList.SelectedItems)
            {
                selectedItems.Add(item);
            }
            foreach (var s in selectedItems.OfType<string>())
            {
                BlockList.Remove(s);
            }
            HostListText.KeepKeyboardFocus(RemoveSelectedButton, hadFocus, BlockedUrlList, NewBlockTextBox);
        }

        private void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            bool hadFocus = ClearAllButton.IsKeyboardFocused;
            BlockList?.Clear();
            HostListText.KeepKeyboardFocus(ClearAllButton, hadFocus, BlockedUrlList, NewBlockTextBox);
        }
    }

    // Layout for the new look's Block and Bypass panels: the widths at which they change their layout, which a panel's
    // XAML tests on its own width with these converters, and the alignment of the count beside the list's heading.
    public static class HostListLayout
    {
        // From this width the list has a column of its own, beside the heading, the entry field and the help.
        public const double TwoColumnWidth = 560;

        // At this width and narrower, the examples show each description below its pattern.
        public const double NarrowWidth = 320;

        public static IValueConverter IsTwoColumn { get; } = new WidthTest(width => width >= TwoColumnWidth);

        public static IValueConverter IsNarrow { get; } = new WidthTest(width => width <= NarrowWidth);

        // The top margin that puts the first baseline of a line of smaller text on the baseline of larger text beside it,
        // when both start at the same top: from the larger text's font family and size, then the smaller text's.
        public static IMultiValueConverter BaselineMargin { get; } = new BaselineMarginConverter();

        private sealed class WidthTest(Func<double, bool> test) : IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
                value is double width && test(width);

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
                throw new NotSupportedException();
        }

        private sealed class BaselineMarginConverter : IMultiValueConverter
        {
            public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
                values is [FontFamily largerFamily, double largerSize, FontFamily family, double size]
                    ? new Thickness(0, Math.Max(0, largerFamily.Baseline * largerSize - family.Baseline * size), 0, 0)
                    : new Thickness();

            public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
                throw new NotSupportedException();
        }
    }

    // Text that the Block and Bypass list views show in the new look.
    internal static class HostListText
    {
        // Shows TEXT in TARGET, with each part between backticks, such as `*.example.com`, in the InlineCodeStyle that
        // TARGET's resources define.
        internal static void ShowWithCode(TextBlock target, string text)
        {
            target.Inlines.Clear();
            string[] parts = text.Split('`');
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0)
                {
                    continue;
                }

                var run = new Run(parts[i]);
                if (i % 2 == 1)
                {
                    run.SetResourceReference(FrameworkContentElement.StyleProperty, "InlineCodeStyle");
                }
                target.Inlines.Add(run);
            }
        }

        // The number of entries in a list, or nothing for an empty list, whose well says that it is empty.
        internal static string Count(int count) => count switch
        {
            0 => string.Empty,
            1 => Localization.GetLocalizedString("HOST_LIST_COUNT_ONE"),
            _ => Localization.GetLocalizedString("HOST_LIST_COUNT", count),
        };

        // The new look turns off a list action once it has nothing left to do, such as Remove selected after the
        // selection is removed. Keyboard focus on it would then be lost, so it moves to the list or, once the list is
        // empty, to the entry field.
        internal static void KeepKeyboardFocus(UIElement action, bool hadFocus, ListBox list, TextBox entry)
        {
            if (hadFocus && !action.IsEnabled)
            {
                if (list.HasItems)
                {
                    list.Focus();
                }
                else
                {
                    entry.Focus();
                }
            }
        }
    }
}
