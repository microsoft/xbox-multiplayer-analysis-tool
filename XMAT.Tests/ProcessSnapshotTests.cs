// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Proxy;

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

        [Fact]
        public void ParseIdentities_ReadsCreationTimeAndPinnedImageName()
        {
            const int NameOffset = Entry + 16;
            const string Name = "protected.exe";
            var buffer = new byte[NameOffset + (Name.Length * sizeof(char))];
            GCHandle pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                IntPtr address = pin.AddrOfPinnedObject();
                WriteIdentityEntry(
                    buffer,
                    address,
                    processId: 44,
                    creationFileTime: DateTimeOffset.Parse("2026-11-03T19:00:00.0000000+00:00",
                        System.Globalization.CultureInfo.InvariantCulture).ToFileTime(),
                    Name,
                    NameOffset);

                IReadOnlyDictionary<int, ProcessIdentityData> identities = ProcessSnapshot.ParseIdentities(
                    buffer,
                    buffer.Length,
                    address);

                ProcessIdentityData identity = Assert.Single(identities).Value;
                Assert.Equal(44, identity.ProcessId);
                Assert.Equal(Name, identity.Name);
                Assert.Equal(
                    DateTimeOffset.Parse(
                        "2026-11-03T19:00:00.0000000+00:00",
                        System.Globalization.CultureInfo.InvariantCulture),
                    identity.StartTimeUtc);
            }
            finally
            {
                pin.Free();
            }
        }

        [Theory]
        [InlineData("pointer before buffer")]
        [InlineData("pointer after buffer")]
        [InlineData("odd name length")]
        [InlineData("length exceeds maximum")]
        [InlineData("name extends past buffer")]
        public void ParseIdentities_RejectsUnsafeUnicodeString(string malformation)
        {
            const int NameOffset = Entry + 16;
            var buffer = new byte[NameOffset + 32];
            GCHandle pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                IntPtr address = pin.AddrOfPinnedObject();
                WriteIdentityEntry(buffer, address, 45, DateTimeOffset.UtcNow.ToFileTime(), "safe.exe", NameOffset);
                Span<byte> entry = buffer.AsSpan(0, Entry);
                switch (malformation)
                {
                    case "pointer before buffer":
                        WritePointer(entry, IntPtr.Subtract(address, 2));
                        break;
                    case "pointer after buffer":
                        WritePointer(entry, IntPtr.Add(address, buffer.Length + 2));
                        break;
                    case "odd name length":
                        BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(ProcessSnapshot.ImageNameLengthOffset), 3);
                        break;
                    case "length exceeds maximum":
                        BinaryPrimitives.WriteUInt16LittleEndian(
                            entry.Slice(ProcessSnapshot.ImageNameMaximumLengthOffset),
                            2);
                        break;
                    default:
                        BinaryPrimitives.WriteUInt16LittleEndian(
                            entry.Slice(ProcessSnapshot.ImageNameLengthOffset),
                            64);
                        BinaryPrimitives.WriteUInt16LittleEndian(
                            entry.Slice(ProcessSnapshot.ImageNameMaximumLengthOffset),
                            64);
                        break;
                }

                Assert.Null(ProcessSnapshot.ParseIdentities(buffer, buffer.Length, address));
            }
            finally
            {
                pin.Free();
            }
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

        private static void WriteIdentityEntry(
            byte[] buffer,
            IntPtr address,
            int processId,
            long creationFileTime,
            string name,
            int nameOffset)
        {
            Span<byte> entry = buffer.AsSpan(0, Entry);
            BinaryPrimitives.WriteInt64LittleEndian(entry.Slice(ProcessSnapshot.CreateTimeOffset), creationFileTime);
            BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(ProcessSnapshot.ProcessIdOffset), (ulong)processId);
            ushort nameBytes = checked((ushort)(name.Length * sizeof(char)));
            BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(ProcessSnapshot.ImageNameLengthOffset), nameBytes);
            BinaryPrimitives.WriteUInt16LittleEndian(
                entry.Slice(ProcessSnapshot.ImageNameMaximumLengthOffset),
                nameBytes);
            WritePointer(entry, IntPtr.Add(address, nameOffset));
            MemoryMarshal.AsBytes(name.AsSpan()).CopyTo(buffer.AsSpan(nameOffset, nameBytes));
        }

        private static void WritePointer(Span<byte> entry, IntPtr pointer)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(
                entry.Slice(ProcessSnapshot.ImageNamePointerOffset),
                unchecked((ulong)pointer.ToInt64()));
        }
    }
}
