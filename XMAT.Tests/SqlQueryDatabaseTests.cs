// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Reflection;
using System.Text;
using XMAT.DataAbstractionLayer;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    public class SqlQueryDatabaseTests
    {
        private static QueryExecutionOptions Options(long bytes = 2 * 1024 * 1024) =>
            new(TimeSpan.FromSeconds(10), bytes);

        [Fact]
        public async Task SchemaIncludesRealIdAndSnapshotDoesNotFollowLaterSourceWrites()
        {
            using var database = CreateDatabase();
            var schema = await database.GetQuerySchemaAsync();
            var table = Assert.Single(schema);
            Assert.Equal("Data", table.Name);
            Assert.Equal(new[] { "id", "Value", "Name" }, table.Columns.Select(column => column.Name));
            Assert.True(table.Columns[0].PrimaryKey);
            Assert.False(table.Columns[0].Nullable);
            await using var snapshot = await database.CreateQuerySessionAsync();
            database.TableByName("Data").AddRow(2L, "later");

            await using var result = await snapshot.ExecuteAsync("SELECT count(*) AS Count FROM Data", Options());
            var page = await result.ReadPageAsync(0, 100, 64 * 1024);
            Assert.Equal(1L, page.Rows[0].Cells[0].Value);
            Assert.Equal(snapshot.SnapshotId, result.SnapshotId);
            await using var newer = await database.CreateQuerySessionAsync();
            await using var updated = await newer.ExecuteAsync("SELECT count(*) FROM Data", Options());
            Assert.Equal(2L, (await updated.ReadPageAsync(0, 1, 64 * 1024)).Rows[0].Cells[0].Value);
        }

        [Fact]
        public async Task ReadonlyCaptureSupportsTheSameQueryAbstractionAndReleasesFiles()
        {
            using var directory = new TestDirectory();
            var path = Path.Join(directory.Path, "capture.xmatcap");
            using (var database = new SqLtDatabase())
            {
                database.Initialize();
                database.CreateTable("CaptureDevices",
                    new Field<long>("DeviceType"), new Field<string>("DeviceName"), new Field<long>("CaptureType"),
                    new Field<long>("Value"), new Field<string>("Name")).AddRow(0L, "fixture", 0L, 1L, "first");
                database.SaveToFile(path);
            }
            var before = File.ReadAllBytes(path);
            using (var loaded = new SqLtReadonlyDatabase(path))
            {
                var schema = await loaded.GetQuerySchemaAsync();
                Assert.Single(schema);
                await using var snapshot = await loaded.CreateQuerySessionAsync();
                await using var result = await snapshot.ExecuteAsync("SELECT Value,Name FROM CaptureDevices", Options());
                var page = await result.ReadPageAsync(0, 100, 64 * 1024);
                Assert.Equal(1L, page.Rows[0].Cells[0].Value);
                Assert.Equal("first", page.Rows[0].Cells[1].Value);
            }
            Assert.Equal(before, File.ReadAllBytes(path));
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }

        [Fact]
        public async Task ArbitraryResultsPreserveOrdinalsMixedTypesAndEmptyValues()
        {
            using var database = CreateDatabase();
            await using var session = await database.CreateQuerySessionAsync();
            await using var result = await session.ExecuteAsync(
                "SELECT 9223372036854775807 AS x,NULL AS x,'' AS \"\",x'' AS bytes,3.5 AS real,'a'||char(0)||'b' AS text " +
                "UNION ALL SELECT -1,42,'λ😀',x'0001FF',4,'second'", Options());
            Assert.Equal(2, result.RowCount);
            Assert.Equal(new[] { "x", "x", "", "bytes", "real", "text" }, result.Columns.Select(column => column.Name));
            var first = await result.ReadPageAsync(0, 1, 64 * 1024);
            Assert.True(first.HasMore);
            Assert.Equal(long.MaxValue, first.Rows[0].Cells[0].Value);
            Assert.Equal(QueryValueKind.Null, first.Rows[0].Cells[1].Kind);
            Assert.Null(first.Rows[0].Cells[1].Value);
            Assert.Equal(QueryValueKind.Text, first.Rows[0].Cells[2].Kind);
            Assert.Equal(string.Empty, first.Rows[0].Cells[2].Value);
            Assert.Equal(QueryValueKind.Blob, first.Rows[0].Cells[3].Kind);
            Assert.Empty((byte[])first.Rows[0].Cells[3].Value);
            Assert.Equal(3.5, first.Rows[0].Cells[4].Value);
            Assert.Equal("a\0b", first.Rows[0].Cells[5].Value);
            var second = await result.ReadPageAsync(1, 1, 64 * 1024);
            Assert.False(second.HasMore);
            Assert.Equal(QueryValueKind.Integer, second.Rows[0].Cells[1].Kind);
            Assert.Equal("λ😀", second.Rows[0].Cells[2].Value);
            Assert.Equal(new byte[] { 0, 1, 255 }, second.Rows[0].Cells[3].Value);
            Assert.Equal(QueryValueKind.Integer, second.Rows[0].Cells[4].Kind);
            var chunk = await result.ReadValueAsync(0, 5, 0, 100);
            Assert.Equal(Encoding.UTF8.GetBytes("a\0b"), chunk.Bytes);
        }

        [Fact]
        public async Task EmptyResultsRetainMetadataAndComputedValuesAreNotReexecuted()
        {
            using var database = CreateDatabase();
            await using var session = await database.CreateQuerySessionAsync();
            await using var empty = await session.ExecuteAsync(
                "SELECT Value AS number,Name AS label FROM Data WHERE 0", Options());
            Assert.Equal(0, empty.RowCount);
            var page = await empty.ReadPageAsync(0, 100, 64 * 1024);
            Assert.Equal(2, page.Columns.Count);
            Assert.Empty(page.Rows);
            Assert.False(page.HasMore);

            await using var frozen = await session.ExecuteAsync("SELECT random()", Options());
            var first = await frozen.ReadPageAsync(0, 1, 64 * 1024);
            var again = await frozen.ReadPageAsync(0, 1, 64 * 1024);
            Assert.Equal(first.Rows[0].Cells[0].Value, again.Rows[0].Cells[0].Value);
        }

        [Theory]
        [InlineData("DELETE FROM Data")]
        [InlineData("UPDATE Data SET Name='changed'")]
        [InlineData("INSERT INTO Data(Value,Name) VALUES(3,'changed')")]
        [InlineData("DROP TABLE Data")]
        [InlineData("CREATE TABLE NewData(x)")]
        [InlineData("ALTER TABLE Data ADD COLUMN NewColumn TEXT")]
        [InlineData("WITH q AS (SELECT 1) DELETE FROM Data")]
        [InlineData("PRAGMA query_only=OFF")]
        [InlineData("PRAGMA user_version=1")]
        [InlineData("ATTACH ':memory:' AS other")]
        [InlineData("DETACH main")]
        [InlineData("BEGIN")]
        [InlineData("SAVEPOINT changes")]
        [InlineData("SELECT 1; UPDATE Data SET Name='changed'")]
        [InlineData("SELECT 1; SELECT 2")]
        [InlineData("SELECT load_extension('untrusted')")]
        [InlineData("SELECT writefile('untrusted', 'data')")]
        public async Task UnsafeOrMultipleStatementsAreRejectedBeforeExecution(string sql)
        {
            using var database = CreateDatabase();
            await using var session = await database.CreateQuerySessionAsync();
            await Assert.ThrowsAsync<QueryException>(() => session.ExecuteAsync(sql, Options()));
            await using var after = await session.ExecuteAsync("SELECT Name FROM Data", Options());
            Assert.Equal("first", (await after.ReadPageAsync(0, 1, 64 * 1024)).Rows[0].Cells[0].Value);
            Assert.Equal("first", database.TableByName("Data").Dataset.DataRecords.Single().Str("Name"));
        }

        [Fact]
        public async Task NativeStatementBoundariesHandleCommentsStringsCtesAndWindows()
        {
            using var database = CreateDatabase();
            await using var session = await database.CreateQuerySessionAsync();
            await using var result = await session.ExecuteAsync(
                "/* a leading ; */ WITH q(x) AS (VALUES('a;''b'),('second')) " +
                "SELECT x,row_number() OVER (ORDER BY x) AS n FROM q; -- trailing ;", Options());
            var rows = (await result.ReadPageAsync(0, 100, 64 * 1024)).Rows;
            Assert.Equal(2, rows.Count);
            Assert.Equal("a;'b", rows[0].Cells[0].Value);
            Assert.Equal(1L, rows[0].Cells[1].Value);
            await Assert.ThrowsAsync<QueryException>(() => session.ExecuteAsync("-- only a comment", Options()));
            await Assert.ThrowsAsync<QueryException>(() => session.ExecuteAsync("SELECT $missing", Options()));
        }

        [Fact]
        public async Task VacuumIntoCannotCreateAnExternalFile()
        {
            using var directory = new TestDirectory();
            var target = Path.Join(directory.Path, "must-not-exist.sqlite");
            using var database = CreateDatabase();
            await using var session = await database.CreateQuerySessionAsync();
            await Assert.ThrowsAsync<QueryException>(() => session.ExecuteAsync(
                $"VACUUM INTO '{target.Replace("'", "''")}'", Options()));
            Assert.False(File.Exists(target));
        }

        [Fact]
        public async Task CpuBoundQueriesHonorCancellationAndDeadlinesAndRemainUsable()
        {
            const string expensive = "WITH RECURSIVE n(x) AS (VALUES(0) UNION ALL SELECT x+1 FROM n WHERE x<10000000) SELECT sum(x) FROM n";
            using var database = CreateDatabase();
            await using var session = await database.CreateQuerySessionAsync();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                session.ExecuteAsync(expensive, Options(), cancellation.Token));
            var timeout = await Assert.ThrowsAsync<QueryException>(() => session.ExecuteAsync(
                expensive, new QueryExecutionOptions(TimeSpan.FromMilliseconds(20))));
            Assert.Equal(QueryError.Timeout, timeout.Error);
            await using var after = await session.ExecuteAsync("SELECT 'usable'", Options());
            Assert.Equal("usable", (await after.ReadPageAsync(0, 1, 64 * 1024)).Rows[0].Cells[0].Value);
        }

        [Fact]
        public async Task StoreQuotaRejectsTheWholeResultWithoutBreakingTheSession()
        {
            using var database = CreateDatabase();
            await using var session = await database.CreateQuerySessionAsync();
            var error = await Assert.ThrowsAsync<QueryException>(() => session.ExecuteAsync(
                "WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<1000) SELECT randomblob(1024) FROM n",
                Options(32 * 1024)));
            Assert.Equal(QueryError.ResultLimit, error.Error);
            await using var result = await session.ExecuteAsync("SELECT 42", Options());
            Assert.Equal(42L, (await result.ReadPageAsync(0, 1, 64 * 1024)).Rows[0].Cells[0].Value);
        }

        [Fact]
        public async Task LargeValuesHaveBoundedPreviewsAndExactByteChunks()
        {
            var text = string.Concat(Enumerable.Repeat("λ😀", 20000));
            using var database = CreateDatabase();
            await using var session = await database.CreateQuerySessionAsync();
            await using var result = await session.ExecuteAsync(
                $"SELECT '{text}' AS body,zeroblob(100000) AS payload", Options());
            var page = await result.ReadPageAsync(0, 100, 8192);
            Assert.True(page.Rows[0].Cells[0].IsTruncated);
            Assert.True(page.Rows[0].Cells[1].IsTruncated);
            Assert.DoesNotContain("\uFFFD", (string)page.Rows[0].Cells[0].Value);
            using var collected = new MemoryStream();
            long offset = 0;
            QueryValueChunk chunk;
            do
            {
                chunk = await result.ReadValueAsync(0, 0, offset, 4096);
                collected.Write(chunk.Bytes);
                offset += chunk.Bytes.Length;
            } while (chunk.HasMore);
            Assert.Equal(text, Encoding.UTF8.GetString(collected.ToArray()));
            var blob = await result.ReadValueAsync(0, 1, 99990, 100);
            Assert.Equal(10, blob.Bytes.Length);
            Assert.False(blob.HasMore);
        }

        [Fact]
        public async Task DisposalDrainsReadersAndRemovesOnlyOwnedTemporaryStores()
        {
            using var database = CreateDatabase();
            var session = await database.CreateQuerySessionAsync();
            var result = await session.ExecuteAsync("SELECT Name FROM Data", Options());
            var snapshotDirectory = Workspace(session);
            var resultDirectory = Workspace(result);
            Assert.True(Directory.Exists(snapshotDirectory));
            Assert.True(Directory.Exists(resultDirectory));
            var page = result.ReadPageAsync(0, 1, 64 * 1024);
            var disposing = result.DisposeAsync().AsTask();
            Assert.Single((await page).Rows);
            await disposing;
            await session.DisposeAsync();
            Assert.False(Directory.Exists(snapshotDirectory));
            Assert.False(Directory.Exists(resultDirectory));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => result.ReadPageAsync(0, 1, 64 * 1024));
        }

        [Fact]
        public async Task MalformedUtf8TextStillHasABoundedPreviewAndExactByteChunks()
        {
            var bytes = Enumerable.Repeat((byte)0xff, 12000).ToArray();
            using var database = CreateDatabase();
            await using var session = await database.CreateQuerySessionAsync();
            await using var result = await session.ExecuteAsync(
                $"SELECT CAST(x'{Convert.ToHexString(bytes)}' AS TEXT) AS body", Options());
            var page = await result.ReadPageAsync(0, 1, 8192);
            Assert.True(page.Rows[0].Cells[0].IsTruncated);
            Assert.Contains("\uFFFD", (string)page.Rows[0].Cells[0].Value);
            Assert.True(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(page).Length <= 8192);
            Assert.Equal(bytes, (await result.ReadValueAsync(0, 0, 0, 16384)).Bytes);
        }

        private static SqLtDatabase CreateDatabase()
        {
            var database = new SqLtDatabase();
            database.Initialize();
            database.CreateTable("Data", new Field<long>("Value"), new Field<string>("Name")).AddRow(1L, "first");
            return database;
        }

        private static string Workspace(object owner) =>
            (string)owner.GetType().GetField("_workspace", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner);

        private sealed class TestDirectory : IDisposable
        {
            internal string Path { get; } = System.IO.Path.Join(AppContext.BaseDirectory, $"SqlQueryFixture-{Guid.NewGuid():N}");
            internal TestDirectory() => Directory.CreateDirectory(Path);
            public void Dispose() => Directory.Delete(Path, recursive: true);
        }
    }
}
