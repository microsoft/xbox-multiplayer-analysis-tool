// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using XMAT.Models;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class CaptureChromeTests
    {
        private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
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
        public void ScriptEditorButtons_InheritThemeAndFollowThemeChanges(string theme)
        {
            _application.Run(window =>
            {
                window.Resources.Remove("PrimaryForegroundBrush");
                window.Resources.Remove("SecondaryForegroundBrush");
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

                foreach (var name in new[] { theme, theme == "Dark" ? "Light" : "Dark" })
                {
                    window.Resources.MergedDictionaries.Clear();
                    window.Resources.MergedDictionaries.Add(Theme(name));
                    foreach (var button in buttons)
                    {
                        button.IsEnabled = true;
                    }
                    WpfTestHost.Pump();

                    foreach (var button in buttons)
                    {
                        Assert.True(button.IsEnabled);
                        var baseStyle = (Style)window.FindResource(button.GetType());
                        Assert.Same(baseStyle, button.Style.BasedOn);
                        var border = Assert.IsType<Border>(button.Template.FindName("Bd", button));
                        Assert.Equal(BrushColor(window.FindResource("ButtonBackgroundBrush")), BrushColor(border.Background));
                        Assert.Equal(BrushColor(window.FindResource("ButtonForegroundBrush")), BrushColor(button.Foreground));
                        Assert.True(Contrast(button.Foreground, border.Background) >= 4.5);
                    }

                    view.ScriptEnabled.IsChecked = true;
                    WpfTestHost.Pump();
                    var checkedBorder = (Border)view.ScriptEnabled.Template.FindName("Bd", view.ScriptEnabled);
                    Assert.Equal(BrushColor(window.FindResource("ButtonPressedBackgroundBrush")), BrushColor(checkedBorder.Background));
                    Assert.All(Descendants(view.ScriptEnabled).OfType<TextBlock>(),
                        text => Assert.True(Contrast(text.Foreground, checkedBorder.Background) >= 4.5));
                    view.ScriptEnabled.IsChecked = false;

                    foreach (var button in buttons)
                    {
                        button.IsEnabled = false;
                    }
                    WpfTestHost.Pump();
                    foreach (var button in buttons)
                    {
                        Assert.Equal(BrushColor(window.FindResource("DisabledForegroundBrush")), BrushColor(button.Foreground));
                        Assert.All(Descendants(button).OfType<TextBlock>(),
                            text => Assert.Equal(BrushColor(button.Foreground), BrushColor(text.Foreground)));
                    }
                }
            });
        }

        private static Color BrushColor(object brush) => Assert.IsType<SolidColorBrush>(brush).Color;

        [Fact]
        public void NetworkTraceControls_InheritTheSharedThemeStyles()
        {
            _application.Run(window =>
            {
                var view = new NetworkTraceCaptureControls.NetworkTraceCaptureView();
                var header = (Style)view.Resources["CenterGridHeaderStyle"];
                Assert.Same(window.FindResource(typeof(DataGridColumnHeader)), header.BasedOn);
                var toolbar = ((Grid)view.Content).Children.OfType<ToolBar>().Single();
                var toggle = toolbar.Items.OfType<ToggleButton>().Single();
                Assert.Same(window.FindResource(ToolBar.ToggleButtonStyleKey), toggle.Style.BasedOn);
            });
        }

        [Fact]
        public void MenuShortcutText_InheritsTheMenuForeground()
        {
            _application.Run(window =>
            {
                AddButtonStyles(window.Resources);
                window.Resources.MergedDictionaries.Add(Theme("Dark"));
                var action = new MenuItem { Header = "Action", InputGestureText = "Ctrl+A" };
                WpfTestHost.Show(window, action);
                var shortcut = (TextBlock)action.Template.FindName("IGT", action);
                Assert.Equal(BrushColor(action.Foreground), BrushColor(shortcut.Foreground));
                action.IsEnabled = false;
                WpfTestHost.Pump();
                Assert.Equal(BrushColor(action.Foreground), BrushColor(shortcut.Foreground));
            });
        }

        [Theory]
        [InlineData("PrimaryForegroundBrush", "WindowBackgroundBrush")]
        [InlineData("PrimaryForegroundBrush", "PanelBackgroundBrush")]
        [InlineData("PrimaryForegroundBrush", "AltRowBackgroundBrush")]
        [InlineData("PrimaryForegroundBrush", "ExpanderHeaderBrush")]
        [InlineData("PrimaryForegroundBrush", "ExpanderSubHeaderBrush")]
        [InlineData("PrimaryForegroundBrush", "ExpanderDetailBrush")]
        [InlineData("SecondaryForegroundBrush", "PanelBackgroundBrush")]
        [InlineData("SecondaryForegroundBrush", "AltRowBackgroundBrush")]
        [InlineData("ButtonForegroundBrush", "ButtonBackgroundBrush")]
        [InlineData("ButtonForegroundBrush", "ButtonHoverBackgroundBrush")]
        [InlineData("ButtonForegroundBrush", "ButtonPressedBackgroundBrush")]
        [InlineData("TextBoxForegroundBrush", "TextBoxBackgroundBrush")]
        [InlineData("MenuForegroundBrush", "MenuBackgroundBrush")]
        [InlineData("MenuForegroundBrush", "ButtonHoverBackgroundBrush")]
        [InlineData("ToolBarForegroundBrush", "ToolBarBackgroundBrush")]
        [InlineData("StatusBarForegroundBrush", "StatusBarBackgroundBrush")]
        [InlineData("PrimaryForegroundBrush", "StatusBarBackgroundBrush")]
        [InlineData("TabItemForegroundBrush", "TabItemBackgroundBrush")]
        [InlineData("TabItemSelectedForegroundBrush", "TabItemSelectedBackgroundBrush")]
        [InlineData("DataGridHeaderForegroundBrush", "DataGridHeaderBackgroundBrush")]
        [InlineData("ActiveSelectionForegroundBrush", "ActiveSelectionBrush")]
        public void DarkTheme_TextMeetsMinimumContrast(string foreground, string background)
        {
            _application.Run(_ =>
            {
                var theme = Theme("Dark");
                double contrast = Contrast((Brush)theme[foreground], (Brush)theme[background]);
                Assert.True(contrast >= 4.5, $"{foreground} on {background}: {contrast:F2}:1");
            });
        }

        private static double Contrast(Brush foreground, Brush background)
        {
            static double Linear(byte component)
            {
                double value = component / 255.0;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }

            static double Luminance(Color color) =>
                0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);

            double first = Luminance(BrushColor(foreground));
            double second = Luminance(BrushColor(background));
            return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
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
                var gray = ((SolidColorBrush)window.FindResource("DisabledForegroundBrush")).Color;
                foreach (var button in buttons)
                {
                    Assert.Equal(gray, ((SolidColorBrush)button.Foreground).Color);
                    Assert.All(Descendants(button).OfType<TextBlock>(),
                        label => Assert.Equal(gray, ((SolidColorBrush)label.Foreground).Color));
                }
                Assert.Equal(gray.R, gray.G);
                Assert.Equal(gray.G, gray.B);
                Assert.Equal(gray, ((SolidColorBrush)action.Foreground).Color);
                window.Resources.MergedDictionaries.Remove(dictionary);
                window.Resources.MergedDictionaries.Add(Theme(theme == "Dark" ? "Light" : "Dark"));
                WpfTestHost.Pump();
                foreach (var button in buttons)
                {
                    Assert.Equal(
                        ((SolidColorBrush)window.FindResource("DisabledForegroundBrush")).Color,
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

        private static XDocument ProductMarkup(string name)
        {
            using var stream = typeof(CaptureChromeTests).Assembly.GetManifestResourceStream("XMAT.Tests." + name);
            Assert.NotNull(stream);
            return XDocument.Load(stream);
        }

        private static T Parse<T>(XElement source)
        {
            var element = new XElement(source);
            var namespaceMap = new Dictionary<string, string>();
            element.Attribute(Xaml + "Key")?.Remove();
            var windowEvents = new[] { "Click", "DeviceTabClosed", "AnalysisTabAdded", "AnalysisTabClosed" };
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
            }
            return (T)XamlReader.Parse(element.ToString());
        }

        internal static void AddButtonStyles(ResourceDictionary resources)
        {
            var styles = ProductMarkup("App.xaml").Descendants(Presentation + "ResourceDictionary").First()
                .Elements(Presentation + "Style")
                .Where(style => new[] { "Button", "ToggleButton", "TextBlock", "{x:Type MenuItem}" }
                    .Contains((string)style.Attribute("TargetType")));
            foreach (var source in styles)
            {
                var style = Parse<Style>(source);
                var key = (string)source.Attribute(Xaml + "Key");
                object resourceKey = key switch
                {
                    "{x:Static ToolBar.ButtonStyleKey}" => ToolBar.ButtonStyleKey,
                    "{x:Static ToolBar.ToggleButtonStyleKey}" => ToolBar.ToggleButtonStyleKey,
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
