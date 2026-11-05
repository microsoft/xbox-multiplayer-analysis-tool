// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using XMAT.DataAbstractionLayer;
using XMAT.Operations.Processes;
using XMAT.Tests.Operations.Analysis;
using XMAT.Tests.Proxy;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using static XMAT.Tests.Cli.CliExportCommandLineTests;

namespace XMAT.Tests.Cli
{
    // xmat export, run as a child process: it publishes only the selected sessions and leaves the input and the GUI's
    // storage alone. It never replaces a file and writes nothing when nothing is selected. Its JSON reports the
    // publication in export.output, never in artifacts, and names no path or captured value.
    [Collection(IsolatedProxyTests.Name)]
    public class XmatExportTests
    {
        // A SAZ export keeps each selected session whole (its own response and XMAT's metadata, as the import reads them),
        // leaves the input byte for byte, and reports its publication without naming a path or a captured value.
        [Fact]
        public async Task Export_PublishesOnlyTheSelectedSessionsWithTheirOwnResponsesAndMetadata()
        {
            using var directory = new CliTestDirectory();
            ProxyConnectionModel[] sessions = Enumerable.Range(1, 3).Select(PairedSession).ToArray();
            string input = Path.Join(directory.Root, "capture.saz");
            SazArchive.Write(input, sessions);
            string inputHash = CliAssert.Sha256(input);
            GuiStorageProbe gui = GuiStorageProbe.Capture();

            XmatRun run = await XmatProcess.RunAsync(
                directory.Root, "export", input, "--out", "subset.saz", "--process", "gam?.exe");

            CliAssert.ExitCode(0, run);
            string output = Path.Join(directory.Root, "subset.saz");
            JsonElement json = run.Json();
            JsonElement block = json.GetProperty("export");
            Assert.Equal(new long[] { 3, 2, 2, 2, 2, 0, 0, 0 }, Counts(block.GetProperty("counts")));
            Assert.Equal(
                (true, new FileInfo(output).Length, CliAssert.Sha256(output)),
                (block.GetProperty("output").GetProperty("published").GetBoolean(),
                    block.GetProperty("output").GetProperty("bytes").GetInt64(),
                    block.GetProperty("output").GetProperty("sha256").GetString()));
            Assert.Empty(json.GetProperty("artifacts").EnumerateArray());
            ProxyConnectionModel[] exported = FiddlerSazHandler.ImportSazFile(output, CancellationToken.None)
                .OrderBy(session => session.RequestNumber).ToArray();
            Assert.Equal(new long[] { 1, 3 }, exported.Select(session => session.RequestNumber));
            Assert.All(exported, session => Assert.Equal(Facts(sessions[session.RequestNumber - 1]), Facts(session)));
            Assert.Equal(inputHash, CliAssert.Sha256(input));
            Assert.DoesNotContain("Game.exe", run.Stdout + run.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("pair.test", run.Stdout + run.Stderr, StringComparison.OrdinalIgnoreCase);
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

        // xmat.exe is not long-path aware, yet an --out folder past the classic 260-character limit (three 100-character
        // folders) is staged and published like any other, and its staging folder is removed.
        [Fact]
        public async Task Export_PublishesIntoAFolderLongerThanMaxPath()
        {
            using var directory = new CliTestDirectory();
            string input = Path.Join(directory.Root, "capture.saz");
            SazArchive.Write(input, new[] { PairedSession(1) });
            string folder = Path.Join(directory.Root, new string('d', 100), new string('e', 100), new string('f', 100));
            Directory.CreateDirectory(folder);
            string output = Path.Join(folder, "x.saz");

            XmatRun run = await XmatProcess.RunAsync(directory.Root, "export", input, "--out", output);

            CliAssert.ExitCode(0, run);
            Assert.Equal(new[] { output }, Directory.GetFileSystemEntries(folder));
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

        // A session whose request and response both carry its number; the second one is svchost.exe's, the others
        // Game.exe's. Only XMAT's flags restore its http scheme and its query (its request line says https, and a plain
        // import drops the query), its IPv6 client address and its times finer than a Fiddler timer.
        private static ProxyConnectionModel PairedSession(int number)
        {
            byte[] response = Encoding.UTF8.GetBytes($"{{\"session\":{number}}}");
            var session = new ProxyConnectionModel(
                number, "200", new DateTime(2026, 11, 4, 1, 2, 3, DateTimeKind.Utc).AddTicks(number * 1_234_567L),
                TimeSpan.FromTicks(number * 7_654_321L), "http", "pair.test:443", "GET", $"/pair/{number}?q={number}",
                $"GET https://pair.test/pair/{number}?q={number} HTTP/1.1\r\nHost: pair.test\r\n\r\n", Array.Empty<byte>(),
                $"HTTP/1.1 200 OK\r\nContent-Length: {response.Length}\r\n\r\n", response, $"fe80::{number}")
            {
                RecordRowId = number,
            };
            session.SetImportedIdentity(number, 100 + number);
            session.SetClientProcess(new ClientProcessInfo(
                number == 2 ? "svchost.exe" : "Game.exe", number == 2 ? 900 : 4100, null, ClientProcessStatus.Local));
            return session;
        }

        // What a round trip keeps: the identities, timing, target, client, response and process.
        private static object Facts(ProxyConnectionModel session) => (
            session.RequestNumber, session.Id, session.RequestTime.ToUniversalTime(), session.Duration, session.Scheme,
            session.Path, session.ClientIP, Encoding.UTF8.GetString(session.ResponseBody), session.ClientProcess.Name,
            session.ClientProcess.ProcessId);
    }
}
