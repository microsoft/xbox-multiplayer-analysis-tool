// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using XMAT.Cli;
using XMAT.Tests.Fixtures;
using XMAT.Tests.Proxy;

namespace XMAT.Tests.Cli
{
    // xmat analyze's limit options, run as a child process: the effective limits in the JSON output and summary.json, a
    // lowered limit that refuses a capture, and a raised one that admits a capture that the defaults refuse. The
    // collection runs alone: one capture here holds a 40 MiB value.
    [Collection(IsolatedProxyTests.Name)]
    public class XmatAnalyzeLimitTests
    {
        private const int LargeValueBytes = 40 * 1024 * 1024;

        [Fact]
        public async Task Analyze_ReportsTheDefaultLimits_InItsOutputAndSummary()
        {
            using var directory = new CliTestDirectory();
            string input = SyntheticCaptures.WriteSaz(directory.Root, "capture.saz");

            XmatRun run = await XmatProcess.RunAsync(directory.Root, "analyze", input, "--out", "results");

            CliAssert.ExitCode(0, run);
            Assert.Equal(new[] { 512L, 256L, 32L, 100_000L }, Limits(run.Json()));
            Assert.Equal(new[] { 512L, 256L, 32L, 100_000L }, Limits(Summary(directory, "results")));
        }

        // Two requests are one more than --max-records 1 allows: nothing is published, and the output reports the limit
        // that refused the capture.
        [Fact]
        public async Task Analyze_LoweredRequestLimit_Returns3AndPublishesNothing()
        {
            using var directory = new CliTestDirectory();
            string input = SyntheticCaptures.WriteSaz(
                directory.Root, "two-requests.saz", new SyntheticSazOptions(RequestsPerConsole: 2));
            string[] before = directory.Entries();

            XmatRun run = await XmatProcess.RunAsync(
                directory.Root, "analyze", input, "--out", "results", "--max-records", "1");

            CliAssert.ExitCode(3, run);
            Assert.Equal(new[] { "analysis.input.limit" }, run.ErrorCodes());
            Assert.Equal(new[] { 512L, 256L, 32L, 1L }, Limits(run.Json()));
            Assert.Empty(run.Json().GetProperty("artifacts").EnumerateArray());
            Assert.Equal(before, directory.Entries());
            CliAssert.NoLeaks(run, directory);
            CliAssert.NoStagingFolders(directory.Root);
        }

        // A 40 MiB value in an .xmatcap row: SQLite's value limit and the record limit refuse it by default (the reader
        // sets SQLITE_LIMIT_LENGTH to the record limit plus 1 MiB), a 32 MiB input limit refuses the file before
        // anything is read, and --max-record-mib 48 admits it along with the second row's NULL extension values.
        [Fact]
        public async Task Analyze_LargeXmatcapValue_FollowsTheRecordAndInputLimits()
        {
            using var directory = new CliTestDirectory();
            string input = SyntheticCaptures.WriteXmatcap(
                directory.Root, "large-value.xmatcap", new SyntheticXmatcapOptions(RequestsPerDevice: 2));
            AddLargeExtensionValue(input);

            XmatRun byDefault = await XmatProcess.RunAsync(directory.Root, "analyze", input, "--out", "default");
            XmatRun smallInput = await XmatProcess.RunAsync(
                directory.Root, "analyze", input, "--out", "small-input", "--max-input-mib", "32");
            XmatRun raised = await XmatProcess.RunAsync(
                directory.Root, "analyze", input, "--out", "raised", "--max-record-mib", "48");

            CliAssert.ExitCode(3, byDefault);
            Assert.Equal(new[] { "analysis.input.limit" }, byDefault.ErrorCodes());
            CliAssert.ExitCode(3, smallInput);
            Assert.Equal(new[] { "analysis.input.too-large" }, smallInput.ErrorCodes());
            Assert.Equal(new[] { 32L, 256L, 32L, 100_000L }, Limits(smallInput.Json()));
            CliAssert.ExitCode(0, raised);
            Assert.Equal(2, raised.Json().GetProperty("totals").GetProperty("calls").GetInt32());
            Assert.Equal(new[] { 512L, 256L, 48L, 100_000L }, Limits(raised.Json()));
            Assert.Equal(new[] { 512L, 256L, 48L, 100_000L }, Limits(Summary(directory, "raised")));
            Assert.False(Directory.Exists(Path.Combine(directory.Root, "default")));
            Assert.False(Directory.Exists(Path.Combine(directory.Root, "small-input")));
            CliAssert.NoStagingFolders(directory.Root);
        }

        // Every limit at its maximum is a valid run: SQLite reads back the largest value limit that xmat sets.
        [Fact]
        public async Task Analyze_AllLimitsAtTheirMaximum_Succeeds()
        {
            using var directory = new CliTestDirectory();
            string input = SyntheticCaptures.WriteXmatcap(directory.Root, "capture.xmatcap");

            XmatRun run = await XmatProcess.RunAsync(
                directory.Root,
                "analyze",
                input,
                "--out",
                "results",
                CliLimits.InputOption,
                Text(CliLimits.MaximumInputMib),
                CliLimits.DataOption,
                Text(CliLimits.MaximumDataMib),
                CliLimits.RecordOption,
                Text(CliLimits.MaximumRecordMib),
                CliLimits.RecordsOption,
                Text(CliLimits.MaximumRecords));

            CliAssert.ExitCode(0, run);
            Assert.Equal(
                new long[]
                {
                    CliLimits.MaximumInputMib,
                    CliLimits.MaximumDataMib,
                    CliLimits.MaximumRecordMib,
                    CliLimits.MaximumRecords,
                },
                Limits(run.Json()));
        }

        // Adds TEXT and INTEGER extension columns to WebProxyConnections, fills them in the first row (LargeValueBytes
        // of text) and leaves them NULL in the second: xmat counts every non-NULL column of the rows it reads, admits
        // NULL only in extension columns, and the analysis itself never looks at an extension column.
        private static void AddLargeExtensionValue(string path)
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false,
            }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            foreach (string column in new[] { "Extension TEXT", "ExtensionNumber INTEGER" })
            {
                using SqliteCommand alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE WebProxyConnections ADD COLUMN " + column;
                alter.ExecuteNonQuery();
            }

            using SqliteCommand update = connection.CreateCommand();
            update.CommandText =
                "UPDATE WebProxyConnections SET Extension = $value, ExtensionNumber = 7 " +
                "WHERE rowid = (SELECT MIN(rowid) FROM WebProxyConnections)";
            update.Parameters.AddWithValue("$value", new string('x', LargeValueBytes));
            Assert.Equal(1, update.ExecuteNonQuery());
        }

        private static JsonElement Summary(CliTestDirectory directory, string folder)
        {
            using JsonDocument summary = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(directory.Root, folder, CliAnalyzeCommand.SummaryPath)));
            return summary.RootElement.Clone();
        }

        private static long[] Limits(JsonElement root)
        {
            JsonElement limits = root.GetProperty("limits");
            return new[] { "maxInputMib", "maxDataMib", "maxRecordMib", "maxRecords" }
                .Select(name => limits.GetProperty(name).GetInt64())
                .ToArray();
        }

        private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
