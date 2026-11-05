// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.IO;

namespace XMAT.Operations.Inspection
{
    // What the preview needs of the gzip format (RFC 1952) to prove where a member ends: its header, read as zlib reads it,
    // and CRC-32 for the header CRC and the trailer.
    internal static class BodyPreviewGzip
    {
        // A file name or comment longer than this is not taken for a header.
        internal const int MaxHeaderTextBytes = 64 * 1024;

        private const byte HeaderCrcFlag = 0x02;
        private const byte ExtraFlag = 0x04;
        private const byte NameFlag = 0x08;
        private const byte CommentFlag = 0x10;
        private const byte ReservedFlags = 0xE0;

        private static readonly uint[] Table = CreateTable();

        // Reads a member header from source and returns its length, or -1 when the data ends inside it. Throws
        // InvalidDataException for a header that zlib would refuse: not gzip, not deflate, reserved flags, an over-long name or
        // comment, or a header CRC that does not match.
        internal static int ReadHeader(Stream source)
        {
            Span<byte> fixedPart = stackalloc byte[10];
            Span<byte> one = stackalloc byte[1];
            Span<byte> two = stackalloc byte[2];
            if (!TryRead(source, fixedPart))
            {
                return -1;
            }

            if (fixedPart[0] != 0x1F || fixedPart[1] != 0x8B || fixedPart[2] != 8 || (fixedPart[3] & ReservedFlags) != 0)
            {
                throw new InvalidDataException();
            }

            byte flags = fixedPart[3];
            uint crc = Update(uint.MaxValue, fixedPart);
            int length = fixedPart.Length;
            if ((flags & ExtraFlag) != 0)
            {
                if (!TryRead(source, two))
                {
                    return -1;
                }

                crc = Update(crc, two);
                int extra = two[0] | (two[1] << 8);
                for (int index = 0; index < extra; index++)
                {
                    if (!TryRead(source, one))
                    {
                        return -1;
                    }

                    crc = Update(crc, one);
                }

                length += 2 + extra;
            }

            for (int field = 0; field < 2; field++)
            {
                if ((flags & (field == 0 ? NameFlag : CommentFlag)) == 0)
                {
                    continue;
                }

                // A zero-terminated file name, then comment.
                int text = 0;
                do
                {
                    if (!TryRead(source, one))
                    {
                        return -1;
                    }

                    crc = Update(crc, one);
                    if (++text > MaxHeaderTextBytes)
                    {
                        throw new InvalidDataException();
                    }
                }
                while (one[0] != 0);

                length += text;
            }

            if ((flags & HeaderCrcFlag) != 0)
            {
                if (!TryRead(source, two))
                {
                    return -1;
                }

                if ((ushort)~crc != (two[0] | (two[1] << 8)))
                {
                    throw new InvalidDataException();
                }

                length += 2;
            }

            return length;
        }

        // The CRC-32 (IEEE) of data.
        internal static uint Crc32(ReadOnlySpan<byte> data) => ~Update(uint.MaxValue, data);

        // Reads exactly buffer.Length bytes; false when the data ends first.
        internal static bool TryRead(Stream source, Span<byte> buffer)
        {
            int filled = 0;
            while (filled < buffer.Length)
            {
                int read = source.Read(buffer[filled..]);
                if (read == 0)
                {
                    return false;
                }

                filled += read;
            }

            return true;
        }

        private static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (byte value in data)
            {
                crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
            }

            return crc;
        }

        private static uint[] CreateTable()
        {
            var table = new uint[256];
            for (uint index = 0; index < table.Length; index++)
            {
                uint value = index;
                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                }

                table[index] = value;
            }

            return table;
        }
    }
}
