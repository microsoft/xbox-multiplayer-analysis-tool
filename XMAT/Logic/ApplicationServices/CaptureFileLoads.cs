// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using XMAT.Models;
using XMAT.NetworkTrace;
using XMAT.NetworkTrace.Models;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;

namespace XMAT.ApplicationServices
{
    internal sealed class CaptureFileLoads : ICaptureFileLoads
    {
        private readonly CaptureAppModel _model;
        private readonly Dispatcher _dispatcher;
        private readonly CaptureQueries _sources;
        private readonly ICaptureFileInputs _inputs;
        private readonly Func<string, IReadonlyDatabase> _open;
        private readonly object _gate = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly List<LoadedFile> _files = new();
        private Task _cleanup = Task.CompletedTask;
        private TaskCompletionSource _completion;
        private volatile bool _disposed;
        public bool IsLoading { get { lock (_gate) { return _completion is { Task.IsCompleted: false }; } } }

        internal CaptureFileLoads(
            CaptureAppModel model, Dispatcher dispatcher, CaptureQueries sources, ICaptureFileInputs inputs,
            Func<string, IReadonlyDatabase> open = null)
        {
            _model = model;
            _dispatcher = dispatcher;
            _sources = sources;
            _inputs = inputs;
            _open = open ?? DataAbstractionLayer.DataAbstractionLayer.LoadDatabaseFromFile;
            model.CaptureDeviceContexts.CollectionChanged += ContextsChanged;
            model.PropertyChanged += ModelChanged;
            model.CaptureDataRebound += DataRebound;
        }

        public Task<CaptureLoadState> GetStateAsync(CancellationToken cancellationToken = default) =>
            InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(State());
            });

        private CaptureLoadState State()
        {
            if (_disposed)
                throw new CaptureFileException(CaptureFileFailure.Unavailable);
            var targets = _model.CaptureDeviceContexts.Where(target => target.IsReadOnly)
                .Select(target => _model.CaptureCatalog.DescribeTarget(target))
                .Select(target => new { target.Id, target.Revision }).ToArray();
            var revision = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                new { _model.LoadedCaptureRevision, targets })));
            return new(_model.LoadedDatabase == null ? null : _sources.GetSourceId(_model.LoadedDatabase), revision);
        }

        public async Task<CaptureLoadReceipt> LoadDesktopAsync(string path, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            var state = await GetStateAsync(cancellationToken);
            return await LoadAsync(state, async (destination, token) =>
            {
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await DataAbstractionLayer.DataAbstractionLayer.CopyCaptureInputAsync(path, file, destination, token);
                return Path.GetFileName(path);
            }, path, _ => Task.CompletedTask, cancellationToken);
        }

        public Task<CaptureLoadReceipt> LoadInputAsync(
            Guid inputId, CaptureLoadState expectedState, long maximumBytes,
            Func<CancellationToken, Task> beforePublish, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(expectedState);
            ArgumentNullException.ThrowIfNull(beforePublish);
            return LoadAsync(expectedState, async (destination, token) =>
            {
                var input = await _inputs.CopyCaptureAsync(inputId, destination, maximumBytes, token);
                return input.FileName;
            }, null, beforePublish, cancellationToken);
        }

        private async Task<CaptureLoadReceipt> LoadAsync(
            CaptureLoadState expectedState, Func<Stream, CancellationToken, Task<string>> copy, string originalPath,
            Func<CancellationToken, Task> beforePublish, CancellationToken cancellationToken)
        {
            TaskCompletionSource completion;
            lock (_gate)
            {
                if (_disposed)
                    throw new CaptureFileException(CaptureFileFailure.Unavailable);
                if (_completion is { Task.IsCompleted: false })
                    throw new QueryException(QueryError.SourceBusy, "Another capture file is being prepared.");
                _completion = completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            cancellationToken = lifetime.Token;
            var file = new LoadedFile(Path.Combine(Path.GetTempPath(), $"XMAT.CaptureLoad.{Guid.NewGuid():N}"));
            var adopted = false;
            IDisposable preparation = null;
            try
            {
                await InvokeAsync(() =>
                {
                    DemandState(expectedState, cancellationToken);
                    if (!_model.TryBeginCaptureFileLoad(out preparation))
                        throw new QueryException(QueryError.SourceBusy, "Another capture file is being prepared.");
                    return Task.CompletedTask;
                });
                var name = await Task.Run(async () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Directory.CreateDirectory(file.Directory);
                    await using var destination = new FileStream(file.Path, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, 65536, FileOptions.Asynchronous);
                    var result = await copy(destination, cancellationToken);
                    await destination.FlushAsync(cancellationToken);
                    return result;
                }, cancellationToken);
                var devices = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    file.Database = _open(file.Path);
                    return ReadDevices(file.Database, cancellationToken);
                }, cancellationToken);
                var targets = await InvokeAsync(() =>
                {
                    DemandState(expectedState, cancellationToken);
                    return Task.FromResult(devices.Select(device => new Target(
                        _model.GetDeviceContext(device.Type, device.Name, device.Capture, true) ??
                        new CaptureDeviceContextModel(device.Type, device.Name, device.Capture, true, null,
                            hostListPolicy: _model.HostListPolicy, attachLiveRows: false))).ToArray());
                });
                await Task.Run(() =>
                {
                    foreach (var target in targets)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var tableName = file.Database.TableNames().SingleOrDefault(
                            target.Context.CaptureController.CaptureMethod.OwnsDataTable);
                        var dataset = tableName == null ? null : file.Database.TableByName(tableName).Dataset;
                        if (dataset == null)
                            throw new CaptureFileException(CaptureFileFailure.InvalidCapture);
                        target.Prepare(dataset, cancellationToken);
                    }
                }, cancellationToken);
                return await InvokeAsync(async () =>
                {
                    DemandState(expectedState, cancellationToken);
                    IDisposable lease = null;
                    var notified = true;
                    var sourceId = _sources.GetSourceId(file.Database);
                    ICaptureDeviceContext[] added = null;
                    try
                    {
                        if (!_model.TryBeginCaptureDataOperation(out lease))
                            throw new QueryException(QueryError.SourceBusy, "Finish the current capture/analysis operation first.");
                        await beforePublish(cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (State() != expectedState)
                            throw new QueryException(QueryError.SourceChanged, "The loaded source or read-only contexts changed.");
                        added = targets.Where(target => !_model.CaptureDeviceContexts.Contains(target.Context))
                            .Select(target => target.Context).ToArray();
                        foreach (var existing in _files)
                            existing.Contexts.ExceptWith(targets.Select(target => target.Context));
                        file.Contexts.UnionWith(targets.Select(target => target.Context));
                        _files.Add(file);
                        foreach (var target in targets)
                            target.Apply();
                        _model.CommitLoadedCapture(file.Database, originalPath ?? file.Path, name, added,
                            targets.Select(target => target.Context).ToArray());
                        foreach (var target in targets)
                            _model.CaptureCatalog.RecordDataReplacement(target.Context);
                        adopted = true;
                        foreach (var target in targets)
                            notified &= Notify(target.Notify);
                        foreach (var target in targets)
                            notified &= Notify(() => _model.NotifyCaptureDataRebound(target.Context, file.Database));
                        if (added.Length != 0)
                            notified &= Notify(_model.NotifyLoadedContexts);
                        notified &= Notify(() => _model.RaisePropertyChange(nameof(CaptureAppModel.LoadedDatabase)));
                    }
                    finally
                    {
                        if (lease != null)
                            notified &= Notify(lease.Dispose);
                    }
                    Prune();
                    return new CaptureLoadReceipt(sourceId, added.Length, targets.Length - added.Length, true, notified);
                });
            }
            catch (DataAbstractionLayer.SqLtSchemaException exception)
            {
                throw new CaptureFileException(exception.Failure switch
                {
                    DataAbstractionLayer.SqLtSchemaFailure.Limit => CaptureFileFailure.SchemaLimit,
                    DataAbstractionLayer.SqLtSchemaFailure.Sidecar or
                        DataAbstractionLayer.SqLtSchemaFailure.WriteAheadLog => CaptureFileFailure.LiveDatabase,
                    DataAbstractionLayer.SqLtSchemaFailure.UnsupportedLocation => CaptureFileFailure.UnsupportedLocation,
                    _ => CaptureFileFailure.InvalidCapture
                });
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception) &&
                exception is not (CaptureFileException or QueryException or OperationCanceledException or AgentAccessException))
            {
                throw new CaptureFileException(CaptureFileFailure.InvalidCapture);
            }
            finally
            {
                try
                {
                    if (!adopted)
                        await Task.Run(file.Dispose, CancellationToken.None);
                }
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

        private void DemandState(CaptureLoadState expected, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (State() != expected)
                throw new QueryException(QueryError.SourceChanged, "The loaded source or read-only contexts changed.");
            if (!_model.CanModifyCaptureData)
                throw new QueryException(QueryError.SourceBusy, "Finish the current capture/analysis operation first.");
        }

        private static IReadOnlyList<Device> ReadDevices(IReadonlyDatabase database, CancellationToken cancellationToken)
        {
            var dataset = database.TableByName("CaptureDevices")?.Dataset ??
                throw new CaptureFileException(CaptureFileFailure.InvalidCapture);
            var fields = dataset.FieldDefs.ToDictionary(field => field.Name);
            if (!fields.TryGetValue("DeviceType", out var type) || type.ValueType != FieldValueType.Integer ||
                !fields.TryGetValue("CaptureType", out var capture) || capture.ValueType != FieldValueType.Integer ||
                !fields.TryGetValue("DeviceName", out var name) || name.ValueType != FieldValueType.String)
                throw new CaptureFileException(CaptureFileFailure.InvalidCapture);
            var devices = new HashSet<Device>();
            var result = new List<Device>();
            foreach (var record in dataset.DataRecords)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var deviceType = record.Int("DeviceType");
                var captureType = record.Int("CaptureType");
                var deviceName = record.Str("DeviceName");
                if (deviceType < int.MinValue || deviceType > int.MaxValue || !Enum.IsDefined((DeviceType)deviceType) ||
                    captureType < int.MinValue || captureType > int.MaxValue || !Enum.IsDefined((CaptureType)captureType) ||
                    string.IsNullOrWhiteSpace(deviceName))
                    throw new CaptureFileException(CaptureFileFailure.InvalidCapture);
                var device = new Device((DeviceType)deviceType, deviceName, (CaptureType)captureType);
                if (devices.Add(device))
                    result.Add(device);
            }
            return result;
        }

        private void ContextsChanged(object sender, NotifyCollectionChangedEventArgs args) => Prune();
        private void DataRebound(ICaptureDeviceContext context, IQueryDatabase source)
        {
            foreach (var file in _files)
                if (!ReferenceEquals(file.Database, source))
                    file.Contexts.Remove(context);
            Prune();
        }
        private void ModelChanged(object sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(CaptureAppModel.LoadedDatabase))
                Prune();
        }

        private void Prune()
        {
            foreach (var file in _files.ToArray())
            {
                if (ReferenceEquals(file.Database, _model.LoadedDatabase) ||
                    (file.Database is DataAbstractionLayer.SqLtReadonlyDatabase native
                        ? DataAbstractionLayer.LoadedCaptureDatabaseRegistry.ReferenceCount(native) != 0
                        : file.Contexts.Any(_model.CaptureDeviceContexts.Contains)))
                    continue;
                _files.Remove(file);
                _cleanup = Task.WhenAll(_cleanup, Task.Run(file.Dispose));
            }
        }

        private static bool Notify(Action action)
        {
            try
            { action(); return true; }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                PublicUtilities.AppLog(LogLevel.ERROR, $"Capture load notification failed: {exception.GetType().Name}");
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
            var cleanup = await InvokeAsync(() =>
            {
                _model.CaptureDeviceContexts.CollectionChanged -= ContextsChanged;
                _model.PropertyChanged -= ModelChanged;
                _model.CaptureDataRebound -= DataRebound;
                foreach (var file in _files.ToArray())
                {
                    foreach (var context in file.Contexts)
                        if (ReferenceEquals(DataAbstractionLayer.LoadedCaptureDatabaseRegistry.DatabaseFor(
                            context.CaptureController), file.Database))
                            DataAbstractionLayer.LoadedCaptureDatabaseRegistry.Release(context.CaptureController);
                    _model.ReleaseLoadedWorkspace(file.Database);
                    _cleanup = Task.WhenAll(_cleanup, Task.Run(file.Dispose));
                }
                _files.Clear();
                return Task.FromResult(_cleanup);
            }).ConfigureAwait(false);
            await cleanup.ConfigureAwait(false);
        }

        private Task<T> InvokeAsync<T>(Func<Task<T>> action) => App.RunOnUiAsync(_dispatcher, action);
        private Task InvokeAsync(Func<Task> action) => App.RunOnUiAsync(_dispatcher, action);
        private sealed record Device(DeviceType Type, string Name, CaptureType Capture);

        private sealed class Target(ICaptureDeviceContext context)
        {
            internal ICaptureDeviceContext Context { get; } = context;
            private WebServiceDeviceCaptureController.LoadedCaptures _web;
            private NetworkTracePacketsCollection _packets;
            internal void Prepare(IDataset dataset, CancellationToken token)
            {
                switch (Context.CaptureController)
                {
                    case WebServiceDeviceCaptureController web:
                        _web = web.PrepareLoadedCaptures(dataset, token);
                        break;
                    case NetworkTraceCaptureController trace:
                        _packets = trace.PrepareLoadedCaptures(dataset, token);
                        break;
                    default:
                        throw new CaptureFileException(CaptureFileFailure.InvalidCapture);
                }
            }
            internal void Apply()
            {
                if (Context.CaptureController is WebServiceDeviceCaptureController web)
                    web.ApplyLoadedCaptures(_web);
                else
                    ((NetworkTraceCaptureController)Context.CaptureController).ApplyLoadedCaptures(_packets);
            }
            internal void Notify()
            {
                if (Context.CaptureController is WebServiceDeviceCaptureController web)
                    web.NotifyLoadedCaptures();
                else
                    ((NetworkTraceCaptureController)Context.CaptureController).NotifyLoadedCaptures();
            }
        }

        private sealed class LoadedFile(string directory) : IDisposable
        {
            internal string Directory { get; } = directory;
            internal string Path { get; } = System.IO.Path.Combine(directory, "capture.xmatcap");
            internal IReadonlyDatabase Database { get; set; }
            internal HashSet<ICaptureDeviceContext> Contexts { get; } = new(ReferenceEqualityComparer.Instance);
            public void Dispose()
            {
                (Database as IDisposable)?.Dispose();
                if (System.IO.Directory.Exists(Directory))
                    System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
    }
}
