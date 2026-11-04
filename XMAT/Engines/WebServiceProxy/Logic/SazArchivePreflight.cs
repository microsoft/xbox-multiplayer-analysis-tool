// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;

namespace XMAT.WebServiceCapture
{
    internal enum SazArchiveFailure
    {
        Malformed,
        TooManyEntries,
        CentralDirectoryTooLarge,
    }

    internal sealed class SazArchiveException : Exception
    {
        internal SazArchiveException(
            SazArchiveFailure failure,
            Exception innerException = null)
            : base(
                failure == SazArchiveFailure.Malformed
                    ? "The SAZ archive directory is malformed."
                    : "The SAZ archive directory exceeds a supported safety limit.",
                innerException)
        {
            Failure = failure;
        }

        internal SazArchiveFailure Failure { get; }
    }

    // Checks a SAZ (ZIP) archive's directory before ZipArchive reads it whole: a single disk, a central directory that
    // ends at the end-of-directory record and lists exactly the entries it declares, and at most the allowed number of
    // entries and directory bytes. Entry contents are never read here.
    internal static class SazArchivePreflight
    {
        // Ceilings on the archive directory: three entries per request plus 64, and a 32 MiB directory per 100,000
        // requests, for 250,000 requests. They bound only the directory, not the size of entries or bodies.
        internal const long MaxArchiveEntries = 750_064;
        internal const long MaxCentralDirectoryBytes = 80L * 1024 * 1024;

        private const int EndOfCentralDirectorySize = 22;
        private const int MaxZipCommentBytes = ushort.MaxValue;
        private const int CentralDirectoryHeaderSize = 46;
        private const uint EndOfCentralDirectorySignature = 0x06054b50;
        private const uint Zip64EndOfCentralDirectorySignature = 0x06064b50;
        private const uint Zip64LocatorSignature = 0x07064b50;
        private const uint CentralDirectoryHeaderSignature = 0x02014b50;

        internal static void Validate(
            Stream input,
            CancellationToken cancellationToken)
        {
            Validate(
                input,
                MaxArchiveEntries,
                MaxCentralDirectoryBytes,
                cancellationToken);
        }

        // Smaller limits apply as given; larger ones are capped at the ceilings.
        internal static void Validate(
            Stream input,
            long maxEntries,
            long maxCentralDirectoryBytes,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCentralDirectoryBytes);
            if (!input.CanRead ||
                !input.CanSeek)
            {
                throw new ArgumentException(
                    "The archive must be readable and seekable.",
                    nameof(input));
            }

            EndOfCentralDirectory eocd = FindEndOfCentralDirectory(
                input,
                cancellationToken);
            ulong entryCount;
            ulong centralDirectorySize;
            ulong centralDirectoryOffset;
            long metadataOffset = eocd.Offset;
            if (eocd.UsesZip64)
            {
                Zip64EndOfCentralDirectory zip64 =
                    ReadZip64EndOfCentralDirectory(
                        input,
                        eocd,
                        cancellationToken);
                entryCount = zip64.EntryCount;
                centralDirectorySize = zip64.CentralDirectorySize;
                centralDirectoryOffset = zip64.CentralDirectoryOffset;
                metadataOffset = zip64.Offset;
            }
            else
            {
                if (eocd.DiskNumber != 0 ||
                    eocd.CentralDirectoryDisk != 0 ||
                    eocd.EntriesOnDisk != eocd.EntryCount)
                {
                    throw new SazArchiveException(SazArchiveFailure.Malformed);
                }
                entryCount = eocd.EntryCount;
                centralDirectorySize = eocd.CentralDirectorySize;
                centralDirectoryOffset = eocd.CentralDirectoryOffset;
            }

            long offset = ToLong(centralDirectoryOffset);
            long size = ToLong(centralDirectorySize);
            long end;
            try
            {
                end = checked(offset + size);
            }
            catch (OverflowException ex)
            {
                throw new SazArchiveException(
                    SazArchiveFailure.Malformed,
                    ex);
            }
            if (end != metadataOffset ||
                end > input.Length)
            {
                throw new SazArchiveException(SazArchiveFailure.Malformed);
            }
            long allowedEntries = Math.Min(
                maxEntries,
                MaxArchiveEntries);
            if (entryCount > (ulong)allowedEntries)
            {
                throw new SazArchiveException(SazArchiveFailure.TooManyEntries);
            }
            if (centralDirectorySize >
                (ulong)Math.Min(
                    maxCentralDirectoryBytes,
                    MaxCentralDirectoryBytes))
            {
                throw new SazArchiveException(SazArchiveFailure.CentralDirectoryTooLarge);
            }

            WalkCentralDirectory(
                input,
                offset,
                end,
                entryCount,
                allowedEntries,
                cancellationToken);
        }

        private static EndOfCentralDirectory FindEndOfCentralDirectory(
            Stream input,
            CancellationToken cancellationToken)
        {
            long tailLength = Math.Min(
                input.Length,
                EndOfCentralDirectorySize + MaxZipCommentBytes);
            long tailOffset = input.Length - tailLength;
            var tail = new byte[checked((int)tailLength)];
            ReadExactly(input, tailOffset, tail);
            for (var index = tail.Length - EndOfCentralDirectorySize;
                index >= 0;
                index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadOnlySpan<byte> candidate = tail.AsSpan(index);
                if (BinaryPrimitives.ReadUInt32LittleEndian(candidate) !=
                    EndOfCentralDirectorySignature)
                {
                    continue;
                }
                var commentLength =
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        candidate.Slice(20, 2));
                if (index + EndOfCentralDirectorySize + commentLength !=
                    tail.Length)
                {
                    throw new SazArchiveException(SazArchiveFailure.Malformed);
                }
                return new EndOfCentralDirectory(
                    tailOffset + index,
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        candidate.Slice(4, 2)),
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        candidate.Slice(6, 2)),
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        candidate.Slice(8, 2)),
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        candidate.Slice(10, 2)),
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        candidate.Slice(12, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        candidate.Slice(16, 4)));
            }

            throw new SazArchiveException(SazArchiveFailure.Malformed);
        }

        private static Zip64EndOfCentralDirectory ReadZip64EndOfCentralDirectory(
            Stream input,
            EndOfCentralDirectory eocd,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long locatorOffset = eocd.Offset - 20;
            if (locatorOffset < 0)
            {
                throw new SazArchiveException(SazArchiveFailure.Malformed);
            }

            Span<byte> locator = stackalloc byte[20];
            ReadExactly(input, locatorOffset, locator);
            if (BinaryPrimitives.ReadUInt32LittleEndian(locator) !=
                    Zip64LocatorSignature ||
                BinaryPrimitives.ReadUInt32LittleEndian(
                    locator.Slice(4, 4)) != 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(
                    locator.Slice(16, 4)) != 1)
            {
                throw new SazArchiveException(SazArchiveFailure.Malformed);
            }

            long recordOffset = ToLong(
                BinaryPrimitives.ReadUInt64LittleEndian(
                    locator.Slice(8, 8)));
            Span<byte> record = stackalloc byte[56];
            ReadExactly(input, recordOffset, record);
            if (BinaryPrimitives.ReadUInt32LittleEndian(record) !=
                Zip64EndOfCentralDirectorySignature)
            {
                throw new SazArchiveException(SazArchiveFailure.Malformed);
            }
            ulong recordBodySize =
                BinaryPrimitives.ReadUInt64LittleEndian(
                    record.Slice(4, 8));
            if (recordBodySize < 44)
            {
                throw new SazArchiveException(SazArchiveFailure.Malformed);
            }

            long recordLength;
            try
            {
                recordLength = ToLong(checked(recordBodySize + 12));
            }
            catch (OverflowException ex)
            {
                throw new SazArchiveException(
                    SazArchiveFailure.Malformed,
                    ex);
            }
            long recordEnd;
            try
            {
                recordEnd = checked(recordOffset + recordLength);
            }
            catch (OverflowException ex)
            {
                throw new SazArchiveException(
                    SazArchiveFailure.Malformed,
                    ex);
            }
            if (recordEnd != locatorOffset)
            {
                throw new SazArchiveException(SazArchiveFailure.Malformed);
            }

            uint diskNumber = BinaryPrimitives.ReadUInt32LittleEndian(
                record.Slice(16, 4));
            uint centralDirectoryDisk =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    record.Slice(20, 4));
            ulong entriesOnDisk =
                BinaryPrimitives.ReadUInt64LittleEndian(
                    record.Slice(24, 8));
            ulong entryCount =
                BinaryPrimitives.ReadUInt64LittleEndian(
                    record.Slice(32, 8));
            ulong centralDirectorySize =
                BinaryPrimitives.ReadUInt64LittleEndian(
                    record.Slice(40, 8));
            ulong centralDirectoryOffset =
                BinaryPrimitives.ReadUInt64LittleEndian(
                    record.Slice(48, 8));
            if (diskNumber != 0 ||
                centralDirectoryDisk != 0 ||
                entriesOnDisk != entryCount ||
                !Matches(
                    eocd.DiskNumber,
                    ushort.MaxValue,
                    diskNumber) ||
                !Matches(
                    eocd.CentralDirectoryDisk,
                    ushort.MaxValue,
                    centralDirectoryDisk) ||
                !Matches(
                    eocd.EntriesOnDisk,
                    ushort.MaxValue,
                    entriesOnDisk) ||
                !Matches(
                    eocd.EntryCount,
                    ushort.MaxValue,
                    entryCount) ||
                !Matches(
                    eocd.CentralDirectorySize,
                    uint.MaxValue,
                    centralDirectorySize) ||
                !Matches(
                    eocd.CentralDirectoryOffset,
                    uint.MaxValue,
                    centralDirectoryOffset))
            {
                throw new SazArchiveException(SazArchiveFailure.Malformed);
            }

            return new Zip64EndOfCentralDirectory(
                recordOffset,
                entryCount,
                centralDirectorySize,
                centralDirectoryOffset);
        }

        private static void WalkCentralDirectory(
            Stream input,
            long offset,
            long end,
            ulong declaredCount,
            long maxEntries,
            CancellationToken cancellationToken)
        {
            long position = offset;
            ulong actualCount = 0;
            Span<byte> header = stackalloc byte[CentralDirectoryHeaderSize];
            while (position < end)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (end - position < CentralDirectoryHeaderSize)
                {
                    throw new SazArchiveException(SazArchiveFailure.Malformed);
                }
                ReadExactly(input, position, header);
                if (BinaryPrimitives.ReadUInt32LittleEndian(header) !=
                        CentralDirectoryHeaderSignature ||
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        header.Slice(34, 2)) != 0)
                {
                    throw new SazArchiveException(SazArchiveFailure.Malformed);
                }

                long entryLength;
                try
                {
                    entryLength = checked(
                        CentralDirectoryHeaderSize +
                        BinaryPrimitives.ReadUInt16LittleEndian(
                            header.Slice(28, 2)) +
                        BinaryPrimitives.ReadUInt16LittleEndian(
                            header.Slice(30, 2)) +
                        BinaryPrimitives.ReadUInt16LittleEndian(
                            header.Slice(32, 2)));
                    position = checked(position + entryLength);
                    actualCount = checked(actualCount + 1);
                }
                catch (OverflowException ex)
                {
                    throw new SazArchiveException(
                        SazArchiveFailure.Malformed,
                        ex);
                }
                if (position > end)
                {
                    throw new SazArchiveException(SazArchiveFailure.Malformed);
                }
                if (actualCount > (ulong)maxEntries)
                {
                    throw new SazArchiveException(SazArchiveFailure.TooManyEntries);
                }
            }
            if (position != end ||
                actualCount != declaredCount)
            {
                throw new SazArchiveException(SazArchiveFailure.Malformed);
            }
        }

        private static bool Matches(
            ulong legacyValue,
            ulong saturatedValue,
            ulong zip64Value)
        {
            return legacyValue == saturatedValue || legacyValue == zip64Value;
        }

        private static long ToLong(ulong value)
        {
            if (value > long.MaxValue)
            {
                throw new SazArchiveException(SazArchiveFailure.Malformed);
            }
            return (long)value;
        }

        private static void ReadExactly(
            Stream input,
            long offset,
            Span<byte> destination)
        {
            if (offset < 0 ||
                offset > input.Length - destination.Length)
            {
                throw new SazArchiveException(SazArchiveFailure.Malformed);
            }
            input.Seek(offset, SeekOrigin.Begin);
            var read = 0;
            while (read < destination.Length)
            {
                var count = input.Read(destination.Slice(read));
                if (count == 0)
                {
                    throw new SazArchiveException(SazArchiveFailure.Malformed);
                }
                read += count;
            }
        }

        private sealed record EndOfCentralDirectory(
            long Offset,
            ushort DiskNumber,
            ushort CentralDirectoryDisk,
            ushort EntriesOnDisk,
            ushort EntryCount,
            uint CentralDirectorySize,
            uint CentralDirectoryOffset)
        {
            internal bool UsesZip64 =>
                DiskNumber == ushort.MaxValue ||
                CentralDirectoryDisk == ushort.MaxValue ||
                EntriesOnDisk == ushort.MaxValue ||
                EntryCount == ushort.MaxValue ||
                CentralDirectorySize == uint.MaxValue ||
                CentralDirectoryOffset == uint.MaxValue;
        }

        private sealed record Zip64EndOfCentralDirectory(
            long Offset,
            ulong EntryCount,
            ulong CentralDirectorySize,
            ulong CentralDirectoryOffset);
    }
}
