// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace XMAT
{
    // The info icon at the right end of a column header that opens the column's summary (see ColumnSummary). The new
    // look's header template has one; it shows in the tables with column summaries, for the columns that have one. A
    // click, Enter, Space or Alt+Down opens the summary, and a click while it is open closes it; the icon never sorts
    // its column. A table's icons are one tab stop: Left, Right, Home and End move between them, and Tab moves on to the
    // rows. Its name and tooltip are "<column> summary", and screen readers hear whether the summary is open.
    public sealed class ColumnSummaryButton : ButtonBase
    {
        private static readonly DependencyPropertyKey IsSummaryOpenPropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(IsSummaryOpen), typeof(bool), typeof(ColumnSummaryButton), new PropertyMetadata(false, OnIsSummaryOpenChanged));

        public static readonly DependencyProperty IsSummaryOpenProperty = IsSummaryOpenPropertyKey.DependencyProperty;

        private static readonly DependencyPropertyKey IsFocusVisiblePropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(IsFocusVisible), typeof(bool), typeof(ColumnSummaryButton), new PropertyMetadata(false));

        public static readonly DependencyProperty IsFocusVisibleProperty = IsFocusVisiblePropertyKey.DependencyProperty;

        private ColumnSummaryPopup _summaries;

        static ColumnSummaryButton()
        {
            // Hidden and out of the tab order until its table gives it a summary and makes it the icons' tab stop.
            VisibilityProperty.OverrideMetadata(typeof(ColumnSummaryButton), new FrameworkPropertyMetadata(Visibility.Collapsed));
            IsTabStopProperty.OverrideMetadata(typeof(ColumnSummaryButton), new FrameworkPropertyMetadata(false));
        }

        public ColumnSummaryButton()
        {
            Loaded += (_, _) => Refresh();
            Unloaded += (_, _) => Refresh();
            IsVisibleChanged += (_, _) => _summaries?.UpdateTabStops();
        }

        // The summary of this icon's column is open.
        public bool IsSummaryOpen
        {
            get => (bool)GetValue(IsSummaryOpenProperty);
            internal set => SetValue(IsSummaryOpenPropertyKey, value);
        }

        // The icon has keyboard focus that came from the keyboard rather than a click, and shows it.
        public bool IsFocusVisible
        {
            get => (bool)GetValue(IsFocusVisibleProperty);
            private set => SetValue(IsFocusVisiblePropertyKey, value);
        }

        internal DataGridColumnHeader Header => TemplatedParent as DataGridColumnHeader;

        internal DataGridColumn Column => Header?.Column;

        // Follows the table's column summaries: shows for a column that has one, named for it, and hides otherwise.
        internal void Refresh()
        {
            ColumnSummaryPopup summaries = IsLoaded && GridOf(Header) is { } grid ? ColumnSummary.PopupOf(grid) : null;
            if (!ReferenceEquals(summaries, _summaries))
            {
                _summaries?.Remove(this);
                _summaries = summaries;
                _summaries?.Add(this);
            }

            DataGridColumn column = Column;
            bool shown = summaries != null && column != null && LookState.Current.IsNewLook && summaries.HasSummary(column);
            if (shown)
            {
                string name = Localization.GetLocalizedString("COLUMN_SUMMARY_BUTTON_NAME", ColumnSummary.Title(column));
                AutomationProperties.SetName(this, name);
                ToolTip = name;
            }

            SetValue(VisibilityProperty, shown ? Visibility.Visible : Visibility.Collapsed);
            summaries?.UpdateTabStops();
        }

        internal void Expand()
        {
            if (!IsSummaryOpen)
            {
                _summaries?.Open(Column);
            }
        }

        internal void Collapse()
        {
            if (IsSummaryOpen)
            {
                _summaries?.Close();
            }
        }

        internal void Invoke() => OnClick();

        protected override void OnClick()
        {
            if (AutomationPeer.ListenerExists(AutomationEvents.InvokePatternOnInvoked))
            {
                UIElementAutomationPeer.CreatePeerForElement(this)?.RaiseAutomationEvent(AutomationEvents.InvokePatternOnInvoked);
            }

            base.OnClick();
            if (IsSummaryOpen)
            {
                _summaries?.Close();
            }
            else
            {
                _summaries?.Open(Column);
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (!e.Handled && _summaries != null)
            {
                e.Handled = HandleKey(e);
            }

            base.OnKeyDown(e);
        }

        protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnGotKeyboardFocus(e);
            if (ReferenceEquals(e.NewFocus, this))
            {
                IsFocusVisible = InputManager.Current.MostRecentInputDevice is not (MouseDevice or StylusDevice or TouchDevice);
                _summaries?.SetTabStop(this);
            }
        }

        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnLostKeyboardFocus(e);
            if (ReferenceEquals(e.OldFocus, this))
            {
                IsFocusVisible = false;
            }
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new ColumnSummaryButtonAutomationPeer(this);

        private static void OnIsSummaryOpenChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
        {
            if (UIElementAutomationPeer.FromElement((UIElement)element) is ColumnSummaryButtonAutomationPeer peer)
            {
                peer.RaiseExpandCollapseStateChanged((bool)e.OldValue, (bool)e.NewValue);
            }
        }

        private static DataGrid GridOf(DependencyObject element)
        {
            for (; element != null; element = VisualTreeHelper.GetParent(element))
            {
                if (element is DataGrid grid)
                {
                    return grid;
                }
            }

            return null;
        }

        // Alt+Down opens the summary; Left, Right, Home and End move between the icons; Tab moves on to the rows and
        // Shift+Tab leaves the table; Esc closes an open summary. Enter and Space click, as on any button.
        private bool HandleKey(KeyEventArgs e)
        {
            ModifierKeys modifiers = e.KeyboardDevice.Modifiers;
            bool rightToLeft = FlowDirection == FlowDirection.RightToLeft;
            switch (e.Key)
            {
                case Key.System when e.SystemKey == Key.Down && (modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0:
                    _summaries.Open(Column);
                    return true;
                case Key.Left or Key.Right when modifiers == ModifierKeys.None:
                    _summaries.MoveFocus(this, (e.Key == Key.Right) != rightToLeft ? 1 : -1);
                    return true;
                case Key.Home or Key.End when modifiers == ModifierKeys.None:
                    _summaries.MoveFocus(this, e.Key == Key.Home ? int.MinValue : int.MaxValue);
                    return true;
                case Key.Tab when (modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0:
                    if ((modifiers & ModifierKeys.Shift) != 0 || !_summaries.FocusRows())
                    {
                        MoveFocus(new TraversalRequest(
                            (modifiers & ModifierKeys.Shift) != 0 ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next));
                    }

                    return true;
                case Key.Escape when IsSummaryOpen:
                    _summaries.Close();
                    return true;
                default:
                    return false;
            }
        }
    }

    // An info icon is a button that opens and closes its column's summary, so screen readers hear it as collapsed or
    // expanded. Its glyph is not read.
    internal sealed class ColumnSummaryButtonAutomationPeer : ButtonBaseAutomationPeer, IInvokeProvider, IExpandCollapseProvider
    {
        internal ColumnSummaryButtonAutomationPeer(ColumnSummaryButton owner)
            : base(owner)
        {
        }

        private ColumnSummaryButton Button => (ColumnSummaryButton)Owner;

        public ExpandCollapseState ExpandCollapseState =>
            Button.IsSummaryOpen ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;

        public override object GetPattern(PatternInterface patternInterface) =>
            patternInterface is PatternInterface.Invoke or PatternInterface.ExpandCollapse ? this : base.GetPattern(patternInterface);

        public void Invoke()
        {
            EnsureEnabled();
            Button.Invoke();
        }

        public void Expand()
        {
            EnsureEnabled();
            Button.Expand();
        }

        public void Collapse()
        {
            EnsureEnabled();
            Button.Collapse();
        }

        internal void RaiseExpandCollapseStateChanged(bool wasOpen, bool isOpen) =>
            RaisePropertyChangedEvent(
                ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty,
                wasOpen ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed,
                isOpen ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

        protected override string GetClassNameCore() => nameof(ColumnSummaryButton);

        protected override List<AutomationPeer> GetChildrenCore() => null;

        private void EnsureEnabled()
        {
            if (!IsEnabled())
            {
                throw new ElementNotEnabledException();
            }
        }
    }
}
