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
        [InlineData("odd", "capture.repair.not-legacy-encoding")]
        [InlineData("non-native", "capture.repair.input-not-xmatcap")]
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
    }
}
