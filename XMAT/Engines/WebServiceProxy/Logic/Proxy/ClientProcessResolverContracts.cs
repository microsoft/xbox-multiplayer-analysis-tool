// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.WebServiceCapture.Proxy
{
    internal readonly record struct TcpOwnerRow(IPEndPoint LocalEndPoint, IPEndPoint RemoteEndPoint, int ProcessId);

    internal interface ITcpOwnerTableProvider
    {
        IReadOnlyList<TcpOwnerRow> Snapshot(AddressFamily addressFamily);
    }

    internal enum ProcessIdentityQueryStatus
    {
        Found,
        AccessDenied,
        Missing
    }

    internal sealed record ProcessIdentityData(int ProcessId, DateTimeOffset StartTimeUtc, string Name);

    internal interface IProcessIdentityProvider
    {
        ProcessIdentityQueryStatus Query(int processId, out ProcessIdentityData identity);

        IReadOnlyDictionary<int, ProcessIdentityData> Snapshot();
    }

    internal interface ILocalAddressProvider
    {
        bool IsLocal(IPAddress address);
    }

    internal interface ILocalAddressSnapshotSource
    {
        event EventHandler Changed;

        IReadOnlyCollection<IPAddress> Snapshot();
    }

    internal interface IClientProcessResolverClock
    {
        DateTimeOffset UtcNow { get; }
        long Timestamp { get; }
        TimeSpan ElapsedSince(long timestamp);
        ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
        Task WaitAsync(Task task, TimeSpan timeout, CancellationToken cancellationToken);
    }

    internal sealed record ClientProcessResolverOptions(
        int QueueCapacity,
        TimeSpan ResolutionBudget,
        TimeSpan RetryDelay,
        TimeSpan SnapshotFallbackInterval,
        TimeSpan DutyCycleWindow,
        TimeSpan MaximumWorkPerWindow,
        TimeSpan DrainBudget)
    {
        internal static ClientProcessResolverOptions Default { get; } = new(
            4096,
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(2));
    }
}
