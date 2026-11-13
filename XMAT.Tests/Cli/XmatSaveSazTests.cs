// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using XMAT.Cli;
using XMAT.Tests.Proxy;
using XMAT.Tests.Replay;

namespace XMAT.Tests.Cli
{
    // xmat replay and compose --save-saz: every send as a session of replays.saz, published with the run and only when it
    // is, and an archive that xmat reads back. Loopback servers only; nothing leaves the machine. In-process runs reach
    // the archive's stream through a seam (CliReplayCommand.SazStreamSeam) to fail or cancel it at a point of its writing.
    [Collection(IsolatedProxyTests.Name)]
    public sealed class XmatSaveSazTests
    {
        private const int MiB = 1024 * 1024;

        // --parallel 4 with 8 sends: the archive has each send once, under its id, with what replays/ has for it, and xmat
        // replays the archive: a replay of a replay.
        [Fact]
        public async Task Compose_SavesEverySendAsASession_AndTheArchiveReplays()
        {
            using var directory = new CliTestDirectory();
            await using var server = new LoopbackHttpServer(request => LoopbackResponse.Ok("item " + request.Target));

            XmatRun run = await XmatProcess.RunAsync(
                directory.Root, "compose", "--url", server.Origin + "/item/#", "--sequence", "1-8", "--parallel", "4", "--save-saz", "--out", "results");

            CliAssert.ExitCode(0, run);
            JsonElement json = run.Json();
            Assert.Equal(
                new[] { "summary:summary.json", "replays:replays", "saz:replays.saz" },
                json.GetProperty("artifacts").EnumerateArray()
                    .Select(artifact => artifact.GetProperty("kind").GetString() + ":" + artifact.GetProperty("path").GetString()));
            string published = Path.Join(directory.Root, "results");
            using (JsonDocument summary = JsonDocument.Parse(File.ReadAllText(Path.Join(published, "summary.json"))))
            {
                Assert.True(summary.RootElement.GetProperty("options").GetProperty("saveSaz").GetBoolean());
            }

            string archivePath = Path.Join(published, "replays.saz");
            using (ZipArchive archive = ZipFile.OpenRead(archivePath))
            {
                for (int id = 1; id <= 8; id++)
                {
                    string[] flags = Flags(archive, id);
                    Assert.Contains($"x-xmat-replay-id={id}", flags);
                    string request = File.ReadAllText(Path.Join(published, "replays", id.ToString("D5"), "request.http"));
                    string number = flags.Single(flag => flag.StartsWith("x-xmat-replay-sequence=", StringComparison.Ordinal)).Split('=')[1];
                    Assert.StartsWith($"GET /item/{number} HTTP/1.1\r\n", request, StringComparison.Ordinal);
                    Assert.StartsWith(
                        $"GET {server.Origin}/item/{number} HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\n",
                        Text(archive, $"raw/{id}_c.txt"),
                        StringComparison.Ordinal);
                    Assert.EndsWith("item /item/" + number, Text(archive, $"raw/{id}_s.txt"), StringComparison.Ordinal);
                }

                Assert.Equal(24, archive.Entries.Count);
            }

            XmatRun again = await XmatProcess.RunAsync(
                directory.Root, "replay", archivePath, "--request", "all", "--save-saz", "--out", "again");

            CliAssert.ExitCode(0, again);
            Assert.Equal(16, server.Requests.Count);
            Assert.Equal(
                server.Requests.Take(8).Select(request => request.Target).OrderBy(target => target, StringComparer.Ordinal),
                server.Requests.Skip(8).Select(request => request.Target).OrderBy(target => target, StringComparer.Ordinal));

            // xmat replay --save-saz publishes its own archive, whose sessions are the replay's sends.
            Assert.Contains(
                "saz:replays.saz",
                again.Json().GetProperty("artifacts").EnumerateArray()
                    .Select(artifact => artifact.GetProperty("kind").GetString() + ":" + artifact.GetProperty("path").GetString()));
            string replayed = Path.Join(directory.Root, "again");
            using (JsonDocument summary = JsonDocument.Parse(File.ReadAllText(Path.Join(replayed, "summary.json"))))
            {
                Assert.True(summary.RootElement.GetProperty("options").GetProperty("saveSaz").GetBoolean());
            }

            using (ZipArchive archive = ZipFile.OpenRead(Path.Join(replayed, "replays.saz")))
            {
                Assert.Equal(24, archive.Entries.Count);
                for (int id = 1; id <= 8; id++)
                {
                    string[] flags = Flags(archive, id);
                    Assert.Contains($"x-xmat-replay-id={id}", flags);
                    Assert.Contains("x-xmat-replay-device=d1", flags);
                    Assert.Single(flags, flag => flag.StartsWith("x-xmat-replay-of=", StringComparison.Ordinal));
                    string requestLine = File.ReadAllLines(Path.Join(replayed, "replays", id.ToString("D5"), "request.http"))[0];
                    string target = requestLine.Split(' ')[1];
                    Assert.StartsWith(
                        $"GET {server.Origin}{target} HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\n",
                        Text(archive, $"raw/{id}_c.txt"),
                        StringComparison.Ordinal);
                    Assert.EndsWith("item " + target, Text(archive, $"raw/{id}_s.txt"), StringComparison.Ordinal);
                }
            }

            CliAssert.NoStagingFolders(directory.Root);
        }

        // A cancel while a large body is written into the archive stops the writing within a slice: nothing is published.
        [Fact]
        public async Task ACancelWhileTheArchiveIsWritten_StopsIt_AndPublishesNothing()
        {
            using var directory = new CliTestDirectory();
            byte[] large = new byte[6 * MiB];
            new Random(11).NextBytes(large);
            await using var server = new LoopbackHttpServer(_ => new LoopbackResponse(Body: large));
            using var cancellation = new CancellationTokenSource();
            var seam = new SeamStream.Options { OnWritten = total => { if (total > 3 * MiB / 2) cancellation.Cancel(); } };

            CliReport report = await RunInProcess(seam, cancellation.Token, "compose", "--url", server.Origin + "/large", "--save-saz", "--out", Path.Join(directory.Root, "results"));

            Assert.Equal(CliExitCodes.Cancelled, report.ExitCode);
            Assert.InRange(seam.Written, 3 * MiB / 2, 4 * MiB);
            Assert.False(Directory.Exists(Path.Join(directory.Root, "results")));
            CliAssert.NoStagingFolders(directory.Root);
        }

        // An archive that cannot be completed, or flushed once completed, fails the run: nothing is published, and the
        // staging folder is removed, so nothing kept the archive open.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AnArchiveThatCannotBeFinished_FailsTheRun_AndPublishesNothing(bool atFlush)
        {
            using var directory = new CliTestDirectory();
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok("ok"));
            var seam = new SeamStream.Options { FailCentralDirectory = !atFlush, FailFlushAfterEnd = atFlush };

            CliReport report = await RunInProcess(seam, CancellationToken.None, "compose", "--url", server.Origin + "/", "--save-saz", "--out", Path.Join(directory.Root, "results"));

            Assert.Equal(CliExitCodes.Failed, report.ExitCode);
            Assert.Equal(new[] { "cli.output.write-failed" }, report.Errors.Select(error => error.Code));
            Assert.Equal(CliCleanupStates.Completed, report.CleanupState);
            Assert.False(Directory.Exists(Path.Join(directory.Root, "results")));
            CliAssert.NoStagingFolders(directory.Root);
        }

        // A run that publishes nothing never completes its archive, and its own failure stands.
        [Fact]
        public async Task ARefusedRun_NeverCompletesItsArchive_AndKeepsItsFailure()
        {
            using var directory = new CliTestDirectory();
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok("ok"));
            var seam = new SeamStream.Options();

            CliReport report = await RunInProcess(seam, CancellationToken.None, "compose", "--url", server.Origin + "/", "--method", "POST", "--save-saz", "--out", Path.Join(directory.Root, "results"));

            Assert.Equal(CliExitCodes.InvalidInput, report.ExitCode);
            Assert.Equal(new[] { "replay.policy.unsafe-method" }, report.Errors.Select(error => error.Code));
            Assert.True(seam.Created);
            Assert.False(seam.SawEnd);
            Assert.Equal(CliCleanupStates.Completed, report.CleanupState);
            Assert.Empty(server.Requests);
            CliAssert.NoStagingFolders(directory.Root);
        }

        // Each send goes into the archive as it completes, never held back to the end of the run.
        [Fact]
        public async Task EachSend_IsWrittenIntoTheArchiveAsItCompletes()
        {
            using var directory = new CliTestDirectory();
            var firstWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool writtenBeforeTheSecondAnswer = false;
            await using var server = new LoopbackHttpServer(async request =>
            {
                if (request.Target == "/item/2")
                {
                    writtenBeforeTheSecondAnswer =
                        await Task.WhenAny(firstWritten.Task, Task.Delay(TimeSpan.FromSeconds(5))) == firstWritten.Task;
                }

                return LoopbackResponse.Ok("ok");
            });
            var seam = new SeamStream.Options { OnWritten = _ => firstWritten.TrySetResult() };

            CliReport report = await RunInProcess(seam, CancellationToken.None, "compose", "--url", server.Origin + "/item/#", "--sequence", "1-2", "--save-saz", "--out", Path.Join(directory.Root, "results"));

            Assert.Equal(CliExitCodes.Success, report.ExitCode);
            Assert.True(writtenBeforeTheSecondAnswer);
            Assert.True(seam.SawEnd);
        }

        // Under --parallel, a send goes into the archive as soon as it completes, ahead of an earlier send that still waits:
        // the archive keeps the order in which the sends completed, each under its plan id.
        [Fact]
        public async Task UnderParallel_ASendThatCompletesFirst_IsWrittenFirst()
        {
            using var directory = new CliTestDirectory();
            var secondWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool releasedBySecond = false;
            await using var server = new LoopbackHttpServer(async request =>
            {
                if (request.Target == "/item/1")
                {
                    releasedBySecond =
                        await Task.WhenAny(secondWritten.Task, Task.Delay(TimeSpan.FromSeconds(10))) == secondWritten.Task;
                }

                return LoopbackResponse.Ok("item " + request.Target);
            });

            // The first write into the archive can only be the second send's: the first one is still waiting.
            var seam = new SeamStream.Options { OnWritten = _ => secondWritten.TrySetResult() };

            CliReport report = await RunInProcess(seam, CancellationToken.None, "compose", "--url", server.Origin + "/item/#", "--sequence", "1-2", "--parallel", "2", "--save-saz", "--out", Path.Join(directory.Root, "results"));

            Assert.Equal(CliExitCodes.Success, report.ExitCode);
            Assert.True(releasedBySecond);
            using ZipArchive archive = ZipFile.OpenRead(Path.Join(directory.Root, "results", "replays.saz"));
            Assert.Equal(
                new[] { "raw/2_c.txt", "raw/2_m.xml", "raw/2_s.txt", "raw/1_c.txt", "raw/1_m.xml", "raw/1_s.txt" },
                archive.Entries.Select(entry => entry.FullName));
            Assert.EndsWith("item /item/2", Text(archive, "raw/2_s.txt"), StringComparison.Ordinal);
        }

        // The archive is a new file of the staging folder, never one that is there already, and nothing else opens it while
        // the run writes it.
        [Fact]
        public void CreateFile_NeverReplacesAFile_AndHoldsItAlone()
        {
            using var directory = new CliTestDirectory();
            using CliOutputFolder output = CliTestOutput.Reserve(Path.Join(directory.Root, "results"), "run");
            string path = Path.Join(output.StagingPath, "replays.saz");
            using (FileStream first = output.CreateFile("replays.saz"))
            {
                first.WriteByte(1);
                Assert.Throws<IOException>(() => File.OpenRead(path).Dispose());
            }

            Assert.Throws<IOException>(() => output.CreateFile("replays.saz").Dispose());
            Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(path));
            Assert.True(output.TryDiscard());
        }

        [Theory]
        [InlineData("replay")]
        [InlineData("compose")]
        public void SaveSaz_IsASwitchOfReplayAndComposeOnly(string command)
        {
            string[] args = command == "replay"
                ? new[] { "replay", "capture.saz", "--request", "all", "--save-saz", "--out", "results" }
                : new[] { "compose", "--url", "http://127.0.0.1:9/", "--save-saz", "--out", "results" };

            CliParseResult parsed = CliCommandLine.Parse(args);

            Assert.True(parsed.Succeeded);
            Assert.True(parsed.Request.Replay.SaveSaz);
            Assert.False(CliCommandLine.Parse(args.Where(arg => arg != "--save-saz").ToArray()).Request.Replay.SaveSaz);
            foreach (string[] other in new[]
            {
                new[] { "save-request", "capture.saz", "--request", "1", "--save-saz", "--out", "results" },
                new[] { "curl", "capture.saz", "--request", "1", "--save-saz", "--out", "results" },
            })
            {
                Assert.False(CliCommandLine.Parse(other).Succeeded);
            }
        }

        private static async Task<CliReport> RunInProcess(SeamStream.Options seam, CancellationToken cancellationToken, params string[] args)
        {
            CliParseResult parsed = CliCommandLine.Parse(args);
            Assert.True(parsed.Succeeded);
            CliReplayCommand.SazStreamSeam.Value = file =>
            {
                seam.Created = true;
                return new SeamStream(file, seam);
            };
            try
            {
                return await CliReplayCommand.RunAsync(parsed.Request, "run", configureStorage: false, cancellationToken);
            }
            finally
            {
                CliReplayCommand.SazStreamSeam.Value = null;
            }
        }

        private static string[] Flags(ZipArchive archive, long id)
        {
            using Stream metadata = archive.GetEntry($"raw/{id}_m.xml").Open();
            return XDocument.Load(metadata).Root.Element("SessionFlags").Elements("SessionFlag")
                .Select(flag => (string)flag.Attribute("N") + "=" + (string)flag.Attribute("V"))
                .ToArray();
        }

        private static string Text(ZipArchive archive, string name)
        {
            using var reader = new StreamReader(archive.GetEntry(name).Open(), System.Text.Encoding.Latin1);
            return reader.ReadToEnd();
        }

        // The archive's stream as the seam gives it to the writer: it counts what is written, notes the end of a central
        // directory, and fails or cancels at a point of the writing. The file under it is the sink's to close.
        private sealed class SeamStream(Stream inner, SeamStream.Options options) : Stream
        {
            private static readonly byte[] CentralDirectoryHeader = { 0x50, 0x4B, 0x01, 0x02 };
            private static readonly byte[] EndOfCentralDirectory = { 0x50, 0x4B, 0x05, 0x06 };

            internal sealed class Options
            {
                internal Action<long> OnWritten { get; init; }

                internal bool FailCentralDirectory { get; init; }

                internal bool FailFlushAfterEnd { get; init; }

                internal bool Created { get; set; }

                internal long Written { get; set; }

                internal bool SawEnd { get; set; }
            }

            public override bool CanRead => false;

            public override bool CanSeek => inner.CanSeek;

            public override bool CanWrite => true;

            public override long Length => inner.Length;

            public override long Position
            {
                get => inner.Position;
                set => inner.Position = value;
            }

            public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                if (options.FailCentralDirectory && buffer.IndexOf(CentralDirectoryHeader) >= 0)
                {
                    throw new IOException("The test stream refused the central directory.");
                }

                if (buffer.IndexOf(EndOfCentralDirectory) >= 0)
                {
                    options.SawEnd = true;
                }

                inner.Write(buffer);
                options.Written += buffer.Length;
                options.OnWritten?.Invoke(options.Written);
            }

            public override void Flush()
            {
                if (options.FailFlushAfterEnd && options.SawEnd)
                {
                    throw new IOException("The test stream refused a flush.");
                }

                inner.Flush();
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

            public override void SetLength(long value) => inner.SetLength(value);
        }
    }
}
