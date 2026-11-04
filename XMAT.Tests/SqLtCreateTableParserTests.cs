// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.DataAbstractionLayer;
using XMAT.Tests.Fixtures;

namespace XMAT.Tests
{
    public class SqLtCreateTableParserTests
    {
        [Fact]
        public void RestrictedKeywords_MatchSqlite3503KeywordTable()
        {
            const string Sqlite3503Keywords =
                "ABORT ACTION ADD AFTER ALL ALTER ALWAYS ANALYZE AND AS ASC ATTACH AUTOINCREMENT BEFORE BEGIN " +
                "BETWEEN BY CASCADE CASE CAST CHECK COLLATE COLUMN COMMIT CONFLICT CONSTRAINT CREATE CROSS CURRENT " +
                "CURRENT_DATE CURRENT_TIME CURRENT_TIMESTAMP DATABASE DEFAULT DEFERRED DEFERRABLE DELETE DESC DETACH " +
                "DISTINCT DO DROP END EACH ELSE ESCAPE EXCEPT EXCLUSIVE EXCLUDE EXISTS EXPLAIN FAIL FILTER FIRST " +
                "FOLLOWING FOR FOREIGN FROM FULL GENERATED GLOB GROUP GROUPS HAVING IF IGNORE IMMEDIATE IN INDEX " +
                "INDEXED INITIALLY INNER INSERT INSTEAD INTERSECT INTO IS ISNULL JOIN KEY LAST LEFT LIKE LIMIT MATCH " +
                "MATERIALIZED NATURAL NO NOT NOTHING NOTNULL NULL NULLS OF OFFSET ON OR ORDER OTHERS OUTER OVER " +
                "PARTITION PLAN PRAGMA PRECEDING PRIMARY QUERY RAISE RANGE RECURSIVE REFERENCES REGEXP REINDEX RELEASE " +
                "RENAME REPLACE RESTRICT RETURNING RIGHT ROLLBACK ROW ROWS SAVEPOINT SELECT SET TABLE TEMP TEMPORARY " +
                "THEN TIES TO TRANSACTION TRIGGER UNBOUNDED UNION UNIQUE UPDATE USING VACUUM VALUES VIEW VIRTUAL WHEN " +
                "WHERE WINDOW WITH WITHIN WITHOUT";
            string[] expected =
                Sqlite3503Keywords.Split(
                    ' ',
                    StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal(
                expected.OrderBy(
                    keyword => keyword,
                    StringComparer.Ordinal),
                SqLtCreateTableParser
                    .RestrictedKeywords
                    .OrderBy(
                        keyword => keyword,
                        StringComparer.Ordinal));
        }

        [Fact]
        public void Parser_AcceptsCanonicalProductSchemas()
        {
            foreach (string name in new[]
            {
                "CaptureDevices",
                "WebProxyConnections",
                "NetworkTracePackets",
            })
            {
                string ddl = name switch
                {
                    "CaptureDevices" =>
                        HostileSqliteSchemas.CaptureDevicesDdl,
                    "WebProxyConnections" =>
                        HostileSqliteSchemas.WebProxyConnectionsDdl,
                    _ =>
                        HostileSqliteSchemas.NetworkTracePacketsDdl,
                };

                SqLtValidatedTable table =
                    SqLtCreateTableParser.Parse(
                        ddl,
                        name,
                        2,
                        SqLtSchemaProfile.KnownTables[name]);

                Assert.Equal(name, table.CanonicalName);
                Assert.Equal(
                    SqLtSchemaProfile.KnownTables[name]
                        .RequiredColumns.Count,
                    table.Columns.Count);
            }
        }

        [Fact]
        public void Parser_AcceptsFormattingQuotingAndPlainExtensions()
        {
            const string Ddl =
                "  cReAtE \r\n TaBlE [CaptureDevices] ( " +
                "\"id\" integer primary key, " +
                "`DeviceType` INTEGER, [DeviceName] text, " +
                "\"CaptureType\" INTEGER, ExtraText TEXT, " +
                "ExtraNumber INTEGER, ExtraUntyped ) ; ";

            SqLtValidatedTable table =
                SqLtCreateTableParser.Parse(
                    Ddl,
                    "CaptureDevices",
                    2,
                    SqLtSchemaProfile.KnownTables[
                        "CaptureDevices"]);

            Assert.Equal(6, table.Columns.Count);
            Assert.Equal(
                SqLtDeclaredType.Text,
                table.Columns[3].DeclaredType);
            Assert.Equal(
                SqLtDeclaredType.Integer,
                table.Columns[4].DeclaredType);
            Assert.Equal(
                SqLtDeclaredType.None,
                table.Columns[5].DeclaredType);
        }

        [Fact]
        public void Parser_AcceptsExactlySqlitesAsciiWhitespaceCharacters()
        {
            const string Ddl =
                "CREATE\tTABLE\r\nCaptureDevices\f(\n" +
                "id INTEGER PRIMARY KEY,\rDeviceType INTEGER," +
                "DeviceName TEXT,CaptureType INTEGER )";

            SqLtValidatedTable table =
                SqLtCreateTableParser.Parse(
                    Ddl,
                    "CaptureDevices",
                    2,
                    SqLtSchemaProfile.KnownTables[
                        "CaptureDevices"]);

            Assert.Equal(3, table.Columns.Count);
        }

        [Theory]
        [InlineData('\v')]
        [InlineData('\u00a0')]
        public void Parser_RejectsNonSqliteWhitespace(char whitespace)
        {
            string ddl =
                HostileSqliteSchemas.CaptureDevicesDdl
                    .Replace(
                        "DeviceName TEXT",
                        "DeviceName" +
                        whitespace +
                        "TEXT",
                        StringComparison.Ordinal);

            SqLtSchemaException exception =
                Assert.Throws<SqLtSchemaException>(
                    () => SqLtCreateTableParser.Parse(
                        ddl,
                        "CaptureDevices",
                        2,
                        SqLtSchemaProfile.KnownTables[
                            "CaptureDevices"]));

            Assert.Equal(
                SqLtSchemaFailure.Unsupported,
                exception.Failure);
        }

        [Theory]
        [InlineData("CONSTRAINT")]
        [InlineData("ALWAYS")]
        [InlineData("RETURNING")]
        [InlineData("WITHIN")]
        public void Parser_RejectsUnquotedKeywordsButAllowsQuotedKeywordNames(
            string keyword)
        {
            string unquoted =
                HostileSqliteSchemas.CaptureDevicesDdl
                    .Insert(
                        HostileSqliteSchemas
                            .CaptureDevicesDdl.Length - 1,
                        "," + keyword + " TEXT");
            string quoted =
                HostileSqliteSchemas.CaptureDevicesDdl
                    .Insert(
                        HostileSqliteSchemas
                            .CaptureDevicesDdl.Length - 1,
                        ",\"" + keyword + "\" TEXT");

            Assert.Throws<SqLtSchemaException>(
                () => SqLtCreateTableParser.Parse(
                    unquoted,
                    "CaptureDevices",
                    2,
                    SqLtSchemaProfile.KnownTables[
                        "CaptureDevices"]));
            SqLtValidatedTable table =
                SqLtCreateTableParser.Parse(
                    quoted,
                    "CaptureDevices",
                    2,
                    SqLtSchemaProfile.KnownTables[
                        "CaptureDevices"]);
            Assert.Equal(
                keyword,
                table.Columns[^1].Name);
        }

        [Theory]
        [MemberData(nameof(UnsupportedDdl))]
        public void Parser_RejectsUnsupportedClausesAndObjects(
            string ddl)
        {
            SqLtSchemaException exception =
                Assert.Throws<SqLtSchemaException>(
                    () => SqLtCreateTableParser.Parse(
                        ddl,
                        "CaptureDevices",
                        2,
                        SqLtSchemaProfile.KnownTables[
                            "CaptureDevices"]));

            Assert.Equal(
                SqLtSchemaFailure.Unsupported,
                exception.Failure);
        }

        [Fact]
        public void Parser_BoundsExtensionColumns()
        {
            SqLtSchemaException exception =
                Assert.Throws<SqLtSchemaException>(
                    () => SqLtCreateTableParser.Parse(
                        HostileSqliteSchemas.LongExtensionDdl(
                            SqLtSchemaProfile.MaxExtensionColumns +
                            1),
                        "WebProxyConnections",
                        2,
                        SqLtSchemaProfile.KnownTables[
                            "WebProxyConnections"]));

            Assert.Equal(
                SqLtSchemaFailure.Limit,
                exception.Failure);
        }

        public static IEnumerable<object[]> UnsupportedDdl()
        {
            yield return new object[]
            {
                "CREATE TABLE CaptureDevices(id INTEGER PRIMARY KEY," +
                "DeviceType INTEGER DEFAULT 0,DeviceName TEXT,CaptureType INTEGER)",
            };
            yield return new object[]
            {
                "CREATE TABLE CaptureDevices(id INTEGER PRIMARY KEY," +
                "DeviceType INTEGER,DeviceName TEXT COLLATE NOCASE,CaptureType INTEGER)",
            };
            yield return new object[]
            {
                "CREATE TABLE CaptureDevices(id INTEGER PRIMARY KEY," +
                "DeviceType INTEGER,DeviceName TEXT NOT NULL,CaptureType INTEGER)",
            };
            yield return new object[]
            {
                "CREATE TABLE CaptureDevices(id INTEGER PRIMARY KEY," +
                "DeviceType INTEGER,DeviceName TEXT CHECK(length(DeviceName)>0)," +
                "CaptureType INTEGER)",
            };
            yield return new object[]
            {
                "CREATE TABLE CaptureDevices(id INTEGER PRIMARY KEY," +
                "DeviceType INTEGER,DeviceName TEXT REFERENCES Other(Name)," +
                "CaptureType INTEGER)",
            };
            yield return new object[]
            {
                "CREATE TABLE CaptureDevices(id INTEGER PRIMARY KEY," +
                "DeviceType INTEGER,DeviceName TEXT,CaptureType INTEGER," +
                "Generated TEXT GENERATED ALWAYS AS (printf('x')) VIRTUAL)",
            };
            yield return new object[]
            {
                HostileSqliteSchemas.CaptureDevicesDdl +
                " WITHOUT ROWID",
            };
            yield return new object[]
            {
                HostileSqliteSchemas.CaptureDevicesDdl +
                " STRICT",
            };
            yield return new object[]
            {
                "CREATE TABLE CaptureDevices(id INTEGER PRIMARY KEY," +
                "DeviceType INTEGER,DeviceName TEXT,CaptureType INTEGER," +
                "UNIQUE(DeviceName))",
            };
            yield return new object[]
            {
                "CREATE VIRTUAL TABLE CaptureDevices USING fts5(DeviceName)",
            };
            yield return new object[]
            {
                "CREATE VIEW CaptureDevices AS SELECT 1",
            };
            yield return new object[]
            {
                "CREATE INDEX CaptureDevices ON Other(Name)",
            };
            yield return new object[]
            {
                "CREATE TRIGGER CaptureDevices AFTER INSERT ON Other BEGIN SELECT 1; END",
            };
        }
    }
}
