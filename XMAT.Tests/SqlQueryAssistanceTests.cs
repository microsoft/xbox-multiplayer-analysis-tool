// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text;
using XMAT.Models;
using XMAT.SharedInterfaces;
using XMAT.SqlQueries;
using static XMAT.Tests.SqlQueryWorkspaceTestData;

namespace XMAT.Tests
{
    public class SqlQueryAssistanceTests
    {
        private static readonly QueryTable Table = new("A table", "table",
        [
            new(0, "id", "INTEGER", false, true, false),
            new(1, "some value", "TEXT", true, false, false),
            new(2, "a\"quote", "TEXT", true, false, false)
        ]);
        public SqlQueryAssistanceTests() => Localization.LoadLanguage("en-us");

        [Theory]
        [InlineData("SELECT '--not a comment")]
        [InlineData("SELECT 'it''s a value")]
        [InlineData("SELECT 1 -- SELECT ")]
        [InlineData("SELECT /* FROM A table")]
        public void CompletionDoesNotInsertIntoStringsOrComments(string sql) =>
            Assert.Empty(SqlQueryText.Complete(sql, sql.Length, [Table]).Items);

        [Theory]
        [InlineData("SELECT t.so FROM \"A table\" AS t", "so", "some value", "\"some value\"")]
        [InlineData("SELECT \"A table\".a FROM \"A table\"", "a", "a\"quote", "\"a\"\"quote\"")]
        [InlineData("SELECT t.\"som\" FROM \"A table\" t", "\"som", "some value", "\"some value\"")]
        [InlineData("SELECT t.[som] FROM \"A table\" t", "[som", "some value", "\"some value\"")]
        [InlineData("SELECT t.`som` FROM \"A table\" AS t", "`som", "some value", "\"some value\"")]
        public void QualifiedCompletionUsesRealColumnsAndEscapesIdentifierReplacement(
            string sql, string prefix, string expectedName, string insertion)
        {
            var start = sql.IndexOf("." + prefix, StringComparison.Ordinal) + 1;
            var context = SqlQueryText.Complete(sql, start + prefix.Length, [Table]);
            var completion = Assert.Single(context.Items);
            Assert.Equal(expectedName, completion.Text);
            Assert.Equal(insertion, completion.Insertion);
            Assert.Equal(start, context.Start);
            var replacement = sql.Remove(context.Start, context.Length).Insert(context.Start, completion.Insertion);
            Assert.DoesNotContain("\"some value\"\"", replacement);
            Assert.DoesNotContain("\"some value\"]", replacement);
            Assert.DoesNotContain("\"some value\"`", replacement);
        }

        [Fact]
        public void CompletionSkipsFakeAliasesInStringsAndCommentsAndDoesNotInventComplexCteColumns()
        {
            var sql = "SELECT wrong. FROM \"A table\" AS right -- JOIN \"A table\" wrong";
            var caret = sql.IndexOf("wrong.", StringComparison.Ordinal) + "wrong.".Length;
            Assert.Empty(SqlQueryText.Complete(sql, caret, [Table]).Items);
            sql = "WITH complex AS (SELECT 1 AS invented) SELECT complex.";
            Assert.Empty(SqlQueryText.Complete(sql, sql.Length, [Table]).Items);
            sql = "SELECT right. FROM \"A table\" right";
            caret = sql.IndexOf("right.", StringComparison.Ordinal) + "right.".Length;
            Assert.Equal(3, SqlQueryText.Complete(sql, caret, [Table]).Items.Count);
        }

        [Fact]
        public void LocalCompletionOffersTablesKeywordsFunctionsAndAliasQualifiedColumnsWithoutWrites()
        {
            var sql = "SELECT  FROM \"A table\" t";
            var context = SqlQueryText.Complete(sql, "SELECT ".Length, [Table]);
            Assert.Contains(context.Items, item => item.Insertion == "\"A table\"");
            Assert.Contains(context.Items, item => item.Insertion == "COUNT(");
            Assert.Contains(context.Items, item => item.Insertion == "SELECT");
            Assert.Contains(context.Items, item => item.Insertion == "\"t\".\"some value\"");
            Assert.DoesNotContain(context.Items, item => item.Insertion is "DELETE" or "UPDATE" or "ATTACH" or "PRAGMA");
        }

        [Fact]
        public void ErrorOffsetsTranslateUtf8AndSelectedTextOffsetsWithoutSplittingSurrogatePairs()
        {
            const string text = "SELECT 'λ😀', unknown";
            var prefix = text[..text.IndexOf("unknown", StringComparison.Ordinal)];
            Assert.Equal(10 + prefix.Length,
                SqlQueryText.EditorOffset(text, Encoding.UTF8.GetByteCount(prefix), 10));
            var emoji = text.IndexOf("😀", StringComparison.Ordinal);
            Assert.Equal(emoji,
                SqlQueryText.EditorOffset(text, Encoding.UTF8.GetByteCount(text[..emoji]) + 2, 0));
            Assert.Equal(text.Length + 7, SqlQueryText.EditorOffset(text, int.MaxValue, 7));
            Assert.Equal(new SqlQueryExecutionText("λ😀", 8), SqlQueryText.ExecutionText("SELECT 'λ😀';", 8, 3));
        }

        [Fact]
        public void GeneratedSelectUsesActualOrdinalsAndHiddenMetadataWithoutExecutingAnything()
        {
            var table = new QueryTable("a\"table", "table",
                Table.Columns.Concat([new QuerySchemaColumn(3, "hidden", "TEXT", true, false, true)]).ToArray());
            var select = SqlQueryExamples.Select(table, false);
            Assert.Equal("SELECT \"id\", \"some value\", \"a\"\"quote\"\r\nFROM \"a\"\"table\";", select);
            Assert.EndsWith("\r\nLIMIT 1000;", SqlQueryExamples.Select(table, true));
            var item = new SqlQuerySchemaItem(table, table.Columns[0], "");
            Assert.Contains(Localization.GetLocalizedString("SQL_PRIMARY_KEY"), item.DisplayName);
            Assert.Contains(Localization.GetLocalizedString("SQL_NOT_NULL"), item.ToolTip);
        }

        [Fact]
        public void SampleCatalogRequiresEveryReferencedRealColumnAndUsesStoredCaptureNames()
        {
            var missing = new QueryTable("WebProxyConnections", "table",
                [new(0, "Host", "TEXT", true, false, false)]);
            Assert.Empty(SqlQueryExamples.Create([missing]));
            var web = new QueryTable("WebProxyConnections", "table", new[]
            {
                "id", "RequestNumber", "RequestMethod", "RequestHost", "RequestPath", "RequestStatus",
                "RequestTimestamp", "ResponseTimestamp", "DeviceName"
            }.Select((name, index) => new QuerySchemaColumn(index, name, "TEXT", true, false, false)).ToArray());
            var packets = new QueryTable("NetworkTracePackets", "table",
                [new(0, "ProcessId", "INTEGER", true, false, false), new(1, "DeviceName", "TEXT", true, false, false)]);
            var samples = SqlQueryExamples.Create([web, packets]);
            Assert.Equal(9, samples.Count);
            Assert.Contains(samples, item => item.Sql.Contains("CAST(\"RequestStatus\" AS INTEGER)", StringComparison.Ordinal));
            Assert.Contains(samples, item => item.Sql.StartsWith("WITH ", StringComparison.Ordinal) &&
                item.Sql.Contains("JOIN \"Latest\"", StringComparison.Ordinal));
            Assert.Contains(samples, item => item.Sql.Contains("\"ProcessId\"", StringComparison.Ordinal));
            Assert.DoesNotContain(samples, item => item.Sql.Contains("\"Host\"", StringComparison.Ordinal) ||
                item.Sql.Contains("json_extract", StringComparison.Ordinal) || item.Sql.Contains("\"Protocol\"", StringComparison.Ordinal));
            var partial = web with { Columns = web.Columns.Where(column => column.Name != "id").ToArray() };
            Assert.DoesNotContain(SqlQueryExamples.Create([partial]), item => item.Sql.Contains("\"id\"", StringComparison.Ordinal));
        }

        [Fact]
        public void ResultPresentationSeparatesNullEmptyTextMixedStorageTypesAndDuplicateLabels()
        {
            var columns = new[]
            {
                new QueryColumn(0, "", null, null, null),
                new QueryColumn(1, "same", null, null, null),
                new QueryColumn(2, "same", null, null, null),
                new QueryColumn(3, "same [2]", null, null, null),
                new QueryColumn(4, "", null, null, null)
            };
            var labels = SqlQueryResultPresentation.ColumnLabels(columns);
            Assert.Equal(5, labels.Distinct().Count());
            Assert.Equal("same", labels[1]);
            var cells = new QueryCell[]
            {
                new(QueryValueKind.Null, null, 0, false),
                new(QueryValueKind.Text, "", 0, false),
                new(QueryValueKind.Integer, long.MaxValue, 8, false),
                new(QueryValueKind.Real, 1.25, 8, false),
                new(QueryValueKind.Blob, new byte[4096], 10000000, true)
            };
            var row = SqlQueryResultPresentation.Row(new(1234, cells), columns);
            Assert.NotEqual(row.Cells[0].Preview, row.Cells[1].Preview);
            Assert.Equal("NULL", row.Cells[0].Preview);
            Assert.Equal("9223372036854775807", row.Cells[2].Preview);
            Assert.Equal("1.25", row.Cells[3].Preview);
            Assert.True(row.Cells[4].IsTruncated);
            Assert.True(row.Cells[4].Preview.Length <= 257);
            Assert.Equal(1235, row.DisplayOrdinal);
        }
    }
}
