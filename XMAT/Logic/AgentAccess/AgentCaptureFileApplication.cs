// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XMAT.SharedInterfaces;

namespace XMAT.AgentAccess
{
    internal sealed class AgentCaptureFileApplication(
        AgentRuntime runtime, ICaptureQueries sources, ICaptureFiles files,
        ICaptureFileExports exports, Func<bool> accepting,
        ICaptureFileInputs inputs = null, ICaptureFileLoads loads = null, ICaptureSazImports imports = null,
        ICaptureEtlImports etlImports = null)
    {
        private readonly object _gate = new();
        private readonly Dictionary<AgentSession, int> _running = new();

        internal Task<Guid> ImportSazAsync(
            AgentSession session, Guid inputId, Guid sourceId, string idempotencyKey, CancellationToken cancellationToken) =>
            ImportAsync(session, inputId, sourceId, idempotencyKey, false, cancellationToken);

        internal Task<Guid> ImportEtlAsync(
            AgentSession session, Guid inputId, Guid sourceId, string idempotencyKey, CancellationToken cancellationToken) =>
            ImportAsync(session, inputId, sourceId, idempotencyKey, true, cancellationToken);

        private async Task<Guid> ImportAsync(
            AgentSession session, Guid inputId, Guid sourceId, string idempotencyKey, bool etl, CancellationToken cancellationToken)
        {
            ICaptureFileImports service = etl ? etlImports : imports;
            var action = etl ? "capture.import_etl" : "capture.import_saz";
            DemandAccepting();
            var requirements = new[]
            {
                new AgentPermission(AgentCapability.CaptureImport, AgentScope.Source(sourceId)),
                new AgentPermission(AgentCapability.FileRead, AgentScope.InputFile(inputId))
            };
            runtime.Demand(session, requirements);
            var hash = AgentOperationDescriptor.Hash(new { inputId, sourceId });
            var replay = runtime.FindReplay(session, idempotencyKey, action, hash);
            if (replay.HasValue)
                return replay.Value;
            if (service == null || inputs == null)
                throw new AgentAccessException(AgentAccessError.UnsupportedOperation);
            var input = inputs.GetInputs().SingleOrDefault(item => item.Id == inputId);
            if (input?.Kind != (etl ? CaptureInputKind.NetworkTraceEtl : CaptureInputKind.FiddlerSaz))
                throw new AgentAccessException(AgentAccessError.InvalidPath);
            await ValidateInputAsync(session, inputId, cancellationToken);
            CaptureImportTarget target;
            try
            { target = await service.GetTargetAsync(input.FileName, cancellationToken); }
            catch (QueryException exception) { throw QueryFailure(exception); }
            catch (CaptureFileException exception) { throw FileError(exception); }
            if (target.SourceId != sourceId)
                throw new AgentAccessException(AgentAccessError.SourceChanged);
            var descriptor = new AgentOperationDescriptor(action, requirements,
                AgentOperationEffects.StateChange | AgentOperationEffects.DataDeletion | AgentOperationEffects.HumanOwnedResource,
                hash, Localization.GetLocalizedString(etl ? "ETL_IMPORT_APPROVAL" : "AGENT_SAZ_IMPORT_APPROVAL", input.FileName, input.Length,
                    input.Sha256, sourceId, target.DeviceName, target.CaptureId?.ToString() ?? "-"));
            DemandAccepting();
            runtime.Demand(session, requirements);
            cancellationToken.ThrowIfCancellationRequested();
            return runtime.Submit(session, descriptor, idempotencyKey, async context =>
            {
                DemandAccepting();
                runtime.Demand(session, requirements);
                var limits = runtime.GetSession(session).Limits;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(limits.QueryTimeoutSeconds));
                try
                {
                    var receipt = await service.ImportInputAsync(inputId, target, limits.MaximumQueryResultBytes, token =>
                    {
                        token.ThrowIfCancellationRequested();
                        DemandAccepting();
                        runtime.Demand(session, requirements);
                        return Task.CompletedTask;
                    }, deadline.Token);
                    return AgentJson.Encode(new
                    {
                        inputId,
                        inputHash = input.Sha256,
                        receipt.SourceId,
                        receipt.CaptureId,
                        receipt.DeviceName,
                        receipt.RecordCount,
                        receipt.AddedContext,
                        receipt.Published,
                        receipt.ViewsNotified,
                        sourceGrantsAdded = false,
                        existingSourceGrantsCoverImportedData = true,
                        selectedTabChanged = false
                    });
                }
                catch (CaptureFileException exception) { throw FileError(exception); }
                catch (QueryException exception) { throw QueryFailure(exception); }
                catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
                { throw new AgentAccessException(AgentAccessError.QueryTimeout); }
            }, async token =>
            {
                try
                {
                    if (await service.GetTargetAsync(input.FileName, token) != target)
                        return false;
                    await ValidateInputAsync(session, inputId, token);
                    return true;
                }
                catch (QueryException) { return false; }
                catch (AgentAccessException exception) when (exception.Error == AgentAccessError.SourceChanged)
                { return false; }
            });
        }

        private static AgentAccessException QueryFailure(QueryException exception) => new(exception.Error switch
        {
            QueryError.SourceBusy => AgentAccessError.OperationUnavailable,
            QueryError.SourceChanged => AgentAccessError.SourceChanged,
            QueryError.ResultLimit => AgentAccessError.LimitExceeded,
            _ => AgentAccessError.ExecutionFailed
        });

        internal async Task<Guid> LoadAsync(
            AgentSession session, Guid inputId, Guid? expectedLoadedSourceId,
            string idempotencyKey, CancellationToken cancellationToken)
        {
            DemandAccepting();
            var requirements = new[]
            {
                new AgentPermission(AgentCapability.CaptureLoad, AgentScope.Application),
                new AgentPermission(AgentCapability.FileRead, AgentScope.InputFile(inputId))
            };
            runtime.Demand(session, requirements);
            var hash = AgentOperationDescriptor.Hash(new { inputId, expectedLoadedSourceId });
            var replay = runtime.FindReplay(session, idempotencyKey, "capture.load_file", hash);
            if (replay.HasValue)
                return replay.Value;
            if (loads == null || inputs == null)
                throw new AgentAccessException(AgentAccessError.UnsupportedOperation);
            var input = inputs.GetInputs().SingleOrDefault(item => item.Id == inputId);
            if (input?.Kind != CaptureInputKind.CaptureDatabase)
                throw new AgentAccessException(AgentAccessError.InvalidPath);
            await ValidateInputAsync(session, inputId, cancellationToken);
            var state = await loads.GetStateAsync(cancellationToken);
            if (state.LoadedSourceId != expectedLoadedSourceId)
                throw new AgentAccessException(AgentAccessError.SourceChanged);
            var descriptor = new AgentOperationDescriptor("capture.load_file", requirements,
                AgentOperationEffects.StateChange | AgentOperationEffects.HumanOwnedResource, hash,
                Localization.GetLocalizedString("AGENT_CAPTURE_LOAD_APPROVAL", input.FileName, input.Length,
                    input.Sha256, expectedLoadedSourceId?.ToString() ?? "-"));
            DemandAccepting();
            runtime.Demand(session, requirements);
            cancellationToken.ThrowIfCancellationRequested();
            return runtime.Submit(session, descriptor, idempotencyKey, async context =>
            {
                DemandAccepting();
                runtime.Demand(session, requirements);
                var limits = runtime.GetSession(session).Limits;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(limits.QueryTimeoutSeconds));
                try
                {
                    var receipt = await loads.LoadInputAsync(inputId, state, limits.MaximumQueryResultBytes, token =>
                    {
                        token.ThrowIfCancellationRequested();
                        DemandAccepting();
                        runtime.Demand(session, requirements);
                        return Task.CompletedTask;
                    }, deadline.Token);
                    return AgentJson.Encode(new
                    {
                        inputId,
                        inputHash = input.Sha256,
                        receipt.SourceId,
                        receipt.AddedContexts,
                        receipt.UpdatedContexts,
                        receipt.Published,
                        receipt.ViewsNotified,
                        sourceGrantsAdded = false,
                        activeDataReplaced = false,
                        selectedTabChanged = false
                    });
                }
                catch (CaptureFileException exception) { throw FileError(exception); }
                catch (QueryException exception)
                {
                    throw new AgentAccessException(exception.Error switch
                    {
                        QueryError.SourceBusy => AgentAccessError.OperationUnavailable,
                        QueryError.SourceChanged => AgentAccessError.SourceChanged,
                        QueryError.ResultLimit => AgentAccessError.LimitExceeded,
                        _ => AgentAccessError.ExecutionFailed
                    });
                }
                catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
                { throw new AgentAccessException(AgentAccessError.QueryTimeout); }
            }, async token =>
            {
                if (await loads.GetStateAsync(token) != state)
                    return false;
                try
                { await ValidateInputAsync(session, inputId, token); return true; }
                catch (AgentAccessException exception) when (exception.Error == AgentAccessError.SourceChanged)
                { return false; }
            });
        }

        private async Task ValidateInputAsync(AgentSession session, Guid inputId, CancellationToken cancellationToken)
        {
            var limits = runtime.GetSession(session).Limits;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(limits.QueryTimeoutSeconds));
            try
            { await inputs.ValidateAsync(inputId, limits.MaximumQueryResultBytes, deadline.Token); }
            catch (CaptureFileException exception) { throw FileError(exception); }
            catch (QueryException exception) when (exception.Error == QueryError.ResultLimit)
            { throw new AgentAccessException(AgentAccessError.LimitExceeded); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new AgentAccessException(AgentAccessError.QueryTimeout); }
        }

        internal async Task<Guid> SaveAsync(
            AgentSession session, Guid sourceId, CaptureArchiveFormat format,
            Guid folderId, string fileName, string idempotencyKey, string deviceName,
            CancellationToken cancellationToken)
        {
            DemandAccepting();
            var requirements = new[]
            {
                new AgentPermission(AgentCapability.RawDataRead, AgentScope.Source(sourceId)),
                new AgentPermission(AgentCapability.CaptureExport, AgentScope.Source(sourceId)),
                new AgentPermission(AgentCapability.FileWrite, AgentScope.ExportFolder(folderId))
            };
            runtime.Demand(session, requirements);
            if (format == CaptureArchiveFormat.CaptureDatabase && deviceName != null)
                throw new AgentAccessException(AgentAccessError.InvalidRequest);
            var hash = AgentOperationDescriptor.Hash(new { sourceId, format, folderId, fileName, deviceName });
            var replay = runtime.FindReplay(session, idempotencyKey, "capture.export_file", hash);
            if (replay.HasValue)
                return replay.Value;
            var source = (await sources.GetSourcesAsync(cancellationToken)).SingleOrDefault(item => item.Id == sourceId);
            if (source == null)
                throw new AgentAccessException(AgentAccessError.UnsupportedOperation);
            var destination = new CaptureFileDestination(folderId, fileName, format);
            await ValidateAsync(destination, cancellationToken);
            var folder = exports.GetFolders().Single(item => item.Id == folderId);
            var descriptor = new AgentOperationDescriptor("capture.export_file", requirements,
                AgentOperationEffects.SensitiveDataExport | AgentOperationEffects.StateChange, hash,
                Localization.GetLocalizedString("AGENT_CAPTURE_FILE_SCOPE_APPROVAL", sourceId, source.Kind, format,
                    folder.Path, fileName, deviceName ?? "*", format == CaptureArchiveFormat.SanitizedFiddlerSaz));
            DemandAccepting();
            cancellationToken.ThrowIfCancellationRequested();
            return runtime.Submit(session, descriptor, idempotencyKey, async context =>
            {
                DemandAccepting();
                runtime.Demand(session, requirements);
                var limits = runtime.GetSession(session).Limits;
                lock (_gate)
                {
                    if (_running.GetValueOrDefault(session) >= limits.MaximumQueryResults)
                        throw new AgentAccessException(AgentAccessError.LimitExceeded);
                    _running[session] = _running.GetValueOrDefault(session) + 1;
                }
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(limits.QueryTimeoutSeconds));
                try
                {
                    await using var archive = deviceName == null
                        ? await files.CreateArchiveAsync(sourceId, format, limits.MaximumQueryResultBytes, deadline.Token)
                        : await files.CreateSazArchiveAsync(sourceId, format, deviceName, limits.MaximumQueryResultBytes, deadline.Token);
                    var receipt = archive.Receipt;
                    var length = await exports.SaveCaptureAsync(archive, destination, limits.MaximumQueryResultBytes, token =>
                    {
                        token.ThrowIfCancellationRequested();
                        DemandAccepting();
                        runtime.Demand(session, requirements);
                        return Task.CompletedTask;
                    }, deadline.Token);
                    return AgentJson.Encode(new
                    {
                        sourceId,
                        format,
                        folderId,
                        fileName,
                        length,
                        receipt.CapturedAt,
                        published = true,
                        sanitized = format == CaptureArchiveFormat.SanitizedFiddlerSaz,
                        deviceName = receipt.DeviceName,
                        sanitization = receipt.Sanitization,
                        scope = receipt.DeviceName == null ? "all-source-records" : "exact-device-in-source"
                    });
                }
                catch (CaptureFileException exception) { throw FileError(exception); }
                catch (QueryException exception)
                {
                    throw new AgentAccessException(exception.Error switch
                    {
                        QueryError.SourceBusy => AgentAccessError.OperationUnavailable,
                        QueryError.SourceChanged => AgentAccessError.SourceChanged,
                        QueryError.ResultLimit => AgentAccessError.LimitExceeded,
                        _ => AgentAccessError.ExecutionFailed
                    });
                }
                catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
                { throw new AgentAccessException(AgentAccessError.QueryTimeout); }
                finally
                {
                    lock (_gate)
                    {
                        if (--_running[session] == 0)
                            _running.Remove(session);
                    }
                }
            }, async token =>
            {
                if (!(await sources.GetSourcesAsync(token)).Any(item => item.Id == sourceId))
                    return false;
                try
                {
                    await exports.ValidateCaptureAsync(destination, token);
                    return true;
                }
                catch (CaptureFileException) { return false; }
            });
        }

        private async Task ValidateAsync(CaptureFileDestination destination, CancellationToken cancellationToken)
        {
            try
            { await exports.ValidateCaptureAsync(destination, cancellationToken); }
            catch (CaptureFileException exception) { throw FileError(exception); }
        }

        private static AgentAccessException FileError(CaptureFileException exception) => new(exception.Failure switch
        {
            CaptureFileFailure.DestinationExists => AgentAccessError.DestinationExists,
            CaptureFileFailure.FolderChanged or CaptureFileFailure.InputChanged => AgentAccessError.SourceChanged,
            CaptureFileFailure.InvalidPath or CaptureFileFailure.ProtectedSource => AgentAccessError.InvalidPath,
            CaptureFileFailure.InvalidCapture => AgentAccessError.InvalidCapture,
            _ => AgentAccessError.OperationUnavailable
        });

        private void DemandAccepting()
        {
            if (!accepting())
                throw new AgentAccessException(AgentAccessError.OperationUnavailable);
        }
    }
}
