// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using XMAT.Models;

namespace XMAT
{
    // A one-line summary above the capture timeline of the rows the traffic table shows: how many requests and how fast
    // they arrive, how many fall in each status class, and the busiest hosts. ItemsSource gives it those rows; while
    // IsCapturing, it refreshes once a second and its numbers change in place. Selecting a status class or a host raises
    // FilterRequested, and the view that hosts the overview sets its filters; the overview never changes them itself. The
    // button at its right hides it, and every overview follows that saved choice.
    public partial class TrafficOverview : UserControl
    {
        // The 140px requests line keeps its points at least 3px apart.
        internal const int MaxPoints = 46;

        public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
            nameof(ItemsSource), typeof(IEnumerable), typeof(TrafficOverview),
            new PropertyMetadata(null, (overview, _) => ((TrafficOverview)overview).ItemsSourceChanged()));

        public static readonly DependencyProperty IsCapturingProperty = DependencyProperty.Register(
            nameof(IsCapturing), typeof(bool), typeof(TrafficOverview),
            new PropertyMetadata(false, (overview, _) => ((TrafficOverview)overview).CapturingChanged()));

        public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
            nameof(IsExpanded), typeof(bool), typeof(TrafficOverview),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                (overview, _) => ((TrafficOverview)overview).ExpandedChanged()));

        public static readonly RoutedEvent FilterRequestedEvent = EventManager.RegisterRoutedEvent(
            nameof(FilterRequested), RoutingStrategy.Bubble, typeof(EventHandler<TrafficFilterRequestedEventArgs>),
            typeof(TrafficOverview));

        private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

        private readonly TrafficOverviewState _state = new();
        private readonly DispatcherTimer _timer;
        private INotifyCollectionChanged _observedRows;
        private DispatcherOperation _pendingRefresh;
        private bool _active;
        // The status classes shown so far while capturing, so that a class whose requests are all in flight keeps its place.
        private int _shownClasses;

        public TrafficOverview()
        {
            InitializeComponent();
            Strip.DataContext = _state;
            _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = OneSecond };
            _timer.Tick += (_, _) => Refresh();

            AutomationProperties.SetName(this, Localization.GetLocalizedString("TRAFFIC_OVERVIEW"));
            RequestsLabel.Text = Localization.GetLocalizedString("TRAFFIC_OVERVIEW_REQUESTS");
            StatusLabel.Text = Localization.GetLocalizedString("TRAFFIC_OVERVIEW_STATUS");
            CompactStatusLabel.Text = StatusLabel.Text;
            StatusTextLabel.Text = StatusLabel.Text;
            HostsLabel.Text = Localization.GetLocalizedString("TRAFFIC_OVERVIEW_HOSTS");
            AutomationProperties.SetName(StatusLegend, StatusLabel.Text);
            AutomationProperties.SetName(CompactLegend, StatusLabel.Text);
            AutomationProperties.SetName(StatusText, StatusLabel.Text);
            AutomationProperties.SetName(HostList, HostsLabel.Text);
            string hide = Localization.GetLocalizedString("TRAFFIC_OVERVIEW_HIDE");
            AutomationProperties.SetName(HideButton, hide);
            HideButton.ToolTip = hide;
            ShowButton.Content = Localization.GetLocalizedString("TRAFFIC_OVERVIEW_SHOW");

            foreach (TrafficStatusBar bar in new[] { StatusBar, CompactBar })
            {
                bar.DescribeSegment = statusClass => _state.PartOf(statusClass).ToolTip;
                bar.CanSelectSegment = statusClass => _state.PartOf(statusClass).CanSelect;
                bar.SegmentSelected += (_, statusClass) => RequestFilter(_state.PartOf(statusClass));
            }

            // A legend item that is hovered or focused outlines its segment of the bar.
            foreach (ItemsControl legend in new[] { StatusLegend, CompactLegend })
            {
                legend.MouseMove += (_, _) => UpdateHighlight();
                legend.MouseLeave += (_, _) => UpdateHighlight();
                legend.GotKeyboardFocus += (_, _) => UpdateHighlight();
                legend.LostKeyboardFocus += (_, _) => UpdateHighlight();
            }

            Cells.ReductionChanged += (_, _) => KeepFocusInView();

            Loaded += (_, _) =>
            {
                // Loaded can come again without Unloaded; the overview follows the choice once.
                ShownPreferenceChanged -= FollowShownPreference;
                ShownPreferenceChanged += FollowShownPreference;
                SetCurrentValue(IsExpandedProperty, ReadShownPreference());
                UpdateActivity();
            };
            Unloaded += (_, _) =>
            {
                ShownPreferenceChanged -= FollowShownPreference;
                UpdateActivity();
            };
            IsVisibleChanged += (_, _) => UpdateActivity();
        }

        private static event EventHandler ShownPreferenceChanged;

        // Selecting a status class or a host asks the hosting view to filter the table to it.
        public event EventHandler<TrafficFilterRequestedEventArgs> FilterRequested
        {
            add => AddHandler(FilterRequestedEvent, value);
            remove => RemoveHandler(FilterRequestedEvent, value);
        }

        // The rows the table shows, after every filter; a collection view or another INotifyCollectionChanged tells
        // when they change.
        public IEnumerable ItemsSource
        {
            get => (IEnumerable)GetValue(ItemsSourceProperty);
            set => SetValue(ItemsSourceProperty, value);
        }

        public bool IsCapturing
        {
            get => (bool)GetValue(IsCapturingProperty);
            set => SetValue(IsCapturingProperty, value);
        }

        // Whether the overview is shown or folded to its Show overview button. Loading takes the saved choice.
        public bool IsExpanded
        {
            get => (bool)GetValue(IsExpandedProperty);
            set => SetValue(IsExpandedProperty, value);
        }

        // The saved choice of showing the overview, which every capture tab shares; tests replace how it is read and saved.
        internal static Func<bool> ReadShownPreference { get; set; } =
            () => CaptureAppModel.AppModel.PreferencesModel?.ShowTrafficOverview ?? true;

        internal static Action<bool> SaveShownPreference { get; set; } = SavePreference;

        internal Func<DateTime> Clock { get; set; } = () => DateTime.Now;

        internal TrafficOverviewState State => _state;

        internal TrafficSummary Summary { get; private set; } = TrafficSummary.Empty;

        internal int RefreshCount { get; private set; }

        internal bool IsRefreshTimerRunning => _timer.IsEnabled;

        internal TimeSpan RefreshInterval
        {
            get => _timer.Interval;
            set => _timer.Interval = value;
        }

        internal void Tick() => Refresh();

        protected override AutomationPeer OnCreateAutomationPeer() => new TrafficOverviewAutomationPeer(this);

        private static void SavePreference(bool shown)
        {
            AppPreferencesModel preferences = CaptureAppModel.AppModel.PreferencesModel;
            if (preferences != null && preferences.ShowTrafficOverview != shown)
            {
                preferences.ShowTrafficOverview = shown;
                CaptureAppSettings.Serialize(CaptureAppModel.AppModel);
            }
        }

        private void FollowShownPreference(object sender, EventArgs e)
        {
            if (!ReferenceEquals(sender, this))
            {
                SetCurrentValue(IsExpandedProperty, ReadShownPreference());
            }
        }

        private void Hide_Click(object sender, RoutedEventArgs e) => SetShown(false);

        private void Show_Click(object sender, RoutedEventArgs e) => SetShown(true);

        private void SetShown(bool shown)
        {
            bool hadFocus = IsKeyboardFocusWithin;
            SetCurrentValue(IsExpandedProperty, shown);
            SaveShownPreference(shown);
            ShownPreferenceChanged?.Invoke(this, EventArgs.Empty);
            if (hadFocus)
            {
                // Focus moves to the button that undoes the change, which replaced the one that made it.
                Button next = shown ? HideButton : ShowButton;
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => next.Focus()));
            }
        }

        private void Part_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: TrafficOverviewPart part })
            {
                RequestFilter(part);
            }
        }

        private void RequestFilter(TrafficOverviewPart part)
        {
            if (part.CanSelect)
            {
                RaiseEvent(new TrafficFilterRequestedEventArgs(FilterRequestedEvent, this, part.StatusClass, part.Host));
            }
        }

        private void ExpandedChanged()
        {
            Strip.Visibility = IsExpanded ? Visibility.Visible : Visibility.Collapsed;
            ShowButton.Visibility = IsExpanded ? Visibility.Collapsed : Visibility.Visible;
            UpdateActivity();
        }

        // The overview counts and observes the rows only while it is loaded, visible and expanded.
        private void UpdateActivity()
        {
            bool active = IsLoaded && IsVisible && IsExpanded;
            if (active != _active)
            {
                _active = active;
                if (active)
                {
                    ObserveRows(ItemsSource as INotifyCollectionChanged);
                    _shownClasses = 0;
                    Refresh();
                }
                else
                {
                    ObserveRows(null);
                    _pendingRefresh?.Abort();
                    _pendingRefresh = null;
                }
            }

            UpdateTimer();
        }

        private void UpdateTimer()
        {
            if (_active && IsCapturing)
            {
                _timer.IsEnabled = true;
            }
            else
            {
                _timer.Stop();
            }
        }

        private void ItemsSourceChanged()
        {
            if (_active)
            {
                ObserveRows(ItemsSource as INotifyCollectionChanged);
                _shownClasses = 0;
                RequestRefresh();
            }
        }

        private void CapturingChanged()
        {
            UpdateTimer();
            if (_active)
            {
                // Once stopped, the overview shows exactly the classes that have requests.
                _shownClasses = 0;
                RequestRefresh();
            }
        }

        private void ObserveRows(INotifyCollectionChanged rows)
        {
            if (_observedRows != null)
            {
                _observedRows.CollectionChanged -= Rows_CollectionChanged;
            }

            _observedRows = rows;
            if (rows != null)
            {
                rows.CollectionChanged += Rows_CollectionChanged;
            }
        }

        // While capturing, new rows wait for the next refresh; a new set of rows, as after a filter changes, shows at once.
        private void Rows_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (!ReferenceEquals(sender, _observedRows))
            {
                return;
            }

            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                _shownClasses = 0;
                RequestRefresh();
            }
            else if (!IsCapturing)
            {
                RequestRefresh();
            }
        }

        private void RequestRefresh()
        {
            if (_active && _pendingRefresh?.Status != DispatcherOperationStatus.Pending)
            {
                _pendingRefresh = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Refresh));
            }
        }

        private void Refresh()
        {
            _pendingRefresh = null;
            if (!_active)
            {
                return;
            }

            TrafficSummary summary;
            try
            {
                summary = TrafficSummary.Compute(ItemsSource, IsCapturing, Clock(), MaxPoints);
            }
            catch (InvalidOperationException)
            {
                // The rows' view is being refreshed; its own notification or the next tick brings the overview up to date.
                return;
            }

            Summary = summary;
            RefreshCount++;
            int present = 0;
            for (int index = 0; index < TrafficSummary.ClassCount; index++)
            {
                present |= summary.ClassCounts[index] > 0 ? 1 << index : 0;
            }

            _shownClasses = (IsCapturing ? _shownClasses : 0) | present;
            _state.Apply(summary, _shownClasses);
            UpdateHighlight();
        }

        private void UpdateHighlight()
        {
            TrafficOverviewPart part = null;
            if (StatusLegend.IsMouseOver || CompactLegend.IsMouseOver)
            {
                part = PartUnder(Mouse.DirectlyOver);
            }

            if (part == null && (StatusLegend.IsKeyboardFocusWithin || CompactLegend.IsKeyboardFocusWithin))
            {
                part = PartUnder(Keyboard.FocusedElement);
            }

            int highlighted = part?.StatusClass is { } statusClass ? (int)statusClass : -1;
            StatusBar.HighlightedClass = highlighted;
            CompactBar.HighlightedClass = highlighted;
        }

        // The part an element belongs to. Over a legend item's label the pointer is over one of its runs, which is a
        // content element rather than a framework element.
        internal static TrafficOverviewPart PartUnder(IInputElement element) =>
            ((element as FrameworkElement)?.DataContext ?? (element as FrameworkContentElement)?.DataContext) as TrafficOverviewPart;

        // When a narrower overview hides the part that has focus, focus moves to the same part in what is shown, or to
        // the first part of the status.
        private void KeepFocusInView()
        {
            if (!IsKeyboardFocusWithin || (Keyboard.FocusedElement as UIElement)?.IsVisible != false)
            {
                return;
            }

            var focused = (Keyboard.FocusedElement as FrameworkElement)?.DataContext as TrafficOverviewPart;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                ItemsControl status = StatusChart.IsVisible ? StatusLegend
                    : CompactChart.IsVisible ? CompactLegend
                    : StatusText;
                IEnumerable<Button> parts = Descendants(status).OfType<Button>().Where(button => button.IsVisible).ToList();
                (parts.FirstOrDefault(button => ReferenceEquals(button.DataContext, focused)) ?? parts.FirstOrDefault())
                    ?.Focus();
            }));
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for (int index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
                yield return child;
                foreach (DependencyObject descendant in Descendants(child))
                {
                    yield return descendant;
                }
            }
        }

        // Screen readers find the requests as one line of text, the status classes and the busiest hosts as lists, and
        // the button that hides or shows the overview; the parts that are hidden at the current width are left out.
        private sealed class TrafficOverviewAutomationPeer : UserControlAutomationPeer
        {
            internal TrafficOverviewAutomationPeer(TrafficOverview owner) : base(owner)
            {
            }

            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

            protected override string GetClassNameCore() => nameof(TrafficOverview);

            protected override List<AutomationPeer> GetChildrenCore()
            {
                var owner = (TrafficOverview)Owner;
                return new UIElement[]
                    {
                        owner.TotalText, owner.StatusLegend, owner.CompactLegend, owner.StatusText, owner.HostList,
                        owner.HideButton, owner.ShowButton,
                    }
                    .Where(element => element.IsVisible)
                    .Select(CreatePeerForElement)
                    .Where(peer => peer != null)
                    .ToList();
            }
        }
    }

    // A request to filter the traffic table to one status class or one host.
    public sealed class TrafficFilterRequestedEventArgs : RoutedEventArgs
    {
        internal TrafficFilterRequestedEventArgs(
            RoutedEvent routedEvent, object source, TrafficStatusClass? statusClass, string host)
            : base(routedEvent, source)
        {
            StatusClass = statusClass;
            Host = host;
        }

        // The status class to show, or null for a host.
        public TrafficStatusClass? StatusClass { get; }

        // The host to show, or null for a status class.
        public string Host { get; }

        protected override void InvokeEventHandler(Delegate genericHandler, object genericTarget) =>
            ((EventHandler<TrafficFilterRequestedEventArgs>)genericHandler)(genericTarget, this);
    }

    // One part of the overview that has a count: a status class, or one of the busiest hosts. A part keeps its place and
    // changes its values in place as the counts change.
    public sealed class TrafficOverviewPart : INotifyPropertyChanged
    {
        private string _label;
        private string _countText;
        private double _share;
        private string _name;
        private string _toolTip;
        private bool _showSeparator;

        internal TrafficOverviewPart(TrafficStatusClass statusClass)
        {
            StatusClass = statusClass;
            CanSelect = statusClass != TrafficStatusClass.None;
        }

        internal TrafficOverviewPart()
        {
            CanSelect = true;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        // The status class of a status part; null for a host.
        public TrafficStatusClass? StatusClass { get; }

        // The host of a host part.
        public string Host { get; private set; }

        // Whether selecting it filters the table; requests without a status can't be filtered.
        public bool CanSelect { get; }

        public string Label { get => _label; private set => Set(ref _label, value); }

        public string CountText { get => _countText; private set => Set(ref _countText, value); }

        // A host's count against the busiest host's.
        public double Share { get => _share; private set => Set(ref _share, value); }

        // What screen readers read, such as "4xx: 63 requests, 5 percent".
        public string Name { get => _name; private set => Set(ref _name, value); }

        public string ToolTip { get => _toolTip; private set => Set(ref _toolTip, value); }

        // Whether the status as text puts a separator before this part.
        public bool ShowSeparator { get => _showSeparator; internal set => Set(ref _showSeparator, value); }

        internal int Count { get; private set; }

        public override string ToString() => Name;

        internal void Update(string label, string host, int count, int total, double share)
        {
            Host = host;
            Count = count;
            Label = label;
            CountText = count.ToString("N0", CultureInfo.CurrentCulture);
            Share = share;
            string described = host ?? label;
            string requests = count == 1
                ? Localization.GetLocalizedString("TRAFFIC_OVERVIEW_ONE_REQUEST")
                : Localization.GetLocalizedString("TRAFFIC_OVERVIEW_REQUEST_COUNT", CountText);
            // Rounded as the column summaries round, so a part short of every request never reads 100 percent.
            (int percent, bool underOne) = ColumnSummaryCounts.Percent(count, total);
            Name = Localization.GetLocalizedString(
                "TRAFFIC_OVERVIEW_PART_NAME", described, requests,
                underOne
                    ? Localization.GetLocalizedString("TRAFFIC_OVERVIEW_UNDER_ONE_PERCENT")
                    : Localization.GetLocalizedString("TRAFFIC_OVERVIEW_PERCENT", percent));
            string tip = Localization.GetLocalizedString(
                "TRAFFIC_OVERVIEW_PART_TIP", described, requests, underOne ? "<1" : percent.ToString(CultureInfo.CurrentCulture));
            ToolTip = CanSelect ? tip + " " + Localization.GetLocalizedString("TRAFFIC_OVERVIEW_SELECT_TIP") : tip;
        }

        private void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (!EqualityComparer<T>.Default.Equals(field, value))
            {
                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
        }
    }

    // What the overview shows, from the latest summary. Its parts are created once and updated in place.
    internal sealed class TrafficOverviewState : INotifyPropertyChanged
    {
        private static readonly string[] ClassLabelKeys =
        {
            "TRAFFIC_OVERVIEW_2XX", "TRAFFIC_OVERVIEW_3XX", "TRAFFIC_OVERVIEW_4XX", "TRAFFIC_OVERVIEW_5XX",
            "TRAFFIC_OVERVIEW_NO_STATUS",
        };

        // The status as text shows successes and errors.
        private static readonly TrafficStatusClass[] TextClasses =
        {
            TrafficStatusClass.Success, TrafficStatusClass.ClientError, TrafficStatusClass.ServerError,
        };

        private readonly TrafficOverviewPart[] _classParts = Enumerable.Range(0, TrafficSummary.ClassCount)
            .Select(index => new TrafficOverviewPart((TrafficStatusClass)index)).ToArray();
        private readonly TrafficOverviewPart[] _hostParts = Enumerable.Range(0, TrafficSummary.BusiestHostCount)
            .Select(_ => new TrafficOverviewPart()).ToArray();
        private string _totalText = "0";
        private string _rateText;
        private bool _isRateShown;
        private string _requestsName;
        private IReadOnlyList<double> _requestsPerSecond = Array.Empty<double>();
        private IReadOnlyList<int> _classCounts = new int[TrafficSummary.ClassCount];
        private IReadOnlyList<int> _compactClassCounts = new int[TrafficSummary.ClassCount];

        public event PropertyChangedEventHandler PropertyChanged;

        public string TotalText { get => _totalText; private set => Set(ref _totalText, value); }

        public string RateText { get => _rateText; private set => Set(ref _rateText, value); }

        public bool IsRateShown { get => _isRateShown; private set => Set(ref _isRateShown, value); }

        // What screen readers read for the requests, such as "Requests: 1,284, 38 per second".
        public string RequestsName { get => _requestsName; private set => Set(ref _requestsName, value); }

        public IReadOnlyList<double> RequestsPerSecond { get => _requestsPerSecond; private set => Set(ref _requestsPerSecond, value); }

        public IReadOnlyList<int> ClassCounts { get => _classCounts; private set => Set(ref _classCounts, value); }

        // The counts without No status, for the chart that leaves it out.
        public IReadOnlyList<int> CompactClassCounts
        {
            get => _compactClassCounts;
            private set => Set(ref _compactClassCounts, value);
        }

        // The legend: the status classes with requests, in class order.
        public ObservableCollection<TrafficOverviewPart> StatusParts { get; } = new();

        // The legend without No status.
        public ObservableCollection<TrafficOverviewPart> CompactParts { get; } = new();

        // The status as text.
        public ObservableCollection<TrafficOverviewPart> TextParts { get; } = new();

        public ObservableCollection<TrafficOverviewPart> HostParts { get; } = new();

        internal TrafficOverviewPart PartOf(TrafficStatusClass statusClass) => _classParts[(int)statusClass];

        // SHOWNCLASSES has a bit for each status class the legend shows.
        internal void Apply(TrafficSummary summary, int shownClasses)
        {
            string total = summary.Total.ToString("N0", CultureInfo.CurrentCulture);
            TotalText = total;
            IsRateShown = summary.LastSecondCount != null;
            string rate = summary.LastSecondCount?.ToString("N0", CultureInfo.CurrentCulture);
            RateText = rate == null ? null : Localization.GetLocalizedString("TRAFFIC_OVERVIEW_RATE", rate);
            RequestsName = rate == null
                ? Localization.GetLocalizedString("TRAFFIC_OVERVIEW_REQUESTS_NAME", total)
                : Localization.GetLocalizedString("TRAFFIC_OVERVIEW_REQUESTS_RATE_NAME", total, rate);
            RequestsPerSecond = summary.RequestsPerSecond;
            ClassCounts = summary.ClassCounts;
            int[] compact = summary.ClassCounts.ToArray();
            compact[(int)TrafficStatusClass.None] = 0;
            CompactClassCounts = compact;

            var legend = new List<TrafficOverviewPart>();
            for (int index = 0; index < TrafficSummary.ClassCount; index++)
            {
                _classParts[index].Update(
                    Localization.GetLocalizedString(ClassLabelKeys[index]), null, summary.ClassCounts[index], summary.Total, 0);
                if ((shownClasses & (1 << index)) != 0)
                {
                    legend.Add(_classParts[index]);
                }
            }

            Sync(StatusParts, legend);
            Sync(CompactParts, legend.Where(part => part.StatusClass != TrafficStatusClass.None).ToList());
            var text = legend.Where(part => TextClasses.Contains(part.StatusClass.Value)).ToList();
            Sync(TextParts, text.Count > 0 ? text : legend);
            for (int index = 0; index < TextParts.Count; index++)
            {
                TextParts[index].ShowSeparator = index > 0;
            }

            var shownHosts = summary.BusiestHosts.Select(entry => entry.Host).ToList();
            int busiest = summary.BusiestHosts.Count > 0 ? summary.BusiestHosts[0].Count : 0;
            for (int index = 0; index < summary.BusiestHosts.Count; index++)
            {
                (string host, int count) = summary.BusiestHosts[index];
                _hostParts[index].Update(
                    TrafficSummary.ShortHostName(host, shownHosts), host, count, summary.Total, (double)count / busiest);
            }

            Sync(HostParts, _hostParts.Take(summary.BusiestHosts.Count).ToList());
        }

        // Makes TARGET list DESIRED in order, keeping the parts it already lists, so their items keep focus and place.
        private static void Sync(ObservableCollection<TrafficOverviewPart> target, IList<TrafficOverviewPart> desired)
        {
            for (int index = target.Count - 1; index >= 0; index--)
            {
                if (!desired.Contains(target[index]))
                {
                    target.RemoveAt(index);
                }
            }

            for (int index = 0; index < desired.Count; index++)
            {
                if (index < target.Count && ReferenceEquals(target[index], desired[index]))
                {
                    continue;
                }

                int existing = target.IndexOf(desired[index]);
                if (existing >= 0)
                {
                    target.Move(existing, index);
                }
                else
                {
                    target.Insert(index, desired[index]);
                }
            }
        }

        private void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (!EqualityComparer<T>.Default.Equals(field, value))
            {
                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
        }
    }

    // Picks the template for a part: one that can be selected, or one that only informs.
    public sealed class TrafficPartTemplateSelector : DataTemplateSelector
    {
        public DataTemplate Selectable { get; set; }

        public DataTemplate Information { get; set; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container) =>
            item is TrafficOverviewPart { CanSelect: false } ? Information : Selectable;
    }
}
