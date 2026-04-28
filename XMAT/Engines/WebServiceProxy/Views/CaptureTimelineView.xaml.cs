// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using XMAT.WebServiceCapture.Models;

namespace XMAT.WebServiceCapture
{
    /// <summary>
    /// Waterfall / By-Host timeline view for HTTP proxy requests.
    /// </summary>
    public partial class CaptureTimelineView : UserControl
    {
        private const double BarHeight = 14;
        private const double BarGap = 2;
        private const double RowHeight = BarHeight + BarGap;
        private const double HostLabelHeight = 18;
        private const double MinBarWidth = 3;
        private const double PixelsPerMs = 0.5;
        private const double MinCanvasWidth = 200;

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
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        public ProxyConnectionModel SelectedItem
        {
            get => (ProxyConnectionModel)GetValue(SelectedItemProperty);
            set => SetValue(SelectedItemProperty, value);
        }

        private bool _isWaterfall = true;

        public CaptureTimelineView()
        {
            InitializeComponent();
        }

        private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var view = (CaptureTimelineView)d;
            if (e.OldValue is INotifyCollectionChanged oldCol)
                oldCol.CollectionChanged -= view.OnCollectionChanged;
            if (e.NewValue is INotifyCollectionChanged newCol)
                newCol.CollectionChanged += view.OnCollectionChanged;
            view.RedrawTimeline();
        }

        private void OnCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(RedrawTimeline));
        }

        private void ViewMode_Changed(object sender, RoutedEventArgs e)
        {
            _isWaterfall = WaterfallMode.IsChecked == true;
            RedrawTimeline();
        }

        private void RedrawTimeline()
        {
            if (TimelineCanvas == null || TimeAxisCanvas == null) return;

            TimelineCanvas.Children.Clear();
            TimeAxisCanvas.Children.Clear();

            var items = ItemsSource;
            if (items == null || items.Count == 0)
            {
                TimelineCanvas.Width = MinCanvasWidth;
                TimelineCanvas.Height = 0;
                TimeAxisCanvas.Width = MinCanvasWidth;
                return;
            }

            var valid = items.Where(i => i.RequestTime != DateTime.MinValue).ToList();
            if (valid.Count == 0) return;

            var firstTime = valid.Min(i => i.RequestTime);
            var lastEnd = valid.Max(i => i.RequestTime + i.Duration);
            double totalMs = Math.Max((lastEnd - firstTime).TotalMilliseconds, 1);
            double canvasW = Math.Max(totalMs * PixelsPerMs, MinCanvasWidth);

            if (_isWaterfall)
                DrawWaterfall(valid, firstTime, canvasW, totalMs);
            else
                DrawByHost(valid, firstTime, canvasW, totalMs);

            DrawTimeAxis(canvasW, totalMs);
        }

        #region Waterfall layout

        private void DrawWaterfall(List<ProxyConnectionModel> items, DateTime firstTime, double canvasW, double totalMs)
        {
            double y = 0;
            foreach (var item in items.OrderBy(i => i.RequestTime))
            {
                AddBar(item, firstTime, canvasW, totalMs, y);
                y += RowHeight;
            }
            TimelineCanvas.Width = canvasW;
            TimelineCanvas.Height = y;
        }

        #endregion

        #region By-Host layout

        private void DrawByHost(List<ProxyConnectionModel> items, DateTime firstTime, double canvasW, double totalMs)
        {
            var groups = items.GroupBy(i => i.Host)
                              .OrderBy(g => g.Min(i => i.RequestTime));
            double y = 0;
            var fgBrush = TryFindResource("PrimaryForegroundBrush") as Brush ?? Brushes.White;

            foreach (var group in groups)
            {
                var label = new TextBlock
                {
                    Text = group.Key,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 11,
                    Foreground = fgBrush,
                };
                Canvas.SetLeft(label, 4);
                Canvas.SetTop(label, y);
                TimelineCanvas.Children.Add(label);
                y += HostLabelHeight;

                foreach (var item in group.OrderBy(i => i.RequestTime))
                {
                    AddBar(item, firstTime, canvasW, totalMs, y);
                    y += RowHeight;
                }
                y += 4;
            }
            TimelineCanvas.Width = canvasW;
            TimelineCanvas.Height = y;
        }

        #endregion

        #region Bar rendering

        private void AddBar(ProxyConnectionModel item, DateTime firstTime, double canvasW, double totalMs, double y)
        {
            double offsetMs = (item.RequestTime - firstTime).TotalMilliseconds;
            double durMs = item.Duration.TotalMilliseconds;
            double x = (offsetMs / totalMs) * canvasW;
            double w = Math.Max((durMs / totalMs) * canvasW, MinBarWidth);

            var bar = new Border
            {
                Width = w,
                Height = BarHeight,
                Background = new SolidColorBrush(StatusColor(item.Status)),
                CornerRadius = new CornerRadius(2),
                Cursor = Cursors.Hand,
                ToolTip = $"{item.Method} {item.Host}{item.Path}\n" +
                          $"Status: {item.Status}\n" +
                          $"Time: {item.RequestTime:HH:mm:ss.fff}\n" +
                          $"Duration: {item.Duration.TotalMilliseconds:F0} ms",
                Tag = item,
            };

            if (w > 60)
            {
                bar.Child = new TextBlock
                {
                    Text = $"{item.Method} {item.Path}",
                    FontSize = 9,
                    Foreground = Brushes.White,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(3, 0, 3, 0),
                };
            }

            bar.MouseLeftButtonDown += Bar_Click;
            Canvas.SetLeft(bar, x);
            Canvas.SetTop(bar, y);
            TimelineCanvas.Children.Add(bar);
        }

        private void Bar_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is ProxyConnectionModel model)
            {
                SelectedItem = model;
            }
        }

        #endregion

        #region Time axis

        private void DrawTimeAxis(double canvasW, double totalMs)
        {
            TimeAxisCanvas.Width = canvasW;
            var fgBrush = TryFindResource("SecondaryForegroundBrush") as Brush ?? Brushes.Gray;
            double tickInterval = NiceTickInterval(totalMs, canvasW);

            for (double ms = 0; ms <= totalMs; ms += tickInterval)
            {
                double x = (ms / totalMs) * canvasW;

                var tick = new Line
                {
                    X1 = x, Y1 = 14,
                    X2 = x, Y2 = 20,
                    Stroke = fgBrush,
                    StrokeThickness = 1,
                };
                TimeAxisCanvas.Children.Add(tick);

                string text = totalMs > 60000
                    ? TimeSpan.FromMilliseconds(ms).ToString(@"m\:ss\.f")
                    : totalMs > 1000
                        ? $"{ms / 1000:F1}s"
                        : $"{ms:F0}ms";

                var lbl = new TextBlock
                {
                    Text = text,
                    FontSize = 9,
                    Foreground = fgBrush,
                };
                Canvas.SetLeft(lbl, x + 2);
                Canvas.SetTop(lbl, 0);
                TimeAxisCanvas.Children.Add(lbl);
            }
        }

        private static double NiceTickInterval(double totalMs, double canvasW)
        {
            double target = 80;
            double raw = totalMs / (canvasW / target);
            double[] nice = { 1, 2, 5, 10, 20, 50, 100, 200, 500,
                              1000, 2000, 5000, 10000, 30000, 60000 };
            foreach (var n in nice)
                if (n >= raw) return n;
            return raw;
        }

        #endregion

        #region Status colors

        private static Color StatusColor(string status)
        {
            if (string.IsNullOrEmpty(status) || !int.TryParse(status, out int code))
                return Color.FromRgb(0x68, 0x68, 0x68);

            return code switch
            {
                >= 200 and < 300 => Color.FromRgb(0x4C, 0xAF, 0x50),
                >= 300 and < 400 => Color.FromRgb(0x21, 0x96, 0xF3),
                >= 400 and < 500 => Color.FromRgb(0xFF, 0x98, 0x00),
                >= 500           => Color.FromRgb(0xF4, 0x43, 0x36),
                _                => Color.FromRgb(0x68, 0x68, 0x68),
            };
        }

        #endregion
    }
}
