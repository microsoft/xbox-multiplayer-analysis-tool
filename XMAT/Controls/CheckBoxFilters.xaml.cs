// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace XMAT
{
    /// <summary>
    /// Interaction logic for CheckBoxFilters.xaml
    /// </summary>
    public partial class CheckBoxFilters : UserControl
    {
        private const int AllNoneIndex = 0;
        private const string AllNone = "*";

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
            RaiseEvent(new RoutedEventArgs(FiltersChangedEvent));
        }

        public string FilterDesc
        {
            get { return (string)this.GetValue(FilterDescProperty); }
            set { this.SetValue(FilterDescProperty, value); }
        }
        public static readonly DependencyProperty FilterDescProperty =
            DependencyProperty.Register("FilterDesc", typeof(string), typeof(CheckBoxFilters), new PropertyMetadata(string.Empty));

        public bool IsFilterEnabled
        {
            get { return (bool)this.GetValue(IsFilterEnabledProperty); }
            set { this.SetValue(IsFilterEnabledProperty, value); }
        }
        public static readonly DependencyProperty IsFilterEnabledProperty =
            DependencyProperty.Register("IsFilterEnabled", typeof(bool), typeof(CheckBoxFilters), new PropertyMetadata(false));

        public IEnumerable ItemsSource
        {
            get { return (IEnumerable)GetValue(ItemsSourceProperty); }
            set { SetValue(ItemsSourceProperty, value); }
        }

        public static readonly DependencyProperty ItemsSourceProperty =
          ItemsControl.ItemsSourceProperty.AddOwner(typeof(CheckBoxFilters));

        public CheckBoxFilters()
        {
            InitializeComponent();
            FilterList.DropDownOpened += FilterList_DropDownOpened;
            FilterList.PreviewKeyDown += FilterList_PreviewKeyDown;
            FilterList.SelectionChanged += FilterList_SelectionChanged;
        }

        // The name of the check box that turns a filter on, with {0} for the filter's name.
        public static string EnableFilterNameFormat => Localization.GetLocalizedString("A11Y_FILTER_ENABLE_NAME", "{0}");

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
}
