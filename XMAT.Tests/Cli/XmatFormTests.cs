// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text;
using XMAT.Cli;
using XMAT.Operations;
using XMAT.Operations.Replay;
using XMAT.Tests.Proxy;
using XMAT.Tests.Replay;

namespace XMAT.Tests.Cli
{
    // Multipart forms from xmat's command line (Slice I4, C2-C10), run as child processes against loopback servers in the
    // test process: the exact bytes sent, saved and exported; the refusals that come before anything is reserved, and those
    // that need the request; curl's consent first; and cancellation. Nothing leaves the machine.
    [Collection(IsolatedProxyTests.Name)]
    public sealed class XmatFormTests
    {
        private const string Canary = "XMATFORMCANARY52";
        private const string ContentTypePrefix = "multipart/form-data; boundary=";

        // C2: compose sends the form as its exact bytes, under its own Content-Type. The form is made once for the run:
        // every number of a sequence sends the same bytes under the same random boundary. The output names no part.
        [Fact]
        public async Task Compose_SendsTheFormAsItsExactBytes_MadeOnceForTheRun()
        {
            using var directory = new CliTestDirectory();
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            string file = Path.Join(directory.Root, "report.pdf");
            byte[] bytes = Enumerable.Range(0, 1000).Select(value => (byte)value).ToArray();
            File.WriteAllBytes(file, bytes);

            XmatRun run = await XmatProcess.RunAsync(
                directory.Root, "compose", "--url", server.Origin + "/upload/#", "--sequence", "1-2", "--method", "POST",
                "--allow-unsafe-methods", "--form", $"note={Canary}", "--form", "doc=@" + file, "--form-type", "2=application/pdf",
                "--out", "results");

            CliAssert.ExitCode(0, run);
            Assert.Equal(2, server.Requests.Count);
            string boundary = Boundary(server.Requests[0]);
            Assert.Equal(
                Concat(
                    Ascii($"--{boundary}\r\nContent-Disposition: form-data; name=\"note\"\r\n\r\n{Canary}\r\n"),
                    Ascii($"--{boundary}\r\nContent-Disposition: form-data; name=\"doc\"; filename=\"report.pdf\"\r\n"),
                    Ascii("Content-Type: application/pdf\r\n\r\n"),
                    bytes,
                    Ascii($"\r\n--{boundary}--\r\n")),
                server.Requests[0].Body);
            Assert.Equal(server.Requests[0].Body, server.Requests[1].Body);
            Assert.Equal(boundary, Boundary(server.Requests[1]));
            foreach (string text in new[] { run.Stdout, run.Stderr })
            {
                Assert.DoesNotContain(Canary, text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("report.pdf", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(directory.Name, text, StringComparison.OrdinalIgnoreCase);
            }
        }

        // C3: replay sends the form in place of the captured body, and its Content-Type in place of every captured one; the
        // other headers stay.
        [Fact]
        public async Task Replay_SendsTheFormInPlaceOfTheCapturedBodyAndEveryContentType()
        {
            using var directory = new CliTestDirectory();
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            string input = ReplayCaptures.WriteSaz(
                directory.Root,
                "capture.saz",
                new CapturedExchange(
                    "POST", server.Origin + "/item", "Content-Type: text/plain\r\nX-Kept: 1\r\ncontent-type: other\r\n", "old body"u8.ToArray()));

            XmatRun run = await XmatProcess.RunAsync(
                directory.Root, "replay", input, "--request", "1", "--allow-unsafe-methods", "--form", "a=b", "--out", "results");

            CliAssert.ExitCode(0, run);
            LoopbackRequest received = Assert.Single(server.Requests);
            Assert.Single(received.Headers, header => header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));
            string boundary = Boundary(received);
            Assert.Equal("1", received.Header("X-Kept"));
            Assert.Equal(
                Ascii($"--{boundary}\r\nContent-Disposition: form-data; name=\"a\"\r\n\r\nb\r\n--{boundary}--\r\n"), received.Body);
        }

        // C4: save-request stores the form's bytes and Content-Type only with --include-sensitive; a template keeps neither.
        [Fact]
        public async Task SaveRequest_StoresTheForm_OnlyWithItsContent()
        {
            using var directory = new CliTestDirectory();
            string[] command = { "save-request", "--url", "http://127.0.0.1:9/upload", "--method", "POST", "--form", $"a={Canary}" };

            XmatRun content = await XmatProcess.RunAsync(directory.Root, command.Concat(new[] { "--include-sensitive", "--out", "content" }).ToArray());
            XmatRun template = await XmatProcess.RunAsync(directory.Root, command.Concat(new[] { "--out", "template" }).ToArray());

            CliAssert.ExitCode(0, content);
            CliAssert.ExitCode(0, template);
            RequestFileContent saved = Read(Path.Join(directory.Root, "content", "requests", "00001.json"));
            string contentType = Assert.Single(saved.Headers, header => header.Key == "Content-Type").Value;
            Assert.StartsWith(ContentTypePrefix + "xmat-form-", contentType, StringComparison.Ordinal);
            string boundary = contentType[ContentTypePrefix.Length..];
            Assert.Equal(Ascii($"--{boundary}\r\nContent-Disposition: form-data; name=\"a\"\r\n\r\n{Canary}\r\n--{boundary}--\r\n"), saved.Body);
            string templateFile = File.ReadAllText(Path.Join(directory.Root, "template", "requests", "00001.json"));
            Assert.DoesNotContain(Canary, templateFile, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("xmat-form-", templateFile, StringComparison.Ordinal);
        }

        // C5: curl exports the form's exact bytes as the script's body, under the form's Content-Type.
        [Fact]
        public async Task Curl_ExportsTheFormsExactBytes()
        {
            using var directory = new CliTestDirectory();
            string file = Path.Join(directory.Root, "data.bin");
            File.WriteAllBytes(file, new byte[] { 0, 1, 2, 255 });

            XmatRun run = await XmatProcess.RunAsync(
                directory.Root, "curl", "--url", "http://127.0.0.1:9/upload", "--method", "POST", "--form", "a=b",
                "--form", "f=@" + file, "--include-sensitive", "--out", "out");

            CliAssert.ExitCode(0, run);
            byte[] body = File.ReadAllBytes(Path.Join(directory.Root, "out", "bodies", "00001.bin"));
            string first = Encoding.ASCII.GetString(body, 0, body.AsSpan().IndexOf("\r\n"u8));
            string boundary = first[2..];
            Assert.Matches("^xmat-form-[0-9a-f]{32}$", boundary);
            Assert.Equal(
                Concat(
                    Ascii($"--{boundary}\r\nContent-Disposition: form-data; name=\"a\"\r\n\r\nb\r\n"),
                    Ascii($"--{boundary}\r\nContent-Disposition: form-data; name=\"f\"; filename=\"data.bin\"\r\n"),
                    Ascii("Content-Type: application/octet-stream\r\n\r\n"),
                    new byte[] { 0, 1, 2, 255 },
                    Ascii($"\r\n--{boundary}--\r\n")),
                body);
            Assert.Contains(
                ContentTypePrefix + boundary,
                File.ReadAllText(Path.Join(directory.Root, "out", "scripts", "00001.ps1")),
                StringComparison.Ordinal);
        }

        // C6: a form that cannot be made is refused before anything is reserved or sent, with its own code: a file that is
        // missing or cannot be read, and one too large for what the form leaves, also after an earlier file took its share.
        [Theory]
        [InlineData("missing", MultipartCodes.FileNotFound)]
        [InlineData("folder", MultipartCodes.FileUnreadable)]
        [InlineData("too-large", MultipartCodes.TooLarge)]
        [InlineData("later-file", MultipartCodes.TooLarge)]
        public async Task AFormThatCannotBeMade_SendsAndReservesNothing(string kind, string code)
        {
            using var directory = new CliTestDirectory();
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            string file = Path.Join(directory.Root, "part.bin");
            string earlier = Path.Join(directory.Root, "earlier.bin");
            var form = new List<string>();
            switch (kind)
            {
                case "folder":
                    Directory.CreateDirectory(file);
                    break;
                case "too-large":
                    Sized(file, ReplayBodyFile.MaxBytes);
                    break;
                case "later-file":
                    Sized(earlier, 40L * 1024 * 1024);
                    Sized(file, 30L * 1024 * 1024);
                    form.AddRange(new[] { "--form", "earlier=@" + earlier });
                    break;
            }

            form.AddRange(new[] { "--form", "f=@" + file });

            XmatRun run = await XmatProcess.RunAsync(
                directory.Root,
                new[] { "compose", "--url", server.Origin + "/upload", "--method", "POST", "--allow-unsafe-methods" }
                    .Concat(form).Concat(new[] { "--out", "results" }).ToArray());

            CliAssert.ExitCode(3, run);
            Assert.Equal(new[] { code }, run.ErrorCodes());
            Assert.Equal(0, server.Connections);
            Assert.False(Directory.Exists(Path.Join(directory.Root, "results")));
            CliAssert.NoStagingFolders(directory.Root);
            Assert.DoesNotContain(directory.Name, run.Stdout + run.Stderr, StringComparison.OrdinalIgnoreCase);
        }

        // C6: what is left for a file counts every part's framing, every text and the files read before it: a file one byte
        // over that is refused before it is read, so nothing of its size is allocated.
        [Fact]
        public async Task AFileOverWhatTheFormLeaves_IsRefusedBeforeItIsRead()
        {
            using var directory = new CliTestDirectory();
            string first = Path.Join(directory.Root, "first.bin");
            string second = Path.Join(directory.Root, "second.bin");
            Sized(first, 1024 * 1024);
            var form = new[]
            {
                new CliFormPart("text", new string('t', 1024 * 1024), null, null),
                new CliFormPart("first", null, first, null),
                new CliFormPart("second", null, second, null),
            };
            long left = MultipartForm.MaxBodyBytes - MultipartForm.ClosingLength - 2L * 1024 * 1024 -
                form.Sum(part => MultipartForm.PartFramingLength(part.IsFile, part.Name, part.FileName, part.MediaType));
            Sized(second, left + 1);

            long before = GC.GetTotalAllocatedBytes(precise: true);
            (CliRequestBody body, CliFailure failure) = await CliRequestBody.PrepareAsync(new CliReplay(Form: form), CancellationToken.None);
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

            Assert.Null(body);
            Assert.Equal(MultipartCodes.TooLarge, failure.Code);
            Assert.True(allocated < 16L * 1024 * 1024, $"Refusing the file allocated {allocated} bytes.");
            File.Delete(second);
            Sized(second, left);
            Assert.Equal(MultipartForm.MaxBodyBytes, (await CliRequestBody.PrepareAsync(new CliReplay(Form: form), CancellationToken.None)).Body.Form.Body.LongLength);
        }

        // C6: texts that together leave nothing for the form are refused before any file is opened.
        [Fact]
        public async Task TextsOverTheLimit_AreRefusedBeforeAnyFileIsOpened()
        {
            string value = new('\u4e00', 8 * 1024 * 1024);
            var form = new[]
            {
                new CliFormPart("a", value, null, null),
                new CliFormPart("b", value, null, null),
                new CliFormPart("c", value, null, null),
                new CliFormPart("f", null, Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.bin"), null),
            };

            (CliRequestBody body, CliFailure failure) = await CliRequestBody.PrepareAsync(new CliReplay(Form: form), CancellationToken.None);

            Assert.Null(body);
            Assert.Equal(MultipartCodes.TooLarge, failure.Code);
        }

        // C6: a value that is not well-formed Unicode is refused as invalid-text, data-free, before any file is read. The
        // shared preparation refuses it here; ACommandGivenAValueThatIsNotValidUnicode_ExitsWith3 does from a command.
        [Fact]
        public async Task AValueThatIsNotValidUnicode_IsRefusedBeforeAnyFileIsRead()
        {
            var replay = new CliReplay(Form: new[]
            {
                new CliFormPart("a", "x\ud800" + Canary, null, null),
                new CliFormPart("f", null, Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.bin"), null),
            });

            (CliRequestBody body, CliFailure failure) = await CliRequestBody.PrepareAsync(replay, CancellationToken.None);

            Assert.Null(body);
            Assert.Equal((CliExitCodes.InvalidInput, MultipartCodes.InvalidText), (failure.ExitCode, failure.Code));
            Assert.DoesNotContain(Canary, failure.Message, StringComparison.OrdinalIgnoreCase);
        }

        // C6: a command given a value that is not well-formed Unicode exits with 3 and invalid-text, says nothing of the
        // value, and reserves nothing. Run in process: xmat.exe's .NET host hands Main its arguments through UTF-8, so a
        // lone surrogate passed with CreateProcessW arrives as U+FFFD (observed: x, U+D800, y was saved as 78 EF BF BD 79),
        // but the command must refuse one however it gets one.
        [Fact]
        public async Task ACommandGivenAValueThatIsNotValidUnicode_ExitsWith3_AndReservesNothing()
        {
            using var directory = new CliTestDirectory();
            CliParseResult parsed = CliCommandLine.Parse(new[]
            {
                "compose", "--url", "http://127.0.0.1:9/upload", "--method", "POST", "--allow-unsafe-methods",
                "--form-string", "a=x\ud800" + Canary, "--out", Path.Join(directory.Root, "results"),
            });

            CliReport report = await CliReplayCommand.RunAsync(parsed.Request, "run", CancellationToken.None);

            Assert.Equal(CliExitCodes.InvalidInput, report.ExitCode);
            OperationDiagnostic error = Assert.Single(report.Errors);
            Assert.Equal(MultipartCodes.InvalidText, error.Code);
            Assert.DoesNotContain(Canary, error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(Path.Join(directory.Root, "results")));
            CliAssert.NoStagingFolders(directory.Root);
        }

        // Each --form file is read exactly once, in command-line order, with what the form leaves for it; a --body-file once.
        [Fact]
        public async Task EachFormFile_IsReadExactlyOnce()
        {
            using var directory = new CliTestDirectory();
            string first = Path.Join(directory.Root, "first.bin");
            string second = Path.Join(directory.Root, "second.bin");
            File.WriteAllBytes(first, new byte[] { 1, 2 });
            File.WriteAllBytes(second, new byte[] { 3 });
            var reads = new List<(string Path, long Budget)>();
            Task<ReplayBodyRead> Count(string path, long budget, CancellationToken token)
            {
                reads.Add((path, budget));
                return ReplayBodyFile.ReadAsync(path, budget, token);
            }

            var form = new[]
            {
                new CliFormPart("a", "text", null, null),
                new CliFormPart("f", null, first, null),
                new CliFormPart("g", null, second, null),
                new CliFormPart("h", null, first, null),
            };
            (CliRequestBody body, CliFailure failure) = await CliRequestBody.PrepareAsync(new CliReplay(Form: form), Count, CancellationToken.None);

            Assert.Null(failure);
            Assert.Equal(new[] { first, second, first }, reads.Select(read => read.Path));
            long reserved = MultipartForm.ClosingLength + 4 +
                form.Sum(part => MultipartForm.PartFramingLength(part.IsFile, part.Name, part.FileName, part.MediaType));
            Assert.Equal(
                new[] { MultipartForm.MaxBodyBytes - reserved, MultipartForm.MaxBodyBytes - reserved - 2, MultipartForm.MaxBodyBytes - reserved - 3 },
                reads.Select(read => read.Budget));
            Assert.Equal(MultipartForm.Length(body.Form.Parts), body.Form.Body.LongLength);

            reads.Clear();
            (CliRequestBody file, _) = await CliRequestBody.PrepareAsync(new CliReplay(BodyFile: first), Count, CancellationToken.None);

            Assert.Equal(new[] { (first, ReplayBodyFile.MaxBytes) }, reads);
            Assert.Equal(new byte[] { 1, 2 }, file.Body);
        }

        // C7: curl checks --include-sensitive before it reads any form file: without it, a file that nobody else may open is
        // never opened, and nothing is reserved. With it, the same file cannot be read.
        [Fact]
        public async Task Curl_WithoutConsent_OpensNoFormFile()
        {
            using var directory = new CliTestDirectory();
            string file = Path.Join(directory.Root, "held.bin");
            File.WriteAllBytes(file, new byte[] { 1 });
            string[] command = { "curl", "--url", "http://127.0.0.1:9/upload", "--method", "POST", "--form", "f=@" + file };
            XmatRun denied;
            XmatRun consented;
            using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                denied = await XmatProcess.RunAsync(directory.Root, command.Concat(new[] { "--out", "denied" }).ToArray());
                consented = await XmatProcess.RunAsync(
                    directory.Root, command.Concat(new[] { "--include-sensitive", "--out", "consented" }).ToArray());
            }

            CliAssert.ExitCode(3, denied);
            Assert.Equal(new[] { "replay.curl.consent-required" }, denied.ErrorCodes());
            CliAssert.ExitCode(3, consented);
            Assert.Equal(new[] { MultipartCodes.FileUnreadable }, consented.ErrorCodes());
            Assert.False(Directory.Exists(Path.Join(directory.Root, "denied")));
            CliAssert.NoStagingFolders(directory.Root);
        }

        // C8: a request file that left out its header block takes a form only once --clear-headers supplies the block, for
        // compose, save-request and curl alike; then the form completes the body that the file left out.
        [Fact]
        public async Task ARequestFileWithoutItsHeaders_TakesAFormOnlyOnceTheyAreSupplied()
        {
            using var directory = new CliTestDirectory();
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            string template = Path.Join(directory.Root, "template.json");
            File.WriteAllText(
                template,
                "{ \"schema\": \"xmat-request/1\", \"profile\": \"template\", \"method\": \"POST\", \"url\": null, " +
                    "\"httpVersion\": \"auto\", \"headers\": null, \"body\": null, \"omitted\": [\"url\", \"headers\", \"body\"] }");
            string url = server.Origin + "/upload";
            string[][] commands =
            {
                new[] { "compose", "--request-file", template, "--url", url, "--allow-unsafe-methods", "--form", "a=b" },
                new[] { "save-request", "--request-file", template, "--url", url, "--include-sensitive", "--form", "a=b" },
                new[] { "curl", "--request-file", template, "--url", url, "--include-sensitive", "--form", "a=b" },
            };

            for (int index = 0; index < commands.Length; index++)
            {
                XmatRun refused = await XmatProcess.RunAsync(
                    directory.Root, commands[index].Concat(new[] { "--out", $"refused{index}" }).ToArray());
                XmatRun supplied = await XmatProcess.RunAsync(
                    directory.Root, commands[index].Concat(new[] { "--clear-headers", "--out", $"supplied{index}" }).ToArray());

                CliAssert.ExitCode(3, refused);
                Assert.Equal(new[] { MultipartCodes.HeadersOmitted }, refused.ErrorCodes());
                Assert.False(Directory.Exists(Path.Join(directory.Root, $"refused{index}")));
                CliAssert.ExitCode(0, supplied);
            }

            LoopbackRequest sent = Assert.Single(server.Requests);
            string boundary = Boundary(sent);
            Assert.Equal(Ascii($"--{boundary}\r\nContent-Disposition: form-data; name=\"a\"\r\n\r\nb\r\n--{boundary}--\r\n"), sent.Body);
            RequestFileContent saved = Read(Path.Join(directory.Root, "supplied1", "requests", "00001.json"));
            Assert.StartsWith(ContentTypePrefix, Assert.Single(saved.Headers).Value, StringComparison.Ordinal);
            Assert.Equal((false, false), (saved.Omitted.Headers, saved.Omitted.Body));
        }

        // C9: a request whose Connection header nominates Content-Type, which sending would drop with the boundary, is
        // refused before anything is sent or published, in any case or position; removing Connection sends the form.
        [Theory]
        [InlineData("Connection: keep-alive, Content-Type\r\n")]
        [InlineData("connection: close\r\nConnection: CONTENT-TYPE\r\n")]
        public async Task Replay_AConnectionThatNominatesContentType_RefusesTheForm(string connection)
        {
            using var directory = new CliTestDirectory();
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            string input = ReplayCaptures.WriteSaz(
                directory.Root,
                "capture.saz",
                new CapturedExchange("POST", server.Origin + "/item", connection + "Content-Type: text/plain\r\n", "x"u8.ToArray()));
            string[] command = { "replay", input, "--request", "1", "--allow-unsafe-methods", "--form", "a=b" };

            XmatRun refused = await XmatProcess.RunAsync(directory.Root, command.Concat(new[] { "--out", "refused" }).ToArray());
            XmatRun removed = await XmatProcess.RunAsync(
                directory.Root, command.Concat(new[] { "--remove-header", "Connection", "--out", "removed" }).ToArray());

            CliAssert.ExitCode(3, refused);
            Assert.Equal(new[] { MultipartCodes.ContentTypeDropped }, refused.ErrorCodes());
            Assert.False(Directory.Exists(Path.Join(directory.Root, "refused")));
            CliAssert.ExitCode(0, removed);
            Assert.StartsWith(ContentTypePrefix, Assert.Single(server.Requests).Header("Content-Type"), StringComparison.Ordinal);
        }

        // C10: a run cancelled while its form is made reports cancellation, reserves nothing and reads nothing further.
        [Fact]
        public async Task Cancelled_WhileTheFormIsMade_ReportsCancellation_AndReservesNothing()
        {
            using var directory = new CliTestDirectory();
            string file = Path.Join(directory.Root, "part.bin");
            File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            CliRequest request = CliCommandLine.Parse(new[]
            {
                "compose", "--url", "http://127.0.0.1:9/upload", "--method", "POST", "--allow-unsafe-methods", "--form", "f=@" + file,
                "--out", Path.Join(directory.Root, "results"),
            }).Request;

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CliRequestBody.PrepareAsync(request.Replay, cancellation.Token));
            CliReport report = await CliReplayCommand.RunAsync(request, "run", cancellation.Token);

            Assert.Equal(CliExitCodes.Cancelled, report.ExitCode);
            Assert.False(Directory.Exists(Path.Join(directory.Root, "results")));
            CliAssert.NoStagingFolders(directory.Root);
        }

        private static string Boundary(LoopbackRequest request)
        {
            string contentType = request.Header("Content-Type");
            Assert.StartsWith(ContentTypePrefix + "xmat-form-", contentType, StringComparison.Ordinal);
            return contentType[ContentTypePrefix.Length..];
        }

        private static RequestFileContent Read(string path) => RequestFileReader.Read(File.ReadAllBytes(path)).Content;

        private static void Sized(string path, long length)
        {
            using var stream = new FileStream(path, FileMode.CreateNew);
            stream.SetLength(length);
        }

        private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

        private static byte[] Concat(params byte[][] pieces) => pieces.SelectMany(piece => piece).ToArray();
    }
}
