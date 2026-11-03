// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CaptureAnalysisEngine;
using XMAT.ApplicationServices;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    public class CaptureReportFileExportTests
    {
        [Fact]
        public async Task PublishesTheWholeHashedManifestNewOnlyAndKeepsUserOutputAfterSourceRelease()
        {
            using var directory = new InvestigationDirectory();
            var source = new InvestigationReportFiles(Path.Combine(directory.Path, "source"));
            var destinationPath = Path.Combine(directory.Path, "destination");
            Directory.CreateDirectory(destinationPath);
            var exports = new CaptureFileExports();
            var folder = await exports.RegisterFolderAsync(destinationPath);
            var destination = new CaptureReportDestination(folder.Id, "report");
            var receipt = await exports.SaveReportAsync(source, destination, 65536, _ => Task.CompletedTask);
            Assert.True(receipt.Published);
            Assert.Equal(source.Report.SourceId, receipt.SourceId);
            Assert.Equal(source.Report.SnapshotId, receipt.SnapshotId);
            Assert.Equal(source.Manifest.Sum(file => file.Length), receipt.Length);
            Assert.Equal(source.Manifest.Count, receipt.FileCount);
            await source.DisposeAsync();
            var output = Path.Combine(destinationPath, "report");
            Assert.Equal(source.Manifest.Count, Directory.GetFiles(output).Length);
            foreach (var artifact in source.Manifest)
            {
                using var stream = File.OpenRead(Path.Combine(output, artifact.RelativePath));
                Assert.Equal(artifact.Sha256, Convert.ToHexString(SHA256.HashData(stream)));
            }
            Assert.Equal(CaptureFileFailure.DestinationExists,
                (await Assert.ThrowsAsync<CaptureFileException>(() => exports.ValidateReportAsync(destination))).Failure);
            Assert.DoesNotContain(Directory.GetDirectories(destinationPath), IsStaging);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task AFileOrDirectoryCreatedAfterApprovalIsNeverOverwrittenOrMerged(bool directoryRace)
        {
            using var directory = new InvestigationDirectory();
            var source = new InvestigationReportFiles(Path.Combine(directory.Path, "source"));
            var destinationPath = Path.Combine(directory.Path, "destination");
            Directory.CreateDirectory(destinationPath);
            var exports = new CaptureFileExports();
            var folder = await exports.RegisterFolderAsync(destinationPath);
            var output = Path.Combine(destinationPath, "report");
            var exception = await Assert.ThrowsAsync<CaptureFileException>(() => exports.SaveReportAsync(source,
                new(folder.Id, "report"), 65536, _ =>
                {
                    if (directoryRace)
                    {
                        Directory.CreateDirectory(output);
                        File.WriteAllText(Path.Combine(output, "human.txt"), "preserve");
                    }
                    else
                        File.WriteAllText(output, "preserve");
                    return Task.CompletedTask;
                }));
            Assert.Equal(CaptureFileFailure.DestinationExists, exception.Failure);
            Assert.Equal("preserve", await File.ReadAllTextAsync(directoryRace ? Path.Combine(output, "human.txt") : output));
            Assert.DoesNotContain(Directory.GetDirectories(destinationPath), IsStaging);
        }

        [Fact]
        public async Task DestinationRaceWaitsForTransientStagingFileCloseAndPreservesTheWinner()
        {
            using var directory = new InvestigationDirectory();
            var source = new InvestigationReportFiles(Path.Combine(directory.Path, "source"));
            var destinationPath = Path.Combine(directory.Path, "destination");
            Directory.CreateDirectory(destinationPath);
            var exports = new CaptureFileExports();
            var folder = await exports.RegisterFolderAsync(destinationPath);
            var output = Path.Combine(destinationPath, "report");
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Exception failure = null;
            try
            {
                await exports.SaveReportAsync(source, new(folder.Id, "report"), 65536, _ =>
                {
                    Directory.CreateDirectory(output);
                    File.WriteAllText(Path.Combine(output, "human.txt"), "preserve");
                    var staging = Directory.GetDirectories(destinationPath).Single(IsStaging);
                    var locked = File.Open(Path.Combine(staging, "calls.js"), FileMode.Open, FileAccess.Read, FileShare.None);
                    new Thread(() =>
                    {
                        Thread.Sleep(150);
                        locked.Dispose();
                        closed.TrySetResult();
                    })
                    { IsBackground = true }.Start();
                    return Task.CompletedTask;
                });
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (failure is AnalysisReportExportException { OwnedResidue: not null } residue)
                {
                    residue.OwnedResidue.DeleteUnpublished();
                    residue.OwnedResidue.Dispose();
                }
            }
            Assert.Equal(CaptureFileFailure.DestinationExists, Assert.IsType<CaptureFileException>(failure).Failure);
            Assert.Equal("preserve", await File.ReadAllTextAsync(Path.Combine(output, "human.txt")));
            Assert.DoesNotContain(Directory.GetDirectories(destinationPath), IsStaging);
        }

        [Fact]
        public async Task QuotaCancellationAndSameLengthSourceMutationNeverPublishPartialReports()
        {
            using var directory = new InvestigationDirectory();
            var source = new InvestigationReportFiles(Path.Combine(directory.Path, "source"));
            var destinationPath = Path.Combine(directory.Path, "destination");
            Directory.CreateDirectory(destinationPath);
            var exports = new CaptureFileExports();
            var folder = await exports.RegisterFolderAsync(destinationPath);
            var destination = new CaptureReportDestination(folder.Id, "report");
            Assert.Equal(QueryError.ResultLimit, (await Assert.ThrowsAsync<QueryException>(() =>
                exports.SaveReportAsync(source, destination, 1, _ => Task.CompletedTask))).Error);
            using var cancellation = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exports.SaveReportAsync(source, destination, 65536, _ =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            }, cancellation.Token));
            await File.WriteAllTextAsync(Path.Combine(source.SourceDirectory, "calls.js"), "modified");
            var failure = await Assert.ThrowsAsync<AnalysisReportExportException>(() =>
                exports.SaveReportAsync(source, destination, 65536, _ => Task.CompletedTask));
            Assert.Equal(AnalysisReportExportFailureKind.ManifestMismatch, failure.FailureKind);
            Assert.Null(failure.OwnedResidue);
            Assert.Empty(Directory.GetFileSystemEntries(destinationPath));
        }

        [Fact]
        public async Task ApprovedFolderAndSourceIdentitiesStayPinnedThroughPublication()
        {
            using var directory = new InvestigationDirectory();
            var source = new InvestigationReportFiles(Path.Combine(directory.Path, "source"));
            var destinationPath = Path.Combine(directory.Path, "destination");
            Directory.CreateDirectory(destinationPath);
            var exports = new CaptureFileExports();
            var folder = await exports.RegisterFolderAsync(destinationPath);
            var receipt = await exports.SaveReportAsync(source, new(folder.Id, "report"), 65536, _ =>
            {
                Assert.ThrowsAny<IOException>(() => Directory.Move(destinationPath, destinationPath + ".replaced"));
                Assert.ThrowsAny<IOException>(() => Directory.Move(source.SourceDirectory, source.SourceDirectory + ".replaced"));
                return Task.CompletedTask;
            });
            Assert.True(receipt.Published);
            Assert.True(Directory.Exists(Path.Combine(destinationPath, "report")));
            Assert.False(Directory.Exists(destinationPath + ".replaced"));
        }

        [Fact]
        public async Task FailedCleanupRetainsTheExactOwnedDirectoryHandleForReliableRetry()
        {
            using var directory = new InvestigationDirectory();
            var source = new InvestigationReportFiles(Path.Combine(directory.Path, "source"));
            var destinationPath = Path.Combine(directory.Path, "destination");
            Directory.CreateDirectory(destinationPath);
            var exports = new CaptureFileExports();
            var folder = await exports.RegisterFolderAsync(destinationPath);
            using var cancellation = new CancellationTokenSource();
            FileStream locked = null;
            AnalysisReportExportException failure = null;
            try
            {
                failure = await Assert.ThrowsAsync<AnalysisReportExportException>(() => exports.SaveReportAsync(source,
                    new(folder.Id, "report"), 65536, _ =>
                    {
                        var staging = Directory.GetDirectories(destinationPath).Single(IsStaging);
                        locked = File.Open(Path.Combine(staging, "calls.js"), FileMode.Open, FileAccess.Read, FileShare.None);
                        cancellation.Cancel();
                        return Task.CompletedTask;
                    }, cancellation.Token));
                Assert.NotNull(failure.OwnedResidue);
                Assert.Equal(AnalysisReportPublicationState.NotPublished, failure.PublicationState);
                Assert.False(Directory.Exists(Path.Combine(destinationPath, "report")));
                Assert.True(Directory.Exists(failure.ResiduePath));
            }
            finally
            {
                locked?.Dispose();
                if (failure?.OwnedResidue != null)
                {
                    failure.OwnedResidue.DeleteUnpublished();
                    failure.OwnedResidue.Dispose();
                }
            }
            Assert.Empty(Directory.GetFileSystemEntries(destinationPath));
        }

        [Theory]
        [InlineData("..\\outside")]
        [InlineData("nested\\report")]
        [InlineData(@"\\server\share")]
        [InlineData("report:stream")]
        [InlineData("CON")]
        [InlineData("report.")]
        [InlineData("report ")]
        public async Task InvalidDirectoryNamesNeverPublishOrCreateNestedPaths(string name)
        {
            using var directory = new InvestigationDirectory();
            var exports = new CaptureFileExports();
            var folder = await exports.RegisterFolderAsync(directory.Path);
            Assert.Equal(CaptureFileFailure.InvalidPath, (await Assert.ThrowsAsync<CaptureFileException>(() =>
                exports.ValidateReportAsync(new(folder.Id, name)))).Failure);
            Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
        }

        [Fact]
        public async Task FrozenDetailTablesKeepNullEmptyBinaryAndExactEmbeddedNullUtf8ChunksDistinct()
        {
            var text = "雪\0😀" + new string('a', 5000);
            await using var result = new CaptureDetailTable(Guid.NewGuid(), ["field", "value"],
                [["null", null], ["empty", ""], ["text", text], ["binary", new byte[] { 0, 255 }]]);
            var page = await result.ReadPageAsync(0, 100, 4096);
            Assert.Equal(QueryValueKind.Null, page.Rows[0].Cells[1].Kind);
            Assert.Equal(QueryValueKind.Text, page.Rows[1].Cells[1].Kind);
            Assert.Equal("", page.Rows[1].Cells[1].Value);
            Assert.True(page.Rows[2].Cells[1].IsTruncated);
            using var bytes = new MemoryStream();
            QueryValueChunk chunk;
            do
            {
                chunk = await result.ReadValueAsync(2, 1, bytes.Length, 7);
                bytes.Write(chunk.Bytes);
            } while (chunk.HasMore);
            Assert.Equal(Encoding.UTF8.GetBytes(text), bytes.ToArray());
            Assert.Equal(new byte[] { 0, 255 }, (await result.ReadValueAsync(3, 1, 0, 2)).Bytes);
        }

        [Fact]
        public async Task CanonicalReportDetailsFreezeLargeTextBeforeAnyLaterObjectGraphMutation()
        {
            using var directory = new InvestigationDirectory();
            var body = "private\0雪" + new string('x', 80000);
            var call = new CallReportDocument.Endpoint.Call { Id = 7, ReqBody = body, Uri = "/private" };
            var endpoint = new CallReportDocument.Endpoint();
            endpoint.Calls.Add(call);
            var document = new CallReportDocument();
            document.CallEndpoints.Add(endpoint);
            var descriptor = new CaptureCompletedReportDescriptor(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, CaptureDataKind.WebProxy, new(), 1, 1, null, null, null, []);
            var report = new CaptureAnalysisReport(descriptor,
                new TraceAnalysisResult([new("10.0.0.1", [], [document])]), directory.Path, [42]);
            call.ReqBody = "changed";
            await using var details = report.Details(1);
            var page = await details.ReadPageAsync(0, 100, 4096);
            Assert.True(Assert.Single(page.Rows).Cells[2].IsTruncated);
            using var bytes = new MemoryStream();
            QueryValueChunk chunk;
            do
            {
                chunk = await details.ReadValueAsync(0, 2, bytes.Length, 16381);
                bytes.Write(chunk.Bytes);
            } while (chunk.HasMore);
            using var json = JsonDocument.Parse(bytes.ToArray());
            Assert.Equal(body, json.RootElement.GetProperty("Call List")[0].GetProperty("Calls")[0].GetProperty("Request Body").GetString());
            Assert.Contains(report.Manifest(1024 * 1024, default), artifact => artifact.RelativePath == "record-ids.json");
            Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
        }

        [Fact]
        public async Task CanonicalReportMaterializationUsesTheExactFrozenManifestAndRemovesOnlyPrivateFiles()
        {
            using var directory = new InvestigationDirectory();
            var descriptor = new CaptureCompletedReportDescriptor(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, CaptureDataKind.WebProxy, new(), 1, 1, null, null, null, []);
            var report = new CaptureAnalysisReport(descriptor,
                new TraceAnalysisResult([new("10.0.0.1", [], [new CallReportDocument()])]), directory.Path, [42]);
            Assert.Equal(CaptureDataError.LimitExceeded, (await Assert.ThrowsAsync<CaptureDataException>(() =>
                report.CreateFilesAsync(1, default))).Error);
            Assert.Empty(Directory.GetDirectories(directory.Path));
            await using var files = await report.CreateFilesAsync(1024 * 1024, default);
            Assert.Equal(report.Manifest(1024 * 1024, default), files.Manifest);
            var privateDirectory = files.SourceDirectory;
            Assert.True(File.Exists(Path.Combine(privateDirectory, "record-ids.json")));
            var output = Path.Combine(directory.Path, "destination");
            Directory.CreateDirectory(output);
            var exports = new CaptureFileExports();
            var folder = await exports.RegisterFolderAsync(output);
            var receipt = await exports.SaveReportAsync(files, new(folder.Id, "report"), 1024 * 1024, _ => Task.CompletedTask);
            Assert.True(receipt.Published);
            await files.DisposeAsync();
            Assert.False(Directory.Exists(privateDirectory));
            Assert.True(File.Exists(Path.Combine(output, "report", "record-ids.json")));
        }

        private static bool IsStaging(string path) => Path.GetFileName(path).StartsWith(AnalysisReportExporter.StagingDirectoryPrefix, StringComparison.Ordinal);
    }

    internal sealed class InvestigationReportFiles : ICaptureReportFiles
    {
        internal InvestigationReportFiles(string directory, Guid? sourceId = null, Guid? snapshotId = null)
        {
            SourceDirectory = directory;
            Directory.CreateDirectory(directory);
            var content = new Dictionary<string, string>
            {
                ["index.html"] = "index",
                ["calls.js"] = "original",
                ["provenance.json"] = "{}"
            };
            foreach (var (name, value) in content)
                File.WriteAllText(Path.Combine(directory, name), value);
            Manifest = content.Keys.Order(StringComparer.Ordinal).Select(name =>
            {
                var bytes = File.ReadAllBytes(Path.Combine(directory, name));
                return new CaptureReportArtifact(name, bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)));
            }).ToArray();
            Report = new(Guid.NewGuid(), sourceId ?? Guid.NewGuid(), snapshotId ?? Guid.NewGuid(), DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow, CaptureDataKind.WebProxy, new(), 1, 1, null, null, null, []);
        }

        public string SourceDirectory { get; }
        public CaptureCompletedReportDescriptor Report { get; }
        public IReadOnlyList<CaptureReportArtifact> Manifest { get; }
        internal bool Disposed { get; private set; }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
