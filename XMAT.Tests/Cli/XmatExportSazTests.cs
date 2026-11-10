// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using XMAT.Cli;
using XMAT.Tests.Proxy;
using XMAT.Tests.Replay;
using XMAT.WebServiceCapture;

namespace XMAT.Tests.Cli
{
    [Collection(IsolatedProxyTests.Name)]
    public sealed class XmatExportSazTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ExportSaz_WritesArchiveAndFrozenSummary(
            bool sanitize)
        {
            using var directory =
                new CliTestDirectory();
            const string Canary =
                "export-saz-captured-canary";
            ReplayCaptures.WriteSaz(
                directory.Root,
                "input.saz",
                new CapturedExchange(
                    "POST",
                    "http://127.0.0.1/upload",
                    "Content-Type: text/plain\r\n" +
                    "X-Secret: " + Canary + "\r\n",
                    "visible body"u8.ToArray(),
                    ResponseHead:
                        "HTTP/1.1 200 OK\r\n" +
                        "Content-Type: text/plain\r\n",
                    ResponseBody:
                        "visible response"u8.ToArray()));
            var arguments = new List<string>
            {
                "export-saz",
                "input.saz",
                "--out",
                "results",
            };
            if (sanitize)
            {
                arguments.Add("--sanitize");
            }

            GuiStorageProbe gui = GuiStorageProbe.Capture();
            XmatRun run =
                await XmatProcess.RunAsync(
                    directory.Root,
                    arguments.ToArray());

            CliAssert.ExitCode(0, run);
            gui.AssertUnchanged();
            CliAssert.NoStagingFolders(directory.Root);
            JsonElement json = run.Json();
            Assert.Equal(
                "export-saz",
                json.GetProperty("command")
                    .GetString());
            Assert.Equal(
                "saz",
                json.GetProperty("input")
                    .GetProperty("kind")
                    .GetString());
            Assert.Equal(
                "d1",
                json.GetProperty("device")
                    .GetString());
            Assert.Equal(
                sanitize,
                json.GetProperty("sanitize")
                    .GetBoolean());
            Assert.Equal(
                1,
                json.GetProperty("export")
                    .GetProperty("sessions")
                    .GetInt32());
            Assert.DoesNotContain(
                Canary,
                run.Stdout,
                StringComparison.Ordinal);
            string output = Path.Join(
                directory.Root,
                "results");
            Assert.Equal(
                new[]
                {
                    "capture.saz",
                    "summary.json",
                },
                Directory.GetFiles(output)
                    .Select(Path.GetFileName)
                    .OrderBy(
                        name => name,
                        StringComparer.Ordinal));
            using JsonDocument summary =
                JsonDocument.Parse(
                    File.ReadAllBytes(
                        Path.Join(
                            output,
                            "summary.json")));
            Assert.Equal(
                "xmat-export-saz/1",
                summary.RootElement
                    .GetProperty("schema")
                    .GetString());
            Assert.Equal(
                sanitize
                    ? JsonValueKind.Object
                    : JsonValueKind.Null,
                summary.RootElement
                    .GetProperty("sanitized")
                    .ValueKind);
            Assert.Single(
                FiddlerSazHandler.ImportSazFile(
                    Path.Join(
                        output,
                        "capture.saz"),
                    CancellationToken.None));
        }

        [Fact]
        public async Task ExportSaz_DeviceSelectionFailuresPublishNothing()
        {
            using var directory =
                new CliTestDirectory();
            ReplayCaptures.WriteSaz(
                directory.Root,
                "input.saz",
                new CapturedExchange(
                    "GET",
                    "http://127.0.0.1/"));

            XmatRun run =
                await XmatProcess.RunAsync(
                    directory.Root,
                    "export-saz",
                    "input.saz",
                    "--out",
                    "results",
                    "--device",
                    "d9");

            CliAssert.ExitCode(3, run);
            Assert.Contains(
                run.Json()
                    .GetProperty("errors")
                    .EnumerateArray(),
                error =>
                    error.GetProperty("code")
                        .GetString() ==
                    CliExportSazCommand
                        .DeviceNotFoundCode);
            Assert.False(
                Directory.Exists(
                    Path.Join(
                        directory.Root,
                        "results")));
        }

        [Fact]
        public async Task ExportSaz_ProbesOutputBeforeReadingInput()
        {
            using var directory =
                new CliTestDirectory();
            Directory.CreateDirectory(
                Path.Join(
                    directory.Root,
                    "results"));

            XmatRun run =
                await XmatProcess.RunAsync(
                    directory.Root,
                    "export-saz",
                    "missing.saz",
                    "--out",
                    "results");

            CliAssert.ExitCode(5, run);
            Assert.Contains(
                run.Json()
                    .GetProperty("errors")
                    .EnumerateArray(),
                error =>
                    error.GetProperty("code")
                        .GetString() ==
                    "cli.output.exists");
        }

        // A refusal while planning must come before any staging folder or headless storage exists.
        [Fact]
        public async Task ExportSaz_PlanningRefusalStagesNothing()
        {
            using var directory =
                new CliTestDirectory();
            string input = ReplayCaptures.WriteXmatcap(
                directory.Root,
                "unmarked.xmatcap",
                ("Device 1", new[] { new CapturedExchange("GET", "http://127.0.0.1/one") }));
            XMAT.Tests.Fixtures.HostileSqliteSchemas.SetUserVersion(
                input,
                XMAT.DataAbstractionLayer.CaptureTextFormat.AmbiguousUserVersion);
            string sha256 = CliAssert.Sha256(input);

            GuiStorageProbe gui = GuiStorageProbe.Capture();
            XmatRun run =
                await XmatProcess.RunAsync(
                    directory.Root,
                    "export-saz",
                    "unmarked.xmatcap",
                    "--out",
                    "results");

            CliAssert.ExitCode(3, run);
            Assert.Equal(
                new[] { "capture.text.ambiguous-export" },
                run.ErrorCodes());
            gui.AssertUnchanged();
            CliAssert.NoStagingFolders(directory.Root);
            Assert.False(
                Directory.Exists(
                    Path.Join(
                        directory.Root,
                        "results")));
            Assert.Equal(sha256, CliAssert.Sha256(input));
        }

        // Accepting the stored text of an unmarked capture exports it, with the ambiguity reported once as a warning.
        [Fact]
        public async Task ExportSaz_AcceptedStoredTextOfUnmarkedCaptureIsExportedWithWarning()
        {
            using var directory =
                new CliTestDirectory();
            string input = ReplayCaptures.WriteXmatcap(
                directory.Root,
                "unmarked.xmatcap",
                ("Device 1", new[] { new CapturedExchange("GET", "http://127.0.0.1/one") }));
            XMAT.Tests.Fixtures.HostileSqliteSchemas.SetUserVersion(
                input,
                XMAT.DataAbstractionLayer.CaptureTextFormat.AmbiguousUserVersion);
            string sha256 = CliAssert.Sha256(input);

            XmatRun run =
                await XmatProcess.RunAsync(
                    directory.Root,
                    "export-saz",
                    "unmarked.xmatcap",
                    "--out",
                    "results",
                    "--accept-stored-text");

            CliAssert.ExitCode(0, run);
            Assert.Empty(run.ErrorCodes());
            Assert.Single(
                run.Json()
                    .GetProperty("warnings")
                    .EnumerateArray(),
                warning =>
                    warning.GetProperty("code")
                        .GetString() ==
                    "capture.text.ambiguous");
            CliAssert.NoStagingFolders(directory.Root);
            Assert.Single(
                FiddlerSazHandler.ImportSazFile(
                    Path.Join(
                        directory.Root,
                        "results",
                        "capture.saz"),
                    CancellationToken.None));
            Assert.Equal(sha256, CliAssert.Sha256(input));
        }

        // A start line only the archive writer refuses keeps its own code and invalid-input exit, not a write failure.
        [Fact]
        public async Task ExportSaz_MalformedStartLineIsRefusedAsInvalidInput()
        {
            using var directory =
                new CliTestDirectory();
            ReplayCaptures.WriteXmatcap(
                directory.Root,
                "input.xmatcap",
                ("Device 1", new[] { new CapturedExchange("GET ", "http://127.0.0.1/one") }));

            XmatRun run =
                await XmatProcess.RunAsync(
                    directory.Root,
                    "export-saz",
                    "input.xmatcap",
                    "--out",
                    "results");

            CliAssert.ExitCode(3, run);
            Assert.Equal(
                new[] { "capture-file.export.invalid-start-line" },
                run.ErrorCodes());
            CliAssert.NoStagingFolders(directory.Root);
            Assert.False(
                Directory.Exists(
                    Path.Join(
                        directory.Root,
                        "results")));
        }

        // A stored head without its blank line still gets exactly one separator, so a header-shaped body stays body.
        [Fact]
        public async Task ExportSaz_UnterminatedStoredHeadKeepsItsBodySeparate()
        {
            using var directory =
                new CliTestDirectory();
            byte[] body =
                "X-Injected: 1\r\n\r\ntail"u8.ToArray();
            ReplayCaptures.WriteXmatcap(
                directory.Root,
                "input.xmatcap",
                ("Device 1", new[] { new CapturedExchange("POST", "http://127.0.0.1/upload", Body: body) }));

            XmatRun run =
                await XmatProcess.RunAsync(
                    directory.Root,
                    "export-saz",
                    "input.xmatcap",
                    "--out",
                    "results");

            CliAssert.ExitCode(0, run);
            using System.IO.Compression.ZipArchive archive =
                System.IO.Compression.ZipFile.OpenRead(
                    Path.Join(
                        directory.Root,
                        "results",
                        "capture.saz"));
            System.IO.Compression.ZipArchiveEntry entry =
                Assert.Single(
                    archive.Entries,
                    candidate =>
                        candidate.FullName.EndsWith(
                            "_c.txt",
                            StringComparison.Ordinal));
            using var written = new MemoryStream();
            using (Stream stream = entry.Open())
            {
                stream.CopyTo(written);
            }

            byte[] bytes = written.ToArray();
            int separator =
                bytes.AsSpan().IndexOf(
                    "\r\n\r\n"u8);
            Assert.True(separator > 0);
            Assert.Equal(
                body,
                bytes[(separator + 4)..]);
            Assert.DoesNotContain(
                "X-Injected",
                System.Text.Encoding.Latin1.GetString(
                    bytes,
                    0,
                    separator),
                StringComparison.Ordinal);
        }
    }
}
