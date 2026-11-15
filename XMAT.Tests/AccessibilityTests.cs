// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using XMAT.Models;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class AccessibilityTests
    {
        private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        private readonly CaptureUiApplicationFixture _application;

        public AccessibilityTests(CaptureUiApplicationFixture application) => _application = application;

        [Fact]
        public void SelectedRows_KeepTheirStatusColorAndShiftItOnlyToStayReadable()
        {
            var pending = Color.FromRgb(0xFF, 0xCB, 0x3D);
            var darkSelection = Color.FromRgb(0x24, 0x47, 0x6B);
            Assert.Equal(pending, SelectionForegroundConverter.Readable(pending, darkSelection));
            var faint = Color.FromRgb(0x3A, 0x6E, 0xA5);
            Color shifted = SelectionForegroundConverter.Readable(faint, darkSelection);
            Assert.InRange(SelectionForegroundConverter.Contrast(shifted, darkSelection), 4.5, 6);

            _application.Run(window =>
            {
                window.Resources.Remove("TextPrimaryBrush");
                window.Resources.Remove("TextSecondaryBrush");
                window.Resources.MergedDictionaries.Add(Theme("Dark"));
                window.Resources.MergedDictionaries.Add(AppResources("SelectionForegroundConverter", "DataGridCell"));
                var grid = new DataGrid
                {
                    AutoGenerateColumns = false, IsReadOnly = true, ItemsSource = new[] { "pending", "done" },
                    RowStyle = new Style(typeof(DataGridRow))
                    {
                        Setters = { new Setter(Control.ForegroundProperty, new SolidColorBrush(pending)) }
                    }
                };
                grid.Columns.Add(new DataGridTextColumn { Binding = new Binding(".") });
                WpfTestHost.Show(window, grid);

                grid.SelectedIndex = 0;
                WpfTestHost.Pump();
                var cell = Descendants(grid).OfType<DataGridCell>().First(cell => cell.IsSelected);
                Assert.Equal(pending, Assert.IsType<SolidColorBrush>(cell.Foreground).Color);
                Assert.True(SelectionForegroundConverter.Contrast(pending,
                    Assert.IsType<SolidColorBrush>(cell.Background).Color) >= 4.5);
            });
        }

        [Fact]
        public void ActionToggles_AreButtonsNamedForTheActionTheyShow()
        {
            _application.Run(window =>
            {
                var toggle = new ActionToggleButton { Content = "Start Capture" };
                WpfTestHost.Show(window, toggle);
                var peer = UIElementAutomationPeer.CreatePeerForElement(toggle);
                Assert.Equal(AutomationControlType.Button, peer.GetAutomationControlType());
                Assert.Equal("Start Capture", peer.GetName());
                Assert.Null(peer.GetPattern(PatternInterface.Toggle));

                ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
                WpfTestHost.Pump();
                Assert.True(toggle.IsChecked);
            });
        }

        [Fact]
        public void AnalysisTabs_AreNamedForTheirResultsAndCloseFromTheKeyboard()
        {
            _application.Run(window =>
            {
                var model = new CaptureAppModel();
                var view = new CaptureAnalysisControl();
                window.DataContext = model;
                WpfTestHost.Show(window, view);
                var tabs = (TabControl)view.FindName("CaptureAnalysisResultsTabs");
                tabs.ContentTemplateSelector = null;
                var run = new AnalysisRunModel(null, 1) { Header = "Results" };
                model.AnalysisRuns.Add(run);
                tabs.SelectedItem = run;
                WpfTestHost.Pump();

                var tab = (TabItem)tabs.ItemContainerGenerator.ContainerFromItem(run);
                // Screen readers read the tab control's item peers.
                Assert.Equal("Results", UIElementAutomationPeer.CreatePeerForElement(tabs).GetChildren()
                    .OfType<ItemAutomationPeer>().Single(peer => peer.Item == run).GetName());
                var close = Descendants(tab).OfType<Button>().Single(button => button.Name == "CloseButton");
                Assert.Equal(Localization.GetLocalizedString("TAB_CLOSE_NAME", "Results"), AutomationProperties.GetName(close));
                Assert.Equal(AutomationProperties.GetName(close), close.ToolTip);

                AnalysisRunModel closed = null;
                view.AnalysisTabClosed += (_, e) => closed = ((AnalysisTabClosedRoutedEventArgs)e).AnalysisRun;
                foreach (Key key in new[] { Key.F4, Key.W })
                {
                    var binding = view.InputBindings.OfType<KeyBinding>()
                        .Single(binding => binding.Key == key && binding.Modifiers == ModifierKeys.Control);
                    var command = (RoutedCommand)binding.Command;
                    Assert.Same(run, binding.CommandParameter);
                    run.IsProcessing = true;
                    Assert.False(command.CanExecute(binding.CommandParameter, view), "A running analysis can't be closed.");
                    run.IsProcessing = false;
                    Assert.True(command.CanExecute(binding.CommandParameter, view));
                    closed = null;
                    command.Execute(binding.CommandParameter, view);
                    Assert.Same(run, closed);
                }
                window.DataContext = null;
            });
        }

        [Fact]
        public void CaptureTabs_AreNamedForTheirDeviceAndCapture()
        {
            var tab = new CaptureDeviceContextModel(DeviceType.GenericProxyDevice, "Console", CaptureType.WebProxy, true, null,
                deferPortAllocation: true, proxyFactory: () => new WebServiceProxy());
            Assert.Equal("Console: " + tab.CaptureTypeString + Localization.GetLocalizedString("CAPTURE_DEVICES_TAB_READONLY"),
                tab.ToString());
        }

        [Fact]
        public void CaptureTabs_ShowScreenReadersTheSelectedTabsContent()
        {
            _application.Run(window =>
            {
                var tabs = new TabControlEx
                {
                    Template = (ControlTemplate)XamlReader.Parse(
                        $"<ControlTemplate TargetType=\"TabControl\" xmlns=\"{Presentation.NamespaceName}\" xmlns:x=\"{Xaml.NamespaceName}\">" +
                        "<DockPanel><TabPanel DockPanel.Dock=\"Top\" IsItemsHost=\"True\"/><Grid x:Name=\"PART_ItemsHolder\"/></DockPanel>" +
                        "</ControlTemplate>")
                };
                tabs.Items.Add(new TabItem { Header = "First", Content = new Button { Content = "Inside first" } });
                tabs.Items.Add(new TabItem { Header = "Second", Content = new Button { Content = "Inside second" } });
                WpfTestHost.Show(window, tabs);
                tabs.SelectedIndex = 1;
                WpfTestHost.Pump();

                List<string> Names(AutomationPeer tab) => (tab.GetChildren() ?? new List<AutomationPeer>())
                    .Select(peer => peer.GetName()).ToList();
                var items = UIElementAutomationPeer.CreatePeerForElement(tabs).GetChildren().OfType<TabItemAutomationPeer>().ToList();
                Assert.Equal(2, items.Count);
                Assert.Contains("Inside second", Names(items[1]));
                Assert.DoesNotContain("Inside first", Names(items[0]));

                tabs.SelectedIndex = 0;
                WpfTestHost.Pump();
                items = UIElementAutomationPeer.CreatePeerForElement(tabs).GetChildren().OfType<TabItemAutomationPeer>().ToList();
                Assert.Contains("Inside first", Names(items[0]));
                Assert.DoesNotContain("Inside second", Names(items[1]));
            });
        }

        [Fact]
        public void FilterDropdowns_MoveBetweenCheckBoxesFromTheKeyboardAndKeepTheirName()
        {
            _application.Run(window =>
            {
                var items = new ObservableCollection<CheckedListItem> { new("200"), new("404") };
                var filters = new CheckBoxFilters { FilterDesc = "Status", ItemsSource = items };
                WpfTestHost.Show(window, filters);
                var list = (ComboBox)filters.FindName("FilterList");
                Assert.Equal("Status", AutomationProperties.GetName(list));
                Assert.Equal(Localization.GetLocalizedString("A11Y_FILTER_ENABLE_NAME", "Status"),
                    AutomationProperties.GetName((CheckBox)filters.FindName("EnableFilters")));

                Press(list, Key.Down);
                WpfTestHost.Pump();
                Assert.True(list.IsDropDownOpen);
                Assert.Equal("*", Assert.IsType<CheckBox>(Keyboard.FocusedElement).Content);

                Press((UIElement)Keyboard.FocusedElement, Key.Down);
                WpfTestHost.Pump();
                var first = Assert.IsType<CheckBox>(Keyboard.FocusedElement);
                Assert.Equal("200", first.Content);
                ((IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(first).GetPattern(PatternInterface.Toggle)).Toggle();
                WpfTestHost.Pump();
                Assert.False(items[0].IsChecked);

                Press(first, Key.Enter);
                WpfTestHost.Pump();
                Assert.False(list.IsDropDownOpen);
                Assert.Equal("Status", list.Text);

                // A click on a row outside its check box selects the row; the box still shows the filter's name.
                list.SelectedIndex = 2;
                WpfTestHost.Pump();
                Assert.Equal(-1, list.SelectedIndex);
                Assert.Equal("Status", list.Text);
            });
        }

        [Fact]
        public void Tables_TakeOneTabStopNameAbbreviatedColumnsAndOfferSortBy()
        {
            _application.Run(window =>
            {
                window.Resources.MergedDictionaries.Add(
                    AppResources("DataGrid", "DataGridColumnHeader", "ColumnHeaderGripperStyle"));
                var grid = new DataGrid
                {
                    AutoGenerateColumns = false, IsReadOnly = true,
                    ItemsSource = new[] { new Row("beta", 2), new Row("alpha", 1) }
                };
                var name = new DataGridTextColumn { Header = "NAME", Binding = new Binding(nameof(Row.Name)) };
                var count = new DataGridTextColumn { Header = "N", Binding = new Binding(nameof(Row.Count)) };
                DataGridColumnInfo.SetDescription(count, "Count of calls");
                var go = new DataGridTemplateColumn
                {
                    Header = "Go",
                    CellTemplate = (DataTemplate)XamlReader.Parse(
                        $"<DataTemplate xmlns=\"{Presentation.NamespaceName}\"><Button Content=\"Go\"/></DataTemplate>")
                };
                grid.Columns.Add(name);
                grid.Columns.Add(count);
                grid.Columns.Add(go);
                var after = new Button { Content = "After" };
                WpfTestHost.Show(window, new StackPanel { Children = { grid, after } });

                var headers = Descendants(grid).OfType<DataGridColumnHeader>().Where(header => header.Column != null).ToList();
                var countHeader = headers.Single(header => header.Column == count);
                Assert.Equal("Count of calls", countHeader.ToolTip);
                Assert.Equal("Count of calls", AutomationProperties.GetName(countHeader));
                Assert.Null(headers.Single(header => header.Column == name).ToolTip);

                var cell = Descendants(grid).OfType<DataGridCell>().First();
                Assert.True(cell.Focus());
                Press(cell, Key.Tab);
                var goButton = Descendants(Descendants(grid).OfType<DataGridRow>().First()).OfType<Button>().Single();
                Assert.True(goButton.IsKeyboardFocused, "Tab passes over the row's cells to the controls in them.");
                Press(goButton, Key.Tab);
                Assert.True(after.IsKeyboardFocused, "Tab leaves the table after the row's last control.");

                var menu = grid.ContextMenu;
                Assert.NotNull(menu);
                Assert.True(DataGridSortMenu.AddSortItems(grid, menu));
                Assert.True(DataGridSortMenu.AddSortItems(grid, menu));
                var sortBy = Assert.Single(menu.Items.OfType<MenuItem>());
                Assert.Equal(new[] { "Name", "Count of calls" }, sortBy.Items.OfType<MenuItem>().Select(item => (string)item.Header));
                sortBy.Items.OfType<MenuItem>().First().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                WpfTestHost.Pump();
                Assert.Equal(ListSortDirection.Ascending, name.SortDirection);
                Assert.Equal("alpha", ((Row)grid.Items[0]).Name);
            });
        }

        [Fact]
        public void TableHeaders_MarkEachColumnEdgeButNotTheFiller()
        {
            _application.Run(window =>
            {
                window.Resources.MergedDictionaries.Add(AppResources("DataGridColumnHeader", "ColumnHeaderGripperStyle"));
                var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, ItemsSource = new[] { "a" }, Width = 400 };
                grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new Binding("."), Width = 80 });
                grid.Columns.Add(new DataGridTextColumn { Header = "Value", Binding = new Binding("."), Width = 80 });
                WpfTestHost.Show(window, grid);
                var headers = Descendants(grid).OfType<DataGridColumnHeader>().ToList();
                Visibility DividerOf(DataGridColumnHeader header) => ((Border)header.Template.FindName("Divider", header)).Visibility;

                Assert.Equal(new[] { Visibility.Visible, Visibility.Visible }, headers.Where(h => h.Column != null).Select(DividerOf));
                Assert.Equal(Visibility.Collapsed, DividerOf(headers.Single(h => h.Column == null)));
            });
        }

        [Fact]
        public void Tables_OutlineAFocusedCellUntilItsRowIsSelected()
        {
            _application.Run(window =>
            {
                window.Resources.MergedDictionaries.Add(AppResources("SelectionForegroundConverter", "DataGridCell"));
                var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, ItemsSource = new[] { "a", "b" } };
                grid.Columns.Add(new DataGridTextColumn { Binding = new Binding(".") });
                WpfTestHost.Show(window, grid);
                var cell = Descendants(grid).OfType<DataGridCell>().First();
                var outline = (Border)cell.Template.FindName("FocusOutline", cell);
                Assert.Equal(Visibility.Collapsed, outline.Visibility);

                Assert.True(cell.Focus());
                WpfTestHost.Pump();
                Assert.False(cell.IsSelected);
                Assert.Equal(Visibility.Visible, outline.Visibility);

                // The selected row shows the focus from here.
                grid.SelectedIndex = 0;
                WpfTestHost.Pump();
                Assert.Equal(Visibility.Collapsed, outline.Visibility);
            });
        }

        [Fact]
        public void OriginalLookTables_TakeTheCurrentThemesInactiveSelection()
        {
            _application.Run(window =>
            {
                string previous = ThemeManager.CurrentTheme;
                var look = new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/XboxMultiplayerAnalysisTool;component/Looks/OriginalLook.xaml")
                };
                try
                {
                    ThemeManager.PrepareTheme(look, "Light", newLook: false, highContrast: false);
                    window.Resources.MergedDictionaries.Add(look);
                    var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, ItemsSource = new[] { "a", "b" } };
                    grid.Columns.Add(new DataGridTextColumn { Binding = new Binding(".") });
                    var elsewhere = new Button { Content = "Elsewhere" };
                    WpfTestHost.Show(window, new StackPanel { Children = { grid, elsewhere } });
                    grid.SelectedIndex = 0;
                    Assert.True(elsewhere.Focus());
                    WpfTestHost.Pump();
                    Color Fill() => Assert.IsType<SolidColorBrush>(
                        Descendants(grid).OfType<DataGridCell>().First(cell => cell.IsSelected).Background).Color;
                    Color Expected() => ((SolidColorBrush)look["InactiveSelectionBrush"]).Color;

                    Assert.Equal(Colors.LightBlue, Fill());
                    foreach (var (theme, highContrast) in new[] { ("Dark", false), ("Light", true), ("Light", false) })
                    {
                        ThemeManager.PrepareTheme(look, theme, newLook: false, highContrast);
                        WpfTestHost.Pump();
                        Assert.Equal(Expected(), Fill());
                    }
                }
                finally
                {
                    ThemeManager.PrepareTheme(new ResourceDictionary(), previous, newLook: false, highContrast: false);
                }
            });
        }

        [Fact]
        public void SortBy_LeavesRowMenusToTheirViewsAndSkipsTablesWithoutHeaders()
        {
            _application.Run(window =>
            {
                // A row's own menu, filled later by its view, must still open when no column can be sorted.
                var rowMenu = new ContextMenu();
                var grid = new DataGrid
                {
                    AutoGenerateColumns = false, IsReadOnly = true, ItemsSource = new[] { "a" },
                    RowStyle = new Style(typeof(DataGridRow)) { Setters = { new Setter(FrameworkElement.ContextMenuProperty, rowMenu) } }
                };
                grid.Columns.Add(new DataGridTemplateColumn { Header = "Endpoint", CellTemplate = new DataTemplate() });
                DataGridSortMenu.SetIsEnabled(grid, true);
                WpfTestHost.Show(window, grid);
                var row = Descendants(grid).OfType<DataGridRow>().First();
                Assert.True(DataGridSortMenu.PrepareMenu(grid, row));
                Assert.Empty(rowMenu.Items);
                // The empty menu the table got for Sort by stays closed when there is nothing to sort.
                Assert.False(DataGridSortMenu.PrepareMenu(grid, grid));

                // With its headers hidden a table can't sort, so it lists no unnamed Sort by items.
                var headless = new DataGrid
                {
                    AutoGenerateColumns = false, HeadersVisibility = DataGridHeadersVisibility.None, ItemsSource = new[] { "a" }
                };
                headless.Columns.Add(new DataGridTextColumn { Binding = new Binding(".") });
                Assert.Empty(DataGridSortMenu.SortableColumns(headless));
            });
        }

        [Fact]
        public void CapturingIndicator_ShowsAndAnnouncesWhileACaptureRuns()
        {
            _application.Run(window =>
            {
                window.Resources.MergedDictionaries.Add(AppResources("RecordingIndicatorStyle"));
                var announced = new List<UIElement>();
                void Record(UIElement element) => announced.Add(element);
                LiveRegion.Announced += Record;
                try
                {
                    var indicator = new RecordingIndicator();
                    indicator.SetResourceReference(FrameworkElement.StyleProperty, "RecordingIndicatorStyle");
                    WpfTestHost.Show(window, new StackPanel { Children = { indicator } });
                    Assert.Equal(Visibility.Collapsed, indicator.Visibility);
                    Assert.False(indicator.IsPulsing);

                    indicator.IsActive = true;
                    WpfTestHost.Pump();
                    Assert.Equal(Visibility.Visible, indicator.Visibility);
                    var label = (TextBlock)indicator.Template.FindName("Label", indicator);
                    Assert.Equal(Localization.GetLocalizedString("CAPTURE_CAPTURING"), label.Text);
                    Assert.Equal(new UIElement[] { label }, announced);
                    // The dot pulses, and holds still while animations are off.
                    bool? motion = MotionSettings.Current.Override;
                    try
                    {
                        MotionSettings.Current.Override = true;
                        Assert.True(indicator.IsPulsing);
                        MotionSettings.Current.Override = false;
                        Assert.False(indicator.IsPulsing);
                    }
                    finally
                    {
                        MotionSettings.Current.Override = motion;
                    }

                    indicator.IsActive = false;
                    WpfTestHost.Pump();
                    Assert.Equal(Visibility.Collapsed, indicator.Visibility);
                    Assert.False(indicator.IsPulsing);
                }
                finally
                {
                    LiveRegion.Announced -= Record;
                }
            });
        }

        [Fact]
        public void EntryErrors_AreAnnouncedWhenTheyAppear()
        {
            _application.Run(window =>
            {
                var announced = new List<UIElement>();
                void Record(UIElement element) => announced.Add(element);
                LiveRegion.Announced += Record;
                try
                {
                    var policy = new HostListPolicy();
                    policy.BlockList.Add("example.com");
                    var block = new BlockListView { BlockList = policy.BlockList };
                    WpfTestHost.Show(window, block);
                    var error = (TextBlock)block.FindName("EntryErrorText");
                    Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(error));
                    Assert.Empty(announced);

                    ((TextBox)block.FindName("NewBlockTextBox")).Text = "example.com";
                    ((Button)block.FindName("AddUrlBlockButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    WpfTestHost.Pump();
                    Assert.Equal(new UIElement[] { error }, announced);
                }
                finally
                {
                    LiveRegion.Announced -= Record;
                }
            });
        }

        [Fact]
        public void TextScale_SetsFontSizesFromTheirDefinedSizes()
        {
            var resources = new ResourceDictionary { ["BaseFontSize"] = 12.0, ["PaneTitleFontSize"] = 13.0, ["Gap"] = 5.0 };
            TextScale.Apply(resources, 1.5);
            Assert.Equal(18.0, (double)resources["BaseFontSize"]);
            Assert.Equal(19.5, (double)resources["PaneTitleFontSize"]);
            Assert.Equal(5.0, (double)resources["Gap"]);

            TextScale.Apply(resources, 1.0);
            Assert.Equal(12.0, (double)resources["BaseFontSize"]);
        }

        private sealed record Row(string Name, int Count);

        private static void Press(UIElement target, Key key) =>
            target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), 0, key)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            });

        private static ResourceDictionary Theme(string name) => new()
        {
            Source = new Uri($"pack://application:,,,/XboxMultiplayerAnalysisTool;component/Themes/{name}Theme.xaml")
        };

        // The new look's resources chosen by key, or by target type for implicit styles, parsed into one dictionary.
        internal static ResourceDictionary AppResources(params string[] names)
        {
            var document = NewLookMarkup.Document;
            var source = document.Root;
            var dictionary = new XElement(Presentation + "ResourceDictionary", source.Elements()
                .Where(element => names.Contains((string)element.Attribute(Xaml + "Key") ?? (string)element.Attribute("TargetType")))
                .Select(element => new XElement(element)));
            var qualified = new Dictionary<string, string>();
            foreach (var declaration in document.Root.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            {
                string value = declaration.Value;
                if (value.StartsWith("clr-namespace:", StringComparison.Ordinal) && !value.Contains(";assembly=", StringComparison.Ordinal))
                {
                    qualified[value] = value + ";assembly=XboxMultiplayerAnalysisTool";
                    value = qualified[declaration.Value];
                }
                dictionary.SetAttributeValue(declaration.Name, value);
            }
            foreach (var element in dictionary.DescendantsAndSelf())
            {
                if (qualified.TryGetValue(element.Name.NamespaceName, out string elementNamespace))
                {
                    element.Name = XName.Get(element.Name.LocalName, elementNamespace);
                }

                // Attached properties written as attributes, such as local:LiveRegion.Mode, need the assembly too.
                foreach (var attribute in element.Attributes()
                    .Where(attribute => !attribute.IsNamespaceDeclaration && qualified.ContainsKey(attribute.Name.NamespaceName))
                    .ToList())
                {
                    attribute.Remove();
                    element.SetAttributeValue(XName.Get(attribute.Name.LocalName, qualified[attribute.Name.NamespaceName]), attribute.Value);
                }
            }
            return (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
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
