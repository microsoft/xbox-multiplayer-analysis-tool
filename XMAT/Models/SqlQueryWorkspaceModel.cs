// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using XMAT.SharedInterfaces;

namespace XMAT.Models
{
    internal sealed class SqlQueryWorkspaceModel : SqlQueryObservable, IAsyncDisposable
    {
        private readonly Dispatcher _dispatcher;
        private readonly Func<bool> _canSnapshot;
        private readonly Func<QuerySourceDescriptor, string> _sourcePath;
        private CancellationTokenSource _sourceCancellation;
        private Task _refreshTask = Task.CompletedTask;
        private Task<bool> _closeTask;
        private Task _disposeTask;
        private bool _refreshAgain;
        private bool _isClosing;
        private bool _disposed;
        private long _closeGeneration;
        private readonly HashSet<SqlQueryDocumentModel> _closingDocuments = [];
        private int _nextDocumentNumber;
        private string _message;
        private SqlQueryDocumentModel _selectedDocument;

        internal ICaptureQueries Queries { get; }
        internal Func<IReadOnlyList<string>> ProtectedPaths { get; }
        public ObservableCollection<SqlQueryDocumentModel> Documents { get; } = [];
        public ObservableCollection<SqlQuerySourceModel> Sources { get; } = [];
        public ObservableCollection<SqlQueryHistoryEntry> History { get; } = [];
        public bool IsClosing => _isClosing;
        public bool CanSnapshot => !_disposed && !_isClosing && (_canSnapshot?.Invoke() ?? true);
        public bool CanCreateDocument => !_disposed && !_isClosing;
        public string Message { get => _message; private set => SetProperty(ref _message, value); }
        public SqlQueryDocumentModel SelectedDocument
        {
            get => _selectedDocument;
            set => SetProperty(ref _selectedDocument, value);
        }

        internal SqlQueryWorkspaceModel(
            ICaptureQueries queries,
            Dispatcher dispatcher = null,
            Func<bool> canSnapshot = null,
            Func<QuerySourceDescriptor, string> sourcePath = null,
            Func<IReadOnlyList<string>> protectedPaths = null)
        {
            Queries = queries ?? throw new ArgumentNullException(nameof(queries));
            _dispatcher = dispatcher;
            _canSnapshot = canSnapshot;
            _sourcePath = sourcePath;
            ProtectedPaths = protectedPaths;
        }

        public SqlQueryDocumentModel NewDocument()
        {
            App.VerifyUiAccess(_dispatcher);
            if (!CanCreateDocument)
                return null;
            var document = new SqlQueryDocumentModel(this,
                Localization.GetLocalizedString("SQL_QUERY_TITLE", ++_nextDocumentNumber));
            Documents.Add(document);
            document.UpdateSources(Sources);
            SelectedDocument = document;
            return document;
        }

        public SqlQueryDocumentModel Reopen(SqlQueryHistoryEntry history)
        {
            var document = NewDocument();
            if (document == null)
                return null;
            document.Text = history.Sql;
            document.SelectedSource = Sources.FirstOrDefault(source => source.Id == history.SourceId) ??
                new SqlQuerySourceModel(new(history.SourceId, QuerySourceKind.Loaded, history.SourceName),
                    null, history.SourceName)
                { IsAvailable = false };
            document.UpdateSources(Sources);
            return document;
        }

        internal void AddHistory(SqlQueryHistoryEntry history)
        {
            History.Insert(0, history);
            while (History.Count > 100)
                History.RemoveAt(History.Count - 1);
        }

        public Task RefreshSourcesAsync() => InvokeAsync(() =>
        {
            if (_disposed || _isClosing)
                return Task.CompletedTask;
            _refreshAgain = true;
            if (!_refreshTask.IsCompleted)
                return _refreshTask;
            _sourceCancellation?.Dispose();
            _sourceCancellation = new();
            _refreshTask = RefreshSourcesCoreAsync(_sourceCancellation.Token);
            return _refreshTask;
        });

        private async Task RefreshSourcesCoreAsync(CancellationToken cancellationToken)
        {
            try
            {
                do
                {
                    _refreshAgain = false;
                    var descriptors = await Queries.GetSourcesAsync(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_disposed || _isClosing)
                        return;
                    foreach (var old in Sources)
                    {
                        old.IsAvailable = descriptors.Any(source => source.Id == old.Id);
                    }
                    Sources.Clear();
                    foreach (var descriptor in descriptors)
                    {
                        Sources.Add(new(descriptor, _sourcePath?.Invoke(descriptor)));
                    }
                    foreach (var document in Documents)
                        document.UpdateSources(Sources);
                    Message = string.Empty;
                }
                while (_refreshAgain);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                Message = Localization.GetLocalizedString("SQL_SOURCES_FAILED", exception.Message);
            }
        }

        internal void NotifySnapshotAvailability()
        {
            RaisePropertyChanged(nameof(CanSnapshot));
            foreach (var document in Documents)
                document.NotifyReadiness();
        }

        public Task<bool> CloseDocumentAsync(
            SqlQueryDocumentModel document, Func<SqlQueryDocumentModel, Task<bool>> confirmUnsaved) =>
            InvokeAsync(async () =>
            {
                if (_isClosing || !Documents.Contains(document) || !_closingDocuments.Add(document))
                    return false;
                await document.PauseAndDrainAsync();
                try
                {
                    if (document.IsDirty && !await confirmUnsaved(document))
                        return false;
                    await document.DisposeAsync();
                    var index = Documents.IndexOf(document);
                    Documents.Remove(document);
                    if (ReferenceEquals(SelectedDocument, document))
                    {
                        SelectedDocument = Documents.Count == 0 ? null : Documents[Math.Min(index, Documents.Count - 1)];
                    }
                    return true;
                }
                finally
                {
                    _closingDocuments.Remove(document);
                    document.Resume();
                }
            });

        public Task<bool> RequestCloseAsync(Func<SqlQueryDocumentModel, Task<bool>> confirmUnsaved) =>
            InvokeAsync(() =>
            {
                if (_closeTask != null)
                    return _closeTask;
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var task = completion.Task;
                _closeTask = task;
                var generation = ++_closeGeneration;
                _ = CompleteAsync();
                return task;

                async Task CompleteAsync()
                {
                    try
                    {
                        var accepted = await PrepareCloseAsync(confirmUnsaved, generation);
                        if (!accepted && ReferenceEquals(_closeTask, task))
                            _closeTask = null;
                        completion.TrySetResult(accepted);
                    }
                    catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
                    {
                        if (ReferenceEquals(_closeTask, task))
                            _closeTask = null;
                        completion.TrySetException(exception);
                    }
                }
            });

        private async Task<bool> PrepareCloseAsync(
            Func<SqlQueryDocumentModel, Task<bool>> confirmUnsaved, long generation)
        {
            if (_disposed)
                return true;
            _isClosing = true;
            RaiseClosingProperties();
            _sourceCancellation?.Cancel();
            await Task.WhenAll(Documents.Select(document => document.PauseAndDrainAsync()).Append(_refreshTask));
            try
            {
                foreach (var document in Documents.ToArray())
                {
                    if (generation != _closeGeneration)
                        return false;
                    if (document.IsDirty)
                    {
                        var confirmed = await confirmUnsaved(document);
                        if (generation != _closeGeneration)
                            return false;
                        if (!confirmed)
                        {
                            CancelClosing();
                            return false;
                        }
                    }
                }
                return generation == _closeGeneration;
            }
            catch
            {
                if (generation == _closeGeneration)
                    CancelClosing();
                throw;
            }
        }

        public void CancelClosing()
        {
            App.VerifyUiAccess(_dispatcher);
            if (_disposed)
                return;
            _closeTask = null;
            _closeGeneration++;
            _isClosing = false;
            foreach (var document in Documents)
                document.Resume();
            RaiseClosingProperties();
            _ = RefreshSourcesAsync();
        }

        internal Task PauseAndDrainAsync() => InvokeAsync(async () =>
        {
            _isClosing = true;
            RaiseClosingProperties();
            _sourceCancellation?.Cancel();
            await Task.WhenAll(Documents.Select(document => document.PauseAndDrainAsync()).Append(_refreshTask));
        });

        private void RaiseClosingProperties()
        {
            RaisePropertyChanged(nameof(IsClosing));
            RaisePropertyChanged(nameof(CanCreateDocument));
            NotifySnapshotAvailability();
        }

        internal Task InvokeAsync(Func<Task> action) =>
            App.RunOnUiAsync(_dispatcher, action);

        internal Task<T> InvokeAsync<T>(Func<Task<T>> action) =>
            App.RunOnUiAsync(_dispatcher, action);

        public ValueTask DisposeAsync() => new(InvokeAsync(() => _disposeTask ??= DisposeCoreAsync()));

        private async Task DisposeCoreAsync()
        {
            _disposed = true;
            _closeGeneration++;
            _isClosing = true;
            RaiseClosingProperties();
            await PauseAndDrainAsync();
            foreach (var document in Documents)
                await document.DisposeAsync();
            Documents.Clear();
            Sources.Clear();
            History.Clear();
            SelectedDocument = null;
            _sourceCancellation?.Dispose();
            _sourceCancellation = null;
        }
    }
}
