// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XMAT.ApplicationServices;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;

namespace XMAT.AgentAccess
{
    internal sealed class AgentQueryApplication(
        AgentRuntime runtime,
        ICaptureQueries queries,
        Func<bool> accepting,
        ICaptureFileExports fileExports = null)
    {
        private readonly object _gate = new();
        private readonly Dictionary<Guid, Entry> _results = new();
        private readonly Dictionary<AgentSession, int> _reserved = new();
        private readonly Dictionary<Guid, Artifact> _exports = new();
        private readonly Dictionary<AgentSession, int> _exportReserved = new();
        private readonly byte[] _cursorKey = RandomNumberGenerator.GetBytes(32);
        private static readonly AgentPermission SummaryRead =
            new(AgentCapability.SummaryRead, AgentScope.Application);

        internal async Task<JsonElement> ListSourcesAsync(AgentSession session, CancellationToken cancellationToken)
        {
            DemandAccepting();
            runtime.Demand(session, SummaryRead);
            var sources = await queries.GetSourcesAsync(cancellationToken);
            runtime.Demand(session, SummaryRead);
            return AgentJson.Encode(new { sources });
        }

        internal async Task<JsonElement> DescribeSchemaAsync(
            AgentSession session, Guid sourceId, string tableName, string cursor, CancellationToken cancellationToken)
        {
            DemandAccepting();
            runtime.Demand(session, SummaryRead);
            IReadOnlyList<QueryTable> schema;
            try
            { schema = await queries.GetSchemaAsync(sourceId, cancellationToken); }
            catch (QueryException exception) { throw new AgentAccessException(Map(exception.Error)); }
            var limits = runtime.GetSession(session).Limits;
            object rows;
            long total;
            var fingerprint = AgentOperationDescriptor.Hash(new { sourceId, tableName, schema });
            var offset = ReadOffset(session, sourceId, "schema", fingerprint, cursor);
            string next = null;
            if (tableName == null)
            {
                total = schema.Count;
                var tables = schema.Skip(checked((int)offset)).Take(limits.ResponseRows)
                    .Select(table => new { table.Name, table.Kind, columnCount = table.Columns.Count }).ToArray();
                rows = tables;
                if (offset + tables.Length < total)
                {
                    next = MakeCursor(new Cursor(session.Id, sourceId, "schema", fingerprint, offset + tables.Length));
                }
            }
            else
            {
                var table = schema.FirstOrDefault(item => item.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new AgentAccessException(AgentAccessError.OperationNotFound);
                total = table.Columns.Count;
                var columns = table.Columns.Skip(checked((int)offset)).Take(limits.ResponseRows).ToArray();
                rows = columns;
                if (offset + columns.Length < total)
                {
                    next = MakeCursor(new Cursor(session.Id, sourceId, "schema", fingerprint, offset + columns.Length));
                }
            }
            runtime.Demand(session, SummaryRead);
            DemandAccepting();
            return AgentJson.Encode(new
            {
                sourceId,
                tableName,
                offset,
                totalCount = total,
                rows,
                hasMore = next != null,
                nextCursor = next,
                samples = Samples(schema),
                notes = Localization.GetLocalizedString("AGENT_QUERY_SCHEMA_NOTES")
            });
        }

        internal async Task<Guid> RunAsync(
            AgentSession session,
            Guid sourceId,
            string sql,
            string idempotencyKey,
            int? timeoutSeconds,
            long? maximumResultBytes,
            CancellationToken cancellationToken)
        {
            DemandAccepting();
            var permission = Raw(sourceId);
            runtime.Demand(session, permission);
            ArgumentException.ThrowIfNullOrWhiteSpace(sql);
            var hash = AgentOperationDescriptor.Hash(new { sourceId, sql, timeoutSeconds, maximumResultBytes });
            var replay = runtime.FindReplay(session, idempotencyKey, "query.run", hash);
            if (replay.HasValue)
                return replay.Value;
            if (!(await queries.GetSourcesAsync(cancellationToken)).Any(source => source.Id == sourceId))
            {
                throw new AgentAccessException(AgentAccessError.SourceChanged);
            }
            cancellationToken.ThrowIfCancellationRequested();
            DemandAccepting();
            var descriptor = new AgentOperationDescriptor("query.run", [permission], AgentOperationEffects.None, hash);
            return runtime.Submit(session, descriptor, idempotencyKey, async context =>
            {
                DemandAccepting();
                runtime.Demand(session, permission);
                var limits = runtime.GetSession(session).Limits;
                var seconds = timeoutSeconds ?? limits.QueryTimeoutSeconds;
                var bytes = maximumResultBytes ?? limits.MaximumQueryResultBytes;
                if (seconds <= 0 || seconds > limits.QueryTimeoutSeconds ||
                    bytes < 16 * 1024 || bytes > limits.MaximumQueryResultBytes)
                {
                    throw new AgentAccessException(AgentAccessError.LimitExceeded);
                }
                Reserve(session, limits.MaximumQueryResults);
                IQueryResult result = null;
                var stored = false;
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
                    try
                    {
                        await using var snapshot = await queries.CreateSessionAsync(sourceId, deadline.Token);
                        result = await snapshot.ExecuteAsync(sql, new QueryExecutionOptions(TimeSpan.FromSeconds(seconds), bytes),
                            deadline.Token);
                        var entry = new Entry(session, sourceId, snapshot.CapturedAt, result);
                        lock (_gate)
                        { _results.Add(result.Id, entry); }
                        context.OwnResource(new AgentResourceKey(AgentResourceKind.QueryResult, result.Id),
                            sourceId.ToString("D"), () => RemoveAsync(entry));
                        stored = true;
                        return AgentJson.Encode(new
                        {
                            resultId = result.Id,
                            sourceId,
                            snapshotId = result.SnapshotId,
                            capturedAt = snapshot.CapturedAt,
                            result.RowCount,
                            columnCount = result.Columns.Count,
                            result.StorageBytes
                        });
                    }
                    catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
                    {
                        throw Failure(new QueryException(QueryError.Timeout, "The query exceeded its snapshot/execution deadline."));
                    }
                    catch (QueryException exception) { throw Failure(exception); }
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
            }, async token => (await queries.GetSourcesAsync(token)).Any(source => source.Id == sourceId));
        }

        internal async Task<JsonElement> ReadPageAsync(
            AgentSession session, Guid resultId, string cursor, CancellationToken cancellationToken)
        {
            DemandAccepting();
            var entry = GetEntry(session, resultId);
            var limits = runtime.GetSession(session).Limits;
            var offset = ReadOffset(session, resultId, "rows", resultId.ToString("N"), cursor);
            QueryPage page;
            try
            {
                page = await entry.Result.ReadPageAsync(offset, limits.ResponseRows,
                    Math.Max(1, limits.ResponseBytes - 1024), cancellationToken);
            }
            catch (QueryException exception) { throw Failure(exception); }
            runtime.Demand(session, Raw(entry.SourceId));
            DemandAccepting();
            return AgentJson.Encode(new
            {
                resultId,
                entry.SourceId,
                entry.Result.SnapshotId,
                entry.CapturedAt,
                page.Offset,
                page.TotalRows,
                page.Columns,
                page.Rows,
                page.HasMore,
                nextCursor = page.HasMore ? MakeCursor(new Cursor(session.Id, resultId, "rows",
                    resultId.ToString("N"), offset + page.Rows.Count)) : null
            });
        }

        internal async Task<JsonElement> ReadValueAsync(
            AgentSession session, Guid resultId, long row, int column, long byteOffset,
            int maximumBytes, CancellationToken cancellationToken)
        {
            DemandAccepting();
            var entry = GetEntry(session, resultId);
            var budget = Math.Min(64 * 1024, Math.Max(1, (runtime.GetSession(session).Limits.ResponseBytes - 1024) * 3 / 4));
            QueryValueChunk chunk;
            try
            {
                chunk = await entry.Result.ReadValueAsync(row, column, byteOffset,
                    Math.Min(maximumBytes, budget), cancellationToken);
            }
            catch (QueryException exception) { throw Failure(exception); }
            runtime.Demand(session, Raw(entry.SourceId));
            return AgentJson.Encode(new
            {
                resultId,
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

        internal async Task<JsonElement> ReleaseAsync(AgentSession session, Guid resultId)
        {
            DemandAccepting();
            GetEntry(session, resultId);
            var released = await runtime.ReleaseOwnedResourceAsync(session,
                new AgentResourceKey(AgentResourceKind.QueryResult, resultId));
            if (!released)
                throw new AgentAccessException(AgentAccessError.RecoveryRequired);
            return AgentJson.Encode(new { resultId, released });
        }

        internal Task<JsonElement> ExportFoldersAsync(AgentSession session, int offset)
        {
            DemandAccepting();
            var permitted = runtime.GetSession(session).Permissions
                .Where(permission => permission.Capability == AgentCapability.FileWrite)
                .Select(permission => permission.Scope.Id).ToHashSet();
            var all = fileExports?.GetFolders().Where(folder => permitted.Contains(folder.Id)).ToArray() ?? [];
            if (offset < 0 || offset > all.Length)
                throw new AgentAccessException(AgentAccessError.InvalidRequest);
            var folders = all.Skip(offset).Take(runtime.GetSession(session).Limits.ResponseRows).ToArray();
            foreach (var folder in folders)
                runtime.Demand(session, new AgentPermission(AgentCapability.FileWrite, AgentScope.ExportFolder(folder.Id)));
            DemandAccepting();
            return Task.FromResult(AgentJson.Encode(new
            {
                folders,
                totalCount = all.Length,
                nextOffset = offset + folders.Length < all.Length ? (int?)(offset + folders.Length) : null,
                overwriteSupported = false,
                nestedPathsSupported = false,
                networkPathsSupported = false
            }));
        }

        internal async Task<Guid> SaveFileAsync(
            AgentSession session, Guid resultId, Guid folderId, string fileName,
            QueryExportFormat format, string idempotencyKey, CancellationToken cancellationToken)
        {
            DemandAccepting();
            var folderPermission = new AgentPermission(AgentCapability.FileWrite, AgentScope.ExportFolder(folderId));
            runtime.Demand(session, folderPermission);
            var hash = AgentOperationDescriptor.Hash(new { resultId, folderId, fileName, format });
            var replay = runtime.FindReplay(session, idempotencyKey, "query.save_file", hash);
            if (replay.HasValue)
                return replay.Value;
            if (fileExports == null)
                throw new AgentAccessException(AgentAccessError.UnsupportedOperation);
            var entry = GetEntry(session, resultId);
            var export = new AgentPermission(AgentCapability.QueryResultExport, AgentScope.Source(entry.SourceId));
            var requirements = new[] { Raw(entry.SourceId), export, folderPermission };
            runtime.Demand(session, requirements);
            var destination = new QueryFileDestination(folderId, fileName, format);
            try
            { await fileExports.ValidateAsync(destination, cancellationToken); }
            catch (CaptureFileException exception) { throw new AgentAccessException(FileError(exception.Failure)); }
            var folder = fileExports.GetFolders().Single(item => item.Id == folderId);
            var descriptor = new AgentOperationDescriptor("query.save_file", requirements,
                AgentOperationEffects.SensitiveDataExport | AgentOperationEffects.StateChange, hash,
                Localization.GetLocalizedString("AGENT_QUERY_FILE_APPROVAL", folder.Path, fileName, format,
                    entry.Result.RowCount, entry.SourceId));
            cancellationToken.ThrowIfCancellationRequested();
            DemandAccepting();
            runtime.Demand(session, requirements);
            return runtime.Submit(session, descriptor, idempotencyKey, async context =>
            {
                DemandAccepting();
                runtime.Demand(session, requirements);
                var current = GetEntry(session, resultId);
                using var lease = Use(current);
                var limits = runtime.GetSession(session).Limits;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(limits.QueryTimeoutSeconds));
                lock (_gate)
                {
                    if (_exports.Values.Count(item => ReferenceEquals(item.Owner, session)) +
                        _exportReserved.GetValueOrDefault(session) >= limits.MaximumQueryResults)
                        throw new AgentAccessException(AgentAccessError.LimitExceeded);
                    _exportReserved[session] = _exportReserved.GetValueOrDefault(session) + 1;
                }
                try
                {
                    var receipt = await fileExports.SaveQueryAsync(current.Result, destination,
                        limits.MaximumQueryResultBytes, token =>
                        {
                            token.ThrowIfCancellationRequested();
                            DemandAccepting();
                            runtime.Demand(session, requirements);
                            return Task.CompletedTask;
                        }, deadline.Token);
                    return AgentJson.Encode(receipt);
                }
                catch (CaptureFileException exception) { throw new AgentAccessException(FileError(exception.Failure)); }
                catch (QueryException exception) { throw Failure(exception); }
                catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
                { throw new AgentAccessException(AgentAccessError.QueryTimeout); }
                finally
                {
                    lock (_gate)
                    {
                        if (--_exportReserved[session] == 0)
                            _exportReserved.Remove(session);
                    }
                }
            }, async token =>
            {
                lock (_gate)
                {
                    if (!_results.TryGetValue(resultId, out var found) || !ReferenceEquals(entry, found) || entry.Releasing)
                        return false;
                }
                try
                {
                    await fileExports.ValidateAsync(destination, token);
                    return true;
                }
                catch (CaptureFileException) { return false; }
            });
        }

        private static AgentAccessError FileError(CaptureFileFailure failure) => failure switch
        {
            CaptureFileFailure.FolderChanged => AgentAccessError.SourceChanged,
            CaptureFileFailure.DestinationExists => AgentAccessError.DestinationExists,
            CaptureFileFailure.InvalidPath or CaptureFileFailure.ProtectedSource => AgentAccessError.InvalidPath,
            _ => AgentAccessError.OperationUnavailable
        };

        internal Task<Guid> ExportAsync(
            AgentSession session, Guid resultId, QueryExportFormat format, string idempotencyKey)
        {
            DemandAccepting();
            var hash = AgentOperationDescriptor.Hash(new { resultId, format });
            var replay = runtime.FindReplay(session, idempotencyKey, "query.export", hash);
            if (replay.HasValue)
                return Task.FromResult(replay.Value);
            var entry = GetEntry(session, resultId);
            var exportPermission = new AgentPermission(AgentCapability.QueryResultExport, AgentScope.Source(entry.SourceId));
            runtime.Demand(session, exportPermission);
            if (!Enum.IsDefined(format))
                throw new ArgumentOutOfRangeException(nameof(format));
            var descriptor = new AgentOperationDescriptor("query.export", [Raw(entry.SourceId), exportPermission],
                AgentOperationEffects.SensitiveDataExport, hash,
                Localization.GetLocalizedString("AGENT_QUERY_EXPORT_APPROVAL", entry.SourceId, format,
                    entry.Result.RowCount, entry.Result.Columns.Count));
            var id = runtime.Submit(session, descriptor, idempotencyKey, async context =>
            {
                var current = GetEntry(session, resultId);
                var limits = runtime.GetSession(session).Limits;
                lock (_gate)
                {
                    if (_exports.Values.Count(item => ReferenceEquals(item.Owner, session)) +
                        _exportReserved.GetValueOrDefault(session) >= limits.MaximumQueryResults)
                    {
                        throw new AgentAccessException(AgentAccessError.LimitExceeded);
                    }
                    _exportReserved[session] = _exportReserved.GetValueOrDefault(session) + 1;
                }
                string directory = null;
                Artifact created = null;
                var stored = false;
                try
                {
                    using var lease = Use(current);
                    directory = Path.Combine(Path.GetTempPath(), $"XMAT.QueryExport.{Guid.NewGuid():N}");
                    Directory.CreateDirectory(directory);
                    var path = Path.Combine(directory, format == QueryExportFormat.Csv ? "result.csv" : "result.json");
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(limits.QueryTimeoutSeconds));
                    try
                    {
                        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                            4096, FileOptions.Asynchronous))
                        {
                            await QueryResultExporter.WriteAsync(current.Result, file, format,
                                limits.MaximumQueryResultBytes, deadline.Token);
                            await file.FlushAsync(deadline.Token);
                        }
                    }
                    catch (QueryException exception) { throw Failure(exception); }
                    catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
                    {
                        throw Failure(new QueryException(QueryError.Timeout, "The export exceeded its execution deadline."));
                    }
                    var artifact = new Artifact(session, current.SourceId, directory, path, format);
                    created = artifact;
                    lock (_gate)
                    { _exports.Add(artifact.Id, artifact); }
                    context.OwnResource(new AgentResourceKey(AgentResourceKind.Artifact, artifact.Id),
                        current.SourceId.ToString("D"), () => RemoveExportAsync(artifact));
                    stored = true;
                    return AgentJson.Encode(new
                    {
                        artifactId = artifact.Id,
                        sourceId = current.SourceId,
                        format,
                        artifact.Length,
                        rowCount = current.Result.RowCount,
                        complete = true
                    });
                }
                finally
                {
                    lock (_gate)
                    {
                        if (--_exportReserved[session] == 0)
                            _exportReserved.Remove(session);
                    }
                    if (!stored && directory != null && Directory.Exists(directory))
                    {
                        if (created != null)
                        {
                            lock (_gate)
                            { _exports.Remove(created.Id); }
                        }
                        Directory.Delete(directory, recursive: true);
                    }
                }
            }, _ =>
            {
                lock (_gate)
                {
                    return Task.FromResult(_results.TryGetValue(resultId, out var found) &&
                        ReferenceEquals(found, entry) && !entry.Releasing);
                }
            });
            return Task.FromResult(id);
        }

        internal async Task<JsonElement> ReadExportAsync(
            AgentSession session, Guid artifactId, long byteOffset, int maximumBytes, CancellationToken cancellationToken)
        {
            DemandAccepting();
            var artifact = GetExport(session, artifactId);
            ArgumentOutOfRangeException.ThrowIfNegative(byteOffset);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(byteOffset, artifact.Length);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
            IDisposable lease;
            lock (_gate)
            {
                if (artifact.Releasing || !artifact.Readers.TryBegin(out lease))
                    throw new AgentAccessException(AgentAccessError.OperationUnavailable);
            }
            using (lease)
            {
                var budget = Math.Min(64 * 1024, Math.Max(1, (runtime.GetSession(session).Limits.ResponseBytes - 1024) * 3 / 4));
                var bytes = new byte[(int)Math.Min(Math.Min(maximumBytes, budget), artifact.Length - byteOffset)];
                await using var file = new FileStream(artifact.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    4096, FileOptions.Asynchronous);
                file.Position = byteOffset;
                await file.ReadExactlyAsync(bytes, cancellationToken);
                runtime.Demand(session, Raw(artifact.SourceId),
                    new AgentPermission(AgentCapability.QueryResultExport, AgentScope.Source(artifact.SourceId)));
                return AgentJson.Encode(new
                {
                    artifactId,
                    byteOffset,
                    totalBytes = artifact.Length,
                    bytes,
                    artifact.Format,
                    hasMore = byteOffset + bytes.Length < artifact.Length
                });
            }
        }

        internal async Task<JsonElement> ReleaseExportAsync(AgentSession session, Guid artifactId)
        {
            DemandAccepting();
            GetExport(session, artifactId);
            if (!await runtime.ReleaseOwnedResourceAsync(session, new AgentResourceKey(AgentResourceKind.Artifact, artifactId)))
            {
                throw new AgentAccessException(AgentAccessError.RecoveryRequired);
            }
            return AgentJson.Encode(new { artifactId, released = true });
        }

        internal async Task DisposeResultsAsync()
        {
            Entry[] entries;
            Artifact[] exports;
            lock (_gate)
            {
                entries = _results.Values.ToArray();
                exports = _exports.Values.ToArray();
            }
            foreach (var entry in entries)
                await RemoveAsync(entry);
            foreach (var artifact in exports)
                await RemoveExportAsync(artifact);
        }

        private void Reserve(AgentSession session, int maximum)
        {
            lock (_gate)
            {
                var pending = _reserved.GetValueOrDefault(session);
                if (_results.Values.Count(entry => ReferenceEquals(entry.Owner, session)) + pending >= maximum)
                {
                    throw new AgentAccessException(AgentAccessError.LimitExceeded);
                }
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
            runtime.Demand(session, Raw(entry.SourceId));
            return entry;
        }

        private async Task<AgentCleanupOutcome> RemoveAsync(Entry entry)
        {
            lock (_gate)
            { entry.Releasing = true; }
            await entry.Readers.StopAcceptingAndWaitAsync();
            await entry.Result.DisposeAsync();
            lock (_gate)
            { _results.Remove(entry.Result.Id); }
            return AgentCleanupOutcome.Released;
        }

        private Artifact GetExport(AgentSession session, Guid id)
        {
            Artifact artifact;
            lock (_gate)
            {
                artifact = _exports.TryGetValue(id, out var found) && ReferenceEquals(found.Owner, session) && !found.Releasing
                    ? found : throw new AgentAccessException(AgentAccessError.OperationNotFound);
            }
            runtime.Demand(session, Raw(artifact.SourceId),
                new AgentPermission(AgentCapability.QueryResultExport, AgentScope.Source(artifact.SourceId)));
            return artifact;
        }

        private async Task<AgentCleanupOutcome> RemoveExportAsync(Artifact artifact)
        {
            lock (_gate)
            { artifact.Releasing = true; }
            await artifact.Readers.StopAcceptingAndWaitAsync();
            if (Directory.Exists(artifact.Directory))
                Directory.Delete(artifact.Directory, recursive: true);
            lock (_gate)
            { _exports.Remove(artifact.Id); }
            return AgentCleanupOutcome.Released;
        }

        private IDisposable Use(Entry entry)
        {
            lock (_gate)
            {
                if (entry.Releasing || !entry.Readers.TryBegin(out var lease))
                    throw new AgentAccessException(AgentAccessError.OperationUnavailable);
                return lease;
            }
        }

        private void DemandAccepting()
        {
            if (!accepting())
                throw new AgentAccessException(AgentAccessError.OperationUnavailable);
        }

        private static AgentPermission Raw(Guid sourceId) => new(AgentCapability.RawDataRead, AgentScope.Source(sourceId));

        private static AgentAccessError Map(QueryError error) => error switch
        {
            QueryError.SourceBusy => AgentAccessError.OperationUnavailable,
            QueryError.SourceChanged => AgentAccessError.SourceChanged,
            QueryError.ResultLimit or QueryError.PageTooSmall => AgentAccessError.LimitExceeded,
            QueryError.Timeout => AgentAccessError.QueryTimeout,
            _ => AgentAccessError.QueryFailed
        };

        private static AgentAccessException Failure(QueryException exception) =>
            new(Map(exception.Error), AgentJson.Encode(new
            {
                error = Map(exception.Error),
                queryError = exception.Error,
                message = exception.Message
            }));

        private static object[] Samples(IReadOnlyList<QueryTable> schema)
        {
            var table = schema.FirstOrDefault(item => item.Name == WebServiceCaptureMethod.TableName);
            if (table == null || !new[] { "id", "RequestHost", "RequestStatus" }
                .All(name => table.Columns.Any(column => column.Name == name)))
            {
                return Array.Empty<object>();
            }
            return
            [
                new { name = Localization.GetLocalizedString("AGENT_SAMPLE_LATEST"), sql = "SELECT * FROM \"WebProxyConnections\" ORDER BY \"id\" DESC LIMIT 100;" },
                new { name = Localization.GetLocalizedString("AGENT_SAMPLE_HOSTS"), sql = "SELECT \"RequestHost\",COUNT(*) AS \"CallCount\" FROM \"WebProxyConnections\" GROUP BY \"RequestHost\" ORDER BY \"CallCount\" DESC;" },
                new { name = Localization.GetLocalizedString("AGENT_SAMPLE_THROTTLED"), sql = "SELECT * FROM \"WebProxyConnections\" WHERE CAST(\"RequestStatus\" AS INTEGER)=429 ORDER BY \"id\";" }
            ];
        }

        private string MakeCursor(Cursor cursor)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(cursor);
            return Convert.ToBase64String(bytes.Concat(HMACSHA256.HashData(_cursorKey, bytes)).ToArray());
        }

        private long ReadOffset(AgentSession session, Guid id, string kind, string fingerprint, string value)
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
                {
                    throw new FormatException();
                }
                var cursor = JsonSerializer.Deserialize<Cursor>(bytes.AsSpan(0, bytes.Length - 32));
                if (cursor == null || cursor.Session != session.Id || cursor.Resource != id || cursor.Kind != kind ||
                    cursor.Offset < 0)
                {
                    throw new FormatException();
                }
                if (cursor.Fingerprint != fingerprint)
                    throw new AgentAccessException(AgentAccessError.SourceChanged);
                return cursor.Offset;
            }
            catch (Exception exception) when (exception is FormatException or JsonException)
            {
                throw new AgentAccessException(AgentAccessError.OperationNotFound);
            }
        }

        private sealed record Cursor(Guid Session, Guid Resource, string Kind, string Fingerprint, long Offset);
        private sealed class Entry(AgentSession owner, Guid sourceId, DateTimeOffset capturedAt, IQueryResult result)
        {
            internal AgentSession Owner { get; } = owner;
            internal Guid SourceId { get; } = sourceId;
            internal DateTimeOffset CapturedAt { get; } = capturedAt;
            internal IQueryResult Result { get; } = result;
            internal AsyncOperationTracker Readers { get; } = StartReaders();
            internal bool Releasing { get; set; }
        }

        private sealed class Artifact(AgentSession owner, Guid sourceId, string directory, string path, QueryExportFormat format)
        {
            internal Guid Id { get; } = Guid.NewGuid();
            internal AgentSession Owner { get; } = owner;
            internal Guid SourceId { get; } = sourceId;
            internal string Directory { get; } = directory;
            internal string Path { get; } = path;
            internal QueryExportFormat Format { get; } = format;
            internal long Length { get; } = new FileInfo(path).Length;
            internal AsyncOperationTracker Readers { get; } = StartReaders();
            internal bool Releasing { get; set; }
        }

        private static AsyncOperationTracker StartReaders()
        {
            var readers = new AsyncOperationTracker();
            readers.StartAccepting();
            return readers;
        }
    }
}
