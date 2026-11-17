// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using XMAT.Operations;

namespace XMAT.AgentAccess
{
    internal sealed record AgentAccessPolicySnapshot(
        AgentAccessSettings Settings, AgentPolicyReadStatus ReadStatus, long AccessRevision,
        long DataRevision, bool HasUnsavedRestriction, AgentPolicyFailure? SaveFailure);

    internal sealed class AgentAccessPolicyService : IAsyncDisposable, IOperation<AgentAccessSettings, AgentAccessPolicySnapshot>
    {
        private readonly object _gate = new();
        private readonly SemaphoreSlim _saving = new(1, 1);
        private readonly AgentAccessSettingsStore _store;
        private readonly Channel<byte> _reloads = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });
        private AgentAccessPolicySnapshot _current;
        private FileSystemWatcher _watcher;
        private Task _watching = Task.CompletedTask;
        private Task _disposing;
        private long _failedWriteRevision = -1;
        private bool _disposed;

        internal event EventHandler Changed;
        internal string SettingsDirectory => Path.GetDirectoryName(_store.Path);

        internal AgentAccessPolicySnapshot Current
        {
            get { lock (_gate) { return _current; } }
        }

        internal AgentAccessPolicyService(AgentAccessSettingsStore store = null)
        {
            _store = store ?? new();
            var read = _store.Read();
            _current = new(read.Settings, read.Status, 1, 1, false, null);
        }

        public async Task<OperationResult<AgentAccessPolicySnapshot>> ExecuteAsync(
            AgentAccessSettings desired, OperationContext context, CancellationToken cancellationToken)
        {
            if (context?.WorkingDirectory == null ||
                !string.Equals(Path.GetFullPath(context.WorkingDirectory), SettingsDirectory, StringComparison.OrdinalIgnoreCase))
                return new(OperationOutcome.InvalidInput, Current with { SaveFailure = AgentPolicyFailure.InvalidPolicy }, []);
            try
            {
                return OperationResult<AgentAccessPolicySnapshot>.Success(
                    await SaveAsync(desired, desired?.Revision ?? -1, cancellationToken));
            }
            catch (AgentPolicyException exception)
            {
                return new(exception.Failure == AgentPolicyFailure.PolicyChanged ? OperationOutcome.Conflict : OperationOutcome.Failed,
                    Current with { SaveFailure = exception.Failure }, []);
            }
            catch (OperationCanceledException)
            { return new(OperationOutcome.Cancelled, Current with { SaveFailure = AgentPolicyFailure.SaveFailed }, []); }
        }

        internal void StartWatching()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_watcher != null)
                    return;
                var directory = Path.GetDirectoryName(_store.Path);
                Directory.CreateDirectory(directory);
                _watcher = new FileSystemWatcher(directory, Path.GetFileName(_store.Path))
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                _watcher.Changed += OnFileChanged;
                _watcher.Created += OnFileChanged;
                _watcher.Deleted += OnFileChanged;
                _watcher.Renamed += OnFileChanged;
                _watcher.Error += (_, _) => _reloads.Writer.TryWrite(0);
                _watching = Task.Run(WatchAsync);
                _watcher.EnableRaisingEvents = true;
            }
            Reload();
        }

        internal async Task<AgentAccessPolicySnapshot> SaveAsync(
            AgentAccessSettings desired, long expectedRevision, CancellationToken cancellationToken = default)
        {
            if (desired?.IsValid != true)
                throw new AgentPolicyException(AgentPolicyFailure.InvalidPolicy);
            await _saving.WaitAsync(cancellationToken);
            try
            {
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (_current.Settings.Revision != expectedRevision)
                        throw new AgentPolicyException(AgentPolicyFailure.PolicyChanged);
                    _failedWriteRevision = expectedRevision;
                    var restrictive = _current.Settings.RestrictTo(desired);
                    var changed = restrictive != _current.Settings;
                    PublishLocked(restrictive, _current.ReadStatus,
                        _current.HasUnsavedRestriction || changed, null);
                }
                NotifyChanged();
                try
                {
                    var committed = await _store.SaveAsync(desired, expectedRevision, cancellationToken);
                    lock (_gate)
                    {
                        _failedWriteRevision = -1;
                        PublishLocked(committed, AgentPolicyReadStatus.Loaded, false, null);
                    }
                    NotifyChanged();
                    return Current;
                }
                catch (Exception exception) when (exception is AgentPolicyException or OperationCanceledException)
                {
                    lock (_gate)
                    {
                        _failedWriteRevision = expectedRevision;
                        _current = _current with
                        {
                            SaveFailure = exception is AgentPolicyException failure
                                ? failure.Failure : AgentPolicyFailure.SaveFailed
                        };
                    }
                    NotifyChanged();
                    throw;
                }
            }
            finally { _saving.Release(); }
        }

        internal void Reload()
        {
            var read = _store.Read();
            var changed = false;
            lock (_gate)
            {
                if (_disposed)
                    return;
                if (read.Status == AgentPolicyReadStatus.Loaded &&
                    (read.Settings.Revision < _current.Settings.Revision ||
                    read.Settings.Revision <= _failedWriteRevision))
                    return;
                var effective = _current.HasUnsavedRestriction && read.Status != AgentPolicyReadStatus.Loaded
                    ? _current.Settings.RestrictTo(read.Settings) : read.Settings;
                changed = PublishLocked(effective, read.Status,
                    _current.HasUnsavedRestriction && read.Status != AgentPolicyReadStatus.Loaded,
                    _current.SaveFailure);
            }
            if (changed)
                NotifyChanged();
        }

        private bool PublishLocked(
            AgentAccessSettings settings, AgentPolicyReadStatus status, bool unsaved, AgentPolicyFailure? failure)
        {
            var previous = _current;
            var accessChanged = previous.Settings.Enabled != settings.Enabled ||
                previous.Settings.Actions != settings.Actions || previous.Settings.DataSharing != settings.DataSharing;
            var dataChanged = previous.Settings.Enabled != settings.Enabled ||
                previous.Settings.DataSharing != settings.DataSharing;
            _current = new(settings, status, previous.AccessRevision + (accessChanged ? 1 : 0),
                previous.DataRevision + (dataChanged ? 1 : 0), unsaved, failure);
            return _current != previous;
        }

        private void OnFileChanged(object sender, FileSystemEventArgs args) => _reloads.Writer.TryWrite(0);

        private async Task WatchAsync()
        {
            await foreach (var _ in _reloads.Reader.ReadAllAsync())
            {
                await Task.Delay(25);
                Reload();
            }
        }

        private void NotifyChanged()
        {
            foreach (EventHandler handler in Changed?.GetInvocationList() ?? [])
            {
                try
                { handler(this, EventArgs.Empty); }
                catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
                { Trace.TraceError("An agent policy observer failed."); }
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                if (_disposing != null)
                    return new(_disposing);
                _disposed = true;
                _watcher?.Dispose();
                _reloads.Writer.TryComplete();
                _disposing = DisposeCoreAsync();
                return new(_disposing);
            }
        }

        private async Task DisposeCoreAsync()
        {
            await _watching;
            await _saving.WaitAsync();
            _saving.Release();
        }
    }
}
