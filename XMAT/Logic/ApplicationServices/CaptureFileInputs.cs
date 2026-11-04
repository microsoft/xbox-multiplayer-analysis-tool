// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using XMAT.SharedInterfaces;

namespace XMAT.ApplicationServices
{
    internal sealed class CaptureFileInputs : ICaptureFileInputs
    {
        private readonly object _gate = new();
        private readonly Dictionary<Guid, Entry> _inputs = new();

        public Task<InputFileDescriptor> RegisterAsync(
            string path, long maximumBytes, CancellationToken cancellationToken = default) =>
            Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                    throw new CaptureFileException(CaptureFileFailure.InvalidPath);
                var kind = Path.GetExtension(path).ToLowerInvariant() switch
                {
                    ".txt" => CaptureInputKind.ScriptSource,
                    ".xmatcap" => CaptureInputKind.CaptureDatabase,
                    ".saz" => CaptureInputKind.FiddlerSaz,
                    ".etl" => CaptureInputKind.NetworkTraceEtl,
                    _ => throw new CaptureFileException(CaptureFileFailure.InvalidPath)
                };
                using var directory = new WindowsExportFolder(Path.GetDirectoryName(path));
                var fileName = Path.GetFileName(path);
                await using var file = directory.OpenInput(fileName, out var identity);
                DemandLimit(file, maximumBytes);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
                lock (_gate)
                {
                    var existing = _inputs.Values.FirstOrDefault(entry =>
                        entry.Directory.Equals(directory.Path, StringComparison.OrdinalIgnoreCase) &&
                        entry.DirectoryIdentity == directory.Identity && entry.FileIdentity == identity &&
                        entry.Descriptor.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase) &&
                        entry.Descriptor.Length == file.Length && entry.Descriptor.Sha256 == hash);
                    if (existing != null)
                        return existing.Descriptor;
                    if (_inputs.Count >= 64)
                        throw new CaptureFileException(CaptureFileFailure.Unavailable);
                    var descriptor = new InputFileDescriptor(Guid.NewGuid(), kind,
                        fileName, file.Length, hash);
                    _inputs.Add(descriptor.Id, new(descriptor, directory.Path, directory.Identity, identity));
                    return descriptor;
                }
            }, cancellationToken);

        public IReadOnlyList<InputFileDescriptor> GetInputs()
        {
            lock (_gate)
            { return _inputs.Values.Select(entry => entry.Descriptor).ToArray(); }
        }

        public Task ValidateAsync(Guid inputId, long maximumBytes, CancellationToken cancellationToken = default) =>
            WithFileAsync(inputId, maximumBytes, (_, _, _) => Task.FromResult(true), cancellationToken);

        public Task<ScriptFileInput> ReadScriptAsync(
            Guid inputId, long maximumBytes, CancellationToken cancellationToken = default) =>
            WithFileAsync(inputId, maximumBytes, async (descriptor, file, _) =>
            {
                if (descriptor.Kind != CaptureInputKind.ScriptSource)
                    throw new CaptureFileException(CaptureFileFailure.InvalidPath);
                var source = await ScriptFiles.ReadAsync(file, maximumBytes, cancellationToken);
                return new ScriptFileInput(descriptor, source);
            }, cancellationToken);

        public Task<InputFileDescriptor> CopyCaptureAsync(
            Guid inputId, Stream destination, long maximumBytes, CancellationToken cancellationToken = default) =>
            CopyAsync(inputId, CaptureInputKind.CaptureDatabase, destination, maximumBytes, cancellationToken);

        public Task<InputFileDescriptor> CopySazAsync(
            Guid inputId, Stream destination, long maximumBytes, CancellationToken cancellationToken = default) =>
            CopyAsync(inputId, CaptureInputKind.FiddlerSaz, destination, maximumBytes, cancellationToken);

        public Task<InputFileDescriptor> CopyEtlAsync(
            Guid inputId, Stream destination, long maximumBytes, CancellationToken cancellationToken = default) =>
            CopyAsync(inputId, CaptureInputKind.NetworkTraceEtl, destination, maximumBytes, cancellationToken);

        private Task<InputFileDescriptor> CopyAsync(
            Guid inputId, CaptureInputKind kind, Stream destination, long maximumBytes, CancellationToken cancellationToken) =>
            WithFileAsync(inputId, maximumBytes, async (descriptor, file, path) =>
            {
                if (descriptor.Kind != kind)
                    throw new CaptureFileException(CaptureFileFailure.InvalidPath);
                if (kind == CaptureInputKind.CaptureDatabase)
                    await DataAbstractionLayer.DataAbstractionLayer.CopyCaptureInputAsync(path, file, destination, cancellationToken);
                else
                    await file.CopyToAsync(destination, cancellationToken);
                return descriptor;
            }, cancellationToken);

        private Task<T> WithFileAsync<T>(
            Guid inputId, long maximumBytes, Func<InputFileDescriptor, FileStream, string, Task<T>> action,
            CancellationToken cancellationToken) =>
            Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
                Entry entry;
                lock (_gate)
                {
                    entry = _inputs.GetValueOrDefault(inputId) ??
                        throw new CaptureFileException(CaptureFileFailure.Unavailable);
                }
                using var directory = new WindowsExportFolder(entry.Directory);
                if (directory.Identity != entry.DirectoryIdentity)
                    throw new CaptureFileException(CaptureFileFailure.InputChanged);
                await using var file = directory.OpenInput(entry.Descriptor.FileName, out var identity);
                if (identity != entry.FileIdentity || file.Length != entry.Descriptor.Length)
                    throw new CaptureFileException(CaptureFileFailure.InputChanged);
                DemandLimit(file, maximumBytes);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
                if (hash != entry.Descriptor.Sha256)
                    throw new CaptureFileException(CaptureFileFailure.InputChanged);
                cancellationToken.ThrowIfCancellationRequested();
                file.Position = 0;
                return await action(entry.Descriptor, file, Path.Combine(entry.Directory, entry.Descriptor.FileName));
            }, cancellationToken);

        private static void DemandLimit(FileStream file, long maximumBytes)
        {
            if (file.Length > maximumBytes)
                throw new QueryException(QueryError.ResultLimit, "The input exceeds the approved data limit.");
        }

        private sealed record Entry(
            InputFileDescriptor Descriptor, string Directory,
            WindowsExportFolder.FileIdentity DirectoryIdentity, WindowsExportFolder.FileIdentity FileIdentity);
    }
}
