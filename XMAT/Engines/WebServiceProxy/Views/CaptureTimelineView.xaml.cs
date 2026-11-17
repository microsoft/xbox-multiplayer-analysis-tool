// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;
using XMAT.Operations.Processes;
using XMAT.WebServiceCapture.Models;

namespace XMAT.WebServiceCapture
{
    /// <summary>
    /// Waterfall / By-Host timeline view for HTTP proxy requests, and the Volume chart of requests per second.
    /// </summary>
    public partial class CaptureTimelineView : UserControl
    {
        private const double BarHeight = 14;
        private const double AxisTickLength = 6;
        // The live Volume chart moves on just after each second ends.
        private static readonly TimeSpan SecondTimerDelay = TimeSpan.FromMilliseconds(20);
        private readonly CaptureTimelineLayout _layout = new();
        private readonly HashSet<ProxyConnectionModel> _subscribedItems = new(ReferenceEqualityComparer.Instance);
        private readonly List<CaptureTimelineLayout.Row> _visibleRows = new();
        private readonly List<CaptureTimelineLayout.Tick> _visibleTicks = new();
        private readonly List<BarVisual> _bars = new();
        private readonly List<TextBlock> _hostLabels = new();
        private readonly List<AxisVisual> _axis = new();
        private readonly DispatcherTimer _refreshTimer;
        private readonly DispatcherTimer _secondTimer;
        // Shared by every bar and host label; filled for the right-clicked element when it opens.
        private readonly ContextMenu _hostListMenu = new();
        private ObservableCollection<ProxyConnectionModel> _attachedSource;
        private DispatcherOperation _pendingRender;
        private bool _active;
        private bool _dirty;
        private bool _renderingViewport;
        private bool _isWaterfall = true;
        private bool _isVolume;

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

        public static readonly DependencyProperty ProcessFilterProperty =
            DependencyProperty.Register(nameof(ProcessFilter), typeof(ProcessFilterSpec), typeof(CaptureTimelineView),
                new PropertyMetadata(null, OnProcessFilterChanged));

        // The tab's process filter. Unlike the grid, the timeline ignores the Status, Host and Method filters.
        public ProcessFilterSpec ProcessFilter
        {
            get => (ProcessFilterSpec)GetValue(ProcessFilterProperty);
            set => SetValue(ProcessFilterProperty, value);
        }

        public static readonly DependencyProperty AutoScrollProperty =
            DependencyProperty.Register(nameof(AutoScroll), typeof(bool), typeof(CaptureTimelineView),
                new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        // The hosting capture view's Auto Scroll, which the new look offers at the end of the header, after the
        // Capturing indicator. When the header is too narrow for them beside the modes, they move to a line of their
        // own under the modes.
        public bool AutoScroll
        {
            get => (bool)GetValue(AutoScrollProperty);
            set => SetValue(AutoScrollProperty, value);
        }

        // Whether a bar's context menu offers the process filter items; the capture view that hosts the timeline handles
        // their commands.
        internal bool OffersProcessFilterItems { get; set; }

        // The time axis labels' size, which the Windows text size can enlarge.
        private static readonly DependencyProperty AxisFontSizeProperty =
            DependencyProperty.Register("AxisFontSize", typeof(double), typeof(CaptureTimelineView),
                new PropertyMetadata(12.0, (d, e) => ((CaptureTimelineView)d).RequestRefresh(true)));

        // The time axis is as tall as its labels and the ticks under them need, and never shorter than designed, so
        // labels that the Windows text size enlarges aren't cut off.
        private double AxisHeight => Math.Max(CaptureTimelineLayout.MinimumAxisHeight,
            Math.Ceiling((double)GetValue(AxisFontSizeProperty) * System.Windows.Documents.TextElement.GetFontFamily(this).LineSpacing) +
            AxisTickLength - 2);

        internal int RenderCount { get; private set; }
        internal bool HasPendingRefresh => _refreshTimer.IsEnabled ||
            _pendingRender?.Status == DispatcherOperationStatus.Pending;

        // The clock that the live Volume chart follows, in RequestTime's local time.
        internal Func<DateTime> Clock { get; set; } = () => DateTime.Now;

        // Whether the live Volume chart is moving on once a second.
        internal bool IsFollowingLiveVolume => _secondTimer.IsEnabled;

        public CaptureTimelineView()
        {
            _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _refreshTimer.Tick += RefreshTimerTick;
            _secondTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            _secondTimer.Tick += SecondTimerTick;
            InitializeComponent();
            SetResourceReference(AxisFontSizeProperty, "TimelineAxisFontSize");
            // The connections list shows the same requests and works with the keyboard; the bars are for the mouse.
            AutomationProperties.SetName(TimelineScrollViewer, Localization.GetLocalizedString("TIMELINE_AUTOMATION_NAME"));
            AutomationProperties.SetHelpText(TimelineScrollViewer, Localization.GetLocalizedString("TIMELINE_AUTOMATION_HELP"));
            AutoScrollButton.Content = Localization.GetLocalizedString("WEBCAP_AUTOSCROLL");
            VolumeChart.StepClicked = VolumeStepClicked;
            _hostListMenu.SetResourceReference(Control.BackgroundProperty, "MenuBackgroundBrush");
            _hostListMenu.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
            Loaded += ViewLoaded;
            Unloaded += ViewUnloaded;
            IsVisibleChanged += ViewVisibilityChanged;
            ContextMenuOpening += HostListMenuOpening;
            PropertyChangedEventManager.AddHandler(LookState.Current, LookChanged, nameof(LookState.IsNewLook));
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

        // A filter edit rebuilds the rows once; a request whose process resolves later is shown or hidden on its own.
        private static void OnProcessFilterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var view = (CaptureTimelineView)d;
            view._layout.SetFilter(CaptureProcessViewAdapter.CreatePredicate(e.NewValue as ProcessFilterSpec));
            view.RequestRefresh(true);
        }

        // Stopping shows the final responses, or the whole capture in Volume; starting shows Volume's live minute.
        private static void OnCaptureRunningChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var view = (CaptureTimelineView)d;
            view.UpdateSecondTimer();
            if (!(bool)e.NewValue || view._isVolume)
            {
                view.RequestRefresh(true);
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
            UpdateSecondTimer();
            RequestRefresh(true);
        }

        private void Deactivate()
        {
            _active = false;
            UpdateSecondTimer();
            DetachSource();
            TrimBars(0);
            TrimLabels(0);
            TrimAxis(0);
            VolumeChart.Clear();
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
            _isVolume = VolumeMode?.IsChecked is true;
            // The first mode is checked while the view is still being built.
            if (VolumeChart != null)
            {
                ShowMode();
            }
            RequestRefresh(true);
        }

        // Volume replaces the waterfall in its well; the hidden view lets go of what it drew.
        private void ShowMode()
        {
            TimelineScrollViewer.Visibility = _isVolume ? Visibility.Collapsed : Visibility.Visible;
            VolumeChart.Visibility = _isVolume ? Visibility.Visible : Visibility.Collapsed;
            if (_isVolume)
            {
                TrimBars(0);
                TrimLabels(0);
                TrimAxis(0);
            }
            else
            {
                VolumeChart.Clear();
            }
            UpdateSecondTimer();
        }

        // Bar labels differ by look, so every bar is updated again. Volume is part of the new look only, so the original
        // look goes back to the waterfall.
        private void LookChanged(object sender, PropertyChangedEventArgs e)
        {
            foreach (var bar in _bars)
            {
                bar.Model = null;
            }
            if (!LookState.Current.IsNewLook && VolumeMode.IsChecked is true)
            {
                WaterfallMode.IsChecked = true;
            }
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

        // While a live capture shows Volume, the chart moves on once a second, as each second ends; otherwise nothing
        // runs.
        private void UpdateSecondTimer()
        {
            if (!_active || !_isVolume || !IsCaptureRunning)
            {
                _secondTimer.Stop();
            }
            else if (!_secondTimer.IsEnabled)
            {
                var ticks = Clock().Ticks % TimeSpan.TicksPerSecond;
                _secondTimer.Interval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond - ticks) + SecondTimerDelay;
                _secondTimer.Start();
            }
        }

        private void SecondTimerTick(object sender, EventArgs e)
        {
            _secondTimer.Stop();
            RefreshViewport();
            UpdateSecondTimer();
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
            if (_isVolume)
            {
                _dirty = false;
                RenderCount++;
                VolumeChart.Show(_layout.Volume, IsCaptureRunning, Clock());
                return;
            }

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
            var axisHeight = AxisHeight;
            var extentHeight = axisHeight + _layout.GetHeight(_isWaterfall);
            TimelineExtent.Width = _layout.Width;
            TimelineExtent.Height = extentHeight;
            var left = Math.Clamp(TimelineScrollViewer.HorizontalOffset, 0, Math.Max(0, _layout.Width - width));
            var top = Math.Clamp(TimelineScrollViewer.VerticalOffset, 0, Math.Max(0, extentHeight - height));
            var rowOffset = Math.Max(0, top - axisHeight);
            var rowHeight = Math.Max(0, height - Math.Max(0, axisHeight - top));
            Canvas.SetLeft(TimelineCanvas, left);
            Canvas.SetTop(TimelineCanvas, rowOffset + axisHeight);
            TimelineCanvas.Width = width;
            TimelineCanvas.Height = rowHeight;
            Canvas.SetLeft(TimeAxisCanvas, left);
            TimeAxisCanvas.Width = width;
            TimeAxisCanvas.Height = axisHeight;
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
            RenderAxis(left, width, axisHeight, top < axisHeight);
        }

        private BarVisual GetBar(int index)
        {
            if (index == _bars.Count)
            {
                var label = new TextBlock
                {
                    FontSize = 9,
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
                var status = TrafficSummary.ClassOf(model.Status);
                if (visual.Status != status)
                {
                    visual.Status = status;
                    visual.Root.SetResourceReference(Border.BackgroundProperty, StatusMeanings.ChartFill(status));
                    visual.Label.SetResourceReference(TextBlock.ForegroundProperty, StatusMeanings.ChartLabel(status));
                }
                visual.Root.ToolTip = $"{model.Method} {model.Host}{model.Path}\n" +
                    $"Status: {model.Status}\nTime: {row.Start:HH:mm:ss.fff}\n" +
                    $"Duration: {row.Duration.TotalMilliseconds:F0} ms";
                // The new look starts the label with the status, so that it is not told by color alone.
                visual.Label.Text = LookState.Current.IsNewLook && !string.IsNullOrEmpty(model.Status)
                    ? $"{model.Status} {model.Method} {model.Path}"
                    : $"{model.Method} {model.Path}";
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

        // A Volume point selects its first request, as a bar selects its own.
        private void VolumeStepClicked(long first, long end)
        {
            var model = _layout.FirstRequestBetween(CaptureVolumeSeries.TimeOf(first), CaptureVolumeSeries.TimeOf(end));
            if (model != null)
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
            if (owner.Tag is ProxyConnectionModel request && OffersProcessFilterItems)
            {
                CaptureProcessViewAdapter.AddMenuItems(_hostListMenu, request, ProcessFilter, owner);
            }
        }

        // The menu keeps the items and the bar of its last opening until it opens again; a deletion closes it and releases them.
        internal void ReleaseMenuTargets()
        {
            _hostListMenu.IsOpen = false;
            _hostListMenu.Items.Clear();
            _hostListMenu.ClearValue(ContextMenu.PlacementTargetProperty);
        }

        private TextBlock GetHostLabel(int index)
        {
            if (index == _hostLabels.Count)
            {
                var label = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 11, ContextMenu = _hostListMenu };
                label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                _hostLabels.Add(label);
                TimelineCanvas.Children.Add(label);
            }
            return _hostLabels[index];
        }

        private void RenderAxis(double offset, double width, double height, bool visible)
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
                // The ticks end at the bottom of the axis, under the labels at its top.
                visual.Tick.Y1 = height - AxisTickLength;
                visual.Tick.Y2 = height;
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
                    StrokeThickness = 1
                };
                tick.SetResourceReference(Shape.StrokeProperty, "TextSecondaryBrush");
                var label = new TextBlock();
                label.SetResourceReference(TextBlock.FontSizeProperty, "TimelineAxisFontSize");
                label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
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

        private sealed class BarVisual(Border root, TextBlock label)
        {
            internal Border Root { get; } = root;
            internal TextBlock Label { get; } = label;
            internal ProxyConnectionModel Model { get; set; }
            internal long Version { get; set; }
            // The status class the bar's colors are for, as charts color them (StatusMeanings.ChartFill).
            internal TrafficStatusClass? Status { get; set; }
        }

        private sealed record AxisVisual(Line Tick, TextBlock Label);
    }
}
