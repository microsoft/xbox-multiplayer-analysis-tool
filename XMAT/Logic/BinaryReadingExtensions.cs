// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

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

        public static string ReadLine(this BinaryReader obj)
        {
            byte[] pattern = Encoding.ASCII.GetBytes("\r\n");
            List<byte> bytes = new List<byte>();

            int count = 0;
            do
            {
                var buffer = obj.ReadBytes(4096);
                count = buffer.Length;
                if (count > 0)
                {
                    bytes.AddRange(buffer);
                }

                // Find the newline pattern
                int match = bytes.FindFirstMatch(pattern);
                if (match != -1)
                {
                    // reset the stream to just after the newline
                    obj.BaseStream.Seek(match - bytes.Count + 2, SeekOrigin.Current);
                    return bytes.ConvertToString(match);
                }

            } while (count == 4096);

            return Encoding.ASCII.GetString(bytes.ToArray());
        }

        public static byte[] ReadToEnd(this BinaryReader obj)
        {
            List<byte> bytes = new List<byte>();
            int count = 0;
            do
            {
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
