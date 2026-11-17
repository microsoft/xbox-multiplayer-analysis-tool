// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;

namespace XMAT.WebServiceCapture.Proxy
{
    public sealed record NetworkShapingChange(
        long Sequence,
        DateTimeOffset Timestamp,
        NetworkShapingProfile Profile);

    public sealed class NetworkShapingController
    {
        internal const int MaximumHistory = 4096;

        private readonly object _gate = new();
        private readonly HashSet<ConnectionContext> _connections = new();
        private readonly List<NetworkShapingChange> _history = new();
        private Snapshot _snapshot = new(NetworkShapingProfile.Off, 0);

        public NetworkShapingController()
        {
            _history.Add(new NetworkShapingChange(0, DateTimeOffset.UtcNow, NetworkShapingProfile.Off));
            Upload = new NetworkShapingPacer(this, upload: true);
            Download = new NetworkShapingPacer(this, upload: false);
        }

        public event EventHandler Changed;

        internal NetworkShapingPacer Upload { get; }
        internal NetworkShapingPacer Download { get; }
        internal SemaphoreSlim BufferSlots { get; } = new(256, 256);

        public NetworkShapingProfile Current => ReadSnapshot().Profile;

        public IReadOnlyList<NetworkShapingChange> History
        {
            get { lock (_gate) { return _history.ToArray(); } }
        }

        internal Snapshot ReadSnapshot() => Volatile.Read(ref _snapshot);

        // A false result means a post-commit notification failed, not that the policy was rolled back.
        public bool Apply(NetworkShapingProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);
            profile.Validate();
            ConnectionContext[] abort;
            Snapshot previous;
            lock (_gate)
            {
                // Shape and Broken stop at the cap. Disconnect, then Off, keep reserved entries, so a full
                // history can always return to an unshaped state.
                int limit = profile.Mode switch
                {
                    NetworkShapingMode.Off => MaximumHistory + 2,
                    NetworkShapingMode.Disconnect => MaximumHistory + 1,
                    _ => MaximumHistory,
                };
                if (_history.Count >= limit)
                {
                    throw new InvalidOperationException("The capture's shaping history is full.");
                }

                previous = _snapshot;
                var next = new Snapshot(profile, previous.Version + 1);
                _history.Add(new NetworkShapingChange(next.Version, DateTimeOffset.UtcNow, profile));
                Volatile.Write(ref _snapshot, next);
                abort = profile.Mode == NetworkShapingMode.Disconnect
                    ? new List<ConnectionContext>(_connections).ToArray()
                    : Array.Empty<ConnectionContext>();
            }

            previous.Changed.TrySetResult();
            bool notified = true;
            foreach (ConnectionContext connection in abort)
            {
                try
                {
                    connection.Abort(new ConnectionAbortedException("Proxy connections disconnected by shaping."));
                }
                catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
                {
                    notified = false;
                }
            }

            bool observersNotified = NotifyChanged();
            return notified && observersNotified;
        }

        internal bool ResetHistory()
        {
            lock (_gate)
            {
                _history.Clear();
                _history.Add(new NetworkShapingChange(_snapshot.Version, DateTimeOffset.UtcNow, _snapshot.Profile));
            }
            return NotifyChanged();
        }

        private bool NotifyChanged()
        {
            bool notified = true;
            foreach (Delegate handler in Changed?.GetInvocationList() ?? Array.Empty<Delegate>())
            {
                try
                {
                    ((EventHandler)handler)(this, EventArgs.Empty);
                }
                catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
                {
                    notified = false;
                }
            }
            return notified;
        }

        internal bool Register(ConnectionContext connection)
        {
            lock (_gate)
            {
                if (_snapshot.Profile.Mode is NetworkShapingMode.Broken or NetworkShapingMode.Disconnect)
                {
                    return false;
                }

                return _connections.Add(connection);
            }
        }

        internal void Unregister(ConnectionContext connection)
        {
            lock (_gate) { _connections.Remove(connection); }
        }

        internal sealed class Snapshot(NetworkShapingProfile profile, long version)
        {
            internal NetworkShapingProfile Profile { get; } = profile;
            internal long Version { get; } = version;
            internal TaskCompletionSource Changed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
