// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace XMAT.WebServiceCapture
{
    // The creation time of every process, from one NtQuerySystemInformation snapshot, which unlike opening a process
    // also covers protected ones. Only the x64 layout of SYSTEM_PROCESS_INFORMATION is read, and a snapshot counts
    // only if it is complete, well formed and shows this process with its own creation time.
    internal static class ProcessSnapshot
    {
        // The fixed part of an entry; the threads and image name of its process follow it.
        internal const int EntrySize = 256;
        internal const int CreateTimeOffset = 32;
        internal const int ProcessIdOffset = 80;
        private const int InitialSize = 1 << 20;
        private const int MaximumSize = 64 << 20;
        // Room for processes that start between asking for the size and taking the snapshot.
        private const int Headroom = 64 << 10;
        private const int Attempts = 4;

        // Test hook: stands in for the system snapshot; null stands for one that failed.
        internal static Func<IReadOnlyDictionary<int, long>> Replacement { get; set; }

        // The creation time by process ID, or null if the snapshot fails, cannot be read safely, or does not show this
        // process with the creation time it has.
        internal static IReadOnlyDictionary<int, long> Take()
        {
            var replacement = Replacement;
            var times = replacement != null ? replacement() : Query();
            var self = ProcessIdentity.Current;
            return times != null && times.TryGetValue(self.ProcessId, out var created) && created == self.CreationTime
                ? times
                : null;
        }

        // Reads the first `length` bytes of `buffer` as a chain of entries: each one lies within them, the next one
        // starts past its fixed part, and the last one has a NextEntryOffset of 0. Process IDs must be distinct and
        // fit an int, and creation times must not be negative. Returns null otherwise.
        internal static IReadOnlyDictionary<int, long> Parse(byte[] buffer, int length)
        {
            if (length <= 0 || length > buffer.Length)
            {
                return null;
            }
            var times = new Dictionary<int, long>();
            long offset = 0;
            while (offset + EntrySize <= length)
            {
                var entry = new ReadOnlySpan<byte>(buffer, (int)offset, EntrySize);
                var next = BinaryPrimitives.ReadUInt32LittleEndian(entry);
                var created = BinaryPrimitives.ReadInt64LittleEndian(entry.Slice(CreateTimeOffset));
                var id = BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(ProcessIdOffset));
                if (id > int.MaxValue || created < 0 || !times.TryAdd((int)id, created))
                {
                    return null;
                }
                if (next == 0)
                {
                    return times;
                }
                if (next < EntrySize)
                {
                    return null;
                }
                offset += next;
            }
            return null;
        }

        // A bounded number of attempts with bounded buffers, as processes may start while the size is asked for.
        private static IReadOnlyDictionary<int, long> Query()
        {
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            {
                return null;
            }
            try
            {
                var size = InitialSize;
                for (var attempt = 1; attempt <= Attempts; attempt++)
                {
                    var buffer = new byte[size];
                    var status = RecoveryNativeMethods.NtQuerySystemInformation(
                        RecoveryNativeMethods.SystemProcessInformation, buffer, buffer.Length, out var length);
                    if (status == 0)
                    {
                        return length <= (uint)buffer.Length ? Parse(buffer, (int)length) : null;
                    }
                    var needed = Math.Max((long)size * 2, length + (long)Headroom);
                    if (status != RecoveryNativeMethods.StatusInfoLengthMismatch || needed > MaximumSize)
                    {
                        return null;
                    }
                    size = (int)needed;
                }
                return null;
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                return null;
            }
        }
    }
}
