// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;
using System.Windows.Controls;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests
{
    // Actions > Manage Host Lists keeps one manager per owner window and brings it back when it
    // is chosen again, restoring it from the minimized state without changing any other state. The
    // manager states the lists' shared, session-only scope once, in its intro.
    [Collection(CaptureUiApplicationCollection.Name)]
    public class HostListManagerWindowTests
    {
        private readonly CaptureUiApplicationFixture _application;

        public HostListManagerWindowTests(CaptureUiApplicationFixture application) => _application = application;

        // Capture tabs show each list's scope note; in the manager its intro states it once instead.
        [Fact]
        public void Manager_StatesTheScopeInItsIntroAndHidesEachListsScopeNote()
        {
            _application.Run(_ =>
            {
                var manager = new HostListManagerWindow(new HostListPolicy());
                try
                {
                    manager.Show();
                    WpfTestHost.Pump();
                    var intro = (TextBlock)manager.FindName("IntroText");
                    Assert.True(intro.IsVisible);
                    Assert.Equal(Localization.GetLocalizedString("HOST_LISTS_WINDOW_INTRO"), intro.Text);
                    Assert.DoesNotContain("NO_IMPL_", intro.Text);
                    Assert.Equal(Visibility.Collapsed, ScopeNote(manager, "BlockListEditor").Visibility);
                    Assert.Equal(Visibility.Collapsed, ScopeNote(manager, "BypassListEditor").Visibility);
                }
                finally
                {
                    manager.Close();
                    WpfTestHost.Pump();
                }
            });
        }

        [Fact]
        public void ShowOrActivate_RestoresAMinimizedManagerAndReusesItsWindowAndLists()
        {
            _application.Run(window =>
            {
                WpfTestHost.Show(window, new Grid());
                var policy = new HostListPolicy();
                HostListManagerWindow manager = null;
                HostListManagerWindow reopened = null;
                try
                {
                    manager = HostListManagerWindow.ShowOrActivate(window, policy);
                    WpfTestHost.Pump();
                    Assert.True(manager.IsVisible);
                    Assert.Same(window, manager.Owner);
                    AssertEditsPolicy(manager, policy);

                    // Minimized from its normal state, it comes back normal.
                    manager.WindowState = WindowState.Minimized;
                    WpfTestHost.Pump();
                    Assert.Same(manager, HostListManagerWindow.ShowOrActivate(window, policy));
                    WpfTestHost.Pump();
                    Assert.Equal(WindowState.Normal, manager.WindowState);

                    // Minimized from maximized, it comes back maximized.
                    manager.WindowState = WindowState.Maximized;
                    WpfTestHost.Pump();
                    manager.WindowState = WindowState.Minimized;
                    WpfTestHost.Pump();
                    Assert.Same(manager, HostListManagerWindow.ShowOrActivate(window, policy));
                    WpfTestHost.Pump();
                    Assert.Equal(WindowState.Maximized, manager.WindowState);

                    // A manager that is not minimized keeps its state.
                    Assert.Same(manager, HostListManagerWindow.ShowOrActivate(window, policy));
                    WpfTestHost.Pump();
                    Assert.Equal(WindowState.Maximized, manager.WindowState);
                    Assert.Same(manager, Assert.Single(window.OwnedWindows.OfType<HostListManagerWindow>()));
                    AssertEditsPolicy(manager, policy);

                    // Once closed, the next request opens a new manager for the same lists.
                    HostListManagerWindow closed = manager;
                    manager = null;
                    closed.Close();
                    WpfTestHost.Pump();
                    reopened = HostListManagerWindow.ShowOrActivate(window, policy);
                    WpfTestHost.Pump();
                    Assert.NotSame(closed, reopened);
                    Assert.True(reopened.IsVisible);
                    Assert.Equal(WindowState.Normal, reopened.WindowState);
                    AssertEditsPolicy(reopened, policy);
                }
                finally
                {
                    manager?.Close();
                    reopened?.Close();
                    WpfTestHost.Pump();
                }
            });
        }

        private static void AssertEditsPolicy(HostListManagerWindow manager, HostListPolicy policy)
        {
            Assert.Same(policy.BlockList, ((BlockListView)manager.FindName("BlockListEditor")).BlockList);
            Assert.Same(policy.BypassList, ((BypassListView)manager.FindName("BypassListEditor")).BypassList);
        }

        private static TextBlock ScopeNote(HostListManagerWindow manager, string editor) =>
            (TextBlock)((FrameworkElement)manager.FindName(editor)).FindName("ScopeText");
    }
}
