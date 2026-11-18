// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace XMAT
{
    /// <summary>
    /// Interaction logic for CheckBoxFilters.xaml
    /// </summary>
    // In the original look, a check box beside the list turns the filter on, and the list's checked values show; every
    // value starts checked. In the new look the list is the filter: only the values picked show, and with none picked the
    // filter is off and All is picked. Both read the same state, IsFilterEnabled and each value's IsChecked, so a value
    // shows as picked while the filter is on and the value is checked, and switching looks keeps the filter.
    public partial class CheckBoxFilters : UserControl
    {
        private const int AllNoneIndex = 0;
        internal const string AllNone = "*";

        private bool _allNoneProcessing = false;
        private bool _ignoreIsCheckedEvent = false;

        public static readonly RoutedEvent FiltersChangedEvent = EventManager.RegisterRoutedEvent(
            "FiltersChanged", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(CheckBoxFilters));

        public event RoutedEventHandler FiltersChanged
        {
            add { AddHandler(FiltersChangedEvent, value); }
            remove { RemoveHandler(FiltersChangedEvent, value); }
        }
        void RaiseFiltersChangedEvent()
        {
            UpdateSummary();
            RaiseEvent(new RoutedEventArgs(FiltersChangedEvent));
        }

        public string FilterDesc
        {
            get { return (string)this.GetValue(FilterDescProperty); }
            set { this.SetValue(FilterDescProperty, value); }
        }
        public static readonly DependencyProperty FilterDescProperty =
            DependencyProperty.Register("FilterDesc", typeof(string), typeof(CheckBoxFilters),
                new PropertyMetadata(string.Empty, (d, e) => ((CheckBoxFilters)d).UpdateSummary()));

        public bool IsFilterEnabled
        {
            get { return (bool)this.GetValue(IsFilterEnabledProperty); }
            set { this.SetValue(IsFilterEnabledProperty, value); }
        }
        public static readonly DependencyProperty IsFilterEnabledProperty =
            DependencyProperty.Register("IsFilterEnabled", typeof(bool), typeof(CheckBoxFilters),
                new PropertyMetadata(false, (d, e) => ((CheckBoxFilters)d).UpdateSummary()));

        public IEnumerable ItemsSource
        {
            get { return (IEnumerable)GetValue(ItemsSourceProperty); }
            set { SetValue(ItemsSourceProperty, value); }
        }

        public static readonly DependencyProperty ItemsSourceProperty =
          ItemsControl.ItemsSourceProperty.AddOwner(typeof(CheckBoxFilters),
              new FrameworkPropertyMetadata(null, OnItemsSourceChanged));

        // What the list calls an empty value, such as a request's status before it has one; by default, "(blank)".
        public string BlankText
        {
            get => (string)GetValue(BlankTextProperty);
            set => SetValue(BlankTextProperty, value);
        }

        public static readonly DependencyProperty BlankTextProperty = DependencyProperty.Register(
            nameof(BlankText), typeof(string), typeof(CheckBoxFilters),
            new PropertyMetadata(null, (d, e) => ((CheckBoxFilters)d).UpdateSummary()));

        private static readonly DependencyPropertyKey SummaryPropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(Summary), typeof(string), typeof(CheckBoxFilters), new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty SummaryProperty = SummaryPropertyKey.DependencyProperty;

        // What the filter shows: its name, and in the new look, while it filters, the values it shows after it, as in
        // "Status: 404, 503". Screen readers hear it and the dropdown's tooltip says it; the dropdown itself keeps its
        // name, so it keeps its width, and the view lists the summaries beside its filters.
        public string Summary => (string)GetValue(SummaryProperty);

        public CheckBoxFilters()
        {
            InitializeComponent();
            FilterList.DropDownOpened += FilterList_DropDownOpened;
            FilterList.PreviewKeyDown += FilterList_PreviewKeyDown;
            FilterList.SelectionChanged += FilterList_SelectionChanged;
            PropertyChangedEventManager.AddHandler(LookState.Current, LookState_Changed, nameof(LookState.IsNewLook));
            UpdateSummary();
        }

        // The name of the check box that turns a filter on, with {0} for the filter's name.
        public static string EnableFilterNameFormat => Localization.GetLocalizedString("A11Y_FILTER_ENABLE_NAME", "{0}");

        // What the new look's list calls All.
        public static string AllText => Localization.GetLocalizedString("FILTER_ALL");

        // The new look's button that turns off every filter in a row.
        public static string ClearFiltersText => Localization.GetLocalizedString("FILTERS_CLEAR");

        // Whether a value seen for the first time starts checked. In the original look it always does, so turning the
        // filter on shows it; in the new look a value that appears while the filter is on wasn't picked, so it stays
        // hidden.
        internal bool NewValuesChecked => !(LookState.Current.IsNewLook && IsFilterEnabled);

        // The list holds check boxes rather than choices. From the keyboard, the arrow keys move between the check
        // boxes and Space toggles one, so no key selects an item and replaces the filter's name in the box.
        private void FilterList_DropDownOpened(object sender, EventArgs e)
        {
            if (InputManager.Current.MostRecentInputDevice is KeyboardDevice)
            {
                // The list's items exist once the popup has opened.
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => FocusItem(0)));
            }
        }

        private void FilterList_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if ((Keyboard.Modifiers & (ModifierKeys.Alt | ModifierKeys.Control)) != 0)
            {
                // Alt+Up and Alt+Down still open and close the list.
                return;
            }

            int index = FocusedItemIndex();
            switch (e.Key)
            {
                case Key.Down:
                case Key.Up:
                    if (!FilterList.IsDropDownOpen)
                    {
                        FilterList.IsDropDownOpen = true;
                        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => FocusItem(0)));
                    }
                    else
                    {
                        FocusItem(index < 0 ? 0 : index + (e.Key == Key.Down ? 1 : -1));
                    }
                    e.Handled = true;
                    break;
                case Key.Home when FilterList.IsDropDownOpen:
                    FocusItem(0);
                    e.Handled = true;
                    break;
                case Key.End when FilterList.IsDropDownOpen:
                    FocusItem(FilterList.Items.Count - 1);
                    e.Handled = true;
                    break;
                case Key.Enter when FilterList.IsDropDownOpen:
                    FilterList.IsDropDownOpen = false;
                    e.Handled = true;
                    break;
                case Key.Tab when FilterList.IsDropDownOpen:
                    // Tab leaves the list, as it leaves any dropdown, rather than stepping through its rows.
                    FilterList.IsDropDownOpen = false;
                    FilterList.Focus();
                    FilterList.MoveFocus(new TraversalRequest(
                        (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next));
                    e.Handled = true;
                    break;
            }
        }

        // A click on a row outside its check box selects the row; undo that once the selection has settled, so the
        // box keeps showing the filter's name.
        private void FilterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FilterList.SelectedIndex == -1)
            {
                return;
            }

            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                FilterList.SelectedIndex = -1;
                BindingOperations.GetBindingExpression(FilterList, ComboBox.TextProperty)?.UpdateTarget();
            }));
        }

        private int FocusedItemIndex()
        {
            for (var element = Keyboard.FocusedElement as DependencyObject; element != null; element = ParentOf(element))
            {
                if (element is ComboBoxItem container)
                {
                    return FilterList.ItemContainerGenerator.IndexFromContainer(container);
                }

                if (ReferenceEquals(element, FilterList))
                {
                    break;
                }
            }

            return -1;
        }

        private void FocusItem(int index)
        {
            if (!FilterList.IsDropDownOpen || FilterList.Items.Count == 0)
            {
                return;
            }

            index = Math.Clamp(index, 0, FilterList.Items.Count - 1);
            if (FilterList.ItemContainerGenerator.ContainerFromIndex(index) is not ComboBoxItem container)
            {
                return;
            }

            container.BringIntoView();
            Descendants(container).OfType<CheckBox>().FirstOrDefault()?.Focus();
        }

        private static DependencyObject ParentOf(DependencyObject element) =>
            (element is Visual ? VisualTreeHelper.GetParent(element) : null) ?? LogicalTreeHelper.GetParent(element);

        private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                yield return child;
                foreach (DependencyObject descendant in Descendants(child))
                {
                    yield return descendant;
                }
            }
        }

        private void SetAllItems(bool value)
        {
            _ignoreIsCheckedEvent = true;

            try
            {
                var items = FilterList.Items.Cast<CheckedListItem>();
                foreach (var item in items)
                {
                    if (item.Text == AllNone)
                        continue;

                    item.IsChecked = value;
                }
            }
            catch
            {
            }
            finally
            {
                _ignoreIsCheckedEvent = false;
            }
        }

        // Checks the values that SHOW accepts and clears the others, as when the traffic overview or a column summary
        // filters the table to one status class, host or value. All stays checked only while every value is, and
        // FiltersChanged is raised once.
        internal void CheckOnly(Func<string, bool> show)
        {
            _ignoreIsCheckedEvent = true;
            try
            {
                var items = (ItemsSource ?? Array.Empty<CheckedListItem>()).OfType<CheckedListItem>().ToList();
                foreach (var item in items)
                {
                    item.IsChecked = show(item.Text ?? string.Empty);
                }

                if (FilterList.Items.Count > AllNoneIndex &&
                    FilterList.Items[AllNoneIndex] is CheckedListItem { Text: AllNone } all)
                {
                    all.IsChecked = items.All(item => item.IsChecked);
                }
            }
            finally
            {
                _ignoreIsCheckedEvent = false;
            }

            RaiseFiltersChangedEvent();
        }

        // Turns the filter off and checks every value again, as Clear filters does.
        internal void ShowAll()
        {
            List<CheckedListItem> values = Values();
            _ignoreIsCheckedEvent = true;
            try
            {
                foreach (CheckedListItem value in values)
                {
                    value.IsChecked = true;
                }

                SyncAll(values);
            }
            finally
            {
                _ignoreIsCheckedEvent = false;
            }

            IsFilterEnabled = false;
            RaiseFiltersChangedEvent();
        }

        // The new look's list. A box that a click, a key or a screen reader toggled shows a state the filter doesn't have:
        // picking a value while the filter is off shows only that value; while it's on, a value is picked or unpicked, and
        // unpicking the last one, or picking All, turns the filter off. A box that follows the filter's state is left alone.
        private void Pick_Changed(object sender, RoutedEventArgs e)
        {
            if (_ignoreIsCheckedEvent || sender is not CheckBox box || box.Tag is not CheckedListItem item)
            {
                return;
            }

            List<CheckedListItem> values = Values();
            string text = TextOf(item);
            bool picked = text == AllNone
                ? !IsFilterEnabled
                : IsFilterEnabled && values.Any(value => value.IsChecked && TextOf(value) == text);
            if (box.IsChecked == picked)
            {
                return;
            }

            if (text == AllNone)
            {
                if (box.IsChecked == true)
                {
                    ShowAll();
                }
            }
            else if (picked && values.All(value => !value.IsChecked || TextOf(value) == text))
            {
                ShowAll();
            }
            else
            {
                _ignoreIsCheckedEvent = true;
                try
                {
                    bool wasEnabled = IsFilterEnabled;
                    foreach (CheckedListItem value in values)
                    {
                        if (!wasEnabled || TextOf(value) == text)
                        {
                            value.IsChecked = TextOf(value) == text && !picked;
                        }
                    }

                    SyncAll(values);
                    IsFilterEnabled = true;
                }
                finally
                {
                    _ignoreIsCheckedEvent = false;
                }

                RaiseFiltersChangedEvent();
            }

            // The toggle set the box's state; once this change settles, it shows the filter's.
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                BindingOperations.GetMultiBindingExpression(box, ToggleButton.IsCheckedProperty)?.UpdateTarget()));
        }

        private List<CheckedListItem> Values() =>
            (ItemsSource ?? Array.Empty<CheckedListItem>()).OfType<CheckedListItem>().ToList();

        private static string TextOf(CheckedListItem value) => value.Text ?? string.Empty;

        // The original look's All is checked while every value is.
        private void SyncAll(List<CheckedListItem> values)
        {
            if (FilterList.Items.Count > AllNoneIndex &&
                FilterList.Items[AllNoneIndex] is CheckedListItem { Text: AllNone } all)
            {
                all.IsChecked = values.All(value => value.IsChecked);
            }
        }

        // Whether, in the new look, the filter shows only the values picked in it, which Summary names.
        internal bool HasPicks { get; private set; }

        private void UpdateSummary()
        {
            if (FilterList == null)
            {
                return;
            }

            string name = FilterDesc ?? string.Empty;
            string summary = name;
            bool filtering = LookState.Current.IsNewLook && IsFilterEnabled;
            HasPicks = false;
            if (filtering)
            {
                string[] shown = Values().Where(value => value.IsChecked)
                    .Select(value => TextOf(value).Length == 0 ? BlankLabel(BlankText) : TextOf(value))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.CurrentCulture)
                    .ToArray();
                if (shown.Length > 0)
                {
                    summary = Localization.GetLocalizedString("FILTER_SUMMARY", name, string.Join(", ", shown));
                    HasPicks = true;
                }
            }

            SetValue(SummaryPropertyKey, summary);
            FilterList.ToolTip = filtering ? summary : null;

            // A dropdown that filters looks like a checked toggle.
            if (filtering)
            {
                FilterList.SetResourceReference(BackgroundProperty, "AccentSoftBrush");
                FilterList.SetResourceReference(BorderBrushProperty, "AccentBrush");
            }
            else
            {
                FilterList.ClearValue(BackgroundProperty);
                FilterList.ClearValue(BorderBrushProperty);
            }
        }

        internal static string BlankLabel(string blankText) =>
            blankText ?? Localization.GetLocalizedString("FILTER_BLANK_VALUE");

        private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var filters = (CheckBoxFilters)d;
            if (e.OldValue is INotifyCollectionChanged oldValues)
            {
                CollectionChangedEventManager.RemoveHandler(oldValues, filters.Values_CollectionChanged);
            }

            if (e.NewValue is INotifyCollectionChanged newValues)
            {
                CollectionChangedEventManager.AddHandler(newValues, filters.Values_CollectionChanged);
            }

            // A list that replaces another holds new values, so in the new look the values picked before don't carry over:
            // the filter turns off.
            if (e.OldValue != null && LookState.Current.IsNewLook && filters.IsFilterEnabled)
            {
                filters.ShowAll();
            }
            else
            {
                filters.UpdateSummary();
            }
        }

        private void Values_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e) => UpdateSummary();

        private void LookState_Changed(object sender, PropertyChangedEventArgs e) => UpdateSummary();

        private void FilterCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_ignoreIsCheckedEvent)
                return;

            CheckedListItem checkedItem = (sender as CheckBox).Tag as CheckedListItem;

            if (checkedItem.Text == AllNone && !_allNoneProcessing) // If All/None, (un)check everything
            {
                SetAllItems(checkedItem.IsChecked);
            }

            if (!_allNoneProcessing)
            {
                RaiseFiltersChangedEvent();
            }
        }

        private void FilterCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_ignoreIsCheckedEvent)
                return;

            CheckedListItem checkedItem = (sender as CheckBox).Tag as CheckedListItem;

            if (checkedItem.Text == AllNone && !_allNoneProcessing) // If All/None, (un)check everything
            {
                SetAllItems(checkedItem.IsChecked);
            }
            else if (checkedItem.Text != AllNone) // if a regular item, uncheck All/None
            {
                _allNoneProcessing = true;
                (FilterList.Items[AllNoneIndex] as CheckedListItem).IsChecked = false;
                _allNoneProcessing = false;
            }

            if (!_allNoneProcessing)
            {
                RaiseFiltersChangedEvent();
            }
        }

        private void EnableFilters_Checked(object sender, RoutedEventArgs e)
        {
            RaiseFiltersChangedEvent();
        }

        private void EnableFilters_Unchecked(object sender, RoutedEventArgs e)
        {
            RaiseFiltersChangedEvent();
        }
    }

    // Whether a row of a filter's list in the new look shows as picked, from the row's text, whether its value is checked
    // and whether the filter is on: All while the filter is off, and a value while the filter is on and it's checked.
    public sealed class FilterPickedConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
            values is [_, bool isChecked, bool isEnabled]
                ? (values[0] as string) == CheckBoxFilters.AllNone ? !isEnabled : isEnabled && isChecked
                : false;

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    // What a row of a filter's list in the new look says, from the row's text and what the filter calls an empty value:
    // All for All, that name for an empty value, and otherwise the text.
    public sealed class FilterLabelConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            string text = values.Length > 0 ? values[0] as string : null;
            return text switch
            {
                CheckBoxFilters.AllNone => CheckBoxFilters.AllText,
                null or "" => CheckBoxFilters.BlankLabel(values.Length > 1 ? values[1] as string : null),
                _ => text,
            };
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
