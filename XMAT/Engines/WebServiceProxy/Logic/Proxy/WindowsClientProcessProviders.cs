// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.WebServiceCapture.Proxy
{
    internal sealed class WindowsTcpOwnerTableProvider : ITcpOwnerTableProvider
    {
        private const int InitialBufferSize = 64 * 1024;
        private const int RowSize = 24;
        private const int MaximumRows = 256 * 1024;
        private const int MaximumBufferSize = (MaximumRows * RowSize) + (64 * 1024);
        private byte[] _buffer = new byte[InitialBufferSize];

        public IReadOnlyList<TcpOwnerRow> Snapshot(AddressFamily addressFamily)
        {
            if (addressFamily != AddressFamily.InterNetwork)
            {
                return Array.Empty<TcpOwnerRow>();
            }
            for (var attempt = 0; attempt < 4; attempt++)
            {
                int size = _buffer.Length;
                uint result;
                GCHandle pin = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
                try
                {
                    result = RecoveryNativeMethods.GetExtendedTcpTable(
                        pin.AddrOfPinnedObject(),
                        ref size,
                        false,
                        RecoveryNativeMethods.AfInet,
                        RecoveryNativeMethods.TcpTableOwnerPidConnections,
                        0);
                }
                finally
                {
                    pin.Free();
                }
                if (result == RecoveryNativeMethods.ErrorInsufficientBuffer)
                {
                    if (size <= 0 || size > MaximumBufferSize)
                    {
                        return Array.Empty<TcpOwnerRow>();
                    }
                    _buffer = new byte[Math.Min(MaximumBufferSize, checked(size + (16 * 1024)))];
                    continue;
                }
                if (result != 0 || size < sizeof(int))
                {
                    return Array.Empty<TcpOwnerRow>();
                }
                int rows = BinaryPrimitives.ReadInt32LittleEndian(_buffer);
                if (rows < 0 ||
                    rows > MaximumRows ||
                    (long)sizeof(int) + ((long)rows * RowSize) > Math.Min(size, _buffer.Length))
                {
                    return Array.Empty<TcpOwnerRow>();
                }
                var owners = new List<TcpOwnerRow>(rows);
                for (var index = 0; index < rows; index++)
                {
                    int offset = sizeof(int) + (index * RowSize);
                    owners.Add(
                        new TcpOwnerRow(
                            new IPEndPoint(new IPAddress(_buffer.AsSpan(offset + 4, 4)), ReadPort(offset + 8)),
                            new IPEndPoint(new IPAddress(_buffer.AsSpan(offset + 12, 4)), ReadPort(offset + 16)),
                            BinaryPrimitives.ReadInt32LittleEndian(_buffer.AsSpan(offset + 20, sizeof(int)))));
                }
                return owners;
            }
            return Array.Empty<TcpOwnerRow>();
        }

        private int ReadPort(int offset)
        {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(offset, sizeof(uint)));
            return (int)(((value & 0xFF) << 8) | ((value >> 8) & 0xFF));
        }
    }

    internal sealed class WindowsProcessIdentityProvider : IProcessIdentityProvider
    {
        private const int InitialSnapshotSize = 1 << 20;
        private const int MaximumSnapshotSize = 64 << 20;

        public ProcessIdentityQueryStatus Query(int processId, out ProcessIdentityData identity)
        {
            identity = null;
            using var process = RecoveryNativeMethods.OpenProcess(
                RecoveryNativeMethods.ProcessQueryLimitedInformation,
                false,
                processId);
            if (process.IsInvalid)
            {
                return Marshal.GetLastWin32Error() == RecoveryNativeMethods.ErrorAccessDenied
                    ? ProcessIdentityQueryStatus.AccessDenied
                    : ProcessIdentityQueryStatus.Missing;
            }
            if (!RecoveryNativeMethods.GetProcessTimes(process, out long created, out _, out _, out _))
            {
                return ProcessIdentityQueryStatus.Missing;
            }
            var name = new StringBuilder(32768);
            int length = name.Capacity;
            bool named = RecoveryNativeMethods.QueryFullProcessImageName(process, 0, name, ref length);
            DateTimeOffset startTime;
            try
            {
                startTime = DateTimeOffset.FromFileTime(created).ToUniversalTime();
            }
            catch (ArgumentOutOfRangeException)
            {
                return ProcessIdentityQueryStatus.Missing;
            }
            identity = new ProcessIdentityData(processId, startTime, named ? name.ToString() : string.Empty);
            return ProcessIdentityQueryStatus.Found;
        }

        public IReadOnlyDictionary<int, ProcessIdentityData> Snapshot()
        {
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            {
                return null;
            }
            var size = InitialSnapshotSize;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var buffer = new byte[size];
                GCHandle pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    IntPtr address = pin.AddrOfPinnedObject();
                    int status = RecoveryNativeMethods.NtQuerySystemInformationPinned(
                        RecoveryNativeMethods.SystemProcessInformation,
                        address,
                        buffer.Length,
                        out uint required);
                    if (status == 0)
                    {
                        int parsedLength = required == 0
                            ? buffer.Length
                            : required <= (uint)buffer.Length ? (int)required : 0;
                        IReadOnlyDictionary<int, ProcessIdentityData> identities = parsedLength > 0
                            ? ProcessSnapshot.ParseIdentities(buffer, parsedLength, address)
                            : null;
                        ProcessIdentity self = ProcessIdentity.Current;
                        return identities != null &&
                            identities.TryGetValue(self.ProcessId, out ProcessIdentityData current) &&
                            current.StartTimeUtc.ToFileTime() == self.CreationTime ? identities : null;
                    }
                    long next = Math.Max((long)size * 2, required + (64L * 1024));
                    if (status != RecoveryNativeMethods.StatusInfoLengthMismatch || next > MaximumSnapshotSize)
                    {
                        return null;
                    }
                    size = (int)next;
                }
                finally
                {
                    pin.Free();
                }
            }
            return null;
        }
    }

    internal sealed class WindowsLocalAddressSnapshotSource : ILocalAddressSnapshotSource,
        IDisposable
    {
        internal WindowsLocalAddressSnapshotSource()
        {
            NetworkChange.NetworkAddressChanged += NetworkAddressChanged;
        }

        public event EventHandler Changed;

        public IReadOnlyCollection<IPAddress> Snapshot() =>
            NetworkInterface.GetAllNetworkInterfaces().SelectMany(network =>
            network.GetIPProperties().UnicastAddresses).Select(address => address.Address).ToArray();

        private void NetworkAddressChanged(object sender, EventArgs args)
        {
            _ = sender;
            _ = args;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            NetworkChange.NetworkAddressChanged -= NetworkAddressChanged;
            GC.SuppressFinalize(this);
        }
    }

    internal sealed class WindowsLocalAddressProvider : ILocalAddressProvider,
        IDisposable
    {
        private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(5);
        private readonly ILocalAddressSnapshotSource _source;
        private readonly IClientProcessResolverClock _clock;
        private readonly CancellationTokenSource _refreshCancellation = new();
        private readonly object _refreshGate = new();
        private AddressSnapshot _snapshot = AddressSnapshot.Unavailable;
        private Task _refreshTask = Task.CompletedTask;
        private bool _refreshRequested;
        private bool _refreshRunning;
        private bool _disposed;

        internal WindowsLocalAddressProvider() : this(new WindowsLocalAddressSnapshotSource())
        {
        }

        internal WindowsLocalAddressProvider(ILocalAddressSnapshotSource source) : this(
            source,
            new SystemClientProcessResolverClock())
        {
        }

        internal WindowsLocalAddressProvider(ILocalAddressSnapshotSource source, IClientProcessResolverClock clock)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(clock);
            _source = source;
            _clock = clock;
            _source.Changed += SourceChanged;
            QueueRefresh();
        }

        internal Task RefreshIdle
        {
            get
            {
                lock (_refreshGate)
                {
                    return _refreshTask;
                }
            }
        }

        public bool IsLocal(IPAddress address)
        {
            if (address == null)
            {
                return false;
            }
            AddressSnapshot snapshot = Volatile.Read(ref _snapshot);
            if (!snapshot.Available)
            {
                throw new InvalidOperationException("Local addresses are unavailable.");
            }
            IPAddress value = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
            return snapshot.Addresses.Contains(value);
        }

        public void Dispose()
        {
            lock (_refreshGate)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                _refreshRequested = false;
            }
            _refreshCancellation.Cancel();
            _source.Changed -= SourceChanged;
            if (_source is IDisposable disposable)
            {
                disposable.Dispose();
            }
            if (_refreshTask.IsCompleted)
            {
                _refreshCancellation.Dispose();
            }
            else
            {
                _ = _refreshTask.ContinueWith(
                    _ => _refreshCancellation.Dispose(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            GC.SuppressFinalize(this);
        }

        private void SourceChanged(object sender, EventArgs args)
        {
            _ = sender;
            _ = args;
            QueueRefresh();
        }

        private void QueueRefresh()
        {
            lock (_refreshGate)
            {
                if (_disposed)
                {
                    return;
                }
                _refreshRequested = true;
                if (_refreshRunning)
                {
                    return;
                }
                _refreshRunning = true;
                _refreshTask = Task.Run(RefreshLoopAsync);
            }
        }

        private async Task RefreshLoopAsync()
        {
            TimeSpan retryDelay = InitialRetryDelay;
            while (true)
            {
                lock (_refreshGate)
                {
                    if (_disposed || (!_refreshRequested && Volatile.Read(ref _snapshot).Available))
                    {
                        _refreshRunning = false;
                        return;
                    }
                    _refreshRequested = false;
                }
                try
                {
                    var addresses = new HashSet<IPAddress>();
                    foreach (IPAddress address in _source.Snapshot())
                    {
                        IPAddress value = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
                        if (value.AddressFamily == AddressFamily.InterNetwork)
                        {
                            addresses.Add(value);
                        }
                    }
                    Volatile.Write(ref _snapshot, new AddressSnapshot(addresses, Available: true));
                    retryDelay = InitialRetryDelay;
                }
                catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
                {
                    try
                    {
                        await _clock.DelayAsync(retryDelay, _refreshCancellation.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_refreshCancellation.IsCancellationRequested)
                    {
                        lock (_refreshGate)
                        {
                            _refreshRunning = false;
                        }
                        return;
                    }
                    retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, MaximumRetryDelay.Ticks));
                    lock (_refreshGate)
                    {
                        if (_disposed)
                        {
                            _refreshRunning = false;
                            return;
                        }
                        _refreshRequested = true;
                    }
                }
            }
        }

        private sealed record AddressSnapshot(IReadOnlySet<IPAddress> Addresses, bool Available)
        {
            internal static AddressSnapshot Unavailable { get; } = new(new HashSet<IPAddress>(), Available: false);
        }
    }

    internal sealed class SystemClientProcessResolverClock : IClientProcessResolverClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        public long Timestamp => Stopwatch.GetTimestamp();

        public TimeSpan ElapsedSince(long timestamp) => Stopwatch.GetElapsedTime(timestamp);

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => new(
            Task.Delay(delay, cancellationToken));

        public Task WaitAsync(Task task, TimeSpan timeout, CancellationToken cancellationToken) => task.WaitAsync(
            timeout,
            cancellationToken);
    }

    internal static class ClientProcessResolverHost
    {
        private static readonly Lazy<ClientProcessResolver> Resolver = new(
            () => new ClientProcessResolver(
                new WindowsTcpOwnerTableProvider(),
                new WindowsProcessIdentityProvider(),
                new WindowsLocalAddressProvider(),
                new SystemClientProcessResolverClock()));

        internal static ClientProcessResolver Shared => Resolver.Value;
    }
}
