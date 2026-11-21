// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using XMAT.AgentAccess;
using XMAT.ApplicationServices;
using XMAT.DataAbstractionLayer;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    public class CaptureFileExportTests
    {
        [Fact]
        public async Task ExplicitOverwriteIsBoundToTheReviewedFileAndUsesAtomicPublication()
        {
            using var directory = new DirectoryFixture();
            var path = Path.Combine(directory.Path, "result.csv");
            await File.WriteAllTextAsync(path, "previous contents");
            var exports = new CaptureFileExports();
            Assert.Equal(CaptureFileFailure.DestinationExists, (await Assert.ThrowsAsync<CaptureFileException>(() =>
                exports.PrepareDestinationAsync(path, false))).Failure);
            var prepared = await exports.PrepareDestinationAsync(path, true);
            Assert.NotNull(prepared.Overwrite);
            using var database = new SqLtDatabase();
            database.Initialize();
            await using var snapshot = await database.CreateQuerySessionAsync();
            await using var result = await snapshot.ExecuteAsync("SELECT 17 AS value", new(TimeSpan.FromSeconds(10), 65536));
            await exports.SaveQueryAsync(result, new(prepared.FolderId, prepared.FileName, QueryExportFormat.Csv, prepared.Overwrite),
                65536, _ => Task.CompletedTask);
            Assert.Equal("\"value\"\r\n17\r\n", await File.ReadAllTextAsync(path));
            Assert.Single(Directory.GetFiles(directory.Path));
        }

        [Fact]
        public async Task ChangedOverwriteTargetsAreRejectedWithoutTouchingTheNewContents()
        {
            using var directory = new DirectoryFixture();
            var path = Path.Combine(directory.Path, "result.json");
            await File.WriteAllTextAsync(path, "first");
            var exports = new CaptureFileExports();
            var prepared = await exports.PrepareDestinationAsync(path, true);
            await File.WriteAllTextAsync(path, "a newer edit");
            Assert.Equal(CaptureFileFailure.InputChanged, (await Assert.ThrowsAsync<CaptureFileException>(() =>
                exports.ValidateAsync(new(prepared.FolderId, prepared.FileName, QueryExportFormat.Json, prepared.Overwrite)))).Failure);
            Assert.Equal("a newer edit", await File.ReadAllTextAsync(path));
            Assert.Single(Directory.GetFiles(directory.Path));
        }

        [Fact]
        public async Task OverwriteOptInDoesNotAuthorizeAFileThatAppearsAfterNewFilePreflight()
        {
            using var directory = new DirectoryFixture();
            var path = Path.Combine(directory.Path, "result.csv");
            var exports = new CaptureFileExports();
            var prepared = await exports.PrepareDestinationAsync(path, true);
            Assert.Null(prepared.Overwrite);
            using var database = new SqLtDatabase();
            database.Initialize();
            await using var snapshot = await database.CreateQuerySessionAsync();
            await using var result = await snapshot.ExecuteAsync("SELECT 17", new(TimeSpan.FromSeconds(10), 65536));
            Assert.Equal(CaptureFileFailure.DestinationExists, (await Assert.ThrowsAsync<CaptureFileException>(() =>
                exports.SaveQueryAsync(result, new(prepared.FolderId, prepared.FileName, QueryExportFormat.Csv),
                    65536, _ =>
                    {
                        File.WriteAllText(path, "appeared later");
                        return Task.CompletedTask;
                    }))).Failure);
            Assert.Equal("appeared later", await File.ReadAllTextAsync(path));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AgentOverwritePreservesAnExactPreimageOrBlocksBeforeReplacement(bool failBackup)
        {
            using var directory = new DirectoryFixture();
            var path = Path.Combine(directory.Path, "result.csv");
            var original = new byte[] { 0, 1, 0xff, 0x80, 5 };
            await File.WriteAllBytesAsync(path, original);
            var root = Path.Combine(directory.Path, "Backups");
            if (failBackup)
                await File.WriteAllTextAsync(root, "unavailable");
            await using var policy = new AgentAccessPolicyService(new(Path.Combine(directory.Path, "policy.json")));
            await policy.SaveAsync(new() { Enabled = true, AutomaticBackups = true }, 0);
            using var origin = new AgentMutationProtection(Guid.NewGuid(), policy, new(root));
            var exports = new CaptureFileExports();
            var prepared = await exports.PrepareDestinationAsync(path, true);
            using var database = new SqLtDatabase();
            database.Initialize();
            await using var snapshot = await database.CreateQuerySessionAsync();
            await using var result = await snapshot.ExecuteAsync("SELECT 17", new(TimeSpan.FromSeconds(10), 65536));
            var save = exports.SaveQueryAsync(result,
                new(prepared.FolderId, prepared.FileName, QueryExportFormat.Csv, prepared.Overwrite), 65536, _ => Task.CompletedTask);
            if (failBackup)
            {
                Assert.Equal(AgentAccessError.BackupFailed, (await Assert.ThrowsAsync<AgentAccessException>(() => save)).Error);
                Assert.Equal(original, await File.ReadAllBytesAsync(path));
            }
            else
            {
                await save;
                var backup = Assert.Single(origin.Backups);
                Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(backup.Directory, "previous-file.bin")));
                Assert.Equal("Committed", backup.ActionStatus);
                Assert.Contains("17", await File.ReadAllTextAsync(path));
            }
            Assert.DoesNotContain(Directory.GetFiles(directory.Path), file => Path.GetFileName(file).StartsWith(".xmat-export-", StringComparison.Ordinal));
        }

        [Fact]
        public async Task PublishesCompleteFrozenResultAsANewFileWithoutTemporaryResidue()
        {
            using var directory = new DirectoryFixture();
            using var database = new SqLtDatabase();
            database.Initialize();
            await using var snapshot = await database.CreateQuerySessionAsync();
            await using var result = await snapshot.ExecuteAsync(
                "WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<1105) SELECT x,random() AS value FROM n",
                new(TimeSpan.FromSeconds(10), 2 * 1024 * 1024));
            var first = await result.ReadPageAsync(0, 1, 4096);
            var exports = new CaptureFileExports();
            var folder = await exports.RegisterFolderAsync(directory.Path);
            var destination = new QueryFileDestination(folder.Id, "result.json", QueryExportFormat.Json);
            await exports.ValidateAsync(destination);
            var receipt = await exports.SaveQueryAsync(result, destination, 2 * 1024 * 1024, _ => Task.CompletedTask);
            Assert.True(receipt.Published);
            Assert.Equal(1105, receipt.RowCount);
            Assert.Equal(receipt.Length, new FileInfo(Path.Combine(directory.Path, "result.json")).Length);
            using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory.Path, "result.json")));
            Assert.Equal(1105, json.RootElement.GetProperty("rows").GetArrayLength());
            Assert.Equal((long)first.Rows[0].Cells[1].Value, json.RootElement.GetProperty("rows")[0][1].GetInt64());
            Assert.Single(Directory.GetFiles(directory.Path));
            Assert.Equal(CaptureFileFailure.DestinationExists,
                (await Assert.ThrowsAsync<CaptureFileException>(() => exports.ValidateAsync(destination))).Failure);
        }

        [Theory]
        [InlineData("..\\outside.csv")]
        [InlineData("nested\\result.csv")]
        [InlineData("result.csv:stream")]
        [InlineData("CON.csv")]
        [InlineData("NUL.json")]
        [InlineData("COM1.csv")]
        [InlineData("LPT\u00b2.csv")]
        [InlineData("result.csv.")]
        [InlineData("result.csv ")]
        [InlineData("result.xmatcap")]
        [InlineData("result.json")]
        public async Task InvalidFileNamesNeverCreateOrOverwriteFiles(string name)
        {
            using var directory = new DirectoryFixture();
            var exports = new CaptureFileExports();
            var folder = await exports.RegisterFolderAsync(directory.Path);
            var destination = new QueryFileDestination(folder.Id, name, QueryExportFormat.Csv);
            Assert.Equal(CaptureFileFailure.InvalidPath,
                (await Assert.ThrowsAsync<CaptureFileException>(() => exports.ValidateAsync(destination))).Failure);
            Assert.Empty(Directory.GetFiles(directory.Path));
        }

        [Fact]
        public async Task PublicationRaceCannotOverwriteTheFileCreatedAfterPreflight()
        {
            using var directory = new DirectoryFixture();
            using var database = new SqLtDatabase();
            database.Initialize();
            await using var snapshot = await database.CreateQuerySessionAsync();
            await using var result = await snapshot.ExecuteAsync("SELECT 'private'", new(TimeSpan.FromSeconds(10), 65536));
            var exports = new CaptureFileExports();
            var folder = await exports.RegisterFolderAsync(directory.Path);
            var destination = new QueryFileDestination(folder.Id, "raced.csv", QueryExportFormat.Csv);
            var path = Path.Combine(directory.Path, destination.FileName);
            var exception = await Assert.ThrowsAsync<CaptureFileException>(() =>
                exports.SaveQueryAsync(result, destination, 65536, _ =>
                {
                    File.WriteAllText(path, "human output");
                    return Task.CompletedTask;
                }));
            Assert.Equal(CaptureFileFailure.DestinationExists, exception.Failure);
            Assert.Equal("human output", File.ReadAllText(path));
            Assert.Single(Directory.GetFiles(directory.Path));
        }

        [Fact]
        public async Task QuotaAndCancellationRemoveOnlyTheOwnedUnpublishedTemporaryFile()
        {
            using var directory = new DirectoryFixture();
            var retained = Path.Combine(directory.Path, "retained.txt");
            File.WriteAllText(retained, "human");
            using var database = new SqLtDatabase();
            database.Initialize();
            await using var snapshot = await database.CreateQuerySessionAsync();
            await using var result = await snapshot.ExecuteAsync("SELECT printf('%10000s','value')", new(TimeSpan.FromSeconds(10), 65536));
            var exports = new CaptureFileExports();
            var folder = await exports.RegisterFolderAsync(directory.Path);
            var destination = new QueryFileDestination(folder.Id, "result.json", QueryExportFormat.Json);
            Assert.Equal(QueryError.ResultLimit, (await Assert.ThrowsAsync<QueryException>(() =>
                exports.SaveQueryAsync(result, destination, 100, _ => Task.CompletedTask))).Error);
            using var cancellation = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exports.SaveQueryAsync(
                result, destination, 65536, _ =>
                {
                    cancellation.Cancel();
                    return Task.CompletedTask;
                }, cancellation.Token));
            Assert.Equal(retained, Assert.Single(Directory.GetFiles(directory.Path)));
            Assert.Equal("human", File.ReadAllText(retained));
        }

        [Fact]
        public async Task RegisteredFolderReplacementRequiresANewApprovalIdentity()
        {
            using var directory = new DirectoryFixture();
            var exports = new CaptureFileExports();
            var folder = await exports.RegisterFolderAsync(directory.Path);
            var previous = directory.Path + ".previous";
            Directory.Move(directory.Path, previous);
            Directory.CreateDirectory(directory.Path);
            try
            {
                Assert.Equal(CaptureFileFailure.FolderChanged,
                    (await Assert.ThrowsAsync<CaptureFileException>(() => exports.ValidateAsync(
                        new(folder.Id, "result.csv", QueryExportFormat.Csv)))).Failure);
                var next = await exports.RegisterFolderAsync(directory.Path);
                Assert.NotEqual(folder.Id, next.Id);
            }
            finally { Directory.Delete(previous); }
        }

        [Fact]
        public async Task ProtectedLoadedSourceIsNeverAQueryExportDestination()
        {
            using var directory = new DirectoryFixture();
            var path = Path.Combine(directory.Path, "capture.csv");
            var exports = new CaptureFileExports(() => Task.FromResult<IReadOnlyList<string>>([path]));
            var folder = await exports.RegisterFolderAsync(directory.Path);
            Assert.Equal(CaptureFileFailure.ProtectedSource,
                (await Assert.ThrowsAsync<CaptureFileException>(() => exports.ValidateAsync(
                    new(folder.Id, "capture.csv", QueryExportFormat.Csv)))).Failure);
        }

        [Theory]
        [InlineData(@"\\server\share")]
        [InlineData(@"\\?\C:\exports")]
        [InlineData(@"C:exports")]
        [InlineData(@"relative\exports")]
        public async Task NetworkDeviceAndRelativeRootsAreRejectedBeforeFileAccess(string path)
        {
            var exports = new CaptureFileExports();
            Assert.Equal(CaptureFileFailure.InvalidPath,
                (await Assert.ThrowsAsync<CaptureFileException>(() => exports.RegisterFolderAsync(path))).Failure);
        }

        [Fact]
        public async Task JunctionRootCannotRedirectAnApprovedInputOrExport()
        {
            using var directory = new DirectoryFixture();
            using var outside = new DirectoryFixture();
            var link = Path.Combine(directory.Path, "redirect");
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe"))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                }
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
                $"New-Item -ItemType Junction -Path '{link.Replace("'", "''")}' -Target '{outside.Path.Replace("'", "''")}' -ErrorAction Stop | Out-Null" })
                process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            try
            {
                var error = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(process.ExitCode == 0, await error);
                Assert.Equal(CaptureFileFailure.InvalidPath,
                    (await Assert.ThrowsAsync<CaptureFileException>(() =>
                        new CaptureFileExports().RegisterFolderAsync(link))).Failure);
                var input = Path.Combine(outside.Path, "script.txt");
                await File.WriteAllTextAsync(input, "unreviewed source");
                Assert.Equal(CaptureFileFailure.InvalidPath,
                    (await Assert.ThrowsAsync<CaptureFileException>(() =>
                        new CaptureFileInputs().RegisterAsync(Path.Combine(link, "script.txt"), 65536))).Failure);
                Assert.Equal("unreviewed source", await File.ReadAllTextAsync(input));
                Assert.Single(Directory.GetFiles(outside.Path));
            }
            finally
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                if (Directory.Exists(link))
                    Directory.Delete(link);
            }
        }

        internal sealed class DirectoryFixture : IDisposable
        {
            internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"XMAT.FileExport.{Guid.NewGuid():N}");
            internal DirectoryFixture() => Directory.CreateDirectory(Path);
            public void Dispose() => Directory.Delete(Path, recursive: true);
        }
    }
}
