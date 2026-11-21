// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.SqlQueries
{
    internal static class SqlQueryFileOperations
    {
        internal static void ValidateDestination(
            string path, string extension, Func<IReadOnlyList<string>> protectedPaths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            var fullPath = Path.GetFullPath(path);
            if ((protectedPaths?.Invoke() ?? []).Where(item => !string.IsNullOrEmpty(item)).Any(item =>
                Path.GetFullPath(item).Equals(fullPath, StringComparison.OrdinalIgnoreCase)) ||
                fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part =>
                    part.StartsWith("XMAT.Query.", StringComparison.OrdinalIgnoreCase) ||
                    part.StartsWith(".xmat-query-", StringComparison.OrdinalIgnoreCase)))
            {
                throw new IOException(Localization.GetLocalizedString("SQL_PROTECTED_DESTINATION"));
            }
            if (!Path.GetExtension(fullPath).Equals(extension, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(Localization.GetLocalizedString("SQL_FILE_EXTENSION", extension));
            }
        }

        internal static async Task WriteAtomicAsync(
            string path,
            bool overwrite,
            string extension,
            Func<IReadOnlyList<string>> protectedPaths,
            Func<Stream, CancellationToken, Task> write,
            CancellationToken cancellationToken)
        {
            ValidateDestination(path, extension, protectedPaths);
            var destination = Path.GetFullPath(path);
            var staging = Path.Combine(Path.GetDirectoryName(destination), ".xmat-query-" + Guid.NewGuid().ToString("N"));
            try
            {
                await using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await write(stream, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                ValidateDestination(destination, extension, protectedPaths);
                File.Move(staging, destination, overwrite);
            }
            finally
            {
                File.Delete(staging);
            }
        }
    }
}
