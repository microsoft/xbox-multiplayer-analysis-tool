// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;

namespace XMAT.ApplicationServices
{
    internal sealed class CaptureFiles(CaptureQueries sources) : ICaptureFiles
    {
        public async Task SaveDesktopAsync(
            Guid sourceId, CaptureArchiveFormat format, string path, CancellationToken cancellationToken = default)
        {
            await using var archive = await CreateArchiveAsync(sourceId, format, long.MaxValue, cancellationToken);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.Asynchronous))
                {
                    await archive.CopyToAsync(file, long.MaxValue, cancellationToken);
                    await file.FlushAsync(cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                using (DataAbstractionLayer.LoadedCaptureDatabaseRegistry.AcquireSaveDestination(path))
                {
                    try
                    { File.Move(temporary, path, overwrite: true); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        throw new DataAbstractionLayer.CaptureSaveException(
                            DataAbstractionLayer.CaptureSaveFailure.DestinationLocked, exception);
                    }
                }
            }
            finally { File.Delete(temporary); }
        }

        public Task<ICaptureArchive> CreateArchiveAsync(
            Guid sourceId, CaptureArchiveFormat format, long maximumBytes,
            CancellationToken cancellationToken = default) => format == CaptureArchiveFormat.CaptureDatabase
                ? sources.ReadSourceAsync<ICaptureArchive>(sourceId, database =>
                    StageAsync(sourceId, format, null, maximumBytes, path =>
                    {
                        ((ICaptureDatabaseBackup)database).SaveToFile(path);
                        return null;
                    }, cancellationToken), cancellationToken)
                : CreateSazArchiveAsync(sourceId, format, null, maximumBytes, cancellationToken);

        public Task<ICaptureArchive> CreateSazArchiveAsync(
            Guid sourceId, CaptureArchiveFormat format, string deviceName, long maximumBytes,
            CancellationToken cancellationToken = default) =>
            sources.ReadSourceAsync<ICaptureArchive>(sourceId, database =>
            {
                if (format is not (CaptureArchiveFormat.FiddlerSaz or CaptureArchiveFormat.SanitizedFiddlerSaz) ||
                    deviceName != null && (string.IsNullOrWhiteSpace(deviceName) || deviceName.Length > 255))
                    throw new CaptureFileException(CaptureFileFailure.InvalidPath);
                var dataset = database switch
                {
                    IDatabase writable => writable.TableByName(WebServiceCaptureMethod.TableName)?.Dataset,
                    IReadonlyDatabase loaded => loaded.TableByName(WebServiceCaptureMethod.TableName)?.Dataset,
                    _ => null
                } ?? throw new CaptureFileException(CaptureFileFailure.Unavailable);
                return StageAsync(sourceId, format, deviceName, maximumBytes, path =>
                {
                    System.Collections.Generic.IEnumerable<XMAT.WebServiceCapture.Models.ProxyConnectionModel> Records()
                    {
                        var records = deviceName == null ? dataset.DataRecords : dataset.DataRecordsWhen(
                            new FieldValue<string>(WebServiceCaptureMethod.FieldKey_DeviceName, deviceName));
                        foreach (var record in records)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            yield return new(record);
                        }
                    }
                    if (format == CaptureArchiveFormat.FiddlerSaz)
                    {
                        FiddlerSazHandler.CreateSazFile(path, Records(), cancellationToken);
                        return null;
                    }
                    var summary = FiddlerSazHandler.CreateSanitizedSazFile(path, Records(), cancellationToken);
                    return new CaptureSanitization(summary.Sessions, summary.RedactedValues,
                        summary.OmittedBodies, summary.OmittedHeaders, summary.WorkBudgetOmissions);
                }, cancellationToken);
            }, cancellationToken);

        private static async Task<ICaptureArchive> StageAsync(
            Guid sourceId, CaptureArchiveFormat format, string deviceName, long maximumBytes,
            Func<string, CaptureSanitization> serialize, CancellationToken cancellationToken)
        {
            if (!Enum.IsDefined(format) || maximumBytes <= 0)
                throw new CaptureFileException(CaptureFileFailure.InvalidPath);
            var directory = Path.Combine(Path.GetTempPath(), $"XMAT.CaptureExport.{Guid.NewGuid():N}");
            var path = Path.Combine(directory, format == CaptureArchiveFormat.CaptureDatabase ? "capture.xmatcap" : "capture.saz");
            var published = false;
            try
            {
                var capturedAt = DateTimeOffset.UtcNow;
                var summary = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Directory.CreateDirectory(directory);
                    var result = serialize(path);
                    cancellationToken.ThrowIfCancellationRequested();
                    return result;
                }, cancellationToken);
                var length = new FileInfo(path).Length;
                if (length > maximumBytes)
                    throw new QueryException(QueryError.ResultLimit, "The capture archive exceeds the approved output limit.");
                var archive = new Archive(directory, path, new(sourceId, format, capturedAt, length, deviceName, summary));
                published = true;
                return archive;
            }
            finally
            {
                if (!published)
                    await Task.Run(() =>
                    {
                        if (Directory.Exists(directory))
                            Directory.Delete(directory, recursive: true);
                    }, CancellationToken.None);
            }
        }

        private sealed class Archive(string directory, string path, CaptureArchiveReceipt receipt) : ICaptureArchive
        {
            private readonly FileStream _source = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous);
            public CaptureArchiveReceipt Receipt { get; } = receipt;
            public async Task CopyToAsync(Stream destination, long maximumBytes, CancellationToken cancellationToken = default)
            {
                if (Receipt.Length > maximumBytes)
                    throw new QueryException(QueryError.ResultLimit, "The capture archive exceeds the approved output limit.");
                _source.Position = 0;
                var buffer = new byte[65536];
                long total = 0;
                int read;
                while ((read = await _source.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    total += read;
                    if (total > maximumBytes)
                        throw new QueryException(QueryError.ResultLimit, "The capture archive exceeds the approved output limit.");
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            public async ValueTask DisposeAsync()
            {
                await _source.DisposeAsync();
                await Task.Run(() => Directory.Delete(directory, recursive: true));
            }
        }
    }
}
