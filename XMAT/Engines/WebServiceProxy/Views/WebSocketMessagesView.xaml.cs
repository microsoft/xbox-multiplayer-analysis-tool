// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using XMAT.WebServiceCapture.Models;
using XMAT.Telemetry;

namespace XMAT.WebServiceCapture
{
    public partial class WebSocketMessagesView : UserControl
    {
        public static readonly DependencyProperty ItemsSourceProperty =
            DependencyProperty.Register(
                nameof(ItemsSource),
                typeof(ObservableCollection<WebSocketMessageModel>),
                typeof(WebSocketMessagesView),
                new PropertyMetadata(null, OnItemsSourceChanged));

        public static readonly DependencyProperty SelectedItemProperty =
            DependencyProperty.Register(
                nameof(SelectedItem),
                typeof(WebSocketMessageModel),
                typeof(WebSocketMessagesView),
                new FrameworkPropertyMetadata(
                    null,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    OnSelectedItemChanged));

        public ObservableCollection<WebSocketMessageModel> ItemsSource
        {
            get => (ObservableCollection<WebSocketMessageModel>)GetValue(ItemsSourceProperty);
            set => SetValue(ItemsSourceProperty, value);
        }

        public WebSocketMessageModel SelectedItem
        {
            get => (WebSocketMessageModel)GetValue(SelectedItemProperty);
            set => SetValue(SelectedItemProperty, value);
        }

        public WebSocketMessagesView()
        {
            InitializeComponent();
        }

        private static void OnItemsSourceChanged(
            DependencyObject dependencyObject,
            DependencyPropertyChangedEventArgs args)
        {
            ((WebSocketMessagesView)dependencyObject).ApplyFilter();
        }

        private void Filter_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (IsLoaded && IsVisible && sender is ComboBox { IsKeyboardFocusWithin: true })
            {
                XmatTelemetryService.Current.Use(sender == DirectionFilter
                    ? TelemetryFeature.FilterWebSocketDirection : TelemetryFeature.FilterWebSocketType);
            }
            ApplyFilter();
        }

        private static void OnSelectedItemChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
        {
            var view = (WebSocketMessagesView)owner;
            if (args.NewValue != null)
            {
                view.RecordPayloadView();
            }
        }

        private void PayloadTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source == PayloadTabs)
            {
                RecordPayloadView();
            }
        }

        private void RecordPayloadView()
        {
            if (IsLoaded && IsVisible && SelectedItem != null)
            {
                XmatTelemetryService.Current.Use(PayloadTabs.SelectedIndex switch
                {
                    1 => TelemetryFeature.WebSocketJson,
                    2 => TelemetryFeature.WebSocketHex,
                    _ => TelemetryFeature.WebSocketText
                });
            }
        }

        private void ApplyFilter()
        {
            if (ItemsSource == null)
            {
                return;
            }

            ICollectionView view = CollectionViewSource.GetDefaultView(ItemsSource);
            view.Filter = item =>
            {
                if (item is not WebSocketMessageModel message)
                {
                    return false;
                }

                return message.MatchesFilter(
                    SelectedFilterValue(DirectionFilter),
                    SelectedFilterValue(MessageTypeFilter));
            };
            view.Refresh();
        }

        private static string SelectedFilterValue(ComboBox comboBox)
        {
            return (comboBox?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All";
        }
    }
}
