// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using XMAT.ApplicationServices;
using XMAT.DataAbstractionLayer;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    public class SqlQueryExportTests
    {
        [Fact]
        public async Task JsonPreservesDuplicateLabelsTypesAndSegmentedUnicodeAndBlobValues()
        {
            using var database = Database();
            await using var snapshot = await database.CreateQuerySessionAsync();
            var text = string.Concat(Enumerable.Repeat("\u03bb\U0001F600,\"quoted\"\r\n", 2000));
            await using var result = await snapshot.ExecuteAsync(
                $"SELECT '{text.Replace("'", "''")}' AS same,randomblob(20000) AS same,NULL AS nil,'' AS empty,1e999 AS infinite",
                Options());
            var expectedBlob = await ReadBytes(result, 0, 1);
            await snapshot.DisposeAsync();
            using var output = new MemoryStream();
            await QueryResultExporter.WriteAsync(result, output, QueryExportFormat.Json, 2 * 1024 * 1024);
            using var parsed = JsonDocument.Parse(output.ToArray());
            var root = parsed.RootElement;
            Assert.Equal("same", root.GetProperty("columns")[0].GetProperty("name").GetString());
            Assert.Equal("same", root.GetProperty("columns")[1].GetProperty("name").GetString());
            var row = root.GetProperty("rows")[0];
            Assert.Equal(text, row[0].GetString());
            Assert.Equal("blob", row[1].GetProperty("$type").GetString());
            Assert.Equal(expectedBlob, Convert.FromBase64String(row[1].GetProperty("base64").GetString()));
            Assert.Equal(JsonValueKind.Null, row[2].ValueKind);
            Assert.Equal(string.Empty, row[3].GetString());
            Assert.Equal("real", row[4].GetProperty("$type").GetString());
            Assert.Equal("Infinity", row[4].GetProperty("value").GetString());
            Assert.True(root.GetProperty("complete").GetBoolean());
            Assert.Equal(1, root.GetProperty("rowCount").GetInt64());
            Assert.False(root.TryGetProperty("sql", out _));
            Assert.False(root.TryGetProperty("sourcePath", out _));
        }

        [Fact]
        public async Task CsvQuotesUnicodeCommasQuotesAndNewlinesAndStreamsBase64WithoutPaddingBetweenChunks()
        {
            using var database = Database();
            await using var snapshot = await database.CreateQuerySessionAsync();
            var text = string.Concat(Enumerable.Repeat("\u03bb\U0001F600,\"value\"\r\n", 3000));
            await using var result = await snapshot.ExecuteAsync(
                $"SELECT '{text.Replace("'", "''")}' AS text,randomblob(20001) AS bytes,NULL AS nil,'' AS empty", Options());
            var blob = await ReadBytes(result, 0, 1);
            using var output = new MemoryStream();
            await QueryResultExporter.WriteAsync(result, output, QueryExportFormat.Csv, 2 * 1024 * 1024);
            var csv = Encoding.UTF8.GetString(output.ToArray());
            Assert.Equal("\"text\",\"bytes\",\"nil\",\"empty\"\r\n\"" +
                text.Replace("\"", "\"\"") + "\"," + Convert.ToBase64String(blob) + ",,\"\"\r\n", csv);
        }

        [Theory]
        [InlineData(QueryExportFormat.Csv)]
        [InlineData(QueryExportFormat.Json)]
        public async Task WideCompletedResultsExportEveryColumnWithoutRerunningTheQuery(QueryExportFormat format)
        {
            using var database = Database();
            await using var snapshot = await database.CreateQuerySessionAsync();
            const int count = 1999;
            var fields = string.Join(",", Enumerable.Range(0, count).Select(index => $"{index} AS c{index}"));
            await using var result = await snapshot.ExecuteAsync($"SELECT {fields}", Options());
            await snapshot.DisposeAsync();
            using var output = new MemoryStream();
            await QueryResultExporter.WriteAsync(result, output, format, 2 * 1024 * 1024);
            if (format == QueryExportFormat.Json)
            {
                using var json = JsonDocument.Parse(output.ToArray());
                Assert.Equal(count, json.RootElement.GetProperty("columns").GetArrayLength());
                Assert.Equal(Enumerable.Range(0, count).Select(index => (long)index),
                    json.RootElement.GetProperty("rows")[0].EnumerateArray().Select(value => value.GetInt64()));
            }
            else
            {
                var lines = Encoding.UTF8.GetString(output.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                Assert.Equal(2, lines.Length);
                Assert.Equal(string.Join(",", Enumerable.Range(0, count).Select(index => $"\"c{index}\"")), lines[0]);
                Assert.Equal(string.Join(",", Enumerable.Range(0, count)), lines[1]);
            }
        }

        [Theory]
        [InlineData(QueryExportFormat.Csv)]
        [InlineData(QueryExportFormat.Json)]
        public async Task ExportIncludesEveryStoredRowBeyondTheVisiblePageWithoutReexecution(QueryExportFormat format)
        {
            using var database = Database();
            await using var snapshot = await database.CreateQuerySessionAsync();
            await using var result = await snapshot.ExecuteAsync(
                "WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<1105) SELECT x,random() AS value FROM n",
                Options());
            var first = await result.ReadPageAsync(0, 1, 64 * 1024);
            var last = await result.ReadPageAsync(1104, 1, 64 * 1024);
            await snapshot.DisposeAsync();
            using var output = new MemoryStream();
            await QueryResultExporter.WriteAsync(result, output, format, 2 * 1024 * 1024);
            if (format == QueryExportFormat.Json)
            {
                using var json = JsonDocument.Parse(output.ToArray());
                var rows = json.RootElement.GetProperty("rows");
                Assert.Equal(1105, rows.GetArrayLength());
                Assert.Equal(first.Rows[0].Cells[1].Value, rows[0][1].GetInt64());
                Assert.Equal(last.Rows[0].Cells[1].Value, rows[1104][1].GetInt64());
            }
            else
            {
                var lines = Encoding.UTF8.GetString(output.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                Assert.Equal(1106, lines.Length);
                Assert.Equal($"1,{first.Rows[0].Cells[1].Value}", lines[1]);
                Assert.Equal($"1105,{last.Rows[0].Cells[1].Value}", lines[^1]);
            }
        }

        [Theory]
        [InlineData(QueryExportFormat.Csv)]
        [InlineData(QueryExportFormat.Json)]
        public async Task OutputQuotaAndCancellationFailExplicitlyAndLeaveCompletedResultReadable(QueryExportFormat format)
        {
            using var database = Database();
            await using var snapshot = await database.CreateQuerySessionAsync();
            await using var result = await snapshot.ExecuteAsync("SELECT printf('%10000s','value') AS body", Options());
            using var bounded = new MemoryStream();
            var failure = await Assert.ThrowsAsync<QueryException>(() =>
                QueryResultExporter.WriteAsync(result, bounded, format, 100));
            Assert.Equal(QueryError.ResultLimit, failure.Error);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            using var cancelled = new MemoryStream();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                QueryResultExporter.WriteAsync(result, cancelled, format, 2 * 1024 * 1024, cancellation.Token));
            Assert.Equal(1, result.RowCount);
            Assert.Single((await result.ReadPageAsync(0, 1, 64 * 1024)).Rows);
        }

        private static SqLtDatabase Database()
        {
            var database = new SqLtDatabase();
            database.Initialize();
            return database;
        }

        private static QueryExecutionOptions Options() => new(TimeSpan.FromSeconds(10), 2 * 1024 * 1024);

        private static async Task<byte[]> ReadBytes(IQueryResult result, long row, int column)
        {
            using var output = new MemoryStream();
            long offset = 0;
            QueryValueChunk chunk;
            do
            {
                chunk = await result.ReadValueAsync(row, column, offset, 4096);
                output.Write(chunk.Bytes);
                offset += chunk.Bytes.Length;
            } while (chunk.HasMore);
            return output.ToArray();
        }
    }
}
