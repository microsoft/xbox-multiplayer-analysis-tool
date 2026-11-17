// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XMAT.AgentAccess;
using XMAT.AgentConnection;

namespace XMAT
{
    public partial class AgentAccessWindow : Window
    {
        private readonly AgentAccessHost _host;
        private readonly AgentAccessPolicyService _policy;
        private AgentClientSetup _setup;
        private bool _refreshing = true;
        private bool _saving;
        private bool _closed;
        private int _refreshQueued;
        private string _lastFailure;

        internal AgentAccessWindow(AgentAccessHost host)
        {
            _host = host;
            _policy = host.Policy ?? throw new InvalidOperationException("The setup window requires the global policy.");
            InitializeComponent();
            Title = Text("AGENT_ACCESS_TITLE");
            Intro.Text = Text("AGENT_GLOBAL_INTRO");
            EnableAccess.Content = Text("AGENT_GLOBAL_ENABLE");
            DataLabel.Content = Text("AGENT_GLOBAL_DATA");
            ActionsLabel.Content = Text("AGENT_GLOBAL_ACTIONS");
            BackupsLabel.Text = Text("AGENT_GLOBAL_BACKUPS");
            BackupsHint.Text = Text("AGENT_GLOBAL_BACKUPS_HINT");
            SetupGroup.Header = Text("AGENT_GLOBAL_SETUP");
            ClientLabel.Content = Text("AGENT_GLOBAL_CLIENT");
            LaunchLabel.Content = Text("AGENT_GLOBAL_LAUNCH");
            SetupViewLabel.Content = Text("AGENT_GLOBAL_SETUP_VIEW");
            CopyCommand.Content = Text("AGENT_GLOBAL_COPY_COMMAND");
            CopyConfiguration.Content = Text("AGENT_ACCESS_COPY");
            SetupHint.Text = Text("AGENT_GLOBAL_SETUP_HINT");
            CloudDisclosure.Text = Text("AGENT_GLOBAL_CLOUD_DISCLOSURE");
            TrustDisclosure.Text = Text("AGENT_GLOBAL_TRUST_DISCLOSURE");
            BackupDisclosure.Text = Text("AGENT_GLOBAL_BACKUP_DISCLOSURE");
            DataSharingChoice.ItemsSource = new[]
            {
                new Choice<AgentDataSharing>(AgentDataSharing.Redacted, Text("AGENT_GLOBAL_REDACTED")),
                new(AgentDataSharing.Complete, Text("AGENT_GLOBAL_COMPLETE"))
            };
            ActionsChoice.ItemsSource = new[]
            {
                new Choice<AgentActionAccess>(AgentActionAccess.InspectAndExport, Text("AGENT_GLOBAL_INSPECT")),
                new(AgentActionAccess.FullControl, Text("AGENT_GLOBAL_FULL"))
            };
            ClientChoice.ItemsSource = new[]
            {
                new Choice<AgentClientKind>(AgentClientKind.Copilot, "Copilot CLI"),
                new(AgentClientKind.Claude, "Claude Code"),
                new(AgentClientKind.Codex, "Codex CLI"),
                new(AgentClientKind.Gemini, "Gemini CLI")
            };
            LaunchChoice.ItemsSource = new[]
            {
                new Choice<AgentLaunchMode>(AgentLaunchMode.Headless, Text("AGENT_GLOBAL_HEADLESS")),
                new(AgentLaunchMode.Desktop, Text("AGENT_GLOBAL_DESKTOP")),
                new(AgentLaunchMode.ConnectOnly, Text("AGENT_GLOBAL_CONNECT_ONLY"))
            };
            SetupViewChoice.ItemsSource = new[] { Text("AGENT_GLOBAL_INSTRUCTIONS"), Text("AGENT_GLOBAL_CONFIGURATION") };
            ClientChoice.SelectedIndex = LaunchChoice.SelectedIndex = SetupViewChoice.SelectedIndex = 0;
            _host.Changed += Changed;
            _policy.Changed += Changed;
            Closed += (_, _) =>
            {
                _closed = true;
                _host.Changed -= Changed;
                _policy.Changed -= Changed;
            };
            Refresh();
            UpdateSetup();
        }

        internal static AgentAccessWindow ShowOrActivate(Window owner, AgentAccessHost host)
        {
            var window = owner.OwnedWindows.OfType<AgentAccessWindow>().FirstOrDefault();
            if (window == null)
            {
                window = new(host) { Owner = owner };
                window.Show();
            }
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            window.Activate();
            return window;
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        }

        private void Changed(object sender, EventArgs args)
        {
            if (_closed || Dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _refreshQueued, 1) != 0)
                return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref _refreshQueued, 0);
                if (!_closed)
                    Refresh();
            }));
        }

        private void Refresh()
        {
            var state = _policy.Current;
            _refreshing = true;
            try
            {
                if (!_saving)
                {
                    EnableAccess.IsChecked = state.Settings.Enabled;
                    DataSharingChoice.SelectedValue = state.Settings.DataSharing;
                    ActionsChoice.SelectedValue = state.Settings.Actions;
                    AutomaticBackups.IsChecked = state.Settings.AutomaticBackups;
                }
                EnableAccess.IsEnabled = PolicyControls.IsEnabled = !_saving;
                AccessStatus.Text = Text(_saving ? "AGENT_GLOBAL_SAVING" : _host.IsAccepting
                    ? "AGENT_ACCESS_ON" : state.Settings.Enabled ? "AGENT_GLOBAL_DRAINING" : "AGENT_ACCESS_OFF");
                PersistenceStatus.Text = state.HasUnsavedRestriction ? Text("AGENT_GLOBAL_UNSAVED_RESTRICTION") :
                    _lastFailure != null ? _lastFailure :
                    state.SaveFailure.HasValue ? Text("AGENT_GLOBAL_SAVE_FAILED", state.SaveFailure) :
                    state.ReadStatus == AgentPolicyReadStatus.Invalid ? Text("AGENT_GLOBAL_INVALID_POLICY") :
                    Text("AGENT_GLOBAL_PERSISTED");
            }
            finally { _refreshing = false; }
        }

        private async void PolicyChanged_Click(object sender, RoutedEventArgs args) => await SaveAsync();
        private async void PolicyChoice_SelectionChanged(object sender, SelectionChangedEventArgs args)
        {
            if (!_refreshing)
                await SaveAsync();
        }

        private async Task SaveAsync()
        {
            if (_refreshing || _saving || _closed ||
                DataSharingChoice.SelectedValue is not AgentDataSharing data || ActionsChoice.SelectedValue is not AgentActionAccess actions)
                return;
            var current = _policy.Current;
            var desired = current.Settings with
            {
                Enabled = EnableAccess.IsChecked == true,
                DataSharing = data,
                Actions = actions,
                AutomaticBackups = AutomaticBackups.IsChecked == true
            };
            _saving = true;
            _lastFailure = null;
            Refresh();
            try
            {
                var result = await _policy.ExecuteAsync(desired, new(_policy.SettingsDirectory), CancellationToken.None);
                if (!result.Succeeded)
                    throw new AgentPolicyException(result.Value.SaveFailure ?? AgentPolicyFailure.SaveFailed);
                await _host.SynchronizePolicyAsync();
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                if (!_closed)
                    _lastFailure = Text("AGENT_GLOBAL_SAVE_FAILED",
                        exception is AgentPolicyException policy ? policy.Failure : "RuntimeUnavailable");
            }
            finally
            {
                _saving = false;
                if (!_closed)
                    Refresh();
            }
        }

        private void SetupChoice_SelectionChanged(object sender, SelectionChangedEventArgs args)
        {
            if (!_refreshing)
                UpdateSetup();
        }

        private void UpdateSetup()
        {
            if (ClientChoice.SelectedValue is not AgentClientKind client || LaunchChoice.SelectedValue is not AgentLaunchMode launch)
                return;
            _setup = AgentClientConfiguration.Create(AppContext.BaseDirectory, client, launch);
            Configuration.Text = SetupViewChoice.SelectedIndex == 1 ? _setup.Configuration :
                Text("AGENT_GLOBAL_REGISTRATION", _setup.Command ?? Text("AGENT_GLOBAL_TOML_INSTRUCTIONS"),
                    _setup.ManagementCommand, _setup.ConfigurationFile, Path.Combine(AppContext.BaseDirectory, "AGENTS.md"));
            CopyCommand.IsEnabled = _setup.Command != null;
        }

        private void CopyCommand_Click(object sender, RoutedEventArgs args) => Copy(_setup?.Command);
        private void CopyConfiguration_Click(object sender, RoutedEventArgs args) => Copy(_setup?.Configuration);
        private void Copy(string value)
        {
            if (value == null)
                return;
            try
            { Clipboard.SetText(value); }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            { PersistenceStatus.Text = Text("AGENT_GLOBAL_CLIPBOARD_FAILED"); }
        }

        private static string Text(string key, params object[] arguments) => Localization.GetLocalizedString(key, arguments);
        private sealed record Choice<T>(T Value, string Label);
    }
}
