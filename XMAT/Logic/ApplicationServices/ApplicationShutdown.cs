// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using XMAT.AgentAccess;
using XMAT.Models;
using XMAT.NetworkTrace;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;

namespace XMAT.ApplicationServices
{
    internal sealed class ApplicationShutdown(CaptureAppModel model, Dispatcher dispatcher, Func<Task> saveSettings = null)
    {
        private Action _exit;
        private Action<bool> _blockInput;
        private Func<bool> _canClose;
        private bool _preparing;
        private string _preparedRevision;
        private IDisposable _backupRetention;
        private AgentMutationProtection.Protection _captureBackup;
        internal bool IsClosing => _preparedRevision != null;

        internal void Configure(Action exit, Action<bool> blockInput = null, Func<bool> canClose = null)
        {
            ApplicationDispatch.VerifyUiAccess(dispatcher);
            _exit = exit;
            _blockInput = blockInput;
            _canClose = canClose;
        }

        internal Task<ApplicationShutdownSnapshot> InspectAsync(CancellationToken cancellationToken = default) =>
            ApplicationDispatch.RunOnUiAsync(dispatcher, () => InspectCoreAsync(cancellationToken));

        private async Task<ApplicationShutdownSnapshot> InspectCoreAsync(CancellationToken cancellationToken)
        {
            var captures = await model.CaptureCatalog.GetCapturesAsync(cancellationToken);
            var documents = model.HasSqlQueryWorkspace ? model.SqlQueryWorkspace.Documents.Where(document => document.IsDirty)
                .Select(document => new ShutdownDocument(document.Id, document.TextRevision, document.Title, document.FilePath)).ToArray() : [];
            var scripts = new List<CaptureScriptDescriptor>();
            foreach (var capture in captures.Where(capture => !capture.IsReadOnly && capture.CaptureType == CaptureType.WebProxy))
                scripts.AddRange((await model.CaptureScripts.GetScriptsAsync(capture.Id, cancellationToken)).Where(script =>
                    script.Revision != 0 || script.IsEnabled || script.IsCompiling));
            var data = model.ActiveDatabase?.TableNames().Where(name => name == WebServiceCaptureMethod.TableName ||
                name == NetworkTraceCaptureMethod.TableName).Any(name =>
                model.ActiveDatabase.TableByName(name).Dataset.HasRecords()) == true;
            var recovery = captures.Any(capture => capture.RecoveryRequired || capture.TraceMayBeRunning && !capture.IsRunning) ||
                model.HasAgentAccess && model.AgentAccess.Runtime.GetOwnedResources().Any(resource => resource.State == AgentResourceState.RecoveryRequired);
            var revision = AgentOperationDescriptor.Hash(new
            {
                sourceId = model.ActiveDatabase == null ? Guid.Empty : ((CaptureQueries)model.CaptureQueries).GetSourceId(model.ActiveDatabase),
                captures = captures.Select(capture => new { capture.Id, capture.Revision, capture.CaptureEpoch, capture.IsRunning }),
                documents = documents.Select(document => new { document.Id, document.Revision }),
                scripts = scripts.Select(script => new { script.Id, script.Revision, script.SourceHash })
            });
            return new(revision, model.IsHeadless, _exit != null && !_preparing && !IsClosing &&
                !model.IsAnalysisRunning && !model.IsCaptureDataOperationRunning && !model.IsCaptureFileLoadRunning &&
                !scripts.Any(script => script.IsCompiling) && (_canClose?.Invoke() ?? true),
                data || captures.Any(capture => capture.IsRunning), captures, documents, scripts.ToArray(), recovery);
        }

        internal Task<Plan> PrepareAsync(
            string expectedRevision, ApplicationShutdownChoices choices, CancellationToken cancellationToken) =>
            ApplicationDispatch.RunOnUiAsync(dispatcher, async () =>
            {
                choices ??= new();
                var snapshot = await InspectCoreAsync(cancellationToken);
                if (snapshot.Revision != expectedRevision)
                    throw new AgentAccessException(AgentAccessError.SourceChanged);
                if (!snapshot.CanRequestClose)
                    throw new AgentAccessException(AgentAccessError.OperationUnavailable);
                if (snapshot.RecoveryRequired)
                    throw new AgentAccessException(AgentAccessError.RecoveryRequired);
                var documentChoices = choices.Documents ?? [];
                if (documentChoices.Select(choice => choice.Id).Distinct().Count() != documentChoices.Count ||
                    documentChoices.Any(choice => !snapshot.UnsavedSqlDocuments.Any(document => document.Id == choice.Id)))
                    throw new AgentAccessException(AgentAccessError.InvalidRequest);
                var files = new Dictionary<Guid, CaptureLocalDestination>();
                foreach (var document in snapshot.UnsavedSqlDocuments)
                {
                    var decision = documentChoices.SingleOrDefault(choice => choice.Id == document.Id);
                    if (decision == null || decision.Action == ShutdownDocumentAction.Cancel ||
                        snapshot.HasCapturedData && !choices.DiscardCapturedData && string.IsNullOrEmpty(choices.SaveCapturePath) ||
                        snapshot.ScriptDrafts.Count > 0 && !choices.DiscardScriptDrafts)
                        throw Decisions(snapshot);
                    if (decision.ExpectedRevision != document.Revision)
                        throw new AgentAccessException(AgentAccessError.SourceChanged);
                    if (decision.Action == ShutdownDocumentAction.Save)
                    {
                        DemandExtension(decision.Path, ".sql");
                        files.Add(document.Id, await model.CaptureFileExports.PrepareDestinationAsync(
                            decision.Path, decision.Overwrite, cancellationToken));
                    }
                    else if (decision.Action != ShutdownDocumentAction.Discard)
                        throw new AgentAccessException(AgentAccessError.InvalidRequest);
                }
                if (snapshot.HasCapturedData && !choices.DiscardCapturedData && string.IsNullOrEmpty(choices.SaveCapturePath) ||
                    snapshot.ScriptDrafts.Count > 0 && !choices.DiscardScriptDrafts)
                    throw Decisions(snapshot);
                CaptureLocalDestination capture = null;
                if (!string.IsNullOrEmpty(choices.SaveCapturePath))
                {
                    DemandExtension(choices.SaveCapturePath, ".xmatcap");
                    capture = await model.CaptureFileExports.PrepareDestinationAsync(
                        choices.SaveCapturePath, choices.OverwriteCapture, cancellationToken);
                }
                return new Plan(snapshot, choices, files, capture);
            });

        internal Task<ApplicationShutdownReceipt> ExecuteAsync(Plan plan, CancellationToken cancellationToken) =>
            ApplicationDispatch.RunOnUiAsync(dispatcher, async () =>
            {
                var current = await InspectCoreAsync(cancellationToken);
                if (current.Revision != plan.Snapshot.Revision || !current.CanRequestClose)
                    throw new AgentAccessException(AgentAccessError.SourceChanged);
                _preparing = true;
                _blockInput?.Invoke(true);
                var stopped = 0;
                var savedDocuments = 0;
                var captureSaved = false;
                var proxyAttempts = 0;
                var completed = false;
                var effectsStarted = false;
                try
                {
                    if (model.HasSqlQueryWorkspace)
                    {
                        var accepted = await model.SqlQueryWorkspace.RequestCloseAsync(async document =>
                        {
                            var decision = plan.Choices.Documents.SingleOrDefault(choice => choice.Id == document.Id);
                            if (decision == null || decision.ExpectedRevision != document.TextRevision)
                                throw new AgentAccessException(AgentAccessError.SourceChanged);
                            if (decision.Action == ShutdownDocumentAction.Discard)
                                return true;
                            effectsStarted = true;
                            if (!await document.SaveForAgentCloseAsync(decision.Path, decision.ExpectedRevision,
                                plan.Documents[document.Id], model.CaptureFileExports, cancellationToken))
                                return false;
                            if (document.TextRevision != decision.ExpectedRevision || document.IsDirty)
                                throw new AgentAccessException(AgentAccessError.SourceChanged);
                            savedDocuments++;
                            return true;
                        });
                        if (!accepted)
                            throw new AgentAccessException(AgentAccessError.OperationUnavailable);
                    }
                    foreach (var capture in plan.Snapshot.Captures.Where(capture => capture.IsRunning))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        effectsStarted = true;
                        await model.CaptureCatalog.WithTargetAsync(capture.Id, capture.Revision, async (target, _) =>
                        {
                            var result = await model.CaptureOperations.SetRunningAsync(target, false, cancellationToken: cancellationToken);
                            if (!result.Succeeded || result.IsRunning)
                                throw new AgentAccessException(AgentAccessError.RecoveryRequired);
                            return true;
                        });
                        stopped++;
                    }
                    if (plan.Choices.DisableXboxProxies)
                    {
                        foreach (var capture in (await model.CaptureCatalog.GetCapturesAsync(cancellationToken)).Where(capture =>
                            !capture.IsReadOnly && capture.DeviceType == DeviceType.XboxConsole && capture.CaptureType == CaptureType.WebProxy))
                        {
                            var preflight = await model.CaptureProxyOperations.PrepareAsync(
                                capture.Id, capture.Revision, CaptureProxyAction.Disable, cancellationToken: cancellationToken);
                            effectsStarted = true;
                            proxyAttempts++;
                            var changed = await model.CaptureProxyOperations.ExecuteAsync(preflight, cancellationToken);
                            if (!changed.Succeeded)
                                throw new AgentAccessException(AgentAccessError.RecoveryRequired);
                        }
                    }
                    var final = await InspectCoreAsync(cancellationToken);
                    if (final.RecoveryRequired || final.Captures.Any(capture => capture.IsRunning))
                        throw new AgentAccessException(AgentAccessError.RecoveryRequired);
                    if (plan.Capture != null)
                    {
                        var sourceId = ((CaptureQueries)model.CaptureQueries).GetSourceId(model.ActiveDatabase);
                        await using var archive = await model.CaptureFiles.CreateArchiveAsync(
                            sourceId, CaptureArchiveFormat.CaptureDatabase, 512L * 1024 * 1024, cancellationToken);
                        effectsStarted = true;
                        await model.CaptureFileExports.SaveCaptureAsync(archive,
                            new(plan.Capture.FolderId, plan.Capture.FileName, CaptureArchiveFormat.CaptureDatabase, plan.Capture.Overwrite),
                            512L * 1024 * 1024, _ => Task.CompletedTask, cancellationToken);
                        captureSaved = true;
                    }
                    if (!model.TryBeginCaptureDataOperation(out var lease, requireAllCapturesStopped: true))
                        throw new AgentAccessException(AgentAccessError.OperationUnavailable);
                    using (lease)
                    {
                        if (final.HasCapturedData)
                            _captureBackup = await AgentMutationProtection.BeforeDatabaseAsync(
                                model, "host-shutdown", final.Revision, cancellationToken);
                        _captureBackup?.Demand();
                        await (saveSettings?.Invoke() ?? CaptureAppSettings.SerializeAsync(model));
                        cancellationToken.ThrowIfCancellationRequested();
                        _backupRetention = AgentMutationProtection.Current?.Retain();
                        _preparedRevision = (await InspectCoreAsync(cancellationToken)).Revision;
                    }
                    completed = true;
                    return new ApplicationShutdownReceipt(true, _preparedRevision, stopped, captureSaved, savedDocuments, true,
                        XboxProxyAttempts: proxyAttempts);
                }
                catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
                {
                    if (!effectsStarted)
                        throw;
                    var failure = exception as AgentAccessException ??
                        (exception is CaptureFileException file ? AgentFileErrors.Translate(file) :
                            new AgentAccessException(AgentAccessError.OperationUnavailable));
                    throw new AgentAccessException(failure.Error,
                        AgentJson.Encode(new
                        {
                            error = failure.Error,
                            shutdownRequested = false,
                            effectsMayHaveOccurred = true,
                            stoppedCaptures = stopped,
                            savedSqlDocuments = savedDocuments,
                            captureSaved,
                            xboxProxyAttempts = proxyAttempts,
                            backups = AgentMutationProtection.Current?.Backups,
                            failure = failure.ProtectedResult,
                            automaticRollback = false
                        }));
                }
                finally
                {
                    _preparing = false;
                    if (!completed)
                        CancelPrepared();
                }
            });

        internal Task RequestExitAsync(
            string expectedRevision, Func<ApplicationShutdownSnapshot, bool> stillAuthorized, CancellationToken cancellationToken) =>
            ApplicationDispatch.RunOnUiAsync(dispatcher, async () =>
            {
                var snapshot = await InspectCoreAsync(cancellationToken);
                if (_preparedRevision != expectedRevision || snapshot.Revision != expectedRevision || !stillAuthorized(snapshot) ||
                    snapshot.RecoveryRequired || snapshot.Captures.Any(capture => capture.IsRunning))
                {
                    CancelPrepared();
                    throw new AgentAccessException(AgentAccessError.SourceChanged);
                }
                _captureBackup?.Demand();
                _exit();
            });

        internal async Task DatabaseDisposedAsync()
        {
            if (_captureBackup != null)
            {
                _captureBackup.Committed();
                await _captureBackup.DisposeAsync();
                _captureBackup = null;
            }
            _backupRetention?.Dispose();
            _backupRetention = null;
        }

        internal void CancelPrepared()
        {
            _preparedRevision = null;
            _backupRetention?.Dispose();
            _backupRetention = null;
            _captureBackup = null;
            model.CancelSqlWorkspaceClosing();
            _blockInput?.Invoke(false);
        }

        internal Task CancelPreparedAsync() => ApplicationDispatch.RunOnUiAsync(dispatcher, CancelPrepared);

        private static AgentAccessException Decisions(ApplicationShutdownSnapshot snapshot) =>
            new(AgentAccessError.ShutdownDecisionRequired, AgentJson.Encode(new
            {
                error = AgentAccessError.ShutdownDecisionRequired,
                shutdown = snapshot,
                instruction = Localization.GetLocalizedString("AGENT_SHUTDOWN_DECISIONS")
            }));

        private static void DemandExtension(string path, string extension)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
                !Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase))
                throw new AgentAccessException(AgentAccessError.InvalidPath);
        }

        internal sealed record Plan(
            ApplicationShutdownSnapshot Snapshot, ApplicationShutdownChoices Choices,
            IReadOnlyDictionary<Guid, CaptureLocalDestination> Documents, CaptureLocalDestination Capture);
    }
}
