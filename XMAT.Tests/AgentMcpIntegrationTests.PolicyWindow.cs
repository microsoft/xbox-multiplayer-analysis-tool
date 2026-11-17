// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ModelContextProtocol.Client;
using XMAT.AgentAccess;
using XMAT.Models;

namespace XMAT.Tests
{
    public partial class AgentMcpIntegrationTests
    {
        [Fact]
        public void GlobalWindowAppliesRememberedChoicesToExistingLaterAndNewCaptureConnections()
        {
            WithPolicyWindow((model, host, policy, window, store) =>
            {
                var enable = (ToggleButton)window.FindName("EnableAccess");
                Assert.False(enable.IsChecked);
                Assert.Null(window.FindName("Sessions"));
                Assert.Null(window.FindName("Approve"));
                Assert.Null(window.FindName("CaptureTarget"));
                enable.IsChecked = true;
                enable.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                CaptureDeviceTestData.PumpUntil(() => enable.IsEnabled && host.IsAccepting);
                McpClient first = null, second = null, later = null;
                try
                {
                    first = Complete(Connect(host, "global window first"));
                    second = Complete(Connect(host, "global window second"));
                    ((ComboBox)window.FindName("ActionsChoice")).SelectedIndex = 1;
                    CaptureDeviceTestData.PumpUntil(() => enable.IsEnabled && policy.Current.Settings.Actions == AgentActionAccess.FullControl);
                    ((ComboBox)window.FindName("DataSharingChoice")).SelectedIndex = 1;
                    CaptureDeviceTestData.PumpUntil(() => enable.IsEnabled && policy.Current.Settings.DataSharing == AgentDataSharing.Complete);
                    ((CheckBox)window.FindName("AutomaticBackups")).IsChecked = true;
                    ((CheckBox)window.FindName("AutomaticBackups")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    CaptureDeviceTestData.PumpUntil(() => enable.IsEnabled && policy.Current.Settings.AutomaticBackups);
                    model.CaptureDeviceContexts.Add(new Context("new target without grants"));
                    foreach (var client in new[] { first, second })
                    {
                        var session = Payload(Complete(client.CallToolAsync("xmat_get_session").AsTask()));
                        Assert.Equal("FullControl", session.GetProperty("globalPolicy").GetProperty("actions").GetString());
                        Assert.True(session.GetProperty("globalPolicy").GetProperty("automaticBackups").GetBoolean());
                        Assert.Equal(2, Payload(Complete(client.CallToolAsync("xmat_list_captures").AsTask()))
                            .GetProperty("captures").GetArrayLength());
                    }
                    Assert.All(host.Runtime.GetSessions(), session => Assert.Empty(session.Permissions));
                    window.Close();
                    later = Complete(Connect(host, "connected after dialog closed"));
                    Assert.False(Complete(later.CallToolAsync("xmat_list_captures").AsTask()).IsError ?? false);
                    Assert.True(store.Read().Settings.Enabled);
                    Assert.Equal(AgentDataSharing.Complete, store.Read().Settings.DataSharing);
                }
                finally
                {
                    if (first != null)
                        Finish(first.DisposeAsync().AsTask());
                    if (second != null)
                        Finish(second.DisposeAsync().AsTask());
                    if (later != null)
                        Finish(later.DisposeAsync().AsTask());
                }
            });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void PolicyWindowCannotBroadenAfterSaveFailureAndKeepsFailedRestrictionsEffective(bool restrict)
        {
            WithPolicyWindow((_, host, policy, window, store) =>
            {
                var initial = Complete(policy.SaveAsync(new() { Enabled = restrict }, policy.Current.Settings.Revision));
                Finish(host.SynchronizePolicyAsync());
                WpfTestHost.Pump();
                using var locked = new FileStream(store.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var enable = (ToggleButton)window.FindName("EnableAccess");
                enable.IsChecked = !restrict;
                enable.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                CaptureDeviceTestData.PumpUntil(() => enable.IsEnabled && policy.Current.SaveFailure.HasValue);
                Assert.False(policy.Current.Settings.Enabled);
                Assert.False(host.IsEnabled);
                Assert.Equal(restrict, policy.Current.HasUnsavedRestriction);
                var text = ((TextBlock)window.FindName("PersistenceStatus")).Text;
                Assert.Contains(restrict ? "restart" : "failed save", text, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(initial.Settings.Revision, store.Read().Settings.Revision);
                Assert.Equal(restrict, store.Read().Settings.Enabled);
            });
        }

        [Fact]
        public void ClientAndLaunchChoicesGenerateSetupWithoutChangingPolicy()
        {
            WithPolicyWindow((_, _, policy, window, _) =>
            {
                var revision = policy.Current.Settings.Revision;
                ((ComboBox)window.FindName("ClientChoice")).SelectedIndex = 2;
                ((ComboBox)window.FindName("LaunchChoice")).SelectedIndex = 1;
                ((ComboBox)window.FindName("SetupViewChoice")).SelectedIndex = 1;
                var configuration = ((TextBox)window.FindName("Configuration")).Text;
                Assert.StartsWith("[mcp_servers.xmat]", configuration);
                Assert.Contains("desktop", configuration);
                Assert.False(((Button)window.FindName("CopyCommand")).IsEnabled);
                Assert.Equal(revision, policy.Current.Settings.Revision);
                Assert.False(policy.Current.Settings.Enabled);
            });
        }

        private void WithPolicyWindow(
            Action<CaptureAppModel, AgentAccessHost, AgentAccessPolicyService, AgentAccessWindow, AgentAccessSettingsStore> test)
        {
            _application.Run(owner =>
            {
                WpfTestHost.Show(owner, new Grid());
                using var directory = new CaptureFileExportTests.DirectoryFixture();
                var store = new AgentAccessSettingsStore(Path.Combine(directory.Path, "policy.json"));
                var policy = new AgentAccessPolicyService(store);
                var model = new CaptureAppModel();
                model.ConfigureAgentPolicy(policy);
                model.CaptureDeviceContexts.Add(new Context("initial target"));
                var host = model.AgentAccess;
                AgentAccessWindow window = null;
                try
                {
                    host.MarkReady();
                    Finish(host.SynchronizePolicyAsync());
                    window = AgentAccessWindow.ShowOrActivate(owner, host);
                    WpfTestHost.Pump();
                    Assert.Same(window, AgentAccessWindow.ShowOrActivate(owner, host));
                    test(model, host, policy, window, store);
                }
                finally
                {
                    window?.Close();
                    Finish(host.DisposeAsync().AsTask());
                    Finish(policy.DisposeAsync().AsTask());
                }
            });
        }
    }
}
