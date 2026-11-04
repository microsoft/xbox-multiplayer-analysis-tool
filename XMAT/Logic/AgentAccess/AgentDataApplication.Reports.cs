// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XMAT.ApplicationServices;
using XMAT.SharedInterfaces;

namespace XMAT.AgentAccess
{
    internal sealed partial class AgentDataApplication
    {
        private readonly Dictionary<AgentSession, int> _reportExports = new();

        internal async Task<JsonElement> ListReportsAsync(AgentSession session, string cursor, CancellationToken cancellationToken)
        {
            DemandAccepting();
            Runtime.Demand(session);
            var permissions = Runtime.GetSession(session).Permissions.ToHashSet();
            var reports = (await Investigation.GetCompletedReportsAsync(cancellationToken))
                .Where(report => permissions.Contains(Summary(report.SourceId)))
                .Select(report => new ReportListing(report.Id, report.SourceId, report.SnapshotId, report.Kind,
                    report.CapturedAt, report.CompletedAt, report.CaptureId, report.CaptureEpoch, report.DesktopRunId,
                    report.Documents.Count, null)).ToList();
            lock (_gate)
            {
                reports.AddRange(_results.Values.Where(entry => ReferenceEquals(entry.Owner, session) &&
                    !entry.Releasing && entry.Result.Report != null && permissions.Contains(Summary(entry.SourceId)))
                    .Select(entry =>
                    {
                        var report = entry.Result.Report;
                        return new ReportListing(report.Id, report.SourceId, report.SnapshotId, report.Kind,
                            report.CapturedAt, report.CompletedAt, report.CaptureId, report.CaptureEpoch, report.DesktopRunId,
                            report.Documents.Count, entry.Result.Id);
                    }));
            }
            var rows = reports.GroupBy(report => report.ReportId)
                .Select(group => group.FirstOrDefault(report => report.ResultId.HasValue) ?? group.First())
                .OrderBy(report => report.ReportId).ToArray();
            var key = "reports:" + AgentOperationDescriptor.Hash(rows);
            var offset = ReadOffset(session, Guid.Empty, key, cursor);
            if (offset > rows.Length)
                throw new AgentAccessException(AgentAccessError.OperationNotFound);
            var limits = Runtime.GetSession(session).Limits;
            var page = new List<ReportListing>();
            long used = 1024;
            for (var index = offset; index < rows.Length && page.Count < limits.ResponseRows; index++)
            {
                var row = rows[index];
                var size = JsonSerializer.SerializeToUtf8Bytes(row, AgentJson.Options).LongLength;
                if (used + size > limits.ResponseBytes)
                {
                    if (page.Count == 0)
                        throw new AgentAccessException(AgentAccessError.LimitExceeded);
                    break;
                }
                page.Add(row);
                used += size;
            }
            foreach (var report in page)
                Runtime.Demand(session, Summary(report.SourceId));
            Runtime.Demand(session);
            DemandAccepting();
            return AgentJson.Encode(new
            {
                reports = page,
                totalCount = rows.Length,
                offset,
                hasMore = offset + page.Count < rows.Length,
                nextCursor = offset + page.Count < rows.Length
                    ? MakeCursor(new Cursor(session.Id, Guid.Empty, key, offset + page.Count)) : null
            });
        }

        internal async Task<Guid> OpenReportAsync(
            AgentSession session, Guid reportId, string idempotencyKey, CancellationToken cancellationToken)
        {
            DemandAccepting();
            var hash = AgentOperationDescriptor.Hash(new { reportId });
            var replay = Runtime.FindReplay(session, idempotencyKey, "data.open_report", hash);
            if (replay.HasValue)
                return replay.Value;
            var report = (await Investigation.GetCompletedReportsAsync(cancellationToken))
                .SingleOrDefault(report => report.Id == reportId)
                ?? throw new AgentAccessException(AgentAccessError.OperationNotFound);
            var permission = Summary(report.SourceId);
            Runtime.Demand(session, permission);
            var descriptor = new AgentOperationDescriptor("data.open_report", [permission], AgentOperationEffects.None, hash);
            return Runtime.Submit(session, descriptor, idempotencyKey, async context =>
            {
                var limits = Runtime.GetSession(session).Limits;
                Reserve(session, limits.MaximumQueryResults);
                CaptureDataResult result = null;
                var stored = false;
                try
                {
                    result = await Investigation.OpenCompletedReportAsync(reportId, context.CancellationToken);
                    ValidateResult(result, report.SourceId, report.Kind, true);
                    if (result.Details?.RetainedBytes > limits.MaximumQueryResultBytes)
                        throw new AgentAccessException(AgentAccessError.LimitExceeded);
                    if (result.Report?.Id != reportId || result.SourceId != report.SourceId ||
                        result.SnapshotId != report.SnapshotId)
                        throw new AgentAccessException(AgentAccessError.SourceChanged);
                    Runtime.Demand(session, permission);
                    var entry = new Entry(session, result.SourceId, result);
                    lock (_gate)
                    { _results.Add(result.Id, entry); }
                    context.OwnResource(new AgentResourceKey(AgentResourceKind.CaptureData, result.Id),
                        result.SourceId.ToString("D"), () => RemoveAsync(entry));
                    stored = true;
                    return AgentJson.Encode(new
                    {
                        resultId = result.Id,
                        reportId,
                        result.SourceId,
                        result.SnapshotId,
                        result.CapturedAt,
                        result.Kind,
                        result.CaptureId,
                        result.CaptureEpoch,
                        report.CompletedAt,
                        recordCount = result.Records.Count,
                        findingCount = result.Findings.Count,
                        reportDocumentCount = report.Documents.Count
                    });
                }
                catch (CaptureDataException exception) { throw DetailFailure(exception); }
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
            }, async token => (await Investigation.GetCompletedReportsAsync(token))
                .Any(current => current.Id == reportId && current.SourceId == report.SourceId && current.SnapshotId == report.SnapshotId));
        }

        internal async Task<JsonElement> ReadReportManifestAsync(
            AgentSession session, Guid resultId, string cursor, CancellationToken cancellationToken)
        {
            DemandAccepting();
            var entry = GetEntry(session, resultId);
            await EnsureRawAsync(session, entry);
            using var lease = Use(entry, true);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, entry.RawCancellation.Token);
            cancellation.CancelAfter(TimeSpan.FromSeconds(Runtime.GetSession(session).Limits.QueryTimeoutSeconds));
            try
            {
                var manifest = await entry.Result.Details.GetReportManifestAsync(
                    Runtime.GetSession(session).Limits.MaximumQueryResultBytes, cancellation.Token);
                var key = "manifest:" + AgentOperationDescriptor.Hash(manifest);
                var offset = ReadOffset(session, resultId, key, cursor);
                Runtime.Demand(session, Summary(entry.SourceId), Raw(entry.SourceId));
                return Page(session, entry.Result, manifest, offset, key);
            }
            catch (Exception exception) when (exception is CaptureDataException or QueryException or ObjectDisposedException)
            { throw DetailFailure(exception); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new AgentAccessException(AgentAccessError.QueryTimeout); }
        }

        internal async Task<Guid> ExportReportAsync(
            AgentSession session, Guid resultId, Guid folderId, string directoryName, string idempotencyKey,
            CancellationToken cancellationToken)
        {
            DemandAccepting();
            var folderPermission = new AgentPermission(AgentCapability.FileWrite, AgentScope.ExportFolder(folderId));
            Runtime.Demand(session, folderPermission);
            var hash = AgentOperationDescriptor.Hash(new { resultId, folderId, directoryName });
            var replay = Runtime.FindReplay(session, idempotencyKey, "data.export_report", hash);
            if (replay.HasValue)
                return replay.Value;
            if (FileExports == null)
                throw new AgentAccessException(AgentAccessError.UnsupportedOperation);
            var entry = GetEntry(session, resultId);
            var report = entry.Result.Report ?? throw new AgentAccessException(AgentAccessError.UnsupportedOperation);
            var requirements = new[] { Summary(entry.SourceId), Raw(entry.SourceId), ReportExport(entry.SourceId), folderPermission };
            Runtime.Demand(session, requirements);
            await EnsureRawAsync(session, entry);
            var destination = new CaptureReportDestination(folderId, directoryName);
            try
            { await FileExports.ValidateReportAsync(destination, cancellationToken); }
            catch (CaptureFileException exception) { throw new AgentAccessException(FileError(exception.Failure)); }
            IReadOnlyList<CaptureReportArtifact> manifest;
            using (Use(entry, true))
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, entry.RawCancellation.Token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(Runtime.GetSession(session).Limits.QueryTimeoutSeconds));
                try
                {
                    manifest = await entry.Result.Details.GetReportManifestAsync(
                        Runtime.GetSession(session).Limits.MaximumQueryResultBytes, deadline.Token);
                }
                catch (CaptureDataException exception) { throw DetailFailure(exception); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { throw new AgentAccessException(AgentAccessError.QueryTimeout); }
            }
            var manifestHash = AgentOperationDescriptor.Hash(manifest);
            var folder = FileExports.GetFolders().Single(item => item.Id == folderId);
            var totalBytes = manifest.Sum(artifact => artifact.Length);
            var details = Localization.GetLocalizedString("DATA_REPORT_EXPORT_APPROVAL",
                report.Id, report.SourceId, report.SnapshotId, report.CompletedAt, folder.Path, directoryName,
                manifest.Count, totalBytes, manifestHash) + Environment.NewLine +
                Localization.GetLocalizedString("DATA_REPORT_EXPORT_SCOPE", report.Scope, report.MatchedRecords, report.ScannedRecords,
                    JsonSerializer.Serialize(report.Filter, AgentJson.Options)) + Environment.NewLine +
                string.Join(Environment.NewLine, manifest.Select(artifact => $"{artifact.RelativePath} ({artifact.Length} bytes, SHA-256 {artifact.Sha256})"));
            if (details.Length > 256 * 1024)
                throw new AgentAccessException(AgentAccessError.LimitExceeded);
            var descriptor = new AgentOperationDescriptor("data.export_report", requirements,
                AgentOperationEffects.StateChange | AgentOperationEffects.SensitiveDataExport, hash, details);
            Runtime.Demand(session, requirements);
            DemandAccepting();
            return Runtime.Submit(session, descriptor, idempotencyKey, async context =>
            {
                var current = GetEntry(session, resultId);
                using var lease = Use(current, true);
                Runtime.Demand(session, requirements);
                var limits = Runtime.GetSession(session).Limits;
                lock (_gate)
                {
                    if (_reportExports.GetValueOrDefault(session) >= limits.MaximumQueryResults)
                        throw new AgentAccessException(AgentAccessError.LimitExceeded);
                    _reportExports[session] = _reportExports.GetValueOrDefault(session) + 1;
                }
                ICaptureReportFiles files = null;
                CaptureReportExportReceipt receipt = null;
                var cleanupRequired = false;
                Exception failure = null;
                try
                {
                    try
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, current.RawCancellation.Token);
                        deadline.CancelAfter(TimeSpan.FromSeconds(limits.QueryTimeoutSeconds));
                        files = await current.Result.Details.CreateReportFilesAsync(limits.MaximumQueryResultBytes, deadline.Token);
                        if (AgentOperationDescriptor.Hash(files.Manifest) != manifestHash ||
                            files.Report.Id != report.Id || files.Report.SourceId != report.SourceId || files.Report.SnapshotId != report.SnapshotId)
                            throw new AgentAccessException(AgentAccessError.SourceChanged);
                        receipt = await FileExports.SaveReportAsync(files, destination, limits.MaximumQueryResultBytes, token =>
                        {
                            token.ThrowIfCancellationRequested();
                            Runtime.Demand(session, requirements);
                            DemandAccepting();
                            return Task.CompletedTask;
                        }, deadline.Token);
                    }
                    catch (CaptureFileException exception) { throw new AgentAccessException(FileError(exception.Failure)); }
                    catch (CaptureDataException exception) { throw DetailFailure(exception); }
                    catch (QueryException exception) { throw DetailFailure(exception); }
                    catch (CaptureAnalysisReport.CaptureReportArtifactsException exception)
                    {
                        files = exception.Files;
                        if (exception.Original is CaptureDataException or QueryException)
                            throw DetailFailure(exception.Original);
                        if (exception.Original is OperationCanceledException)
                        {
                            context.CancellationToken.ThrowIfCancellationRequested();
                            throw new AgentAccessException(AgentAccessError.QueryTimeout);
                        }
                        throw new AgentAccessException(AgentAccessError.ExecutionFailed);
                    }
                    catch (AnalysisReportExportException exception)
                    {
                        if (exception.OwnedResidue != null)
                        {
                            var residue = exception.OwnedResidue;
                            context.OwnResource(new AgentResourceKey(AgentResourceKind.Artifact, Guid.NewGuid()), report.SourceId.ToString("D"),
                                () =>
                                {
                                    try
                                    {
                                        residue.DeleteUnpublished();
                                        residue.Dispose();
                                        return Task.FromResult(AgentCleanupOutcome.Released);
                                    }
                                    catch (Exception cleanupException) when (!ExceptionUtilities.IsFatal(cleanupException))
                                    { return Task.FromResult(AgentCleanupOutcome.RecoveryRequired); }
                                });
                        }
                        throw new AgentAccessException(exception.ResiduePath != null ? AgentAccessError.RecoveryRequired : AgentAccessError.ExecutionFailed,
                            AgentJson.Encode(new
                            {
                                reportId = report.Id,
                                report.SourceId,
                                report.SnapshotId,
                                folderId,
                                directoryName,
                                published = exception.PublicationState == AnalysisReportPublicationState.Unknown ? (bool?)null : false,
                                publicationState = exception.PublicationState,
                                cleanupRequired = exception.ResiduePath != null,
                                residueDirectory = exception.ResiduePath == null ? null : Path.GetFileName(exception.ResiduePath)
                            }));
                    }
                    catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
                    { throw new AgentAccessException(AgentAccessError.QueryTimeout); }
                }
                catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception)) { failure = exception; }
                finally
                {
                    try
                    {
                        if (files != null)
                        {
                            try
                            { await files.DisposeAsync(); }
                            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
                            {
                                cleanupRequired = true;
                                var retainedFiles = files;
                                context.OwnResource(new AgentResourceKey(AgentResourceKind.Artifact, Guid.NewGuid()), report.SourceId.ToString("D"),
                                    async () =>
                                    {
                                        try
                                        { await retainedFiles.DisposeAsync(); return AgentCleanupOutcome.Released; }
                                        catch (IOException) { return AgentCleanupOutcome.RecoveryRequired; }
                                        catch (UnauthorizedAccessException) { return AgentCleanupOutcome.RecoveryRequired; }
                                    });
                            }
                        }
                    }
                    finally
                    {
                        lock (_gate)
                        {
                            if (--_reportExports[session] == 0)
                                _reportExports.Remove(session);
                        }
                    }
                }
                if (cleanupRequired && receipt == null)
                {
                    if (failure is AgentAccessException { ProtectedResult: JsonElement previous } original &&
                        previous.ValueKind == JsonValueKind.Object)
                    {
                        var values = previous.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
                        values["cleanupRequired"] = JsonSerializer.SerializeToElement(true);
                        throw new AgentAccessException(original.Error, AgentJson.Encode(values));
                    }
                    throw new AgentAccessException(AgentAccessError.RecoveryRequired, AgentJson.Encode(new
                    {
                        reportId = report.Id,
                        report.SourceId,
                        report.SnapshotId,
                        folderId,
                        directoryName,
                        published = false,
                        cleanupRequired = true
                    }));
                }
                if (failure != null)
                    ExceptionDispatchInfo.Capture(failure).Throw();
                return AgentJson.Encode(new
                {
                    receipt.ReportId,
                    receipt.SourceId,
                    receipt.SnapshotId,
                    receipt.FolderId,
                    receipt.DirectoryName,
                    receipt.Length,
                    receipt.FileCount,
                    receipt.Published,
                    cleanupRequired,
                    cancellationRequested = context.CancellationToken.IsCancellationRequested
                });
            }, async token =>
            {
                lock (_gate)
                {
                    if (!_results.TryGetValue(resultId, out var current) || !ReferenceEquals(current, entry) ||
                        entry.Releasing || entry.RawInvalidated)
                        return false;
                }
                try
                { await FileExports.ValidateReportAsync(destination, token); return true; }
                catch (CaptureFileException) { return false; }
            });
        }

        private static AgentPermission ReportExport(Guid sourceId)
        {
            if (!Enum.TryParse<AgentCapability>("AnalysisReportExport", out var capability))
                throw new AgentAccessException(AgentAccessError.UnsupportedOperation);
            return new(capability, AgentScope.Source(sourceId));
        }

        private static AgentAccessError FileError(CaptureFileFailure failure) => failure switch
        {
            CaptureFileFailure.FolderChanged => AgentAccessError.SourceChanged,
            CaptureFileFailure.DestinationExists => AgentAccessError.DestinationExists,
            CaptureFileFailure.InvalidPath or CaptureFileFailure.ProtectedSource => AgentAccessError.InvalidPath,
            _ => AgentAccessError.OperationUnavailable
        };

        private sealed record ReportListing(
            Guid ReportId, Guid SourceId, Guid SnapshotId, CaptureDataKind Kind,
            DateTimeOffset CapturedAt, DateTimeOffset CompletedAt, Guid? CaptureId, long? CaptureEpoch,
            long? DesktopRunId, int DocumentCount, Guid? ResultId);
    }
}
