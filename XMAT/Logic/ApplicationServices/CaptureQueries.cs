// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using XMAT.Models;
using XMAT.SharedInterfaces;

namespace XMAT.ApplicationServices
{
    internal sealed class CaptureQueries(CaptureAppModel model, Dispatcher dispatcher) : ICaptureQueries
    {
        private readonly ConditionalWeakTable<IQueryDatabase, Identity> _identities = new();

        public Task<IReadOnlyList<QuerySourceDescriptor>> GetSourcesAsync(CancellationToken cancellationToken = default) =>
            InvokeAsync<IReadOnlyList<QuerySourceDescriptor>>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sources = new List<QuerySourceDescriptor>();
                if (model.ActiveDatabase != null)
                {
                    sources.Add(new(GetId(model.ActiveDatabase), QuerySourceKind.Active,
                        Localization.GetLocalizedString("AGENT_SOURCE_ACTIVE")));
                }
                if (model.LoadedDatabase != null)
                {
                    sources.Add(new(GetId(model.LoadedDatabase), QuerySourceKind.Loaded,
                        !string.IsNullOrEmpty(model.LoadedCaptureDisplayName) ? model.LoadedCaptureDisplayName :
                        string.IsNullOrEmpty(model.LoadedCaptureFilePath)
                            ? Localization.GetLocalizedString("AGENT_SOURCE_LOADED")
                            : Path.GetFileName(model.LoadedCaptureFilePath)));
                }
                return Task.FromResult<IReadOnlyList<QuerySourceDescriptor>>(sources.AsReadOnly());
            });

        public Task<IReadOnlyList<QueryTable>> GetSchemaAsync(Guid sourceId, CancellationToken cancellationToken = default) =>
            ReadSourceAsync(sourceId, source => source.GetQuerySchemaAsync(cancellationToken), cancellationToken);

        public Task<IQuerySession> CreateSessionAsync(Guid sourceId, CancellationToken cancellationToken = default) =>
            ReadSourceAsync(sourceId, source => source.CreateQuerySessionAsync(cancellationToken), cancellationToken);

        internal Task<T> ReadSourceAsync<T>(
            Guid sourceId,
            Func<IQueryDatabase, Task<T>> action,
            CancellationToken cancellationToken) =>
            InvokeAsync(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = Resolve(sourceId);
                if (!model.TryBeginCaptureDataOperation(out var lease, requireAllCapturesStopped: true))
                {
                    throw new QueryException(QueryError.SourceBusy, "Stop all captures and finish other capture/analysis operations first.");
                }
                using (lease)
                { return await action(source); }
            });

        private IQueryDatabase Resolve(Guid id)
        {
            if (model.ActiveDatabase != null && GetId(model.ActiveDatabase) == id)
            {
                return model.ActiveDatabase;
            }
            if (model.LoadedDatabase != null && GetId(model.LoadedDatabase) == id)
            {
                return model.LoadedDatabase;
            }
            throw new QueryException(QueryError.SourceChanged, "The selected source is no longer the active or currently loaded database.");
        }

        private Guid GetId(IQueryDatabase database) => _identities.GetValue(database, _ => new Identity()).Id;
        internal Guid GetSourceId(IQueryDatabase database) => GetId(database);
        private Task<T> InvokeAsync<T>(Func<Task<T>> action) => dispatcher.CheckAccess()
            ? action() : dispatcher.InvokeAsync(action).Task.Unwrap();
        private sealed class Identity { internal Guid Id { get; } = Guid.NewGuid(); }
    }
}
