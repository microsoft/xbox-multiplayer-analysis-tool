// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using XMAT.Operations;
using XMAT.Operations.Captures;
using XMAT.Operations.Processes;
using XMAT.WebServiceCapture.Models;

namespace XMAT.WebServiceCapture
{
    public partial class CaptureConnectionView
    {
        // The process filter's decision for a row: the shared selection's, until the row's process changes.
        private bool IsShownByProcessFilter(ProxyConnectionModel model) =>
            _processRows.TryGetValue(model, out ProcessRow row) && Equals(row.Process, model.ClientProcess)
                ? row.State.HasFlag(ProcessRowState.Included)
                : _processMatcher.Matches(model.ClientProcess);

        // Live filtering re-evaluates one row when its process changes. It is on only while a process filter is set, and
        // only on the grid's own view.
        private void SetLiveProcessFiltering(bool active)
        {
            ListCollectionView view = _connectionsView;
            if (view == null || !view.CanChangeLiveFiltering)
            {
                return;
            }
            const string property = nameof(ProxyConnectionModel.ClientProcess);
            if (active)
            {
                if (!view.LiveFilteringProperties.Contains(property))
                {
                    view.LiveFilteringProperties.Add(property);
                }
                if (view.IsLiveFiltering != true)
                {
                    view.IsLiveFiltering = true;
                }
            }
            else if (view.IsLiveFiltering == true || view.LiveFilteringProperties.Contains(property))
            {
                view.IsLiveFiltering = false;
                view.LiveFilteringProperties.Remove(property);
            }
        }

        // Takes the attached capture's process filter. New selectors, or new rows when reselect is set, get a new
        // selection from the shared operation. Returns whether the rows that the grid shows may have changed.
        private bool UpdateProcessSpec(bool reselect)
        {
            ProcessFilterSpec spec = _attachedController?.ProcessFilter ?? ProcessFilterSpec.Empty;
            bool changed = !CaptureProcessViewAdapter.SameSelectors(spec, _processSpec);
            _processSpec = spec;
            if (!changed && !reselect)
            {
                return false;
            }
            _processMatcher = CaptureProcessViewAdapter.CreateMatcher(spec);
            SelectProcesses();
            if (changed)
            {
                ShowProcessSpec(spec);
            }
            return true;
        }

        private void ShowProcessSpec(ProcessFilterSpec spec)
        {
            _processEditTimer?.Stop();
            _showingProcessSpec = true;
            try
            {
                ProcessIncludeText.Text = CaptureProcessViewAdapter.Join(spec.Include);
                ProcessExcludeText.Text = CaptureProcessViewAdapter.Join(spec.Exclude);
            }
            finally
            {
                _showingProcessSpec = false;
            }
            ProcessFilterError.Visibility = Visibility.Collapsed;
        }

        // The rows that the process filter shows and its counts, as the shared selection computes them over the
        // controller's snapshot of the tab's sessions. Later rows and process changes update both incrementally.
        private void SelectProcesses()
        {
            _processRows.Clear();
            FilterCaptureSessionsResult selected = _processMatcher != null && _attachedController != null
                ? SelectSessions(_attachedController, _processSpec)
                : null;
            HashSet<CaptureRowKey> included = selected == null
                ? null
                : new HashSet<CaptureRowKey>(selected.IncludedKeys);
            long includedRows = 0;
            long unknownRows = 0;
            foreach (ProxyConnectionModel model in _observedConnections)
            {
                ClientProcessInfo process = model.ClientProcess;
                ProcessRowState state = included == null
                    ? ProcessStateOf(process)
                    : (included.Contains(model.RowKey) ? ProcessRowState.Included : ProcessRowState.None) |
                        UnknownState(process);
                _processRows[model] = new ProcessRow(state, process);
                includedRows += state.HasFlag(ProcessRowState.Included) ? 1 : 0;
                unknownRows += state.HasFlag(ProcessRowState.Unknown) ? 1 : 0;
            }
            _processIncluded = selected?.Scope.Counts.Included ?? includedRows;
            _processUnknown = selected?.Scope.Counts.Unknown ?? unknownRows;
            UpdateProcessIndicator();
        }

        // The shared selection over the controller's snapshot of the tab's sessions. It completes synchronously; when it
        // fails, the matcher places the rows instead.
        private FilterCaptureSessionsResult SelectSessions(
            WebServiceDeviceCaptureController controller,
            ProcessFilterSpec spec)
        {
            OperationResult<FilterCaptureSessionsResult> selected = ProcessSelection(
                new FilterCaptureSessionsRequest(controller.SnapshotProcessSessions(ProcessSessionsDeviceId), spec),
                default);
            return selected.Succeeded &&
                selected.Value?.IncludedKeys != null &&
                selected.Value.Scope?.Counts != null
                ? selected.Value
                : null;
        }

        private ProcessRowState ProcessStateOf(ClientProcessInfo process) =>
            (_processMatcher == null || _processMatcher.Matches(process)
                ? ProcessRowState.Included
                : ProcessRowState.None) |
            UnknownState(process);

        private static ProcessRowState UnknownState(ClientProcessInfo process) =>
            ProcessFilter.IsUnknown(process) ? ProcessRowState.Unknown : ProcessRowState.None;

        // A new row, or a row whose process changed, is placed by the incremental matcher; a row whose process did not
        // change keeps its decision.
        private void TrackProcess(ProxyConnectionModel model)
        {
            ClientProcessInfo process = model.ClientProcess;
            bool tracked = _processRows.TryGetValue(model, out ProcessRow previous);
            if (tracked && Equals(previous.Process, process))
            {
                return;
            }
            var row = new ProcessRow(ProcessStateOf(process), process);
            if (tracked)
            {
                Tally(previous.State, -1);
            }
            _processRows[model] = row;
            Tally(row.State, 1);
            RequestProcessIndicatorUpdate();
        }

        private void UntrackProcess(ProxyConnectionModel model)
        {
            if (_processRows.Remove(model, out ProcessRow previous))
            {
                Tally(previous.State, -1);
                RequestProcessIndicatorUpdate();
            }
        }

        private void Tally(ProcessRowState state, int delta)
        {
            if (state.HasFlag(ProcessRowState.Included))
            {
                _processIncluded += delta;
            }
            if (state.HasFlag(ProcessRowState.Unknown))
            {
                _processUnknown += delta;
            }
        }

        // Rows arrive in bursts during a capture; the indicator text changes at most once per dispatcher pass.
        private void RequestProcessIndicatorUpdate()
        {
            if (_attachedController != null &&
                _pendingProcessIndicator?.Status != DispatcherOperationStatus.Pending)
            {
                _pendingProcessIndicator = Dispatcher.BeginInvoke(
                    DispatcherPriority.Background, new Action(UpdateProcessIndicator));
            }
        }

        private void UpdateProcessIndicator()
        {
            _pendingProcessIndicator?.Abort();
            _pendingProcessIndicator = null;
            bool active = _attachedController != null && !ProcessFilter.IsEmpty(_processSpec);
            if (active)
            {
                long total = _processRows.Count;
                ProcessFilterIndicator.Text = Localization.GetLocalizedString(
                    "PROCESS_FILTER_INDICATOR",
                    _processIncluded,
                    total,
                    total - _processIncluded,
                    _processUnknown);
            }
            var visibility = active ? Visibility.Visible : Visibility.Collapsed;
            ProcessFilterIndicator.Visibility = visibility;
            ShowAllProcessesButton.Visibility = visibility;
        }

        private void SetProcessFilter(ProcessFilterSpec spec)
        {
            _attachedController?.SetProcessFilter(spec ?? ProcessFilterSpec.Empty);
        }

        // The selectors in the editor, applied or not.
        private OperationResult<ProcessFilterSpec> ParseProcessEditor() =>
            ProcessFilter.Parse(
                CaptureProcessViewAdapter.Selectors(ProcessIncludeText.Text),
                CaptureProcessViewAdapter.Selectors(ProcessExcludeText.Text));

        private void ApplyProcessEditor()
        {
            _processEditTimer.Stop();
            ApplyProcessSpec(ParseProcessEditor());
        }

        // Applies selectors that came from the editor; invalid selectors stay in the editor next to the error.
        private bool ApplyProcessSpec(OperationResult<ProcessFilterSpec> parsed)
        {
            if (!parsed.Succeeded || parsed.Value == null)
            {
                ShowProcessFilterError();
                return false;
            }
            ProcessFilterError.Visibility = Visibility.Collapsed;
            SetProcessFilter(parsed.Value);
            return true;
        }

        private void ShowProcessFilterError()
        {
            ProcessFilterError.Text = Localization.GetLocalizedString("PROCESS_FILTER_INVALID");
            ProcessFilterError.Visibility = Visibility.Visible;
        }

        private void ApplyProcessFilter_Click(object sender, RoutedEventArgs e) => ApplyProcessEditor();

        private void ShowAllProcesses_Click(object sender, RoutedEventArgs e) => SetProcessFilter(ProcessFilterSpec.Empty);

        // Hides svchost.exe on top of the selectors in the editor, including any not applied yet.
        private void HideSvchost_Click(object sender, RoutedEventArgs e)
        {
            _processEditTimer.Stop();
            OperationResult<ProcessFilterSpec> typed = ParseProcessEditor();
            OperationResult<ProcessFilterSpec> hidden = typed.Succeeded && typed.Value != null
                ? CaptureProcessViewAdapter.WithExcluded(typed.Value, CaptureProcessViewAdapter.ServiceHostSelector)
                : typed;
            if (ApplyProcessSpec(hidden) && _attachedController != null)
            {
                // The capture keeps an equal filter without a notification, so the editor shows it here.
                ShowProcessSpec(_attachedController.ProcessFilter);
            }
        }

        // The row and timeline menus' process commands carry the filter to apply, decided when the menu opened.
        private void ProcessFilterCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = _attachedController != null && e.Parameter is ProcessFilterSpec;
            e.Handled = true;
        }

        private void ProcessFilterCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (e.Parameter is ProcessFilterSpec spec)
            {
                SetProcessFilter(spec);
            }
        }

        private void ProcessFilterText_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_showingProcessSpec && _attachedController != null)
            {
                _processEditTimer.Stop();
                _processEditTimer.Start();
            }
        }

        private void ProcessFilterText_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                ApplyProcessEditor();
            }
        }

        private void ProcessEditTimer_Tick(object sender, EventArgs e) => ApplyProcessEditor();
    }
}
