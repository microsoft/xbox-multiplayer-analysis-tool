// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace XMAT
{
    public static class BinaryReadingExtensions
    {
        public static String ConvertToString(this IList<byte> buffer, int length = int.MaxValue)
        {
            if (length > buffer.Count)
                return Encoding.ASCII.GetString(buffer.ToArray());

            return Encoding.ASCII.GetString(buffer.ToArray(), 0, length);
        }


        public static int FindFirstMatch(this IList<byte> source, byte[] pattern)
        {
            if (source == null || pattern == null || source.Count == 0 ||
               pattern.Length == 0 || pattern.Length > source.Count)
                return -1;

            for (int i = 0; i < source.Count - pattern.Length; ++i)
            {
                if (IsMatch(source, i, pattern))
                {
                    return i;
                }
            }

            return -1;
        }

        public static bool IsMatch(IList<byte> source, int position, byte[] pattern)
        {
            for (int j = 0; j < pattern.Length; ++j)
            {
                if (source[position + j] != pattern[j])
                {
                    return false;
                }
            }

            return true;
        }

        public static string ReadLine(this BinaryReader obj, CancellationToken cancellationToken = default)
        {
            byte[] pattern = Encoding.ASCII.GetBytes("\r\n");
            List<byte> bytes = new List<byte>();

            int count = 0;
            var scanPosition = 0;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var buffer = obj.ReadBytes(4096);
                count = buffer.Length;
                if (count > 0)
                {
                    bytes.AddRange(buffer);
                }

                for (; scanPosition <= bytes.Count - pattern.Length; scanPosition++)
                {
                    if ((scanPosition & 4095) == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                    if (IsMatch(bytes, scanPosition, pattern))
                    {
                        obj.BaseStream.Seek(scanPosition - bytes.Count + 2, SeekOrigin.Current);
                        return bytes.ConvertToString(scanPosition);
                    }
                }

            } while (count == 4096);

            return Encoding.ASCII.GetString(bytes.ToArray());
        }

        public static byte[] ReadToEnd(this BinaryReader obj, CancellationToken cancellationToken = default)
        {
            List<byte> bytes = new List<byte>();
            int count = 0;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var buffer = obj.ReadBytes(4096);
                count = buffer.Length;
                if (count > 0)
                {
                    bytes.AddRange(buffer);
                }
            } while (count == 4096);

            return bytes.ToArray();
        }

        public static bool IsEndOfStream(this BinaryReader obj)
        {
            return obj.BaseStream.Position == obj.BaseStream.Length;
        }
    }
}
