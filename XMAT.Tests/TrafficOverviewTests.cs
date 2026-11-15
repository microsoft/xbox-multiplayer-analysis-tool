// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class TrafficOverviewTests
    {
        private static readonly DateTime Now = new(2026, 11, 14, 22, 30, 5, 300);
        private readonly CaptureUiApplicationFixture _application;

        public TrafficOverviewTests(CaptureUiApplicationFixture application) => _application = application;

        [Fact]
        public void Summary_CountsEachStatusClassAsTheTimelineColorsIt()
        {
            string[] statuses = { "200", "204", "301", "304", "404", "429", "500", "503", "600", string.Empty, "101", "abc" };
            var rows = statuses.Select((status, index) => Row(index + 1, status: status)).ToList();

            TrafficSummary summary = TrafficSummary.Compute(rows, false, Now, TrafficOverview.MaxPoints);

            Assert.Equal(12, summary.Total);
            // 2xx, 3xx, 4xx, 5xx and above, then no status, 1xx and anything else.
            Assert.Equal(new[] { 2, 2, 2, 3, 3 }, summary.ClassCounts);
        }

        [Fact]
        public void Summary_RanksTheThreeBusiestHosts_AndShortensTheirNames()
        {
            var rows = new List<ProxyConnectionModel>();
            foreach ((string host, int count) in new[]
                { ("a.example.com", 3), ("b.example.com", 5), ("c.example.com", 3), ("d.example.com", 1), (string.Empty, 4) })
            {
                rows.AddRange(Enumerable.Range(0, count).Select(_ => Row(rows.Count + 1, host: host)));
            }

            TrafficSummary summary = TrafficSummary.Compute(rows, false, Now, TrafficOverview.MaxPoints);

            Assert.Equal(new[] { ("b.example.com", 5), ("a.example.com", 3), ("c.example.com", 3) }, summary.BusiestHosts);
            Assert.Equal("b", TrafficSummary.ShortHostName("b.example.com", new[] { "b.example.com", "a.example.com" }));
            // Hosts that start alike, and addresses, keep their full names.
            string[] titles = { "title.mgt.xboxlive.com", "title.auth.xboxlive.com" };
            Assert.Equal("title.mgt.xboxlive.com", TrafficSummary.ShortHostName(titles[0], titles));
            Assert.Equal("10.0.0.5", TrafficSummary.ShortHostName("10.0.0.5", new[] { "10.0.0.5" }));
        }

        [Fact]
        public void Summary_RateIsTheLastFullSecond_AndTheLineEndsThereWhileCapturing()
        {
            DateTime second = Now.Date.AddHours(Now.Hour).AddMinutes(Now.Minute);
            var rows = new List<ProxyConnectionModel>();
            foreach ((int at, int count) in new[] { (1, 3), (3, 2), (4, 4), (5, 1) })
            {
                rows.AddRange(Enumerable.Range(0, count)
                    .Select(index => Row(rows.Count + 1, second.AddSeconds(at).AddMilliseconds(100 * index))));
            }

            TrafficSummary capturing = TrafficSummary.Compute(rows, true, Now, TrafficOverview.MaxPoints);
            TrafficSummary stopped = TrafficSummary.Compute(rows, false, Now, TrafficOverview.MaxPoints);

            // At 22:30:05.3 the last full second is 22:30:04; the request in the current second counts but isn't drawn yet.
            Assert.Equal(4, capturing.LastSecondCount);
            Assert.Equal(10, capturing.Total);
            Assert.Equal(new double[] { 3, 0, 2, 4 }, capturing.RequestsPerSecond);
            Assert.Null(stopped.LastSecondCount);
            Assert.Equal(new double[] { 3, 0, 2, 4, 1 }, stopped.RequestsPerSecond);

            // A long capture averages neighboring seconds, ending with the newest.
            var steady = Enumerable.Range(0, 100).Select(index => Row(index + 1, second.AddSeconds(index))).ToList();
            steady.AddRange(Enumerable.Range(0, 6).Select(index => Row(200 + index, second.AddSeconds(99))));
            IReadOnlyList<double> line = TrafficSummary.Compute(steady, false, Now, TrafficOverview.MaxPoints).RequestsPerSecond;
            Assert.Equal(34, line.Count);
            Assert.Equal(3, line[^1]);
            Assert.All(line.Take(33), value => Assert.Equal(1, value));
        }

        [Fact]
        public void NarrowWidths_DropNoStatusThenTheHostsThenShowTheStatusAsText()
        {
            // Requests 213, chart 290 or 230 without No status, hosts 239 with 8 kept for the hide button.
            Assert.Equal(TrafficOverviewReduction.None, TrafficOverviewPanel.Reduce(750, 213, 290, 230, 239, 8));
            Assert.Equal(TrafficOverviewReduction.WithoutNoStatus, TrafficOverviewPanel.Reduce(700, 213, 290, 230, 239, 8));
            Assert.Equal(TrafficOverviewReduction.WithoutHosts, TrafficOverviewPanel.Reduce(600, 213, 290, 230, 239, 8));
            Assert.Equal(TrafficOverviewReduction.WithoutHostsOrNoStatus, TrafficOverviewPanel.Reduce(450, 213, 290, 230, 239, 8));
            Assert.Equal(TrafficOverviewReduction.StatusAsText, TrafficOverviewPanel.Reduce(400, 213, 290, 230, 239, 8));

            _application.Run(window =>
            {
                window.Width = 1200;
                TrafficOverview overview = Show(window, SampleRows(), 1000);
                Assert.Equal(TrafficOverviewReduction.None, overview.Cells.Reduction);
                Assert.True(overview.HostList.IsVisible);
                Assert.Contains(overview.StatusLegend.Items.Cast<TrafficOverviewPart>(),
                    part => part.StatusClass == TrafficStatusClass.None);

                ((FrameworkElement)overview.Parent).Width = 380;
                WpfTestHost.Pump();
                Assert.Equal(TrafficOverviewReduction.StatusAsText, overview.Cells.Reduction);
                Assert.False(overview.HostList.IsVisible);
                Assert.False(overview.StatusBar.IsVisible);
                Assert.True(overview.StatusText.IsVisible);
                // Successes and errors, as in "2xx 1,102 · 4xx 63 · 5xx 14".
                Assert.Equal(new[] { "2xx", "4xx", "5xx" },
                    overview.StatusText.Items.Cast<TrafficOverviewPart>().Select(part => part.Label));
                Assert.Equal(new[] { false, true, true },
                    overview.StatusText.Items.Cast<TrafficOverviewPart>().Select(part => part.ShowSeparator));
            });
        }

        [Fact]
        public void SelectingAStatusClassOrAHost_RequestsAFilter_FromTheMouseOrTheKeyboard()
        {
            _application.Run(window =>
            {
                window.Width = 1200;
                TrafficOverview overview = Show(window, SampleRows(), 1000);
                var requests = new List<(TrafficStatusClass?, string)>();
                overview.FilterRequested += (_, e) => requests.Add((e.StatusClass, e.Host));

                Part(overview.StatusLegend, TrafficStatusClass.ClientError).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Button host = Descendants(overview.HostList).OfType<Button>().First();
                host.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(new (TrafficStatusClass?, string)[]
                    { (TrafficStatusClass.ClientError, null), (null, "sessiondirectory.xboxlive.com") }, requests);

                // No status only informs: it can't filter, so it is no button.
                Assert.DoesNotContain(Descendants(overview.StatusLegend).OfType<Button>(),
                    button => ((TrafficOverviewPart)button.DataContext).StatusClass == TrafficStatusClass.None);

                // Over a legend item's label the pointer is over one of its text runs, which still finds the item.
                Button clientError = Part(overview.StatusLegend, TrafficStatusClass.ClientError);
                System.Windows.Documents.Run run = Descendants(clientError).OfType<TextBlock>()
                    .SelectMany(text => text.Inlines.OfType<System.Windows.Documents.Run>()).First();
                Assert.Same(clientError.DataContext, TrafficOverview.PartUnder(run));

                // Tab enters the status classes once, the arrow keys move within them and Enter selects.
                requests.Clear();
                Button success = Part(overview.StatusLegend, TrafficStatusClass.Success);
                Assert.Equal(KeyboardNavigationMode.Once, KeyboardNavigation.GetTabNavigation(overview.StatusLegend));
                Assert.True(success.Focus());
                Assert.True(success.MoveFocus(new TraversalRequest(FocusNavigationDirection.Right)));
                Button redirect = Part(overview.StatusLegend, TrafficStatusClass.Redirect);
                Assert.Same(redirect, Keyboard.FocusedElement);
                Assert.Equal((int)TrafficStatusClass.Redirect, overview.StatusBar.HighlightedClass);
                redirect.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(redirect), 0, Key.Enter)
                {
                    RoutedEvent = Keyboard.KeyDownEvent
                });
                Assert.Equal(new (TrafficStatusClass?, string)[] { (TrafficStatusClass.Redirect, null) }, requests);
                Assert.True(redirect.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                Assert.Same(host, Keyboard.FocusedElement);
                Assert.True(host.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                Assert.Same(overview.HideButton, Keyboard.FocusedElement);
            });
        }

        [Fact]
        public void EveryPart_ReadsAsText_ForScreenReaders()
        {
            _application.Run(window =>
            {
                window.Width = 1200;
                TrafficOverview overview = Show(window, SampleRows(), 1000, capturing: true);
                var peer = (FrameworkElementAutomationPeer)UIElementAutomationPeer.CreatePeerForElement(overview);

                Assert.Equal("Traffic overview", peer.GetName());
                Assert.Equal(AutomationControlType.Group, peer.GetAutomationControlType());
                List<AutomationPeer> children = peer.GetChildren();
                Assert.Equal(
                    new[] { "Requests: 1,284, 38 per second", "Status", "Busiest hosts", "Hide overview" },
                    children.Select(child => child.GetName()));
                Assert.Equal(
                    new[]
                    {
                        "2xx: 1,102 requests, 86 percent", "3xx: 98 requests, 8 percent", "4xx: 63 requests, 5 percent",
                        "5xx: 14 requests, 1 percent", "No status: 7 requests, less than 1 percent",
                    },
                    children[1].GetChildren().Select(item => item.GetName()));
                Assert.Equal("sessiondirectory.xboxlive.com: 412 requests, 32 percent", children[2].GetChildren()[0].GetName());
                Assert.Equal("4xx: 63 requests (5%). Select to filter.", overview.State.PartOf(TrafficStatusClass.ClientError).ToolTip);
                Assert.Equal("No status: 7 requests (<1%).", overview.State.PartOf(TrafficStatusClass.None).ToolTip);
                // Every part shows its name and count as text, so color is never the only cue.
                Assert.All(overview.StatusLegend.Items.Cast<TrafficOverviewPart>(), part =>
                    Assert.Contains(Descendants(overview.StatusLegend).OfType<TextBlock>(),
                        text => text.DataContext == part && TextOf(text) == part.Label + " " + part.CountText));

                ProxyConnectionModel[] few = { Row(1, status: "200"), Row(2, status: "500") };
                few = few.Concat(Enumerable.Range(3, 200).Select(number => Row(number, status: "201"))).ToArray();
                overview.ItemsSource = few;
                overview.Tick();
                Assert.Equal("5xx: 1 request, less than 1 percent", overview.State.PartOf(TrafficStatusClass.ServerError).Name);
                // 201 of 202 requests is 99.5%, but a part short of every request never reads 100 percent.
                TrafficOverviewPart success = overview.State.PartOf(TrafficStatusClass.Success);
                Assert.Equal("2xx: 201 requests, 99 percent", success.Name);
                Assert.Equal("2xx: 201 requests (99%). Select to filter.", success.ToolTip);
            });
        }

        [Fact]
        public void WhileCapturing_TheOverviewRefreshesOnceASecond_AndChangesInPlace()
        {
            _application.Run(window =>
            {
                window.Width = 1200;
                var rows = new ObservableCollection<ProxyConnectionModel>(SampleRows());
                TrafficOverview overview = Show(window, rows, 1000, capturing: true);
                Assert.True(overview.IsRefreshTimerRunning);
                int refreshes = overview.RefreshCount;
                Button success = Part(overview.StatusLegend, TrafficStatusClass.Success);

                rows.Add(Row(5000, Now.AddMilliseconds(-200)));
                rows.Add(Row(5001, Now.AddMilliseconds(-100)));
                WpfTestHost.Pump();
                Assert.Equal(refreshes, overview.RefreshCount);
                Assert.Equal("1,284", overview.State.TotalText);

                overview.Tick();
                Assert.Equal(refreshes + 1, overview.RefreshCount);
                Assert.Equal("1,286", overview.State.TotalText);
                Assert.Equal("1,104", overview.State.PartOf(TrafficStatusClass.Success).CountText);
                Assert.Same(success, Part(overview.StatusLegend, TrafficStatusClass.Success));

                // Once stopped, changes show at once and nothing ticks.
                overview.IsCapturing = false;
                WpfTestHost.Pump();
                Assert.False(overview.IsRefreshTimerRunning);
                Assert.False(overview.State.IsRateShown);
                rows.Add(Row(5002, Now));
                WpfTestHost.Pump();
                Assert.Equal("1,287", overview.State.TotalText);
            });
        }

        [Fact]
        public void CaptureView_SetsTheFilterToTheSelectedPart_AndSelectingItAgainClearsIt()
        {
            _application.Run(window =>
            {
                window.Width = 1600;
                var controller = new WebServiceDeviceCaptureController("Overview test", DeviceType.Unknown, true);
                string[] statuses = { "200", "201", "404", "429", "503", "200" };
                for (int index = 0; index < statuses.Length; index++)
                {
                    controller.ProxyConnections.Add(Row(index + 1, status: statuses[index], host: index % 2 == 0 ? "a.test" : "b.test"));
                }
                var view = new CaptureConnectionView { AutoScroll = false };
                ((DataGrid)view.FindName("ConnectionList")).IsSynchronizedWithCurrentItem = false;
                view.DataContext = new Context(controller);
                WpfTestHost.Show(window, view);
                var overview = (TrafficOverview)view.FindName("OverviewStrip");
                var grid = (DataGrid)view.FindName("ConnectionList");
                var statusFilters = (CheckBoxFilters)view.FindName("StatusFilters");
                var hostFilters = (CheckBoxFilters)view.FindName("HostFilters");
                var filtersHost = (FilterOverflowHost)view.FindName("FiltersHost");
                // The Status dropdown's All item, which isn't one of the controller's values.
                var all = (CheckedListItem)((ComboBox)statusFilters.FindName("FilterList")).Items[0];
                Assert.Same(grid.ItemsSource, overview.ItemsSource);

                Request(overview, TrafficStatusClass.ClientError, null);
                Assert.True(statusFilters.IsFilterEnabled);
                Assert.Equal(new[] { "404", "429" }, controller.StatusFilterList.Where(item => item.IsChecked).Select(item => item.Text));
                Assert.Equal(new[] { "404", "429" }, grid.Items.Cast<ProxyConnectionModel>().Select(row => row.Status));
                // The dropdown shows the same choice, and a folded filter row counts the filter.
                Assert.False(all.IsChecked);
                Assert.Equal(1, filtersHost.ActiveFilterCount);
                // The overview counts the rows the table shows.
                Assert.Equal(2, overview.Summary.Total);
                Assert.Equal(new[] { 0, 0, 2, 0, 0 }, overview.Summary.ClassCounts);

                Request(overview, TrafficStatusClass.ClientError, null);
                Assert.False(statusFilters.IsFilterEnabled);
                Assert.All(controller.StatusFilterList, item => Assert.True(item.IsChecked));
                Assert.True(all.IsChecked);
                Assert.Equal(0, filtersHost.ActiveFilterCount);
                Assert.Equal(6, grid.Items.Count);
                Assert.Equal(6, overview.Summary.Total);

                Request(overview, null, "b.test");
                Assert.True(hostFilters.IsFilterEnabled);
                Assert.Equal(new[] { "b.test" }, controller.HostFilterList.Where(item => item.IsChecked).Select(item => item.Text));
                Assert.Equal(3, grid.Items.Count);
                Assert.Equal(3, overview.Summary.Total);
                // Another host replaces the first, and selecting it again clears the filter.
                Request(overview, null, "a.test");
                Assert.Equal(new[] { "a.test" }, controller.HostFilterList.Where(item => item.IsChecked).Select(item => item.Text));
                Request(overview, null, "a.test");
                Assert.False(hostFilters.IsFilterEnabled);
                Assert.Equal(6, grid.Items.Count);
            });

            static void Request(TrafficOverview overview, TrafficStatusClass? statusClass, string host)
            {
                overview.RaiseEvent(new TrafficFilterRequestedEventArgs(TrafficOverview.FilterRequestedEvent, overview, statusClass, host));
                WpfTestHost.Pump();
            }
        }

        // The overview lines up with the filters above it and the timeline below it: 12px below the filter row and 8px
        // above the timeline, or 8px and 4px with Comfort view off. Hidden, it keeps a slim row for Show overview.
        [Fact]
        public void CaptureView_InsetsTheOverviewLikeTheFiltersAndTheTimeline_InEitherSpacing()
        {
            _application.Run(window =>
            {
                window.Width = 1600;
                window.Height = 720;
                window.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/XboxMultiplayerAnalysisTool;component/Styles/TrafficOverview.xaml")
                });
                Func<bool> read = TrafficOverview.ReadShownPreference;
                Action<bool> save = TrafficOverview.SaveShownPreference;
                TrafficOverview.ReadShownPreference = () => true;
                TrafficOverview.SaveShownPreference = _ => { };
                var controller = new WebServiceDeviceCaptureController("Overview test", DeviceType.Unknown, true);
                controller.ProxyConnections.Add(Row(1));
                var view = new CaptureConnectionView { AutoScroll = false };
                ((DataGrid)view.FindName("ConnectionList")).IsSynchronizedWithCurrentItem = false;
                view.DataContext = new Context(controller);
                try
                {
                    WpfTestHost.Show(window, view);
                    var overview = (TrafficOverview)view.FindName("OverviewStrip");
                    var filters = (FrameworkElement)view.FindName("FiltersHost");
                    var timeline = (FrameworkElement)view.FindName("TimelineView");
                    foreach (var (comfort, below, above) in new[] { (false, 8.0, 4.0), (true, 12.0, 8.0) })
                    {
                        ComfortView.Apply(window.Resources, comfort);
                        WpfTestHost.Pump();
                        Rect row = Bounds(filters, view);
                        Rect strip = Bounds(overview, view);
                        Rect chart = Bounds(timeline, view);
                        Assert.Equal(row.Left, strip.Left, 1);
                        Assert.Equal(row.Right, strip.Right, 1);
                        Assert.Equal(chart.Left, strip.Left, 1);
                        Assert.Equal(below, strip.Top - row.Bottom, 1);
                        Assert.Equal(above, chart.Top - strip.Bottom, 1);
                    }

                    overview.IsExpanded = false;
                    WpfTestHost.Pump();
                    Rect hidden = Bounds(overview, view);
                    Assert.Equal(8, hidden.Top - Bounds(filters, view).Bottom, 1);
                    Assert.Equal(4, Bounds(timeline, view).Top - hidden.Bottom, 1);
                    Assert.Equal(hidden.Right, Bounds(overview.ShowButton, view).Right, 1);
                }
                finally
                {
                    ComfortView.Apply(window.Resources, on: true);
                    TrafficOverview.ReadShownPreference = read;
                    TrafficOverview.SaveShownPreference = save;
                }
            });
        }

        [Fact]
        public void HidingTheOverview_IsRemembered_AndEveryOverviewFollows()
        {
            _application.Run(window =>
            {
                Func<bool> read = TrafficOverview.ReadShownPreference;
                Action<bool> save = TrafficOverview.SaveShownPreference;
                bool shown = true;
                var saved = new List<bool>();
                TrafficOverview.ReadShownPreference = () => shown;
                TrafficOverview.SaveShownPreference = value => saved.Add(shown = value);
                try
                {
                    var first = new TrafficOverview { ItemsSource = SampleRows() };
                    var second = new TrafficOverview { ItemsSource = SampleRows() };
                    WpfTestHost.Show(window, new StackPanel { Children = { first, second } });

                    first.HideButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    WpfTestHost.Pump();
                    Assert.Equal(new[] { false }, saved);
                    Assert.All(new[] { first, second }, overview =>
                    {
                        Assert.False(overview.IsExpanded);
                        Assert.False(overview.Strip.IsVisible);
                        Assert.True(overview.ShowButton.IsVisible);
                    });
                    Assert.Equal("Show overview", first.ShowButton.Content);

                    var later = new TrafficOverview();
                    WpfTestHost.Show(window, later);
                    Assert.False(later.IsExpanded);
                    later.ShowButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    WpfTestHost.Pump();
                    Assert.True(later.IsExpanded);
                    Assert.True(later.Strip.IsVisible);
                    Assert.Equal(new[] { false, true }, saved);
                }
                finally
                {
                    TrafficOverview.ReadShownPreference = read;
                    TrafficOverview.SaveShownPreference = save;
                }
            });
        }

        [Fact]
        public void OriginalLook_HasNoOverview()
        {
            _application.Run(window =>
            {
                var controller = new WebServiceDeviceCaptureController("Overview test", DeviceType.Unknown, true);
                controller.ProxyConnections.Add(Row(1));
                var view = new CaptureConnectionView { AutoScroll = false, DataContext = new Context(controller) };
                WpfTestHost.Show(window, view);
                var overview = (TrafficOverview)view.FindName("OverviewStrip");
                var layout = (Grid)overview.Parent;
                Assert.True(overview.IsVisible);
                try
                {
                    LookState.Current.IsNewLook = false;
                    WpfTestHost.Pump();
                    Assert.Equal(Visibility.Collapsed, overview.Visibility);
                    Assert.Equal(0, overview.ActualHeight);
                    // Its row takes no room, so the original look's rows keep their places.
                    Assert.Equal(0, layout.RowDefinitions[Grid.GetRow(overview)].ActualHeight);
                    int refreshes = overview.RefreshCount;
                    controller.ProxyConnections.Add(Row(2));
                    WpfTestHost.Pump();
                    Assert.Equal(refreshes, overview.RefreshCount);
                }
                finally
                {
                    LookState.Current.IsNewLook = true;
                    WpfTestHost.Pump();
                }
                Assert.True(overview.IsVisible);
            });
        }

        // The new look's resources for the overview: the Light theme and the overview's own styles.
        private static TrafficOverview Show(Window window, IEnumerable<ProxyConnectionModel> rows, double width, bool capturing = false)
        {
            window.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/XboxMultiplayerAnalysisTool;component/Themes/LightTheme.xaml")
            });
            window.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/XboxMultiplayerAnalysisTool;component/Styles/TrafficOverview.xaml")
            });
            // The tests tick the overview themselves.
            var overview = new TrafficOverview
            {
                ItemsSource = rows, IsCapturing = capturing, Clock = () => Now, RefreshInterval = TimeSpan.FromHours(1)
            };
            var host = new Border { Width = width, HorizontalAlignment = HorizontalAlignment.Left, Child = overview };
            WpfTestHost.Show(window, host);
            overview.Tick();
            WpfTestHost.Pump();
            return overview;
        }

        // 1,284 requests ending in the second before Now, with 38 in that second: 1,102 2xx, 98 3xx, 63 4xx, 14 5xx and 7
        // without a status, to sessiondirectory, userpresence and profile most of all.
        private static List<ProxyConnectionModel> SampleRows()
        {
            var statuses = new[] { ("200", 1102), ("304", 98), ("429", 63), ("503", 14), (string.Empty, 7) }
                .SelectMany(entry => Enumerable.Repeat(entry.Item1, entry.Item2)).ToList();
            var hosts = new[]
                {
                    ("sessiondirectory.xboxlive.com", 412), ("userpresence.xboxlive.com", 268), ("profile.xboxlive.com", 187),
                    ("social.xboxlive.com", 150), ("achievements.xboxlive.com", 138), ("titlehub.xboxlive.com", 129),
                }
                .SelectMany(entry => Enumerable.Repeat(entry.Item1, entry.Item2)).ToList();
            DateTime lastSecond = Now.AddMilliseconds(-Now.Millisecond).AddSeconds(-1);
            return statuses.Select((status, index) => Row(
                    index + 1,
                    index < 38 ? lastSecond.AddMilliseconds(index * 20) : lastSecond.AddSeconds(-1 - index / 40),
                    hosts[(index * 7) % hosts.Count],
                    status))
                .ToList();
        }

        private static ProxyConnectionModel Row(int number, DateTime? time = null, string host = "example.test", string status = "200") =>
            CaptureTimelineTestData.Create(number, time ?? Now.AddSeconds(-2), host, TimeSpan.FromMilliseconds(20), status);

        private static Button Part(ItemsControl group, TrafficStatusClass statusClass) =>
            Descendants(group).OfType<Button>().Single(button => ((TrafficOverviewPart)button.DataContext).StatusClass == statusClass);

        private static string TextOf(TextBlock text) =>
            string.Concat(text.Inlines.OfType<System.Windows.Documents.Run>().Select(run => run.Text));

        private static Rect Bounds(FrameworkElement element, Visual ancestor) =>
            element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

        private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, index);
                yield return child;
                foreach (DependencyObject descendant in Descendants(child))
                {
                    yield return descendant;
                }
            }
        }

        private sealed class Context(IDeviceCaptureController controller) : ICaptureDeviceContext
        {
            public DeviceType DeviceType => DeviceType.Unknown;
            public CaptureType CaptureType => CaptureType.WebProxy;
            public string DeviceName => "Overview test";
            public bool IsSelected { get; set; }
            public bool IsReadOnly { get; set; } = true;
            public System.Drawing.Color DeviceColor { get; set; }
            public IDeviceCaptureController CaptureController { get; } = controller;
        }
    }
}
