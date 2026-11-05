// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using XMAT.Operations.Processes;

namespace XMAT.WebServiceCapture.Proxy
{
    internal sealed class ClientProcessResolver : IAsyncDisposable
    {
        private readonly ITcpOwnerTableProvider _tcpOwners;
        private readonly IProcessIdentityProvider _processes;
        private readonly ILocalAddressProvider _localAddresses;
        private readonly IClientProcessResolverClock _clock;
        private readonly object _syncRoot = new();
        private readonly LinkedList<Pending> _pending = new();
        private readonly SemaphoreSlim _signal = new(0);
        private readonly object _applicationSyncRoot = new();
        private readonly Queue<ApplicationWork> _applications = new();
        private readonly Dictionary<ClientProcessContext, ApplicationWork> _queuedApplications = new(
            ReferenceEqualityComparer.Instance);
        private readonly SemaphoreSlim _applicationSignal = new(0);
        private readonly Dictionary<Guid, RunState> _runs = new();
        private readonly HashSet<Guid> _sealedRuns = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _worker;
        private readonly Task _applicationWorker;
        private IReadOnlyDictionary<int, ProcessIdentityData> _fallback;
        private readonly Dictionary<ProcessCacheKey, ClientProcessInfo> _processCache = new();
        private readonly Queue<ProcessCacheKey> _processCacheOrder = new();
        private long _fallbackAtTimestamp;
        private bool _fallbackTimestampInitialized;
        private long _dutyWindowStarted;
        private TimeSpan _dutyWork;
        private bool _dutyWindowInitialized;
        private bool _disposed;
        private int _outstanding;

        internal ClientProcessResolver(
            ITcpOwnerTableProvider tcpOwners,
            IProcessIdentityProvider processes,
            ILocalAddressProvider localAddresses,
            IClientProcessResolverClock clock,
            ClientProcessResolverOptions options = null)
        {
            ArgumentNullException.ThrowIfNull(tcpOwners);
            ArgumentNullException.ThrowIfNull(processes);
            ArgumentNullException.ThrowIfNull(localAddresses);
            ArgumentNullException.ThrowIfNull(clock);
            Options = options ?? ClientProcessResolverOptions.Default;
            ValidateOptions(Options);
            _tcpOwners = tcpOwners;
            _processes = processes;
            _localAddresses = localAddresses;
            _clock = clock;
            _worker = Task.Factory.StartNew(
                Worker,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            _applicationWorker = Task.Factory.StartNew(
                ApplicationWorker,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        internal ClientProcessResolverOptions Options { get; }
        internal IClientProcessResolverClock Clock => _clock;
        internal int PendingCount
        {
            get
            {
                lock (_syncRoot)
                {
                    return _outstanding;
                }
            }
        }

        internal bool TryEnqueue(ClientProcessContext connection)
        {
            ArgumentNullException.ThrowIfNull(connection);
            bool? local = IsLocal(connection);
            if (!local.HasValue)
            {
                PublishUnresolved(connection, 0);
                return true;
            }
            if (!local.Value)
            {
                connection.Publish(new ClientProcessInfo(string.Empty, 0, null, ClientProcessStatus.Remote));
                return true;
            }
            if (!TryNormalize(connection.ClientEndPoint, out _) || !TryNormalize(connection.ProxyEndPoint, out _))
            {
                PublishUnresolved(connection, 0);
                return true;
            }
            lock (_syncRoot)
            {
                if (_disposed || _sealedRuns.Contains(connection.RunId) || _outstanding >= Options.QueueCapacity)
                {
                    PublishUnresolved(connection, 0);
                    return false;
                }
                if (!_runs.TryGetValue(connection.RunId, out RunState run))
                {
                    run = new RunState();
                    _runs.Add(connection.RunId, run);
                }
                if (run.Outstanding == 0)
                {
                    run.Drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                var pending = new Pending(connection, connection.ReservePublicationOrder());
                pending.QueueNode = _pending.AddLast(pending);
                run.Pending.Add(pending);
                run.Outstanding++;
                _outstanding++;
            }
            _signal.Release();
            return true;
        }

        internal Task DrainAsync(Guid runId, CancellationToken cancellationToken = default)
        {
            Task drained;
            lock (_syncRoot)
            {
                drained = _runs.TryGetValue(runId, out RunState run) &&
                    run.Outstanding != 0 ? run.Drained.Task : Task.CompletedTask;
            }
            return DrainRunAsync(runId, drained, cancellationToken);
        }

        internal void SealRun(Guid runId)
        {
            lock (_syncRoot)
            {
                _sealedRuns.Add(runId);
            }
        }

        internal void UnsealRun(Guid runId)
        {
            lock (_syncRoot)
            {
                _sealedRuns.Remove(runId);
            }
        }

        internal void ReleaseRun(Guid runId)
        {
            lock (_syncRoot)
            {
                if (!_runs.TryGetValue(runId, out RunState run) || run.Outstanding == 0)
                {
                    _runs.Remove(runId);
                    _sealedRuns.Remove(runId);
                }
            }
        }

        private async Task DrainRunAsync(Guid runId, Task drained, CancellationToken cancellationToken)
        {
            try
            {
                await _clock.WaitAsync(drained, Options.DrainBudget, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                RetireRun(runId);
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                RetireRun(runId);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
            }
            await _stopping.CancelAsync().ConfigureAwait(false);
            _signal.Release();
            _applicationSignal.Release();
            try
            {
                await Task.WhenAll(_worker, _applicationWorker).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is the resolver's normal shutdown path.
            }
            finally
            {
                lock (_syncRoot)
                {
                    _pending.Clear();
                    _outstanding = 0;
                    foreach (RunState run in _runs.Values)
                    {
                        run.Outstanding = 0;
                        run.Pending.Clear();
                        run.Drained.TrySetResult();
                    }
                }
                lock (_applicationSyncRoot)
                {
                    _applications.Clear();
                    _queuedApplications.Clear();
                }
                _signal.Dispose();
                _applicationSignal.Dispose();
                _stopping.Dispose();
                (_localAddresses as IDisposable)?.Dispose();
            }
        }

        private void Worker()
        {
            CancellationToken cancellationToken = _stopping.Token;
            while (true)
            {
                _signal.Wait(cancellationToken);
                List<Pending> batch;
                lock (_syncRoot)
                {
                    if (_disposed && _pending.Count == 0)
                    {
                        return;
                    }
                    batch = new List<Pending>(_pending.Count);
                    while (_pending.Count != 0)
                    {
                        Pending item = _pending.First.Value;
                        _pending.RemoveFirst();
                        item.QueueNode = null;
                        batch.Add(item);
                    }
                }
                if (batch.Count == 0)
                {
                    continue;
                }
                try
                {
                    ResolveBatchAsync(batch, cancellationToken).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    foreach (Pending pending in batch)
                    {
                        RetirePending(pending);
                    }
                    throw;
                }
                catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
                {
                    foreach (Pending pending in batch)
                    {
                        QueueApplication(pending, Unresolved(0));
                    }
                }
            }
        }

        private async Task ResolveBatchAsync(List<Pending> pending, CancellationToken cancellationToken)
        {
            List<Pending> current = pending.Where(item => !item.IsRetired).ToList();
            while (current.Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long workStarted = _clock.Timestamp;
                IReadOnlyList<TcpOwnerRow> rows;
                try
                {
                    rows = _tcpOwners.Snapshot(AddressFamily.InterNetwork) ?? Array.Empty<TcpOwnerRow>();
                }
                catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
                {
                    foreach (Pending item in current)
                    {
                        QueueApplication(item, Unresolved(0));
                    }
                    return;
                }
                var retry = new List<Pending>();
                var resolved = new List<ResolvedWork>();
                IReadOnlyDictionary<TcpTuple, int> owners = IndexOwners(rows, current);
                var processLookups = new Dictionary<int, ProcessLookup>();
                foreach (Pending item in current)
                {
                    if (item.IsRetired)
                    {
                        continue;
                    }
                    int processId = FindOwner(owners, item.Context);
                    if (processId > 0)
                    {
                        IdentityResolution resolution = ResolveIdentity(item.Context, processId, processLookups);
                        if (resolution.Retry && WithinResolutionBudget(item.Context))
                        {
                            retry.Add(item);
                        }
                        else
                        {
                            resolved.Add(new ResolvedWork(item, resolution.Info));
                        }
                    }
                    else if (WithinResolutionBudget(item.Context))
                    {
                        retry.Add(item);
                    }
                    else
                    {
                        resolved.Add(new ResolvedWork(item, Unresolved(0)));
                    }
                }
                TimeSpan nativeWork = _clock.ElapsedSince(workStarted);
                TimeSpan dutyDelay = ComputeDutyDelay(workStarted, nativeWork);
                foreach (ResolvedWork work in resolved)
                {
                    QueueApplication(work.Pending, work.Info);
                }
                if (dutyDelay > TimeSpan.Zero)
                {
                    await _clock.DelayAsync(dutyDelay, cancellationToken).ConfigureAwait(false);
                    _dutyWindowStarted = _clock.Timestamp;
                    _dutyWork = TimeSpan.Zero;
                }
                if (retry.Count == 0)
                {
                    return;
                }
                await _clock.DelayAsync(Options.RetryDelay, cancellationToken).ConfigureAwait(false);
                current = retry.Where(item => !item.IsRetired).ToList();
            }
        }

        private IdentityResolution ResolveIdentity(
            ClientProcessContext context,
            int processId,
            IDictionary<int, ProcessLookup> lookups)
        {
            if (!lookups.TryGetValue(processId, out ProcessLookup lookup))
            {
                lookup = LookupProcess(processId);
                lookups.Add(processId, lookup);
            }
            ProcessIdentityData identity = lookup.Identity;
            if (lookup.AccessDenied &&
                (!lookup.SnapshotAtTimestamp.HasValue || SnapshotPrecedesAccept(lookup.SnapshotAtTimestamp.Value,
                    context)))
            {
                return new IdentityResolution(Unresolved(processId), Retry: true);
            }
            if (lookup.Status != ProcessIdentityQueryStatus.Found ||
                identity == null ||
                identity.ProcessId != processId)
            {
                return new IdentityResolution(Unresolved(processId), Retry: lookup.AccessDenied);
            }
            if (identity.StartTimeUtc > context.AcceptedAtUtc)
            {
                return new IdentityResolution(Unresolved(processId), Retry: false);
            }
            var cacheKey = new ProcessCacheKey(processId, identity.StartTimeUtc.UtcDateTime.Ticks);
            if (_processCache.TryGetValue(cacheKey, out ClientProcessInfo cached))
            {
                return new IdentityResolution(cached, Retry: false);
            }
            ClientProcessInfo result = ClientProcessRecordCodec.FromValues(
                identity.Name,
                processId,
                identity.StartTimeUtc.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                "local");
            if (result.Status == ClientProcessStatus.NotRecorded)
            {
                result = new ClientProcessInfo(
                    string.Empty,
                    processId,
                    identity.StartTimeUtc,
                    ClientProcessStatus.Local);
            }
            AddToCache(cacheKey, result);
            return new IdentityResolution(result, Retry: false);
        }

        private ProcessLookup LookupProcess(int processId)
        {
            ProcessIdentityQueryStatus status;
            ProcessIdentityData identity;
            try
            {
                status = _processes.Query(processId, out identity);
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                return new ProcessLookup(ProcessIdentityQueryStatus.Missing, null, null, AccessDenied: false);
            }
            long? snapshotAtTimestamp = null;
            bool accessDenied = status == ProcessIdentityQueryStatus.AccessDenied;
            if (status == ProcessIdentityQueryStatus.AccessDenied)
            {
                identity = FindFallback(processId);
                snapshotAtTimestamp = _fallbackAtTimestamp;
                status = identity == null ? ProcessIdentityQueryStatus.Missing : ProcessIdentityQueryStatus.Found;
            }
            return new ProcessLookup(status, identity, snapshotAtTimestamp, accessDenied);
        }

        private bool WithinResolutionBudget(ClientProcessContext context)
        {
            TimeSpan elapsed = _clock.ElapsedSince(context.AcceptedAtTimestamp);
            return elapsed >= TimeSpan.Zero && elapsed <Options.ResolutionBudget;
        }

        private static bool SnapshotPrecedesAccept(long snapshotTimestamp, ClientProcessContext context) =>
            snapshotTimestamp < context.AcceptedAtTimestamp;

        private TimeSpan ComputeDutyDelay(long workStarted, TimeSpan nativeWork)
        {
            if (!_dutyWindowInitialized || _clock.ElapsedSince(_dutyWindowStarted) >= Options.DutyCycleWindow)
            {
                _dutyWindowStarted = workStarted;
                _dutyWork = TimeSpan.Zero;
                _dutyWindowInitialized = true;
            }
            _dutyWork += nativeWork;
            if (_dutyWork <Options.MaximumWorkPerWindow)
            {
                return TimeSpan.Zero;
            }

            TimeSpan remaining = Options.DutyCycleWindow - _clock.ElapsedSince(_dutyWindowStarted);
            TimeSpan delay = remaining > TimeSpan.Zero ? remaining : Options.DutyCycleWindow;
            return delay;
        }

        private ProcessIdentityData FindFallback(int processId)
        {
            if (_fallback == null ||
                !_fallbackTimestampInitialized ||
                _clock.ElapsedSince(_fallbackAtTimestamp) >= Options.SnapshotFallbackInterval)
            {
                try
                {
                    _fallback = _processes.Snapshot() ?? new Dictionary<int, ProcessIdentityData>();
                    _fallbackAtTimestamp = _clock.Timestamp;
                    _fallbackTimestampInitialized = true;
                }
                catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
                {
                    _fallback = new Dictionary<int, ProcessIdentityData>();
                    _fallbackAtTimestamp = _clock.Timestamp;
                    _fallbackTimestampInitialized = true;
                }
            }
            _fallback.TryGetValue(processId, out ProcessIdentityData identity);
            return identity;
        }

        private bool? IsLocal(ClientProcessContext context)
        {
            try
            {
                IPAddress address = context.ClientEndPoint.Address;
                return IPAddress.IsLoopback(address) ||
                    _localAddresses.IsLocal(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                return null;
            }
        }

        private static int FindOwner(IReadOnlyDictionary<TcpTuple, int> owners, ClientProcessContext context)
        {
            if (!TryNormalize(context.ClientEndPoint, out IPEndPoint client) ||
                !TryNormalize(context.ProxyEndPoint, out IPEndPoint proxy))
            {
                return 0;
            }
            owners.TryGetValue(new TcpTuple(client.Address, client.Port, proxy.Address, proxy.Port), out int processId);
            return processId > 0 ? processId : 0;
        }

        private static IReadOnlyDictionary<TcpTuple, int> IndexOwners(
            IReadOnlyList<TcpOwnerRow> rows,
            IReadOnlyList<Pending> pending)
        {
            var owners = new Dictionary<TcpTuple, int>();
            foreach (Pending item in pending)
            {
                if (TryNormalize(item.Context.ClientEndPoint, out IPEndPoint client) &&
                    TryNormalize(item.Context.ProxyEndPoint, out IPEndPoint proxy))
                {
                    owners.TryAdd(new TcpTuple(client.Address, client.Port, proxy.Address, proxy.Port), 0);
                }
            }
            foreach (TcpOwnerRow row in rows)
            {
                if (row.ProcessId > 0 &&
                    TryNormalize(row.LocalEndPoint, out IPEndPoint local) &&
                    TryNormalize(row.RemoteEndPoint, out IPEndPoint remote))
                {
                    var tuple = new TcpTuple(local.Address, local.Port, remote.Address, remote.Port);
                    if (!owners.TryGetValue(tuple, out int existing))
                    {
                        continue;
                    }
                    if (existing == 0)
                    {
                        owners[tuple] = row.ProcessId;
                    }
                    else if (existing > 0 && existing != row.ProcessId)
                    {
                        owners[tuple] = -1;
                    }
                }
            }
            return owners;
        }

        private static bool TryNormalize(IPEndPoint source, out IPEndPoint normalized)
        {
            normalized = null;
            if (source == null)
            {
                return false;
            }
            IPAddress address = source.Address;
            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }
            if (address.AddressFamily != AddressFamily.InterNetwork)
            {
                return false;
            }
            normalized = new IPEndPoint(address, source.Port);
            return true;
        }

        private static ClientProcessInfo Unresolved(int processId) => new(
            string.Empty,
            processId > 0 ? processId : 0,
            null,
            ClientProcessStatus.Unresolved);

        private void QueueApplication(Pending pending, ClientProcessInfo info)
        {
            if (!pending.TryQueueApplication())
            {
                return;
            }
            if (!TryEnqueueApplication(new ApplicationWork(pending.Context, info, pending.PublicationOrder, pending)))
            {
                RetirePending(pending);
            }
        }

        private void QueueTerminalPublication(ClientProcessContext context)
        {
            _ = TryEnqueueApplication(
                new ApplicationWork(context, Unresolved(0), context.ReservePublicationOrder(), null));
        }

        private bool TryEnqueueApplication(ApplicationWork work)
        {
            bool signal = false;
            lock (_applicationSyncRoot)
            {
                if (_disposed)
                {
                    return false;
                }
                if (_queuedApplications.TryGetValue(work.Context, out ApplicationWork existing))
                {
                    if (work.PublicationOrder > existing.PublicationOrder)
                    {
                        existing.Info = work.Info;
                        existing.PublicationOrder = work.PublicationOrder;
                        existing.Pending = work.Pending;
                    }
                    return true;
                }
                if (_applications.Count >= Options.QueueCapacity)
                {
                    return false;
                }
                _applications.Enqueue(work);
                _queuedApplications.Add(work.Context, work);
                signal = true;
            }
            if (signal)
            {
                _applicationSignal.Release();
            }
            return true;
        }

        private void ApplicationWorker()
        {
            CancellationToken cancellationToken = _stopping.Token;
            while (true)
            {
                _applicationSignal.Wait(cancellationToken);
                ApplicationWork work;
                lock (_applicationSyncRoot)
                {
                    if (_disposed && _applications.Count == 0)
                    {
                        return;
                    }
                    if (!_applications.TryDequeue(out work))
                    {
                        continue;
                    }
                    if (_queuedApplications.TryGetValue(work.Context, out ApplicationWork queued) &&
                        ReferenceEquals(queued, work))
                    {
                        _queuedApplications.Remove(work.Context);
                    }
                }
                Pending pending = work.Pending;
                if (pending != null && !pending.CanApply)
                {
                    continue;
                }
                try
                {
                    _ = work.Context.TryPublish(work.Info, work.PublicationOrder);
                }
                catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
                {
                    // Attribution observers are isolated from traffic and the native resolver.
                }
                if (pending != null && pending.TryComplete())
                {
                    ReleasePending(pending);
                }
            }
        }

        private void RetirePending(Pending pending)
        {
            if (!pending.TryRetire())
            {
                return;
            }
            ReleasePending(pending);
            QueueTerminalPublication(pending.Context);
        }

        private void ReleasePending(Pending pending)
        {
            if (!pending.TryReleaseAccounting())
            {
                return;
            }
            lock (_syncRoot)
            {
                if (pending.QueueNode?.List != null)
                {
                    _pending.Remove(pending.QueueNode);
                    pending.QueueNode = null;
                }
                _outstanding--;
                if (_runs.TryGetValue(pending.Context.RunId, out RunState run))
                {
                    run.Pending.Remove(pending);
                    run.Outstanding--;
                    if (run.Outstanding == 0)
                    {
                        run.Drained.TrySetResult();
                    }
                }
            }
        }

        private void AddToCache(ProcessCacheKey key, ClientProcessInfo value)
        {
            if (_processCache.TryAdd(key, value))
            {
                _processCacheOrder.Enqueue(key);
            }
            while (_processCacheOrder.Count > 1024)
            {
                ProcessCacheKey expired = _processCacheOrder.Dequeue();
                _processCache.Remove(expired);
            }
        }

        private static void PublishUnresolved(ClientProcessContext context, int processId) => context.Publish(
            Unresolved(processId));

        private void RetireRun(Guid runId)
        {
            Pending[] pending;
            lock (_syncRoot)
            {
                pending = _runs.TryGetValue(runId, out RunState run) ? run.Pending.ToArray() : Array.Empty<Pending>();
            }
            foreach (Pending item in pending)
            {
                RetirePending(item);
            }
        }

        private static TaskCompletionSource CompletedRunDrain()
        {
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            completed.SetResult();
            return completed;
        }

        private static void ValidateOptions(ClientProcessResolverOptions options)
        {
            if (options.QueueCapacity <= 0 ||
                options.ResolutionBudget <= TimeSpan.Zero ||
                options.RetryDelay <= TimeSpan.Zero ||
                options.SnapshotFallbackInterval <= TimeSpan.Zero ||
                options.DutyCycleWindow <= TimeSpan.Zero ||
                options.MaximumWorkPerWindow <= TimeSpan.Zero ||
                options.MaximumWorkPerWindow > options.DutyCycleWindow ||
                options.DrainBudget <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(options));
            }
        }

        private sealed class Pending
        {
            private const int Active = 0;
            private const int ApplicationQueued = 1;
            private const int Retired = 2;
            private const int Completed = 3;
            private int _state;
            private int _accounted = 1;

            internal Pending(ClientProcessContext context, long publicationOrder)
            {
                Context = context;
                PublicationOrder = publicationOrder;
            }

            internal ClientProcessContext Context { get; }
            internal long PublicationOrder { get; }
            internal LinkedListNode<Pending> QueueNode { get; set; }
            internal bool IsRetired => Volatile.Read(ref _state) >= Retired;
            internal bool CanApply => Volatile.Read(ref _state) == ApplicationQueued;

            internal bool TryQueueApplication() => Interlocked.CompareExchange(ref _state, ApplicationQueued, Active) ==
                Active;

            internal bool TryRetire()
            {
                while (true)
                {
                    int state = Volatile.Read(ref _state);
                    if (state >= Retired)
                    {
                        return false;
                    }
                    if (Interlocked.CompareExchange(ref _state, Retired, state) == state)
                    {
                        return true;
                    }
                }
            }

            internal bool TryComplete() => Interlocked.CompareExchange(ref _state, Completed, ApplicationQueued) ==
                ApplicationQueued;

            internal bool TryReleaseAccounting() => Interlocked.Exchange(ref _accounted, 0) == 1;
        }

        private sealed class ApplicationWork
        {
            internal ApplicationWork(
                ClientProcessContext context,
                ClientProcessInfo info,
                long publicationOrder,
                Pending pending)
            {
                Context = context;
                Info = info;
                PublicationOrder = publicationOrder;
                Pending = pending;
            }

            internal ClientProcessContext Context { get; }
            internal ClientProcessInfo Info { get; set; }
            internal long PublicationOrder { get; set; }
            internal Pending Pending { get; set; }
        }

        private sealed class RunState
        {
            internal int Outstanding { get; set; }
            internal HashSet<Pending> Pending { get; } = new(ReferenceEqualityComparer.Instance);
            internal TaskCompletionSource Drained { get; set; } = CompletedRunDrain();
        }

        private readonly record struct ProcessCacheKey(int ProcessId, long StartTimeTicks);

        private readonly record struct ProcessLookup(
            ProcessIdentityQueryStatus Status,
            ProcessIdentityData Identity,
            long? SnapshotAtTimestamp,
            bool AccessDenied);

        private readonly record struct IdentityResolution(ClientProcessInfo Info, bool Retry);

        private readonly record struct ResolvedWork(Pending Pending, ClientProcessInfo Info);

        private readonly record struct TcpTuple(
            IPAddress LocalAddress,
            int LocalPort,
            IPAddress RemoteAddress,
            int RemotePort);
    }
}
