// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using XMAT.Cli;
using XMAT.DataAbstractionLayer;
using XMAT.Operations.CaptureFiles;
using XMAT.Tests.Fixtures;
using XMAT.Tests.Proxy;

namespace XMAT.Tests.Cli
{
    [Collection(IsolatedProxyTests.Name)]
    public class XmatMarkCaptureTextTests
    {
        [Theory]
        [InlineData("as-stored", "a\"\"b", "a\"\"b", "asStored")]
        [InlineData("legacy-one-layer", "a\"\"b\"\"\"\"c", "a\"b\"\"c", "legacyOneLayer")]
        public async Task MarkCaptureText_CreatesOneMarkedCopyAndSummary(
            string interpretation,
            string stored,
            string expected,
            string jsonInterpretation)
        {
            using var directory = new CliTestDirectory();
            string input =
                SyntheticCaptures.WriteXmatcap(
                    directory.Root,
                    "input.xmatcap",
                    new SyntheticXmatcapOptions(
                        RequestsPerDevice: 1,
                        UserVersion:
                            CaptureTextFormat.AmbiguousUserVersion));
            UpdateValue(
                input,
                stored);
            byte[] before =
                File.ReadAllBytes(input);
            string beforeHash =
                Convert.ToHexString(
                        SHA256.HashData(before))
                    .ToLowerInvariant();
            GuiStorageProbe gui =
                GuiStorageProbe.Capture();

            XmatRun run =
                await XmatProcess.RunAsync(
                    directory.Root,
                    "mark-capture-text",
                    input,
                    "--out",
                    "results",
                    "--interpret",
                    interpretation);

            CliAssert.ExitCode(0, run);
            Assert.False(run.WindowSeen);
            string output =
                Path.Join(
                    directory.Root,
                    "results");
            string capture =
                Path.Join(
                    output,
                    "capture.xmatcap");
            string summaryPath =
                Path.Join(
                    output,
                    "summary.json");
            Assert.Equal(
                new[]
                {
                    "capture.xmatcap",
                    "summary.json",
                },
                Directory.GetFileSystemEntries(
                        output)
                    .Select(Path.GetFileName)
                    .OrderBy(
                        name => name,
                        StringComparer.Ordinal));
            Assert.False(
                Directory.Exists(
                    Path.Join(
                        output,
                        "work")));
            Assert.Equal(
                expected,
                ReadValue(capture));
            Assert.Equal(
                CaptureTextFormat.LiteralUserVersion,
                ReadUserVersion(capture));
            Assert.Equal(
                before,
                File.ReadAllBytes(input));
            CliAssert.OnlyCurrentUserCanOpen(output);

            JsonElement root =
                run.Json();
            Assert.Equal(
                jsonInterpretation,
                root.GetProperty(
                        "markCaptureText")
                    .GetProperty(
                        "interpretation")
                    .GetString());
            Assert.Equal(
                512,
                root.GetProperty(
                        "markCaptureText")
                    .GetProperty(
                        "limits")
                    .GetProperty(
                        "maxInputMib")
                    .GetInt32());
            Assert.False(
                root.TryGetProperty(
                    "acceptStoredText",
                    out _));
            Assert.False(
                root.GetProperty(
                        "markCaptureText")
                    .TryGetProperty(
                        "acceptStoredText",
                        out _));
            Assert.Equal(
                beforeHash,
                root.GetProperty(
                        "markCaptureText")
                    .GetProperty(
                        "input")
                    .GetProperty(
                        "sha256")
                    .GetString());
            Assert.Equal(
                new[]
                {
                    "capture:capture.xmatcap",
                    "summary:summary.json",
                },
                root.GetProperty(
                        "artifacts")
                    .EnumerateArray()
                    .Select(
                        artifact =>
                            artifact.GetProperty(
                                    "kind")
                                .GetString() +
                            ":" +
                            artifact.GetProperty(
                                    "path")
                                .GetString()));
            using JsonDocument summary =
                JsonDocument.Parse(
                    File.ReadAllBytes(
                        summaryPath));
            Assert.Equal(
                "xmat-mark-capture-text/1",
                summary.RootElement.GetProperty(
                        "schema")
                    .GetString());
            Assert.Equal(
                root.GetProperty(
                        "runId")
                    .GetString(),
                summary.RootElement.GetProperty(
                        "runId")
                    .GetString());
            Assert.Equal(
                CliReport.ToolVersion,
                summary.RootElement.GetProperty(
                        "version")
                    .GetString());
            CliAssert.NoStagingFolders(
                directory.Root);
            CliAssert.NoLeaks(
                run,
                directory,
                output);
            gui.AssertUnchanged();
        }

        [Theory]
        [InlineData("marked", "capture.repair.already-marked")]
        [InlineData("future", "capture.format.future")]
        [InlineData("foreign-future", "capture.format.future")]
        [InlineData("odd", "capture.repair.not-legacy-encoding")]
        [InlineData("non-native", "capture.repair.input-not-xmatcap")]
        [InlineData("non-native-sqlite", "capture.repair.input-not-xmatcap")]
        [InlineData("non-native-view", "capture.repair.input-not-xmatcap")]
        [InlineData("non-native-utf16", "capture.repair.input-not-xmatcap")]
        [InlineData("utf16-future", "capture.format.future")]
        public async Task MarkCaptureText_RefusalsCreateNoOutput(
            string inputKind,
            string code)
        {
            using var directory = new CliTestDirectory();
            string input =
                Path.Join(
                    directory.Root,
                    "input.xmatcap");
            string interpretation =
                inputKind == "odd"
                    ? "legacy-one-layer"
                    : "as-stored";
            if (inputKind == "non-native")
            {
                File.WriteAllText(
                    input,
                    "not sqlite");
            }
            else if (inputKind == "non-native-sqlite")
            {
                input =
                    HostileSqliteSchemas.CreateDatabase(
                        directory.Root,
                        "input.xmatcap",
                        "CREATE TABLE Other(id INTEGER PRIMARY KEY,Value TEXT)");
            }
            else if (inputKind == "non-native-view")
            {
                input =
                    HostileSqliteSchemas.CreateDatabase(
                        directory.Root,
                        "input.xmatcap",
                        "CREATE VIEW CaptureDevices AS SELECT 1 AS id");
            }
            else if (inputKind is
                "non-native-utf16" or
                "utf16-future")
            {
                input =
                    HostileSqliteSchemas.CreateUtf16Database(
                        directory.Root,
                        "input.xmatcap",
                        inputKind == "utf16-future"
                            ? (uint)2
                            : CaptureTextFormat.AmbiguousUserVersion,
                        "CREATE TABLE Other(id INTEGER PRIMARY KEY,Value TEXT)");
            }
            else if (inputKind == "foreign-future")
            {
                input =
                    HostileSqliteSchemas.CreateDatabase(
                        directory.Root,
                        "input.xmatcap",
                        (uint)2,
                        "CREATE TABLE Other(id INTEGER PRIMARY KEY,Value TEXT)");
            }
            else
            {
                uint version =
                    inputKind switch
                    {
                        "marked" =>
                            CaptureTextFormat.LiteralUserVersion,
                        "future" => 2,
                        _ =>
                            CaptureTextFormat.AmbiguousUserVersion,
                    };
                input =
                    SyntheticCaptures.WriteXmatcap(
                        directory.Root,
                        "input.xmatcap",
                        new SyntheticXmatcapOptions(
                            RequestsPerDevice: 1,
                            UserVersion: version));
                if (inputKind == "odd")
                {
                    UpdateValue(
                        input,
                        "odd\"");
                }
            }
            string[] before =
                directory.Entries();

            XmatRun run =
                await XmatProcess.RunAsync(
                    directory.Root,
                    "mark-capture-text",
                    input,
                    "--out",
                    "results",
                    "--interpret",
                    interpretation);

            CliAssert.ExitCode(3, run);
            Assert.Equal(
                new[] { code },
                run.ErrorCodes());
            if (inputKind is
                "future" or
                "foreign-future" or
                "utf16-future")
            {
                Assert.Equal(
                    "The SQLite file has an unsupported user_version value. XMAT accepts only 0 or 1.",
                    Assert.Single(
                            run.Json()
                                .GetProperty(
                                    "errors")
                                .EnumerateArray())
                        .GetProperty(
                            "message")
                        .GetString());
            }
            Assert.Empty(
                run.Json()
                    .GetProperty(
                        "artifacts")
                    .EnumerateArray());
            JsonElement mark =
                run.Json()
                    .GetProperty(
                        "markCaptureText");
            Assert.False(
                mark.TryGetProperty(
                    "source",
                    out _));
            Assert.False(
                mark.TryGetProperty(
                    "output",
                    out _));
            Assert.False(
                mark.TryGetProperty(
                    "counts",
                    out _));
            Assert.False(
                Directory.Exists(
                    Path.Join(
                        directory.Root,
                        "results")));
            Assert.Equal(
                before,
                directory.Entries());
            CliAssert.NoStagingFolders(
                directory.Root);
        }

        [Fact]
        public async Task MarkCaptureText_UnsupportedProbeFactsWinBeforeInputPreparation()
        {
            using var directory = new CliTestDirectory();
            string input =
                Path.Join(
                    directory.Root,
                    "input.xmatcap");
            File.WriteAllText(
                input,
                "not sqlite");
            string output =
                Path.Join(
                    directory.Root,
                    "results");
            var request =
                new CliRequest(
                    CliCommands.MarkCaptureText,
                    Input: input,
                    Output: output,
                    Interpretation:
                        CaptureTextInterpretation.AsStored,
                    MaxInputMib: 512);

            CliReport report =
                await CliMarkCaptureTextCommand.RunAsync(
                    request,
                    "run",
                    CancellationToken.None,
                    _ =>
                        (
                            FileSystemName: "FAT32",
                            DevicePath:
                                @"\Device\HarddiskVolume3\out"));

            Assert.Equal(
                CliExitCodes.Failed,
                report.ExitCode);
            Assert.Equal(
                "cli.output.unprotected",
                Assert.Single(
                    report.Errors)
                    .Code);
            Assert.Equal(
                "xmat cannot keep its output private there: the --out folder must be on a local NTFS volume and reached " +
                    "without a network path, a mapped or substituted drive, a junction, a mounted folder or a symbolic link.",
                Assert.Single(
                    report.Errors)
                    .Message);
            using (JsonDocument json =
                JsonDocument.Parse(
                    CliJson.Serialize(
                        report)))
            {
                Assert.Empty(
                    json.RootElement.GetProperty(
                            "artifacts")
                        .EnumerateArray());
            }
            Assert.False(
                Directory.Exists(
                    output));
            Assert.Equal(
                new[] { "input.xmatcap" },
                directory.Entries());
            CliAssert.NoStagingFolders(
                directory.Root);
        }

        [Fact]
        public async Task MarkCaptureText_ExistingOutputIsReportedBeforeTheInputScan()
        {
            using var directory = new CliTestDirectory();
            string input =
                Path.Join(
                    directory.Root,
                    "missing.xmatcap");
            string output =
                Path.Join(
                    directory.Root,
                    "results");
            Directory.CreateDirectory(output);
            File.WriteAllText(
                Path.Join(
                    output,
                    "keep.txt"),
                "keep");

            XmatRun run =
                await XmatProcess.RunAsync(
                    directory.Root,
                    "mark-capture-text",
                    input,
                    "--out",
                    output,
                    "--interpret",
                    "as-stored");

            CliAssert.ExitCode(5, run);
            Assert.Equal(
                new[] { "cli.output.exists" },
                run.ErrorCodes());
            Assert.Equal(
                "keep",
                File.ReadAllText(
                    Path.Join(
                        output,
                        "keep.txt")));
            CliAssert.NoStagingFolders(
                directory.Root);
        }

        [Fact]
        public async Task MarkCaptureText_LateCommitConflictPublishesNoResultFields()
        {
            using var directory =
                new CliTestDirectory();
            string input =
                SyntheticCaptures.WriteXmatcap(
                    directory.Root,
                    "input.xmatcap",
                    new SyntheticXmatcapOptions(
                        RequestsPerDevice: 1,
                        UserVersion:
                            CaptureTextFormat.AmbiguousUserVersion));
            string output =
                Path.Join(
                    directory.Root,
                    "results");
            var request =
                new CliRequest(
                    CliCommands.MarkCaptureText,
                    Input: input,
                    Output: output,
                    Interpretation:
                        CaptureTextInterpretation.AsStored,
                    MaxInputMib: 512);

            CliReport report =
                await CliMarkCaptureTextCommand.RunAsync(
                    request,
                    "run",
                    CancellationToken.None,
                    commitObserver:
                        new BeforeCommitObserver(
                            () =>
                                Directory.CreateDirectory(
                                    output)),
                    configureHeadlessStorage:
                        _ =>
                        {
                        });

            Assert.Equal(
                CliExitCodes.Conflict,
                report.ExitCode);
            Assert.Equal(
                "cli.output.exists",
                Assert.Single(
                    report.Errors)
                    .Code);
            Assert.Empty(
                report.Artifacts);
            using JsonDocument json =
                JsonDocument.Parse(
                    CliJson.Serialize(
                        report));
            JsonElement mark =
                json.RootElement.GetProperty(
                    "markCaptureText");
            Assert.False(
                mark.TryGetProperty(
                    "source",
                    out _));
            Assert.False(
                mark.TryGetProperty(
                    "output",
                    out _));
            Assert.False(
                mark.TryGetProperty(
                    "counts",
                    out _));
            Assert.False(
                Directory.Exists(
                    Path.Join(
                        output,
                        "capture.xmatcap")));
            CliAssert.NoStagingFolders(
                directory.Root);
        }

        [Fact]
        public async Task MarkCaptureText_CancellationAfterSummaryDiscardsEverything()
        {
            using var directory =
                new CliTestDirectory();
            string input =
                SyntheticCaptures.WriteXmatcap(
                    directory.Root,
                    "input.xmatcap",
                    new SyntheticXmatcapOptions(
                        RequestsPerDevice: 1,
                        UserVersion:
                            CaptureTextFormat.AmbiguousUserVersion));
            string output =
                Path.Join(
                    directory.Root,
                    "results");
            var request =
                new CliRequest(
                    CliCommands.MarkCaptureText,
                    Input: input,
                    Output: output,
                    Interpretation:
                        CaptureTextInterpretation.AsStored,
                    MaxInputMib: 512);
            using var cancellation =
                new CancellationTokenSource();

            CliReport report =
                await CliMarkCaptureTextCommand.RunAsync(
                    request,
                    "run",
                    cancellation.Token,
                    commitObserver:
                        new BeforeCommitObserver(
                            cancellation.Cancel),
                    configureHeadlessStorage:
                        _ =>
                        {
                        });

            Assert.Equal(
                CliExitCodes.Cancelled,
                report.ExitCode);
            Assert.Equal(
                "cli.cancelled",
                Assert.Single(
                    report.Errors)
                    .Code);
            Assert.Empty(
                report.Artifacts);
            Assert.False(
                Directory.Exists(
                    output));
            using JsonDocument json =
                JsonDocument.Parse(
                    CliJson.Serialize(
                        report));
            JsonElement mark =
                json.RootElement.GetProperty(
                    "markCaptureText");
            Assert.False(
                mark.TryGetProperty(
                    "source",
                    out _));
            Assert.False(
                mark.TryGetProperty(
                    "output",
                    out _));
            Assert.False(
                mark.TryGetProperty(
                    "counts",
                    out _));
            CliAssert.NoStagingFolders(
                directory.Root);
        }

        [Fact]
        public async Task MarkCaptureText_CancellationDuringTheOperationReportsTheOperationCode()
        {
            using var directory =
                new CliTestDirectory();
            string input =
                SyntheticCaptures.WriteXmatcap(
                    directory.Root,
                    "input.xmatcap",
                    new SyntheticXmatcapOptions(
                        RequestsPerDevice: 1,
                        UserVersion:
                            CaptureTextFormat.AmbiguousUserVersion));
            string output =
                Path.Join(
                    directory.Root,
                    "results");
            using var cancellation =
                new CancellationTokenSource();

            CliReport report =
                await CliMarkCaptureTextCommand.RunAsync(
                    new CliRequest(
                        CliCommands.MarkCaptureText,
                        Input: input,
                        Output: output,
                        Interpretation:
                            CaptureTextInterpretation.AsStored,
                        MaxInputMib: 512),
                    "run",
                    cancellation.Token,
                    configureHeadlessStorage:
                        _ =>
                        {
                        },
                    configureOperation:
                        operation =>
                            operation.Checkpoint =
                                checkpoint =>
                                {
                                    if (checkpoint ==
                                        "output-opened")
                                    {
                                        cancellation.Cancel();
                                    }
                                });

            Assert.Equal(
                CliExitCodes.Cancelled,
                report.ExitCode);
            Assert.Equal(
                "capture.repair.cancelled",
                Assert.Single(
                    report.Errors)
                    .Code);
            Assert.Empty(
                report.Artifacts);
            Assert.Equal(
                CliCleanupStates.Completed,
                report.CleanupState);
            Assert.False(
                Directory.Exists(
                    output));
            CliAssert.NoStagingFolders(
                directory.Root);
        }

        [Theory]
        [InlineData(
            "output-invalid",
            CliExitCodes.Failed,
            "capture.repair.output-invalid")]
        [InlineData(
            "write-failed",
            CliExitCodes.Failed,
            "cli.output.write-failed")]
        [InlineData(
            "internal",
            CliExitCodes.Internal,
            "capture.repair.internal")]
        [InlineData(
            "location-unsupported",
            CliExitCodes.InvalidInput,
            "analysis.input.location-unsupported")]
        [InlineData(
            "malformed",
            CliExitCodes.InvalidInput,
            "analysis.input.malformed")]
        [InlineData(
            "output-unavailable",
            CliExitCodes.Failed,
            "cli.output.unavailable")]
        public async Task MarkCaptureText_SeamedFailuresPinTheirCliContract(
            string problem,
            int exitCode,
            string code)
        {
            using var directory =
                new CliTestDirectory();
            string input =
                SyntheticCaptures.WriteXmatcap(
                    directory.Root,
                    "input.xmatcap",
                    new SyntheticXmatcapOptions(
                        RequestsPerDevice: 1,
                        UserVersion:
                            CaptureTextFormat.AmbiguousUserVersion));
            string output =
                Path.Join(
                    directory.Root,
                    "results");
            var request =
                new CliRequest(
                    CliCommands.MarkCaptureText,
                    Input: input,
                    Output: output,
                    Interpretation:
                        CaptureTextInterpretation.AsStored,
                    MaxInputMib: 512);

            CliReport report =
                await CliMarkCaptureTextCommand.RunAsync(
                    request,
                    "run",
                    CancellationToken.None,
                    configureHeadlessStorage:
                        _ =>
                        {
                        },
                    configureOperation:
                        operation =>
                        {
                            if (problem ==
                                "internal")
                            {
                                operation.RepairConnectionOpened =
                                    _ =>
                                        throw new InvalidOperationException(
                                            "Injected repair failure.");
                            }
                            else if (problem ==
                                "location-unsupported")
                            {
                                operation.DatabaseFactory =
                                    (databasePath, profile) =>
                                        new SqLtReadonlyDatabase(
                                            databasePath,
                                            profile,
                                            beforeSqliteOpens: null,
                                            finalPathUnavailable:
                                                _ =>
                                                    true);
                            }
                            else if (problem ==
                                "malformed")
                            {
                                operation.DatabaseFactory =
                                    (_, _) =>
                                    {
                                        using var connection =
                                            new SqliteConnection(
                                                "Data Source=:memory:");
                                        connection.Open();
                                        using SqliteCommand command =
                                            connection.CreateCommand();
                                        command.CommandText =
                                            "SELECT * FROM missing_table";
                                        command.ExecuteScalar();
                                        throw new InvalidOperationException(
                                            "The malformed SQLite seam did not fail.");
                                    };
                            }
                        },
                    outputPrepared:
                        folder =>
                        {
                            if (problem ==
                                "output-invalid")
                            {
                                File.WriteAllText(
                                    Path.Join(
                                        folder.StagingPath,
                                        CliMarkCaptureTextCommand.CapturePath),
                                    "not empty");
                            }
                            else if (problem ==
                                "write-failed")
                            {
                                Directory.CreateDirectory(
                                    Path.Join(
                                        folder.StagingPath,
                                        CliMarkCaptureTextCommand.SummaryPath));
                            }
                        },
                    reserveCheckpoint:
                        name =>
                        {
                            if (problem ==
                                    "output-unavailable" &&
                                name.StartsWith(
                                    CliOutputFolder.StagingPrefix,
                                    StringComparison.Ordinal))
                            {
                                throw new IOException(
                                    "Injected staging creation failure.");
                            }
                        });

            Assert.Equal(
                exitCode,
                report.ExitCode);
            Assert.Equal(
                code,
                Assert.Single(
                    report.Errors)
                    .Code);
            Assert.Empty(
                report.Artifacts);
            using JsonDocument json =
                JsonDocument.Parse(
                    CliJson.Serialize(
                        report));
            Assert.Empty(
                json.RootElement.GetProperty(
                        "artifacts")
                    .EnumerateArray());
            Assert.False(
                json.RootElement.GetProperty(
                        "markCaptureText")
                    .TryGetProperty(
                        "output",
                        out _));
            Assert.False(
                Directory.Exists(
                    output));
            CliAssert.NoStagingFolders(
                directory.Root);
        }

        [Fact]
        public async Task MarkCaptureText_CleanupFailureWinsAfterARepairRefusal()
        {
            using var directory =
                new CliTestDirectory();
            string input =
                SyntheticCaptures.WriteXmatcap(
                    directory.Root,
                    "input.xmatcap",
                    new SyntheticXmatcapOptions(
                        RequestsPerDevice: 1,
                        UserVersion:
                            CaptureTextFormat.AmbiguousUserVersion));
            string output =
                Path.Join(
                    directory.Root,
                    "results");
            string staging = null;
            FileStream held = null;
            try
            {
                CliReport report =
                    await CliMarkCaptureTextCommand.RunAsync(
                        new CliRequest(
                            CliCommands.MarkCaptureText,
                            Input: input,
                            Output: output,
                            Interpretation:
                                CaptureTextInterpretation.AsStored,
                            MaxInputMib: 512),
                        "run",
                        CancellationToken.None,
                        configureHeadlessStorage:
                            _ =>
                            {
                            },
                        outputPrepared:
                            folder =>
                            {
                                staging =
                                    folder.StagingPath;
                                File.WriteAllText(
                                    Path.Join(
                                        staging,
                                        CliMarkCaptureTextCommand.CapturePath),
                                    "not empty");
                                held =
                                    new FileStream(
                                        Path.Join(
                                            staging,
                                            "held"),
                                        FileMode.CreateNew,
                                        FileAccess.Write,
                                        FileShare.None);
                            });

                Assert.Equal(
                    CliExitCodes.CleanupFailed,
                    report.ExitCode);
                Assert.Equal(
                    new[]
                    {
                        "capture.repair.output-invalid",
                        "cli.cleanup.failed",
                    },
                    report.Errors.Select(
                        error =>
                            error.Code));
                Assert.Empty(
                    report.Artifacts);
                Assert.False(
                    Directory.Exists(
                        output));
                Assert.True(
                    Directory.Exists(
                        staging));
            }
            finally
            {
                held?.Dispose();
                if (staging != null &&
                    Directory.Exists(
                        staging))
                {
                    Directory.Delete(
                        staging,
                        recursive: true);
                }
            }
            CliAssert.NoStagingFolders(
                directory.Root);
        }

        [Fact]
        public async Task MarkCaptureText_LinkOutputIsRefusedBeforeTheInputScan()
        {
            using var directory = new CliTestDirectory();
            string real =
                Path.Join(
                    directory.Root,
                    "real");
            Directory.CreateDirectory(real);
            string link =
                Path.Join(
                    directory.Root,
                    "link");
            Links.Junction(
                link,
                real);
            try
            {
                XmatRun run =
                    await XmatProcess.RunAsync(
                        directory.Root,
                        "mark-capture-text",
                        "missing.xmatcap",
                        "--out",
                        Path.Join(
                            link,
                            "results"),
                        "--interpret",
                        "as-stored");

                CliAssert.ExitCode(6, run);
                Assert.Equal(
                    new[] { "cli.output.unprotected" },
                    run.ErrorCodes());
                Assert.Empty(
                    Directory.GetFileSystemEntries(
                        real));
            }
            finally
            {
                Links.Remove(link);
            }
        }

        [Theory]
        [InlineData("missing-input", CliExitCodes.InvalidInput, "analysis.input.not-found")]
        [InlineData("too-large", CliExitCodes.InvalidInput, "analysis.input.too-large")]
        [InlineData("missing-parent", CliExitCodes.Usage, "cli.output.parent-missing")]
        [InlineData("limit", CliExitCodes.InvalidInput, "analysis.input.limit")]
        [InlineData("live-database", CliExitCodes.InvalidInput, "analysis.input.live-database")]
        [InlineData("schema", CliExitCodes.InvalidInput, "analysis.input.schema")]
        public async Task MarkCaptureText_PinsAdditionalRefusalCodes(
            string problem,
            int exitCode,
            string code)
        {
            using var directory = new CliTestDirectory();
            string input =
                Path.Join(
                    directory.Root,
                    "input.xmatcap");
            string output =
                problem == "missing-parent"
                    ? Path.Join(
                        directory.Root,
                        "missing",
                        "results")
                    : Path.Join(
                        directory.Root,
                        "results");
            if (problem != "missing-input")
            {
                input =
                    SyntheticCaptures.WriteXmatcap(
                        directory.Root,
                        "input.xmatcap",
                        new SyntheticXmatcapOptions(
                            RequestsPerDevice: 1,
                            UserVersion:
                                CaptureTextFormat.AmbiguousUserVersion));
            }
            if (problem == "too-large")
            {
                using FileStream stream =
                    new(
                        input,
                        FileMode.Open,
                        FileAccess.Write,
                        FileShare.None);
                stream.SetLength(
                    2L *
                    1024 *
                    1024);
            }
            else if (problem == "limit")
            {
                HostileSqliteSchemas.ReplaceSchemaSql(
                    input,
                    "WebProxyConnections",
                    "CREATE TABLE WebProxyConnections(" +
                    SyntheticCaptures.Canary +
                    new string(
                        'x',
                        SqLtSchemaProfile.MaxSchemaRecordBytes *
                            2) +
                    ")");
            }
            else if (problem == "live-database")
            {
                File.WriteAllBytes(
                    input +
                        "-wal",
                    new byte[]
                    {
                        1,
                    });
            }
            else if (problem == "schema")
            {
                File.Delete(
                    input);
                input =
                    HostileSqliteSchemas.CreateGeneratedColumnCapture(
                        directory.Root,
                        "input.xmatcap");
                HostileSqliteSchemas.SetUserVersion(
                    input,
                    CaptureTextFormat.AmbiguousUserVersion);
            }
            var args =
                new List<string>
                {
                    "mark-capture-text",
                    input,
                    "--out",
                    output,
                    "--interpret",
                    "as-stored",
                };
            if (problem == "too-large")
            {
                args.Add(
                    "--max-input-mib");
                args.Add(
                    "1");
            }
            XmatRun run =
                await XmatProcess.RunAsync(
                    directory.Root,
                    args.ToArray());

            CliAssert.ExitCode(
                exitCode,
                run);
            Assert.Equal(
                new[]
                {
                    code,
                },
                run.ErrorCodes());
            Assert.Empty(
                run.Json()
                    .GetProperty(
                        "artifacts")
                    .EnumerateArray());
            Assert.False(
                Directory.Exists(
                    output));
            CliAssert.NoStagingFolders(
                directory.Root);
        }

        [Fact]
        public async Task MarkCaptureText_ActiveWriterIsUnreadableAndPublishesNothing()
        {
            using var directory = new CliTestDirectory();
            string input =
                SyntheticCaptures.WriteXmatcap(
                    directory.Root,
                    "input.xmatcap",
                    new SyntheticXmatcapOptions(
                        RequestsPerDevice: 1,
                        UserVersion:
                            CaptureTextFormat.AmbiguousUserVersion));
            string output =
                Path.Join(
                    directory.Root,
                    "results");
            using var writer =
                new FileStream(
                    input,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite |
                        FileShare.Delete);

            XmatRun run =
                await XmatProcess.RunAsync(
                    directory.Root,
                    "mark-capture-text",
                    input,
                    "--out",
                    output,
                    "--interpret",
                    "as-stored");

            CliAssert.ExitCode(
                CliExitCodes.InvalidInput,
                run);
            Assert.Equal(
                new[]
                {
                    "analysis.input.unreadable",
                },
                run.ErrorCodes());
            Assert.Empty(
                run.Json()
                    .GetProperty(
                        "artifacts")
                    .EnumerateArray());
            Assert.False(
                Directory.Exists(
                    output));
            CliAssert.NoStagingFolders(
                directory.Root);
        }

        private static void UpdateValue(
            string path,
            string value)
        {
            using var connection =
                Open(
                    path,
                    SqliteOpenMode.ReadWrite);
            using var command =
                connection.CreateCommand();
            command.CommandText =
                "UPDATE WebProxyConnections SET RequestHost=$value";
            command.Parameters.AddWithValue(
                "$value",
                value);
            command.ExecuteNonQuery();
        }

        private static string ReadValue(
            string path)
        {
            using var connection =
                Open(
                    path,
                    SqliteOpenMode.ReadOnly);
            using var command =
                connection.CreateCommand();
            command.CommandText =
                "SELECT RequestHost FROM WebProxyConnections";
            return command.ExecuteScalar() as string;
        }

        private static int ReadUserVersion(
            string path)
        {
            using var connection =
                Open(
                    path,
                    SqliteOpenMode.ReadOnly);
            using var command =
                connection.CreateCommand();
            command.CommandText =
                "PRAGMA user_version";
            return Convert.ToInt32(
                command.ExecuteScalar(),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        private static SqliteConnection Open(
            string path,
            SqliteOpenMode mode)
        {
            var connection =
                new SqliteConnection(
                    new SqliteConnectionStringBuilder
                    {
                        DataSource = path,
                        Mode = mode,
                        Pooling = false,
                    }.ToString());
            connection.Open();
            return connection;
        }

        private sealed class BeforeCommitObserver(
            Action action) :
            ICliCommitObserver
        {
            public void BeforeCommit() =>
                action();

            public void CommitAttemptFailed(
                int attempt)
            {
            }

            public void AfterCommit()
            {
            }
        }
    }
}
