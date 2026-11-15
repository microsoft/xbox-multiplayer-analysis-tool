// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;

namespace XMAT
{
    // Announces a status or error message to screen readers when its text changes. Setting Mode on a TextBlock makes it
    // a live region: Polite for status, Assertive for errors that need attention now.
    public static class LiveRegion
    {
        public static readonly DependencyProperty ModeProperty = DependencyProperty.RegisterAttached(
            "Mode", typeof(AutomationLiveSetting), typeof(LiveRegion),
            new PropertyMetadata(AutomationLiveSetting.Off, OnModeChanged));

        // A copy of the element's text, bound so that each change can be announced.
        private static readonly DependencyProperty WatchedTextProperty = DependencyProperty.RegisterAttached(
            "WatchedText", typeof(string), typeof(LiveRegion), new PropertyMetadata(null, OnWatchedTextChanged));

        // The announcement waiting for the next layout, so several quick changes announce once.
        private static readonly DependencyProperty PendingAnnouncementProperty = DependencyProperty.RegisterAttached(
            "PendingAnnouncement", typeof(EventHandler), typeof(LiveRegion), new PropertyMetadata(null));

        // Raised for each announcement, whether or not a screen reader is listening.
        internal static event Action<UIElement> Announced;

        public static AutomationLiveSetting GetMode(DependencyObject element) =>
            (AutomationLiveSetting)element.GetValue(ModeProperty);

        public static void SetMode(DependencyObject element, AutomationLiveSetting value) =>
            element.SetValue(ModeProperty, value);

        private static void OnModeChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
        {
            var mode = (AutomationLiveSetting)e.NewValue;
            AutomationProperties.SetLiveSetting(element, mode);
            if (mode == AutomationLiveSetting.Off || element is not TextBlock)
            {
                BindingOperations.ClearBinding(element, WatchedTextProperty);
                return;
            }

            BindingOperations.SetBinding(element, WatchedTextProperty, new Binding(nameof(TextBlock.Text))
            {
                RelativeSource = RelativeSource.Self,
                Mode = BindingMode.OneWay,
            });
        }

        private static void OnWatchedTextChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
        {
            if (element is not UIElement target || string.IsNullOrEmpty(e.NewValue as string) ||
                target.GetValue(PendingAnnouncementProperty) != null)
            {
                return;
            }

            // Announce after the next layout, so a message that was hidden while empty is visible by then.
            EventHandler announce = null;
            announce = (_, _) =>
            {
                target.LayoutUpdated -= announce;
                target.ClearValue(PendingAnnouncementProperty);
                if (!target.IsVisible)
                {
                    return;
                }

                Announced?.Invoke(target);
                if (AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
                {
                    (UIElementAutomationPeer.FromElement(target) ?? UIElementAutomationPeer.CreatePeerForElement(target))
                        ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
                }
            };
            target.SetValue(PendingAnnouncementProperty, announce);
            target.LayoutUpdated += announce;
        }
    }
}
