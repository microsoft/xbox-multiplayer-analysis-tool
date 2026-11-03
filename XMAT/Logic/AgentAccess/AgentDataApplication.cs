// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XMAT.Models;
using XMAT.SharedInterfaces;

namespace XMAT.AgentAccess
{
    internal sealed partial class AgentDataApplication(
        AgentRuntime runtime, ICaptureInvestigation investigation, Func<bool> accepting,
        ICaptureFileExports fileExports = null, CaptureAppModel navigationModel = null)
    {
        private AgentRuntime Runtime => runtime;
        private ICaptureInvestigation Investigation => investigation;
        private ICaptureFileExports FileExports => fileExports;
        private CaptureAppModel NavigationModel => navigationModel;
        private readonly object _gate = new();
        private readonly Dictionary<Guid, Entry> _results = new();
        private readonly Dictionary<AgentSession, int> _reserved = new();
        private readonly byte[] _cursorKey = RandomNumberGenerator.GetBytes(32);

        internal async Task<JsonElement> ListAnalyzersAsync(AgentSession session, CancellationToken cancellationToken)
        {
            DemandAccepting();
            var permission = new AgentPermission(AgentCapability.SummaryRead, AgentScope.Application);
            runtime.Demand(session, permission);
            var analyzers = await investigation.GetAnalyzersAsync(cancellationToken);
            runtime.Demand(session, permission);
            return AgentJson.Encode(new { analyzers });
        }

        internal Guid Run(
            AgentSession session, Guid sourceId, CaptureDataKind kind, CaptureDataFilter filter,
            string idempotencyKey, bool analyze, int? timeoutSeconds, long? maximumResultBytes)
        {
            DemandAccepting();
            var permission = Summary(sourceId);
            runtime.Demand(session, permission);
            filter ??= new();
            var action = analyze ? "data.analyze" : "data.search";
            var hash = AgentOperationDescriptor.Hash(new { sourceId, kind, filter, timeoutSeconds, maximumResultBytes });
            var descriptor = new AgentOperationDescriptor(action, [permission], AgentOperationEffects.None, hash);
            return runtime.Submit(session, descriptor, idempotencyKey, async context =>
            {
                var limits = runtime.GetSession(session).Limits;
                var seconds = timeoutSeconds ?? limits.QueryTimeoutSeconds;
                var bytes = maximumResultBytes ?? limits.MaximumQueryResultBytes;
                if (seconds <= 0 || seconds > limits.QueryTimeoutSeconds ||
                    bytes < 16 * 1024 || bytes > limits.MaximumQueryResultBytes)
                    throw new AgentAccessException(AgentAccessError.LimitExceeded);
                Reserve(session, limits.MaximumQueryResults);
                CaptureDataResult result = null;
                var stored = false;
                try
                {
                    try
                    {
                        var options = new QueryExecutionOptions(TimeSpan.FromSeconds(seconds), bytes);
                        result = analyze
                            ? await investigation.AnalyzeAsync(sourceId, kind, filter, options, context.CancellationToken)
                            : await investigation.SearchAsync(sourceId, kind, filter, options, context.CancellationToken);
                    }
                    catch (CaptureDataException exception)
                    {
                        throw new AgentAccessException(Map(exception.Error));
                    }
                    ValidateResult(result, sourceId, kind, analyze);
                    var entry = new Entry(session, sourceId, result);
                    lock (_gate)
                    { _results.Add(result.Id, entry); }
                    context.OwnResource(new AgentResourceKey(AgentResourceKind.CaptureData, result.Id),
                        sourceId.ToString("D"), () => RemoveAsync(entry));
                    stored = true;
                    return AgentJson.Encode(new
                    {
                        resultId = result.Id,
                        sourceId,
                        result.SnapshotId,
                        result.CapturedAt,
                        result.Kind,
                        result.IsAnalysis,
                        result.ScannedRecords,
                        result.MatchedRecords,
                        result.MalformedRecords,
                        result.EligibleServiceCalls,
                        recordCount = result.Records.Count,
                        metricCount = result.Metrics.Count,
                        findingCount = result.Findings.Count,
                        result.FirstTimestamp,
                        result.LastTimestamp,
                        result.CaptureId,
                        result.CaptureEpoch,
                        result.SnapshotKind,
                        reportId = result.Report?.Id,
                        reportDocumentCount = result.Report?.Documents.Count ?? 0
                    });
                }
                finally
                {
                    lock (_gate)
                    {
                        if (--_reserved[session] == 0)
                            _reserved.Remove(session);
                    }
                    if (!stored && result != null)
                    {
                        lock (_gate)
                        { _results.Remove(result.Id); }
                        await result.DisposeAsync();
                    }
                }
            }, _ => Task.FromResult(accepting()));
        }

        internal JsonElement ReadPage(AgentSession session, Guid resultId, CaptureDataSection section, string cursor)
        {
            DemandAccepting();
            var entry = GetEntry(session, resultId);
            using var lease = Use(entry, false);
            var result = entry.Result;
            var key = section.ToString();
            var offset = ReadOffset(session, resultId, key, cursor);
            return section switch
            {
                CaptureDataSection.Records => Page(session, result, result.Records, offset, key),
                CaptureDataSection.Metrics => Page(session, result, result.Metrics, offset, key),
                CaptureDataSection.Findings => Page(session, result, result.Findings, offset, key),
                CaptureDataSection.Reports => Page(session, result,
                    result.Report?.Documents ?? Array.Empty<CaptureReportDocumentDescriptor>(), offset, key),
                _ => throw new AgentAccessException(AgentAccessError.InvalidRequest)
            };
        }

        internal JsonElement ReadEvidence(AgentSession session, Guid resultId, long findingId, string cursor)
        {
            DemandAccepting();
            var entry = GetEntry(session, resultId);
            using var lease = Use(entry, false);
            var result = entry.Result;
            if (!result.Findings.Any(finding => finding.Id == findingId))
                throw new AgentAccessException(AgentAccessError.OperationNotFound);
            var key = $"evidence:{findingId}";
            var offset = ReadOffset(session, resultId, key, cursor);
            return Page(session, result,
                result.Evidence.TryGetValue(findingId, out var references) ? references : Array.Empty<CaptureEvidence>(),
                offset, key);
        }

        internal async Task<JsonElement> ReleaseAsync(AgentSession session, Guid resultId)
        {
            DemandAccepting();
            var entry = GetEntry(session, resultId);
            Task binding;
            lock (_gate)
            { binding = entry.RawBinding; }
            if (binding != null)
            {
                try
                { await binding; }
                catch (AgentAccessException) { }
            }
            Guid? rawResource;
            lock (_gate)
            { rawResource = entry.RawResource; }
            if (rawResource.HasValue && !await runtime.ReleaseOwnedResourceAsync(session,
                new AgentResourceKey(AgentResourceKind.CaptureData, rawResource.Value)))
                throw new AgentAccessException(AgentAccessError.RecoveryRequired);
            if (!await runtime.ReleaseOwnedResourceAsync(session, new AgentResourceKey(AgentResourceKind.CaptureData, resultId)))
                throw new AgentAccessException(AgentAccessError.RecoveryRequired);
            return AgentJson.Encode(new { resultId, released = true });
        }

        internal async Task DisposeResultsAsync()
        {
            Entry[] entries;
            lock (_gate)
            { entries = _results.Values.ToArray(); }
            foreach (var entry in entries)
                await RemoveAsync(entry);
        }

        private JsonElement Page<T>(
            AgentSession session, CaptureDataResult result, IReadOnlyList<T> rows, long offset, string key, bool retained = true)
        {
            if (offset > rows.Count)
                throw new AgentAccessException(AgentAccessError.OperationNotFound);
            var limits = runtime.GetSession(session).Limits;
            var values = new List<T>();
            var budget = Math.Max(0, limits.ResponseBytes - 1024);
            long used = 0;
            for (var index = offset; index < rows.Count && values.Count < limits.ResponseRows; index++)
            {
                var row = rows[checked((int)index)];
                var size = JsonSerializer.SerializeToUtf8Bytes(row, AgentJson.Options).Length;
                if (used + size > budget)
                {
                    if (values.Count == 0)
                        throw new AgentAccessException(AgentAccessError.LimitExceeded);
                    break;
                }
                values.Add(row);
                used += size;
            }
            var next = offset + values.Count;
            var hasMore = next < rows.Count;
            var page = AgentJson.Encode(new
            {
                resultId = result.Id,
                result.SourceId,
                result.SnapshotId,
                result.CapturedAt,
                result.CaptureId,
                result.CaptureEpoch,
                result.SnapshotKind,
                agentSessionId = session.Id,
                section = key,
                offset,
                totalCount = rows.Count,
                rows = values,
                hasMore,
                nextCursor = hasMore ? MakeCursor(new Cursor(session.Id, result.Id, key, next)) : null
            });
            if (retained)
                GetEntry(session, result.Id);
            else
                runtime.Demand(session, Summary(result.SourceId));
            DemandAccepting();
            return page;
        }

        private void Reserve(AgentSession session, int maximum)
        {
            lock (_gate)
            {
                var pending = _reserved.GetValueOrDefault(session);
                if (_results.Values.Count(entry => ReferenceEquals(entry.Owner, session)) + pending >= maximum)
                    throw new AgentAccessException(AgentAccessError.LimitExceeded);
                _reserved[session] = pending + 1;
            }
        }

        private Entry GetEntry(AgentSession session, Guid id)
        {
            Entry entry;
            lock (_gate)
            {
                entry = _results.TryGetValue(id, out var found) && ReferenceEquals(found.Owner, session) && !found.Releasing
                    ? found : throw new AgentAccessException(AgentAccessError.OperationNotFound);
            }
            runtime.Demand(session, Summary(entry.SourceId));
            return entry;
        }

        private async Task<AgentCleanupOutcome> RemoveAsync(Entry entry)
        {
            lock (_gate)
            { entry.Releasing = true; }
            await CancelRevokedReadersAsync(entry);
            await Task.WhenAll(entry.Readers.StopAcceptingAndWaitAsync(), entry.RawReaders.StopAcceptingAndWaitAsync());
            if (NavigationModel != null)
                await NavigationModel.UnregisterInvestigationNavigationAsync(entry.NavigationTargets.Values.Select(target => target.Id).ToArray());
            await entry.Result.DisposeAsync();
            entry.RawCancellation.Dispose();
            entry.NavigationGate.Dispose();
            lock (_gate)
            { _results.Remove(entry.Result.Id); }
            return AgentCleanupOutcome.Released;
        }

        private async Task CancelRevokedReadersAsync(Entry entry)
        {
            try
            {
                var current = runtime.GetSession(entry.Owner);
                if (current.State == AgentSessionState.Connected &&
                    current.Permissions.Contains(Raw(entry.SourceId)) && current.Permissions.Contains(Summary(entry.SourceId)))
                    return;
            }
            catch (AgentAccessException) { }
            try
            { await entry.RawCancellation.CancelAsync(); }
            catch (ObjectDisposedException) { }
        }

        private void DemandAccepting()
        {
            if (!accepting())
                throw new AgentAccessException(AgentAccessError.OperationUnavailable);
        }

        private static AgentPermission Summary(Guid sourceId) => new(AgentCapability.SummaryRead, AgentScope.Source(sourceId));
        private static AgentPermission Raw(Guid sourceId) => new(AgentCapability.RawDataRead, AgentScope.Source(sourceId));

        private static void ValidateResult(CaptureDataResult result, Guid sourceId, CaptureDataKind kind, bool analysis)
        {
            if (result == null || result.Id == Guid.Empty || result.SnapshotId == Guid.Empty ||
                result.SourceId != sourceId || result.Kind != kind || result.IsAnalysis != analysis)
                throw new AgentAccessException(AgentAccessError.SourceChanged);
            var report = result.Report;
            if (report != null && (!analysis || report.Id == Guid.Empty || report.SourceId != result.SourceId ||
                report.SnapshotId != result.SnapshotId || report.Kind != result.Kind))
                throw new AgentAccessException(AgentAccessError.SourceChanged);
        }

        private IDisposable Use(Entry entry, bool raw)
        {
            lock (_gate)
            {
                if (entry.Releasing || (raw && entry.RawInvalidated) ||
                    !(raw ? entry.RawReaders : entry.Readers).TryBegin(out var lease))
                    throw new AgentAccessException(AgentAccessError.OperationNotFound);
                return lease;
            }
        }

        private static AgentAccessError Map(CaptureDataError error) => error switch
        {
            CaptureDataError.SourceBusy => AgentAccessError.OperationUnavailable,
            CaptureDataError.SourceChanged => AgentAccessError.SourceChanged,
            CaptureDataError.LimitExceeded => AgentAccessError.LimitExceeded,
            CaptureDataError.Timeout => AgentAccessError.QueryTimeout,
            CaptureDataError.UnsupportedData => AgentAccessError.UnsupportedOperation,
            CaptureDataError.DetailNotFound => AgentAccessError.OperationNotFound,
            _ => AgentAccessError.InvalidRequest
        };

        private static AgentAccessException DetailFailure(Exception exception) => new(exception switch
        {
            CaptureDataException data => Map(data.Error),
            QueryException { Error: QueryError.Timeout } => AgentAccessError.QueryTimeout,
            QueryException { Error: QueryError.ResultLimit or QueryError.PageTooSmall } => AgentAccessError.LimitExceeded,
            QueryException => AgentAccessError.QueryFailed,
            ObjectDisposedException => AgentAccessError.OperationNotFound,
            _ => AgentAccessError.InvalidRequest
        });

        private string MakeCursor(Cursor cursor)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(cursor);
            return Convert.ToBase64String(bytes.Concat(HMACSHA256.HashData(_cursorKey, bytes)).ToArray());
        }

        private long ReadOffset(AgentSession session, Guid resultId, string section, string value)
        {
            if (value == null)
                return 0;
            try
            {
                if (value.Length > 1024)
                    throw new FormatException();
                var bytes = Convert.FromBase64String(value);
                if (bytes.Length <= 32 || !CryptographicOperations.FixedTimeEquals(bytes.AsSpan(bytes.Length - 32),
                    HMACSHA256.HashData(_cursorKey, bytes.AsSpan(0, bytes.Length - 32))))
                    throw new FormatException();
                var cursor = JsonSerializer.Deserialize<Cursor>(bytes.AsSpan(0, bytes.Length - 32));
                if (cursor == null || cursor.Session != session.Id || cursor.Result != resultId ||
                    cursor.Section != section || cursor.Offset < 0)
                    throw new FormatException();
                return cursor.Offset;
            }
            catch (Exception exception) when (exception is FormatException or JsonException)
            {
                throw new AgentAccessException(AgentAccessError.OperationNotFound);
            }
        }

        private sealed class Entry(AgentSession owner, Guid sourceId, CaptureDataResult result)
        {
            internal AgentSession Owner { get; } = owner;
            internal Guid SourceId { get; } = sourceId;
            internal CaptureDataResult Result { get; } = result;
            internal AsyncOperationTracker Readers { get; } = StartReaders();
            internal AsyncOperationTracker RawReaders { get; } = StartReaders();
            internal bool Releasing { get; set; }
            internal bool RawInvalidated { get; set; }
            internal Guid? RawResource { get; set; }
            internal Task RawBinding { get; set; }
            internal string RawBindingKey { get; } = Guid.NewGuid().ToString("N");
            internal CancellationTokenSource RawCancellation { get; } = new();
            internal Dictionary<CaptureDetailTarget, DesktopNavigationTarget> NavigationTargets { get; } = new();
            internal SemaphoreSlim NavigationGate { get; } = new(1, 1);

            private static AsyncOperationTracker StartReaders()
            {
                var readers = new AsyncOperationTracker();
                readers.StartAccepting();
                return readers;
            }
        }
        private sealed record Cursor(Guid Session, Guid Result, string Section, long Offset);
    }
}
