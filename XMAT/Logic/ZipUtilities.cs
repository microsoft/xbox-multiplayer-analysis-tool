// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.IO;
using System.IO.Compression;

namespace XMAT
{
    internal static class ZipUtilities
    {
        internal static MemoryStream DecompressEntryToMemory(ZipArchiveEntry entry)
        {
            var memory = new MemoryStream();
            try
            {
                using (var zip = entry.Open())
                {
                    zip.CopyTo(memory);
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
