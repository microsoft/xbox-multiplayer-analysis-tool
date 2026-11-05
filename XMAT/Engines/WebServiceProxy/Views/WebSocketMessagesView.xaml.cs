// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using XMAT.Operations;
using XMAT.Operations.Inspection;
using XMAT.WebServiceCapture.Models;

namespace XMAT.WebServiceCapture
{
    // The Text, JSON and Hex tabs show one bounded preview of the selected message's payload (BodyPreviewOperation),
    // made off the UI thread whenever the selection changes and only while the view is loaded (BodyPreviewPipeline).
    public partial class WebSocketMessagesView : UserControl, IBodyPreviewTarget
    {
        private readonly BodyPreviewPipeline _preview;

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
            _preview = new BodyPreviewPipeline(this);
            InitializeComponent();
            Loaded += (_, _) => StartPreviews();
            Unloaded += (_, _) => StopPreview();
        }

        // Runs this view's previews (BodyPreviewPipeline.Runner); tests replace it to decide when previews complete.
        internal Func<BodyPreviewRequest, CancellationToken, Task<OperationResult<BodyPreview>>> PreviewRunner
        {
            get => _preview.Runner;
            set => _preview.Runner = value;
        }

        // Stops previewing: the preview in progress is cancelled and its result never shown. The view does so when it
        // is unloaded or the application exits.
        internal void StopPreview()
        {
            Dispatcher.ShutdownStarted -= Dispatcher_ShutdownStarted;
            _preview.Deactivate();
        }

        private void StartPreviews()
        {
            if (Dispatcher.HasShutdownStarted)
            {
                return;
            }

            Dispatcher.ShutdownStarted -= Dispatcher_ShutdownStarted;
            Dispatcher.ShutdownStarted += Dispatcher_ShutdownStarted;
            _preview.Activate();
        }

        private void Dispatcher_ShutdownStarted(object sender, EventArgs e) => StopPreview();

        private static void OnSelectedItemChanged(
            DependencyObject dependencyObject,
            DependencyPropertyChangedEventArgs args)
        {
            ((WebSocketMessagesView)dependencyObject)._preview.InputChanged();
        }

        // A message model never changes, so the same message is the same input.
        object IBodyPreviewTarget.InputIdentity => SelectedItem;

        BodyPreviewRequest IBodyPreviewTarget.CreateRequest() => SelectedItem.PreviewRequest();

        void IBodyPreviewTarget.ShowNothing() => Show(null, null, null, null, null);

        void IBodyPreviewTarget.ShowPending() => Show(BodyPreviewDisplay.Pending, null, null, null, null);

        void IBodyPreviewTarget.ShowFailure() => Show(BodyPreviewDisplay.Failed, null, null, null, null);

        void IBodyPreviewTarget.ShowPreview(BodyPreview preview) =>
            Show(
                BodyPreviewDisplay.Message(preview, "WEBSOCKET_PREVIEW_EMPTY"),
                BodyPreviewDisplay.NoJson(preview),
                preview.Text,
                preview.JsonDocuments,
                preview.Hex);

        void IBodyPreviewTarget.Post(Action action) =>
            _ = Dispatcher.InvokeAsync(action, DispatcherPriority.Background);

        // The message bar is above every tab; the JSON tab only says when it has no tree.
        private void Show(
            string message, string noJson, string text, IReadOnlyList<BodyPreviewNode> documents, string hex)
        {
            ShowMessage(PreviewMessage, message);
            ShowMessage(JsonMessage, noJson);
            PayloadTextBox.Text = text ?? string.Empty;
            PayloadJsonTree.ItemsSource = BodyPreviewDisplay.Tree(documents);
            PayloadHexBox.Text = hex ?? string.Empty;
        }

        private static void ShowMessage(TextBlock bar, string message)
        {
            bar.Text = message ?? string.Empty;
            bar.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        private static void OnItemsSourceChanged(
            DependencyObject dependencyObject,
            DependencyPropertyChangedEventArgs args)
        {
            ((WebSocketMessagesView)dependencyObject).ApplyFilter();
        }

        private void Filter_Changed(object sender, SelectionChangedEventArgs e)
        {
            ApplyFilter();
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
