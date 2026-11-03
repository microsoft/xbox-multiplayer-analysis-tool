// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using XMAT.AgentAccess;
using XMAT.SharedInterfaces;

namespace XMAT
{
    public partial class AgentAccessWindow : Window
    {
        private readonly AgentAccessHost _host;
        private bool _refreshing;
        private bool _closed;
        private int _refreshQueued;

        internal AgentAccessWindow(AgentAccessHost host)
        {
            _host = host;
            InitializeComponent();
            Title = Text("AGENT_ACCESS_TITLE");
            EnableAccess.Content = Text("AGENT_ACCESS_ENABLE");
            CopyConfiguration.Content = Text("AGENT_ACCESS_COPY");
            Intro.Text = Text("AGENT_ACCESS_INTRO");
            SessionsGroup.Header = Text("AGENT_ACCESS_SESSIONS");
            OperationsGroup.Header = Text("AGENT_ACCESS_OPERATIONS");
            ResourcesGroup.Header = Text("AGENT_ACCESS_RESOURCES");
            SummaryGrant.Content = Text("AGENT_GRANT_SUMMARY");
            ControlGrant.Content = Text("AGENT_GRANT_CONTROL");
            DeviceCreationGrant.Content = Text("AGENT_GRANT_DEVICE_CREATION");
            DeviceCreationGrant.ToolTip = Text("AGENT_DEVICE_CREATION_SCOPE");
            CaptureLoadGrant.Content = Text("AGENT_GRANT_CAPTURE_LOAD");
            CaptureLoadGrant.ToolTip = Text("AGENT_CAPTURE_LOAD_SCOPE");
            AdministrationGrantsLabel.Text = Text("CTRL_GRANTS_APPLICATION");
            HostPolicyGrant.Content = Text("CTRL_GRANT_HOST_POLICY");
            HostPolicyGrant.ToolTip = Text("CTRL_GRANT_HOST_POLICY_SCOPE");
            PreferencesGrant.Content = Text("CTRL_GRANT_PREFERENCES");
            PreferencesGrant.ToolTip = Text("CTRL_GRANT_PREFERENCES_SCOPE");
            DiagnosticsGrant.Content = Text("CTRL_GRANT_DIAGNOSTICS");
            DiagnosticsGrant.ToolTip = Text("CTRL_GRANT_DIAGNOSTICS_SCOPE");
            NavigationGrant.Content = Text("CTRL_GRANT_NAVIGATION");
            NavigationGrant.ToolTip = Text("CTRL_GRANT_NAVIGATION_SCOPE");
            LifecycleGrant.Content = Text("CTRL_GRANT_LIFECYCLE");
            LifecycleGrant.ToolTip = Text("CTRL_GRANT_LIFECYCLE_SCOPE");
            CaptureImportGrant.Content = Text("AGENT_GRANT_SAZ_IMPORT");
            CaptureImportGrant.ToolTip = Text("AGENT_SAZ_IMPORT_SCOPE");
            ReportExportGrant.Content = Text("APP_GRANT_REPORT_EXPORT");
            ReportExportGrant.ToolTip = Text("APP_REPORT_EXPORT_SCOPE");
            DeviceConfigurationGrant.Content = Text("AGENT_GRANT_DEVICE_CONFIGURATION");
            DeviceConfigurationGrant.ToolTip = Text("AGENT_DEVICE_CONFIGURATION_SCOPE");
            ScriptGrant.Content = Text("AGENT_GRANT_SCRIPTS");
            ScriptReadGrant.Content = Text("AGENT_GRANT_SCRIPT_READ");
            ScriptReadGrant.ToolTip = Text("AGENT_SCRIPT_SOURCE_PRIVACY");
            ScriptExportGrant.Content = Text("AGENT_GRANT_SCRIPT_EXPORT");
            ScriptExportGrant.ToolTip = Text("AGENT_SCRIPT_EXPORT_SCOPE");
            ScriptEditGrant.Content = Text("AGENT_GRANT_SCRIPT_EDIT");
            ScriptEditGrant.ToolTip = Text("AGENT_SCRIPT_EDIT_SCOPE");
            QuerySourceLabel.Text = Text("AGENT_QUERY_SOURCE");
            SourceSummaryGrantLabel.Text = Text("AGENT_GRANT_SOURCE_SUMMARY");
            SourceSummaryGrant.ToolTip = Text("AGENT_SOURCE_SUMMARY_PRIVACY");
            RawQueryGrantLabel.Text = Text("AGENT_GRANT_RAW_QUERY");
            QueryExportGrantLabel.Text = Text("AGENT_GRANT_QUERY_EXPORT");
            CaptureExportGrant.Content = Text("AGENT_GRANT_CAPTURE_EXPORT");
            CaptureExportGrant.ToolTip = Text("AGENT_CAPTURE_EXPORT_SCOPE");
            ExportFolderLabel.Text = Text("AGENT_EXPORT_FOLDER");
            ChooseExportFolder.Content = Text("AGENT_EXPORT_FOLDER_CHOOSE");
            FileWriteGrant.Content = Text("AGENT_GRANT_FILE_WRITE");
            FileWriteGrant.ToolTip = Text("AGENT_FILE_WRITE_SCOPE");
            InputFileLabel.Text = Text("AGENT_INPUT_FILE");
            ChooseInputFile.Content = Text("AGENT_INPUT_FILE_CHOOSE");
            FileReadGrant.Content = Text("AGENT_GRANT_FILE_READ");
            FileReadGrant.ToolTip = Text("AGENT_FILE_READ_SCOPE");
            QueryLimitsLabel.Text = Text("AGENT_QUERY_LIMITS");
            QuerySeconds.ToolTip = Text("AGENT_QUERY_SECONDS");
            QueryMegabytes.ToolTip = Text("AGENT_QUERY_MEGABYTES");
            QueryResultCount.ToolTip = Text("AGENT_QUERY_RESULT_COUNT");
            ApplyGrants.Content = Text("AGENT_GRANT_APPLY");
            Revoke.Content = Text("AGENT_REVOKE");
            Approve.Content = Text("AGENT_APPROVE");
            Deny.Content = Text("AGENT_DENY");
            CancelOperation.Content = Text("AGENT_CANCEL_OPERATION");
            TakeOver.Content = Text("AGENT_TAKE_OVER");
            ClientColumn.Header = Text("AGENT_CLIENT");
            SessionStateColumn.Header = OperationStateColumn.Header = ResourceStateColumn.Header = Text("AGENT_STATE");
            ActionColumn.Header = Text("AGENT_ACTION");
            ErrorColumn.Header = Text("AGENT_ERROR");
            ResourceKindColumn.Header = Text("AGENT_RESOURCE_KIND");
            ResourceIdColumn.Header = Text("AGENT_RESOURCE_ID");
            Configuration.Text = host.ConnectionConfiguration;
            CaptureTarget.ToolTip = Text("AGENT_CAPTURE_TARGET");
            CaptureTarget.SelectionChanged += (_, _) => ShowGrantSelection();
            QuerySourceTarget.SelectionChanged += (_, _) => ShowGrantSelection();
            ExportFolderTarget.SelectionChanged += (_, _) => ShowGrantSelection();
            InputFileTarget.SelectionChanged += (_, _) => ShowGrantSelection();
            ScriptReadGrant.Checked += (_, _) =>
            {
                ScriptEditGrant.IsEnabled = true;
                ScriptExportGrant.IsEnabled = true;
            };
            ScriptReadGrant.Unchecked += (_, _) =>
            {
                ScriptEditGrant.IsChecked = false;
                ScriptEditGrant.IsEnabled = false;
                ScriptExportGrant.IsChecked = false;
                ScriptExportGrant.IsEnabled = false;
            };
            RawQueryGrant.Checked += (_, _) =>
            {
                QueryExportGrant.IsEnabled = true;
                CaptureExportGrant.IsEnabled = QuerySourceTarget.SelectedItem is QuerySourceDescriptor;
                ReportExportGrant.IsEnabled = QuerySourceTarget.SelectedItem is QuerySourceDescriptor;
            };
            RawQueryGrant.Unchecked += (_, _) =>
            {
                QueryExportGrant.IsChecked = false;
                QueryExportGrant.IsEnabled = false;
                CaptureExportGrant.IsChecked = false;
                CaptureExportGrant.IsEnabled = false;
                ReportExportGrant.IsChecked = false;
                ReportExportGrant.IsEnabled = false;
            };
            _host.Changed += HostChanged;
            Closed += (_, _) =>
            {
                _closed = true;
                _host.Changed -= HostChanged;
            };
            QueueRefresh();
        }

        internal static AgentAccessWindow ShowOrActivate(Window owner, AgentAccessHost host)
        {
            var window = owner.OwnedWindows.OfType<AgentAccessWindow>().FirstOrDefault();
            if (window == null)
            {
                window = new AgentAccessWindow(host) { Owner = owner };
                window.Show();
            }
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }
            window.Activate();
            return window;
        }

        private void HostChanged(object sender, EventArgs args) => QueueRefresh();

        private void QueueRefresh()
        {
            if (_closed || Dispatcher.HasShutdownStarted ||
                Interlocked.Exchange(ref _refreshQueued, 1) != 0)
            {
                return;
            }
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                Interlocked.Exchange(ref _refreshQueued, 0);
                if (!_closed)
                {
                    await TryAsync(RefreshAsync);
                }
            }));
        }

        private async Task RefreshAsync()
        {
            var selectedSession = (Sessions.SelectedItem as AgentSessionSnapshot)?.Id;
            var selectedCapture = (CaptureTarget.SelectedItem as CaptureDescriptor)?.Id;
            var selectedSource = (QuerySourceTarget.SelectedItem as QuerySourceDescriptor)?.Id;
            var selectedFolder = (ExportFolderTarget.SelectedItem as ExportFolderDescriptor)?.Id;
            var selectedInput = (InputFileTarget.SelectedItem as InputFileDescriptor)?.Id;
            var selectedOperation = (Operations.SelectedItem as AgentOperationSnapshot)?.Id;
            var selectedResource = (OwnedResources.SelectedItem as AgentOwnedResourceSnapshot)?.Resource;
            var captures = await _host.GetCapturesAsync();
            var sources = await _host.GetSourcesAsync();
            if (_closed)
            {
                return;
            }
            _refreshing = true;
            try
            {
                var sessions = _host.Runtime.GetSessions();
                Sessions.ItemsSource = sessions;
                Sessions.SelectedItem = sessions.FirstOrDefault(item => item.Id == selectedSession);
                CaptureTarget.ItemsSource = captures;
                CaptureTarget.SelectedItem = captures.FirstOrDefault(item => item.Id == selectedCapture) ??
                    (captures.Count > 0 ? captures[0] : null);
                QuerySourceTarget.ItemsSource = sources;
                QuerySourceTarget.SelectedItem = sources.FirstOrDefault(item => item.Id == selectedSource) ??
                    (sources.Count > 0 ? sources[0] : null);
                var folders = _host.GetExportFolders();
                ExportFolderTarget.ItemsSource = folders;
                ExportFolderTarget.SelectedItem = folders.FirstOrDefault(item => item.Id == selectedFolder);
                var inputs = _host.GetInputFiles();
                InputFileTarget.ItemsSource = inputs;
                InputFileTarget.SelectedItem = inputs.FirstOrDefault(item => item.Id == selectedInput);
                var operations = _host.Runtime.GetOperations().Reverse().Take(200).ToArray();
                Operations.ItemsSource = operations;
                Operations.SelectedItem = operations.FirstOrDefault(item => item.Id == selectedOperation);
                var resources = _host.Runtime.GetOwnedResources();
                OwnedResources.ItemsSource = resources;
                OwnedResources.SelectedItem = resources.FirstOrDefault(item => item.Resource == selectedResource);
                EnableAccess.IsChecked = _host.IsEnabled;
                AccessStatus.Text = Text(_host.IsEnabled ? "AGENT_ACCESS_ON" : "AGENT_ACCESS_OFF");
                ShowCurrentGrants();
                ShowApproval();
            }
            finally { _refreshing = false; }
            if ((QuerySourceTarget.SelectedItem as QuerySourceDescriptor)?.Id != selectedSource ||
                (CaptureTarget.SelectedItem as CaptureDescriptor)?.Id != selectedCapture)
            {
                ShowGrantSelection();
            }
        }

        private void Sessions_SelectionChanged(object sender, SelectionChangedEventArgs args)
        {
            if (!_refreshing)
            {
                ShowGrantSelection();
            }
        }

        private void ShowGrantSelection()
        {
            if (_refreshing)
            {
                return;
            }
            var session = Sessions.SelectedItem as AgentSessionSnapshot;
            var capture = CaptureTarget.SelectedItem as CaptureDescriptor;
            ControlGrant.IsEnabled = capture is { IsReadOnly: false };
            DeviceConfigurationGrant.IsEnabled = capture is { IsReadOnly: false };
            ScriptGrant.IsEnabled = ScriptReadGrant.IsEnabled =
                capture is { IsReadOnly: false, CaptureType: CaptureType.WebProxy };
            SummaryGrant.IsChecked = session?.Permissions.Any(permission =>
                permission == new AgentPermission(AgentCapability.SummaryRead, AgentScope.Application)) ?? false;
            ControlGrant.IsChecked = HasCaptureGrant(session, capture, AgentCapability.CaptureControl);
            DeviceCreationGrant.IsChecked = session?.Permissions.Contains(
                new AgentPermission(AgentCapability.DeviceCreation, AgentScope.Application)) == true;
            CaptureLoadGrant.IsChecked = session?.Permissions.Contains(
                new AgentPermission(AgentCapability.CaptureLoad, AgentScope.Application)) == true;
            foreach (var (control, capability) in AdministrationGrants())
                control.IsChecked = session?.Permissions.Contains(new AgentPermission(capability, AgentScope.Application)) == true;
            DeviceConfigurationGrant.IsChecked = HasCaptureGrant(session, capture, AgentCapability.DeviceConfiguration);
            ScriptGrant.IsChecked = HasCaptureGrant(session, capture, AgentCapability.ScriptExecute);
            ScriptReadGrant.IsChecked = HasCaptureGrant(session, capture, AgentCapability.ScriptRead);
            ScriptEditGrant.IsChecked = HasCaptureGrant(session, capture, AgentCapability.ScriptEdit);
            ScriptEditGrant.IsEnabled = ScriptReadGrant.IsEnabled && ScriptReadGrant.IsChecked == true;
            ScriptExportGrant.IsChecked = HasCaptureGrant(session, capture, AgentCapability.ScriptExport);
            ScriptExportGrant.IsEnabled = ScriptReadGrant.IsEnabled && ScriptReadGrant.IsChecked == true;
            var source = QuerySourceTarget.SelectedItem as QuerySourceDescriptor;
            SourceSummaryGrant.IsEnabled = source != null;
            SourceSummaryGrant.IsChecked = source != null &&
                session?.Permissions.Contains(new AgentPermission(AgentCapability.SummaryRead, AgentScope.Source(source.Id))) == true;
            RawQueryGrant.IsEnabled = source != null;
            RawQueryGrant.IsChecked = source != null &&
                session?.Permissions.Contains(new AgentPermission(AgentCapability.RawDataRead, AgentScope.Source(source.Id))) == true;
            QueryExportGrant.IsChecked = source != null &&
                session?.Permissions.Contains(new AgentPermission(AgentCapability.QueryResultExport, AgentScope.Source(source.Id))) == true;
            QueryExportGrant.IsEnabled = RawQueryGrant.IsChecked == true;
            CaptureExportGrant.IsEnabled = source != null && RawQueryGrant.IsChecked == true;
            CaptureExportGrant.IsChecked = source != null && session?.Permissions.Contains(
                new AgentPermission(AgentCapability.CaptureExport, AgentScope.Source(source.Id))) == true;
            CaptureImportGrant.IsEnabled = source?.Kind == QuerySourceKind.Active;
            CaptureImportGrant.IsChecked = source?.Kind == QuerySourceKind.Active && session?.Permissions.Contains(
                new AgentPermission(AgentCapability.CaptureImport, AgentScope.Source(source.Id))) == true;
            ReportExportGrant.IsEnabled = source != null && RawQueryGrant.IsChecked == true;
            ReportExportGrant.IsChecked = source != null && session?.Permissions.Contains(
                new AgentPermission(AgentCapability.AnalysisReportExport, AgentScope.Source(source.Id))) == true;
            var folder = ExportFolderTarget.SelectedItem as ExportFolderDescriptor;
            FileWriteGrant.IsEnabled = folder != null;
            FileWriteGrant.IsChecked = folder != null && session?.Permissions.Contains(
                new AgentPermission(AgentCapability.FileWrite, AgentScope.ExportFolder(folder.Id))) == true;
            var input = InputFileTarget.SelectedItem as InputFileDescriptor;
            FileReadGrant.IsEnabled = input != null;
            FileReadGrant.IsChecked = input != null && session?.Permissions.Contains(
                new AgentPermission(AgentCapability.FileRead, AgentScope.InputFile(input.Id))) == true;
            var limits = session?.Limits ?? new AgentLimits();
            QuerySeconds.Text = limits.QueryTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            QueryMegabytes.Text = (limits.MaximumQueryResultBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture);
            QueryResultCount.Text = limits.MaximumQueryResults.ToString(CultureInfo.InvariantCulture);
            ShowCurrentGrants();
        }

        private void ShowCurrentGrants()
        {
            var session = Sessions.SelectedItem as AgentSessionSnapshot;
            CurrentGrants.Text = session == null ? string.Empty : Text("AGENT_CURRENT_GRANTS",
                string.Join(", ", session.Permissions.Select(permission => $"{permission.Capability}:{permission.Scope.Id}")));
        }

        private static bool HasCaptureGrant(AgentSessionSnapshot session, CaptureDescriptor capture, AgentCapability capability) =>
            capture != null && session?.Permissions.Contains(new AgentPermission(capability, AgentScope.Capture(capture.Id))) == true;

        private async void EnableAccess_Click(object sender, RoutedEventArgs args)
        {
            EnableAccess.IsEnabled = false;
            try
            {
                await TryAsync(async () =>
                {
                    if (EnableAccess.IsChecked == true)
                    {
                        _host.Enable();
                    }
                    else
                    {
                        await _host.DisableAsync();
                    }
                    await RefreshAsync();
                });
            }
            finally
            {
                EnableAccess.IsEnabled = true;
                EnableAccess.IsChecked = _host.IsEnabled;
            }
        }

        private async void CopyConfiguration_Click(object sender, RoutedEventArgs args) =>
            await TryAsync(() =>
            {
                Clipboard.SetText(_host.ConnectionConfiguration);
                return Task.CompletedTask;
            });

        private async void ApplyGrants_Click(object sender, RoutedEventArgs args)
        {
            if (Sessions.SelectedItem is not AgentSessionSnapshot selected)
            {
                return;
            }
            await TryAsync(async () =>
            {
                var permissions = new List<AgentPermission>();
                if (SummaryGrant.IsChecked == true)
                {
                    permissions.Add(new AgentPermission(AgentCapability.SummaryRead, AgentScope.Application));
                }
                if (DeviceCreationGrant.IsChecked == true)
                {
                    permissions.Add(new AgentPermission(AgentCapability.DeviceCreation, AgentScope.Application));
                }
                if (CaptureLoadGrant.IsChecked == true)
                    permissions.Add(new AgentPermission(AgentCapability.CaptureLoad, AgentScope.Application));
                foreach (var (control, capability) in AdministrationGrants())
                {
                    if (control.IsChecked == true)
                        permissions.Add(new AgentPermission(capability, AgentScope.Application));
                }
                if (CaptureTarget.SelectedItem is CaptureDescriptor capture)
                {
                    if (!capture.IsReadOnly && DeviceConfigurationGrant.IsChecked == true)
                    {
                        permissions.Add(new AgentPermission(AgentCapability.DeviceConfiguration, AgentScope.Capture(capture.Id)));
                    }
                    if (!capture.IsReadOnly && capture.CaptureType == CaptureType.WebProxy && ScriptReadGrant.IsChecked == true)
                    {
                        permissions.Add(new AgentPermission(AgentCapability.ScriptRead, AgentScope.Capture(capture.Id)));
                        if (ScriptEditGrant.IsChecked == true)
                            permissions.Add(new AgentPermission(AgentCapability.ScriptEdit, AgentScope.Capture(capture.Id)));
                        if (ScriptExportGrant.IsChecked == true)
                            permissions.Add(new AgentPermission(AgentCapability.ScriptExport, AgentScope.Capture(capture.Id)));
                    }
                    if (ControlGrant.IsChecked == true && !capture.IsReadOnly)
                    {
                        permissions.Add(new AgentPermission(AgentCapability.CaptureControl, AgentScope.Capture(capture.Id)));
                    }
                    if (ScriptGrant.IsChecked == true && !capture.IsReadOnly &&
                        capture.CaptureType == CaptureType.WebProxy)
                    {
                        if (MessageBox.Show(this, Text("AGENT_SCRIPT_TRUST"), Title,
                            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                        {
                            return;
                        }
                        permissions.Add(new AgentPermission(AgentCapability.ScriptExecute, AgentScope.Capture(capture.Id)));
                    }
                }
                if (!int.TryParse(QuerySeconds.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
                    !int.TryParse(QueryMegabytes.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var megabytes) ||
                    !int.TryParse(QueryResultCount.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var resultCount) ||
                    seconds <= 0 || seconds > (uint.MaxValue - 1L) / 1000 ||
                    megabytes is <= 0 or > 2047 || resultCount <= 0)
                {
                    MessageBox.Show(this, Text("AGENT_QUERY_LIMITS_INVALID"), Title,
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (RawQueryGrant.IsChecked == true && QuerySourceTarget.SelectedItem is QuerySourceDescriptor source)
                {
                    if (MessageBox.Show(this, Text("AGENT_RAW_QUERY_TRUST", source.DisplayName), Title,
                        MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                    {
                        return;
                    }
                    permissions.Add(new AgentPermission(AgentCapability.RawDataRead, AgentScope.Source(source.Id)));
                    if (QueryExportGrant.IsChecked == true)
                    {
                        permissions.Add(new AgentPermission(AgentCapability.QueryResultExport, AgentScope.Source(source.Id)));
                    }
                    if (CaptureExportGrant.IsChecked == true)
                    {
                        permissions.Add(new AgentPermission(AgentCapability.CaptureExport, AgentScope.Source(source.Id)));
                    }
                }
                if (SourceSummaryGrant.IsChecked == true && QuerySourceTarget.SelectedItem is QuerySourceDescriptor summarySource)
                {
                    permissions.Add(new AgentPermission(AgentCapability.SummaryRead, AgentScope.Source(summarySource.Id)));
                }
                if (FileWriteGrant.IsChecked == true && ExportFolderTarget.SelectedItem is ExportFolderDescriptor folder)
                {
                    permissions.Add(new AgentPermission(AgentCapability.FileWrite, AgentScope.ExportFolder(folder.Id)));
                }
                if (FileReadGrant.IsChecked == true && InputFileTarget.SelectedItem is InputFileDescriptor input)
                {
                    permissions.Add(new AgentPermission(AgentCapability.FileRead, AgentScope.InputFile(input.Id)));
                }
                if (CaptureImportGrant.IsChecked == true && QuerySourceTarget.SelectedItem is QuerySourceDescriptor importSource &&
                    importSource.Kind == QuerySourceKind.Active)
                    permissions.Add(new AgentPermission(AgentCapability.CaptureImport, AgentScope.Source(importSource.Id)));
                if (ReportExportGrant.IsChecked == true && RawQueryGrant.IsChecked == true &&
                    QuerySourceTarget.SelectedItem is QuerySourceDescriptor reportSource)
                    permissions.Add(new AgentPermission(AgentCapability.AnalysisReportExport, AgentScope.Source(reportSource.Id)));
                await _host.Runtime.SetPermissionsAsync(_host.FindSession(selected.Id), permissions,
                    new AgentLimits(selected.Limits.ResponseRows, selected.Limits.ResponseBytes, selected.Limits.MaximumOperations,
                        seconds, megabytes * 1024L * 1024, resultCount));
                await RefreshAsync();
            });
        }

        private async void ChooseExportFolder_Click(object sender, RoutedEventArgs args)
        {
            var dialog = new OpenFolderDialog { Title = Text("AGENT_EXPORT_FOLDER_CHOOSE"), Multiselect = false };
            if (dialog.ShowDialog(this) != true)
                return;
            await TryAsync(async () =>
            {
                var folder = await _host.RegisterExportFolderAsync(dialog.FolderName);
                ExportFolderTarget.ItemsSource = _host.GetExportFolders();
                ExportFolderTarget.SelectedItem = folder;
                FileWriteGrant.IsChecked = false;
            });
        }

        private async void ChooseInputFile_Click(object sender, RoutedEventArgs args)
        {
            var dialog = new OpenFileDialog
            {
                Title = Text("AGENT_INPUT_FILE_CHOOSE"),
                Filter = Text("AGENT_INPUT_FILE_FILTER"),
                Multiselect = false
            };
            if (dialog.ShowDialog(this) != true)
                return;
            ChooseInputFile.IsEnabled = false;
            try
            {
                await TryAsync(async () =>
                {
                    var maximum = (Sessions.SelectedItem as AgentSessionSnapshot)?.Limits.MaximumQueryResultBytes ??
                        new AgentLimits().MaximumQueryResultBytes;
                    var input = await _host.RegisterInputFileAsync(dialog.FileName, maximum);
                    if (_closed)
                        return;
                    InputFileTarget.ItemsSource = _host.GetInputFiles();
                    InputFileTarget.SelectedItem = input;
                    FileReadGrant.IsChecked = false;
                });
            }
            finally { ChooseInputFile.IsEnabled = true; }
        }

        private async void Revoke_Click(object sender, RoutedEventArgs args)
        {
            if (Sessions.SelectedItem is AgentSessionSnapshot selected)
            {
                await TryAsync(() => _host.RevokeAsync(selected.Id));
            }
        }

        private void Operations_SelectionChanged(object sender, SelectionChangedEventArgs args) => ShowApproval();

        private void ShowApproval()
        {
            var operation = Operations.SelectedItem as AgentOperationSnapshot;
            var pending = operation?.State == AgentOperationState.AwaitingApproval;
            ApprovalDetails.Text = pending ? _host.Runtime.GetApprovalDetails(operation.Id) : string.Empty;
            Approve.IsEnabled = Deny.IsEnabled = pending;
        }

        private void Approve_Click(object sender, RoutedEventArgs args)
        {
            if (Operations.SelectedItem is AgentOperationSnapshot operation)
            {
                _host.Runtime.Approve(operation.Id);
            }
        }

        private void Deny_Click(object sender, RoutedEventArgs args)
        {
            if (Operations.SelectedItem is AgentOperationSnapshot operation)
            {
                _host.Runtime.Deny(operation.Id);
            }
        }

        private async void CancelOperation_Click(object sender, RoutedEventArgs args)
        {
            if (Operations.SelectedItem is AgentOperationSnapshot operation)
            {
                await TryAsync(async () =>
                    await _host.Runtime.CancelAsync(_host.FindSession(operation.SessionId), operation.Id));
            }
        }

        private void TakeOver_Click(object sender, RoutedEventArgs args)
        {
            if (OwnedResources.SelectedItem is AgentOwnedResourceSnapshot temporary &&
                temporary.Resource.Kind is AgentResourceKind.QueryResult or AgentResourceKind.Artifact or
                    AgentResourceKind.CaptureData or AgentResourceKind.ScriptValidation)
            {
                MessageBox.Show(this, Text("AGENT_QUERY_TEMPORARY"), Title,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (OwnedResources.SelectedItem is AgentOwnedResourceSnapshot resource &&
                MessageBox.Show(this, Text("AGENT_TAKE_OVER_CONFIRM"), Title,
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            {
                if (!_host.Runtime.TakeOver(resource.Resource))
                {
                    MessageBox.Show(this, Text("AGENT_TAKE_OVER_BUSY"), Title,
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        private async Task TryAsync(Func<Task> action)
        {
            try
            { await action(); }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                var code = exception is AgentAccessException access ? access.Error : AgentAccessError.ExecutionFailed;
                MessageBox.Show(this, Text("AGENT_ACCESS_FAILURE", code), Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static string Text(string key, params object[] arguments) =>
            Localization.GetLocalizedString(key, arguments);

        private (CheckBox Control, AgentCapability Capability)[] AdministrationGrants() =>
        [
            (HostPolicyGrant, AgentCapability.HostPolicy),
            (PreferencesGrant, AgentCapability.Preferences),
            (DiagnosticsGrant, AgentCapability.Diagnostics),
            (NavigationGrant, AgentCapability.Navigation),
            (LifecycleGrant, AgentCapability.Lifecycle)
        ];
    }
}
