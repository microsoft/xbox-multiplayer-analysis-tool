// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Text;

namespace XMAT.ApplicationServices
{
    internal static class AtomicSettingsFile
    {
        internal static void Write(string path, string json, Action<Exception> cleanupFailure = null)
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using var writer = new StreamWriter(file, new UTF8Encoding(false), leaveOpen: true);
                    writer.Write(json);
                    writer.Flush();
                    file.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                try
                { File.Delete(temporary); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    cleanupFailure?.Invoke(exception);
                }
            }
        }
    }
}
