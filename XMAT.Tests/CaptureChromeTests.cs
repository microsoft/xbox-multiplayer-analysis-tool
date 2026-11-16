// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using XMAT.Models;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class CaptureChromeTests
    {
        private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        private static readonly XNamespace Sys = "clr-namespace:System;assembly=mscorlib";
        private readonly CaptureUiApplicationFixture _application;

        public CaptureChromeTests(CaptureUiApplicationFixture application) => _application = application;

        [Fact]
        public void SelectedCapture_DrivesFooterAndDetachesPreviousController()
        {
            _application.Run(window =>
            {
                var first = Proxy(9000);
                var second = Proxy(9100);
                var model = new CaptureAppModel
                {
                    StatusBarText1 = "existing network status",
                    StatusBarText2 = "existing second panel",
                    StatusBarText3 = "existing third panel"
                };
                model.SelectedDeviceContext = new Context(first);
                Assert.Equal(first.CaptureStatusText, model.StatusBarText1);
                Assert.Equal(first.PortDescription, model.StatusBarText2);
                first.QueuePortChange(9001, () => { });
                Assert.Equal(first.PortStatus, model.StatusBarText3);

                var notifications = new List<string>();
                model.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
                first.IsRunning = true;
                Assert.Equal(Localization.GetLocalizedString("PROXY_CAPTURE_RUNNING"), model.StatusBarText1);
                Assert.Contains(nameof(model.StatusBarText1), notifications);
                first.IsRunning = false;
                Assert.Equal(Localization.GetLocalizedString("PROXY_CAPTURE_STOPPED"), model.StatusBarText1);

                model.SelectedDeviceContext = new Context(second);
                second.QueuePortChange(9101, () => { });
                notifications.Clear();
                first.DiscardPortChange(() => { });
                first.IsRunning = true;
                Assert.DoesNotContain(notifications, name => name.StartsWith("StatusBarText", StringComparison.Ordinal));
                Assert.Equal(second.PortDescription, model.StatusBarText2);
                Assert.Equal(second.PortStatus, model.StatusBarText3);

                model.SelectedDeviceContext = new Context(new OtherController(), CaptureType.NetworkTrace);
                Assert.Equal("existing network status", model.StatusBarText1);
                Assert.Equal("existing second panel", model.StatusBarText2);
                Assert.Equal("existing third panel", model.StatusBarText3);
                model.SelectedDeviceContext = null;
            });
        }

        [Fact]
        public void ImportedCapture_HasReadOnlyFooterWithoutAnAssignedPort()
        {
            _application.Run(window =>
            {
                var model = new CaptureAppModel
                {
                    SelectedDeviceContext = new Context(Proxy(9000, true)) { IsReadOnly = true }
                };
                Assert.Equal(Localization.GetLocalizedString("PROXY_CAPTURE_READ_ONLY"), model.StatusBarText1);
                Assert.Equal(Localization.GetLocalizedString("PROXY_PORT_NONE"), model.StatusBarText2);
                model.SelectedDeviceContext = null;
            });
        }

        [Fact]
        public void ActionsMenuAndBottomBar_UseSelectedCaptureInsteadOfKeyboardFocus()
        {
            _application.Run(window =>
            {
                var document = ProductMarkup("MainWindow.xaml");
                var commandBinding = document.Descendants(Presentation + "CommandBinding")
                    .Single(element => (string)element.Attribute("Executed") == "ChangeProxyPort_Executed");
                Assert.Equal("ChangeProxyPort_CanExecute", (string)commandBinding.Attribute("CanExecute"));
                var selected = new Context(Proxy(9000));
                var model = new CaptureAppModel { SelectedDeviceContext = selected };
                model.CaptureDeviceContexts.Add(selected);
                var root = Parse<Grid>(document.Root.Element(Presentation + "Grid"));
                var captures = (AllCaptureDevicesTabControl)root.FindName("AllCaptureDevicesPanel");
                var tabs = (TabControl)captures.FindName("CaptureDevicesTabs");
                // Use the product template without the selector's concrete settings-model requirement.
                tabs.ContentTemplateSelector = null;
                tabs.ContentTemplate = (DataTemplate)captures.Resources["WebProxyTemplate"];
                window.DataContext = model;
                window.CommandBindings.Add(new CommandBinding(
                    WebServiceProxyCommands.ChangePort, (_, e) => e.Handled = true,
                    (_, e) => e.CanExecute = CaptureConnectionView.CanChangeProxyPort(model.SelectedDeviceContext)));
                WpfTestHost.Show(window, root);
                var menu = (MenuItem)root.FindName("ChangeProxyPort");
                var actions = (MenuItem)root.FindName("ActionsMenu");
                var panel = root.FindName("AllCaptureDevicesPanel");
                Assert.Contains(menu, actions.Items.Cast<object>());
                Assert.Same(WebServiceProxyCommands.ChangePort, menu.Command);
                Assert.Same(panel, menu.CommandTarget);
                Assert.True(WebServiceProxyCommands.ChangePort.CanExecute(null, menu.CommandTarget));

                var view = Descendants(root).OfType<CaptureConnectionView>().Single();
                Assert.Null(view.FindName("ChangePortButton"));
                Assert.Null(view.FindName("DetailsText"));
                Assert.Null(view.FindName("PortText"));
                Assert.NotNull(view.FindName("FiltersText"));
                var bar = (StatusBar)root.FindName("MainStatusBar");
                var activity = (TextBlock)root.FindName("StatusBar1");
                var port = (TextBlock)root.FindName("StatusBar2");
                var detail = (TextBlock)root.FindName("StatusBar3");
                Assert.Equal(2, Grid.GetRow(bar));
                Assert.Equal(GridLength.Auto, root.RowDefinitions[2].Height);
                var barPosition = bar.TransformToAncestor(root).Transform(new Point());
                Assert.InRange(Math.Abs(barPosition.Y + bar.ActualHeight - root.ActualHeight), 0, 1);
                Assert.Equal(model.StatusBarText1, activity.Text);
                Assert.Equal(model.StatusBarText2, port.Text);
                selected.Proxy.QueuePortChange(9001, () => { });
                WpfTestHost.Pump();
                Assert.Equal(selected.Proxy.PortStatus, detail.Text);
                Assert.Equal(detail.Text, detail.ToolTip);
                Assert.Equal(TextTrimming.CharacterEllipsis, detail.TextTrimming);
                Assert.True(detail.ActualWidth > 0);

                selected.Proxy.IsRunning = true;
                WpfTestHost.Pump();
                Assert.False(WebServiceProxyCommands.ChangePort.CanExecute(null, menu.CommandTarget));
                selected.Proxy.IsRunning = false;
                var imported = new Context(Proxy(9200, true)) { IsReadOnly = true };
                model.CaptureDeviceContexts.Add(imported);
                model.SelectedDeviceContext = imported;
                WpfTestHost.Pump();
                Assert.False(WebServiceProxyCommands.ChangePort.CanExecute(null, menu.CommandTarget));
                Assert.False(CaptureConnectionView.CanChangeProxyPort(
                    new Context(new OtherController(), CaptureType.NetworkTrace)));
                model.SelectedDeviceContext = null;
                window.DataContext = null;
            });
        }

        [Theory]
        [InlineData("Light")]
        [InlineData("Dark")]
        public void ScriptEditorButtons_EnabledTextMeetsMinimumContrast(string theme)
        {
            _application.Run(window =>
            {
                window.Resources.Remove("TextPrimaryBrush");
                window.Resources.Remove("TextSecondaryBrush");
                window.Resources.MergedDictionaries.Add(Theme(theme));
                var scripts = new Scripting.ScriptCollection(typeof(WebServiceCaptureScriptableEventType));
                foreach (var script in scripts)
                {
                    script.Filename = "contrast-test.cs";
                    script.Script = "Params.Continue = true;";
                }
                var view = new ScriptEditorView { ScriptCollection = scripts };
                WpfTestHost.Show(window, view);
                var buttons = Descendants(view).OfType<Button>().Cast<ButtonBase>()
                    .Append(view.ScriptEnabled).ToArray();
                Assert.Equal(4, buttons.Length);

                foreach (var button in buttons)
                {
                    Assert.True(button.IsEnabled);
                    AssertButtonTextContrast(button);
                }
                view.ScriptEnabled.IsChecked = true;
                WpfTestHost.Pump();
                AssertButtonTextContrast(view.ScriptEnabled);
            });
        }

        [Theory]
        [InlineData("Light")]
        [InlineData("Dark")]
        public void CaptureToolbar_StatesRemainReadableAndFitAfterThemeChanges(string theme)
        {
            _application.Run(window =>
            {
                window.Width = 1280;
                window.Height = 720;
                window.Resources.Remove("TextPrimaryBrush");
                window.Resources.Remove("TextSecondaryBrush");
                var toolbarStyle = NewLookMarkup.Document.Descendants(Presentation + "Style")
                    .Single(style => (string)style.Attribute("TargetType") == "ToolBar");
                window.Resources[typeof(ToolBar)] = Parse<Style>(toolbarStyle);
                window.Resources.MergedDictionaries.Add(AccessibilityTests.AppResources("RecordingIndicatorStyle"));
                var dictionary = Theme(theme);
                window.Resources.MergedDictionaries.Add(dictionary);
                var controller = Proxy(9000);
                var view = new CaptureConnectionView { DataContext = new Context(controller) };
                ((DataGrid)view.FindName("ConnectionList")).IsSynchronizedWithCurrentItem = false;
                WpfTestHost.Show(window, view);
                var start = (ToggleButton)view.FindName("StartButton");
                var toolbar = Descendants(view).OfType<ToolBar>().Single();
                var workspace = Assert.IsType<Grid>(toolbar.Parent);
                var inspector = Assert.IsType<TabControl>(((TabItem)view.FindName("ReqestResponseTab")).Parent);
                var toolbarBounds = toolbar.TransformToAncestor(view).TransformBounds(new Rect(toolbar.RenderSize));
                var inspectorBounds = inspector.TransformToAncestor(view).TransformBounds(new Rect(inspector.RenderSize));
                Assert.InRange(Math.Abs(toolbarBounds.Width - workspace.ActualWidth), 0, 1);
                Assert.True(toolbarBounds.Bottom <= inspectorBounds.Top,
                    "Capture-wide actions must sit above both the traffic and inspector panes.");
                Assert.InRange(Math.Abs(workspace.ColumnDefinitions[0].ActualWidth -
                    workspace.ColumnDefinitions[2].ActualWidth), 0, 1);
                foreach (var name in new[] { "StartButton", "AnalyzeButton", "ClearButton" })
                {
                    Assert.Same(view, ((ButtonBase)view.FindName(name)).CommandTarget);
                }

                foreach (var name in new[] { theme, theme == "Dark" ? "Light" : "Dark" })
                {
                    if (name != theme)
                    {
                        window.Resources.MergedDictionaries.Remove(dictionary);
                        dictionary = Theme(name);
                        window.Resources.MergedDictionaries.Add(dictionary);
                        WpfTestHost.Pump();
                    }
                    Assert.True(start.IsEnabled);
                    Assert.False(start.IsChecked);
                    AssertButtonTextContrast(start);
                    // Start is the toolbar's one filled action, in Xbox green with a soft gradient.
                    var startBackground = Assert.IsType<LinearGradientBrush>(start.Background);
                    Assert.Contains(startBackground.GradientStops, stop => stop.Color == Color.FromRgb(0x10, 0x7C, 0x10));
                    Assert.True(start.ActualHeight >= 28);
                    var timeline = (CaptureTimelineView)view.FindName("TimelineView");
                    var autoScroll = (CheckBox)timeline.FindName("AutoScrollButton");
                    var mode = (RadioButton)timeline.FindName("ByHostMode");
                    var autoBounds = autoScroll.TransformToAncestor(view).TransformBounds(new Rect(autoScroll.RenderSize));
                    var modeBounds = mode.TransformToAncestor(view).TransformBounds(new Rect(mode.RenderSize));
                    var timelineBounds = timeline.TransformToAncestor(view).TransformBounds(new Rect(timeline.RenderSize));
                    Assert.True(autoBounds.Left >= modeBounds.Right && autoBounds.Right <= timelineBounds.Right,
                        "Auto Scroll must remain visible beside the timeline modes.");
                    foreach (var button in Descendants(toolbar).OfType<ButtonBase>().Where(button => button.IsVisible))
                    {
                        var bounds = button.TransformToAncestor(toolbar)
                            .TransformBounds(new Rect(button.RenderSize));
                        Assert.True(bounds.Left >= 0 && bounds.Right <= toolbar.ActualWidth + 1,
                            "Capture toolbar actions must fit at 1280 pixels.");
                        Assert.True(bounds.Top >= 0 && bounds.Bottom <= toolbar.ActualHeight + 1,
                            "The taller toolbar actions must not be clipped.");
                    }

                    // Set only the controller's observable state; do not start a proxy listener.
                    controller.IsRunning = true;
                    WpfTestHost.Pump();
                    Assert.True(start.IsChecked);
                    // The Capturing indicator sits with the live timeline, between its modes and Auto Scroll.
                    var capturing = (RecordingIndicator)timeline.FindName("CapturingIndicator");
                    Assert.True(capturing.IsVisible);
                    Assert.DoesNotContain(capturing, Descendants(toolbar));
                    var capturingBounds = capturing.TransformToAncestor(view).TransformBounds(new Rect(capturing.RenderSize));
                    var liveAutoBounds = autoScroll.TransformToAncestor(view).TransformBounds(new Rect(autoScroll.RenderSize));
                    Assert.True(capturingBounds.Left >= modeBounds.Right && capturingBounds.Right <= liveAutoBounds.Left,
                        "Capturing must sit between the timeline modes and Auto Scroll.");
                    Assert.InRange(Math.Abs((capturingBounds.Top + capturingBounds.Bottom) - (liveAutoBounds.Top + liveAutoBounds.Bottom)) / 2, 0, 1);
                    Assert.Contains(Descendants(start).OfType<TextBlock>(),
                        text => text.Text == CaptureConnectionView.StopTraceText);
                    Assert.NotSame(startBackground, start.Background);
                    AssertButtonTextContrast(start);
                    controller.IsRunning = false;
                    WpfTestHost.Pump();
                    Assert.False(start.IsChecked);
                    Assert.Contains(Descendants(start).OfType<TextBlock>(),
                        text => text.Text == CaptureConnectionView.StartTraceText);
                    AssertButtonTextContrast(start);
                }

                view.DataContext = new Context(Proxy(9001, true)) { IsReadOnly = true };
                WpfTestHost.Pump();
                Assert.False(start.IsEnabled);
                Assert.Equal(((SolidColorBrush)view.FindResource("TextDisabledBrush")).Color,
                    Assert.IsType<SolidColorBrush>(start.Foreground).Color);
                Assert.All(Descendants(start).OfType<TextBlock>(),
                    text => Assert.Equal(start.Foreground, text.Foreground));
                view.DataContext = null;
            });
        }

        private static void AssertButtonTextContrast(ButtonBase button)
        {
            var background = Descendants(button).OfType<Border>().First().Background;
            Assert.True(Contrast(button.Foreground, background) >= 4.5, "Enabled button text must meet 4.5:1 contrast.");
            Assert.All(Descendants(button).OfType<TextBlock>(),
                text => Assert.True(Contrast(text.Foreground, background) >= 4.5,
                    $"Button label '{text.Text}' ({text.Foreground}) must contrast with {background}."));
        }

        // However long the content, a scroll bar's thumb in the new look is at least 24px long, so it stays easy to see and
        // to grab.
        [Theory]
        [InlineData(Orientation.Vertical)]
        [InlineData(Orientation.Horizontal)]
        public void ScrollBarThumbs_AreAtLeast24pxLong(Orientation orientation)
        {
            _application.Run(window =>
            {
                ResourceDictionary look = ThemeManager.CreateLook(newLook: true);
                ThemeManager.LoadTheme(look, "Light", newLook: true, highContrast: false);
                window.Resources = look;
                var bar = new ScrollBar
                {
                    Orientation = orientation,
                    Maximum = 10000,
                    ViewportSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                };
                if (orientation == Orientation.Vertical)
                {
                    bar.Height = 160;
                }
                else
                {
                    bar.Width = 160;
                }

                WpfTestHost.Show(window, bar);
                Thumb thumb = ((Track)bar.Template.FindName("PART_Track", bar)).Thumb;
                Assert.Equal(24, orientation == Orientation.Vertical ? thumb.ActualHeight : thumb.ActualWidth);
            });
        }

        [Theory]
        [InlineData("Light", GridResizeDirection.Columns)]
        [InlineData("Light", GridResizeDirection.Rows)]
        [InlineData("Dark", GridResizeDirection.Columns)]
        [InlineData("Dark", GridResizeDirection.Rows)]
        public void PaneDividers_StayHiddenUntilInteractionWithoutShrinkingTheGrabArea(
            string theme, GridResizeDirection direction)
        {
            _application.Run(window =>
            {
                var dictionary = Theme(theme);
                window.Resources.MergedDictionaries.Add(dictionary);
                var style = NewLookMarkup.Document.Descendants(Presentation + "Style")
                    .Single(element => (string)element.Attribute("TargetType") == "GridSplitter" &&
                        element.Attribute(Xaml + "Key") == null);
                var splitter = new DraggableGridSplitter
                {
                    Style = Parse<Style>(style),
                    ResizeDirection = direction,
                    Width = direction == GridResizeDirection.Columns ? 12 : 160,
                    Height = direction == GridResizeDirection.Rows ? 12 : 160
                };
                WpfTestHost.Show(window, splitter);
                var line = Assert.IsType<Border>(splitter.Template.FindName("ResizeLine", splitter));
                Assert.Equal(0, line.Opacity);
                Assert.Equal(1, direction == GridResizeDirection.Columns ? line.ActualWidth : line.ActualHeight);
                Assert.Equal(160, direction == GridResizeDirection.Columns ? line.ActualHeight : line.ActualWidth);
                Assert.NotNull(splitter.InputHitTest(new Point(1, 1)));
                Assert.NotNull(splitter.InputHitTest(new Point(splitter.ActualWidth - 1, splitter.ActualHeight - 1)));

                splitter.SetDragging(true);
                WpfTestHost.Pump();
                Assert.Equal(1, line.Opacity);
                Assert.Equal(((SolidColorBrush)dictionary["BorderControlBrush"]).Color,
                    Assert.IsType<SolidColorBrush>(line.Background).Color);
                window.Resources.MergedDictionaries.Remove(dictionary);
                dictionary = Theme(theme == "Dark" ? "Light" : "Dark");
                window.Resources.MergedDictionaries.Add(dictionary);
                WpfTestHost.Pump();
                Assert.Equal(((SolidColorBrush)dictionary["BorderControlBrush"]).Color,
                    Assert.IsType<SolidColorBrush>(line.Background).Color);

                splitter.SetDragging(false);
                WpfTestHost.Pump();
                Assert.Equal(0, line.Opacity);
            });
        }

        [Theory]
        [InlineData("Light")]
        [InlineData("Dark")]
        public void CaptureRows_TextContrastSurvivesThemeAndStatusChanges(string theme)
        {
            _application.Run(window =>
            {
                window.Resources.Remove("TextPrimaryBrush");
                window.Resources.Remove("TextSecondaryBrush");
                window.Height = 650;
                var gridStyle = NewLookMarkup.Document.Descendants(Presentation + "Style")
                    .Single(style => (string)style.Attribute("TargetType") == "DataGrid");
                window.Resources[typeof(DataGrid)] = Parse<Style>(gridStyle);
                var controller = Proxy(9000);
                var states = new[] { ("200", "POST"), ("200", "CONNECT"), ("", "GET"), ("503", "GET") };
                foreach (var (status, method) in states)
                {
                    for (int repeat = 0; repeat < 2; repeat++)
                    {
                        int number = controller.ProxyConnections.Count + 1;
                        controller.ProxyConnections.Add(new ProxyConnectionModel(
                            number, status, CaptureTimelineTestData.Origin, TimeSpan.FromMilliseconds(20),
                            "https", "example.test:443", method, "/contrast",
                            "", Array.Empty<byte>(), "", Array.Empty<byte>(), "127.0.0.1"));
                    }
                }
                var view = new CaptureConnectionView { DataContext = new Context(controller) };
                var grid = (DataGrid)view.FindName("ConnectionList");
                grid.IsSynchronizedWithCurrentItem = false;
                grid.EnableRowVirtualization = false;
                var dictionary = Theme(theme);
                window.Resources.MergedDictionaries.Add(dictionary);
                WpfTestHost.Show(window, view);
                grid.SelectedItem = null;

                foreach (var name in new[] { theme, theme == "Dark" ? "Light" : "Dark" })
                {
                    if (name != theme)
                    {
                        window.Resources.MergedDictionaries.Remove(dictionary);
                        dictionary = Theme(name);
                        window.Resources.MergedDictionaries.Add(dictionary);
                    }
                    WpfTestHost.Pump();
                    var colors = new List<Color>();
                    foreach (var model in controller.ProxyConnections)
                    {
                        grid.ScrollIntoView(model);
                        WpfTestHost.Pump();
                        var row = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromItem(model));
                        Assert.True(Contrast(row.Foreground, row.Background) >= 4.5,
                            $"{name}: {model.Method} {model.Status} must contrast with its row background.");
                        Assert.All(Descendants(row).OfType<TextBlock>().Where(text => !string.IsNullOrEmpty(text.Text)),
                            text => Assert.True(Contrast(text.Foreground, row.Background) >= 4.5,
                                $"{name}: capture cell text must meet 4.5:1 contrast."));
                        colors.Add(Assert.IsType<SolidColorBrush>(row.Foreground).Color);
                    }
                    Assert.Equal(4, colors.Distinct().Count());
                }

                var pending = controller.ProxyConnections[4];
                grid.ScrollIntoView(pending);
                WpfTestHost.Pump();
                var pendingRow = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromItem(pending));
                var pendingColor = Assert.IsType<SolidColorBrush>(pendingRow.Foreground).Color;
                CaptureTimelineTestData.Update(pending, status: "200");
                WpfTestHost.Pump();
                Assert.NotEqual(pendingColor, Assert.IsType<SolidColorBrush>(pendingRow.Foreground).Color);
                Assert.True(Contrast(pendingRow.Foreground, pendingRow.Background) >= 4.5);
            });
        }

        [Theory]
        [InlineData("Light")]
        [InlineData("Dark")]
        public void CaptureRows_NarrowInspectorLayoutKeepsShortValuesReadable(string theme)
        {
            _application.Run(window =>
            {
                window.Width = 1200;
                window.Height = 650;
                window.Resources.MergedDictionaries.Add(Theme(theme));
                var gridStyle = NewLookMarkup.Document.Descendants(Presentation + "Style")
                    .Single(style => (string)style.Attribute("TargetType") == "DataGrid");
                window.Resources[typeof(DataGrid)] = Parse<Style>(gridStyle);
                var controller = Proxy(9000);
                var model = new ProxyConnectionModel(
                    63, "429", CaptureTimelineTestData.Origin, TimeSpan.FromMilliseconds(20),
                    "https", "sessions.example", "CONNECT", "/sessions/active",
                    "", Array.Empty<byte>(), "", Array.Empty<byte>(), "192.0.2.10");
                controller.ProxyConnections.Add(model);
                var view = new CaptureConnectionView { DataContext = new Context(controller) };
                var grid = (DataGrid)view.FindName("ConnectionList");
                grid.IsSynchronizedWithCurrentItem = false;
                WpfTestHost.Show(window, view);

                window.Width = 900;
                WpfTestHost.Pump();
                var scroller = Descendants(grid).OfType<ScrollViewer>().First();
                Assert.True(scroller.ScrollableWidth > 0,
                    "A narrow traffic pane must scroll rather than crush its columns.");
                var row = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromItem(model));
                var cells = Descendants(row).OfType<DataGridCell>()
                    .Where(cell => cell.Column.DisplayIndex < 4).ToArray();
                Assert.Equal(4, cells.Length);
                foreach (var cell in cells)
                {
                    var text = Assert.Single(Descendants(cell).OfType<TextBlock>());
                    var measured = new FormattedText(
                        text.Text, System.Globalization.CultureInfo.CurrentCulture, text.FlowDirection,
                        new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
                        text.FontSize, text.Foreground, VisualTreeHelper.GetDpi(text).PixelsPerDip);
                    Assert.True(text.ActualWidth + 0.5 >= measured.WidthIncludingTrailingWhitespace,
                        $"The complete capture value '{text.Text}' must remain visible after resizing.");
                }
            });
        }

        [Theory]
        [InlineData("Light")]
        [InlineData("Dark")]
        public void AddCaptureButton_CentersItsIconAndKeepsTheNewCommandWithAFullTabRow(string theme)
        {
            _application.Run(window =>
            {
                window.Width = 720;
                window.Height = 500;
                window.Resources.Remove("TextPrimaryBrush");
                window.Resources.Remove("TextSecondaryBrush");
                window.Resources.MergedDictionaries.Add(Theme(theme));
                var tabStyle = NewLookMarkup.Document.Descendants(Presentation + "Style")
                    .Single(style => (string)style.Attribute("TargetType") == "TabItem");
                window.Resources[typeof(TabItem)] = Parse<Style>(tabStyle);
                var model = new CaptureAppModel();
                for (ushort index = 0; index < 6; index++)
                {
                    model.CaptureDeviceContexts.Add(new Context(Proxy((ushort)(9000 + index))));
                }
                model.SelectedDeviceContext = model.CaptureDeviceContexts[0];
                var captures = new AllCaptureDevicesTabControl { DataContext = model };
                var tabs = (TabControl)captures.FindName("CaptureDevicesTabs");
                tabs.ContentTemplateSelector = null;
                tabs.ContentTemplate = new DataTemplate();
                var newRequests = 0;
                window.CommandBindings.Add(new CommandBinding(ApplicationCommands.New,
                    (_, e) => { newRequests++; e.Handled = true; },
                    (_, e) => { e.CanExecute = true; e.Handled = true; }));
                WpfTestHost.Show(window, captures);
                var add = (Button)tabs.Template.FindName("AddCaptureButton", tabs);
                Assert.True(add.IsEnabled);
                Assert.Equal(Localization.GetLocalizedString("FILE_NEW_CAPTURE"),
                    System.Windows.Automation.AutomationProperties.GetName(add));
                AssertButtonTextContrast(add);
                var icon = Assert.IsType<System.Windows.Shapes.Path>(add.Content);
                var bounds = icon.TransformToAncestor(add).TransformBounds(new Rect(icon.RenderSize));
                Assert.InRange(Math.Abs(bounds.Left + bounds.Width / 2 - add.ActualWidth / 2), 0, 0.5);
                Assert.InRange(Math.Abs(bounds.Top + bounds.Height / 2 - add.ActualHeight / 2), 0, 0.5);
                var headers = (OverflowTabPanel)tabs.Template.FindName("headerPanel", tabs);
                Assert.True(headers.HasOverflow, "Exercise a tab row with more tabs than fit.");
                Assert.True(headers.ActualHeight < add.ActualHeight * 2, "The tabs stay in one row.");
                var addBounds = add.TransformToAncestor(tabs).TransformBounds(new Rect(add.RenderSize));
                var headerBounds = headers.TransformToAncestor(tabs).TransformBounds(new Rect(headers.RenderSize));
                Assert.True(addBounds.Right <= headerBounds.Left, "The add action must not overlap any tab.");
                var active = Assert.IsType<TabItem>(tabs.ItemContainerGenerator.ContainerFromItem(model.SelectedDeviceContext));
                var backplate = (Border)tabs.Template.FindName("ContentPanel", tabs);
                Assert.Equal(new CornerRadius(0), backplate.CornerRadius);
                Assert.Equal(new Thickness(0), backplate.BorderThickness);
                var activeBounds = active.TransformToAncestor(tabs).TransformBounds(new Rect(active.RenderSize));
                var plateTop = backplate.TransformToAncestor(tabs).Transform(new Point()).Y;
                var fill = Assert.IsType<SolidColorBrush>(((Border)active.Template.FindName("Border", active)).Background).Color;
                Assert.Equal(fill, RenderedColor(tabs, new Point(activeBounds.Left + activeBounds.Width / 2, plateTop)));
                Assert.Same(ApplicationCommands.New, add.Command);
                ApplicationCommands.New.Execute(null, add);
                Assert.Equal(1, newRequests);
                model.SelectedDeviceContext = null;
                captures.DataContext = null;
            });
        }

        [Theory]
        [InlineData("Light", 1280)]
        [InlineData("Dark", 960)]
        public void Inspector_TextWellsAreInsetAndMetadataDoesNotOverlapTheViewSelectors(string theme, int width)
        {
            _application.Run(window =>
            {
                window.Width = width;
                window.Height = 720;
                window.Resources.Remove("TextPrimaryBrush");
                window.Resources.MergedDictionaries.Add(Theme(theme));
                foreach (var target in new[] { "TabControl", "TabItem" })
                {
                    var source = NewLookMarkup.Document.Descendants(Presentation + "Style")
                        .Single(style => (string)style.Attribute("TargetType") == target);
                    var style = Parse<Style>(source);
                    window.Resources[style.TargetType] = style;
                }
                var controller = Proxy(9000);
                var connection = new ProxyConnectionModel(
                    42, "200", CaptureTimelineTestData.Origin, TimeSpan.FromMilliseconds(1500),
                    "https", "example.test:443", "GET", "/inspect",
                    "GET /inspect HTTP/1.1\r\nHost: example.test\r\nAccept: */*",
                    Array.Empty<byte>(), "", Array.Empty<byte>(), "127.0.0.1");
                controller.ProxyConnections.Add(connection);
                var view = new CaptureConnectionView { DataContext = new Context(controller) };
                var grid = (DataGrid)view.FindName("ConnectionList");
                grid.IsSynchronizedWithCurrentItem = false;
                WpfTestHost.Show(window, view);
                grid.SelectedItem = connection;
                WpfTestHost.Pump();
                var request = (RequestResponseView)view.FindName("RequestViewControl");
                var metadata = Assert.IsType<TextBlock>(request.HeaderContent);
                Assert.Contains(metadata.Inlines.OfType<System.Windows.Documents.Run>(), run => run.Text == "42");
                // The whole request's duration, not only the milliseconds part of it.
                Assert.Contains(metadata.Inlines.OfType<System.Windows.Documents.Run>(), run => run.Text == "1500 ms");
                var selector = Descendants(request).OfType<OverflowTabPanel>().Single();
                var metadataBounds = metadata.TransformToAncestor(request).TransformBounds(new Rect(metadata.RenderSize));
                var selectorBounds = selector.TransformToAncestor(request).TransformBounds(new Rect(selector.RenderSize));
                Assert.True(metadataBounds.Bottom + 8 <= selectorBounds.Top + 1,
                    "Metadata must wrap in its heading row, leaving an eight-pixel gap before view selectors.");
                var text = Descendants(request).OfType<TextBox>().Single();
                Assert.True(text.IsReadOnly);
                Assert.Contains("example.test", text.Text, StringComparison.Ordinal);
                var well = Descendants(request).OfType<ContentControl>()
                    .Single(control => control.Style == request.FindResource("RecessedSurfaceStyle"));
                Assert.DoesNotContain(Descendants(well).OfType<Border>(), border => border.Background is LinearGradientBrush);
                Assert.True(Luminance(((SolidColorBrush)well.Background).Color) <
                    Luminance(((SolidColorBrush)request.FindResource("SurfacePanelBrush")).Color));
                Assert.True(Contrast(text.Foreground, well.Background) >= 4.5);
                var insideText = text.TransformToAncestor(well).Transform(
                    new Point(text.ActualWidth - 2, text.ActualHeight - 2));
                Assert.Equal(((SolidColorBrush)well.Background).Color, RenderedColor(well, insideText));
                var firstLine = text.GetRectFromCharacterIndex(0);
                var secondLine = text.GetRectFromCharacterIndex(text.Text.IndexOf('\n') + 1);
                Assert.InRange(secondLine.Top - firstLine.Top, 17, 19);
                view.DataContext = null;
            });
        }

        [Theory]
        [InlineData("Light")]
        [InlineData("Dark")]
        public void EditableDropdowns_DrawTheirLabelOnTheFieldBackground(string theme)
        {
            _application.Run(window =>
            {
                window.Resources.MergedDictionaries.Add(Theme(theme));
                var comboStyle = NewLookMarkup.Document.Descendants(Presentation + "Style")
                    .Single(style => (string)style.Attribute("TargetType") == "ComboBox");
                window.Resources[typeof(ComboBox)] = Parse<Style>(comboStyle);
                var combo = new ComboBox
                {
                    IsEditable = true, IsReadOnly = true, Text = "Status", Width = 140, Height = 28,
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
                };
                WpfTestHost.Show(window, combo);
                var label = (TextBox)combo.Template.FindName("PART_EditableTextBox", combo);
                var field = Assert.IsType<SolidColorBrush>(combo.Background).Color;
                // A field reads as a field by its outline: a soft edge with a strong bottom line at 3:1 or more, while
                // its fill may match the panel around it.
                var panel = ((SolidColorBrush)window.FindResource("SurfacePanelBrush")).Color;
                var outline = Assert.IsType<LinearGradientBrush>(combo.BorderBrush);
                Assert.Contains(outline.GradientStops, stop => ColorContrast(stop.Color, panel) >= 3);
                var besideLabel = label.TransformToAncestor(combo)
                    .Transform(new Point(label.ActualWidth - 4, label.ActualHeight / 2));
                Assert.Equal(field, RenderedColor(combo, besideLabel));
            });
        }

        [Fact]
        public void Appearance_SwitchesAllPalettesAndNativeColorsWithoutAccumulatingDictionaries()
        {
            _application.Run(window =>
            {
                var previous = ThemeManager.CurrentTheme;
                window.Resources.Remove("TextPrimaryBrush");
                window.Resources.Remove("TextSecondaryBrush");
                var panel = new Border();
                panel.SetResourceReference(Border.BackgroundProperty, "SurfacePanelBrush");
                var text = new TextBlock { Text = "Theme preview" };
                text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                panel.Child = text;
                WpfTestHost.Show(window, panel);
                try
                {
                    foreach (var name in new[] { "Dark", "Light", "Dark", "Light" })
                    {
                        // A test machine with high contrast on would get the high contrast theme from ApplyTheme.
                        ThemeManager.PrepareTheme(
                            Application.Current.Resources, name, LookState.Current.IsNewLook, highContrast: false);
                        ThemeManager.RaiseThemeChanged();
                        WpfTestHost.Pump();
                        var expected = Theme(name);
                        Assert.Equal(name, ThemeManager.CurrentTheme);
                        Assert.Equal(((SolidColorBrush)expected["SurfacePanelBrush"]).Color,
                            Assert.IsType<SolidColorBrush>(panel.Background).Color);
                        Assert.True(Contrast(text.Foreground, panel.Background) >= 4.5);
                        foreach (var key in new[] { SystemColors.WindowBrushKey, SystemColors.HighlightBrushKey,
                            SystemColors.MenuBrushKey, SystemColors.InactiveSelectionHighlightTextBrushKey })
                        {
                            Assert.Equal(((SolidColorBrush)expected[key]).Color,
                                Assert.IsType<SolidColorBrush>(Application.Current.Resources[key]).Color);
                        }
                        Assert.True(Contrast((Brush)expected["AccentOnFillBrush"],
                            (Brush)expected["AccentFillBrush"]) >= 4.5);
                        Assert.Single(Application.Current.Resources.MergedDictionaries,
                            dictionary => dictionary.Source?.OriginalString.EndsWith("Theme.xaml", StringComparison.Ordinal) == true);
                    }
                    Assert.Throws<ArgumentOutOfRangeException>(() => ThemeManager.ApplyTheme("Unknown"));
                    Assert.Equal("Light", ThemeManager.CurrentTheme);
                }
                finally
                {
                    ThemeManager.ApplyTheme(previous);
                }
            });
        }

        // Comfort view off swaps the new look's spacing for Looks/DenseSpacing.xaml's in open views, and on swaps it
        // back. Only spacing changes, and header text still lines up with the text in its column.
        [Fact]
        public void ComfortView_SwapsTheNewLooksSpacingForTheDenseSpacingInOpenViews()
        {
            _application.Run(window =>
            {
                AddButtonStyles(window.Resources);
                window.Resources.MergedDictionaries.Add(AccessibilityTests.AppResources(
                    "DataGrid", "DataGridRow", "DataGridCell", "SelectionForegroundConverter", "DataGridColumnHeader",
                    "ColumnHeaderGripperStyle"));
                var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, ItemsSource = new[] { "a", "b" } };
                grid.Columns.Add(new DataGridTextColumn { Header = "Host", Binding = new System.Windows.Data.Binding(".") });
                WpfTestHost.Show(window, grid);
                var header = Descendants(grid).OfType<DataGridColumnHeader>().First(candidate => candidate.Column != null);
                var row = Descendants(grid).OfType<DataGridRow>().First();
                double TextLeft(DependencyObject container) =>
                    Descendants(container).OfType<TextBlock>().First().TransformToAncestor(grid).Transform(new Point()).X;

                var dense = new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/XboxMultiplayerAnalysisTool;component/Looks/DenseSpacing.xaml")
                };
                Assert.All(dense.Keys.Cast<object>(), key =>
                {
                    Assert.True(window.Resources.Contains(key), $"The new look must define {key} for Comfort view.");
                    Assert.True(dense[key] is Thickness or double, $"{key}: Comfort view changes spacing only.");
                    Assert.DoesNotContain((string)key, TextScale.FontSizeKeys);
                });

                foreach (var (on, headerHeight, rowHeight) in new[] { (false, 28, 20), (true, 36, 24), (false, 28, 20) })
                {
                    ComfortView.Apply(window.Resources, on);
                    WpfTestHost.Pump();
                    Assert.Equal(headerHeight, (double)window.Resources["TableHeaderMinHeight"]);
                    Assert.Equal(headerHeight, header.ActualHeight);
                    Assert.Equal(rowHeight, row.ActualHeight);
                    Assert.Equal(TextLeft(row), TextLeft(header), 1);
                }
                ComfortView.Apply(window.Resources, on: true);
                Assert.All(dense.Keys.Cast<object>(), key => Assert.NotEqual(dense[key], window.Resources[key]));
            });
        }

        // Comfort view follows the theme items in Edit > Appearance. The original look keeps its own spacing, so there
        // the item is unavailable and its tooltip says why.
        [Fact]
        public void ComfortView_FollowsTheThemesAndIsUnavailableInTheOriginalLook()
        {
            _application.Run(window =>
            {
                var markup = ProductMarkup("MainWindow.xaml").Descendants(Presentation + "MenuItem")
                    .Single(element => (string)element.Attribute(Xaml + "Name") == "AppearanceMenu");
                Assert.Equal("ComfortView_Click", (string)markup.Elements(Presentation + "MenuItem").Last().Attribute("Click"));
                var appearance = Parse<MenuItem>(markup);
                var comfort = appearance.Items.OfType<MenuItem>().Last();
                Assert.Equal("ComfortViewMenu", comfort.Name);
                Assert.Equal(new[] { "Light", "Dark" },
                    appearance.Items.OfType<MenuItem>().SkipLast(1).Select(item => (string)item.Tag));
                Assert.IsType<Separator>(appearance.Items[appearance.Items.IndexOf(comfort) - 1]);
                Assert.True(comfort.IsCheckable);

                bool newLook = LookState.Current.IsNewLook;
                try
                {
                    LookState.Current.IsNewLook = true;
                    Assert.True(comfort.IsEnabled);
                    Assert.Equal(Localization.GetLocalizedString("COMFORT_VIEW_TOOLTIP"), comfort.ToolTip);

                    LookState.Current.IsNewLook = false;
                    Assert.False(comfort.IsEnabled);
                    Assert.True(ToolTipService.GetShowOnDisabled(comfort));
                    Assert.Equal(Localization.GetLocalizedString("COMFORT_VIEW_NEW_LOOK_ONLY"), comfort.ToolTip);
                    Assert.DoesNotContain("NO_IMPL_", (string)comfort.ToolTip, StringComparison.Ordinal);
                }
                finally
                {
                    LookState.Current.IsNewLook = newLook;
                }
            });
        }

        private static Color RenderedColor(FrameworkElement element, Point point)
        {
            var dpi = VisualTreeHelper.GetDpi(element);
            var image = new RenderTargetBitmap(
                (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX),
                (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            image.Render(element);
            var pixel = new byte[4];
            image.CopyPixels(new Int32Rect(
                (int)Math.Floor(point.X * dpi.DpiScaleX),
                (int)Math.Ceiling(point.Y * dpi.DpiScaleY), 1, 1), pixel, 4, 0);
            return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
        }

        [Theory]
        [InlineData("Light")]
        [InlineData("Dark")]
        public void SelectedTabs_AreLighterAndTextRemainsReadableAcrossThemeChanges(string theme)
        {
            _application.Run(window =>
            {
                window.Resources.Remove("TextPrimaryBrush");
                window.Resources.Remove("TextSecondaryBrush");
                AddButtonStyles(window.Resources);
                var secondContent = new TextBlock { Text = "Second capture" };
                var first = new TabItem { Header = "First", Content = new TextBlock { Text = "First capture" } };
                var second = new TabItem { Header = "Second", Content = secondContent };
                var tabs = new TabControl { Items = { first, second } };
                var dictionary = Theme(theme);
                window.Resources.MergedDictionaries.Add(dictionary);
                WpfTestHost.Show(window, tabs);
                // A tab keeps its width whichever tab is selected, so the strip never shifts.
                var widths = new[] { first.ActualWidth, second.ActualWidth };

                foreach (var name in new[] { theme, theme == "Dark" ? "Light" : "Dark" })
                {
                    if (name != theme)
                    {
                        window.Resources.MergedDictionaries.Remove(dictionary);
                        dictionary = Theme(name);
                        window.Resources.MergedDictionaries.Add(dictionary);
                    }
                    foreach (var selected in new[] { first, second })
                    {
                        tabs.SelectedItem = selected;
                        WpfTestHost.Pump();
                        var other = selected == first ? second : first;
                        var selectedBackground = ((Border)selected.Template.FindName("Border", selected)).Background;
                        var otherBackground = ((Border)other.Template.FindName("Border", other)).Background;
                        Assert.True(Luminance(Assert.IsType<SolidColorBrush>(selectedBackground).Color) >
                            Luminance(Assert.IsType<SolidColorBrush>(otherBackground).Color),
                            $"{name}: the selected tab must be lighter than an unselected tab.");
                        var selectedLabel = (ContentPresenter)selected.Template.FindName("ContentSite", selected);
                        var otherLabel = (ContentPresenter)other.Template.FindName("ContentSite", other);
                        Assert.True(Contrast(TextElement.GetForeground(selectedLabel), selectedBackground) >= 4.5);
                        Assert.True(Contrast(TextElement.GetForeground(otherLabel), otherBackground) >= 4.5);
                        Assert.Equal(FontWeights.SemiBold, TextElement.GetFontWeight(selectedLabel));
                        Assert.Equal(FontWeights.Normal, TextElement.GetFontWeight(otherLabel));
                        Assert.Equal(widths, new[] { first.ActualWidth, second.ActualWidth });
                        // Only the gap between capture tabs separates them; there is no divider line.
                        Assert.Equal(new Thickness(0), ((Border)other.Template.FindName("Border", other)).BorderThickness);
                        var backplate = (Border)tabs.Template.FindName("ContentPanel", tabs);
                        Assert.Equal(((SolidColorBrush)selectedBackground).Color,
                            Assert.IsType<SolidColorBrush>(backplate.Background).Color);
                        var selectedBounds = selected.TransformToAncestor(tabs)
                            .TransformBounds(new Rect(selected.RenderSize));
                        var backplateBounds = backplate.TransformToAncestor(tabs)
                            .TransformBounds(new Rect(backplate.RenderSize));
                        Assert.True(selectedBounds.Bottom >= backplateBounds.Top + backplate.BorderThickness.Top,
                            "The selected tab must overlap the backplate edge so they remain visually connected.");
                        Assert.Equal(0, ((Border)selected.Template.FindName("Border", selected)).BorderThickness.Bottom);
                        Assert.Equal(new CornerRadius(4, 4, 0, 0), ((Border)selected.Template.FindName("Border", selected)).CornerRadius);
                        Assert.Equal(Visibility.Collapsed,
                            ((Border)selected.Template.FindName("InsetShadow", selected)).Visibility);
                        var shadow = (Border)other.Template.FindName("InsetShadow", other);
                        Assert.Equal(Visibility.Visible, shadow.Visibility);
                        var stops = Assert.IsType<LinearGradientBrush>(shadow.Background).GradientStops;
                        Assert.True(stops.First().Color.A < stops.Last().Color.A,
                            "The inactive tab's inner shadow must darken toward the tab's base.");
                    }
                }
                // The selected tab's semibold label must not make the capture inside it bold.
                Assert.Same(second, tabs.SelectedItem);
                Assert.Equal(FontWeights.Normal, secondContent.FontWeight);
            });
        }

        [Theory]
        [InlineData("TextPrimaryBrush", "SurfacePanelBrush")]
        [InlineData("TextControlBrush", "SurfaceControlBrush")]
        [InlineData("TextControlBrush", "SurfaceHoverBrush")]
        [InlineData("TextControlBrush", "AccentSoftBrush")]
        [InlineData("TextSecondaryBrush", "SurfaceCanvasBrush")]
        [InlineData("TextPrimaryBrush", "SurfaceCanvasBrush")]
        public void DarkTheme_TextMeetsMinimumContrast(string foreground, string background)
        {
            _application.Run(_ =>
            {
                var theme = Theme("Dark");
                double contrast = Contrast((Brush)theme[foreground], (Brush)theme[background]);
                Assert.True(contrast >= 4.5, $"{foreground} on {background}: {contrast:F2}:1");
            });
        }

        private static double Luminance(Color color)
        {
            static double Linear(byte component)
            {
                double value = component / 255.0;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }

        [Theory]
        [InlineData("Light")]
        [InlineData("Dark")]
        public void InspectorAndFormatTabs_ShareTheToolbarTextSizeAndKeepFormatsConnected(string theme)
        {
            _application.Run(window =>
            {
                window.Resources.MergedDictionaries.Add(Theme(theme));
                var view = new RequestResponseView
                {
                    Title = "Response",
                    FirstLineAndHeaders = "HTTP/1.1 429 Too Many Requests\r\nRetry-After: 1",
                    BodyText = "{\"error\":\"rate_limited\"}"
                };
                WpfTestHost.Show(window, view);
                var tabs = (TabControl)view.FindName("RequestTabs");
                var headers = (TabItem)view.FindName("HeadersTab");
                var body = (TabItem)view.FindName("BodyTab");
                var json = (TabItem)view.FindName("JsonTab");
                Assert.Same(window.FindResource("FormatTabControlStyle"), tabs.Style);
                Assert.Equal(28, headers.MinHeight);
                Assert.Equal(new Thickness(), tabs.Padding);
                var well = (Brush)window.FindResource("SurfaceWellBrush");
                var formatWidths = new[] { headers.ActualWidth, body.ActualWidth, json.ActualWidth };
                foreach (var selected in new[] { headers, body, json })
                {
                    tabs.SelectedItem = selected;
                    WpfTestHost.Pump();
                    Assert.Equal(formatWidths, new[] { headers.ActualWidth, body.ActualWidth, json.ActualWidth });
                    // The selected format takes the well's color, so it reads as the top of the well below it.
                    var selectedSurface = (Border)selected.Template.FindName("TabSurface", selected);
                    Assert.Equal(((SolidColorBrush)well).Color, Assert.IsType<SolidColorBrush>(selectedSurface.Background).Color);
                    Assert.Equal(new CornerRadius(4, 4, 0, 0), selectedSurface.CornerRadius);
                    Assert.Null(selected.Template.FindName("TabShadow", selected));
                    var inactive = selected == headers ? body : headers;
                    var inactiveSurface = (Border)inactive.Template.FindName("TabSurface", inactive);
                    Assert.Equal(0, Assert.IsType<SolidColorBrush>(inactiveSurface.Background).Color.A);
                    var surface = (Border)tabs.Template.FindName("ContentPanel", tabs);
                    var headerBounds = selected.TransformToAncestor(tabs).TransformBounds(new Rect(selected.RenderSize));
                    var surfaceBounds = surface.TransformToAncestor(tabs).TransformBounds(new Rect(surface.RenderSize));
                    Assert.InRange(Math.Abs(headerBounds.Bottom - surfaceBounds.Top), 0, 1);
                }
                var inspector = new TabControl
                {
                    Style = (Style)window.FindResource("InspectorTabControlStyle"),
                    Items = { new TabItem { Header = "Request/Response" }, new TabItem { Header = "WebSockets" } }
                };
                WpfTestHost.Show(window, inspector);
                inspector.SelectedIndex = 0;
                WpfTestHost.Pump();
                // Inspector and format tabs use the toolbar buttons' text size; the inspector's accent underline sets
                // its tabs apart. The selected view keeps the same size, in semibold.
                var active = (TabItem)inspector.Items[0];
                var other = (TabItem)inspector.Items[1];
                Assert.Equal(28, active.MinHeight);
                var activeLabel = (ContentPresenter)active.Template.FindName("HeaderSite", active);
                var otherLabel = (ContentPresenter)other.Template.FindName("HeaderSite", other);
                double buttonSize = (double)window.FindResource("BaseFontSize");
                Assert.Equal(12, buttonSize);
                Assert.Equal(buttonSize, TextElement.GetFontSize(activeLabel));
                Assert.Equal(FontWeights.SemiBold, TextElement.GetFontWeight(activeLabel));
                Assert.Equal(buttonSize, TextElement.GetFontSize(otherLabel));
                Assert.Equal(FontWeights.Normal, TextElement.GetFontWeight(otherLabel));
                Assert.Equal(buttonSize, TextElement.GetFontSize((ContentPresenter)body.Template.FindName("HeaderSite", body)));
                Assert.Equal(Visibility.Visible, ((Border)active.Template.FindName("SelectionIndicator", active)).Visibility);
            });
        }

        [Theory]
        [InlineData("Light")]
        [InlineData("Dark")]
        public void QuietLabels_KeepTheirOwnColorsUnderTheAppWideTextStyle(string theme)
        {
            _application.Run(window =>
            {
                // The app-wide TextBlock style (in application resources, as in the app) reaches into templates, so tab,
                // column header and status bar labels have to reset it to show their own, quieter colors.
                window.Resources.MergedDictionaries.Add(Theme(theme));
                window.Resources.MergedDictionaries.Add(AccessibilityTests.AppResources(
                    "TitleCaseConverter", "DataGridColumnHeader", "ColumnHeaderGripperStyle", "TrafficGridHeaderStyle",
                    "StatusBar", "MainStatusBarStyle", "ToolTip"));
                Color Of(string key) => ((SolidColorBrush)window.FindResource(key)).Color;
                TextBlock Label(DependencyObject root, string text) =>
                    Descendants(root).OfType<TextBlock>().Single(block => block.Text == text);
                Color ColorOf(TextBlock label) => Assert.IsType<SolidColorBrush>(label.Foreground).Color;

                var inspector = new TabControl
                {
                    Style = (Style)window.FindResource("InspectorTabControlStyle"),
                    Items = { new TabItem { Header = "Request/Response" }, new TabItem { Header = "WebSockets" } }
                };
                var grid = new DataGrid
                {
                    AutoGenerateColumns = false, ItemsSource = new[] { "a" }, Height = 80,
                    ColumnHeaderStyle = (Style)window.FindResource("TrafficGridHeaderStyle")
                };
                grid.Columns.Add(new DataGridTextColumn { Header = "STATUS", Binding = new System.Windows.Data.Binding(".") });
                var statusBar = new StatusBar
                {
                    Style = (Style)window.FindResource("MainStatusBarStyle"),
                    Items = { new StatusBarItem { Content = new TextBlock { Text = "Capture stopped" } } }
                };
                var tipTarget = new Button { Content = "Target" };
                WpfTestHost.Show(window, new StackPanel { Children = { inspector, grid, statusBar, tipTarget } });
                inspector.SelectedIndex = 0;
                WpfTestHost.Pump();

                Assert.Equal(Of("TextHeadingBrush"), ColorOf(Label(inspector, "Request/Response")));
                Assert.Equal(Of("TextSecondaryBrush"), ColorOf(Label(inspector, "WebSockets")));
                Assert.Equal(Of("TextSecondaryBrush"), ColorOf(Label(grid, "Status")));
                Assert.Equal(Of("TextSecondaryBrush"), ColorOf(Label(statusBar, "Capture stopped")));

                // Tooltips take the menu surface, so their text reads in both themes.
                var tip = new ToolTip
                {
                    Content = "Resize the column",
                    Style = (Style)window.FindResource(typeof(ToolTip)),
                    PlacementTarget = tipTarget,
                    Placement = PlacementMode.Bottom
                };
                tip.Resources.MergedDictionaries.Add(Theme(theme));
                tip.IsOpen = true;
                WpfTestHost.Pump();
                try
                {
                    Assert.Equal(((SolidColorBrush)tip.FindResource("SurfaceCanvasBrush")).Color,
                        Assert.IsType<SolidColorBrush>(tip.Background).Color);
                    var tipLabel = Label(tip, "Resize the column");
                    Assert.Equal(((SolidColorBrush)tip.FindResource("TextPrimaryBrush")).Color, ColorOf(tipLabel));
                    Assert.True(Contrast(tipLabel.Foreground, tip.Background) >= 4.5, "Tooltip text must meet 4.5:1 contrast.");
                }
                finally
                {
                    tip.IsOpen = false;
                }
            });
        }

        [Fact]
        public void AnalysisEmptyState_FollowsSelectedDataAndSharedCaptureEligibility()
        {
            _application.Run(window =>
            {
                var model = new CaptureAppModel();
                var view = new CaptureAnalysisControl();
                window.DataContext = model;
                window.CommandBindings.Add(new CommandBinding(AppCommands.AnalyzeCaptures,
                    (_, _) => { }, (_, e) => e.CanExecute = model.CanAnalyzeCaptures));
                WpfTestHost.Show(window, view);
                var empty = (Border)view.FindName("AnalysisEmptyState");
                var message = (TextBlock)view.FindName("AnalysisEmptyMessage");
                var button = (Button)view.FindName("AnalyzeEmptyCaptureButton");
                Assert.Equal(Visibility.Visible, empty.Visibility);
                Assert.Equal(Localization.GetLocalizedString("ANALYSIS_EMPTY_CAPTURE"), message.Text);
                Assert.Equal(Visibility.Collapsed, button.Visibility);

                var proxy = Proxy(9000);
                var context = new Context(proxy);
                model.CaptureDeviceContexts.Add(context);
                model.SelectedDeviceContext = context;
                WpfTestHost.Pump();
                Assert.Equal(Visibility.Collapsed, button.Visibility);
                proxy.ProxyConnections.Add(new ProxyConnectionModel(1, 1, 1));
                WpfTestHost.Pump();
                Assert.Equal(Localization.GetLocalizedString("ANALYSIS_EMPTY_READY"), message.Text);
                Assert.Equal(Visibility.Visible, button.Visibility);
                Assert.True(button.IsEnabled);
                Assert.Same(AppCommands.AnalyzeCaptures, button.Command);
                Assert.Same(window, button.CommandTarget);

                var other = Proxy(9100);
                model.CaptureDeviceContexts.Add(new Context(other));
                other.IsRunning = true;
                WpfTestHost.Pump();
                Assert.Equal(Localization.GetLocalizedString("ANALYSIS_EMPTY_STOP"), message.Text);
                Assert.False(button.IsEnabled);
                other.IsRunning = false;
                WpfTestHost.Pump();
                Assert.True(button.IsEnabled);

                var tabs = (TabControl)view.FindName("CaptureAnalysisResultsTabs");
                tabs.ContentTemplateSelector = null;
                model.AnalysisRuns.Add(new AnalysisRunModel(null, 1) { Header = "Results" });
                WpfTestHost.Pump();
                Assert.Equal(Visibility.Collapsed, empty.Visibility);
                model.AnalysisRuns.Clear();
                proxy.ProxyConnections.Clear();
                WpfTestHost.Pump();
                Assert.Equal(Visibility.Visible, empty.Visibility);
                Assert.Equal(Localization.GetLocalizedString("ANALYSIS_EMPTY_CAPTURE"), message.Text);
                Assert.Equal(Visibility.Collapsed, button.Visibility);
                model.SelectedDeviceContext = null;
                model.CaptureDeviceContexts.Clear();
                window.DataContext = null;
            });
        }

        private static double Contrast(Brush foreground, Brush background)
        {
            Color text = Assert.IsType<SolidColorBrush>(foreground).Color;
            return BackgroundColors(background).Min(color => ColorContrast(text, color));
        }

        private static double ColorContrast(Color first, Color second)
        {
            double a = Luminance(first), b = Luminance(second);
            return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
        }

        // The colors behind a label: a solid fill, or a vertical gradient across the middle half, where text sits.
        private static IEnumerable<Color> BackgroundColors(Brush background)
        {
            if (background is LinearGradientBrush gradient)
            {
                return new[] { 0.25, 0.5, 0.75 }.Select(offset => ColorAt(gradient.GradientStops, offset));
            }

            return new[] { Assert.IsType<SolidColorBrush>(background).Color };
        }

        private static Color ColorAt(GradientStopCollection stops, double offset)
        {
            var ordered = stops.OrderBy(stop => stop.Offset).ToList();
            var after = ordered.FirstOrDefault(stop => stop.Offset >= offset) ?? ordered[^1];
            var before = ordered.LastOrDefault(stop => stop.Offset <= offset) ?? ordered[0];
            double span = after.Offset - before.Offset;
            double t = span <= 0 ? 0 : (offset - before.Offset) / span;
            byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * t);
            return Color.FromRgb(Mix(before.Color.R, after.Color.R), Mix(before.Color.G, after.Color.G),
                Mix(before.Color.B, after.Color.B));
        }

        [Theory]
        [InlineData("Light")]
        [InlineData("Dark")]
        public void DisabledButtonsAndNestedText_AreGrayAndFollowThemeChanges(string theme)
        {
            _application.Run(window =>
            {
                AddButtonStyles(window.Resources);
                var dictionary = Theme(theme);
                window.Resources.MergedDictionaries.Add(dictionary);
                var text = new TextBlock { Text = "Explicit button text" };
                var normal = new Button { Content = "Normal button" };
                var nested = new Button { Content = text };
                var toggleText = new TextBlock { Text = "Toggle text" };
                var toggle = new ToggleButton { Content = toggleText };
                var toolbarText = new TextBlock { Text = "Toolbar text" };
                var toolbarButton = new Button { Content = toolbarText };
                var toolbarToggleText = new TextBlock { Text = "Toolbar toggle text" };
                var toolbarToggle = new ToggleButton { Content = toolbarToggleText };
                var toolbar = new ToolBar();
                toolbar.Items.Add(toolbarButton);
                toolbar.Items.Add(toolbarToggle);
                var action = new MenuItem { Header = "Disabled action", IsEnabled = false };
                var menu = new Menu();
                menu.Items.Add(action);
                var content = new StackPanel();
                content.Children.Add(normal);
                content.Children.Add(nested);
                content.Children.Add(toggle);
                content.Children.Add(toolbar);
                content.Children.Add(menu);
                WpfTestHost.Show(window, content);
                var buttons = new ButtonBase[] { normal, nested, toggle, toolbarButton, toolbarToggle };
                foreach (var button in buttons)
                {
                    button.IsEnabled = false;
                }
                WpfTestHost.Pump();
                var gray = ((SolidColorBrush)window.FindResource("TextDisabledBrush")).Color;
                foreach (var button in buttons)
                {
                    Assert.Equal(gray, ((SolidColorBrush)button.Foreground).Color);
                    Assert.All(Descendants(button).OfType<TextBlock>(),
                        label => Assert.Equal(gray, ((SolidColorBrush)label.Foreground).Color));
                }
                // A gray from the palette: its slight cool tint stays under 0.01 chroma, which reads as neutral.
                Assert.True(DesignSystem.PaletteGenerator.OkLch($"#{gray.R:X2}{gray.G:X2}{gray.B:X2}").C < 0.01,
                    $"Disabled text {gray} must be gray.");
                Assert.Equal(gray, ((SolidColorBrush)action.Foreground).Color);
                window.Resources.MergedDictionaries.Remove(dictionary);
                window.Resources.MergedDictionaries.Add(Theme(theme == "Dark" ? "Light" : "Dark"));
                WpfTestHost.Pump();
                foreach (var button in buttons)
                {
                    Assert.Equal(
                        ((SolidColorBrush)window.FindResource("TextDisabledBrush")).Color,
                        ((SolidColorBrush)button.Foreground).Color);
                    button.IsEnabled = true;
                }
                WpfTestHost.Pump();
                Assert.NotEqual(gray, ((SolidColorBrush)normal.Foreground).Color);
                Assert.NotEqual(gray, ((SolidColorBrush)text.Foreground).Color);
                Assert.NotEqual(gray, ((SolidColorBrush)toolbarToggleText.Foreground).Color);
            });
        }

        private static WebServiceDeviceCaptureController Proxy(ushort port, bool readOnly = false) =>
            new("UI test", DeviceType.LocalPC, readOnly, new ProxyPortPool()) { Port = port };

        private static ResourceDictionary Theme(string name) => new()
        {
            Source = new Uri($"pack://application:,,,/XboxMultiplayerAnalysisTool;component/Themes/{name}Theme.xaml")
        };

        internal static XDocument ProductMarkup(string name)
        {
            using var stream = typeof(CaptureChromeTests).Assembly.GetManifestResourceStream("XMAT.Tests." + name);
            Assert.NotNull(stream);
            return XDocument.Load(stream);
        }

        internal static T Parse<T>(XElement source)
        {
            var element = new XElement(source);
            var namespaceMap = new Dictionary<string, string>();
            element.Attribute(Xaml + "Key")?.Remove();
            var windowEvents = new[] { "Click", "Checked", "Unchecked", "DeviceTabClosed", "AnalysisTabAdded", "AnalysisTabClosed" };
            foreach (var attribute in element.DescendantsAndSelf().Attributes()
                .Where(attribute => windowEvents.Contains(attribute.Name.LocalName)).ToArray())
            {
                attribute.Remove();
            }
            foreach (var declaration in source.Document.Root.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            {
                var value = declaration.Value;
                if (value.StartsWith("clr-namespace:", StringComparison.Ordinal) && !value.Contains(";assembly=", StringComparison.Ordinal))
                {
                    value += ";assembly=XboxMultiplayerAnalysisTool";
                    namespaceMap[declaration.Value] = value;
                }
                element.SetAttributeValue(declaration.Name, value);
            }
            foreach (var child in element.DescendantsAndSelf())
            {
                if (namespaceMap.TryGetValue(child.Name.NamespaceName, out var qualifiedNamespace))
                {
                    child.Name = XName.Get(child.Name.LocalName, qualifiedNamespace);
                }
                // Attached properties written as attributes, such as local:LiveRegion.Mode, need the assembly too.
                foreach (var attribute in child.Attributes()
                    .Where(attribute => !attribute.IsNamespaceDeclaration && namespaceMap.ContainsKey(attribute.Name.NamespaceName))
                    .ToArray())
                {
                    attribute.Remove();
                    child.SetAttributeValue(
                        XName.Get(attribute.Name.LocalName, namespaceMap[attribute.Name.NamespaceName]), attribute.Value);
                }
            }
            return (T)XamlReader.Parse(element.ToString());
        }

        internal static void AddButtonStyles(ResourceDictionary resources)
        {
            // The new look merges the menus' surface and labels, which its menu styles build on.
            resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/XboxMultiplayerAnalysisTool;component/Styles/Menus.xaml")
            });
            var dictionary = NewLookMarkup.Document.Root;
            foreach (var primitive in dictionary.Elements()
                .Where(element => element.Name == Presentation + "CornerRadius" ||
                    element.Name == Presentation + "Thickness" ||
                    element.Name == Presentation + "ControlTemplate" ||
                    element.Name == Sys + "Double"))
            {
                resources[(string)primitive.Attribute(Xaml + "Key")] = Parse<object>(primitive);
            }
            var styles = dictionary.Elements(Presentation + "Style")
                .Where(style => new[] { "Button", "ToggleButton", "TextBlock", "TextBox", "ScrollViewer", "ContentControl",
                    "TabItem", "TabControl", "{x:Type TabItem}", "{x:Type TabControl}", "{x:Type MenuItem}", "ContextMenu" }
                    .Contains((string)style.Attribute("TargetType")));
            foreach (var source in styles)
            {
                var style = Parse<Style>(source);
                var key = (string)source.Attribute(Xaml + "Key");
                object resourceKey = key switch
                {
                    "{x:Static ToolBar.ButtonStyleKey}" => ToolBar.ButtonStyleKey,
                    "{x:Static ToolBar.ToggleButtonStyleKey}" => ToolBar.ToggleButtonStyleKey,
                    _ when key != null => key,
                    _ => style.TargetType
                };
                resources[resourceKey] = style;
            }
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject source)
        {
            yield return source;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(source); i++)
            {
                foreach (var child in Descendants(VisualTreeHelper.GetChild(source, i)))
                {
                    yield return child;
                }
            }
        }

        private sealed class DraggableGridSplitter : GridSplitter
        {
            internal void SetDragging(bool dragging) => IsDragging = dragging;
        }

        private sealed class Context : ICaptureDeviceContext
        {
            public DeviceType DeviceType => DeviceType.LocalPC;
            public CaptureType CaptureType { get; }
            public string CaptureTypeString => CaptureType.ToString();
            public string DeviceName => "UI test";
            public bool IsSelected { get; set; }
            public bool IsReadOnly { get; set; }
            public System.Drawing.Color DeviceColor { get; set; }
            public IDeviceCaptureController CaptureController { get; }
            internal WebServiceDeviceCaptureController Proxy => (WebServiceDeviceCaptureController)CaptureController;
            internal Context(IDeviceCaptureController controller, CaptureType captureType = CaptureType.WebProxy)
            {
                CaptureController = controller;
                CaptureType = captureType;
            }
        }

        private sealed class OtherController : IDeviceCaptureController
        {
            public bool IsRunning => false;
            public ICaptureMethod CaptureMethod => null;
            public object Parameters => null;
            public event PropertyChangedEventHandler PropertyChanged { add { } remove { } }
            public void Initialize() { }
            public void LoadCaptures(IEnumerable<IDataset> tables) { }
            public void ClearAllCaptures() { }
            public Task<bool> CanCloseAsync() => Task.FromResult(true);
            public Task CloseAsync() => Task.CompletedTask;
        }
    }
}
