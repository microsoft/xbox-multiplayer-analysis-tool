// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using XMAT.Models;
using XMAT.NetworkTrace;
using XMAT.NetworkTrace.Models;
using XMAT.NetworkTrace.NTDE;
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
            InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(Target(fileName));
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
                target == null ? null : model.CaptureCatalog.DescribeTarget(target).Id, revision);
        }

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
                            true, null, hostListPolicy: model.HostListPolicy);
                    model.CaptureCatalog.DescribeTarget(target);
                    return Task.FromResult(target);
                });
                using var imported = await Task.Run(async () =>
                {
                    if (!IsEtl && context.CaptureController is WebServiceDeviceCaptureController web)
                    {
                        web.CancelExistingFiddlerImport();
                        cancellationToken.ThrowIfCancellationRequested();
                        return new Prepared(WebServiceCaptureMethod.PrepareFiddlerImport(
                            path, web, maximumBytes, cancellationToken));
                    }
                    var stage = new NetworkTraceEventStage(maximumBytes, cancellationToken);
                    try
                    {
                        await ReadEtlAsync(path, stage.Append, cancellationToken);
                        stage.Complete();
                        var packets = new NetworkTracePacketsCollection();
                        foreach (var record in stage.Records(expected.DeviceName))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            packets.AddFromDataRecord(record);
                        }
                        return new Prepared(stage, packets);
                    }
                    catch { stage.Dispose(); throw; }
                }, cancellationToken);
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
                        if (Target(expected.FileName) != expected)
                            throw new QueryException(QueryError.SourceChanged, "The import source or target changed.");
                        var database = model.ActiveDatabase;
                        var table = getTable?.Invoke() ?? database.TableByName(TableName);
                        if (table is not IImportedCaptureTable)
                            throw new CaptureFileException(CaptureFileFailure.Unavailable);
                        await Task.Run(() => imported.Persist(
                            table, expected.DeviceName, maximumBytes, cancellationToken), cancellationToken);

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

        private async Task ReadEtlAsync(string path, Action<string> append, CancellationToken token)
        {
            if (readEtl != null)
            {
                await readEtl(path, append, token);
                return;
            }
            var engine = new LocalNetworkTraceEngineImpl();
            EventHandler<string> handler = (_, record) => append(record);
            engine.EventRecordAvailable += handler;
            try
            { await engine.ReadEventsFromFileAsync(path, token); }
            finally { engine.EventRecordAvailable -= handler; }
        }

        private void Demand(CaptureImportTarget expected, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Target(expected.FileName) != expected)
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

        private Task<T> InvokeAsync<T>(Func<Task<T>> action) => dispatcher.CheckAccess()
            ? action() : dispatcher.InvokeAsync(action).Task.Unwrap();
        private Task InvokeAsync(Func<Task> action) => dispatcher.CheckAccess()
            ? action() : dispatcher.InvokeAsync(action).Task.Unwrap();

        private sealed class Prepared : IDisposable
        {
            private readonly WebServiceDeviceCaptureController.ImportedCaptures _web;
            private readonly NetworkTraceEventStage _stage;
            private readonly NetworkTracePacketsCollection _packets;
            internal int Count => _web?.Connections.Count ?? _packets.Count;
            internal Prepared(WebServiceDeviceCaptureController.ImportedCaptures web) { _web = web; }
            internal Prepared(NetworkTraceEventStage stage, NetworkTracePacketsCollection packets)
            { _stage = stage; _packets = packets; }
            internal void Persist(IDataTable table, string name, long maximumBytes, CancellationToken token)
            {
                if (_web != null)
                    WebServiceCaptureMethod.PersistFiddlerImport(_web, table, name, maximumBytes, token);
                else
                {
                    var ids = ((IImportedCaptureTable)table).ReplaceImportedRows(
                        new FieldValue<string>(NetworkTraceCaptureMethod.FieldKey_DeviceName, name),
                        Rows(name, maximumBytes, token), token);
                    for (var index = 0; index < _packets.Count; index++)
                        _packets[index].RecordRowId = ids[index];
                }
            }

            private IEnumerable<object[]> Rows(string name, long maximumBytes, CancellationToken token)
            {
                var remaining = maximumBytes;
                foreach (var row in _stage.Rows(name))
                {
                    token.ThrowIfCancellationRequested();
                    var size = 64L + row.Sum(value => value is string text
                        ? Encoding.UTF8.GetByteCount(text) + (long)text.Count(character => character == '"') + 16 : 24);
                    if (size > remaining)
                        throw new QueryException(QueryError.ResultLimit, "The imported trace rows exceed the approved data limit.");
                    remaining -= size;
                    yield return row;
                }
            }
            internal void Apply(IDeviceCaptureController controller, IDataTable table)
            {
                if (_web != null)
                    ((WebServiceDeviceCaptureController)controller).ApplyImportedCaptures(_web, table);
                else
                    ((NetworkTraceCaptureController)controller).ApplyLoadedCaptures(_packets);
            }
            internal bool Notify(IDeviceCaptureController controller)
            {
                if (_web != null)
                    return WebServiceCaptureMethod.NotifyFiddlerImport((WebServiceDeviceCaptureController)controller);
                return CaptureFileImports.Notify(((NetworkTraceCaptureController)controller).NotifyLoadedCaptures);
            }
            public void Dispose()
            {
                try
                { _stage?.Dispose(); }
                catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
                { PublicUtilities.AppLog(LogLevel.ERROR, $"Trace stage cleanup failed: {exception.GetType().Name}"); }
            }
        }
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
