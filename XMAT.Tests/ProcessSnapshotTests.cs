// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using XMAT.WebServiceCapture;

namespace XMAT.Tests
{
    // Reading the snapshot of every process that tells an owner that cannot be opened apart from a process that took
    // over its ID.
    public sealed class ProcessSnapshotTests
    {
        private const int Entry = ProcessSnapshot.EntrySize;

        [Fact]
        public void Parse_ReadsEveryEntryOfACompleteChain()
        {
            // The first entry is followed by data of its own, as its threads and image name are.
            var buffer = Entries(3 * Entry, (0, 2 * Entry, 100, 4), (2 * Entry, 0, 200, 8));

            var times = ProcessSnapshot.Parse(buffer, buffer.Length);

            Assert.Equal(2, times.Count);
            Assert.Equal(100L, times[4]);
            Assert.Equal(200L, times[8]);
        }

        // Data that does not match the layout exactly is never trusted.
        [Theory]
        [InlineData("chain without an end")]
        [InlineData("entry cut short")]
        [InlineData("overlapping entries")]
        [InlineData("offset past the data")]
        [InlineData("duplicate process IDs")]
        [InlineData("process ID too large")]
        [InlineData("negative creation time")]
        [InlineData("length past the buffer")]
        [InlineData("no data")]
        public void Parse_RejectsMalformedData(string malformation)
        {
            var (buffer, length) = malformation switch
            {
                "chain without an end" => (Entries(2 * Entry, (0, Entry, 1, 4), (Entry, Entry, 2, 8)), 2 * Entry),
                "entry cut short" => (Entries(Entry, (0, 0, 1, 4)), Entry - 1),
                "overlapping entries" => (Entries(2 * Entry, (0, 8, 1, 4)), 2 * Entry),
                "offset past the data" => (Entries(Entry, (0, uint.MaxValue, 1, 4)), Entry),
                "duplicate process IDs" => (Entries(2 * Entry, (0, Entry, 1, 4), (Entry, 0, 2, 4)), 2 * Entry),
                "process ID too large" => (Entries(Entry, (0, 0, 1, (ulong)int.MaxValue + 1)), Entry),
                "negative creation time" => (Entries(Entry, (0, 0, -1, 4)), Entry),
                "length past the buffer" => (Entries(Entry, (0, 0, 1, 4)), Entry + 1),
                _ => (Array.Empty<byte>(), 0)
            };

            Assert.Null(ProcessSnapshot.Parse(buffer, length));
        }

        // `size` bytes with x64 entries at the given offsets, each with its NextEntryOffset, creation time and ID.
        private static byte[] Entries(int size, params (int Offset, uint Next, long Created, ulong Id)[] entries)
        {
            var buffer = new byte[size];
            foreach (var (offset, next, created, id) in entries)
            {
                var entry = buffer.AsSpan(offset, Entry);
                BinaryPrimitives.WriteUInt32LittleEndian(entry, next);
                BinaryPrimitives.WriteInt64LittleEndian(entry.Slice(ProcessSnapshot.CreateTimeOffset), created);
                BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(ProcessSnapshot.ProcessIdOffset), id);
            }
            return buffer;
        }
    }
}
