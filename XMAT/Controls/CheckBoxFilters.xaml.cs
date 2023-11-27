// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

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
        }

        private void SetAllItems(bool value)
        {
            _ignoreIsCheckedEvent = true;

            try
            {
                var items = FilterList.Items.Cast<CheckedListItem>();
                foreach(var item in items)
                {
                    if(item.Text == AllNone)
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

        private void FilterCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if(_ignoreIsCheckedEvent)
                return;

            CheckedListItem checkedItem = (sender as CheckBox).Tag as CheckedListItem;

            if(checkedItem.Text == AllNone && !_allNoneProcessing) // If All/None, (un)check everything
            {
                SetAllItems(checkedItem.IsChecked);
            }

            if(!_allNoneProcessing)
            {
                RaiseFiltersChangedEvent();
            }
        }

        private void FilterCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if(_ignoreIsCheckedEvent)
                return;

            CheckedListItem checkedItem = (sender as CheckBox).Tag as CheckedListItem;

            if(checkedItem.Text == AllNone && !_allNoneProcessing) // If All/None, (un)check everything
            {
                SetAllItems(checkedItem.IsChecked);
            }
            else if(checkedItem.Text != AllNone) // if a regular item, uncheck All/None
            {
                _allNoneProcessing = true;
                (FilterList.Items[AllNoneIndex] as CheckedListItem).IsChecked = false;
                _allNoneProcessing = false;
            }

            if(!_allNoneProcessing)
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
