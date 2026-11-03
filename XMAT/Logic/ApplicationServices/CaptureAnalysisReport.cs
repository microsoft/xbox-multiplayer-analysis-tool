// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CaptureAnalysisEngine;
using XMAT.SharedInterfaces;

namespace XMAT.ApplicationServices
{
    internal sealed class CaptureAnalysisReport
    {
        private readonly Document[] _documents;
        private readonly string _workspaceRoot;
        private readonly long[] _recordIds;
        private string _desktopDirectory;
        private IReadOnlyList<CaptureReportArtifact> _desktopManifest;
        internal CaptureCompletedReportDescriptor Descriptor { get; }
        internal IReadOnlyList<CaptureReportArtifact> DesktopManifest => _desktopManifest;
        internal long StorageBytes => _documents.Sum(document => document.JsonBytes.LongLength + 256) + _recordIds.LongLength * 24;

        internal CaptureAnalysisReport(CaptureCompletedReportDescriptor descriptor, TraceAnalysisResult analysis,
            string workspaceRoot = null, IReadOnlyList<long> recordIds = null)
        {
            _workspaceRoot = workspaceRoot;
            _recordIds = recordIds?.ToArray() ?? Array.Empty<long>();
            _documents = analysis.Consoles
                .SelectMany((console, index) => console.Reports.Where(KnownDocument)
                    .OrderBy(report => report.GetType().Name, StringComparer.Ordinal)
                    .Select(report => new Document(index + 1, console.ConsoleId, report.GetType().Name, Encoding.UTF8.GetBytes(report.ToJson()))))
                .ToArray();
            Descriptor = descriptor with
            {
                Documents = Array.AsReadOnly(_documents.Select((document, index) =>
                    new CaptureReportDocumentDescriptor(index + 1, document.Type,
                        IPAddress.TryParse(document.Console, out var address) ? address.ToString() : null,
                        document.JsonBytes.LongLength)).ToArray())
            };
        }

        internal CaptureAnalysisReport(CaptureCompletedReportDescriptor descriptor, string statisticsJson,
            string workspaceRoot = null, IReadOnlyList<long> recordIds = null)
        {
            _workspaceRoot = workspaceRoot;
            _recordIds = recordIds?.ToArray() ?? Array.Empty<long>();
            _documents = [new(1, null, "NetworkTraceStatistics", Encoding.UTF8.GetBytes(statisticsJson))];
            Descriptor = descriptor with
            {
                Documents = Array.AsReadOnly(new[]
                {
                    new CaptureReportDocumentDescriptor(1, "NetworkTraceStatistics", null, Encoding.UTF8.GetByteCount(statisticsJson))
                })
            };
        }

        internal CaptureDetailTable Details(long id)
        {
            if (id < 1 || id > _documents.Length)
                throw new CaptureDataException(CaptureDataError.DetailNotFound);
            var document = _documents[id - 1];
            return new(Descriptor.SnapshotId, ["console", "type", "json"],
                [[document.Console, document.Type, new CaptureDetailTable.Utf8Text(document.JsonBytes)]]);
        }

        internal async Task<ICaptureReportFiles> CreateFilesAsync(long maximumBytes, CancellationToken cancellationToken)
        {
            if (_desktopManifest != null)
            {
                if (_desktopManifest.Sum(artifact => artifact.Length) > maximumBytes)
                    throw new CaptureDataException(CaptureDataError.LimitExceeded);
                return new Files(_desktopDirectory, Descriptor, false) { Manifest = _desktopManifest };
            }
            if (_documents.Length == 0)
                throw new CaptureDataException(CaptureDataError.UnsupportedData);
            cancellationToken.ThrowIfCancellationRequested();
            var root = _workspaceRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", PublicUtilities.AppName, "AgentReportArtifacts");
            if (!Path.IsPathFullyQualified(root) || root.StartsWith('\\'))
                throw new CaptureFileException(CaptureFileFailure.InvalidPath);
            Directory.CreateDirectory(root);
            using var storage = new WindowsExportFolder(root);
            var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
            new PhysicalAnalysisReportExportFileSystem().CreateSingleDirectory(directory);
            var files = new Files(directory, Descriptor, true);
            try
            {
                using var spool = new WindowsExportFolder(directory);
                await WriteAsync(directory, maximumBytes, cancellationToken);
                files.Manifest = Array.AsReadOnly(Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal)
                    .Select(path =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using var stream = File.OpenRead(path);
                        return new CaptureReportArtifact(Path.GetFileName(path), stream.Length, Convert.ToHexString(SHA256.HashData(stream)));
                    }).ToArray());
                return files;
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                try
                { await files.DisposeAsync(); }
                catch (Exception cleanupException) when (!ExceptionUtilities.IsFatal(cleanupException))
                { throw new CaptureReportArtifactsException(files, exception); }
                throw;
            }
        }

        internal async Task WriteAsync(string directory, long maximumBytes, CancellationToken cancellationToken)
        {
            long written = 0;
            foreach (var (name, bytes) in Artifacts())
            {
                cancellationToken.ThrowIfCancellationRequested();
                written = checked(written + bytes.LongLength);
                if (written > maximumBytes)
                    throw new CaptureDataException(CaptureDataError.LimitExceeded);
                await using var stream = new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.Asynchronous);
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
        }

        internal IReadOnlyList<CaptureReportArtifact> Manifest(long maximumBytes, CancellationToken cancellationToken)
        {
            if (_documents.Length == 0)
                throw new CaptureDataException(CaptureDataError.UnsupportedData);
            if (_desktopManifest != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_desktopManifest.Sum(artifact => artifact.Length) > maximumBytes)
                    throw new CaptureDataException(CaptureDataError.LimitExceeded);
                return _desktopManifest;
            }
            long total = 0;
            var manifest = new List<CaptureReportArtifact>();
            foreach (var (name, bytes) in Artifacts())
            {
                cancellationToken.ThrowIfCancellationRequested();
                total = checked(total + bytes.LongLength);
                if (total > maximumBytes)
                    throw new CaptureDataException(CaptureDataError.LimitExceeded);
                manifest.Add(new(name, bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes))));
                cancellationToken.ThrowIfCancellationRequested();
            }
            return manifest.OrderBy(artifact => artifact.RelativePath, StringComparer.Ordinal).ToArray();
        }

        internal void AdoptDesktopFiles(string directory)
        {
            if (_desktopManifest != null)
                throw new InvalidOperationException("The completed report manifest is already frozen.");
            var manifest = Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal).Select(path =>
            {
                using var stream = File.OpenRead(path);
                return new CaptureReportArtifact(Path.GetFileName(path), stream.Length, Convert.ToHexString(SHA256.HashData(stream)));
            }).ToArray();
            _desktopDirectory = directory;
            _desktopManifest = Array.AsReadOnly(manifest);
        }

        private IEnumerable<(string Name, byte[] Bytes)> Artifacts()
        {
            yield return ("provenance.json", JsonSerializer.SerializeToUtf8Bytes(Descriptor));
            yield return ("record-ids.json", JsonSerializer.SerializeToUtf8Bytes(_recordIds));
            foreach (var document in _documents)
                yield return ($"console-{document.ConsoleOrdinal:D4}-{document.Type}.json", document.JsonBytes);
            var webConsoles = _documents.GroupBy(document => document.ConsoleOrdinal)
                .Where(group => group.Any(document => document.Type == nameof(StatsReportDocument))).ToArray();
            if (webConsoles.Length == 0)
                yield break;
            foreach (var resource in AnalysisReportArtifactWriter.SharedResources)
                yield return (resource, AnalysisReportArtifactWriter.Resource(resource));
            foreach (var console in webConsoles)
            {
                var prefix = console.Key == 1 ? string.Empty : $"console-{console.Key:D4}-";
                var stats = console.First(document => document.Type == nameof(StatsReportDocument));
                var calls = console.FirstOrDefault(document => document.Type == nameof(CallReportDocument));
                yield return (prefix + "index.html", AnalysisReportArtifactWriter.Index(prefix));
                yield return (prefix + "stats.js", AnalysisReportArtifactWriter.Stats(Encoding.UTF8.GetString(stats.JsonBytes)));
                yield return (prefix + "calls.js", AnalysisReportArtifactWriter.Calls(calls == null ? "{\"Call List\":[]}" :
                    Encoding.UTF8.GetString(calls.JsonBytes)));
            }
        }

        private static bool KnownDocument(ReportDocument document)
        {
            var type = document?.GetType();
            return type == typeof(PerEndpointReportDocument) || type == typeof(StatsReportDocument) ||
                type == typeof(CallReportDocument) || type == typeof(CertWarningReportDocument);
        }

        private sealed record Document(int ConsoleOrdinal, string Console, string Type, byte[] JsonBytes);

        private sealed class Files(string directory, CaptureCompletedReportDescriptor report, bool owned) : ICaptureReportFiles
        {
            public string SourceDirectory => directory;
            public CaptureCompletedReportDescriptor Report => report;
            public IReadOnlyList<CaptureReportArtifact> Manifest { get; internal set; } = Array.Empty<CaptureReportArtifact>();
            public ValueTask DisposeAsync()
            {
                if (owned && Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
                return ValueTask.CompletedTask;
            }
        }

        internal sealed class CaptureReportArtifactsException(ICaptureReportFiles files, Exception original)
            : Exception("The private report artifacts require cleanup.", original)
        {
            internal ICaptureReportFiles Files { get; } = files;
            internal Exception Original { get; } = original;
        }
    }
}
