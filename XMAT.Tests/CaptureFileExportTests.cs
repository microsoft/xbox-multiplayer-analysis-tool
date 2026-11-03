// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using XMAT.ApplicationServices;
using XMAT.DataAbstractionLayer;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    public class CaptureFileExportTests
    {
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
