// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using XMAT.Cli;
using XMAT.DataAbstractionLayer;
using XMAT.Tests.Fixtures;
using XMAT.Tests.Proxy;
using XMAT.WebServiceCapture;

namespace XMAT.Tests.Cli
{
    // xmat analyze's limit options, run as a child process: the effective limits in the JSON output and summary.json, a
    // lowered limit that refuses a capture, and the default record limit that admits a row with large bodies. The
    // collection runs alone: one capture here holds bodies of 8 and 24 MiB.
    [Collection(IsolatedProxyTests.Name)]
    public class XmatAnalyzeLimitTests
    {
        private const int LargeRequestBodyBytes = 8 * 1024 * 1024;
        private const int LargeResponseBodyBytes = 24 * 1024 * 1024;

        [Fact]
        public async Task Analyze_ReportsTheDefaultLimits_InItsOutputAndSummary()
        {
            using var directory = new CliTestDirectory();
            string input = SyntheticCaptures.WriteSaz(directory.Root, "capture.saz");

            XmatRun run = await XmatProcess.RunAsync(directory.Root, "analyze", input, "--out", "results");

            CliAssert.ExitCode(0, run);
            Assert.Equal(new[] { 512L, 256L, 128L, 100_000L }, Limits(run.Json()));
            Assert.Equal(new[] { 512L, 256L, 128L, 100_000L }, Limits(Summary(directory, "results")));
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
            Assert.Equal(new[] { 512L, 256L, 128L, 1L }, Limits(run.Json()));
            Assert.Empty(run.Json().GetProperty("artifacts").EnumerateArray());
            Assert.Equal(before, directory.Entries());
            CliAssert.NoLeaks(run, directory);
            CliAssert.NoStagingFolders(directory.Root);
        }

        // An .xmatcap row whose base64 request and response bodies (about 43 MiB) exceed the former 32 MiB record default:
        // the defaults admit it, and xmat export keeps both bodies exactly. A lowered record limit still refuses it (the
        // reader sets SQLITE_LIMIT_LENGTH to the record limit plus 1 MiB), and a 32 MiB input limit refuses the file before
        // anything is read. The export's name is short because the staged copy and its SQLite journal must stay under 260
        // characters in CI's deeper test folder (xmat.exe isn't long-path aware).
        [Fact]
        public async Task Analyze_LargeXmatcapBodies_AreAdmittedByDefault_AndFollowLoweredLimits()
        {
            using var directory = new CliTestDirectory();
            string input = SyntheticCaptures.WriteXmatcap(
                directory.Root, "large-bodies.xmatcap", new SyntheticXmatcapOptions(RequestsPerDevice: 2));
            (string Request, string Response) stored = AddLargeBodies(input);

            XmatRun byDefault = await XmatProcess.RunAsync(directory.Root, "analyze", input, "--out", "default");
            XmatRun exported = await XmatProcess.RunAsync(directory.Root, "export", input, "--out", "x.xmatcap");
            XmatRun lowered = await XmatProcess.RunAsync(
                directory.Root, "analyze", input, "--out", "lowered", "--max-record-mib", "32");
            XmatRun smallInput = await XmatProcess.RunAsync(
                directory.Root, "analyze", input, "--out", "small-input", "--max-input-mib", "32");

            CliAssert.ExitCode(0, byDefault);
            Assert.Equal(2, byDefault.Json().GetProperty("totals").GetProperty("calls").GetInt32());
            Assert.Equal(new[] { 512L, 256L, 128L, 100_000L }, Limits(Summary(directory, "default")));
            CliAssert.ExitCode(0, exported);
            Assert.Equal(stored, StoredBodies(Path.Combine(directory.Root, "x.xmatcap")));
            CliAssert.ExitCode(3, lowered);
            Assert.Equal(new[] { "analysis.input.limit" }, lowered.ErrorCodes());
            Assert.Equal(new[] { 512L, 256L, 32L, 100_000L }, Limits(lowered.Json()));
            CliAssert.ExitCode(3, smallInput);
            Assert.Equal(new[] { "analysis.input.too-large" }, smallInput.ErrorCodes());
            Assert.Equal(new[] { 32L, 256L, 128L, 100_000L }, Limits(smallInput.Json()));
            Assert.False(Directory.Exists(Path.Combine(directory.Root, "lowered")));
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

        // Gives the first WebProxyConnections row JSON request and response bodies of LargeRequestBodyBytes and
        // LargeResponseBodyBytes, stored as base64 as XMAT stores them, and adds TEXT and INTEGER extension columns that
        // are set in that row and NULL in the second: xmat counts every non-NULL column of the rows it reads, admits NULL
        // only in extension columns, and the analysis itself never looks at an extension column. Returns the SHA-256 of
        // the stored request and response bodies.
        private static (string Request, string Response) AddLargeBodies(string path)
        {
            string request = Convert.ToBase64String(JsonBody(LargeRequestBodyBytes));
            string response = Convert.ToBase64String(JsonBody(LargeResponseBodyBytes));
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
                "UPDATE WebProxyConnections SET RequestBody = $request, ResponseBody = $response, " +
                "Extension = 'extension', ExtensionNumber = 7 " +
                "WHERE rowid = (SELECT MIN(rowid) FROM WebProxyConnections)";
            update.Parameters.AddWithValue("$request", request);
            update.Parameters.AddWithValue("$response", response);
            Assert.Equal(1, update.ExecuteNonQuery());
            return (Sha256(request), Sha256(response));
        }

        // A JSON object of SIZE bytes: {"padding":"aaa…"}.
        private static byte[] JsonBody(int size)
        {
            byte[] body = new byte[size];
            body.AsSpan().Fill((byte)'a');
            "{\"padding\":\""u8.CopyTo(body);
            "\"}"u8.CopyTo(body.AsSpan(size - 2));
            return body;
        }

        // The SHA-256 of the stored request and response bodies of request 1 in an exported capture.
        private static (string Request, string Response) StoredBodies(string capture)
        {
            using var database = new SqLtReadonlyDatabase(capture);
            var record = database.TableByName(WebServiceCaptureMethod.TableName).Dataset.DataRecords
                .Single(row => row.Int(WebServiceCaptureMethod.FieldKey_RequestNumber) == 1);
            return (
                Sha256(record.Str(WebServiceCaptureMethod.FieldKey_RequestBody)),
                Sha256(record.Str(WebServiceCaptureMethod.FieldKey_ResponseBody)));
        }

        private static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(text)));

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
