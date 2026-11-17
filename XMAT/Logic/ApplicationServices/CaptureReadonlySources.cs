// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XMAT.NetworkTrace;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;

namespace XMAT.ApplicationServices
{
    internal sealed class CaptureReadonlySources(
        ICaptureFileInputs inputs, ICaptureFileLoads loads,
        Func<string, Action<string>, CancellationToken, Task> readEtl = null) : ICaptureReadonlySources
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly HashSet<string> _cleanup = new(StringComparer.OrdinalIgnoreCase);
        private TaskCompletionSource _completion;
        private bool _disposed;

        public async Task<CaptureOpenReceipt> OpenInputAsync(
            Guid inputId, CaptureLoadState expectedState, long maximumBytes,
            Func<CancellationToken, Task> beforePublish, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(expectedState);
            ArgumentNullException.ThrowIfNull(beforePublish);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
            TaskCompletionSource completion;
            lock (_gate)
            {
                if (_disposed)
                    throw new CaptureFileException(CaptureFileFailure.Unavailable);
                if (_completion is { Task.IsCompleted: false })
                    throw new QueryException(QueryError.SourceBusy, "Another read-only source is being prepared.");
                _completion = completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            cancellationToken = lifetime.Token;
            string directory = null;
            try
            {
                if (await loads.GetStateAsync(cancellationToken) != expectedState)
                    throw new QueryException(QueryError.SourceChanged, "The loaded source changed.");
                var input = inputs.GetInputs().SingleOrDefault(item => item.Id == inputId)
                    ?? throw new CaptureFileException(CaptureFileFailure.Unavailable);
                CaptureLoadReceipt receipt;
                if (input.Kind == CaptureInputKind.CaptureDatabase)
                    receipt = await loads.LoadInputAsync(inputId, expectedState, maximumBytes, beforePublish, cancellationToken);
                else
                {
                    if (input.Kind is not (CaptureInputKind.FiddlerSaz or CaptureInputKind.NetworkTraceEtl))
                        throw new CaptureFileException(CaptureFileFailure.InvalidPath);
                    directory = Path.Combine(Path.GetTempPath(), "XMAT.ReadonlySource." + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(directory);
                    var etl = input.Kind == CaptureInputKind.NetworkTraceEtl;
                    var path = Path.Combine(directory, etl ? "input.etl" : "input.saz");
                    await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                    {
                        if (etl)
                            await inputs.CopyEtlAsync(inputId, file, maximumBytes, cancellationToken);
                        else
                            await inputs.CopySazAsync(inputId, file, maximumBytes, cancellationToken);
                        await file.FlushAsync(cancellationToken);
                    }
                    var type = etl ? CaptureType.NetworkTrace : CaptureType.WebProxy;
                    var name = (etl ? "NetTrace - " : "Fiddler - ") + input.FileName;
                    using var prepared = await PreparedCaptureImport.PrepareAsync(
                        path, type, name, null, maximumBytes, readEtl, cancellationToken);
                    var nativePath = Path.Combine(directory, "source.xmatcap");
                    await Task.Run(() =>
                    {
                        var database = DataAbstractionLayer.DataAbstractionLayer.CreateWorkingDatabase();
                        try
                        {
                            CaptureDatabaseSchema.CreateDevices(database).AddRow((long)DeviceType.Unknown, name, (long)type);
                            var table = etl ? NetworkTraceCaptureMethod.CreateDataTable(database) : WebServiceCaptureMethod.CreateDataTable(database);
                            prepared.Persist(table, name, maximumBytes, cancellationToken);
                            database.CreateSnapshotToFile(nativePath, maximumBytes, cancellationToken);
                        }
                        finally { database.Shutdown(); }
                    }, cancellationToken);
                    await inputs.ValidateAsync(inputId, maximumBytes, cancellationToken);
                    receipt = await loads.LoadPreparedAsync(nativePath, input.FileName, inputs.GetOriginalPath(inputId),
                        expectedState, beforePublish, cancellationToken);
                }
                return new(receipt.SourceId, input.Kind, input.Sha256, input.Length,
                    receipt.AddedContexts, receipt.UpdatedContexts, receipt.Published, receipt.ViewsNotified);
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception) &&
                exception is not (CaptureFileException or QueryException or AgentAccessException or OperationCanceledException))
            { throw new CaptureFileException(CaptureFileFailure.InvalidCapture); }
            finally
            {
                try
                {
                    if (directory != null)
                        await Task.Run(() => Cleanup(directory), CancellationToken.None);
                }
                finally { completion.TrySetResult(); }
            }
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
                PublicUtilities.AppLog(LogLevel.ERROR, $"Read-only source cleanup failed: {exception.GetType().Name}");
            }
        }

        public async ValueTask DisposeAsync()
        {
            Task pending;
            lock (_gate)
            {
                _disposed = true;
                pending = _completion?.Task ?? Task.CompletedTask;
            }
            await _lifetime.CancelAsync();
            await pending;
            string[] cleanup;
            lock (_gate)
            { cleanup = _cleanup.ToArray(); }
            await Task.Run(() =>
            {
                foreach (var directory in cleanup)
                    Cleanup(directory);
            });
        }
    }
}
