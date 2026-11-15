// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using XMAT.Models;
using XMAT.SharedInterfaces;

namespace XMAT
{
    // In the new look, capture tabs can be put in another order: dragged along their row, or moved one place with
    // Ctrl+Shift+Page Up and Ctrl+Shift+Page Down, as in web browsers. A tab's item moves within CaptureDeviceContexts,
    // so no tab or capture view is closed or created again.
    public partial class AllCaptureDevicesTabControl
    {
        public static readonly RoutedCommand MoveTabLeft = new(nameof(MoveTabLeft), typeof(AllCaptureDevicesTabControl));

        public static readonly RoutedCommand MoveTabRight = new(nameof(MoveTabRight), typeof(AllCaptureDevicesTabControl));

        // The tab that the left button went down on, where it went down in the row and in the tab, and once the pointer
        // moves past the system drag distance, the dragged tab and its index when the drag began.
        private TabItem _pressedTab;
        private Point _pressPoint;
        private Point _pressOffset;
        private TabItem _draggedTab;
        private int _dragStartIndex;
        private UIElement _escapeScope;

        // Where the pointer is relative to an element while the left button is down, or null once it is up. Mouse
        // events raised in code, as tests raise them, carry neither, so tests replace this.
        internal Func<MouseEventArgs, IInputElement, Point?> PressedPosition { get; set; } =
            (e, element) => e.LeftButton == MouseButtonState.Pressed ? e.GetPosition(element) : null;

        internal bool IsDraggingTab => _draggedTab != null;

        private ObservableCollection<ICaptureDeviceContext> Tabs =>
            CaptureDevicesTabs.ItemsSource as ObservableCollection<ICaptureDeviceContext>;

        private OverflowTabPanel HeaderPanel =>
            CaptureDevicesTabs.Template?.FindName("headerPanel", CaptureDevicesTabs) as OverflowTabPanel;

        private void AddTabReordering()
        {
            CommandBindings.Add(new CommandBinding(
                MoveTabLeft, (sender, e) => MoveTab(e.Parameter, -1), (sender, e) => e.CanExecute = CanMoveTab(e.Parameter, -1)));
            CommandBindings.Add(new CommandBinding(
                MoveTabRight, (sender, e) => MoveTab(e.Parameter, 1), (sender, e) => e.CanExecute = CanMoveTab(e.Parameter, 1)));
            foreach ((RoutedCommand command, Key key) in new[] { (MoveTabLeft, Key.PageUp), (MoveTabRight, Key.PageDown) })
            {
                var shortcut = new KeyBinding(command, key, ModifierKeys.Control | ModifierKeys.Shift);
                BindingOperations.SetBinding(shortcut, InputBinding.CommandParameterProperty,
                    new Binding(nameof(Selector.SelectedItem)) { Source = CaptureDevicesTabs });
                InputBindings.Add(shortcut);
            }

            PreviewKeyDown += MoveShortcut_PreviewKeyDown;
            CaptureDevicesTabs.PreviewMouseLeftButtonDown += Tabs_PreviewMouseLeftButtonDown;
            CaptureDevicesTabs.PreviewMouseMove += Tabs_PreviewMouseMove;
            CaptureDevicesTabs.PreviewMouseLeftButtonUp += Tabs_PreviewMouseLeftButtonUp;
            CaptureDevicesTabs.LostMouseCapture += Tabs_LostMouseCapture;
        }

        private bool CanMoveTab(object item, int step)
        {
            int index = Tabs?.IndexOf(item as ICaptureDeviceContext) ?? -1;
            return LookState.Current.IsNewLook && !IsDraggingTab && index >= 0 && index + step >= 0 && index + step < Tabs.Count;
        }

        private void MoveTab(object item, int step)
        {
            if (CanMoveTab(item, step))
            {
                var tab = (ICaptureDeviceContext)item;
                int index = Tabs.IndexOf(tab);
                Tabs.Move(index, index + step);
                TabMoved(tab);
            }
        }

        // A table takes Page Up and Page Down for itself, so the shortcuts that move tabs run before the focused control
        // sees them. In the new look they never page a table, even when the tab can't move further.
        private void MoveShortcut_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key is not (Key.PageUp or Key.PageDown) || !LookState.Current.IsNewLook)
            {
                return;
            }

            KeyBinding shortcut = InputBindings.OfType<KeyBinding>().FirstOrDefault(binding =>
                (binding.Command == MoveTabLeft || binding.Command == MoveTabRight) && binding.Gesture.Matches(this, e));
            if (shortcut?.Command is RoutedCommand command)
            {
                if (command.CanExecute(shortcut.CommandParameter, this))
                {
                    command.Execute(shortcut.CommandParameter, this);
                }

                e.Handled = true;
            }
        }

        // The tab is in its new place. Screen readers hear where, once. XMAT reopens the captures it remembers in tab
        // order, so moving one of them saves the order, as adding or closing a tab does; loaded captures aren't kept.
        private void TabMoved(ICaptureDeviceContext tab)
        {
            TabMoveAnnouncement.Text = string.Empty;
            TabMoveAnnouncement.Text = Localization.GetLocalizedString(
                "A11Y_TAB_POSITION", tab, Tabs.IndexOf(tab) + 1, Tabs.Count);
            if (!tab.IsReadOnly && tab.DeviceType != DeviceType.Unknown && ReferenceEquals(DataContext, CaptureAppModel.AppModel))
            {
                CaptureAppSettings.Serialize(CaptureAppModel.AppModel);
            }
        }

        private void Tabs_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _pressedTab = null;
            if (IsDraggingTab || !LookState.Current.IsNewLook || Tabs == null || HeaderPanel is not { } row)
            {
                return;
            }

            TabItem tab = TabAt(e.OriginalSource as DependencyObject, row);
            if (tab != null && PressedPosition(e, row) is Point point && PressedPosition(e, tab) is Point offset)
            {
                _pressedTab = tab;
                _pressPoint = point;
                _pressOffset = offset;
            }
        }

        private void Tabs_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_pressedTab == null && _draggedTab == null)
            {
                return;
            }

            OverflowTabPanel row = HeaderPanel;
            if (row == null || PressedPosition(e, row) is not Point point)
            {
                // The button went up where this control didn't see it.
                if (IsDraggingTab)
                {
                    EndDrag(keep: true);
                }

                _pressedTab = null;
                return;
            }

            if (!IsDraggingTab)
            {
                Vector moved = point - _pressPoint;
                if ((Math.Abs(moved.X) <= SystemParameters.MinimumHorizontalDragDistance &&
                     Math.Abs(moved.Y) <= SystemParameters.MinimumVerticalDragDistance) ||
                    !StartDrag(row))
                {
                    return;
                }
            }

            DragTo(point.X, row);
            e.Handled = true;
        }

        private void Tabs_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _pressedTab = null;
            if (IsDraggingTab)
            {
                EndDrag(keep: true);
                e.Handled = true;
            }
        }

        // Losing the mouse during a drag, as when another window takes it, puts the tab back.
        private void Tabs_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (IsDraggingTab && e.OriginalSource == HeaderPanel)
            {
                EndDrag(keep: false);
            }
        }

        private void DragScope_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (IsDraggingTab && e.Key == Key.Escape)
            {
                EndDrag(keep: false);
                e.Handled = true;
            }
        }

        // The tab in ROW that ELEMENT is part of, or null when ELEMENT is in a button, such as the tab's close button.
        private static TabItem TabAt(DependencyObject element, Panel row)
        {
            while (element != null && element != row)
            {
                if (element is ButtonBase)
                {
                    return null;
                }

                DependencyObject parent = element is Visual or Visual3D
                    ? VisualTreeHelper.GetParent(element)
                    : LogicalTreeHelper.GetParent(element);
                if (parent == row)
                {
                    return element as TabItem;
                }

                element = parent;
            }

            return null;
        }

        private bool StartDrag(OverflowTabPanel row)
        {
            TabItem tab = _pressedTab;
            _pressedTab = null;
            int index = Tabs.IndexOf(ItemOf(tab));
            if (index < 0)
            {
                return false;
            }

            // A press selects its tab, and the selected tab always keeps its place in the row.
            tab.SetCurrentValue(Selector.IsSelectedProperty, true);
            row.UpdateLayout();
            _draggedTab = tab;
            _dragStartIndex = index;
            tab.RenderTransform = new TranslateTransform();
            Panel.SetZIndex(tab, 1);
            row.CaptureMouse();
            _escapeScope = Window.GetWindow(this) ?? (UIElement)this;
            _escapeScope.PreviewKeyDown += DragScope_PreviewKeyDown;
            return true;
        }

        // The dragged tab follows the pointer along the row, and moves past each neighbour whose middle its leading edge
        // passes.
        private void DragTo(double pointerX, OverflowTabPanel row)
        {
            for (int moves = 0; Tabs != null && moves <= Tabs.Count; moves++)
            {
                List<TabItem> inRow = row.Children.OfType<TabItem>()
                    .Where(tab => tab.Visibility == Visibility.Visible && !OverflowTabPanel.GetIsOverflowItem(tab))
                    .ToList();
                int at = inRow.IndexOf(_draggedTab);
                if (at < 0)
                {
                    return;
                }

                Rect slot = LayoutInformation.GetLayoutSlot(_draggedTab);
                double rowEnd = inRow.Max(tab => LayoutInformation.GetLayoutSlot(tab).Right);
                double left = Math.Clamp(pointerX - _pressOffset.X, 0, Math.Max(0, rowEnd - slot.Width));
                ((TranslateTransform)_draggedTab.RenderTransform).X = left - slot.X;
                TabItem passed =
                    at + 1 < inRow.Count && left + slot.Width > Middle(inRow[at + 1]) ? inRow[at + 1] :
                    at > 0 && left < Middle(inRow[at - 1]) ? inRow[at - 1] :
                    null;
                if (passed == null)
                {
                    return;
                }

                Tabs.Move(Tabs.IndexOf(ItemOf(_draggedTab)), Tabs.IndexOf(ItemOf(passed)));
                row.UpdateLayout();
            }
        }

        private static double Middle(UIElement tab)
        {
            Rect slot = LayoutInformation.GetLayoutSlot((FrameworkElement)tab);
            return slot.X + (slot.Width / 2);
        }

        // Ends the drag, keeping the new order or putting the tab back where the drag began.
        private void EndDrag(bool keep)
        {
            TabItem tab = _draggedTab;
            _draggedTab = null;
            _pressedTab = null;
            _escapeScope.PreviewKeyDown -= DragScope_PreviewKeyDown;
            _escapeScope = null;
            tab.ClearValue(RenderTransformProperty);
            tab.ClearValue(Panel.ZIndexProperty);
            if (HeaderPanel is { IsMouseCaptured: true } row)
            {
                row.ReleaseMouseCapture();
            }

            ICaptureDeviceContext item = ItemOf(tab);
            int index = Tabs?.IndexOf(item) ?? -1;
            if (index < 0 || index == _dragStartIndex)
            {
                return;
            }

            if (keep)
            {
                TabMoved(item);
            }
            else if (_dragStartIndex < Tabs.Count)
            {
                Tabs.Move(index, _dragStartIndex);
            }
        }

        private ICaptureDeviceContext ItemOf(TabItem tab) =>
            CaptureDevicesTabs.ItemContainerGenerator.ItemFromContainer(tab) as ICaptureDeviceContext;
    }
}
