// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XMAT.ApplicationServices;
using XMAT.SharedInterfaces;
using XMAT.SqlQueries;

namespace XMAT.Models
{
    internal sealed class SqlQueryDocumentModel : SqlQueryObservable, IAsyncDisposable
    {
        private const int PageSize = 1000;
        private const int PageReadBytes = 1024 * 1024;
        private const int DisplayPageBytes = 4 * 1024 * 1024;
        private readonly SqlQueryWorkspaceModel _workspace;
        private readonly string _untitledName;
        private readonly HashSet<long> _selectedRows = [];
        private readonly List<long> _pageStarts = [0];
        private string _text = string.Empty;
        private string _savedText = string.Empty;
        private string _filePath;
        private string _messageText = string.Empty;
        private string _schemaSearch = string.Empty;
        private string _schemaStatus;
        private string _inspectorText = string.Empty;
        private string _inspectorChunkInfo = string.Empty;
        private IReadOnlyList<QueryTable> _schema = [];
        private IReadOnlyList<SqlQuerySourceModel> _sourceChoices = [];
        private IReadOnlyList<SqlQuerySchemaItem> _schemaItems = [];
        private IReadOnlyList<SqlQueryResultRow> _pageRows = [];
        private SqlQuerySourceModel _selectedSource;
        private SqlQueryResultRow _inspectorRow;
        private IQueryResult _result;
        private CancellationTokenSource _cancellation;
        private Task<bool> _operation = Task.FromResult(true);
        private Task _disposeTask;
        private bool _isBusy;
        private bool _isCancelling;
        private bool _suspended;
        private bool _disposed;
        private bool _updatingSources;
        private bool _inspectorHasMore;
        private long _textRevision;
        private long _generation;
        private long _schemaGeneration;
        private long _pageOffset;
        private long _inspectedRow;
        private int _inspectedColumn;
        private int _selectedInspectorColumn = -1;
        private long _inspectionGeneration;
        private long _inspectorNextOffset;
        private Decoder _inspectorDecoder;
        private int _timeoutSeconds = 60;
        private int _maximumResultMiB = 512;

        internal SqlQueryDocumentModel(SqlQueryWorkspaceModel workspace, string untitledName)
        {
            _workspace = workspace;
            _untitledName = untitledName;
            _schemaStatus = Localization.GetLocalizedString("SQL_SCHEMA_NOT_LOADED");
        }

        public string Text
        {
            get => _text;
            set
            {
                if (!SetProperty(ref _text, value ?? string.Empty))
                    return;
                _textRevision++;
                RaiseDocumentProperties();
            }
        }
        public string FilePath => _filePath;
        public bool IsDirty => !string.Equals(_text, _savedText, StringComparison.Ordinal);
        public string Title => (_filePath == null ? _untitledName : Path.GetFileName(_filePath)) + (IsDirty ? " *" : string.Empty);
        public int CaretOffset { get; set; }
        public int SelectionStart { get; set; }
        public int SelectionLength { get; set; }
        public double VerticalOffset { get; set; }
        public double HorizontalOffset { get; set; }
        public int TimeoutSeconds { get => _timeoutSeconds; set => SetProperty(ref _timeoutSeconds, value); }
        public int MaximumResultMiB { get => _maximumResultMiB; set => SetProperty(ref _maximumResultMiB, value); }
        public bool IsBusy => _isBusy;
        public bool CanEditFiles => !_disposed && !_isBusy && !_suspended;
        public bool CanRun => CanEditFiles && _workspace.CanSnapshot && _selectedSource?.IsAvailable == true &&
            !string.IsNullOrWhiteSpace(Text);
        public bool CanRefreshSchema => CanEditFiles && _workspace.CanSnapshot && _selectedSource?.IsAvailable == true;
        public bool CanCancel => _isBusy && !_isCancelling;
        public bool CanReadResult => CanEditFiles && _result != null;
        public bool CanExportAll => CanReadResult;
        public bool CanExportSelected => CanReadResult && _selectedRows.Count != 0;
        public bool CanPreviousPage => CanReadResult && _pageStarts.Count > 1;
        public bool CanNextPage => CanReadResult && _pageOffset + _pageRows.Count < RowCount;
        public bool InspectorHasMore => CanReadResult && _inspectorHasMore;
        public string RunAvailability => _selectedSource?.IsAvailable != true
            ? Localization.GetLocalizedString("SQL_SOURCE_REQUIRED")
            : !_workspace.CanSnapshot
                ? Localization.GetLocalizedString("SQL_STOP_CAPTURES")
                : Localization.GetLocalizedString("SQL_RUN_TOOLTIP");
        public string MessageText { get => _messageText; private set => SetProperty(ref _messageText, value); }
        public string SchemaStatus { get => _schemaStatus; private set => SetProperty(ref _schemaStatus, value); }
        public IReadOnlyList<SqlQuerySourceModel> SourceChoices => _sourceChoices;
        public SqlQuerySourceModel SelectedSource
        {
            get => _selectedSource;
            set
            {
                if (_updatingSources || ReferenceEquals(value, _selectedSource))
                    return;
                var previousId = _selectedSource?.Id;
                _selectedSource = value;
                RaisePropertyChanged();
                if (previousId != value?.Id)
                    InvalidateSchema();
                NotifyReadiness();
            }
        }
        public IReadOnlyList<QueryTable> Schema => _schema;
        public IReadOnlyList<SqlQuerySchemaItem> SchemaItems => _schemaItems;
        public IReadOnlyList<SqlQueryExample> Examples => SqlQueryExamples.Create(_schema);
        public string SchemaSearch
        {
            get => _schemaSearch;
            set
            {
                if (SetProperty(ref _schemaSearch, value ?? string.Empty))
                    RebuildSchemaItems();
            }
        }
        public IReadOnlyList<QueryColumn> ResultColumns => _result?.Columns ?? [];
        public IReadOnlyList<SqlQueryResultRow> PageRows => _pageRows;
        public long RowCount => _result?.RowCount ?? 0;
        public long PageOffset => _pageOffset;
        public string PageSummary => Localization.GetLocalizedString("SQL_PAGE_SUMMARY",
            _pageRows.Count == 0 ? 0 : _pageOffset + 1, _pageOffset + _pageRows.Count, RowCount, _selectedRows.Count);
        public SqlQueryRun ResultRun { get; private set; }
        public string ResultSummary => ResultRun == null ? Localization.GetLocalizedString("SQL_NO_RESULT") :
            Localization.GetLocalizedString("SQL_RESULT_SUMMARY", ResultRun.SourceName,
                ResultRun.CapturedAt.ToLocalTime(), ResultRun.RowCount, ResultRun.Elapsed.TotalSeconds);
        public IReadOnlyCollection<long> SelectedRows => _selectedRows;
        public SqlQueryResultRow InspectorRow => _inspectorRow;
        public string InspectorText { get => _inspectorText; private set => SetProperty(ref _inspectorText, value); }
        public string InspectorChunkInfo { get => _inspectorChunkInfo; private set => SetProperty(ref _inspectorChunkInfo, value); }

        internal void UpdateSources(IEnumerable<SqlQuerySourceModel> sources)
        {
            var choices = sources.ToList();
            var selected = _selectedSource == null
                ? choices.FirstOrDefault(source => source.Kind == QuerySourceKind.Active)
                : choices.FirstOrDefault(source => source.Id == _selectedSource.Id);
            if (_selectedSource != null && selected == null)
            {
                _selectedSource.IsAvailable = false;
                selected = _selectedSource;
                choices.Add(selected);
            }
            var invalidate = _selectedSource?.Id != selected?.Id ||
                _selectedSource?.IsAvailable == false;
            _updatingSources = true;
            try
            {
                _selectedSource = selected;
                _sourceChoices = choices.AsReadOnly();
                RaisePropertyChanged(nameof(SourceChoices));
                RaisePropertyChanged(nameof(SelectedSource));
            }
            finally
            {
                _updatingSources = false;
            }
            if (invalidate)
                InvalidateSchema();
            NotifyReadiness();
        }

        private void InvalidateSchema()
        {
            _schemaGeneration++;
            _schema = [];
            SchemaStatus = Localization.GetLocalizedString("SQL_SCHEMA_NOT_LOADED");
            RebuildSchemaItems();
            RaisePropertyChanged(nameof(Schema));
            RaisePropertyChanged(nameof(Examples));
        }

        private void RebuildSchemaItems()
        {
            _schemaItems = _schema.Select(table => new SqlQuerySchemaItem(table, null, _schemaSearch.Trim()))
                .Where(item => item.Children.Count != 0 || item.Table.Name.Contains(
                    _schemaSearch.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
            RaisePropertyChanged(nameof(SchemaItems));
        }

        public Task<bool> RefreshSchemaAsync() => _workspace.InvokeAsync(() =>
        {
            if (!CanEditFiles || !_workspace.CanSnapshot || _selectedSource?.IsAvailable != true)
            {
                SchemaStatus = RunAvailability;
                return Task.FromResult(false);
            }
            var source = _selectedSource;
            var generation = ++_schemaGeneration;
            return StartOperationAsync("SQL_SCHEMA_LOADING", async cancellationToken =>
            {
                var schema = await _workspace.Queries.GetSchemaAsync(source.Id, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (generation != _schemaGeneration || _selectedSource?.Id != source.Id)
                    return;
                _schema = schema;
                SchemaStatus = Localization.GetLocalizedString("SQL_SCHEMA_READY", source.Name, DateTimeOffset.Now);
                MessageText = SchemaStatus;
                RebuildSchemaItems();
                RaisePropertyChanged(nameof(Schema));
                RaisePropertyChanged(nameof(Examples));
            });
        });

        public Task<bool> RunAsync(int selectionStart = 0, int selectionLength = 0) => _workspace.InvokeAsync(() =>
        {
            if (!CanRun)
            {
                MessageText = RunAvailability;
                return Task.FromResult(false);
            }
            var execution = SqlQueryText.ExecutionText(Text, selectionStart, selectionLength);
            var source = _selectedSource;
            var generation = ++_generation;
            return StartOperationAsync("SQL_PREPARING", async cancellationToken =>
            {
                var stopwatch = Stopwatch.StartNew();
                var status = Localization.GetLocalizedString("SQL_RUN_FAILED");
                IQueryResult candidate = null;
                try
                {
                    var options = new QueryExecutionOptions(TimeSpan.FromSeconds(TimeoutSeconds),
                        MaximumResultMiB * 1024L * 1024);
                    await using var session = await _workspace.Queries.CreateSessionAsync(source.Id, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    MessageText = Localization.GetLocalizedString("SQL_EXECUTING");
                    candidate = await session.ExecuteAsync(execution.Text, options, cancellationToken);
                    MessageText = Localization.GetLocalizedString("SQL_LOADING_PAGE");
                    var rows = await ReadDisplayPageAsync(candidate, 0, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_disposed || generation != _generation)
                        return;
                    var previous = _result;
                    _result = candidate;
                    candidate = null;
                    ResultRun = new(_result.Id, _result.SnapshotId, source.Id, source.Name, execution.Text,
                        execution.EditorOffset, session.CapturedAt, stopwatch.Elapsed, _result.RowCount);
                    _selectedRows.Clear();
                    _pageStarts.Clear();
                    _pageStarts.Add(0);
                    _pageOffset = 0;
                    _pageRows = rows;
                    SelectInspectorRow(null);
                    RaiseResultProperties();
                    status = Localization.GetLocalizedString("SQL_RUN_SUCCEEDED");
                    MessageText = Localization.GetLocalizedString("SQL_RUN_COMPLETE", RowCount, stopwatch.Elapsed.TotalSeconds);
                    if (previous != null)
                        await previous.DisposeAsync();
                }
                catch (OperationCanceledException)
                {
                    status = Localization.GetLocalizedString("SQL_RUN_CANCELLED");
                    throw;
                }
                finally
                {
                    if (candidate != null)
                        await candidate.DisposeAsync();
                    _workspace.AddHistory(new(source.Id, source.Name, execution.Text, status,
                        DateTimeOffset.Now, stopwatch.Elapsed));
                }
            });
        });

        public Task<bool> MovePageAsync(bool next) => _workspace.InvokeAsync(() =>
        {
            if (next ? !CanNextPage : !CanPreviousPage)
                return Task.FromResult(false);
            var offset = next ? _pageOffset + _pageRows.Count : _pageStarts[^2];
            return StartOperationAsync("SQL_LOADING_PAGE", async cancellationToken =>
            {
                var rows = await ReadDisplayPageAsync(_result, offset, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (next)
                    _pageStarts.Add(offset);
                else
                    _pageStarts.RemoveAt(_pageStarts.Count - 1);
                _pageOffset = offset;
                _pageRows = rows;
                SelectInspectorRow(null);
                RaiseResultProperties();
                MessageText = Localization.GetLocalizedString("SQL_PAGE_ORDER");
            });
        });

        private static async Task<IReadOnlyList<SqlQueryResultRow>> ReadDisplayPageAsync(
            IQueryResult result, long offset, CancellationToken cancellationToken)
        {
            var rows = new List<SqlQueryResultRow>();
            var bytes = 0L;
            while (rows.Count < PageSize && offset + rows.Count < result.RowCount)
            {
                var page = await result.ReadPageAsync(offset + rows.Count, PageSize - rows.Count,
                    PageReadBytes, cancellationToken);
                if (page.Rows.Count == 0)
                    throw new InvalidDataException("A result page made no progress.");
                foreach (var row in page.Rows)
                {
                    var display = SqlQueryResultPresentation.Row(row, result.Columns);
                    var cost = 64L + display.Cells.Sum(cell => 128L + cell.Preview.Length * 2L);
                    if (rows.Count != 0 && bytes + cost > DisplayPageBytes)
                        return rows.AsReadOnly();
                    bytes += cost;
                    rows.Add(display);
                }
            }
            return rows.AsReadOnly();
        }

        public void SetRowSelected(long row, bool selected)
        {
            UpdateSelectedRows(selected ? [row] : [], selected ? [] : [row]);
        }

        public void UpdateSelectedRows(IEnumerable<long> added, IEnumerable<long> removed)
        {
            if (_result == null)
                return;
            var changed = false;
            foreach (var row in removed)
                changed |= _selectedRows.Remove(row);
            foreach (var row in added.Where(row => row >= 0 && row < RowCount))
                changed |= _selectedRows.Add(row);
            if (!changed)
                return;
            RaisePropertyChanged(nameof(SelectedRows));
            RaisePropertyChanged(nameof(PageSummary));
            NotifyReadiness();
        }

        public void ClearSelection()
        {
            _selectedRows.Clear();
            RaisePropertyChanged(nameof(SelectedRows));
            RaisePropertyChanged(nameof(PageSummary));
            NotifyReadiness();
        }

        public void SelectInspectorRow(SqlQueryResultRow row)
        {
            if (ReferenceEquals(_inspectorRow, row))
                return;
            _inspectorRow = row;
            _selectedInspectorColumn = -1;
            _inspectionGeneration++;
            _inspectorHasMore = false;
            _inspectorDecoder = null;
            InspectorText = string.Empty;
            InspectorChunkInfo = string.Empty;
            RaisePropertyChanged(nameof(InspectorRow));
            RaisePropertyChanged(nameof(InspectorHasMore));
        }

        public void SelectInspectorCell(SqlQueryCellModel cell)
        {
            if ((cell?.Ordinal ?? -1) == _selectedInspectorColumn)
                return;
            _selectedInspectorColumn = cell?.Ordinal ?? -1;
            _inspectionGeneration++;
            _inspectorHasMore = false;
            _inspectorDecoder = null;
            InspectorText = string.Empty;
            InspectorChunkInfo = cell?.Description ?? string.Empty;
            RaisePropertyChanged(nameof(InspectorHasMore));
        }

        public Task<bool> InspectValueAsync(SqlQueryCellModel cell, bool restart) => _workspace.InvokeAsync(() =>
        {
            if (!CanReadResult || _inspectorRow == null || cell == null)
                return Task.FromResult(false);
            SelectInspectorCell(cell);
            if (cell.Kind is not (QueryValueKind.Text or QueryValueKind.Blob))
            {
                InspectorText = cell.Preview;
                InspectorChunkInfo = cell.Description;
                _inspectorHasMore = false;
                RaisePropertyChanged(nameof(InspectorHasMore));
                return Task.FromResult(true);
            }
            var row = _inspectorRow;
            var generation = _inspectionGeneration;
            var offset = restart ? 0 : _inspectorNextOffset;
            if (!restart && (!_inspectorHasMore || row.Ordinal != _inspectedRow || cell.Ordinal != _inspectedColumn))
            {
                return Task.FromResult(false);
            }
            return StartOperationAsync("SQL_INSPECT_LOADING", async cancellationToken =>
            {
                var chunk = await _result.ReadValueAsync(row.Ordinal, cell.Ordinal, offset, 64 * 1024, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReferenceEquals(row, _inspectorRow) || generation != _inspectionGeneration)
                    return;
                if (restart || _inspectorDecoder == null)
                    _inspectorDecoder = Encoding.UTF8.GetDecoder();
                if (chunk.Kind == QueryValueKind.Text)
                {
                    var chars = new char[Encoding.UTF8.GetMaxCharCount(chunk.Bytes.Length)];
                    var count = _inspectorDecoder.GetChars(chunk.Bytes, chars, !chunk.HasMore);
                    InspectorText = new string(chars, 0, count);
                }
                else
                {
                    InspectorText = Convert.ToHexString(chunk.Bytes);
                }
                InspectorChunkInfo = Localization.GetLocalizedString("SQL_INSPECT_CHUNK",
                    chunk.ByteOffset, chunk.ByteOffset + chunk.Bytes.Length, chunk.TotalBytes, cell.Type);
                _inspectedRow = row.Ordinal;
                _inspectedColumn = cell.Ordinal;
                _inspectorNextOffset = chunk.ByteOffset + chunk.Bytes.Length;
                _inspectorHasMore = chunk.HasMore;
                RaisePropertyChanged(nameof(InspectorHasMore));
            });
        });

        public Task<bool> ExportAsync(string path, QueryExportFormat format, bool selectedOnly, bool overwrite) =>
            _workspace.InvokeAsync(() =>
            {
                if (!CanReadResult || selectedOnly && _selectedRows.Count == 0)
                    return Task.FromResult(false);
                var result = _result;
                var selection = selectedOnly ? _selectedRows.ToArray() : null;
                return StartOperationAsync("SQL_EXPORTING", async cancellationToken =>
                {
                    var operation = _operation;
                    var progress = new Progress<long>(rows =>
                    {
                        if (!_disposed && _isBusy && ReferenceEquals(_operation, operation))
                        {
                            MessageText = Localization.GetLocalizedString("SQL_EXPORT_PROGRESS", rows,
                                selection?.LongLength ?? result.RowCount);
                        }
                    });
                    var scope = new SqlQueryExportResult(result, selection, progress);
                    await SqlQueryFileOperations.WriteAtomicAsync(path, overwrite,
                        format == QueryExportFormat.Csv ? ".csv" : ".json", _workspace.ProtectedPaths,
                        (stream, token) => QueryResultExporter.WriteAsync(scope, stream, format, long.MaxValue, token),
                        cancellationToken);
                    MessageText = Localization.GetLocalizedString("SQL_EXPORT_COMPLETE", scope.RowCount, path);
                });
            });

        public Task<bool> OpenFileAsync(string path) => _workspace.InvokeAsync(() =>
        {
            var revision = _textRevision;
            return StartOperationAsync("SQL_FILE_LOADING", async cancellationToken =>
            {
                SqlQueryFileOperations.ValidateDestination(path, ".sql", _workspace.ProtectedPaths);
                var text = await File.ReadAllTextAsync(path, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (revision != _textRevision)
                {
                    throw new IOException(Localization.GetLocalizedString("SQL_DRAFT_CHANGED"));
                }
                CaretOffset = SelectionStart = SelectionLength = 0;
                VerticalOffset = HorizontalOffset = 0;
                Text = text;
                _savedText = text;
                _filePath = Path.GetFullPath(path);
                RaisePropertyChanged(nameof(Text));
                RaisePropertyChanged(nameof(FilePath));
                RaiseDocumentProperties();
                MessageText = Localization.GetLocalizedString("SQL_FILE_OPENED", _filePath);
            });
        });

        public Task<bool> SaveFileAsync(string path, bool overwrite, bool forClosing = false) => _workspace.InvokeAsync(() =>
        {
            var text = Text;
            return StartOperationAsync("SQL_FILE_SAVING", async cancellationToken =>
            {
                await SqlQueryFileOperations.WriteAtomicAsync(path, overwrite, ".sql", _workspace.ProtectedPaths,
                    async (stream, token) =>
                    {
                        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
                        await writer.WriteAsync(text.AsMemory(), token);
                        await writer.FlushAsync(token);
                    }, cancellationToken);
                _savedText = text;
                _filePath = Path.GetFullPath(path);
                RaisePropertyChanged(nameof(FilePath));
                RaiseDocumentProperties();
                MessageText = Localization.GetLocalizedString("SQL_FILE_SAVED", _filePath);
            }, forClosing);
        });

        internal void ReportMessage(string message) => MessageText = message;

        private Task<bool> StartOperationAsync(
            string messageKey, Func<CancellationToken, Task> action, bool allowSuspended = false)
        {
            if (_disposed || _isBusy || _suspended && !allowSuspended)
                return Task.FromResult(false);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancellation = new CancellationTokenSource();
            _operation = completion.Task;
            _cancellation = cancellation;
            _isBusy = true;
            _isCancelling = false;
            MessageText = Localization.GetLocalizedString(messageKey);
            NotifyReadiness();
            _ = PerformAsync();
            return completion.Task;

            async Task PerformAsync()
            {
                var succeeded = false;
                try
                {
                    await action(cancellation.Token);
                    succeeded = true;
                }
                catch (OperationCanceledException)
                {
                    MessageText = Localization.GetLocalizedString("SQL_CANCELLED_PRESERVED");
                }
                catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
                {
                    var advice = exception is QueryException query ? query.Error switch
                    {
                        QueryError.ResultLimit => "SQL_QUOTA_ADVICE",
                        QueryError.Timeout => "SQL_TIMEOUT_ADVICE",
                        QueryError.SourceBusy => "SQL_STOP_CAPTURES",
                        QueryError.SourceChanged => "SQL_STALE_ADVICE",
                        QueryError.PageTooSmall => "SQL_PAGE_ADVICE",
                        _ => "SQL_READ_ONLY_ADVICE"
                    } : "SQL_FILE_ADVICE";
                    MessageText = Localization.GetLocalizedString("SQL_ERROR_PRESERVED",
                        exception.Message, Localization.GetLocalizedString(advice));
                }
                finally
                {
                    _cancellation = null;
                    _isBusy = false;
                    _isCancelling = false;
                    cancellation.Dispose();
                    NotifyReadiness();
                    completion.TrySetResult(succeeded);
                }
            }
        }

        public void Cancel()
        {
            if (_cancellation == null)
                return;
            _isCancelling = true;
            MessageText = Localization.GetLocalizedString("SQL_CANCELLING");
            _cancellation.Cancel();
            NotifyReadiness();
        }

        internal Task PauseAndDrainAsync() => _workspace.InvokeAsync(async () =>
        {
            _suspended = true;
            Cancel();
            NotifyReadiness();
            await _operation;
        });

        internal void Resume()
        {
            if (_disposed)
                return;
            _suspended = false;
            NotifyReadiness();
        }

        internal void NotifyReadiness()
        {
            foreach (var property in new[]
            {
                nameof(IsBusy), nameof(CanEditFiles), nameof(CanRun), nameof(CanRefreshSchema), nameof(CanCancel), nameof(CanReadResult),
                nameof(CanExportAll), nameof(CanExportSelected), nameof(CanPreviousPage), nameof(CanNextPage),
                nameof(InspectorHasMore), nameof(RunAvailability)
            })
            {
                RaisePropertyChanged(property);
            }
        }

        private void RaiseDocumentProperties()
        {
            RaisePropertyChanged(nameof(IsDirty));
            RaisePropertyChanged(nameof(Title));
            RaisePropertyChanged(nameof(CanRun));
        }

        private void RaiseResultProperties()
        {
            foreach (var property in new[]
            {
                nameof(ResultColumns), nameof(ResultRun), nameof(ResultSummary), nameof(PageRows),
                nameof(RowCount), nameof(PageOffset), nameof(PageSummary), nameof(SelectedRows)
            })
            {
                RaisePropertyChanged(property);
            }
            NotifyReadiness();
        }

        public ValueTask DisposeAsync() => new(_workspace.InvokeAsync(() => _disposeTask ??= DisposeCoreAsync()));

        private async Task DisposeCoreAsync()
        {
            _disposed = true;
            _generation++;
            _schemaGeneration++;
            await PauseAndDrainAsync();
            if (_result != null)
            {
                await _result.DisposeAsync();
                _result = null;
            }
            _pageRows = [];
            _selectedRows.Clear();
            SelectInspectorRow(null);
            RaiseResultProperties();
        }
    }
}
