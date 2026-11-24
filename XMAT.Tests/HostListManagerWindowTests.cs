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

        // Capture tabs state each list's scope: the original look in a note, the new look in the list's description. In the
        // manager its intro states it once instead: in the new look a lead sentence and the facts that apply to both lists,
        // in the original look one paragraph. Each list then says only what it does.
        [Fact]
        public void Manager_StatesTheScopeInItsIntroInsteadOfInEachList()
        {
            _application.Run(_ =>
            {
                var manager = new HostListManagerWindow(new HostListPolicy());
                bool newLook = LookState.Current.IsNewLook;
                try
                {
                    LookState.Current.IsNewLook = true;
                    manager.Show();
                    WpfTestHost.Pump();
                    var intro = (TextBlock)manager.FindName("IntroText");
                    Assert.False(intro.IsVisible);
                    foreach ((string name, string key) in new[]
                    {
                        ("IntroLeadText", "HOST_LISTS_WINDOW_LEAD"),
                        ("ChangesFactText", "HOST_LISTS_WINDOW_FACT_CHANGES"),
                        ("BlockFactText", "HOST_LISTS_WINDOW_FACT_BLOCK"),
                        ("ExitFactText", "HOST_LISTS_WINDOW_FACT_EXIT"),
                    })
                    {
                        var text = (TextBlock)manager.FindName(name);
                        Assert.True(text.IsVisible);
                        Assert.Equal(Localization.GetLocalizedString(key), text.Text);
                        Assert.DoesNotContain("NO_IMPL_", text.Text);
                    }
                    AssertListsLeaveTheScopeToTheIntro(manager);

                    LookState.Current.IsNewLook = false;
                    WpfTestHost.Pump();
                    Assert.True(intro.IsVisible);
                    Assert.Equal(Localization.GetLocalizedString("HOST_LISTS_WINDOW_INTRO"), intro.Text);
                    Assert.DoesNotContain("NO_IMPL_", intro.Text);
                    Assert.False(((FrameworkElement)manager.FindName("IntroLeadText")).IsVisible);
                    AssertListsLeaveTheScopeToTheIntro(manager);
                }
                finally
                {
                    LookState.Current.IsNewLook = newLook;
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

        // The original look's scope notes are hidden, and the new look's descriptions keep only their first sentence.
        private static void AssertListsLeaveTheScopeToTheIntro(HostListManagerWindow manager)
        {
            foreach ((string editor, string summaryKey) in new[]
            {
                ("BlockListEditor", "HOST_LIST_BLOCK_SUMMARY"),
                ("BypassListEditor", "HOST_LIST_BYPASS_SUMMARY"),
            })
            {
                var view = (FrameworkElement)manager.FindName(editor);
                Assert.Equal(Visibility.Collapsed, ((TextBlock)view.FindName("ScopeText")).Visibility);
                string summary = ((TextBlock)view.FindName("DescriptionText")).Text;
                Assert.Equal(Localization.GetLocalizedString(summaryKey), summary);
                Assert.DoesNotContain("NO_IMPL_", summary);
            }
        }
    }
}
