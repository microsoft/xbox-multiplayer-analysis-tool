// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using XMAT.WebServiceCapture.Models;

namespace XMAT.WebServiceCapture
{
    /// <summary>
    /// Waterfall / By-Host timeline view for HTTP proxy requests.
    /// </summary>
    public partial class CaptureTimelineView : UserControl
    {
        private const double BarHeight = 14;
        private readonly CaptureTimelineLayout _layout = new();
        private readonly HashSet<ProxyConnectionModel> _subscribedItems = new(ReferenceEqualityComparer.Instance);
        private readonly List<CaptureTimelineLayout.Row> _visibleRows = new();
        private readonly List<CaptureTimelineLayout.Tick> _visibleTicks = new();
        private readonly List<BarVisual> _bars = new();
        private readonly List<TextBlock> _hostLabels = new();
        private readonly List<AxisVisual> _axis = new();
        private readonly DispatcherTimer _refreshTimer;
        // Shared by every bar and host label; filled for the right-clicked element when it opens.
        private readonly ContextMenu _hostListMenu = new();
        private ObservableCollection<ProxyConnectionModel> _attachedSource;
        private DispatcherOperation _pendingRender;
        private bool _active;
        private bool _dirty;
        private bool _renderingViewport;
        private bool _isWaterfall = true;
        private static readonly Brush[] StatusBrushes = CreateStatusBrushes();

        public static readonly DependencyProperty ItemsSourceProperty =
            DependencyProperty.Register(nameof(ItemsSource),
                typeof(ObservableCollection<ProxyConnectionModel>),
                typeof(CaptureTimelineView),
                new PropertyMetadata(null, OnItemsSourceChanged));

        public ObservableCollection<ProxyConnectionModel> ItemsSource
        {
            get => (ObservableCollection<ProxyConnectionModel>)GetValue(ItemsSourceProperty);
            set => SetValue(ItemsSourceProperty, value);
        }

        public static readonly DependencyProperty SelectedItemProperty =
            DependencyProperty.Register(nameof(SelectedItem),
                typeof(ProxyConnectionModel),
                typeof(CaptureTimelineView),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    OnSelectionChanged));

        public ProxyConnectionModel SelectedItem
        {
            get => (ProxyConnectionModel)GetValue(SelectedItemProperty);
            set => SetValue(SelectedItemProperty, value);
        }

        public static readonly DependencyProperty IsCaptureRunningProperty =
            DependencyProperty.Register(nameof(IsCaptureRunning), typeof(bool), typeof(CaptureTimelineView),
                new PropertyMetadata(false, OnCaptureRunningChanged));

        public bool IsCaptureRunning
        {
            get => (bool)GetValue(IsCaptureRunningProperty);
            set => SetValue(IsCaptureRunningProperty, value);
        }

        public static readonly DependencyProperty HostListPolicyProperty =
            DependencyProperty.Register(nameof(HostListPolicy), typeof(HostListPolicy), typeof(CaptureTimelineView),
                new PropertyMetadata(null));

        // The application's host lists edited from bar and host-label context menus.
        public HostListPolicy HostListPolicy
        {
            get => (HostListPolicy)GetValue(HostListPolicyProperty);
            set => SetValue(HostListPolicyProperty, value);
        }

        internal int RenderCount { get; private set; }
        internal bool HasPendingRefresh => _refreshTimer.IsEnabled ||
            _pendingRender?.Status == DispatcherOperationStatus.Pending;

        public CaptureTimelineView()
        {
            _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _refreshTimer.Tick += RefreshTimerTick;
            InitializeComponent();
            _hostListMenu.SetResourceReference(Control.BackgroundProperty, "MenuBackgroundBrush");
            _hostListMenu.SetResourceReference(Control.ForegroundProperty, "MenuForegroundBrush");
            Loaded += ViewLoaded;
            Unloaded += ViewUnloaded;
            IsVisibleChanged += ViewVisibilityChanged;
            ContextMenuOpening += HostListMenuOpening;
        }

        private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var view = (CaptureTimelineView)d;
            view.DetachSource();
            if (view._active)
            {
                view.AttachSource();
                view.RequestRefresh(true);
            }
        }

        private static void OnSelectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
            ((CaptureTimelineView)d).RequestRefresh(true);

        private static void OnCaptureRunningChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(bool)e.NewValue)
            {
                ((CaptureTimelineView)d).RequestRefresh(true);
            }
        }

        private void ViewLoaded(object sender, RoutedEventArgs e) => UpdateActivity();
        private void ViewUnloaded(object sender, RoutedEventArgs e) => Deactivate();
        private void ViewVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateActivity();

        private void UpdateActivity()
        {
            if (!IsLoaded || !IsVisible)
            {
                Deactivate();
                return;
            }
            if (!_active)
            {
                _active = true;
                AttachSource();
            }
            RequestRefresh(true);
        }

        private void Deactivate()
        {
            _active = false;
            DetachSource();
            TrimBars(0);
            TrimLabels(0);
            TrimAxis(0);
        }

        private void AttachSource()
        {
            _attachedSource = ItemsSource;
            if (_attachedSource != null)
            {
                _attachedSource.CollectionChanged += OnCollectionChanged;
                foreach (var model in _attachedSource)
                {
                    AttachItem(model);
                }
            }
        }

        private void DetachSource()
        {
            CancelRefresh();
            if (_attachedSource != null)
            {
                _attachedSource.CollectionChanged -= OnCollectionChanged;
                _attachedSource = null;
            }
            foreach (var model in _subscribedItems)
            {
                model.PropertyChanged -= ItemPropertyChanged;
            }
            _subscribedItems.Clear();
            _layout.Reset(Array.Empty<ProxyConnectionModel>());
            _visibleRows.Clear();
            foreach (var bar in _bars)
            {
                bar.Model = null;
                bar.Root.Tag = null;
                bar.Root.ToolTip = null;
            }
        }

        private void AttachItem(ProxyConnectionModel model)
        {
            if (_subscribedItems.Add(model))
            {
                _layout.Add(model);
                model.PropertyChanged += ItemPropertyChanged;
            }
        }

        private void DetachItem(ProxyConnectionModel model)
        {
            if (_subscribedItems.Remove(model))
            {
                model.PropertyChanged -= ItemPropertyChanged;
                _layout.Remove(model);
            }
        }

        private void OnCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            Dispatcher.VerifyAccess();
            if (!ReferenceEquals(sender, _attachedSource))
            {
                return;
            }
            if (e.Action is NotifyCollectionChangedAction.Reset or NotifyCollectionChangedAction.Move or
                    NotifyCollectionChangedAction.Replace ||
                (e.Action == NotifyCollectionChangedAction.Add &&
                    e.NewStartingIndex != _attachedSource.Count - e.NewItems.Count))
            {
                DetachSource();
                AttachSource();
            }
            else
            {
                if (e.OldItems != null)
                {
                    foreach (ProxyConnectionModel model in e.OldItems)
                    {
                        DetachItem(model);
                    }
                }
                if (e.NewItems != null)
                {
                    foreach (ProxyConnectionModel model in e.NewItems)
                    {
                        AttachItem(model);
                    }
                }
            }
            RequestRefresh(e.Action != NotifyCollectionChangedAction.Add);
        }

        private void ItemPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            Dispatcher.VerifyAccess();
            if (_layout.Update((ProxyConnectionModel)sender))
            {
                RequestRefresh(false);
            }
        }

        private void ViewMode_Changed(object sender, RoutedEventArgs e)
        {
            _isWaterfall = WaterfallMode.IsChecked is true;
            RequestRefresh(true);
        }

        private void TimelineScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.HorizontalChange != 0 || e.VerticalChange != 0 ||
                e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0)
            {
                RefreshViewport();
            }
        }

        private void TimelineSizeChanged(object sender, SizeChangedEventArgs e) => RefreshViewport();

        private void RefreshViewport()
        {
            if (!_active)
            {
                return;
            }
            CancelRefresh();
            _dirty = true;
            // ScrollChanged is raised during layout; queued background work misses this composition frame.
            RenderViewport();
        }

        private void RequestRefresh(bool immediate)
        {
            if (!_active)
            {
                return;
            }
            _dirty = true;
            if (immediate || !IsCaptureRunning)
            {
                _refreshTimer.Stop();
                if (_pendingRender?.Status != DispatcherOperationStatus.Pending)
                {
                    _pendingRender = Dispatcher.BeginInvoke(
                        DispatcherPriority.Background, new Action(RenderPendingUpdate));
                }
            }
            else if (_pendingRender?.Status != DispatcherOperationStatus.Pending && !_refreshTimer.IsEnabled)
            {
                _refreshTimer.Start();
            }
        }

        private void RefreshTimerTick(object sender, EventArgs e)
        {
            _refreshTimer.Stop();
            RequestRefresh(true);
        }

        private void CancelRefresh()
        {
            _refreshTimer.Stop();
            _pendingRender?.Abort();
            _pendingRender = null;
            _dirty = false;
        }

        private void RenderPendingUpdate()
        {
            _pendingRender = null;
            RenderViewport();
        }

        private void RenderViewport()
        {
            if (!_active || !_dirty || _renderingViewport)
            {
                return;
            }
            _renderingViewport = true;
            try
            {
                RenderViewportCore();
            }
            finally
            {
                _renderingViewport = false;
            }
        }

        private void RenderViewportCore()
        {
            var width = TimelineScrollViewer.ViewportWidth;
            var height = TimelineScrollViewer.ViewportHeight;
            if (width <= 0 || height <= 0 || !double.IsFinite(width) || !double.IsFinite(height))
            {
                TrimBars(0);
                TrimLabels(0);
                TrimAxis(0);
                return;
            }

            _dirty = false;
            RenderCount++;
            var extentHeight = CaptureTimelineLayout.AxisHeight + _layout.GetHeight(_isWaterfall);
            TimelineExtent.Width = _layout.Width;
            TimelineExtent.Height = extentHeight;
            var left = Math.Clamp(TimelineScrollViewer.HorizontalOffset, 0, Math.Max(0, _layout.Width - width));
            var top = Math.Clamp(TimelineScrollViewer.VerticalOffset, 0, Math.Max(0, extentHeight - height));
            var rowOffset = Math.Max(0, top - CaptureTimelineLayout.AxisHeight);
            var rowHeight = Math.Max(0, height - Math.Max(0, CaptureTimelineLayout.AxisHeight - top));
            Canvas.SetLeft(TimelineCanvas, left);
            Canvas.SetTop(TimelineCanvas, rowOffset + CaptureTimelineLayout.AxisHeight);
            TimelineCanvas.Width = width;
            TimelineCanvas.Height = rowHeight;
            Canvas.SetLeft(TimeAxisCanvas, left);
            TimeAxisCanvas.Width = width;
            _layout.GetVisibleRows(_isWaterfall, rowOffset, rowHeight, _visibleRows);

            var barCount = 0;
            var labelCount = 0;
            foreach (var row in _visibleRows)
            {
                if (row.Model == null)
                {
                    var label = GetHostLabel(labelCount++);
                    label.Text = row.Host;
                    label.Tag = row.Host;
                    Canvas.SetLeft(label, 4 - left);
                    Canvas.SetTop(label, row.Top - rowOffset);
                }
                else
                {
                    var bounds = _layout.GetBarBounds(row);
                    if (bounds.Left + bounds.Width >= left && bounds.Left <= left + width)
                    {
                        UpdateBar(GetBar(barCount++), row, bounds.Left - left, bounds.Width, row.Top - rowOffset);
                    }
                }
            }
            TrimBars(barCount);
            TrimLabels(labelCount);
            RenderAxis(left, width, top < CaptureTimelineLayout.AxisHeight);
        }

        private BarVisual GetBar(int index)
        {
            if (index == _bars.Count)
            {
                var label = new TextBlock
                {
                    FontSize = 9,
                    Foreground = Brushes.White,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(3, 0, 3, 0)
                };
                var border = new Border
                {
                    Height = BarHeight,
                    CornerRadius = new CornerRadius(2),
                    Cursor = Cursors.Hand,
                    Child = label,
                    ContextMenu = _hostListMenu
                };
                border.MouseLeftButtonDown += Bar_Click;
                _bars.Add(new BarVisual(border, label));
                TimelineCanvas.Children.Add(border);
            }
            return _bars[index];
        }

        private static void UpdateBar(BarVisual visual, CaptureTimelineLayout.Row row, double left, double width, double top)
        {
            var model = row.Model;
            if (!ReferenceEquals(visual.Model, model) || visual.Version != row.Version)
            {
                visual.Model = model;
                visual.Version = row.Version;
                visual.Root.Tag = model;
                visual.Root.Background = StatusBrush(model.Status);
                visual.Root.ToolTip = $"{model.Method} {model.Host}{model.Path}\n" +
                    $"Status: {model.Status}\nTime: {row.Start:HH:mm:ss.fff}\n" +
                    $"Duration: {row.Duration.TotalMilliseconds:F0} ms";
                visual.Label.Text = $"{model.Method} {model.Path}";
            }
            visual.Root.Width = width;
            visual.Label.Visibility = width > 60 ? Visibility.Visible : Visibility.Collapsed;
            Canvas.SetLeft(visual.Root, left);
            Canvas.SetTop(visual.Root, top);
        }

        private void Bar_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is ProxyConnectionModel model)
            {
                SelectedItem = model;
            }
        }

        // Bars carry their request and host labels carry their host group in Tag; the menu
        // acts on the element that owns the route, not on the selected request.
        private void HostListMenuOpening(object sender, ContextMenuEventArgs e)
        {
            FrameworkElement owner = HostListContextMenu.FindOwner(e.OriginalSource);
            if (owner == null || !ReferenceEquals(owner.ContextMenu, _hostListMenu))
            {
                return;
            }
            if (HostListPolicy == null)
            {
                e.Handled = true;
                return;
            }

            string host = owner.Tag switch
            {
                ProxyConnectionModel model => model.ActionableHost,
                string hostGroup => hostGroup,
                _ => null
            };
            HostListContextMenu.Populate(_hostListMenu, HostListPolicy, host, "HOST_LIST_NO_HOST", owner);
        }

        private TextBlock GetHostLabel(int index)
        {
            if (index == _hostLabels.Count)
            {
                var label = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 11, ContextMenu = _hostListMenu };
                label.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryForegroundBrush");
                _hostLabels.Add(label);
                TimelineCanvas.Children.Add(label);
            }
            return _hostLabels[index];
        }

        private void RenderAxis(double offset, double width, bool visible)
        {
            _visibleTicks.Clear();
            if (visible)
            {
                _layout.GetVisibleTicks(offset, width, _visibleTicks);
            }
            for (var index = 0; index < _visibleTicks.Count; index++)
            {
                var visual = GetAxisVisual(index);
                var tick = _visibleTicks[index];
                var left = tick.Left - offset;
                visual.Tick.X1 = left;
                visual.Tick.X2 = left;
                visual.Label.Text = _layout.SpanMilliseconds > 60000
                    ? TimeSpan.FromMilliseconds(tick.Milliseconds).ToString(@"m\:ss\.f")
                    : _layout.SpanMilliseconds > 1000
                        ? $"{tick.Milliseconds / 1000:F1}s"
                        : $"{tick.Milliseconds:F0}ms";
                Canvas.SetLeft(visual.Label, left + 2);
            }
            TrimAxis(_visibleTicks.Count);
        }

        private AxisVisual GetAxisVisual(int index)
        {
            if (index == _axis.Count)
            {
                var tick = new Line
                {
                    Y1 = 14,
                    Y2 = 20,
                    StrokeThickness = 1
                };
                tick.SetResourceReference(Shape.StrokeProperty, "SecondaryForegroundBrush");
                var label = new TextBlock { FontSize = 9 };
                label.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryForegroundBrush");
                _axis.Add(new AxisVisual(tick, label));
                TimeAxisCanvas.Children.Add(tick);
                TimeAxisCanvas.Children.Add(label);
            }
            return _axis[index];
        }

        private void TrimBars(int count)
        {
            while (_bars.Count > count)
            {
                var bar = _bars[^1];
                bar.Root.MouseLeftButtonDown -= Bar_Click;
                bar.Root.Tag = null;
                bar.Root.ToolTip = null;
                TimelineCanvas.Children.Remove(bar.Root);
                _bars.RemoveAt(_bars.Count - 1);
            }
        }

        private void TrimLabels(int count)
        {
            while (_hostLabels.Count > count)
            {
                TimelineCanvas.Children.Remove(_hostLabels[^1]);
                _hostLabels.RemoveAt(_hostLabels.Count - 1);
            }
        }

        private void TrimAxis(int count)
        {
            while (_axis.Count > count)
            {
                var visual = _axis[^1];
                TimeAxisCanvas.Children.Remove(visual.Tick);
                TimeAxisCanvas.Children.Remove(visual.Label);
                _axis.RemoveAt(_axis.Count - 1);
            }
        }

        private static Brush StatusBrush(string status)
        {
            var index = int.TryParse(status, out var code) ? code / 100 : 0;
            return StatusBrushes[index >= 2 ? Math.Min(index, 5) - 1 : 0];
        }

        private static Brush[] CreateStatusBrushes()
        {
            var colors = new[]
            {
                Color.FromRgb(0x68, 0x68, 0x68), Color.FromRgb(0x4C, 0xAF, 0x50),
                Color.FromRgb(0x21, 0x96, 0xF3), Color.FromRgb(0xFF, 0x98, 0x00),
                Color.FromRgb(0xF4, 0x43, 0x36)
            };
            var brushes = new Brush[colors.Length];
            for (var index = 0; index < colors.Length; index++)
            {
                var brush = new SolidColorBrush(colors[index]);
                brush.Freeze();
                brushes[index] = brush;
            }
            return brushes;
        }

        private sealed class BarVisual(Border root, TextBlock label)
        {
            internal Border Root { get; } = root;
            internal TextBlock Label { get; } = label;
            internal ProxyConnectionModel Model { get; set; }
            internal long Version { get; set; }
        }

        private sealed record AxisVisual(Line Tick, TextBlock Label);
    }
}
