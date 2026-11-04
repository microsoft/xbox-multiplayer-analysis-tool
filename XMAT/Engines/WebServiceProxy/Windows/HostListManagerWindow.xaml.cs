// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using System.Windows;
using XMAT.WebServiceCapture.Models;

namespace XMAT.WebServiceCapture
{
    /// <summary>
    /// Always-available editor for the application's session-only Block and Bypass lists.
    /// It reuses the list views shown in each capture tab, bound to the same models, so it
    /// works with no capture selected and while viewing imported captures or reports.
    /// </summary>
    public partial class HostListManagerWindow : Window
    {
        private readonly HostListPolicy _policy;
        private WindowState _restoreState = WindowState.Normal;

        internal HostListManagerWindow(HostListPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(policy);
            _policy = policy;
            InitializeComponent();
            Title = Localization.GetLocalizedString("HOST_LISTS_WINDOW_TITLE");
            IntroText.Text = Localization.GetLocalizedString("HOST_LISTS_WINDOW_INTRO");
            BlockTab.Header = Localization.GetLocalizedString("HOST_LISTS_BLOCK_TAB");
            BypassTab.Header = Localization.GetLocalizedString("HOST_LISTS_BYPASS_TAB");
            CloseButton.Content = Localization.GetLocalizedString("HOST_LISTS_CLOSE");
            BlockListEditor.BlockList = policy.BlockList;
            BypassListEditor.BypassList = policy.BypassList;
        }

        /// <summary>
        /// Brings back the visible manager that <paramref name="owner"/> owns for these lists,
        /// restoring a minimized one to its last state before activating it, or opens a new one.
        /// </summary>
        internal static HostListManagerWindow ShowOrActivate(Window owner, HostListPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(owner);
            HostListManagerWindow manager = owner.OwnedWindows.OfType<HostListManagerWindow>()
                .FirstOrDefault(window => window.IsVisible && ReferenceEquals(window._policy, policy));
            if (manager == null)
            {
                manager = new HostListManagerWindow(policy) { Owner = owner };
                manager.Show();
                return manager;
            }

            if (manager.WindowState == WindowState.Minimized)
            {
                manager.WindowState = manager._restoreState;
            }
            manager.Activate();
            return manager;
        }

        // Remembers the state that a minimized manager returns to.
        protected override void OnStateChanged(EventArgs e)
        {
            if (WindowState != WindowState.Minimized)
            {
                _restoreState = WindowState;
            }
            base.OnStateChanged(e);
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
