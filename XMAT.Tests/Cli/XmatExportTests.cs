// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using XMAT.DataAbstractionLayer;
using XMAT.Tests.Operations.Analysis;
using XMAT.Tests.Proxy;
using XMAT.WebServiceCapture;
using static XMAT.Tests.Cli.CliExportCommandLineTests;

namespace XMAT.Tests.Cli
{
    // xmat export, run as a child process: it publishes only the selected sessions and leaves the input and the GUI's
    // storage alone. It never replaces a file and writes nothing when nothing is selected. Its JSON reports the
    // publication in export.output, never in artifacts, and names no path or captured value.
    [Collection(IsolatedProxyTests.Name)]
    public class XmatExportTests
    {
        [Fact]
        public async Task Export_PublishesOnlyTheSelectedSessions()
        {
            using var directory = new CliTestDirectory();
            string input = ProcessCaptures.WriteXmatcap(directory.Root, "capture.xmatcap", ProcessCaptures.MixedSessions);
            string inputHash = CliAssert.Sha256(input);
            GuiStorageProbe gui = GuiStorageProbe.Capture();

            XmatRun run = await XmatProcess.RunAsync(
                directory.Root, "export", input, "--out", "subset.xmatcap", "--process", "game*.exe");

            CliAssert.ExitCode(0, run);
            string output = Path.Join(directory.Root, "subset.xmatcap");
            JsonElement json = run.Json();
            JsonElement block = json.GetProperty("export");
            Assert.Equal(new long[] { 8, 4, 4, 4, 4, 0, 0, 0 }, Counts(block.GetProperty("counts")));
            Assert.Equal(
                (true, new FileInfo(output).Length, CliAssert.Sha256(output)),
                (block.GetProperty("output").GetProperty("published").GetBoolean(),
                    block.GetProperty("output").GetProperty("bytes").GetInt64(),
                    block.GetProperty("output").GetProperty("sha256").GetString()));
            Assert.Empty(json.GetProperty("artifacts").EnumerateArray());
            Assert.Equal(ProcessCaptures.MixedGameRowIds, RequestNumbers(output));
            Assert.Equal(inputHash, CliAssert.Sha256(input));
            Assert.DoesNotContain("Game.exe", run.Stdout + run.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(ProcessCaptures.Device, run.Stdout + run.Stderr, StringComparison.OrdinalIgnoreCase);
            CliAssert.NoLeaks(run, directory);
            gui.AssertUnchanged();
        }

        // An existing --out is never replaced (exit 5); a selection without sessions writes nothing (exit 3).
        [Fact]
        public async Task Export_PublishesNothingOverAFileOrForAnEmptySelection()
        {
            using var directory = new CliTestDirectory();
            string input = ProcessCaptures.WriteXmatcap(directory.Root, "capture.xmatcap", ProcessCaptures.MixedSessions);
            string existing = Path.Join(directory.Root, "subset.saz");
            File.WriteAllText(existing, "keep me");
            string[] before = directory.Entries();

            XmatRun replace = await XmatProcess.RunAsync(directory.Root, "export", input, "--out", "subset.saz");
            XmatRun empty = await XmatProcess.RunAsync(
                directory.Root, "export", input, "--out", "none.saz", "--process", "nomatch.exe");

            CliAssert.ExitCode(5, replace);
            CliAssert.ExitCode(3, empty);
            Assert.Equal("keep me", File.ReadAllText(existing));
            Assert.Equal(before, directory.Entries());
            Assert.Equal(new long[] { 8, 0, 0, 0, 0, 0, 0, 0 }, Counts(empty.Json().GetProperty("export").GetProperty("counts")));
            foreach (XmatRun run in new[] { replace, empty })
            {
                Assert.False(run.Json().GetProperty("export").GetProperty("output").GetProperty("published").GetBoolean());
                CliAssert.NoLeaks(run, directory);
            }
        }

        // The request numbers in an exported capture, in order.
        internal static long[] RequestNumbers(string capture)
        {
            using var database = new SqLtReadonlyDatabase(capture);
            return database.TableByName(WebServiceCaptureMethod.TableName).Dataset.DataRecords
                .Select(record => record.Int(WebServiceCaptureMethod.FieldKey_RequestNumber))
                .OrderBy(number => number)
                .ToArray();
        }
    }
}
