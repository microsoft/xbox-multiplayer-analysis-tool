// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using XMAT.Scripting;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public class ScriptEditorLayoutTests
    {
        private readonly CaptureUiApplicationFixture _application;

        public ScriptEditorLayoutTests(CaptureUiApplicationFixture application)
        {
            _application = application;
            Localization.LoadLanguage("en-us");
        }

        // The actions are a footer pinned to the bottom of the tab: at any width they wrap inside it, and when the tab
        // is too short only the content above them scrolls.
        [Theory]
        [InlineData(260, 460, true)]
        [InlineData(380, 460, true)]
        [InlineData(640, 460, true)]
        [InlineData(640, 300, true)]
        [InlineData(260, 300, false)]
        [InlineData(640, 460, false)]
        public void Footer_StaysInsideTheTabWhileOnlyTheContentAboveItScrolls(double width, double height, bool newLook)
        {
            _application.Run(window =>
            {
                bool wasNewLook = LookState.Current.IsNewLook;
                LookState.Current.IsNewLook = newLook;
                try
                {
                    var view = Show(window, width, height);
                    ButtonBase[] actions = Actions(view);
                    Assert.Equal(4, actions.Length);
                    foreach (var action in actions)
                    {
                        Rect bounds = Bounds(action, view);
                        Assert.True(bounds.Left >= -0.5 && bounds.Right <= width + 0.5 && bounds.Top >= -0.5 &&
                            bounds.Bottom <= height + 0.5,
                            $"'{action.Content}' spans {bounds} in a {width} x {height} tab.");
                        Assert.False(view.ScriptScroller.IsAncestorOf(action), "The actions must not scroll.");
                    }
                    Assert.True(Bounds(view.ScriptScroller, view).Bottom <= actions.Min(action => Bounds(action, view).Top) + 0.5);
                    if (height < 400)
                    {
                        Assert.True(view.ScriptScroller.ScrollableHeight > 0, "A short tab scrolls the content above the footer.");
                    }

                    Rect[] before = actions.Select(action => Bounds(action, view)).ToArray();
                    view.ScriptScroller.ScrollToEnd();
                    WpfTestHost.Pump();
                    Assert.Equal(before, actions.Select(action => Bounds(action, view)));

                    // The panes sit side by side or one below another, never over each other or past the tab's edge.
                    Rect editor = Bounds(view.ScriptEditor, view);
                    Rect splitter = Bounds(view.ScriptTypesSplitter, view);
                    Rect types = Bounds(view.TypeReferencePane, view);
                    if (view.ScriptLayout.IsStacked)
                    {
                        Assert.True(editor.Bottom <= splitter.Top + 0.5 && splitter.Bottom <= types.Top + 0.5);
                    }
                    else
                    {
                        Assert.True(editor.Right <= splitter.Left + 0.5 && splitter.Right <= types.Left + 0.5);
                    }
                    Assert.True(editor.Left >= -0.5 && types.Right <= width + 0.5);
                    Assert.True(Bounds(view.TypeInfoTree, view).Bottom <= Bounds(view.TypeDescriptionSplitter, view).Top + 0.5);
                    Assert.True(Bounds(view.TypeDescriptionSplitter, view).Bottom <= Bounds(view.TypeDescription, view).Top + 0.5);
                    Assert.True(Bounds(view.TypeDescription, view).Bottom <= types.Bottom + 0.5);
                }
                finally
                {
                    LookState.Current.IsNewLook = wasNewLook;
                }
            });
        }

        [Fact]
        public void Splitters_ResizeThePanesFromTheMouseAndTheKeyboard()
        {
            _application.Run(window =>
            {
                var view = Show(window, 640, 640);
                var host = (FrameworkElement)view.Parent;
                GridSplitter types = view.ScriptTypesSplitter;
                Assert.False(view.ScriptLayout.IsStacked);
                Assert.Equal(GridResizeDirection.Columns, types.ResizeDirection);
                Assert.Same(Cursors.SizeWE, types.Cursor);
                double width = view.TypeReferencePane.ActualWidth;
                double editor = view.ScriptEditor.ActualWidth;
                Assert.InRange(width, 240, 280);
                Assert.True(editor > width, "The editor is the largest pane.");

                Press(types, Key.Left);
                Assert.Equal(width + types.KeyboardIncrement, view.TypeReferencePane.ActualWidth, 1);
                Assert.Equal(editor - types.KeyboardIncrement, view.ScriptEditor.ActualWidth, 1);
                Drag(types, -40, 0);
                Assert.Equal(width + types.KeyboardIncrement + 40, view.TypeReferencePane.ActualWidth, 1);
                Drag(types, -1000, 0);
                Assert.True(view.ScriptEditor.ActualWidth >= view.TypeReferencePane.ActualWidth - 0.5,
                    "The type reference never grows past the editor.");
                Drag(types, 1000, 0);
                Assert.Equal(view.TypesColumn.MinWidth, view.TypeReferencePane.ActualWidth, 1);

                GridSplitter description = view.TypeDescriptionSplitter;
                double tree = view.TypeInfoTree.ActualHeight;
                double text = view.TypeDescription.ActualHeight;
                Press(description, Key.Up);
                Assert.Equal(text + description.KeyboardIncrement, view.TypeDescription.ActualHeight, 1);
                Assert.Equal(tree - description.KeyboardIncrement, view.TypeInfoTree.ActualHeight, 1);
                Drag(description, 0, 1000);
                Assert.Equal(view.TypeDescriptionRow.MinHeight, view.TypeDescription.ActualHeight, 1);
                Drag(description, 0, -1000);
                Assert.Equal(view.TypeTreeRow.MinHeight, view.TypeInfoTree.ActualHeight, 1);

                // In a narrow tab the type reference moves below the editor, and its divider resizes rows.
                host.Width = 380;
                WpfTestHost.Pump();
                Assert.True(view.ScriptLayout.IsStacked);
                Assert.Equal(GridResizeDirection.Rows, types.ResizeDirection);
                Assert.Same(Cursors.SizeNS, types.Cursor);
                Assert.True(Bounds(types, view).Top >= Bounds(view.ScriptEditor, view).Bottom - 0.5);
                double below = view.TypeReferencePane.ActualHeight;
                Assert.True(view.ScriptEditor.ActualHeight > below,
                    $"The editor ({view.ScriptEditor.ActualHeight:0} px) must stay taller than the type reference ({below:0} px).");
                Press(types, Key.Up);
                Assert.Equal(below + types.KeyboardIncrement, view.TypeReferencePane.ActualHeight, 1);
                Drag(types, 0, -1000);
                Assert.True(view.ScriptEditor.ActualHeight >= view.TypeReferencePane.ActualHeight - 0.5,
                    "The type reference never grows past the editor.");
                Assert.Equal(0, view.TypesColumn.ActualWidth);

                // Back beside the editor, the type reference has the width it was given there.
                host.Width = 640;
                WpfTestHost.Pump();
                Assert.False(view.ScriptLayout.IsStacked);
                Assert.Equal(GridResizeDirection.Columns, types.ResizeDirection);
                Assert.Equal(view.TypesColumn.MinWidth, view.TypeReferencePane.ActualWidth, 1);
                Assert.Equal(0, view.TypesRow.ActualHeight);
            });
        }

        // Each look keeps its own pane sizes and divider thickness when the look changes, also after the type reference
        // has moved below the editor and back.
        [Fact]
        public void Panes_TakeEachLooksSizesWhenTheLookChanges()
        {
            _application.Run(window =>
            {
                bool wasNewLook = LookState.Current.IsNewLook;
                LookState.Current.IsNewLook = true;
                try
                {
                    var view = Show(window, 380, 640);
                    Assert.True(view.ScriptLayout.IsStacked);
                    ((FrameworkElement)view.Parent).Width = 640;
                    WpfTestHost.Pump();
                    Assert.Equal(260, view.TypeReferencePane.ActualWidth, 1);
                    Assert.Equal(12, view.ScriptTypesSplitter.ActualWidth, 1);

                    LookState.Current.IsNewLook = false;
                    WpfTestHost.Pump();
                    Assert.Equal(4, view.ScriptTypesSplitter.ActualWidth, 1);
                    Assert.Equal(4, view.TypeDescriptionSplitter.ActualHeight, 1);
                    Assert.Equal((640 - 4) / 4.0, view.TypeReferencePane.ActualWidth, 1);

                    LookState.Current.IsNewLook = true;
                    WpfTestHost.Pump();
                    Assert.Equal(260, view.TypeReferencePane.ActualWidth, 1);
                    Assert.Equal(12, view.ScriptTypesSplitter.ActualWidth, 1);
                }
                finally
                {
                    LookState.Current.IsNewLook = wasNewLook;
                }
            });
        }

        // Switching to the original look while the type reference is below the editor gives its column the original
        // proportions, not a width kept from the new look.
        [Fact]
        public void StackedPanes_TakeTheOriginalProportionsWhenTheLookChanges()
        {
            _application.Run(window =>
            {
                bool wasNewLook = LookState.Current.IsNewLook;
                LookState.Current.IsNewLook = true;
                try
                {
                    var view = Show(window, 380, 640);
                    Assert.True(view.ScriptLayout.IsStacked);

                    LookState.Current.IsNewLook = false;
                    WpfTestHost.Pump();
                    Assert.False(view.ScriptLayout.IsStacked);
                    Assert.Equal(new GridLength(1, GridUnitType.Star), view.TypesColumn.Width);
                    ((FrameworkElement)view.Parent).Width = 1000;
                    WpfTestHost.Pump();
                    Assert.Equal((1000 - 4) / 4.0, view.TypeReferencePane.ActualWidth, 1);

                    LookState.Current.IsNewLook = true;
                    WpfTestHost.Pump();
                    Assert.Equal(260, view.TypeReferencePane.ActualWidth, 1);
                }
                finally
                {
                    LookState.Current.IsNewLook = wasNewLook;
                }
            });
        }

        // The dividers are the capture's pane dividers: the app's splitter style, named for screen readers, and in the
        // Tab order between the panes they resize, before the actions.
        [Fact]
        public void Splitters_LookAndReadLikeThePaneDividersInTheTabOrder()
        {
            _application.Run(window =>
            {
                window.Resources.MergedDictionaries.Add(AccessibilityTests.AppResources("GridSplitter"));
                var view = Show(window, 640, 640);
                GridSplitter types = view.ScriptTypesSplitter;
                GridSplitter description = view.TypeDescriptionSplitter;
                Assert.Equal("Resize the script and the script types", AutomationProperties.GetName(types));
                Assert.Equal("Resize the script types and the type description", AutomationProperties.GetName(description));
                Assert.Equal("Type description", AutomationProperties.GetName(view.TypeDescription));
                Assert.Equal(12, types.ActualWidth, 1);
                Assert.Equal(12, description.ActualHeight, 1);

                var line = Assert.IsType<Border>(types.Template.FindName("ResizeLine", types));
                Assert.Equal(0, line.Opacity);
                Assert.True(view.ScriptEditor.Focus());
                var order = new List<IInputElement> { Keyboard.FocusedElement };
                ButtonBase save = Actions(view).Last();
                while (!ReferenceEquals(Keyboard.FocusedElement, save) && order.Count < 20)
                {
                    ((UIElement)Keyboard.FocusedElement).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                    WpfTestHost.Pump();
                    order.Add(Keyboard.FocusedElement);
                    if (ReferenceEquals(Keyboard.FocusedElement, types))
                    {
                        Assert.Equal(1, line.Opacity);
                    }
                }

                int Stop(UIElement element) => order.FindIndex(focused =>
                    ReferenceEquals(focused, element) || (focused is DependencyObject inner && element.IsAncestorOf(inner)));
                var stops = new UIElement[]
                {
                    view.ScriptEditor, types, view.TypeInfoTree, description, view.TypeDescription, view.ScriptEnabled, save
                }.Select(Stop).ToArray();
                Assert.DoesNotContain(-1, stops);
                Assert.Equal(stops.OrderBy(stop => stop), stops);
            });
        }

        private static ScriptEditorView Show(Window window, double width, double height)
        {
            window.Width = 1200;
            window.Height = 900;
            var view = new ScriptEditorView
            {
                ScriptCollection = new ScriptCollection(typeof(WebServiceCaptureScriptableEventType)),
                ScriptTypeCollection = new ScriptTypeCollection(new[]
                {
                    typeof(WebServiceCaptureScriptParams), typeof(ClientRequest), typeof(ServerResponse)
                })
            };
            var host = new Border
            {
                Width = width, Height = height, HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top, Child = view
            };
            WpfTestHost.Show(window, host);
            WpfTestHost.Pump();
            return view;
        }

        private static ButtonBase[] Actions(ScriptEditorView view) =>
            new ButtonBase[] { view.ScriptEnabled }
                .Concat(Descendants(view.ScriptActions).OfType<Button>())
                .ToArray();

        private static void Press(UIElement target, Key key)
        {
            Assert.True(target.Focus());
            target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), 0, key)
            {
                RoutedEvent = Keyboard.KeyDownEvent
            });
            WpfTestHost.Pump();
        }

        // The events a GridSplitter's thumb raises for a mouse drag.
        private static void Drag(GridSplitter splitter, double horizontal, double vertical)
        {
            splitter.RaiseEvent(new DragStartedEventArgs(0, 0));
            splitter.RaiseEvent(new DragDeltaEventArgs(horizontal, vertical));
            splitter.RaiseEvent(new DragCompletedEventArgs(horizontal, vertical, false));
            WpfTestHost.Pump();
        }

        private static Rect Bounds(FrameworkElement element, Visual ancestor) =>
            element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

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
