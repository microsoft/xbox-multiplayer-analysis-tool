// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XMAT.AgentAccess;
using XMAT.Models;
using XMAT.SharedInterfaces;

namespace XMAT.ApplicationServices
{
    internal sealed class AgentMutationProtection : IDisposable
    {
        private static readonly AsyncLocal<AgentMutationProtection> Ambient = new();
        private readonly AgentMutationProtection _previous;
        private readonly AgentAccessPolicyService _policy;
        private readonly AgentBackupStore _store;
        private readonly object _gate = new();
        private readonly List<AgentBackupReceipt> _backups = new();
        private readonly Guid _operationId;
        private int _references = 1;

        internal static AgentMutationProtection Current => Ambient.Value;
        internal IReadOnlyList<AgentBackupReceipt> Backups
        {
            get { lock (_gate) { return _backups.ToArray(); } }
        }

        internal AgentMutationProtection(Guid operationId, AgentAccessPolicyService policy, AgentBackupStore store)
        {
            _previous = Ambient.Value;
            _operationId = operationId;
            _policy = policy;
            _store = store;
            Ambient.Value = this;
        }

        internal static async Task<Protection> BeforeCaptureAsync(
            CaptureAppModel model, string tableName, string deviceName, string reason,
            string revision, CancellationToken cancellationToken)
        {
            var current = Current;
            if (current?._policy?.Current.Settings.AutomaticBackups != true)
                return new(current, null);
            var database = model.ActiveDatabase;
            var table = database?.TableByName(tableName);
            if (table == null || !await Task.Run(() => table.Dataset.HasRecords(
                new FieldValue<string>("DeviceName", deviceName)), cancellationToken))
                return new(null, null);
            var sourceId = ((CaptureQueries)model.CaptureQueries).GetSourceId(database);
            if (database is not ICaptureDatabaseBackup source)
                throw new AgentAccessException(AgentAccessError.BackupFailed);
            var receipt = await current.CreateAsync(() => current._store.CaptureAsync(current._operationId, sourceId,
                revision, reason, tableName + ":" + deviceName, source, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(database, model.ActiveDatabase))
                throw new AgentAccessException(AgentAccessError.SourceChanged);
            return new(current, receipt);
        }

        internal async Task<Protection> BeforeFileAsync(string path, Stream previous, CancellationToken cancellationToken)
        {
            if (_policy?.Current.Settings.AutomaticBackups != true)
                return new(this, null);
            var receipt = await CreateAsync(() => _store.FileAsync(_operationId, path, previous, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            return new(this, receipt);
        }

        private async Task<AgentBackupReceipt> CreateAsync(Func<Task<AgentBackupReceipt>> create)
        {
            try
            {
                var receipt = await create();
                lock (_gate)
                {
                    _backups.RemoveAll(item => item.BackupId == receipt.BackupId);
                    _backups.Add(receipt);
                }
                return receipt;
            }
            catch (AgentBackupException exception)
            {
                if (exception.Prepared != null)
                    lock (_gate)
                    { _backups.Add(exception.Prepared); }
                throw new AgentAccessException(AgentAccessError.BackupFailed, AgentJson.Encode(new
                {
                    error = AgentAccessError.BackupFailed,
                    protectedChangeStarted = false,
                    backups = Backups,
                    failureType = exception.InnerException?.GetType().Name,
                    failureReason = (exception.InnerException as CaptureFileException)?.Failure.ToString(),
                    failureStage = exception.InnerException?.TargetSite?.Name,
                    failureNativeCode = (exception.InnerException as CaptureFileException)?.NativeErrorCode ??
                        (exception.InnerException as System.ComponentModel.Win32Exception)?.NativeErrorCode,
                    failureCode = exception.InnerException?.HResult
                }));
            }
        }

        internal static string Revision(long revision) => revision.ToString(CultureInfo.InvariantCulture);

        internal IDisposable Retain()
        {
            Interlocked.Increment(ref _references);
            return new Retained(this);
        }

        internal static async Task<Protection> BeforeDatabaseAsync(
            CaptureAppModel model, string reason, string revision, CancellationToken cancellationToken)
        {
            var current = Current;
            if (current?._policy?.Current.Settings.AutomaticBackups != true)
                return new(current, null);
            var database = model.ActiveDatabase;
            if (database is not ICaptureDatabaseBackup source)
                throw new AgentAccessException(AgentAccessError.BackupFailed);
            var sourceId = ((CaptureQueries)model.CaptureQueries).GetSourceId(database);
            var receipt = await current.CreateAsync(() => current._store.CaptureAsync(current._operationId, sourceId,
                revision, reason, "all-active-source-records", source, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(database, model.ActiveDatabase))
                throw new AgentAccessException(AgentAccessError.SourceChanged);
            return new(current, receipt);
        }

        public void Dispose()
        {
            Ambient.Value = _previous;
            Release();
        }

        private void Release()
        {
            if (Interlocked.Decrement(ref _references) == 0)
                foreach (var backup in Backups)
                    backup.Pin?.Dispose();
        }

        private sealed class Retained(AgentMutationProtection owner) : IDisposable
        {
            private AgentMutationProtection _owner = owner;
            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
        }

        internal sealed class Protection(AgentMutationProtection owner, AgentBackupReceipt receipt) : IAsyncDisposable
        {
            private bool _committed;
            internal void Demand()
            {
                if (owner?._policy?.Current.Settings.AutomaticBackups == true && receipt == null)
                    throw new AgentAccessException(AgentAccessError.BackupFailed);
            }
            internal void Committed() => _committed = true;
            public async ValueTask DisposeAsync()
            {
                if (receipt == null || !_committed)
                    return;
                var completed = await owner._store.CommittedAsync(receipt);
                lock (owner._gate)
                {
                    var index = owner._backups.FindIndex(item => item.BackupId == receipt.BackupId);
                    if (index >= 0)
                        owner._backups[index] = completed;
                }
            }
        }
    }
}
