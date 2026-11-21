// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.IO;
using System.IO.Compression;
using System.Threading;
using XMAT.SharedInterfaces;

namespace XMAT
{
    internal static class ZipUtilities
    {
        internal static MemoryStream DecompressEntryToMemory(
            ZipArchiveEntry entry, long maximumBytes = long.MaxValue, CancellationToken cancellationToken = default)
        {
            if (entry.Length > maximumBytes)
                throw new QueryException(QueryError.ResultLimit, "The expanded archive entry exceeds the approved input limit.");
            maximumBytes = entry.Length;
            var memory = new MemoryStream();
            try
            {
                using (var zip = entry.Open())
                {
                    var buffer = new byte[65536];
                    int read;
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        read = zip.Read(buffer);
                        if (read == 0)
                            break;
                        if (read > maximumBytes - memory.Length)
                            throw new QueryException(QueryError.ResultLimit, "The expanded archive entry exceeds the approved input limit.");
                        memory.Write(buffer, 0, read);
                    }
                }
                memory.Position = 0;
                return memory;
            }
            catch
            {
                memory.Dispose();
                throw;
            }
        }
    }
}
