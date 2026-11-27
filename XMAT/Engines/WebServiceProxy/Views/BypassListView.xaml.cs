// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Specialized;
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

        // The new look's labels for controls that both looks show; the original look's are in the XAML.
        public static string AddButtonText => Localization.GetLocalizedString("HOST_LIST_ADD");
        public static string ListHeadingText => Localization.GetLocalizedString("HOST_LIST_BYPASSED_HEADING");
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
                    value ? "HOST_LIST_BYPASS_DESCRIPTION" : "HOST_LIST_BYPASS_SUMMARY");
            }
        }

        public BypassListView()
        {
            InitializeComponent();
            BypassTimingText.Text = Localization.GetLocalizedString("HOST_LIST_BYPASS_TIMING");
            PatternHelpText.Text = Localization.GetLocalizedString("HOST_LIST_PATTERN_HELP");
            ScopeText.Text = Localization.GetLocalizedString("HOST_LISTS_SCOPE");
            System.Windows.Automation.AutomationProperties.SetName(NewBypassTextBox, Localization.GetLocalizedString("A11Y_BYPASS_ENTRY_NAME"));

            // In the new look the description says when bypass takes effect, as the original look's timing note does.
            DescriptionText.Text = Localization.GetLocalizedString("HOST_LIST_BYPASS_DESCRIPTION");
            EntryHintText.Text = Localization.GetLocalizedString("HOST_LIST_ENTRY_HINT");
            EntryExampleText.Text = Localization.GetLocalizedString("HOST_LIST_ENTRY_EXAMPLES");
            System.Windows.Automation.AutomationProperties.SetHelpText(NewBypassTextBox, EntryExampleText.Text);
            XstsExampleText.Text = Localization.GetLocalizedString("HOST_LIST_BYPASS_EXAMPLE_XSTS");
            AuthExampleText.Text = Localization.GetLocalizedString("HOST_LIST_BYPASS_EXAMPLE_AUTH");
            LoginExampleText.Text = Localization.GetLocalizedString("HOST_LIST_BYPASS_EXAMPLE_LOGIN");
            MatchingRules.Header = Localization.GetLocalizedString("HOST_LIST_MATCHING");
            HostListText.ShowWithCode(HostRuleText, Localization.GetLocalizedString("HOST_LIST_RULE_HOST"));
            HostListText.ShowWithCode(PortRuleText, Localization.GetLocalizedString("HOST_LIST_RULE_PORT"));
            HostListText.ShowWithCode(PatternRuleText, Localization.GetLocalizedString("HOST_LIST_RULE_PATTERN"));
            HostListText.ShowWithCode(LiteralRuleText, Localization.GetLocalizedString("HOST_LIST_RULE_LITERAL"));
            HostListText.ShowWithCode(Ipv6RuleText, Localization.GetLocalizedString("HOST_LIST_RULE_IPV6"));
            BypassedEmptyText.Text = Localization.GetLocalizedString("HOST_LIST_BYPASS_EMPTY");
            ((INotifyCollectionChanged)BypassedUrlList.Items).CollectionChanged +=
                (_, _) => BypassedCountText.Text = HostListText.Count(BypassedUrlList.Items.Count);
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

            bool hadFocus = RemoveSelectedButton.IsKeyboardFocused;
            var selectedItems = new System.Collections.Generic.List<object>();
            foreach (var item in BypassedUrlList.SelectedItems)
            {
                selectedItems.Add(item);
            }
            foreach (var s in selectedItems.OfType<string>())
            {
                BypassList.Remove(s);
            }
            HostListText.KeepKeyboardFocus(RemoveSelectedButton, hadFocus, BypassedUrlList, NewBypassTextBox);
        }

        private void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            bool hadFocus = ClearAllButton.IsKeyboardFocused;
            BypassList?.Clear();
            HostListText.KeepKeyboardFocus(ClearAllButton, hadFocus, BypassedUrlList, NewBypassTextBox);
        }
    }
}
