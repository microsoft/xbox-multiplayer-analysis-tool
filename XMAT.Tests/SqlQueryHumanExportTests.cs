// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using XMAT.ApplicationServices;
using XMAT.Models;
using XMAT.SharedInterfaces;
using XMAT.SqlQueries;
using static XMAT.Tests.SqlQueryWorkspaceTestData;

namespace XMAT.Tests
{
    public class SqlQueryHumanExportTests
    {
        public SqlQueryHumanExportTests() => Localization.LoadLanguage("en-us");

        [Fact]
        public async Task PagingAndAllOrSelectedExportUseOneImmutableExecutionBeyondTheVisiblePage()
        {
            using var directory = new DirectoryFixture();
            var result = new Result(2505)
            {
                Columns = [new(0, "same", null, null, null), new(1, "same", null, null, null), new(2, "", null, null, null)],
                Cells = row => [new(QueryValueKind.Integer, row, 8, false),
                    new(QueryValueKind.Text, "saved-" + row, 8, false), new(QueryValueKind.Null, null, 0, false)]
            };
            var sources = new Sources { Execute = (_, _, _) => Task.FromResult<IQueryResult>(result) };
            await using var workspace = new SqlQueryWorkspaceModel(sources);
            await workspace.RefreshSourcesAsync();
            var document = workspace.NewDocument();
            document.Text = "SELECT random(), * FROM original";
            Assert.True(await document.RunAsync());
            Assert.Equal(1000, document.PageRows.Count);
            document.SetRowSelected(1, true);
            Assert.True(await document.MovePageAsync(true));
            Assert.Equal(1000, document.PageOffset);
            document.SetRowSelected(1001, true);
            Assert.True(await document.MovePageAsync(true));
            Assert.Equal(505, document.PageRows.Count);
            Assert.False(document.CanNextPage);
            document.SetRowSelected(2504, true);
            Assert.True(await document.MovePageAsync(false));
            Assert.Equal(new long[] { 1, 1001, 2504 }, document.SelectedRows.Order().ToArray());
            document.Text = "a newer query that must not execute";
            document.SelectedSource = workspace.Sources.Single(source => source.Kind == QuerySourceKind.Loaded);
            var all = Path.Combine(directory.Path, "all.json");
            var selected = Path.Combine(directory.Path, "selected.json");
            Assert.True(await document.ExportAsync(all, QueryExportFormat.Json, false, false));
            Assert.True(await document.ExportAsync(selected, QueryExportFormat.Json, true, false));
            using var allJson = JsonDocument.Parse(await File.ReadAllTextAsync(all));
            using var selectedJson = JsonDocument.Parse(await File.ReadAllTextAsync(selected));
            Assert.Equal(2505, allJson.RootElement.GetProperty("rows").GetArrayLength());
            Assert.Equal(2505, allJson.RootElement.GetProperty("rowCount").GetInt64());
            Assert.Equal("same", allJson.RootElement.GetProperty("columns")[0].GetProperty("name").GetString());
            Assert.Equal("same", allJson.RootElement.GetProperty("columns")[1].GetProperty("name").GetString());
            Assert.Equal("", allJson.RootElement.GetProperty("columns")[2].GetProperty("name").GetString());
            Assert.Equal(new long[] { 1, 1001, 2504 }, selectedJson.RootElement.GetProperty("rows")
                .EnumerateArray().Select(row => row[0].GetInt64()).ToArray());
            Assert.DoesNotContain("original", await File.ReadAllTextAsync(all));
            Assert.Single(sources.ExecutedSql);
            Assert.False(result.Disposed);
            Assert.Equal(2, Directory.GetFiles(directory.Path).Length);
        }

        [Theory]
        [InlineData(QueryExportFormat.Csv)]
        [InlineData(QueryExportFormat.Json)]
        public async Task SelectedRowWrapperStreamsFullUnicodeAndBlobChunksWithOriginalOrdinals(QueryExportFormat format)
        {
            const string fullText = "=untrusted,\r\n\"λ😀\"";
            var bytes = Enumerable.Range(0, 10001).Select(value => (byte)value).ToArray();
            var result = new Result(10)
            {
                Columns = [new(0, "text", "TEXT", null, null), new(1, "blob", "BLOB", null, null)],
                Cells = _ => [new(QueryValueKind.Text, "=u", Encoding.UTF8.GetByteCount(fullText), true),
                    new(QueryValueKind.Blob, bytes[..2], bytes.Length, true)],
                ChunkSize = 7
            };
            foreach (var ordinal in new[] { 0L, 9L })
            {
                result.FullValues[(ordinal, 0)] = Encoding.UTF8.GetBytes(fullText);
                result.FullValues[(ordinal, 1)] = bytes;
            }
            await using var selection = new SqlQueryExportResult(result, [9, 0, 9]);
            await using var output = new MemoryStream();
            await QueryResultExporter.WriteAsync(selection, output, format, long.MaxValue);
            var text = Encoding.UTF8.GetString(output.ToArray());
            Assert.Equal(2, selection.RowCount);
            if (format == QueryExportFormat.Json)
            {
                using var json = JsonDocument.Parse(text);
                Assert.Equal(fullText, json.RootElement.GetProperty("rows")[0][0].GetString());
                Assert.Equal(Convert.ToBase64String(bytes), json.RootElement.GetProperty("rows")[1][1].GetProperty("base64").GetString());
            }
            else
            {
                Assert.Contains("\"=untrusted,\r\n\"\"λ😀\"\"\"", text);
                Assert.Equal(2, text.Split(Convert.ToBase64String(bytes), StringSplitOptions.None).Length - 1);
            }
            Assert.False(result.Disposed);
            await result.DisposeAsync();
        }

        [Fact]
        public async Task CancellingExportLeavesExistingDestinationUnchangedAndKeepsTheResultAlive()
        {
            using var directory = new DirectoryFixture();
            var path = Path.Combine(directory.Path, "result.csv");
            await File.WriteAllTextAsync(path, "old destination");
            var result = new Result(2);
            var sources = new Sources { Execute = (_, _, _) => Task.FromResult<IQueryResult>(result) };
            await using var workspace = new SqlQueryWorkspaceModel(sources);
            await workspace.RefreshSourcesAsync();
            var document = workspace.NewDocument();
            document.Text = "SELECT 1";
            Assert.True(await document.RunAsync());
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            result.PageReader = async (_, _, _, cancellationToken) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return result.Page(0, 2);
            };
            var export = document.ExportAsync(path, QueryExportFormat.Csv, false, true);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(await document.RunAsync());
            document.Cancel();
            Assert.False(await export.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("old destination", await File.ReadAllTextAsync(path));
            Assert.Single(Directory.GetFiles(directory.Path));
            Assert.False(result.Disposed);
            Assert.True(document.CanRun);
        }

        [Fact]
        public async Task ConfirmedCloseWaitsForExportCancellationBeforeDisposingItsStoredResult()
        {
            using var directory = new DirectoryFixture();
            var result = new Result(1);
            var sources = new Sources { Execute = (_, _, _) => Task.FromResult<IQueryResult>(result) };
            await using var workspace = new SqlQueryWorkspaceModel(sources);
            await workspace.RefreshSourcesAsync();
            var document = workspace.NewDocument();
            document.Text = "SELECT 1";
            Assert.True(await document.RunAsync());
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var draining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            result.PageReader = async (_, _, _, token) =>
            {
                entered.TrySetResult();
                try
                { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException)
                {
                    draining.TrySetResult();
                    await release.Task;
                    throw;
                }
                return result.Page(0, 1);
            };
            var export = document.ExportAsync(Path.Combine(directory.Path, "partial.json"), QueryExportFormat.Json, false, false);
            await entered.Task;
            var close = workspace.CloseDocumentAsync(document, _ => Task.FromResult(true));
            await draining.Task;
            Assert.False(close.IsCompleted);
            Assert.False(result.Disposed);
            release.TrySetResult();
            Assert.False(await export);
            Assert.True(await close);
            Assert.Equal(1, result.DisposeCalls);
            Assert.Empty(Directory.GetFiles(directory.Path));
        }

        [Fact]
        public async Task AtomicPublicationRechecksProtectedSourcesAndNeverOverwritesWithoutConsent()
        {
            using var directory = new DirectoryFixture();
            var path = Path.Combine(directory.Path, "result.csv");
            await File.WriteAllTextAsync(path, "untouched");
            IReadOnlyList<string> protectedPaths = [];
            await Assert.ThrowsAsync<IOException>(() => SqlQueryFileOperations.WriteAtomicAsync(
                path, true, ".csv", () => protectedPaths,
                async (stream, token) =>
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes("new contents"), token);
                    protectedPaths = [path];
                }, default));
            Assert.Equal("untouched", await File.ReadAllTextAsync(path));
            protectedPaths = [];
            await Assert.ThrowsAsync<IOException>(() => SqlQueryFileOperations.WriteAtomicAsync(
                path, false, ".csv", () => protectedPaths,
                (stream, token) => stream.WriteAsync(Encoding.UTF8.GetBytes("replacement"), token).AsTask(), default));
            Assert.Equal("untouched", await File.ReadAllTextAsync(path));
            Assert.Single(Directory.GetFiles(directory.Path));
            Assert.Throws<IOException>(() => SqlQueryFileOperations.ValidateDestination(
                Path.Combine(directory.Path, "XMAT.Query.owned", "result.csv"), ".csv", null));
            Assert.Throws<IOException>(() => SqlQueryFileOperations.ValidateDestination(
                Path.Combine(directory.Path, "capture.xmatcap"), ".sql", null));
        }
    }
}
