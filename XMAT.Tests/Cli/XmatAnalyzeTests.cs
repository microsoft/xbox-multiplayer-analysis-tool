// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using XMAT.DataAbstractionLayer;
using XMAT.Tests.Fixtures;
using XMAT.Tests.Proxy;

namespace XMAT.Tests.Cli
{
    // xmat analyze, run as a child process over synthetic captures: exit codes, the JSON on standard output, the
    // published folder and the isolation from the GUI. The collection disables parallelization, so no other test writes
    // the GUI's storage while the read-only GuiStorageProbe watches it.
    [Collection(IsolatedProxyTests.Name)]
    public class XmatAnalyzeTests
    {
        [Fact]
        public async Task Analyze_Saz_PublishesADataFreeSummaryAndLeavesTheGuiAlone()
        {
            using var directory = new CliTestDirectory();
            string input = SyntheticCaptures.WriteSaz(
                directory.Root, "capture.saz", new SyntheticSazOptions(Consoles: 2, IncludeCanaries: true));
            string inputHash = CliAssert.Sha256(input);
            DateTime inputWritten = File.GetLastWriteTimeUtc(input);
            GuiStorageProbe gui = GuiStorageProbe.Capture();

            XmatRun run = await XmatProcess.RunAsync(directory.Root, "analyze", input, "--out", "results");

            CliAssert.ExitCode(0, run);
            JsonElement json = run.Json();
            Assert.Equal("xmat-cli/1", json.GetProperty("schema").GetString());
            Assert.Equal("analyze", json.GetProperty("command").GetString());
            Assert.Matches("^[0-9a-f]{32}$", json.GetProperty("runId").GetString());
            Assert.Equal("succeeded", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("exitCode").GetInt32());
            Assert.Equal("saz", json.GetProperty("input").GetProperty("kind").GetString());
            Assert.Equal(inputHash, json.GetProperty("input").GetProperty("sha256").GetString());
            JsonElement device = Assert.Single(json.GetProperty("devices").EnumerateArray());
            Assert.Equal("d1", device.GetProperty("id").GetString());
            Assert.Equal(
                new[] { "c1", "c2" },
                device.GetProperty("consoles").EnumerateArray().Select(console => console.GetProperty("id").GetString()));
            int calls = json.GetProperty("totals").GetProperty("calls").GetInt32();
            Assert.True(calls > 0);
            Assert.Equal(calls, device.GetProperty("consoles").EnumerateArray().Sum(console => console.GetProperty("calls").GetInt32()));
            Assert.Equal(
                new[] { "summary:summary.json" },
                json.GetProperty("artifacts").EnumerateArray()
                    .Select(artifact => artifact.GetProperty("kind").GetString() + ":" + artifact.GetProperty("path").GetString()));
            Assert.Equal("completed", json.GetProperty("cleanup").GetProperty("state").GetString());
            Assert.Empty(json.GetProperty("errors").EnumerateArray());

            string published = Path.Combine(directory.Root, "results");
            Assert.Equal(
                new[] { "summary.json" },
                Directory.GetFiles(published, "*", SearchOption.AllDirectories).Select(file => Path.GetRelativePath(published, file)));
            using (JsonDocument summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(published, "summary.json"))))
            {
                Assert.Equal("xmat-analysis/1", summary.RootElement.GetProperty("schema").GetString());
                Assert.Equal(json.GetProperty("runId").GetString(), summary.RootElement.GetProperty("runId").GetString());
                Assert.Equal(calls, summary.RootElement.GetProperty("totals").GetProperty("calls").GetInt32());
                Assert.Equal(Diagnostics(json.GetProperty("warnings")), Diagnostics(summary.RootElement.GetProperty("warnings")));
            }

            CliAssert.OnlyCurrentUserCanOpen(published);
            CliAssert.NoLeaks(run, directory, published);
            CliAssert.NoStagingFolders(directory.Root);
            Assert.Equal(inputHash, CliAssert.Sha256(input));
            Assert.Equal(inputWritten, File.GetLastWriteTimeUtc(input));
            Assert.False(run.WindowSeen);
            gui.AssertUnchanged();
        }

        [Fact]
        public async Task Analyze_MalformedSazHeaders_WarnsOnceAndKeepsTheFrame()
        {
            using var directory = new CliTestDirectory();
            string input = SyntheticCaptures.WriteMalformedHeaderSaz(
                directory.Root,
                "malformed-headers.saz");

            XmatRun run = await XmatProcess.RunAsync(
                directory.Root,
                "analyze",
                input,
                "--out",
                "results");

            CliAssert.ExitCode(0, run);
            JsonElement json = run.Json();
            Assert.Equal(
                1,
                json.GetProperty("totals").GetProperty("calls").GetInt32());
            JsonElement warning = Assert.Single(
                json.GetProperty("warnings").EnumerateArray());
            Assert.Equal(
                "analysis.input.malformed-header",
                warning.GetProperty("code").GetString());
            Assert.Equal(
                "One or more malformed HTTP header lines were skipped.",
                warning.GetProperty("message").GetString());
            string published = Path.Combine(
                directory.Root,
                "results");
            using JsonDocument summary = JsonDocument.Parse(
                File.ReadAllText(
                    Path.Combine(
                        published,
                        "summary.json")));
            Assert.Equal(
                Diagnostics(json.GetProperty("warnings")),
                Diagnostics(summary.RootElement.GetProperty("warnings")));
            CliAssert.NoLeaks(run, directory, published);
        }

        [Fact]
        public async Task Analyze_AllMalformedStartLines_ReturnsNoDataAndPublishesNothing()
        {
            using var directory = new CliTestDirectory();
            string input = SyntheticCaptures.WriteSazFrames(
                directory.Root,
                "malformed-start-lines.saz",
                new[]
                {
                    SyntheticSazFrameKind.MalformedRequestStartLine,
                    SyntheticSazFrameKind.MalformedResponseStartLine,
                });
            string[] before = directory.Entries();

            XmatRun run = await XmatProcess.RunAsync(
                directory.Root,
                "analyze",
                input,
                "--out",
                "results");

            CliAssert.ExitCode(3, run);
            Assert.Equal(
                "failed",
                run.Json().GetProperty("status").GetString());
            Assert.Contains(
                "analysis.no-data",
                run.ErrorCodes());
            JsonElement warning = Assert.Single(
                run.Json().GetProperty("warnings").EnumerateArray());
            Assert.Equal(
                "analysis.input.incomplete-frame",
                warning.GetProperty("code").GetString());
            Assert.Equal(
                "One or more incomplete or malformed SAZ frames were skipped.",
                warning.GetProperty("message").GetString());
            Assert.Empty(
                run.Json().GetProperty("artifacts").EnumerateArray());
            Assert.False(
                Directory.Exists(
                    Path.Combine(
                        directory.Root,
                        "results")));
            Assert.Equal(before, directory.Entries());
            CliAssert.NoLeaks(run, directory);
            CliAssert.NoStagingFolders(directory.Root);
        }

        [Fact]
        public async Task Analyze_HostlessFrames_SucceedsWithoutAnIncompleteFrameWarning()
        {
            using var directory = new CliTestDirectory();
            string input = SyntheticCaptures.WriteSazFrames(
                directory.Root,
                "missing-hosts.saz",
                new[]
                {
                    SyntheticSazFrameKind.MissingHost,
                    SyntheticSazFrameKind.MalformedHost,
                });

            XmatRun run = await XmatProcess.RunAsync(
                directory.Root,
                "analyze",
                input,
                "--out",
                "results");

            CliAssert.ExitCode(0, run);
            JsonElement json = run.Json();
            Assert.Equal(
                2,
                json.GetProperty("totals")
                    .GetProperty("calls")
                    .GetInt32());
            JsonElement warning = Assert.Single(
                json.GetProperty("warnings").EnumerateArray());
            Assert.Equal(
                "analysis.input.malformed-header",
                warning.GetProperty("code").GetString());
            Assert.DoesNotContain(
                json.GetProperty("warnings").EnumerateArray(),
                diagnostic =>
                    diagnostic.GetProperty("code").GetString() ==
                    "analysis.input.incomplete-frame");
            string published = Path.Combine(
                directory.Root,
                "results");
            Assert.True(
                File.Exists(
                    Path.Combine(
                        published,
                        "summary.json")));
            CliAssert.NoLeaks(run, directory, published);
            CliAssert.NoStagingFolders(directory.Root);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Analyze_HeaderLimit_Returns3AndPublishesNothing(
            bool response)
        {
            using var directory = new CliTestDirectory();
            string input = SyntheticCaptures.WriteHeaderLimitSaz(
                directory.Root,
                "header-limit.saz",
                response);
            string[] before = directory.Entries();

            XmatRun run = await XmatProcess.RunAsync(
                directory.Root,
                "analyze",
                input,
                "--out",
                "results");

            CliAssert.ExitCode(3, run);
            Assert.Equal(
                new[] { "analysis.input.limit" },
                run.ErrorCodes());
            Assert.Empty(
                run.Json().GetProperty("artifacts").EnumerateArray());
            Assert.False(
                Directory.Exists(
                    Path.Combine(
                        directory.Root,
                        "results")));
            Assert.Equal(before, directory.Entries());
            CliAssert.NoLeaks(run, directory);
            CliAssert.NoStagingFolders(directory.Root);
        }

        [Fact]
        public async Task Analyze_Xmatcap_ReportsEveryDeviceOrTheSelectedOne()
        {
            using var directory = new CliTestDirectory();
            string input = SyntheticCaptures.WriteXmatcap(
                directory.Root,
                "capture.xmatcap",
                new SyntheticXmatcapOptions(Devices: 2, RequestsPerDevice: 3, IncludeCanaries: true));
            string inputHash = CliAssert.Sha256(input);

            XmatRun all = await XmatProcess.RunAsync(directory.Root, "analyze", input, "--out", "all");
            XmatRun one = await XmatProcess.RunAsync(directory.Root, "analyze", input, "--out=one", "--device=d2");

            CliAssert.ExitCode(0, all);
            CliAssert.ExitCode(0, one);
            Assert.Equal("xmatcap", all.Json().GetProperty("input").GetProperty("kind").GetString());
            Assert.Equal(new[] { "d1", "d2" }, DeviceIds(all));
            Assert.Equal(new[] { "d2" }, DeviceIds(one));
            CliAssert.NoLeaks(all, directory, Path.Combine(directory.Root, "all"));
            CliAssert.NoLeaks(one, directory, Path.Combine(directory.Root, "one"));
            Assert.Equal(inputHash, CliAssert.Sha256(input));
        }

        [Fact]
        public async Task Analyze_FailOnFindings_Returns10AndStillPublishes()
        {
            using var directory = new CliTestDirectory();
            string input = SyntheticCaptures.WriteSaz(
                directory.Root, "capture.saz", new SyntheticSazOptions(IncludeXr049Violation: true));

            XmatRun run = await XmatProcess.RunAsync(directory.Root, "analyze", input, "--out", "results", "--fail-on", "findings");

            CliAssert.ExitCode(10, run);
            JsonElement json = run.Json();
            Assert.Equal("findingsFound", json.GetProperty("status").GetString());
            Assert.True(json.GetProperty("totals").GetProperty("findings").GetInt32() > 0);
            Assert.Contains(
                json.GetProperty("devices").EnumerateArray()
                    .SelectMany(device => device.GetProperty("consoles").EnumerateArray())
                    .SelectMany(console => console.GetProperty("findings").EnumerateArray()),
                finding => finding.GetProperty("rule").GetString() == "XR049Rule");
            Assert.True(File.Exists(Path.Combine(directory.Root, "results", "summary.json")));
        }

        // One representative per exit-code-3 path through the process; the operation tests cover the other invalid inputs.
        [Theory]
        [InlineData("missing", "analysis.input.not-found")]
        [InlineData("zip-bomb", "analysis.input.limit")]
        public async Task Analyze_InvalidInput_Returns3AndPublishesNothing(string kind, string code)
        {
            using var directory = new CliTestDirectory();
            string input = kind == "zip-bomb"
                ? SyntheticCaptures.WriteZipBombSaz(directory.Root, "capture.saz")
                : Path.Combine(directory.Root, "capture.saz");
            string[] before = directory.Entries();

            XmatRun run = await XmatProcess.RunAsync(directory.Root, "analyze", input, "--out", "results");

            CliAssert.ExitCode(3, run);
            JsonElement json = run.Json();
            Assert.Equal("failed", json.GetProperty("status").GetString());
            Assert.Contains(code, run.ErrorCodes());
            Assert.Empty(json.GetProperty("artifacts").EnumerateArray());
            Assert.Equal("completed", json.GetProperty("cleanup").GetProperty("state").GetString());
            Assert.Equal(before, directory.Entries());
            CliAssert.NoLeaks(run, directory);
        }

        [Theory]
        [InlineData("generated", "analysis.input.schema")]
        [InlineData("huge-ddl", "analysis.input.limit")]
        public async Task Analyze_HostileSchema_Returns3AndPublishesNothing(
            string kind,
            string code)
        {
            using var directory = new CliTestDirectory();
            string input = kind == "generated"
                ? HostileSqliteSchemas.CreateGeneratedColumnCapture(
                    directory.Root,
                    "hostile.xmatcap")
                : HostileSqliteSchemas.CreateOrdinaryCapture(
                    directory.Root,
                    "hostile.xmatcap");
            if (kind == "huge-ddl")
            {
                HostileSqliteSchemas.ReplaceSchemaSql(
                    input,
                    "WebProxyConnections",
                    "CREATE TABLE WebProxyConnections(" +
                    SyntheticCaptures.Canary +
                    new string(
                        'x',
                        SqLtSchemaProfile
                            .MaxSchemaRecordBytes *
                        2) +
                    ")");
            }
            string[] before = directory.Entries();

            XmatRun run = await XmatProcess.RunAsync(
                directory.Root,
                "analyze",
                input,
                "--out",
                "results");

            CliAssert.ExitCode(3, run);
            Assert.Equal(
                new[] { code },
                run.ErrorCodes());
            Assert.Empty(
                run.Json().GetProperty("artifacts")
                    .EnumerateArray());
            Assert.False(
                Directory.Exists(
                    Path.Combine(
                        directory.Root,
                        "results")));
            Assert.Equal(before, directory.Entries());
            CliAssert.NoLeaks(run, directory);
            CliAssert.NoStagingFolders(directory.Root);
        }

        [Fact]
        public async Task Analyze_ExistingOut_Returns5AndLeavesItAlone()
        {
            using var directory = new CliTestDirectory();
            string input = SyntheticCaptures.WriteSaz(directory.Root, "capture.saz");
            string existing = Path.Combine(directory.Root, "results");
            Directory.CreateDirectory(existing);
            File.WriteAllText(Path.Combine(existing, "keep.txt"), "keep");

            XmatRun run = await XmatProcess.RunAsync(directory.Root, "analyze", input, "--out", "results");

            CliAssert.ExitCode(5, run);
            Assert.Equal(new[] { "cli.output.exists" }, run.ErrorCodes());
            Assert.Equal(new[] { "keep.txt" }, Directory.GetFileSystemEntries(existing).Select(Path.GetFileName));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(existing, "keep.txt")));
            CliAssert.NoStagingFolders(directory.Root);
        }

        [Fact]
        public async Task Analyze_OutWithoutParentFolder_IsAUsageErrorAndCreatesNothing()
        {
            using var directory = new CliTestDirectory();
            string input = SyntheticCaptures.WriteSaz(directory.Root, "capture.saz");
            string[] before = directory.Entries();

            XmatRun run = await XmatProcess.RunAsync(directory.Root, "analyze", input, "--out", Path.Combine("missing", "results"));

            CliAssert.ExitCode(2, run);
            Assert.Equal(new[] { "cli.output.parent-missing" }, run.ErrorCodes());
            Assert.Equal(before, directory.Entries());
        }

        private static string[] DeviceIds(XmatRun run) =>
            run.Json().GetProperty("devices").EnumerateArray().Select(device => device.GetProperty("id").GetString()).ToArray();

        private static string[] Diagnostics(JsonElement diagnostics) =>
            diagnostics.EnumerateArray()
                .Select(diagnostic => diagnostic.GetProperty("code").GetString() + ": " + diagnostic.GetProperty("message").GetString())
                .ToArray();
    }
}
