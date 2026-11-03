// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XMAT.SharedInterfaces;
using XMAT.NetworkTraceCaptureAnalysis;
using XMAT.XboxLiveCaptureAnalysis;

namespace XMAT.ApplicationServices
{
    internal sealed class CaptureDataDetails : ICaptureDataDetails
    {
        private readonly State _state;
        private int _released;
        public CaptureCompletedReportDescriptor Report => _state.Report?.Descriptor;
        public long RetainedBytes => _state.RetainedBytes;
        internal XboxLiveAnalysisResult CanonicalWebAnalysis => _state.CanonicalWebAnalysis;
        internal NetworkTraceStatisticsResult CanonicalNetworkAnalysis => _state.CanonicalNetworkAnalysis;
        internal IReadOnlyList<CaptureReportArtifact> DesktopManifest => _state.Report?.DesktopManifest;

        internal CaptureDataDetails(
            IQuerySession snapshot, string table, string[] fields, IEnumerable<long> records,
            QueryExecutionOptions options, long retainedBytes, Dictionary<long, CaptureDetailTable> findings,
            Dictionary<long, CaptureDetailTable> frozenRecords, CaptureAnalysisReport report,
            XboxLiveAnalysisResult canonicalWebAnalysis = null, NetworkTraceStatisticsResult canonicalNetworkAnalysis = null)
        {
            _state = new(snapshot, table, fields, records.ToHashSet(), options, retainedBytes,
                findings, frozenRecords, report, canonicalWebAnalysis, canonicalNetworkAnalysis);
        }

        private CaptureDataDetails(State state) => _state = state;

        internal CaptureDataDetails Retain()
        {
            lock (_state.Lifetime)
            {
                ObjectDisposedException.ThrowIf(_released != 0 || _state.Disposing != null, this);
                _state.References++;
                return new(_state);
            }
        }

        internal void OnReleased(Action action)
        {
            lock (_state.Lifetime)
            { _state.Released += action; }
        }

        internal Task WriteDesktopArtifactsAsync(string directory, CancellationToken cancellationToken = default) =>
            _state.Report.WriteAsync(directory, _state.Options.MaximumResultBytes, cancellationToken);

        internal Task AdoptDesktopFilesAsync(string directory) => Task.Run(() => _state.Report.AdoptDesktopFiles(directory));

        public Task<QueryPage> ReadPageAsync(
            CaptureDetailTarget target, long offset, int maximumRows, int maximumBytes,
            QueryExecutionOptions options, CancellationToken cancellationToken = default) =>
            ReadAsync(target, options, (result, token) => result.ReadPageAsync(offset, maximumRows, maximumBytes, token), cancellationToken);

        public Task<QueryValueChunk> ReadValueAsync(
            CaptureDetailTarget target, long row, int column, long byteOffset, int maximumBytes,
            QueryExecutionOptions options, CancellationToken cancellationToken = default) =>
            ReadAsync(target, options, (result, token) => result.ReadValueAsync(row, column, byteOffset, maximumBytes, token), cancellationToken);

        private async Task<T> ReadAsync<T>(
            CaptureDetailTarget target, QueryExecutionOptions options, Func<IQueryResult, CancellationToken, Task<T>> read,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(target);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _released) != 0, this);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(options.Timeout);
            await _state.Gate.WaitAsync(deadline.Token);
            try
            {
                ObjectDisposedException.ThrowIf(_state.Disposing != null, this);
                var result = await _state.GetResultAsync(target, options, deadline.Token);
                return await read(result, deadline.Token);
            }
            finally { _state.Gate.Release(); }
        }

        public async Task<ICaptureReportFiles> CreateReportFilesAsync(
            long maximumBytes, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _released) != 0, this);
            await _state.Gate.WaitAsync(cancellationToken);
            try
            {
                ObjectDisposedException.ThrowIf(_state.Disposing != null, this);
                if (_state.Report == null)
                    throw new CaptureDataException(CaptureDataError.UnsupportedData);
                return await _state.Report.CreateFilesAsync(maximumBytes, cancellationToken);
            }
            finally { _state.Gate.Release(); }
        }

        public async Task<IReadOnlyList<CaptureReportArtifact>> GetReportManifestAsync(
            long maximumBytes, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _released) != 0, this);
            await _state.Gate.WaitAsync(cancellationToken);
            try
            {
                ObjectDisposedException.ThrowIf(_state.Disposing != null, this);
                if (_state.Report == null)
                    throw new CaptureDataException(CaptureDataError.UnsupportedData);
                return _state.Report.Manifest(maximumBytes, cancellationToken);
            }
            finally { _state.Gate.Release(); }
        }

        public ValueTask DisposeAsync()
        {
            TaskCompletionSource completion;
            lock (_state.Lifetime)
            {
                if (Interlocked.Exchange(ref _released, 1) != 0)
                    return new(_state.Disposing ?? Task.CompletedTask);
                if (--_state.References != 0)
                    return ValueTask.CompletedTask;
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _state.Disposing = completion.Task;
            }
            _ = DisposeCoreAsync(completion);
            return new(completion.Task);
        }

        private async Task DisposeCoreAsync(TaskCompletionSource completion)
        {
            try
            {
                await _state.DisposeAsync();
                completion.TrySetResult();
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        }

        private sealed class State(
            IQuerySession snapshot, string table, string[] fields, HashSet<long> records,
            QueryExecutionOptions options, long retainedBytes, Dictionary<long, CaptureDetailTable> findings,
            Dictionary<long, CaptureDetailTable> frozenRecords, CaptureAnalysisReport report,
            XboxLiveAnalysisResult canonicalWebAnalysis, NetworkTraceStatisticsResult canonicalNetworkAnalysis)
        {
            internal object Lifetime { get; } = new();
            internal SemaphoreSlim Gate { get; } = new(1, 1);
            internal int References { get; set; } = 1;
            internal Task Disposing { get; set; }
            internal Action Released { get; set; }
            internal CaptureAnalysisReport Report { get; } = report;
            internal QueryExecutionOptions Options { get; } = options;
            internal long RetainedBytes { get; } = retainedBytes;
            internal XboxLiveAnalysisResult CanonicalWebAnalysis { get; } = canonicalWebAnalysis;
            internal NetworkTraceStatisticsResult CanonicalNetworkAnalysis { get; } = canonicalNetworkAnalysis;
            private IQueryResult _cached;
            private CaptureDetailTarget _cachedTarget;

            internal async Task<IQueryResult> GetResultAsync(
                CaptureDetailTarget target, QueryExecutionOptions currentOptions, CancellationToken cancellationToken)
            {
                var maximum = Math.Min(Options.MaximumResultBytes, currentOptions.MaximumResultBytes);
                if (RetainedBytes > maximum)
                    throw new CaptureDataException(CaptureDataError.LimitExceeded);
                if (target.Kind == CaptureDetailKind.Finding && findings.TryGetValue(target.Id, out var finding))
                    return finding;
                if (target.Kind == CaptureDetailKind.Report)
                    return Report?.Details(target.Id) ?? throw new CaptureDataException(CaptureDataError.DetailNotFound);
                if (target.Kind != CaptureDetailKind.Record || !records.Contains(target.Id))
                    throw new CaptureDataException(CaptureDataError.DetailNotFound);
                if (frozenRecords?.TryGetValue(target.Id, out var frozen) == true)
                    return new CaptureRecordDetailResult(frozen);
                if (_cachedTarget == target && _cached != null)
                {
                    if (RetainedBytes + _cached.StorageBytes > maximum)
                        throw new CaptureDataException(CaptureDataError.LimitExceeded);
                    return _cached;
                }
                if (snapshot == null || maximum - RetainedBytes < 16 * 1024)
                    throw new CaptureDataException(CaptureDataError.LimitExceeded);
                if (_cached != null)
                {
                    await _cached.DisposeAsync();
                    _cached = null;
                    _cachedTarget = null;
                }
                var sql = $"SELECT {string.Join(",", fields.Select(field => $"\"{field}\""))} FROM \"{table}\" " +
                    $"WHERE \"id\"={target.Id.ToString(CultureInfo.InvariantCulture)}";
                _cached = new CaptureRecordDetailResult(await snapshot.ExecuteAsync(sql, new QueryExecutionOptions(
                    currentOptions.Timeout, maximum - RetainedBytes), cancellationToken));
                _cachedTarget = target;
                return _cached;
            }

            internal async Task DisposeAsync()
            {
                await Gate.WaitAsync();
                try
                {
                    if (_cached != null)
                        await _cached.DisposeAsync();
                    if (snapshot != null)
                        await snapshot.DisposeAsync();
                    Released?.Invoke();
                }
                finally { Gate.Release(); }
            }
        }
    }
}
