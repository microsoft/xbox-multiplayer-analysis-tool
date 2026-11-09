// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using XMAT.Operations.Captures;
using XMAT.Operations.Processes;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.WebServiceCapture
{
    internal sealed class CaptureRowRegistry
    {
        private readonly object _syncRoot = new();
        private readonly Func<CaptureRowKey, ClientProcessInfo, bool> _applyProcess;
        private readonly Dictionary<CaptureRowKey, Binding> _bindings = new();
        private readonly Dictionary<RequestKey, CaptureRowKey> _requests = new();
        private readonly Dictionary<ClientProcessContext, HashSet<RequestKey>> _connectionRequests = new(
            ReferenceEqualityComparer.Instance);
        private readonly Dictionary<ClientProcessContext, HashSet<CaptureRowKey>> _connectionRows = new(
            ReferenceEqualityComparer.Instance);
        private readonly HashSet<ClientProcessContext> _connections = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<ClientProcessContext> _closedConnections = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<StreamKey, StreamBinding> _streams = new();
        private readonly Dictionary<ClientProcessContext, HashSet<StreamKey>> _connectionStreams = new(
            ReferenceEqualityComparer.Instance);
        private readonly HashSet<CaptureRowKey> _streamKeys = new();
        private Guid _activeRun;
        private long _generation;
        private long _nextIncarnation;
        private bool _acceptingUpdates;

        internal CaptureRowRegistry(Guid sourceId, Func<CaptureRowKey, ClientProcessInfo, bool> applyProcess)
        {
            ArgumentNullException.ThrowIfNull(applyProcess);
            SourceId = sourceId;
            _applyProcess = applyProcess;
        }

        internal Guid SourceId { get; }
        internal Action<string, int> VisitObserver { get; set; }

        internal IDisposable EnterMutation()
        {
            Monitor.Enter(_syncRoot);
            return new MutationLease(_syncRoot);
        }

        internal void StartRun(Guid runId)
        {
            if (runId == Guid.Empty)
            {
                throw new ArgumentException("A capture run needs an identity.", nameof(runId));
            }
            lock (_syncRoot)
            {
                if (_acceptingUpdates)
                {
                    throw new InvalidOperationException("A capture run is already active.");
                }
                _activeRun = runId;
                _acceptingUpdates = true;
            }
        }

        internal CaptureRowKey RegisterInserted(
            ClientProcessContext connection,
            long requestToken,
            long rowId,
            ClientProcessInfo insertedIdentity,
            out ClientProcessInfo catchUp)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentOutOfRangeException.ThrowIfNegative(rowId);
            ClientProcessInfo inserted = insertedIdentity ?? ClientProcessInfo.NotRecorded;
            lock (_syncRoot)
            {
                if (!_acceptingUpdates || connection.RunId != _activeRun)
                {
                    throw new InvalidOperationException("The connection is not part of the active capture run.");
                }
                var request = new RequestKey(connection, requestToken);
                if (_requests.ContainsKey(request))
                {
                    throw new InvalidOperationException("The request already has a capture row.");
                }

                ClientProcessInfo observed = connection.Identity;
                if (observed.Status == ClientProcessStatus.NotRecorded && _connections.Add(connection))
                {
                    connection.IdentityChanged += Resolve;
                }
                var key = new CaptureRowKey(SourceId, _generation, rowId, checked(++_nextIncarnation));
                var binding = new Binding(connection.RunId, connection, inserted);
                _bindings.Add(key, binding);
                _requests.Add(request, key);
                IndexRequest(connection, request);
                if (!_connectionRows.TryGetValue(connection, out HashSet<CaptureRowKey> rows))
                {
                    rows = new HashSet<CaptureRowKey>();
                    _connectionRows.Add(connection, rows);
                }
                rows.Add(key);

                catchUp = connection.Identity;
                if (catchUp.Status != ClientProcessStatus.NotRecorded && ConnectionApplied(connection, catchUp))
                {
                    StopObserving(connection);
                }
                return key;
            }
        }

        internal CaptureRowKey RegisterFallback(
            ClientProcessContext connection,
            long requestToken,
            long rowId,
            ClientProcessInfo insertedIdentity,
            out ClientProcessInfo catchUp)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentOutOfRangeException.ThrowIfNegative(rowId);
            ClientProcessInfo inserted = insertedIdentity ?? ClientProcessInfo.NotRecorded;
            lock (_syncRoot)
            {
                var request = new RequestKey(connection, requestToken);
                if (_requests.TryGetValue(request, out CaptureRowKey existing))
                {
                    catchUp = connection.Identity;
                    return existing;
                }
                var key = new CaptureRowKey(SourceId, _generation, rowId, checked(++_nextIncarnation));
                var binding = new Binding(
                    _acceptingUpdates ? _activeRun : connection.RunId,
                    connection,
                    inserted,
                    isFallback: true);
                _bindings.Add(key, binding);
                _requests.Add(request, key);
                IndexRequest(connection, request);
                if (!_connectionRows.TryGetValue(connection, out HashSet<CaptureRowKey> rows))
                {
                    rows = new HashSet<CaptureRowKey>();
                    _connectionRows.Add(connection, rows);
                }
                rows.Add(key);
                catchUp = connection.Identity;
                return key;
            }
        }

        internal void ApplyFallback(CaptureRowKey key, ClientProcessInfo identity)
        {
            lock (_syncRoot)
            {
                if (_bindings.TryGetValue(key, out Binding binding) && binding.IsFallback && IsCurrentCore(key))
                {
                    ApplyProcess(key, binding, identity ?? ClientProcessInfo.NotRecorded);
                }
            }
        }

        internal void Resolve(ClientProcessContext connection, ClientProcessInfo info)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ClientProcessInfo identity = info ?? ClientProcessInfo.NotRecorded;
            if (connection.Identity != identity)
            {
                if (connection.Identity.Status == ClientProcessStatus.NotRecorded)
                {
                    connection.Publish(identity);
                }
                return;
            }
            lock (_syncRoot)
            {
                if (!_acceptingUpdates || connection.RunId != _activeRun)
                {
                    return;
                }
                if (!_connectionRows.TryGetValue(connection, out HashSet<CaptureRowKey> rows))
                {
                    if (identity.Status != ClientProcessStatus.NotRecorded)
                    {
                        if (_closedConnections.Contains(connection))
                        {
                            DetachConnection(connection);
                        }
                        else if (ConnectionApplied(connection, identity))
                        {
                            StopObserving(connection);
                        }
                    }
                    return;
                }
                foreach (var item in rows.ToArray()
                    .Select(key => (Key: key, Binding: key.Generation == _generation
                        ? _bindings.GetValueOrDefault(key) : null))
                    .Where(item => item.Binding != null))
                {
                    ApplyProcess(item.Key, item.Binding, identity);
                }
                if (identity.Status != ClientProcessStatus.NotRecorded && ConnectionApplied(connection, identity))
                {
                    if (_closedConnections.Contains(connection))
                    {
                        DetachConnection(connection);
                    }
                    else
                    {
                        StopObserving(connection);
                    }
                }
            }
        }

        internal bool TryGet(ClientProcessContext connection, long requestToken, out CaptureRowKey key)
        {
            ArgumentNullException.ThrowIfNull(connection);
            lock (_syncRoot)
            {
                return _requests.TryGetValue(new RequestKey(connection, requestToken), out key) && IsCurrentCore(key);
            }
        }

        internal bool TryGetOrCreateStream(ClientProcessContext connection, long requestToken, out CaptureRowKey key)
        {
            ArgumentNullException.ThrowIfNull(connection);
            lock (_syncRoot)
            {
                var stream = new StreamKey(connection, requestToken);
                if (_streams.TryGetValue(stream, out StreamBinding existing) && IsCurrentCore(existing.Key))
                {
                    key = existing.Key;
                    return true;
                }
                if (_requests.TryGetValue(new RequestKey(connection, requestToken), out key) && IsCurrentCore(key))
                {
                    AssociateStreamCore(stream, key);
                    return true;
                }
                if (!_acceptingUpdates || connection.RunId != _activeRun)
                {
                    key = default;
                    return false;
                }
                key = new CaptureRowKey(SourceId, _generation, requestToken, checked(++_nextIncarnation));
                AssociateStreamCore(stream, key);
                return true;
            }
        }

        internal bool TryUpdate(Guid runId, CaptureRowKey key, Action write)
        {
            ArgumentNullException.ThrowIfNull(write);
            lock (_syncRoot)
            {
                if (!_acceptingUpdates ||
                    !_bindings.TryGetValue(key, out Binding binding) ||
                    (!binding.IsFallback && (runId != _activeRun || binding.RunId != runId)) ||
                    !IsCurrentCore(key))
                {
                    return false;
                }
                write();
                return true;
            }
        }

        internal bool IsCurrent(CaptureRowKey key)
        {
            lock (_syncRoot)
            {
                return IsCurrentCore(key);
            }
        }

        internal void AdvanceGeneration()
        {
            lock (_syncRoot)
            {
                _generation = checked(_generation + 1);
                _bindings.Clear();
                _requests.Clear();
                _connectionRequests.Clear();
                _connectionRows.Clear();
                _streamKeys.Clear();
                foreach (StreamBinding stream in _streams.Values)
                {
                    stream.Key = new CaptureRowKey(
                        SourceId,
                        _generation,
                        stream.Key.RowId,
                        checked(++_nextIncarnation));
                    _streamKeys.Add(stream.Key);
                }
            }
        }

        internal void CloseConnection(ClientProcessContext connection)
        {
            ArgumentNullException.ThrowIfNull(connection);
            lock (_syncRoot)
            {
                if (_connectionRequests.Remove(connection, out HashSet<RequestKey> requests))
                {
                    VisitObserver?.Invoke("requests", requests.Count);
                    foreach (RequestKey request in requests)
                    {
                        _requests.Remove(request);
                    }
                }
                if (_connectionStreams.Remove(connection, out HashSet<StreamKey> streams))
                {
                    VisitObserver?.Invoke("streams", streams.Count);
                    foreach (StreamKey stream in streams)
                    {
                        _streams.Remove(stream);
                    }
                }
                if (connection.Identity.Status == ClientProcessStatus.NotRecorded && _connections.Contains(connection))
                {
                    _closedConnections.Add(connection);
                }
                else
                {
                    DetachConnection(connection);
                }
            }
        }

        internal void CloseStream(ClientProcessContext connection, long requestToken)
        {
            ArgumentNullException.ThrowIfNull(connection);
            lock (_syncRoot)
            {
                var stream = new StreamKey(connection, requestToken);
                _streams.Remove(stream);
                if (_connectionStreams.TryGetValue(connection, out HashSet<StreamKey> streams))
                {
                    streams.Remove(stream);
                    if (streams.Count == 0)
                    {
                        _connectionStreams.Remove(connection);
                    }
                }
            }
        }

        internal void FinishRun(Guid runId)
        {
            lock (_syncRoot)
            {
                if (runId == _activeRun)
                {
                    ClientProcessInfo terminal = new(string.Empty, 0, null, ClientProcessStatus.Unresolved);
                    foreach (
                        KeyValuePair<CaptureRowKey, Binding> item in _bindings.Where(item => item.Value.RunId == runId
                            && item.Key.Generation == _generation).ToArray())
                    {
                        ClientProcessInfo desired = item.Value.DesiredProcess.Status == ClientProcessStatus.NotRecorded
                            ? terminal
                            : item.Value.DesiredProcess;
                        if (item.Value.AppliedProcess != desired)
                        {
                            ApplyProcess(item.Key, item.Value, desired);
                        }
                    }
                    _acceptingUpdates = false;
                    foreach (
                        ClientProcessContext connection in _connections.Where(connection => connection.RunId ==
                            runId).ToArray())
                    {
                        StopObserving(connection);
                        _connectionRows.Remove(connection);
                        _closedConnections.Remove(connection);
                    }
                    foreach (Binding binding in _bindings.Values.Where(binding => binding.RunId == runId))
                    {
                        binding.Connection = null;
                    }
                    _requests.Clear();
                    _connectionRequests.Clear();
                    _connectionRows.Clear();
                    _streams.Clear();
                    _connectionStreams.Clear();
                }
            }
        }

        private void ApplyProcess(CaptureRowKey key, Binding binding, ClientProcessInfo identity)
        {
            if (identity.Status == ClientProcessStatus.NotRecorded &&
                binding.DesiredProcess.Status != ClientProcessStatus.NotRecorded)
            {
                return;
            }
            if (identity.Status == ClientProcessStatus.Unresolved &&
                binding.DesiredProcess.Status is ClientProcessStatus.Local or ClientProcessStatus.Imported or
                    ClientProcessStatus.Remote)
            {
                return;
            }
            binding.DesiredProcess = identity;
            if (binding.AppliedProcess == identity)
            {
                return;
            }
            try
            {
                if (_applyProcess(key, identity))
                {
                    binding.AppliedProcess = identity;
                }
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                // Attribution persistence is best effort and never affects traffic.
            }
        }

        private void AssociateStreamCore(StreamKey stream, CaptureRowKey key)
        {
            if (_streams.TryGetValue(stream, out StreamBinding existing))
            {
                _streamKeys.Remove(existing.Key);
                existing.Key = key;
            }
            else
            {
                _streams.Add(stream, new StreamBinding(key));
                IndexStream(stream.Connection, stream);
            }
            _streamKeys.Add(key);
        }

        private void IndexRequest(ClientProcessContext connection, RequestKey request)
        {
            if (!_connectionRequests.TryGetValue(connection, out HashSet<RequestKey> requests))
            {
                requests = new HashSet<RequestKey>();
                _connectionRequests.Add(connection, requests);
            }
            requests.Add(request);
        }

        private void IndexStream(ClientProcessContext connection, StreamKey stream)
        {
            if (!_connectionStreams.TryGetValue(connection, out HashSet<StreamKey> streams))
            {
                streams = new HashSet<StreamKey>();
                _connectionStreams.Add(connection, streams);
            }
            streams.Add(stream);
        }

        private void StopObserving(ClientProcessContext connection)
        {
            if (_connections.Remove(connection))
            {
                connection.IdentityChanged -= Resolve;
            }
        }

        private void DetachConnection(ClientProcessContext connection)
        {
            StopObserving(connection);
            _closedConnections.Remove(connection);
            if (!_connectionRows.Remove(connection, out HashSet<CaptureRowKey> rows))
            {
                return;
            }
            VisitObserver?.Invoke("bindings", rows.Count);
            foreach (Binding binding in rows
                .Select(key => _bindings.GetValueOrDefault(key))
                .Where(binding => binding != null))
            {
                binding.Connection = null;
            }
        }

        private bool ConnectionApplied(ClientProcessContext connection, ClientProcessInfo identity) =>
            !_connectionRows.TryGetValue(connection, out HashSet<CaptureRowKey> rows) ||
            rows.All(key => _bindings.TryGetValue(key, out Binding binding) && binding.AppliedProcess == identity);

        private bool IsCurrentCore(CaptureRowKey key) => key.SourceId == SourceId &&
            key.Generation == _generation &&
            (_bindings.ContainsKey(key) || _streamKeys.Contains(key));

        private sealed class Binding
        {
            internal Binding(
                Guid runId,
                ClientProcessContext connection,
                ClientProcessInfo appliedProcess,
                bool isFallback = false)
            {
                RunId = runId;
                Connection = connection;
                AppliedProcess = appliedProcess;
                DesiredProcess = appliedProcess;
                IsFallback = isFallback;
            }

            internal Guid RunId { get; }
            internal ClientProcessContext Connection { get; set; }
            internal ClientProcessInfo AppliedProcess { get; set; }
            internal ClientProcessInfo DesiredProcess { get; set; }
            internal bool IsFallback { get; }
        }

        private readonly record struct RequestKey(ClientProcessContext Connection, long RequestToken);

        private readonly record struct StreamKey(ClientProcessContext Connection, long RequestToken);

        private sealed class StreamBinding
        {
            internal StreamBinding(CaptureRowKey key)
            {
                Key = key;
            }

            internal CaptureRowKey Key { get; set; }
        }

        private sealed class MutationLease : IDisposable
        {
            private object _gate;

            internal MutationLease(object gate)
            {
                _gate = gate;
            }

            public void Dispose()
            {
                object gate = Interlocked.Exchange(ref _gate, null);
                if (gate != null)
                {
                    Monitor.Exit(gate);
                }
            }
        }
    }
}
