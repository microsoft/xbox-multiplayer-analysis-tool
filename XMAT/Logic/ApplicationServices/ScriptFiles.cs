// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XMAT.Scripting;
using XMAT.SharedInterfaces;

namespace XMAT.ApplicationServices
{
    internal static class ScriptFiles
    {
        internal static async Task LoadDesktopAsync(
            ScriptModel script, string path, bool rememberFileName, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(script);
            var before = script.CaptureState();
            var source = await Task.Run(async () =>
            {
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    4096, FileOptions.Asynchronous);
                return await ReadAsync(file, long.MaxValue, cancellationToken);
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ScriptOperations.Update(script, source, before.Revision);
            if (rememberFileName)
                script.Filename = path;
        }

        internal static async Task<string> ReadAsync(
            Stream source, long maximumBytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source.CanSeek && source.Length > maximumBytes)
                throw new QueryException(QueryError.ResultLimit, "The script input exceeds the approved data limit.");
            using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            var text = await reader.ReadToEndAsync(cancellationToken);
            if (Encoding.UTF8.GetByteCount(text) > maximumBytes)
                throw new QueryException(QueryError.ResultLimit, "The decoded script exceeds the approved data limit.");
            return text;
        }

        internal static Task SaveDesktopAsync(
            ScriptModel script, string path, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(script);
            var source = script.CaptureState().Source ?? string.Empty;
            var destination = Path.GetFullPath(path);
            return Task.Run(async () =>
            {
                var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        4096, FileOptions.Asynchronous))
                    {
                        await WriteAsync(source, file, long.MaxValue, cancellationToken);
                        await file.FlushAsync(cancellationToken);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(temporary, destination, overwrite: true);
                }
                finally { File.Delete(temporary); }
            }, cancellationToken);
        }

        internal static async Task WriteAsync(
            string source, Stream destination, long maximumBytes, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(source);
            cancellationToken.ThrowIfCancellationRequested();
            if (Encoding.UTF8.GetByteCount(source) > maximumBytes)
                throw new QueryException(QueryError.ResultLimit, "The script exceeds the approved output limit.");
            await using var writer = new StreamWriter(destination, new UTF8Encoding(false), leaveOpen: true);
            await writer.WriteAsync(source.AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
        }
    }
}
