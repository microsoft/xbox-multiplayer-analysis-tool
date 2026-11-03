// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XMAT.SharedInterfaces;

namespace XMAT.AgentAccess
{
    internal sealed class AgentScriptApplication(
        AgentRuntime runtime, ICaptureScripts scripts, Func<bool> accepting,
        ICaptureFileExports fileExports = null, ICaptureFileInputs fileInputs = null)
    {
        private readonly object _gate = new();
        private readonly Dictionary<Guid, ValidationEntry> _validations = new();
        private readonly Dictionary<AgentSession, int> _reserved = new();
        private readonly Dictionary<AgentSession, int> _fileReserved = new();
        private readonly byte[] _cursorKey = RandomNumberGenerator.GetBytes(32);

        internal async Task<JsonElement> ListAsync(AgentSession session, Guid captureId, string cursor, CancellationToken cancellationToken)
        {
            Demand(session, captureId, AgentCapability.ScriptRead);
            var items = await Call(() => scripts.GetScriptsAsync(captureId, cancellationToken));
            var fingerprint = AgentOperationDescriptor.Hash(items);
            var offset = Offset(session, captureId, "scripts", fingerprint, cursor);
            return Page(session, captureId, captureId, "scripts", fingerprint, items, offset);
        }

        internal async Task<JsonElement> SymbolsAsync(AgentSession session, Guid captureId, string cursor, CancellationToken cancellationToken)
        {
            Demand(session, captureId, AgentCapability.ScriptRead);
            var symbols = await Call(() => scripts.GetSymbolsAsync(captureId, cancellationToken));
            var items = symbols.SelectMany(symbol => Flatten(symbol, null)).ToArray();
            var fingerprint = AgentOperationDescriptor.Hash(items);
            var offset = Offset(session, captureId, "symbols", fingerprint, cursor);
            return Page(session, captureId, captureId, "symbols", fingerprint, items, offset);
        }

        internal async Task<JsonElement> ReadAsync(
            AgentSession session, Guid captureId, Guid scriptId, long expectedRevision,
            long byteOffset, int maximumBytes, CancellationToken cancellationToken)
        {
            Demand(session, captureId, AgentCapability.ScriptRead);
            var source = await Call(() => scripts.ReadAsync(captureId, scriptId, cancellationToken));
            if (source.Script.Revision != expectedRevision)
                throw new AgentAccessException(AgentAccessError.SourceChanged);
            if (source.Script.SourceBytes > runtime.GetSession(session).Limits.MaximumQueryResultBytes)
                throw new AgentAccessException(AgentAccessError.LimitExceeded);
            ArgumentOutOfRangeException.ThrowIfNegative(byteOffset);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
            var utf8 = Encoding.UTF8.GetBytes(source.Source);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(byteOffset, utf8.LongLength);
            var budget = Math.Min(64 * 1024, Math.Max(1, (runtime.GetSession(session).Limits.ResponseBytes - 1024) * 3 / 4));
            var length = (int)Math.Min(Math.Min(maximumBytes, budget), utf8.LongLength - byteOffset);
            var bytes = utf8.AsSpan(checked((int)byteOffset), length).ToArray();
            var result = AgentJson.Encode(new
            {
                script = source.Script,
                byteOffset,
                totalBytes = utf8.LongLength,
                bytes,
                hasMore = byteOffset + length < utf8.LongLength,
                encoding = "utf-8"
            });
            Demand(session, captureId, AgentCapability.ScriptRead);
            return result;
        }

        internal async Task<Guid> UpdateAsync(
            AgentSession session, Guid captureId, Guid scriptId, long expectedRevision, string expectedSourceHash,
            string source, string idempotencyKey, CancellationToken cancellationToken)
        {
            var requirements = Permissions(captureId, AgentCapability.ScriptRead, AgentCapability.ScriptEdit);
            Demand(session, requirements);
            ArgumentNullException.ThrowIfNull(source);
            if (Encoding.UTF8.GetByteCount(source) > runtime.GetSession(session).Limits.MaximumQueryResultBytes)
                throw new AgentAccessException(AgentAccessError.LimitExceeded);
            var hash = AgentOperationDescriptor.Hash(new { captureId, scriptId, expectedRevision, expectedSourceHash, source });
            var replay = runtime.FindReplay(session, idempotencyKey, "script.update", hash);
            if (replay.HasValue)
                return replay.Value;
            var current = await Preflight(session, captureId, scriptId, expectedRevision, expectedSourceHash, cancellationToken);
            runtime.EnsureOwnershipAvailable(session, Key(scriptId));
            var descriptor = new AgentOperationDescriptor("script.update", requirements,
                AgentOperationEffects.StateChange | AgentOperationEffects.HumanOwnedResource, hash,
                Localization.GetLocalizedString("AGENT_SCRIPT_EDIT_APPROVAL", current.CaptureName, current.Script.Event,
                    expectedRevision, expectedSourceHash, SourceHash(source), Diff(current.Source, source)));
            cancellationToken.ThrowIfCancellationRequested();
            return runtime.Submit(session, descriptor, idempotencyKey, async context =>
            {
                Demand(session, requirements);
                runtime.EnsureOwnershipAvailable(session, Key(scriptId));
                var result = await Call(() => scripts.UpdateAsync(captureId, scriptId, expectedRevision,
                    expectedSourceHash, source, context.CancellationToken));
                await ReleaseOldOwnershipAsync(session, scriptId, expectedRevision);
                return AgentJson.Encode(result);
            }, token => RevalidateAsync(captureId, scriptId, expectedRevision, expectedSourceHash, token));
        }

        internal Task<JsonElement> InputsAsync(AgentSession session, int offset)
        {
            Demand(session, Array.Empty<AgentPermission>());
            var permissions = runtime.GetSession(session).Permissions;
            var granted = permissions.Where(permission => permission.Capability == AgentCapability.FileRead)
                .Select(permission => permission.Scope.Id).ToHashSet();
            var all = fileInputs?.GetInputs().Where(input => granted.Contains(input.Id)).ToArray() ?? [];
            if (offset < 0 || offset > all.Length)
                throw new AgentAccessException(AgentAccessError.InvalidRequest);
            var items = all.Skip(offset).Take(runtime.GetSession(session).Limits.ResponseRows).ToArray();
            foreach (var input in items)
                Demand(session, new AgentPermission(AgentCapability.FileRead, AgentScope.InputFile(input.Id)));
            return Task.FromResult(AgentJson.Encode(new
            {
                inputs = items,
                totalCount = all.Length,
                nextOffset = offset + items.Length < all.Length ? (int?)(offset + items.Length) : null,
                arbitraryFileReadSupported = false
            }));
        }

        internal async Task<Guid> LoadFileAsync(
            AgentSession session, Guid captureId, Guid scriptId, long expectedRevision, string expectedSourceHash,
            Guid inputId, string idempotencyKey, CancellationToken cancellationToken)
        {
            var requirements = Permissions(captureId, AgentCapability.ScriptRead, AgentCapability.ScriptEdit)
                .Append(new AgentPermission(AgentCapability.FileRead, AgentScope.InputFile(inputId))).ToArray();
            Demand(session, requirements);
            var hash = AgentOperationDescriptor.Hash(new { captureId, scriptId, expectedRevision, expectedSourceHash, inputId });
            var replay = runtime.FindReplay(session, idempotencyKey, "script.load_file", hash);
            if (replay.HasValue)
                return replay.Value;
            var current = await Preflight(session, captureId, scriptId, expectedRevision, expectedSourceHash, cancellationToken);
            runtime.EnsureOwnershipAvailable(session, Key(scriptId));
            var input = await ReadInputAsync(session, inputId, cancellationToken);
            var inputFile = input.File;
            var loadedSourceHash = SourceHash(input.Source);
            Demand(session, requirements);
            var descriptor = new AgentOperationDescriptor("script.load_file", requirements,
                AgentOperationEffects.StateChange | AgentOperationEffects.HumanOwnedResource, hash,
                Localization.GetLocalizedString("AGENT_SCRIPT_INPUT_APPROVAL", current.CaptureName, current.Script.Event,
                    expectedRevision, expectedSourceHash, inputFile.FileName, inputFile.Length, inputFile.Sha256,
                    loadedSourceHash, Diff(current.Source, input.Source)));
            cancellationToken.ThrowIfCancellationRequested();
            return runtime.Submit(session, descriptor, idempotencyKey, async context =>
            {
                Demand(session, requirements);
                runtime.EnsureOwnershipAvailable(session, Key(scriptId));
                using var deadline = Deadline(context, runtime.GetSession(session).Limits);
                try
                {
                    var source = await ReadInputAsync(session, inputId, deadline.Token);
                    Demand(session, requirements);
                    if (source.File != inputFile || SourceHash(source.Source) != loadedSourceHash)
                        throw new AgentAccessException(AgentAccessError.SourceChanged);
                    var result = await Call(() => scripts.UpdateAsync(captureId, scriptId,
                        expectedRevision, expectedSourceHash, source.Source, deadline.Token));
                    await ReleaseOldOwnershipAsync(session, scriptId, expectedRevision);
                    return AgentJson.Encode(new
                    {
                        inputId,
                        inputHash = source.File.Sha256,
                        script = result.Script,
                        result.Changed,
                        fileNameRebound = false
                    });
                }
                catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
                { throw new AgentAccessException(AgentAccessError.QueryTimeout); }
            }, async token =>
            {
                if (!await RevalidateAsync(captureId, scriptId, expectedRevision, expectedSourceHash, token))
                    return false;
                try
                {
                    var limits = runtime.GetSession(session).Limits;
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(limits.QueryTimeoutSeconds));
                    await fileInputs.ValidateAsync(inputId, limits.MaximumQueryResultBytes, deadline.Token);
                    return true;
                }
                catch (CaptureFileException) { return false; }
                catch (QueryException) { return false; }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { throw new AgentAccessException(AgentAccessError.QueryTimeout); }
            });
        }

        private async Task<ScriptFileInput> ReadInputAsync(
            AgentSession session, Guid inputId, CancellationToken cancellationToken)
        {
            if (fileInputs == null)
                throw new AgentAccessException(AgentAccessError.UnsupportedOperation);
            var permission = new AgentPermission(AgentCapability.FileRead, AgentScope.InputFile(inputId));
            Demand(session, permission);
            var limits = runtime.GetSession(session).Limits;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(limits.QueryTimeoutSeconds));
            ReserveFile(session, limits.MaximumQueryResults);
            try
            {
                var input = await fileInputs.ReadScriptAsync(inputId, limits.MaximumQueryResultBytes, deadline.Token);
                Demand(session, permission);
                return input;
            }
            catch (CaptureFileException exception) { throw FileError(exception); }
            catch (QueryException exception) when (exception.Error == QueryError.ResultLimit)
            { throw new AgentAccessException(AgentAccessError.LimitExceeded); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new AgentAccessException(AgentAccessError.QueryTimeout); }
            finally { UnreserveFile(session); }
        }

        internal async Task<Guid> SaveFileAsync(
            AgentSession session, Guid captureId, Guid scriptId, long expectedRevision, string expectedSourceHash,
            Guid folderId, string fileName, string idempotencyKey, CancellationToken cancellationToken)
        {
            var requirements = Permissions(captureId, AgentCapability.ScriptRead, AgentCapability.ScriptExport)
                .Append(new AgentPermission(AgentCapability.FileWrite, AgentScope.ExportFolder(folderId))).ToArray();
            Demand(session, requirements);
            var hash = AgentOperationDescriptor.Hash(new
            {
                captureId,
                scriptId,
                expectedRevision,
                expectedSourceHash,
                folderId,
                fileName
            });
            var replay = runtime.FindReplay(session, idempotencyKey, "script.save_file", hash);
            if (replay.HasValue)
                return replay.Value;
            if (fileExports == null)
                throw new AgentAccessException(AgentAccessError.UnsupportedOperation);
            var source = await Preflight(session, captureId, scriptId, expectedRevision, expectedSourceHash, cancellationToken);
            var destination = new ScriptFileDestination(folderId, fileName);
            try
            { await fileExports.ValidateScriptAsync(destination, cancellationToken); }
            catch (CaptureFileException exception) { throw FileError(exception); }
            var folder = fileExports.GetFolders().Single(item => item.Id == folderId);
            var descriptor = new AgentOperationDescriptor("script.save_file", requirements,
                AgentOperationEffects.SensitiveDataExport | AgentOperationEffects.StateChange, hash,
                Localization.GetLocalizedString("AGENT_SCRIPT_FILE_APPROVAL", source.CaptureName, source.Script.Event,
                    scriptId, expectedRevision, expectedSourceHash, source.Script.SourceBytes, folder.Path, fileName));
            Demand(session, requirements);
            cancellationToken.ThrowIfCancellationRequested();
            return runtime.Submit(session, descriptor, idempotencyKey, async context =>
            {
                Demand(session, requirements);
                var limits = runtime.GetSession(session).Limits;
                using var deadline = Deadline(context, limits);
                ReserveFile(session, limits.MaximumQueryResults);
                try
                {
                    var current = await Preflight(session, captureId, scriptId, expectedRevision, expectedSourceHash, deadline.Token);
                    var receipt = await fileExports.SaveScriptAsync(current, destination, limits.MaximumQueryResultBytes, async token =>
                    {
                        token.ThrowIfCancellationRequested();
                        Demand(session, requirements);
                        if (!await RevalidateAsync(captureId, scriptId, expectedRevision, expectedSourceHash, token))
                            throw new AgentAccessException(AgentAccessError.SourceChanged);
                        Demand(session, requirements);
                    }, deadline.Token);
                    return AgentJson.Encode(receipt);
                }
                catch (CaptureFileException exception) { throw FileError(exception); }
                catch (QueryException exception) when (exception.Error == QueryError.ResultLimit)
                { throw new AgentAccessException(AgentAccessError.LimitExceeded); }
                catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
                { throw new AgentAccessException(AgentAccessError.QueryTimeout); }
                finally
                {
                    UnreserveFile(session);
                }
            }, async token =>
            {
                if (!await RevalidateAsync(captureId, scriptId, expectedRevision, expectedSourceHash, token))
                    return false;
                try
                {
                    await fileExports.ValidateScriptAsync(destination, token);
                    return true;
                }
                catch (CaptureFileException) { return false; }
            });
        }

        private static AgentAccessException FileError(CaptureFileException exception) => new(exception.Failure switch
        {
            CaptureFileFailure.FolderChanged or CaptureFileFailure.InputChanged => AgentAccessError.SourceChanged,
            CaptureFileFailure.DestinationExists => AgentAccessError.DestinationExists,
            CaptureFileFailure.InvalidPath or CaptureFileFailure.ProtectedSource => AgentAccessError.InvalidPath,
            _ => AgentAccessError.OperationUnavailable
        });

        private void ReserveFile(AgentSession session, int maximum)
        {
            lock (_gate)
            {
                if (_fileReserved.GetValueOrDefault(session) >= maximum)
                    throw new AgentAccessException(AgentAccessError.LimitExceeded);
                _fileReserved[session] = _fileReserved.GetValueOrDefault(session) + 1;
            }
        }

        private void UnreserveFile(AgentSession session)
        {
            lock (_gate)
            {
                if (--_fileReserved[session] == 0)
                    _fileReserved.Remove(session);
            }
        }

        internal async Task<Guid> ValidateAsync(
            AgentSession session, Guid captureId, Guid scriptId, long expectedRevision, string expectedSourceHash,
            string idempotencyKey, CancellationToken cancellationToken)
        {
            var permission = Permission(captureId, AgentCapability.ScriptRead);
            Demand(session, permission);
            var hash = AgentOperationDescriptor.Hash(new { captureId, scriptId, expectedRevision, expectedSourceHash });
            var replay = runtime.FindReplay(session, idempotencyKey, "script.validate", hash);
            if (replay.HasValue)
                return replay.Value;
            await Preflight(session, captureId, scriptId, expectedRevision, expectedSourceHash, cancellationToken);
            var descriptor = new AgentOperationDescriptor("script.validate", [permission], AgentOperationEffects.None, hash);
            cancellationToken.ThrowIfCancellationRequested();
            return runtime.Submit(session, descriptor, idempotencyKey, async context =>
            {
                var limits = runtime.GetSession(session).Limits;
                Reserve(session, limits.MaximumQueryResults);
                try
                {
                    using var deadline = Deadline(context, limits);
                    CaptureScriptValidation validation;
                    try
                    {
                        validation = await Call(() => scripts.ValidateAsync(captureId, scriptId, expectedRevision,
                            expectedSourceHash, deadline.Token));
                    }
                    catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
                    {
                        throw new AgentAccessException(AgentAccessError.QueryTimeout);
                    }
                    return StoreValidation(context, session, captureId, validation);
                }
                finally { Unreserve(session); }
            }, token => RevalidateAsync(captureId, scriptId, expectedRevision, expectedSourceHash, token));
        }

        internal async Task<Guid> SetEnabledAsync(
            AgentSession session, Guid captureId, Guid scriptId, long expectedRevision, string expectedSourceHash,
            bool enabled, string idempotencyKey, CancellationToken cancellationToken)
        {
            var requirements = Permissions(captureId, AgentCapability.ScriptRead, AgentCapability.ScriptExecute);
            Demand(session, requirements);
            var action = enabled ? "script.enable" : "script.disable";
            var hash = AgentOperationDescriptor.Hash(new { captureId, scriptId, expectedRevision, expectedSourceHash, enabled });
            var replay = runtime.FindReplay(session, idempotencyKey, action, hash);
            if (replay.HasValue)
                return replay.Value;
            var current = await Preflight(session, captureId, scriptId, expectedRevision, expectedSourceHash, cancellationToken);
            runtime.EnsureOwnershipAvailable(session, Key(scriptId));
            var descriptor = new AgentOperationDescriptor(action, requirements,
                AgentOperationEffects.StateChange | AgentOperationEffects.TrustedCodeExecution |
                AgentOperationEffects.HumanOwnedResource, hash,
                Localization.GetLocalizedString("AGENT_SCRIPT_ACTIVATION_APPROVAL", current.CaptureName, current.Script.Event,
                    enabled, expectedRevision, expectedSourceHash, current.Source));
            cancellationToken.ThrowIfCancellationRequested();
            return runtime.Submit(session, descriptor, idempotencyKey, async context =>
            {
                Demand(session, requirements);
                runtime.EnsureOwnershipAvailable(session, Key(scriptId));
                var limits = runtime.GetSession(session).Limits;
                using var deadline = Deadline(context, limits);
                CaptureScriptTransition result;
                try
                {
                    result = await Call(() => scripts.SetEnabledAsync(captureId, scriptId, expectedRevision,
                        expectedSourceHash, enabled, deadline.Token));
                }
                catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
                {
                    throw new AgentAccessException(AgentAccessError.QueryTimeout);
                }
                if (result.Outcome == CaptureScriptOutcome.SourceChanged)
                    throw new AgentAccessException(AgentAccessError.SourceChanged);
                if (result.Outcome == CaptureScriptOutcome.Enabled && result.Changed)
                {
                    var revision = result.Script.Revision;
                    context.OwnResource(Key(scriptId), revision.ToString(CultureInfo.InvariantCulture),
                        () => CleanupAsync(captureId, scriptId, revision));
                }
                else if (result.Changed)
                    await ReleaseOldOwnershipAsync(session, scriptId, expectedRevision);
                if (result.Outcome == CaptureScriptOutcome.CompilationFailed)
                {
                    Reserve(session, limits.MaximumQueryResults);
                    try
                    {
                        var validation = new CaptureScriptValidation(result.Script, result.Script.Revision,
                            expectedSourceHash, result.IsCurrent, true, result.Diagnostics);
                        var receipt = StoreValidation(context, session, captureId, validation);
                        throw new AgentAccessException(AgentAccessError.ScriptCompilationFailed, receipt);
                    }
                    finally { Unreserve(session); }
                }
                return AgentJson.Encode(new { script = result.Script, result.Outcome, result.Changed, result.IsCurrent, diagnosticCount = result.Diagnostics.Count });
            }, token => RevalidateAsync(captureId, scriptId, expectedRevision, expectedSourceHash, token));
        }

        internal JsonElement Diagnostics(AgentSession session, Guid validationId, string cursor)
        {
            var entry = GetValidation(session, validationId);
            var fingerprint = entry.Validation.SourceHash;
            var offset = Offset(session, validationId, "diagnostics", fingerprint, cursor);
            var result = Page(session, entry.CaptureId, validationId, "diagnostics", fingerprint, entry.Validation.Diagnostics, offset);
            GetValidation(session, validationId);
            return result;
        }

        internal async Task<JsonElement> ReleaseValidationAsync(AgentSession session, Guid validationId)
        {
            GetValidation(session, validationId);
            if (!await runtime.ReleaseOwnedResourceAsync(session, new AgentResourceKey(AgentResourceKind.ScriptValidation, validationId)))
                throw new AgentAccessException(AgentAccessError.RecoveryRequired);
            return AgentJson.Encode(new { validationId, released = true });
        }

        internal Task DisposeResultsAsync()
        {
            lock (_gate)
            { _validations.Clear(); }
            return Task.CompletedTask;
        }

        private JsonElement StoreValidation(
            AgentExecutionContext context, AgentSession owner, Guid captureId, CaptureScriptValidation validation)
        {
            if (JsonSerializer.SerializeToUtf8Bytes(validation, AgentJson.Options).LongLength >
                runtime.GetSession(owner).Limits.MaximumQueryResultBytes)
                throw new AgentAccessException(AgentAccessError.LimitExceeded);
            var id = Guid.NewGuid();
            var entry = new ValidationEntry(owner, captureId, validation);
            lock (_gate)
            { _validations.Add(id, entry); }
            try
            {
                context.OwnResource(new AgentResourceKey(AgentResourceKind.ScriptValidation, id), validation.SourceHash, () =>
                {
                    lock (_gate)
                    { _validations.Remove(id); }
                    return Task.FromResult(AgentCleanupOutcome.Released);
                });
            }
            catch
            {
                lock (_gate)
                { _validations.Remove(id); }
                throw;
            }
            return AgentJson.Encode(new
            {
                validationId = id,
                captureId,
                scriptId = validation.Script.Id,
                validation.ValidatedRevision,
                validation.SourceHash,
                validation.IsCurrent,
                validation.HasErrors,
                diagnosticCount = validation.Diagnostics.Count
            });
        }

        private async Task<CaptureScriptSource> Preflight(
            AgentSession session, Guid captureId, Guid scriptId, long revision, string hash, CancellationToken cancellationToken)
        {
            var source = await Call(() => scripts.ReadAsync(captureId, scriptId, cancellationToken));
            Demand(session, captureId, AgentCapability.ScriptRead);
            if (source.Script.SourceBytes > runtime.GetSession(session).Limits.MaximumQueryResultBytes)
                throw new AgentAccessException(AgentAccessError.LimitExceeded);
            if (source.Script.Revision != revision || hash == null || source.Script.SourceHash != hash)
                throw new AgentAccessException(AgentAccessError.SourceChanged);
            runtime.ForgetSupersededScript(scriptId, source.Script.Revision);
            return source;
        }

        private async Task<bool> RevalidateAsync(
            Guid captureId, Guid scriptId, long revision, string hash, CancellationToken cancellationToken)
        {
            try
            {
                var source = await scripts.ReadAsync(captureId, scriptId, cancellationToken);
                return source.Script.Revision == revision && source.Script.SourceHash == hash;
            }
            catch (CaptureScriptException) { return false; }
        }

        private async Task<AgentCleanupOutcome> CleanupAsync(Guid captureId, Guid scriptId, long revision) =>
            await scripts.DisableOwnedAsync(captureId, scriptId, revision)
                ? AgentCleanupOutcome.Released : AgentCleanupOutcome.NoLongerOwned;

        private async Task ReleaseOldOwnershipAsync(AgentSession owner, Guid scriptId, long revision)
        {
            if (!runtime.IsOwned(owner, Key(scriptId), revision.ToString(CultureInfo.InvariantCulture)))
                return;
            try
            { await runtime.ReleaseOwnedResourceAsync(owner, Key(scriptId)); }
            catch (AgentAccessException exception) when (exception.Error is AgentAccessError.SessionClosed or AgentAccessError.PermissionsUpdating) { }
        }

        private ValidationEntry GetValidation(AgentSession session, Guid id)
        {
            ValidationEntry entry;
            lock (_gate)
            {
                entry = _validations.TryGetValue(id, out var found) && ReferenceEquals(found.Owner, session)
                    ? found : throw new AgentAccessException(AgentAccessError.OperationNotFound);
            }
            Demand(session, entry.CaptureId, AgentCapability.ScriptRead);
            return entry;
        }

        private JsonElement Page<T>(
            AgentSession session, Guid captureId, Guid resourceId, string kind, string fingerprint, IReadOnlyList<T> rows, long offset)
        {
            if (offset > rows.Count)
                throw new AgentAccessException(AgentAccessError.OperationNotFound);
            var limits = runtime.GetSession(session).Limits;
            var selected = new List<T>();
            long used = 0;
            for (var index = offset; index < rows.Count && selected.Count < limits.ResponseRows; index++)
            {
                var row = rows[checked((int)index)];
                var bytes = JsonSerializer.SerializeToUtf8Bytes(row, AgentJson.Options).Length;
                if (used + bytes > limits.ResponseBytes - 1024)
                {
                    if (selected.Count == 0)
                        throw new AgentAccessException(AgentAccessError.LimitExceeded);
                    break;
                }
                selected.Add(row);
                used += bytes;
            }
            var more = offset + selected.Count < rows.Count;
            var result = AgentJson.Encode(new
            {
                captureId,
                resourceId,
                kind,
                offset,
                totalCount = rows.Count,
                rows = selected,
                hasMore = more,
                nextCursor = more ? MakeCursor(new Cursor(session.Id, resourceId, kind, fingerprint, offset + selected.Count)) : null
            });
            Demand(session, captureId, AgentCapability.ScriptRead);
            return result;
        }

        private void Reserve(AgentSession session, int maximum)
        {
            lock (_gate)
            {
                var count = _reserved.GetValueOrDefault(session);
                if (_validations.Values.Count(entry => ReferenceEquals(entry.Owner, session)) + count >= maximum)
                    throw new AgentAccessException(AgentAccessError.LimitExceeded);
                _reserved[session] = count + 1;
            }
        }

        private void Unreserve(AgentSession session)
        {
            lock (_gate)
            { if (--_reserved[session] == 0) _reserved.Remove(session); }
        }

        private void Demand(AgentSession session, Guid captureId, AgentCapability capability) =>
            Demand(session, Permission(captureId, capability));
        private void Demand(AgentSession session, params AgentPermission[] permissions)
        {
            if (!accepting())
                throw new AgentAccessException(AgentAccessError.OperationUnavailable);
            runtime.Demand(session, permissions);
        }
        private static AgentPermission Permission(Guid captureId, AgentCapability capability) => new(capability, AgentScope.Capture(captureId));
        private static AgentPermission[] Permissions(Guid captureId, params AgentCapability[] capabilities) =>
            capabilities.Select(capability => Permission(captureId, capability)).ToArray();
        private static AgentResourceKey Key(Guid scriptId) => new(AgentResourceKind.Script, scriptId);
        private static CancellationTokenSource Deadline(AgentExecutionContext context, AgentLimits limits)
        {
            var result = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            result.CancelAfter(TimeSpan.FromSeconds(limits.QueryTimeoutSeconds));
            return result;
        }

        private static async Task<T> Call<T>(Func<Task<T>> action)
        {
            try
            { return await action(); }
            catch (CaptureScriptException exception)
            {
                throw new AgentAccessException(exception.Error switch
                {
                    CaptureScriptError.TargetNotFound => AgentAccessError.OperationNotFound,
                    CaptureScriptError.SourceChanged => AgentAccessError.SourceChanged,
                    CaptureScriptError.UnsupportedCapture => AgentAccessError.UnsupportedOperation,
                    CaptureScriptError.InvalidSource => AgentAccessError.InvalidRequest,
                    _ => AgentAccessError.OperationUnavailable
                });
            }
        }

        private static string SourceHash(string source) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));

        private static IEnumerable<CaptureScriptSymbol> Flatten(CaptureScriptSymbol symbol, string parent)
        {
            var name = parent == null ? symbol.Name : $"{parent}.{symbol.Name}";
            yield return symbol with { Name = name, Properties = Array.Empty<CaptureScriptSymbol>() };
            foreach (var child in symbol.Properties)
            {
                foreach (var item in Flatten(child, name))
                    yield return item;
            }
        }

        private static string Diff(string before, string after)
        {
            var oldLines = before.Replace("\r\n", "\n").Split('\n');
            var newLines = after.Replace("\r\n", "\n").Split('\n');
            var prefix = 0;
            while (prefix < oldLines.Length && prefix < newLines.Length && oldLines[prefix] == newLines[prefix])
                prefix++;
            var suffix = 0;
            while (suffix < oldLines.Length - prefix && suffix < newLines.Length - prefix &&
                oldLines[^(suffix + 1)] == newLines[^(suffix + 1)])
                suffix++;
            var start = Math.Max(0, prefix - 3);
            var endOld = Math.Min(oldLines.Length, oldLines.Length - suffix + 3);
            var endNew = Math.Min(newLines.Length, newLines.Length - suffix + 3);
            var text = new StringBuilder("--- current\n+++ proposed\n");
            text.AppendLine($"@@ -{start + 1},{endOld - start} +{start + 1},{endNew - start} @@");
            for (var index = start; index < prefix; index++)
                text.AppendLine(" " + oldLines[index]);
            for (var index = prefix; index < oldLines.Length - suffix; index++)
                text.AppendLine("-" + oldLines[index]);
            for (var index = prefix; index < newLines.Length - suffix; index++)
                text.AppendLine("+" + newLines[index]);
            for (var index = oldLines.Length - suffix; index < endOld; index++)
                text.AppendLine(" " + oldLines[index]);
            return text.ToString();
        }

        private string MakeCursor(Cursor cursor)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(cursor);
            return Convert.ToBase64String(bytes.Concat(HMACSHA256.HashData(_cursorKey, bytes)).ToArray());
        }
        private long Offset(AgentSession session, Guid resource, string kind, string fingerprint, string value)
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
                if (cursor == null || cursor.Session != session.Id || cursor.Resource != resource ||
                    cursor.Kind != kind || cursor.Offset < 0)
                    throw new FormatException();
                if (cursor.Fingerprint != fingerprint)
                    throw new AgentAccessException(AgentAccessError.SourceChanged);
                return cursor.Offset;
            }
            catch (Exception exception) when (exception is FormatException or JsonException)
            {
                throw new AgentAccessException(AgentAccessError.OperationNotFound);
            }
        }

        private sealed record ValidationEntry(AgentSession Owner, Guid CaptureId, CaptureScriptValidation Validation);
        private sealed record Cursor(Guid Session, Guid Resource, string Kind, string Fingerprint, long Offset);
    }
}
