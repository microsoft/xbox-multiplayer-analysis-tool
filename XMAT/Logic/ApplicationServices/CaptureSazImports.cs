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
using System.Windows.Threading;
using XMAT.Models;
using XMAT.NetworkTrace;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;

namespace XMAT.ApplicationServices
{
    internal class CaptureFileImports(
        CaptureAppModel model, Dispatcher dispatcher, CaptureQueries sources, ICaptureFileInputs inputs,
        CaptureType captureType, Func<IDataTable> getTable = null,
        Func<string, Action<string>, CancellationToken, Task> readEtl = null) : ICaptureFileImports
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly HashSet<string> _cleanup = new(StringComparer.OrdinalIgnoreCase);
        private TaskCompletionSource _completion;
        private volatile bool _disposed;

        private bool IsEtl => captureType == CaptureType.NetworkTrace;
        private string Extension => IsEtl ? ".etl" : ".saz";
        private string TableName => IsEtl ? NetworkTraceCaptureMethod.TableName : WebServiceCaptureMethod.TableName;

        public Task<CaptureImportTarget> GetTargetAsync(string fileName, CancellationToken cancellationToken = default) =>
            InvokeAsync(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Target(fileName);
                var replacesStoredRows = await HasStoredRowsAsync(target.DeviceName, cancellationToken);
                if (Target(fileName) != target)
                    throw new QueryException(QueryError.SourceChanged, "The import target changed during inspection.");
                return target with { ReplacesStoredRows = replacesStoredRows };
            });

        private CaptureImportTarget Target(string fileName)
        {
            if (_disposed || model.ActiveDatabase == null)
                throw new CaptureFileException(CaptureFileFailure.Unavailable);
            if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName ||
                !Path.GetExtension(fileName).Equals(Extension, StringComparison.OrdinalIgnoreCase))
                throw new CaptureFileException(CaptureFileFailure.InvalidPath);
            var name = $"{(IsEtl ? "NetTrace" : "Fiddler")} - {fileName}";
            var affected = model.CaptureDeviceContexts.Where(context =>
                context.CaptureType == captureType && context.DeviceName == name).ToArray();
            if (affected.Any(context => !context.IsReadOnly))
                throw new QueryException(QueryError.SourceBusy, "A writable capture uses the imported device name.");
            var target = model.GetDeviceContext(DeviceType.Unknown, name, captureType, true);
            var sourceId = sources.GetSourceId(model.ActiveDatabase);
            var revisions = affected.Select(context => model.CaptureCatalog.DescribeTarget(context))
                .Select(context => new { context.Id, context.Revision }).ToArray();
            var revision = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                new { sourceId, revisions })));
            return new(sourceId, fileName, name,
                target == null ? null : model.CaptureCatalog.DescribeTarget(target).Id, revision, false);
        }

        private Task<bool> HasStoredRowsAsync(string deviceName, CancellationToken cancellationToken)
        {
            var table = model.ActiveDatabase.TableByName(TableName);
            var field = IsEtl ? NetworkTraceCaptureMethod.FieldKey_DeviceName : WebServiceCaptureMethod.FieldKey_DeviceName;
            return table == null ? Task.FromResult(false) : Task.Run(() =>
                table.Dataset.HasRecords(new FieldValue<string>(field, deviceName)), cancellationToken);
        }

        private bool IsCurrent(CaptureImportTarget expected) =>
            Target(expected.FileName) == (expected with { ReplacesStoredRows = false });

        public async Task<CaptureImportReceipt> ImportDesktopAsync(string path, CancellationToken cancellationToken = default)
        {
            var expected = await GetTargetAsync(Path.GetFileName(path), cancellationToken);
            return await ImportAsync(expected, async (destination, token) =>
            {
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await file.CopyToAsync(destination, token);
                return expected.FileName;
            }, long.MaxValue, _ => Task.CompletedTask, cancellationToken);
        }

        public Task<CaptureImportReceipt> ImportInputAsync(
            Guid inputId, CaptureImportTarget expectedTarget, long maximumBytes,
            Func<CancellationToken, Task> beforePublish, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(expectedTarget);
            ArgumentNullException.ThrowIfNull(beforePublish);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
            return ImportAsync(expectedTarget, async (destination, token) =>
                (IsEtl ? await inputs.CopyEtlAsync(inputId, destination, maximumBytes, token) :
                    await inputs.CopySazAsync(inputId, destination, maximumBytes, token)).FileName,
                maximumBytes, beforePublish, cancellationToken);
        }

        private async Task<CaptureImportReceipt> ImportAsync(
            CaptureImportTarget expected, Func<Stream, CancellationToken, Task<string>> copy, long maximumBytes,
            Func<CancellationToken, Task> beforePublish, CancellationToken cancellationToken)
        {
            TaskCompletionSource completion;
            lock (_gate)
            {
                if (_disposed)
                    throw new CaptureFileException(CaptureFileFailure.Unavailable);
                if (_completion is { Task.IsCompleted: false })
                    throw new QueryException(QueryError.SourceBusy, "Another capture import is running.");
                _completion = completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            cancellationToken = lifetime.Token;
            var directory = Path.Combine(Path.GetTempPath(), $"XMAT.CaptureImport.{Guid.NewGuid():N}");
            var path = Path.Combine(directory, "capture" + Extension);
            IDisposable preparation = null;
            try
            {
                await InvokeAsync(() =>
                {
                    Demand(expected, cancellationToken);
                    if (!model.TryBeginCaptureFileLoad(out preparation))
                        throw new QueryException(QueryError.SourceBusy, "Another capture file is being prepared.");
                    return Task.CompletedTask;
                });
                await Task.Run(async () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Directory.CreateDirectory(directory);
                    await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, 65536, FileOptions.Asynchronous);
                    if (await copy(destination, cancellationToken) != expected.FileName)
                        throw new QueryException(QueryError.SourceChanged, "The approved input does not match the import target.");
                    await destination.FlushAsync(cancellationToken);
                }, cancellationToken);
                var context = await InvokeAsync(() =>
                {
                    Demand(expected, cancellationToken);
                    var target = model.GetDeviceContext(DeviceType.Unknown, expected.DeviceName, captureType, true) ??
                        new CaptureDeviceContextModel(DeviceType.Unknown, expected.DeviceName, captureType,
                            true, null, hostListPolicy: model.HostListPolicy, attachLiveRows: false);
                    model.CaptureCatalog.DescribeTarget(target);
                    return Task.FromResult(target);
                });
                var web = context.CaptureController as WebServiceDeviceCaptureController;
                web?.CancelExistingFiddlerImport();
                cancellationToken.ThrowIfCancellationRequested();
                using var imported = await PreparedCaptureImport.PrepareAsync(path, captureType, expected.DeviceName,
                    web, maximumBytes, readEtl, cancellationToken);
                return await InvokeAsync(async () =>
                {
                    Demand(expected, cancellationToken);
                    IDisposable operation = null;
                    var notified = true;
                    try
                    {
                        if (!model.TryBeginCaptureDataOperation(out operation, requireAllCapturesStopped: true))
                            throw new QueryException(QueryError.SourceBusy, "Stop all captures and finish other data/analysis operations first.");
                        await beforePublish(cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!IsCurrent(expected) ||
                            await HasStoredRowsAsync(expected.DeviceName, cancellationToken) != expected.ReplacesStoredRows)
                            throw new QueryException(QueryError.SourceChanged, "The import source or target changed.");
                        var database = model.ActiveDatabase;
                        var table = getTable?.Invoke() ?? database.TableByName(TableName);
                        if (table is not IImportedCaptureTable)
                            throw new CaptureFileException(CaptureFileFailure.Unavailable);
                        await using var backup = await AgentMutationProtection.BeforeCaptureAsync(model, TableName,
                            expected.DeviceName, "import-replacement", AgentAccess.AgentOperationDescriptor.Hash(expected), cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!IsCurrent(expected))
                            throw new QueryException(QueryError.SourceChanged, "The import target changed before replacement.");
                        await Task.Run(() =>
                        {
                            backup.Demand();
                            imported.Persist(table, expected.DeviceName, maximumBytes, cancellationToken);
                        }, cancellationToken);
                        backup.Committed();

                        // The transaction committed: finish publication even if cancellation arrived afterward.
                        imported.Apply(context.CaptureController, table);
                        if (!expected.CaptureId.HasValue)
                            model.RegisterPreparedContexts(new[] { context });
                        model.CaptureCatalog.RecordDataReplacement(context);
                        notified &= imported.Notify(context.CaptureController);
                        notified &= Notify(() => model.NotifyCaptureDataRebound(context, database));
                        if (!expected.CaptureId.HasValue)
                            notified &= Notify(model.NotifyLoadedContexts);
                    }
                    finally
                    {
                        if (operation != null)
                            notified &= Notify(operation.Dispose);
                    }
                    return new CaptureImportReceipt(expected.SourceId, model.CaptureCatalog.DescribeTarget(context).Id,
                        expected.DeviceName, imported.Count, !expected.CaptureId.HasValue, true, notified);
                });
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception) &&
                exception is not (CaptureFileException or QueryException or AgentAccessException or OperationCanceledException))
            {
                throw new CaptureFileException(CaptureFileFailure.InvalidCapture);
            }
            finally
            {
                try
                { await Task.Run(() => Cleanup(directory), CancellationToken.None); }
                finally
                {
                    try
                    {
                        if (preparation != null)
                            await InvokeAsync(() =>
                            {
                                Notify(preparation.Dispose);
                                return Task.CompletedTask;
                            });
                    }
                    finally { completion.TrySetResult(); }
                }
            }
        }

        private void Demand(CaptureImportTarget expected, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrent(expected))
                throw new QueryException(QueryError.SourceChanged, "The import source or target changed.");
            if (!model.CanBeginCaptureDataOperation)
                throw new QueryException(QueryError.SourceBusy, "Stop all captures and finish other data/analysis operations first.");
        }

        private void Cleanup(string directory)
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
                lock (_gate)
                { _cleanup.Remove(directory); }
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                lock (_gate)
                { _cleanup.Add(directory); }
                PublicUtilities.AppLog(LogLevel.ERROR, $"SAZ input cleanup failed: {exception.GetType().Name}");
            }
        }

        private static bool Notify(Action action)
        {
            try
            { action(); return true; }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                PublicUtilities.AppLog(LogLevel.ERROR, $"SAZ import notification failed: {exception.GetType().Name}");
                return false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Task draining;
            lock (_gate)
            {
                _disposed = true;
                draining = _completion?.Task ?? Task.CompletedTask;
            }
            await _lifetime.CancelAsync().ConfigureAwait(false);
            await draining.ConfigureAwait(false);
            string[] pending;
            lock (_gate)
            { pending = _cleanup.ToArray(); }
            await Task.Run(() =>
            {
                foreach (var directory in pending)
                    Cleanup(directory);
            }).ConfigureAwait(false);
        }

        private Task<T> InvokeAsync<T>(Func<Task<T>> action) => ApplicationDispatch.RunOnUiAsync(dispatcher, action);
        private Task InvokeAsync(Func<Task> action) => ApplicationDispatch.RunOnUiAsync(dispatcher, action);

    }

    internal sealed class CaptureSazImports(
        CaptureAppModel model, Dispatcher dispatcher, CaptureQueries sources, ICaptureFileInputs inputs,
        Func<IDataTable> getTable = null) : CaptureFileImports(model, dispatcher, sources, inputs, CaptureType.WebProxy, getTable),
        ICaptureSazImports
    { }

    internal sealed class CaptureEtlImports(
        CaptureAppModel model, Dispatcher dispatcher, CaptureQueries sources, ICaptureFileInputs inputs,
        Func<string, Action<string>, CancellationToken, Task> readEtl = null, Func<IDataTable> getTable = null) :
        CaptureFileImports(model, dispatcher, sources, inputs, CaptureType.NetworkTrace, getTable, readEtl), ICaptureEtlImports
    { }
}
