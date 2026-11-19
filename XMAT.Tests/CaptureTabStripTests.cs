// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using XMAT.Models;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class CaptureTabStripTests
    {
        private static readonly (DeviceType Type, string Name, bool ReadOnly)[] Captures =
        {
            (DeviceType.LocalPC, "Local PC", false),
            (DeviceType.GenericProxyDevice, "Windows Handheld or Remote Windows Device", false),
            (DeviceType.GenericProxyDevice, "Generic Device", false),
            (DeviceType.Unknown, "Fiddler - lobby-session.saz", true),
            (DeviceType.Unknown, "Fiddler - matchmaking.saz", true),
        };

        private readonly CaptureUiApplicationFixture _application;

        public CaptureTabStripTests(CaptureUiApplicationFixture application) => _application = application;

        [Fact]
        public void FiveTabsAtANarrowWidth_StayInOneRow_AndMoreListsTheRestByTheirFullNames()
        {
            _application.Run(window =>
            {
                var strip = Show(window, 640);
                var panel = strip.Panel;
                var more = strip.More;
                List<TabItem> inRow = strip.Tabs().Where(tab => !OverflowTabPanel.GetIsOverflowItem(tab)).ToList();
                List<TabItem> outOfRow = strip.Tabs().Where(OverflowTabPanel.GetIsOverflowItem).ToList();

                Assert.True(panel.HasOverflow);
                Assert.NotEmpty(outOfRow);
                Assert.Equal(outOfRow, panel.OverflowItems);
                Assert.Contains(strip.Container(0), inRow);
                // One row: every tab in it at the top, as tall as the row, at its own width, one after another.
                double rowHeight = strip.Container(0).ActualHeight;
                Assert.Equal(rowHeight, panel.ActualHeight, 1);
                double x = 0;
                foreach (TabItem tab in inRow)
                {
                    Rect slot = Slot(tab);
                    Assert.Equal(0, slot.Y);
                    Assert.Equal(rowHeight, slot.Height, 1);
                    Assert.Equal(tab.DesiredSize.Width, slot.Width, 1);
                    Assert.Equal(x, slot.X, 1);
                    x = slot.Right;
                }

                Assert.All(outOfRow, tab => Assert.Equal(0, Slot(tab).Width));
                // More follows the last tab in the row, as tall as the tabs, and fits in the strip.
                Assert.Equal(Visibility.Visible, more.Visibility);
                Assert.Equal(rowHeight, more.ActualHeight, 1);
                Assert.Equal(x, more.TranslatePoint(new Point(), panel).X, 1);
                Assert.True(more.TranslatePoint(new Point(more.ActualWidth, 0), panel).X <= panel.ActualWidth + 0.5);

                more.IsChecked = true;
                WpfTestHost.Pump();
                var listed = more.Menu.Items.OfType<MenuItem>().ToList();
                Assert.Equal(outOfRow.Select(tab => Label(strip, tab)), listed.Select(item => (string)item.Header));
                Assert.Contains("Fiddler - matchmaking.saz : WebProxy [Read Only]", listed.Select(item => (string)item.Header));

                listed.Last().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                more.Menu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
                WpfTestHost.Pump();
                TabItem chosen = outOfRow.Last();
                Assert.True(chosen.IsSelected);
                Assert.Same(strip.Model.CaptureDeviceContexts[strip.Index(chosen)], strip.Model.SelectedDeviceContext);
                Assert.False(OverflowTabPanel.GetIsOverflowItem(chosen));
                Assert.Equal(0, Slot(chosen).Y);
                Assert.Equal(rowHeight, panel.ActualHeight, 1);
                strip.Close();
            });
        }

        [Fact]
        public void TheSelectedTab_AlwaysStaysInTheRow()
        {
            _application.Run(window =>
            {
                var strip = Show(window, 640);
                foreach (ICaptureDeviceContext capture in strip.Model.CaptureDeviceContexts.ToList())
                {
                    strip.Model.SelectedDeviceContext = capture;
                    WpfTestHost.Pump();
                    TabItem tab = strip.Container(strip.Model.CaptureDeviceContexts.IndexOf(capture));
                    Assert.True(tab.IsSelected);
                    Assert.False(OverflowTabPanel.GetIsOverflowItem(tab), $"{capture} must stay in the row.");
                    Assert.Equal(tab.DesiredSize.Width, Slot(tab).Width, 1);
                    Assert.Equal(0, Slot(tab).Y);
                    Assert.True(strip.Panel.HasOverflow);
                    Assert.Equal(tab.ActualHeight, strip.Panel.ActualHeight, 1);
                }

                strip.Close();
            });
        }

        [Fact]
        public void DraggingATab_MovesItsCaptureAlongTheRowWithoutRecreatingItsView()
        {
            _application.Run(window =>
            {
                var strip = Show(window, 1700);
                var actions = strip.RecordChanges();
                var announced = strip.RecordAnnouncements();
                try
                {
                    ICaptureDeviceContext local = strip.Model.CaptureDeviceContexts[0];
                    ICaptureDeviceContext handheld = strip.Model.CaptureDeviceContexts[1];
                    TabItem tab = strip.Container(0);
                    ContentPresenter view = strip.View(local);
                    UIElement content = (UIElement)VisualTreeHelper.GetChild(view, 0);
                    Rect first = Slot(tab);
                    Rect second = Slot(strip.Container(1));
                    var grab = new Point(first.Width / 2, first.Height / 2);

                    // A press on the close button never starts a drag.
                    var close = Descendants(tab).OfType<Button>().Single(button => button.Name == "CloseButton");
                    strip.Pointer = close.TranslatePoint(new Point(4, 4), strip.Panel);
                    strip.Press(close);
                    strip.Pointer += new Vector(200, 0);
                    strip.MoveTo(close);
                    strip.Release(close);
                    Assert.False(strip.Captures.IsDraggingTab);
                    Assert.Empty(actions);

                    // Within the system drag distance the tab stays put.
                    strip.Pointer = grab;
                    strip.Press(tab);
                    strip.Pointer += new Vector(SystemParameters.MinimumHorizontalDragDistance - 1, 0);
                    strip.MoveTo(tab);
                    Assert.False(strip.Captures.IsDraggingTab);

                    // Past it, the tab follows the pointer, and it moves past the next tab once its right edge passes that
                    // tab's middle.
                    strip.Pointer = new Point((first.Width + second.Width) / 2 - 1, grab.Y);
                    strip.MoveTo(tab);
                    Assert.True(strip.Captures.IsDraggingTab);
                    Assert.Equal(strip.Pointer.X - grab.X, tab.TranslatePoint(new Point(), strip.Panel).X, 1);
                    Assert.Empty(actions);

                    strip.Pointer += new Vector(2, 0);
                    strip.MoveTo(tab);
                    Assert.Equal(new[] { handheld, local }, strip.Model.CaptureDeviceContexts.Take(2));
                    Assert.Equal(strip.Pointer.X - grab.X, tab.TranslatePoint(new Point(), strip.Panel).X, 1);
                    Assert.Same(tab, strip.Container(1));
                    Assert.True(tab.IsSelected);

                    strip.Release(tab);
                    Assert.False(strip.Captures.IsDraggingTab);
                    Assert.Equal(new[] { handheld, local }, strip.Model.CaptureDeviceContexts.Take(2));
                    Assert.Equal(Slot(tab).X, tab.TranslatePoint(new Point(), strip.Panel).X, 1);
                    Assert.All(actions, action => Assert.Equal(NotifyCollectionChangedAction.Move, action));
                    Assert.Single(actions);
                    Assert.Same(view, strip.View(local));
                    Assert.Same(content, VisualTreeHelper.GetChild(view, 0));
                    Assert.Equal(Visibility.Visible, view.Visibility);
                    Assert.Same(local, strip.Model.SelectedDeviceContext);
                    Assert.Single(announced);
                    Assert.Equal(
                        Localization.GetLocalizedString("A11Y_TAB_POSITION", local, 2, 5), strip.Announcement.Text);
                }
                finally
                {
                    strip.Close();
                }
            });
        }

        [Fact]
        public void Escape_PutsADraggedTabBack()
        {
            _application.Run(window =>
            {
                var strip = Show(window, 1700);
                var announced = strip.RecordAnnouncements();
                try
                {
                    var order = strip.Model.CaptureDeviceContexts.ToList();
                    TabItem tab = strip.Container(0);
                    ContentPresenter view = strip.View(order[0]);
                    strip.Pointer = new Point(10, 10);
                    strip.Press(tab);
                    // Far enough right to pass the next two tabs' middles, not the third's.
                    strip.Pointer = new Point(Slot(strip.Container(2)).Right - Slot(tab).Width + 10, 10);
                    strip.MoveTo(tab);
                    Assert.True(strip.Captures.IsDraggingTab);
                    Assert.Equal(order[0], strip.Model.CaptureDeviceContexts[2]);

                    tab.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(tab), 0, Key.Escape)
                    {
                        RoutedEvent = Keyboard.PreviewKeyDownEvent
                    });
                    WpfTestHost.Pump();

                    Assert.False(strip.Captures.IsDraggingTab);
                    Assert.Equal(order, strip.Model.CaptureDeviceContexts);
                    Assert.Equal(Slot(tab).X, tab.TranslatePoint(new Point(), strip.Panel).X, 1);
                    Assert.Same(view, strip.View(order[0]));
                    Assert.Empty(announced);

                    // The drag is over: moving on and letting go changes nothing.
                    strip.Pointer += new Vector(300, 0);
                    strip.MoveTo(tab);
                    strip.Release(tab);
                    Assert.Equal(order, strip.Model.CaptureDeviceContexts);
                }
                finally
                {
                    strip.Close();
                }
            });
        }

        [Fact]
        public void CtrlShiftPageUpAndPageDown_MoveTheSelectedTab_AndAnnounceItsPlaceOnce()
        {
            _application.Run(window =>
            {
                var strip = Show(window, 640);
                var actions = strip.RecordChanges();
                var announced = strip.RecordAnnouncements();
                try
                {
                    var order = strip.Model.CaptureDeviceContexts.ToList();
                    ICaptureDeviceContext generic = order[2];
                    strip.Model.SelectedDeviceContext = generic;
                    WpfTestHost.Pump();
                    TabItem tab = strip.Container(2);
                    ContentPresenter view = strip.View(generic);
                    Assert.True(tab.Focus());

                    strip.PressShortcut(Key.PageUp);
                    Assert.Equal(new[] { order[0], generic, order[1], order[3], order[4] }, strip.Model.CaptureDeviceContexts);
                    Assert.Equal(new[] { NotifyCollectionChangedAction.Move }, actions);
                    Assert.Same(tab, strip.Container(1));
                    Assert.Same(view, strip.View(generic));
                    Assert.True(tab.IsSelected);
                    Assert.True(tab.IsKeyboardFocused);
                    Assert.Single(announced);
                    Assert.Equal("Generic Device: WebProxy, tab 2 of 5", strip.Announcement.Text);

                    strip.PressShortcut(Key.PageDown);
                    strip.PressShortcut(Key.PageDown);
                    strip.PressShortcut(Key.PageDown);
                    Assert.Equal(new[] { order[0], order[1], order[3], order[4], generic }, strip.Model.CaptureDeviceContexts);
                    Assert.Equal("Generic Device: WebProxy, tab 5 of 5", strip.Announcement.Text);
                    Assert.Equal(4, announced.Count);
                    Assert.False(OverflowTabPanel.GetIsOverflowItem(tab));

                    // The last tab can't move further right; the first can't move further left.
                    Assert.False(AllCaptureDevicesTabControl.MoveTabRight.CanExecute(generic, strip.Captures));
                    Assert.False(AllCaptureDevicesTabControl.MoveTabLeft.CanExecute(order[0], strip.Captures));
                    Assert.All(actions, action => Assert.Equal(NotifyCollectionChangedAction.Move, action));
                    Assert.Same(view, strip.View(generic));
                }
                finally
                {
                    strip.Close();
                }
            });
        }

        // While its capture runs, a capture tab starts with the red dot, which fades unless animations are off, and screen
        // readers hear the tab's status as Capturing. A tab in More shows the dot in the menu too. The original look shows
        // no dot.
        [Fact]
        public void ARunningCapture_LeadsItsTabWithTheRedDot_InTheRowAndInMore()
        {
            _application.Run(window =>
            {
                // The test application has neither the new look's styles nor its spacing, so the window takes the dot's
                // style and the spacing it uses.
                var dotStyle = AccessibilityTests.AppResources("TabRecordingDotStyle");
                var spacing = new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/XboxMultiplayerAnalysisTool;component/Styles/Spacing.xaml")
                };
                var originalLook = new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/XboxMultiplayerAnalysisTool;component/Looks/OriginalLook.xaml")
                };
                bool wasNewLook = LookState.Current.IsNewLook;
                window.Resources.MergedDictionaries.Add(spacing);
                window.Resources.MergedDictionaries.Add(dotStyle);
                var strip = Show(window, 640);
                var selected = (CaptureTab)strip.Model.CaptureDeviceContexts[0];
                var hidden = (CaptureTab)strip.Model.CaptureDeviceContexts[^1];
                try
                {
                    TabItem tab = strip.Container(0);
                    Assert.True(OverflowTabPanel.GetIsOverflowItem(strip.Container(strip.Model.CaptureDeviceContexts.Count - 1)));
                    RecordingIndicator Dot(TabItem item) => Descendants(item).OfType<RecordingIndicator>().Single();
                    AutomationPeer Peer(ICaptureDeviceContext capture) =>
                        UIElementAutomationPeer.CreatePeerForElement(strip.Control).GetChildren()
                            .Single(peer => peer.GetName() == capture.ToString());
                    double idleWidth = tab.ActualWidth;
                    Assert.False(Dot(tab).IsVisible);
                    Assert.True(string.IsNullOrEmpty(Peer(selected).GetItemStatus()));

                    selected.Controller.IsRunning = true;
                    WpfTestHost.Pump();
                    RecordingIndicator dot = Dot(tab);
                    TextBlock name = Descendants(tab).OfType<TextBlock>()
                        .Single(block => block.Inlines.OfType<Run>().Any(run => run.Text == selected.DeviceName));
                    Assert.True(dot.IsVisible);
                    Assert.Equal(MotionSettings.Current.AnimationsEnabled, dot.IsPulsing);
                    Assert.Equal(8, dot.ActualWidth, 1);
                    Assert.Equal(8, name.TranslatePoint(new Point(), dot).X - dot.ActualWidth, 1);
                    Assert.Equal(idleWidth + 16, tab.ActualWidth, 1);
                    Assert.Equal(RecordingIndicator.CapturingText, Peer(selected).GetItemStatus());
                    Assert.True(string.IsNullOrEmpty(Peer(hidden).GetItemStatus()));

                    hidden.Controller.IsRunning = true;
                    WpfTestHost.Pump();
                    strip.More.IsChecked = true;
                    WpfTestHost.Pump();
                    List<MenuItem> listed = strip.More.Menu.Items.OfType<MenuItem>().ToList();
                    MenuItem running = listed.Single(item => (string)item.Header == Label(strip, strip.Container(strip.Model.CaptureDeviceContexts.Count - 1)));
                    Assert.True(running.Icon is RecordingIndicator { IsActive: true, Visibility: Visibility.Visible });
                    Assert.All(listed.Where(item => item != running), item => Assert.Null(item.Icon));
                    strip.More.Menu.SetCurrentValue(ContextMenu.IsOpenProperty, false);
                    WpfTestHost.Pump();

                    selected.Controller.IsRunning = false;
                    WpfTestHost.Pump();
                    Assert.False(Dot(tab).IsVisible);
                    Assert.Equal(idleWidth, tab.ActualWidth, 1);
                    Assert.True(string.IsNullOrEmpty(Peer(selected).GetItemStatus()));

                    window.Resources.MergedDictionaries.Add(originalLook);
                    LookState.Current.IsNewLook = false;
                    selected.Controller.IsRunning = true;
                    WpfTestHost.Pump();
                    Assert.False(Dot(tab).IsVisible);
                    Assert.True(string.IsNullOrEmpty(Peer(selected).GetItemStatus()));
                }
                finally
                {
                    LookState.Current.IsNewLook = wasNewLook;
                    window.Resources.MergedDictionaries.Remove(originalLook);
                    window.Resources.MergedDictionaries.Remove(dotStyle);
                    window.Resources.MergedDictionaries.Remove(spacing);
                    selected.Controller.IsRunning = false;
                    hidden.Controller.IsRunning = false;
                    strip.Close();
                }
            });
        }

        [Fact]
        public void TheOriginalLook_StillWrapsTheTabsIntoRowsAsATabPanelDoes_WithoutRecreatingTheViews()
        {
            _application.Run(window =>
            {
                var strip = Show(window, 800);
                OverflowTabPanel panel = strip.Panel;
                ContentPresenter view = strip.View(strip.Model.CaptureDeviceContexts[0]);
                bool wasNewLook = LookState.Current.IsNewLook;
                var originalLook = new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/XboxMultiplayerAnalysisTool;component/Looks/OriginalLook.xaml")
                };
                try
                {
                    window.Resources.MergedDictionaries.Add(originalLook);
                    LookState.Current.IsNewLook = false;
                    WpfTestHost.Pump();

                    // Switching looks keeps the strip and the capture views.
                    Assert.Same(panel, strip.Panel);
                    Assert.Same(view, strip.View(strip.Model.CaptureDeviceContexts[0]));
                    Assert.False(panel.HasOverflow);
                    Assert.Equal(Visibility.Hidden, strip.More.Visibility);

                    // A TabPanel with the same tabs, in a tab strip as wide, places every tab in the same spot.
                    var reference = new TabControl
                    {
                        Width = panel.ActualWidth,
                        ItemTemplate = strip.Control.ItemTemplate,
                        ItemsSource = strip.Model.CaptureDeviceContexts,
                        IsSynchronizedWithCurrentItem = false,
                        HorizontalAlignment = HorizontalAlignment.Left,
                    };
                    var host = (FrameworkElement)window.Content;
                    window.Content = null;
                    WpfTestHost.Show(window, new StackPanel { Children = { host, reference } });
                    Assert.Equal(reference.Width, panel.ActualWidth, 3);
                    foreach (int selected in new[] { 0, 4 })
                    {
                        strip.Model.SelectedDeviceContext = strip.Model.CaptureDeviceContexts[selected];
                        reference.SelectedIndex = selected;
                        WpfTestHost.Pump();
                        Assert.IsType<TabPanel>(VisualTreeHelper.GetParent(
                            (TabItem)reference.ItemContainerGenerator.ContainerFromIndex(0)));
                        var tops = strip.Tabs().Select(tab => Slot(tab).Y).Distinct().ToList();
                        Assert.True(tops.Count > 1, "Five tabs at 800 px wrap into rows in the original look.");
                        Assert.Equal(tops.Max(), Slot(strip.Container(selected)).Y);
                        Assert.All(strip.Tabs(), tab => Assert.False(OverflowTabPanel.GetIsOverflowItem(tab)));
                        for (int index = 0; index < Captures.Length; index++)
                        {
                            Rect expected = Slot((TabItem)reference.ItemContainerGenerator.ContainerFromIndex(index));
                            Rect actual = Slot(strip.Container(index));
                            Assert.Equal(expected.X, actual.X, 3);
                            Assert.Equal(expected.Y, actual.Y, 3);
                            Assert.Equal(expected.Width, actual.Width, 3);
                            Assert.Equal(expected.Height, actual.Height, 3);
                        }
                    }

                    // Tabs can't be dragged or moved from the keyboard in the original look.
                    strip.Pointer = new Point(Slot(strip.Container(4)).X + 10, Slot(strip.Container(4)).Y + 10);
                    strip.Press(strip.Container(4));
                    strip.Pointer += new Vector(-300, 0);
                    strip.MoveTo(strip.Container(4));
                    strip.Release(strip.Container(4));
                    Assert.False(strip.Captures.IsDraggingTab);
                    Assert.Equal(Captures.Select(capture => capture.Name), strip.Model.CaptureDeviceContexts.Select(capture => capture.DeviceName));
                    Assert.False(AllCaptureDevicesTabControl.MoveTabLeft.CanExecute(strip.Model.SelectedDeviceContext, strip.Captures));

                    window.Resources.MergedDictionaries.Remove(originalLook);
                    LookState.Current.IsNewLook = true;
                    WpfTestHost.Pump();
                    Assert.Same(panel, strip.Panel);
                    Assert.Single(strip.Tabs().Where(tab => !OverflowTabPanel.GetIsOverflowItem(tab)).Select(tab => Slot(tab).Y).Distinct());
                }
                finally
                {
                    LookState.Current.IsNewLook = wasNewLook;
                    window.Resources.MergedDictionaries.Remove(originalLook);
                    strip.Close();
                }
            });
        }

        private static Strip Show(Window window, double width)
        {
            window.Width = width + 100;
            window.Height = 500;
            var model = new CaptureAppModel();
            foreach (var (type, name, readOnly) in Captures)
            {
                model.CaptureDeviceContexts.Add(new CaptureTab(type, name, readOnly));
            }

            model.SelectedDeviceContext = model.CaptureDeviceContexts[0];
            var captures = new AllCaptureDevicesTabControl { DataContext = model };
            var tabs = (TabControlEx)captures.FindName("CaptureDevicesTabs");
            tabs.ContentTemplateSelector = null;
            var view = new FrameworkElementFactory(typeof(TextBlock));
            view.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ICaptureDeviceContext.DeviceName)));
            tabs.ContentTemplate = new DataTemplate { VisualTree = view };
            var host = new Border { Width = width, HorizontalAlignment = HorizontalAlignment.Left, Child = captures };
            WpfTestHost.Show(window, host);
            return new Strip(captures, tabs, model);
        }

        private static Rect Slot(UIElement tab) => LayoutInformation.GetLayoutSlot((FrameworkElement)tab);

        // The name that More lists for TAB: the text its header shows.
        private static string Label(Strip strip, TabItem tab)
        {
            var capture = (CaptureTab)strip.Model.CaptureDeviceContexts[strip.Index(tab)];
            return $"{capture.DeviceName} : {capture.CaptureTypeString}" +
                (capture.IsReadOnly ? Localization.GetLocalizedString("CAPTURE_DEVICES_TAB_READONLY") : string.Empty);
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                yield return child;
                foreach (DependencyObject descendant in Descendants(child))
                {
                    yield return descendant;
                }
            }
        }

        // A capture tab strip on screen, with a pointer that tests move: mouse events raised in code have no position,
        // so the strip reads the pointer from here.
        private sealed class Strip
        {
            private readonly List<Action> _cleanup = new();

            internal Strip(AllCaptureDevicesTabControl captures, TabControlEx control, CaptureAppModel model)
            {
                Captures = captures;
                Control = control;
                Model = model;
                captures.PressedPosition = (e, element) => Panel.TranslatePoint(Pointer, (UIElement)element);
            }

            internal AllCaptureDevicesTabControl Captures { get; }

            internal TabControlEx Control { get; }

            internal CaptureAppModel Model { get; }

            // Where the pointer is, relative to the tab row.
            internal Point Pointer { get; set; }

            internal OverflowTabPanel Panel => (OverflowTabPanel)Control.Template.FindName("headerPanel", Control);

            internal TabOverflowButton More => (TabOverflowButton)Control.Template.FindName("MoreButton", Control);

            internal TextBlock Announcement => (TextBlock)Captures.FindName("TabMoveAnnouncement");

            internal IEnumerable<TabItem> Tabs() => Panel.Children.OfType<TabItem>();

            internal TabItem Container(int index) => (TabItem)Control.ItemContainerGenerator.ContainerFromIndex(index);

            internal int Index(TabItem tab) => Control.ItemContainerGenerator.IndexFromContainer(tab);

            // The presenter that shows CAPTURE's view, kept while its tab is open.
            internal ContentPresenter View(ICaptureDeviceContext capture)
            {
                var holder = (Grid)Control.Template.FindName("PART_ItemsHolder", Control);
                return holder.Children.OfType<ContentPresenter>().Single(presenter => presenter.Content == capture);
            }

            internal List<NotifyCollectionChangedAction> RecordChanges()
            {
                var actions = new List<NotifyCollectionChangedAction>();
                NotifyCollectionChangedEventHandler record = (sender, e) => actions.Add(e.Action);
                Model.CaptureDeviceContexts.CollectionChanged += record;
                _cleanup.Add(() => Model.CaptureDeviceContexts.CollectionChanged -= record);
                return actions;
            }

            internal List<UIElement> RecordAnnouncements()
            {
                var announced = new List<UIElement>();
                Action<UIElement> record = announced.Add;
                LiveRegion.Announced += record;
                _cleanup.Add(() => LiveRegion.Announced -= record);
                return announced;
            }

            // The mouse events that a press, a move and a release raise on TARGET.
            internal void Press(UIElement target)
            {
                Raise(target, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left), Mouse.PreviewMouseDownEvent, Mouse.MouseDownEvent);
            }

            internal void MoveTo(UIElement target)
            {
                Raise(target, new MouseEventArgs(Mouse.PrimaryDevice, 0), Mouse.PreviewMouseMoveEvent, Mouse.MouseMoveEvent);
            }

            internal void Release(UIElement target)
            {
                Raise(target, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left), Mouse.PreviewMouseUpEvent, Mouse.MouseUpEvent);
            }

            // Ctrl and Shift can't be held in a test, so this runs the strip's binding for Ctrl+Shift+KEY.
            internal void PressShortcut(Key key)
            {
                KeyBinding binding = Assert.Single(Captures.InputBindings.OfType<KeyBinding>(),
                    item => item.Key == key && item.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift));
                var command = Assert.IsAssignableFrom<RoutedCommand>(binding.Command);
                Assert.Same(Model.SelectedDeviceContext, binding.CommandParameter);
                Assert.True(command.CanExecute(binding.CommandParameter, Captures));
                command.Execute(binding.CommandParameter, Captures);
                WpfTestHost.Pump();
            }

            internal void Close()
            {
                foreach (Action cleanup in _cleanup)
                {
                    cleanup();
                }

                Model.SelectedDeviceContext = null;
                Captures.DataContext = null;
            }

            private static void Raise(UIElement target, MouseEventArgs preview, RoutedEvent previewEvent, RoutedEvent bubbling)
            {
                preview.RoutedEvent = previewEvent;
                target.RaiseEvent(preview);
                MouseEventArgs main = preview is MouseButtonEventArgs
                    ? new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    : new MouseEventArgs(Mouse.PrimaryDevice, 0);
                main.RoutedEvent = bubbling;
                main.Handled = preview.Handled;
                target.RaiseEvent(main);
                WpfTestHost.Pump();
            }
        }

        // A capture tab's item, named as the application's are.
        private sealed class CaptureTab : ICaptureDeviceContext
        {
            internal CaptureTab(DeviceType type, string name, bool readOnly)
            {
                DeviceType = type;
                DeviceName = name;
                IsReadOnly = readOnly;
            }

            public DeviceType DeviceType { get; }

            public CaptureType CaptureType => CaptureType.WebProxy;

            public string CaptureTypeString => Localization.GetLocalizedString("WEBCAP_CAPTURE_TYPE_PROXY");

            public string DeviceName { get; }

            public bool IsSelected { get; set; }

            public bool IsReadOnly { get; set; }

            public System.Drawing.Color DeviceColor { get; set; }

            // A capture that starts and stops when a test says.
            public FakeCaptureController Controller { get; } = new();

            public IDeviceCaptureController CaptureController => Controller;

            public override string ToString() =>
                $"{DeviceName}: {CaptureTypeString}" +
                (IsReadOnly ? Localization.GetLocalizedString("CAPTURE_DEVICES_TAB_READONLY") : string.Empty);
        }

        private sealed class FakeCaptureController : IDeviceCaptureController
        {
            private bool _isRunning;

            public event PropertyChangedEventHandler PropertyChanged;

            public bool IsRunning
            {
                get => _isRunning;
                set
                {
                    _isRunning = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRunning)));
                }
            }

            public ICaptureMethod CaptureMethod => null;

            public object Parameters => null;

            public void Initialize()
            {
            }

            public void LoadCaptures(IEnumerable<IDataset> tables)
            {
            }

            public void ClearAllCaptures()
            {
            }

            public Task<bool> CanCloseAsync() => Task.FromResult(true);

            public Task CloseAsync() => Task.CompletedTask;
        }
    }
}
