// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using System.Net.WebSockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using XMAT.Scripting;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class ResponsiveLayoutTests
    {
        private readonly CaptureUiApplicationFixture _application;

        public ResponsiveLayoutTests(CaptureUiApplicationFixture application) => _application = application;

        [Fact]
        public void ReflowPanel_MovesDetailsBelowTheTitleOnlyWhenTheyDontFit()
        {
            _application.Run(window =>
            {
                var title = new TextBlock { Text = "Request" };
                var details = new TextBlock
                {
                    Text = "Request ID: 52 · Time: 12:34:56.789 · Duration: 123 ms",
                    TextWrapping = TextWrapping.Wrap,
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                ReflowPanel.SetWeight(details, 1);
                ReflowPanel.SetStackedAlignment(details, HorizontalAlignment.Left);
                var panel = new ReflowPanel { Spacing = 16, StackedSpacing = 2, Children = { title, details } };
                var host = new Border
                {
                    Width = 600, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    Child = panel
                };
                WpfTestHost.Show(window, host);

                Assert.False(panel.IsStacked);
                Rect line = Bounds(details, panel);
                Assert.Equal(Bounds(title, panel).Top, line.Top, 1);
                Assert.Equal(panel.ActualWidth, line.Right, 1);

                host.Width = 160;
                WpfTestHost.Pump();
                Assert.True(panel.IsStacked);
                Rect wrapped = Bounds(details, panel);
                Assert.True(wrapped.Top >= Bounds(title, panel).Bottom);
                Assert.Equal(0, wrapped.Left, 1);
                Assert.True(wrapped.Right <= 160.5);
                Assert.True(wrapped.Height > line.Height * 1.5, "The details must wrap inside the pane.");
            });
        }

        [Fact]
        public void ReflowPanel_SharesWidthByWeightThenStacksRowsBelowItsBreakpoint()
        {
            _application.Run(window =>
            {
                var editor = new Border();
                var reference = new Border();
                ReflowPanel.SetWeight(editor, 3);
                ReflowPanel.SetStackedHeight(editor, new GridLength(1, GridUnitType.Star));
                ReflowPanel.SetWeight(reference, 1);
                ReflowPanel.SetStackedHeight(reference, new GridLength(100));
                var panel = new ReflowPanel
                {
                    StackBelow = 400, Spacing = 8, StackedSpacing = 8, Children = { editor, reference }
                };
                var host = new Border
                {
                    Width = 808, Height = 300, HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top, Child = panel
                };
                WpfTestHost.Show(window, host);

                Assert.False(panel.IsStacked);
                Assert.Equal(new Rect(0, 0, 600, 300), Bounds(editor, panel));
                Assert.Equal(new Rect(608, 0, 200, 300), Bounds(reference, panel));

                host.Width = 300;
                WpfTestHost.Pump();
                Assert.True(panel.IsStacked);
                Assert.Equal(new Rect(0, 0, 300, 192), Bounds(editor, panel));
                Assert.Equal(new Rect(0, 200, 300, 100), Bounds(reference, panel));
            });
        }

        // n columns of 320 need 352n - 32: as many as fit, never none.
        [Theory]
        [InlineData(300, 1)]
        [InlineData(671, 1)]
        [InlineData(672, 2)]
        [InlineData(1023, 2)]
        [InlineData(1024, 3)]
        [InlineData(double.PositiveInfinity, 1)]
        public void ColumnFlowPanel_FitsAsManyColumnsAsTheWidthHolds(double width, int columns) =>
            Assert.Equal(columns, ColumnFlowPanel.ColumnsFor(width, 320, 32));

        // The panel fills its width with equal columns, filled from the left in rows as tall as their tallest child,
        // and its columns widen with the text size.
        [Fact]
        public void ColumnFlowPanel_FillsItsWidthWithEqualColumnsThatWidenWithTheTextSize()
        {
            _application.Run(window =>
            {
                var first = new Border { Height = 10 };
                var second = new Border { Height = 30 };
                var third = new Border { Height = 20 };
                var panel = new ColumnFlowPanel
                {
                    ColumnMinWidth = 320, ColumnSpacing = 32, RowSpacing = 24, Children = { first, second, third }
                };
                System.Windows.Documents.TextElement.SetFontSize(panel, 12);
                var host = new Border
                {
                    Width = 700, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    Child = panel
                };
                WpfTestHost.Show(window, host);

                Assert.Equal(new Size(700, 74), panel.DesiredSize);
                Assert.Equal(new Rect(0, 0, 334, 30), LayoutInformation.GetLayoutSlot(first));
                Assert.Equal(new Rect(366, 0, 334, 30), LayoutInformation.GetLayoutSlot(second));
                Assert.Equal(new Rect(0, 54, 334, 20), LayoutInformation.GetLayoutSlot(third));

                // At 150% text, a column needs 480, so only one fits.
                System.Windows.Documents.TextElement.SetFontSize(panel, 18);
                WpfTestHost.Pump();
                Assert.Equal(new Rect(0, 0, 700, 10), LayoutInformation.GetLayoutSlot(first));
                Assert.Equal(new Rect(0, 88, 700, 20), LayoutInformation.GetLayoutSlot(third));
            });
        }

        [Fact]
        public void FlexFill_FillsTheRoomLeftAndScrollsThePageOnlyBelowItsMinimum()
        {
            _application.Run(window =>
            {
                var list = new ListBox { ItemsSource = Enumerable.Range(1, 200).Select(i => $"host{i}.example").ToList() };
                var fill = new FlexFill { MinHeight = 120, Child = list };
                var bottom = new Border { Height = 40 };
                var page = new Grid
                {
                    RowDefinitions =
                    {
                        new RowDefinition { Height = GridLength.Auto },
                        new RowDefinition(),
                        new RowDefinition { Height = GridLength.Auto }
                    },
                    Children = { new Border { Height = 50 }, fill, bottom }
                };
                Grid.SetRow(fill, 1);
                Grid.SetRow(bottom, 2);
                var scroller = new ScrollViewer
                {
                    Height = 400, VerticalAlignment = VerticalAlignment.Top, Content = page,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                };
                WpfTestHost.Show(window, scroller);

                // A tall pane: the list takes the room that is left and scrolls its own items.
                Assert.Equal(310, fill.ActualHeight, 1);
                Assert.True(scroller.ExtentHeight <= scroller.ViewportHeight + 0.5, "A tall enough pane must not scroll.");
                var items = Descendants(list).OfType<ScrollViewer>().First();
                Assert.True(items.ScrollableHeight > 0, "Long content must scroll inside the list.");
                Assert.True(items.ViewportHeight > 10, "The list must lay out items for all of its height, not its minimum.");

                // A short pane: the list keeps its minimum and the page scrolls.
                scroller.Height = 200;
                WpfTestHost.Pump();
                Assert.Equal(120, fill.ActualHeight, 1);
                Assert.Equal(50 + 120 + 40, scroller.ExtentHeight, 1);
                Assert.True(scroller.ExtentHeight > scroller.ViewportHeight);
            });
        }

        [Fact]
        public void RightPaneTabs_KeepEveryControlInsideANarrowPane()
        {
            _application.Run(window =>
            {
                window.Width = 1200;
                window.Height = 900;
                var policy = new HostListPolicy();
                var views = new FrameworkElement[]
                {
                    new RequestResponseView
                    {
                        Title = "Request", FirstLineAndHeaders = "GET /inspect HTTP/1.1",
                        HeaderContent = new TextBlock
                        {
                            Text = "Request ID: 52 · Time: 12:34:56.789 · Duration: 123 ms", TextWrapping = TextWrapping.Wrap
                        }
                    },
                    new WebSocketMessagesView { ItemsSource = new ObservableCollection<WebSocketMessageModel>() },
                    new ScriptEditorView { ScriptCollection = new ScriptCollection(typeof(WebServiceCaptureScriptableEventType)) },
                    new BlockListView { BlockList = policy.BlockList },
                    new BypassListView { BypassList = policy.BypassList },
                };
                foreach (var view in views)
                {
                    var host = new Border
                    {
                        Width = 260, Height = 420, HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top, Child = view
                    };
                    WpfTestHost.Show(window, host);
                    foreach (var element in LogicalDescendants(view).OfType<FrameworkElement>()
                        .Where(element => (element is Control or TextBlock) && element.IsVisible && element.ActualWidth > 0))
                    {
                        Rect bounds = Bounds(element, view);
                        Assert.True(bounds.Left >= -0.5 && bounds.Right <= view.ActualWidth + 0.5,
                            $"{view.GetType().Name}: {element.GetType().Name} '{element.Name}' spans {bounds.Left:0} to " +
                            $"{bounds.Right:0} in a {view.ActualWidth:0} px pane.");
                    }

                    if (view is BlockListView)
                    {
                        // The entry field keeps a usable width, with its short Add button beside it.
                        var field = (TextBox)view.FindName("NewBlockTextBox");
                        Rect add = Bounds((Button)view.FindName("AddUrlBlockButton"), view);
                        Assert.True(field.ActualWidth >= 160);
                        Assert.True(add.Left >= Bounds(field, view).Right);
                        Assert.True(add.Top < Bounds(field, view).Bottom);
                    }

                    if (view is ScriptEditorView editor)
                    {
                        // The type reference moves below the editor instead of squeezing beside it, and the editor
                        // keeps a usable height above it.
                        Assert.True(Bounds(editor.TypeInfoTree, view).Top >= Bounds(editor.ScriptEditor, view).Bottom);
                        Assert.True(editor.ScriptEditor.ActualHeight >= 200,
                            $"The stacked editor is only {editor.ScriptEditor.ActualHeight:0} px tall.");
                    }

                    host.Child = null;
                }
            });
        }

        // A wide list view, such as the host list manager, puts the list beside the entry field and its help; a narrow
        // pane keeps one column that reads from the entry field to the list and then the help.
        [Fact]
        public void HostListViews_PutTheListBesideTheEntryFieldOnlyWhenWide()
        {
            _application.Run(window =>
            {
                window.Width = 1200;
                window.Height = 900;
                var policy = new HostListPolicy();
                var view = new BypassListView { BypassList = policy.BypassList };
                var host = new Border
                {
                    Width = 700, Height = 520, HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top, Child = view
                };
                WpfTestHost.Show(window, host);
                var field = (TextBox)view.FindName("NewBypassTextBox");
                var help = (Expander)view.FindName("MatchingRules");
                var list = (ListBox)view.FindName("BypassedUrlList");
                Assert.True(Bounds(list, view).Left >= Bounds(field, view).Right);
                Assert.True(Bounds(list, view).Top < Bounds(help, view).Top);

                host.Width = 380;
                WpfTestHost.Pump();
                Assert.True(Bounds(list, view).Top >= Bounds(field, view).Bottom);
                Assert.True(Bounds(list, view).Bottom <= Bounds(help, view).Top);
                Assert.True(Bounds(list, view).Left < Bounds(field, view).Right);
            });
        }

        [Fact]
        public void Notices_TakeNoRoomUntilTheyHaveSomethingToSay()
        {
            _application.Run(window =>
            {
                var truncated = new WebSocketMessageModel(new WebSocketMessageEventArgs
                {
                    MessageType = WebSocketMessageType.Text, Message = new byte[] { 0x41 }, PayloadTruncated = true
                });
                var messages = new WebSocketMessagesView
                {
                    ItemsSource = new ObservableCollection<WebSocketMessageModel> { truncated }
                };
                WpfTestHost.Show(window, Host(messages));
                var notice = (TextBlock)messages.FindName("PreviewMessage");
                Assert.False(notice.IsVisible, "An empty notice must not leave a blank line above the payload.");

                // The preview, made in the background, says the payload was cut.
                messages.SelectedItem = truncated;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (!notice.IsVisible && clock.Elapsed < TimeSpan.FromSeconds(10))
                {
                    WpfTestHost.Pump();
                    System.Threading.Thread.Sleep(10);
                }
                Assert.True(notice.IsVisible);
                Assert.NotEmpty(notice.Text);

                var policy = new HostListPolicy();
                policy.BlockList.Add("example.com");
                var block = new BlockListView { BlockList = policy.BlockList };
                WpfTestHost.Show(window, Host(block));
                var error = (TextBlock)block.FindName("EntryErrorText");
                Assert.False(error.IsVisible, "An empty entry error must not leave a blank line above the list.");

                ((TextBox)block.FindName("NewBlockTextBox")).Text = "example.com";
                ((Button)block.FindName("AddUrlBlockButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                WpfTestHost.Pump();
                Assert.True(error.IsVisible);
                Assert.NotEmpty(error.Text);
            });

            static Border Host(FrameworkElement view) => new()
            {
                Width = 500, Height = 400, HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top, Child = view
            };
        }

        private static Rect Bounds(FrameworkElement element, Visual ancestor) =>
            element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

        private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject parent)
        {
            foreach (object child in LogicalTreeHelper.GetChildren(parent))
            {
                if (child is DependencyObject element)
                {
                    yield return element;
                    foreach (var descendant in LogicalDescendants(element))
                    {
                        yield return descendant;
                    }
                }
            }
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                yield return child;
                foreach (var descendant in Descendants(child))
                {
                    yield return descendant;
                }
            }
        }
    }
}
