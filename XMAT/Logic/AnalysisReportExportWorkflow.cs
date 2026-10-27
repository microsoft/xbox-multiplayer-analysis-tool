// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace XMAT
{
    internal enum AnalysisReportExportWorkflowResult
    {
        Rejected,
        PickerCancelled,
        ConsentCancelled,
        Succeeded,
        Failed
    }

    internal interface IAnalysisReportExportDialogService
    {
        Task<string> ShowFolderPickerAsync(
            object owner);

        Task<bool> ShowConsentAsync(
            object owner,
            bool defaultCancel);

        Task ShowSuccessAsync(
            object owner,
            string publishedPath);

        Task ShowErrorAsync(
            object owner,
            string errorMessage);

        Task ShowCloseBlockedAsync(
            object owner,
            string message);
    }

    internal sealed class AnalysisReportExportWorkflow
    {
        private readonly object _owner;
        private readonly IAnalysisReportExportDialogService _dialogs;
        private readonly Func<
            AnalysisExportSource,
            string,
            Task<string>> _exportAsync;
        private readonly Func<string> _closeBlockedMessage;
        private readonly object _stateGate = new();
        private readonly SemaphoreSlim _dialogGate = new(1, 1);

        private bool _isRunning;
        private bool _isDialogOpen;
        private bool _closingStarted;
        private bool _closeNoticeShown;

        internal bool IsRunning
        {
            get
            {
                lock (_stateGate)
                {
                    return _isRunning;
                }
            }
        }

        internal bool IsDialogOpen
        {
            get
            {
                lock (_stateGate)
                {
                    return _isDialogOpen;
                }
            }
        }

        internal bool ClosingStarted
        {
            get
            {
                lock (_stateGate)
                {
                    return _closingStarted;
                }
            }
        }

        internal AnalysisReportExportWorkflow(
            object owner,
            IAnalysisReportExportDialogService dialogs,
            Func<
                AnalysisExportSource,
                string,
                Task<string>> exportAsync,
            Func<string> closeBlockedMessage = null)
        {
            ArgumentNullException.ThrowIfNull(owner);
            ArgumentNullException.ThrowIfNull(dialogs);
            ArgumentNullException.ThrowIfNull(exportAsync);
            _owner = owner;
            _dialogs = dialogs;
            _exportAsync = exportAsync;
            _closeBlockedMessage = closeBlockedMessage ??
                (() => Localization.GetLocalizedString(
                    "ANALYSIS_EXPORT_CLOSE_BLOCKED_MESSAGE"));
        }

        internal async Task<AnalysisReportExportWorkflowResult> RunAsync(
            AnalysisExportSource source)
        {
            lock (_stateGate)
            {
                if (_closingStarted || _isRunning)
                {
                    return AnalysisReportExportWorkflowResult.Rejected;
                }
                _isRunning = true;
                _closeNoticeShown = false;
            }

            try
            {
                InvalidateCommands();
                string destination = await ShowDialogAsync(
                    () => _dialogs.ShowFolderPickerAsync(
                        _owner));
                if (string.IsNullOrEmpty(destination))
                {
                    return AnalysisReportExportWorkflowResult.PickerCancelled;
                }

                bool consented = await ShowDialogAsync(
                    () => _dialogs.ShowConsentAsync(
                        _owner,
                        true));
                if (!consented)
                {
                    return AnalysisReportExportWorkflowResult.ConsentCancelled;
                }

                string publishedPath = await _exportAsync(
                    source,
                    destination);
                await ShowDialogAsync(
                    () => _dialogs.ShowSuccessAsync(
                        _owner,
                        publishedPath));
                return AnalysisReportExportWorkflowResult.Succeeded;
            }
            catch (Exception exception) when (
                !ExceptionUtilities.IsFatal(exception))
            {
                string details = GetExportErrorDetails(exception);
                LogBestEffort(
                    $"Analysis report export failed: {details}" +
                    $"{Environment.NewLine}{exception}");
                try
                {
                    await ShowDialogAsync(
                        () => _dialogs.ShowErrorAsync(
                            _owner,
                            details));
                }
                catch (Exception dialogException) when (
                    !ExceptionUtilities.IsFatal(dialogException))
                {
                    LogBestEffort(
                        $"Analysis export error dialog failed: " +
                        $"{dialogException}");
                }
                return AnalysisReportExportWorkflowResult.Failed;
            }
            finally
            {
                lock (_stateGate)
                {
                    _isRunning = false;
                }
                InvalidateCommands();
            }
        }

        internal async Task<bool> RequestCloseAsync()
        {
            bool suppressNotice = false;
            lock (_stateGate)
            {
                if (_closingStarted)
                {
                    return true;
                }
                if (!_isRunning)
                {
                    _closingStarted = true;
                    return true;
                }
                if (_isDialogOpen || _closeNoticeShown)
                {
                    suppressNotice = true;
                }
                else
                {
                    _closeNoticeShown = true;
                }
            }

            if (suppressNotice)
            {
                InvalidateCommands();
                return false;
            }

            try
            {
                await ShowDialogAsync(
                    () => _dialogs.ShowCloseBlockedAsync(
                        _owner,
                        _closeBlockedMessage()));
            }
            catch (Exception exception) when (
                !ExceptionUtilities.IsFatal(exception))
            {
                LogBestEffort(
                    $"Analysis export close notice failed: {exception}");
            }
            finally
            {
                lock (_stateGate)
                {
                    _closeNoticeShown = false;
                }
            }
            InvalidateCommands();
            return false;
        }

        internal void CancelClosing()
        {
            lock (_stateGate)
            {
                _closingStarted = false;
            }
            InvalidateCommands();
        }

        private async Task<T> ShowDialogAsync<T>(
            Func<Task<T>> showDialogAsync)
        {
            await _dialogGate.WaitAsync();
            SetDialogOpen(true);
            try
            {
                return await showDialogAsync();
            }
            finally
            {
                SetDialogOpen(false);
                _dialogGate.Release();
            }
        }

        private async Task ShowDialogAsync(
            Func<Task> showDialogAsync)
        {
            await _dialogGate.WaitAsync();
            SetDialogOpen(true);
            try
            {
                await showDialogAsync();
            }
            finally
            {
                SetDialogOpen(false);
                _dialogGate.Release();
            }
        }

        private void SetDialogOpen(bool value)
        {
            lock (_stateGate)
            {
                _isDialogOpen = value;
            }
        }

        private static void InvalidateCommands()
        {
            Dispatcher dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null ||
                dispatcher.HasShutdownStarted ||
                dispatcher.HasShutdownFinished)
            {
                return;
            }

            if (!dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(
                        CommandManager.InvalidateRequerySuggested));
                return;
            }

            CommandManager.InvalidateRequerySuggested();
        }

        private static string GetExportErrorDetails(Exception exception)
        {
            string details = exception.Message;
            if (exception is not AnalysisReportExportException exportException)
            {
                return details;
            }

            if (exportException.PublicationState ==
                    AnalysisReportPublicationState.Unknown &&
                !string.IsNullOrEmpty(exportException.PublishedPath))
            {
                details += Environment.NewLine + Environment.NewLine +
                    Localization.GetLocalizedString(
                        "ANALYSIS_EXPORT_UNCERTAIN_PUBLICATION",
                        exportException.PublishedPath);
            }
            if (!string.IsNullOrEmpty(exportException.ResiduePath))
            {
                details += Environment.NewLine + Environment.NewLine +
                    Localization.GetLocalizedString(
                        "ANALYSIS_EXPORT_RESIDUE",
                        exportException.ResiduePath);
            }
            return details;
        }

        private static void LogBestEffort(string message)
        {
            try
            {
                PublicUtilities.AppLog(LogLevel.ERROR, message);
            }
            catch (Exception exception) when (
                !ExceptionUtilities.IsFatal(exception))
            {
            }
        }
    }

    internal sealed class WpfAnalysisReportExportDialogService :
        IAnalysisReportExportDialogService
    {
        private static readonly Guid FolderDialogClientGuid =
            new("68F1AFB3-7F5E-477C-A397-C1EA9B93C51F");

        public Task<string> ShowFolderPickerAsync(
            object owner)
        {
            Window window = GetOwner(owner);
            var dialog = new OpenFolderDialog
            {
                ClientGuid = FolderDialogClientGuid,
                Title = Localization.GetLocalizedString(
                    "ANALYSIS_EXPORT_DIALOG_TITLE")
            };
            string selectedFolder = dialog.ShowDialog(window) == true
                ? dialog.FolderName
                : null;
            return Task.FromResult(selectedFolder);
        }

        public Task<bool> ShowConsentAsync(
            object owner,
            bool defaultCancel)
        {
            MessageBoxResult result = MessageBox.Show(
                GetOwner(owner),
                Localization.GetLocalizedString(
                    "ANALYSIS_EXPORT_CONSENT_MESSAGE"),
                Localization.GetLocalizedString(
                    "ANALYSIS_EXPORT_CONSENT_TITLE"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning,
                defaultCancel
                    ? MessageBoxResult.Cancel
                    : MessageBoxResult.OK);
            return Task.FromResult(result == MessageBoxResult.OK);
        }

        public Task ShowSuccessAsync(
            object owner,
            string publishedPath)
        {
            MessageBox.Show(
                GetOwner(owner),
                Localization.GetLocalizedString(
                    "ANALYSIS_EXPORT_SUCCESS_MESSAGE",
                    publishedPath),
                Localization.GetLocalizedString(
                    "ANALYSIS_EXPORT_SUCCESS_TITLE"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return Task.CompletedTask;
        }

        public Task ShowErrorAsync(
            object owner,
            string errorMessage)
        {
            MessageBox.Show(
                GetOwner(owner),
                Localization.GetLocalizedString(
                    "ANALYSIS_EXPORT_ERROR_MESSAGE",
                    errorMessage),
                Localization.GetLocalizedString(
                    "ANALYSIS_EXPORT_ERROR_TITLE"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return Task.CompletedTask;
        }

        public Task ShowCloseBlockedAsync(
            object owner,
            string message)
        {
            MessageBox.Show(
                GetOwner(owner),
                message,
                Localization.GetLocalizedString(
                    "ANALYSIS_EXPORT_CLOSE_BLOCKED_TITLE"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return Task.CompletedTask;
        }

        private static Window GetOwner(object owner)
        {
            if (owner is Window window)
            {
                return window;
            }
            throw new ArgumentException(
                "The analysis export dialog owner must be a Window.",
                nameof(owner));
        }
    }
}
