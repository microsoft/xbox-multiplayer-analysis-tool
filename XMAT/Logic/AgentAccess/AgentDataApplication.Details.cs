// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XMAT.SharedInterfaces;

namespace XMAT.AgentAccess
{
    internal sealed partial class AgentDataApplication
    {
        internal async Task<JsonElement> ListWebSocketsAsync(
            AgentSession session, Guid sourceId, string cursor, CancellationToken cancellationToken)
        {
            DemandAccepting();
            Runtime.Demand(session, Summary(sourceId));
            try
            {
                var captures = await Investigation.GetWebSocketsAsync(sourceId, cancellationToken);
                Runtime.Demand(session, Summary(sourceId));
                var key = "websockets:" + AgentOperationDescriptor.Hash(captures);
                var offset = ReadOffset(session, sourceId, key, cursor);
                var result = new CaptureDataResult(sourceId, sourceId, Guid.Empty, DateTimeOffset.UtcNow, CaptureDataKind.WebSocket,
                    false, 0, 0, 0, 0, null, null, [], [], [], null)
                { SnapshotKind = "Availability" };
                return Page(session, result, captures, offset, key, false);
            }
            catch (CaptureDataException exception) { throw DetailFailure(exception); }
        }

        internal async Task<JsonElement> ReadDetailsAsync(
            AgentSession session, Guid resultId, CaptureDetailKind kind, long itemId, string cursor,
            CancellationToken cancellationToken)
        {
            DemandAccepting();
            var entry = GetEntry(session, resultId);
            await EnsureRawAsync(session, entry);
            using var lease = Use(entry, true);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, entry.RawCancellation.Token);
            var limits = Runtime.GetSession(session).Limits;
            var key = $"detail:{kind}:{itemId}";
            var offset = ReadOffset(session, resultId, key, cursor);
            QueryPage page;
            try
            {
                page = await entry.Result.Details.ReadPageAsync(new(kind, itemId), offset, limits.ResponseRows,
                    Math.Max(1, limits.ResponseBytes - 1024), Options(limits), cancellation.Token);
            }
            catch (Exception exception) when (exception is CaptureDataException or QueryException or ObjectDisposedException or ArgumentException)
            { throw DetailFailure(exception); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new AgentAccessException(AgentAccessError.QueryTimeout); }
            Runtime.Demand(session, Summary(entry.SourceId), Raw(entry.SourceId));
            DemandAccepting();
            return AgentJson.Encode(new
            {
                resultId,
                entry.SourceId,
                entry.Result.SnapshotId,
                entry.Result.CapturedAt,
                entry.Result.CaptureId,
                entry.Result.CaptureEpoch,
                entry.Result.SnapshotKind,
                kind,
                itemId,
                page.Offset,
                page.TotalRows,
                page.Columns,
                page.Rows,
                page.HasMore,
                nextCursor = page.HasMore ? MakeCursor(new Cursor(session.Id, resultId, key, offset + page.Rows.Count)) : null
            });
        }

        internal async Task<JsonElement> ReadDetailValueAsync(
            AgentSession session, Guid resultId, CaptureDetailKind kind, long itemId, long row, int column,
            long byteOffset, int maximumBytes, CancellationToken cancellationToken)
        {
            DemandAccepting();
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
            var entry = GetEntry(session, resultId);
            await EnsureRawAsync(session, entry);
            using var lease = Use(entry, true);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, entry.RawCancellation.Token);
            var limits = Runtime.GetSession(session).Limits;
            var budget = Math.Min(64 * 1024, Math.Max(1, (limits.ResponseBytes - 1024) * 3 / 4));
            QueryValueChunk chunk;
            try
            {
                chunk = await entry.Result.Details.ReadValueAsync(new(kind, itemId), row, column, byteOffset,
                    Math.Min(maximumBytes, budget), Options(limits), cancellation.Token);
            }
            catch (Exception exception) when (exception is CaptureDataException or QueryException or ObjectDisposedException or ArgumentException)
            { throw DetailFailure(exception); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new AgentAccessException(AgentAccessError.QueryTimeout); }
            Runtime.Demand(session, Summary(entry.SourceId), Raw(entry.SourceId));
            DemandAccepting();
            return AgentJson.Encode(new
            {
                resultId,
                entry.SourceId,
                entry.Result.SnapshotId,
                entry.Result.CapturedAt,
                entry.Result.CaptureId,
                entry.Result.CaptureEpoch,
                entry.Result.SnapshotKind,
                detailKind = kind,
                itemId,
                row,
                column,
                chunk.Kind,
                chunk.ByteOffset,
                chunk.TotalBytes,
                chunk.Bytes,
                chunk.HasMore,
                encoding = chunk.Kind == QueryValueKind.Text ? "utf-8" : "binary"
            });
        }

        private async Task EnsureRawAsync(AgentSession session, Entry entry)
        {
            Runtime.Demand(session, Summary(entry.SourceId), Raw(entry.SourceId));
            if (entry.Result.Details == null)
                throw new AgentAccessException(AgentAccessError.UnsupportedOperation);
            Task binding;
            lock (_gate)
            {
                if (entry.Releasing || entry.RawInvalidated)
                    throw new AgentAccessException(AgentAccessError.OperationNotFound);
                binding = entry.RawBinding ??= BindRawAsync(session, entry);
            }
            await binding;
            Runtime.Demand(session, Summary(entry.SourceId), Raw(entry.SourceId));
        }

        private async Task BindRawAsync(AgentSession session, Entry entry)
        {
            var requirements = new[] { Summary(entry.SourceId), Raw(entry.SourceId) };
            var descriptor = new AgentOperationDescriptor("data.bind_details", requirements, AgentOperationEffects.None,
                AgentOperationDescriptor.Hash(new { resultId = entry.Result.Id, sourceId = entry.SourceId }));
            var operation = Runtime.Submit(session, descriptor, entry.RawBindingKey, context =>
            {
                var id = Guid.NewGuid();
                context.OwnResource(new AgentResourceKey(AgentResourceKind.CaptureData, id), entry.SourceId.ToString("D"),
                    async () =>
                    {
                        lock (_gate)
                        { entry.RawInvalidated = true; }
                        await CancelRevokedReadersAsync(entry);
                        await entry.RawReaders.StopAcceptingAndWaitAsync();
                        await entry.Result.DisposeAsync();
                        lock (_gate)
                        { entry.RawResource = null; }
                        return AgentCleanupOutcome.Released;
                    });
                lock (_gate)
                { entry.RawResource = id; }
                return Task.FromResult(AgentJson.Encode(new { bound = true }));
            }, _ =>
            {
                lock (_gate)
                {
                    return Task.FromResult(_results.TryGetValue(entry.Result.Id, out var found) &&
                        ReferenceEquals(found, entry) && !entry.Releasing && !entry.RawInvalidated);
                }
            });
            await Runtime.WaitForCompletionAsync(session, operation);
            var completed = Runtime.GetOperation(session, operation);
            if (completed.State != AgentOperationState.Succeeded)
                throw new AgentAccessException(completed.Error ?? AgentAccessError.OperationUnavailable);
        }

        private static QueryExecutionOptions Options(AgentLimits limits) =>
            new(TimeSpan.FromSeconds(limits.QueryTimeoutSeconds), limits.MaximumQueryResultBytes);
    }
}
