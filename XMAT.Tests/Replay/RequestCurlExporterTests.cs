// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using XMAT.Operations.Replay;
using XMAT.Tests.HeadlessParity;
using XMAT.WebServiceCapture;

namespace XMAT.Tests.Replay
{
    // The cURL exporter without running anything: the exact curl arguments, every refusal in its order, the command-line
    // budget, the pasteable limit, and the body made once from the draft's own bytes.
    public sealed class RequestCurlExporterTests
    {
        private static readonly string[] Prefix =
        {
            "-q", "--globoff", "--noproxy", "*", "--proto", "=http,https", "--path-as-is", "--silent", "--show-error",
        };

        // Options that a script must never give curl: they would read configuration, send credentials, trust anything,
        // use a proxy, follow redirects or keep cookies.
        private static readonly string[] Denied =
        {
            "-k", "--insecure", "--netrc", "--netrc-file", "--netrc-optional", "-u", "--user", "--ntlm", "--negotiate",
            "--anyauth", "--digest", "--basic", "-E", "--cert", "--key", "-x", "--proxy", "-L", "--location", "-b", "-c",
            "--cookie", "--cookie-jar", "-K", "--config", "-o", "-O", "--output", "--compressed",
        };

        [Fact]
        public void Arguments_AreExact_ForAGetWithHeaders()
        {
            var draft = new RequestReplayDraft
            {
                Method = "GET",
                Url = "https://example.test/a%20b?q=1",
                HeadersText = "X-First: 1\r\nAccept: x/y\r\nX-Empty:\r\nX-Repeat: one\r\nX-Repeat: two",
            };

            CurlRequest request = Prepare(draft);

            Assert.Equal(
                Prefix.Concat(new[]
                {
                    "--request", "GET", "--url", "https://example.test/a%20b?q=1",
                    "--header", "X-First: 1", "--header", "Accept: x/y", "--header", "X-Empty;", "--header", "X-Repeat: one, two",
                    "--header", "Content-Length: 0",
                    "--header", "User-Agent:", "--header", "Content-Type:", "--header", "Expect:",
                }),
                request.Arguments);
            Assert.True(request.Body.IsEmpty);
            Assert.False(request.NeedsUnicodeCurl);
        }

        // Request headers in draft order, then content headers in draft order, as XMAT writes them; HTTP/1.1 only is exact;
        // a body comes from stdin, and curl's own Content-Type is removed only when the request has none.
        [Fact]
        public void Arguments_AreExact_ForAPostWithContentHeaders_OverHttp11()
        {
            var draft = new RequestReplayDraft
            {
                Method = "POST",
                Url = "http://127.0.0.1:9/p",
                HeadersText = "X-First: 1\r\nContent-Type: text/plain\r\nX-Second: 2\r\nContent-Language: en",
                BodyText = "hello",
            };

            CurlRequest request = Prepare(draft, new ReplayTransportOptions(ReplayHttpVersion.Http11));

            Assert.Equal(
                Prefix.Concat(new[]
                {
                    "--http1.1", "--request", "POST", "--url", "http://127.0.0.1:9/p",
                    "--header", "X-First: 1", "--header", "X-Second: 2",
                    "--header", "Content-Type: text/plain", "--header", "Content-Language: en",
                    "--header", "Accept:", "--header", "User-Agent:", "--header", "Expect:",
                    "--data-binary", "@-",
                }),
                request.Arguments);
            Assert.Equal("hello"u8.ToArray(), request.Body.ToArray());
        }

        // An empty body in any form, Base64 whitespace included, leaves HEAD a HEAD with nothing to send.
        [Theory]
        [InlineData(false, "")]
        [InlineData(true, "")]
        [InlineData(true, " \r\n")]
        public void Head_IsHead_WithItsEmptyBody(bool bodyIsBase64, string bodyText)
        {
            CurlRequest request = Prepare(new RequestReplayDraft
            {
                Method = "HEAD", Url = "http://127.0.0.1:9/", BodyIsBase64 = bodyIsBase64, BodyText = bodyText,
            });

            Assert.Equal(
                Prefix.Concat(new[]
                {
                    "--head", "--url", "http://127.0.0.1:9/", "--header", "Content-Length: 0",
                    "--header", "Accept:", "--header", "User-Agent:", "--header", "Content-Type:", "--header", "Expect:",
                }),
                request.Arguments);
            Assert.True(request.Body.IsEmpty);
        }

        [Fact]
        public void NoDeniedOption_IsEverGiven_AndQuietIsFirst()
        {
            foreach (RequestReplayDraft draft in new[]
            {
                new RequestReplayDraft { Method = "GET", Url = "https://e.test/" },
                new RequestReplayDraft { Method = "POST", Url = "https://e.test/", HeadersText = "Authorization: Basic x\r\nCookie: a=1", BodyText = "b" },
                new RequestReplayDraft { Method = "HEAD", Url = "http://e.test/" },
            })
            {
                CurlRequest request = Prepare(draft);

                Assert.Equal("-q", request.Arguments[0]);
                Assert.Empty(request.Arguments.Where((argument, index) => index == 0 || request.Arguments[index - 1] != "--header")
                    .Intersect(Denied, StringComparer.Ordinal));
            }
        }

        [Fact]
        public void APartLeftOut_IsRefusedFirst()
        {
            var draft = RequestReplayDraft.FromRequestFile(new RequestFileContent(
                RequestFileProfile.Template, "GET", null, ReplayHttpVersion.Auto, Array.Empty<KeyValuePair<string, string>>(),
                Array.Empty<byte>(), new RequestOmissions(false, true, false, false)));

            Assert.Throws<RequestIncompleteException>(() => Prepare(draft, new ReplayTransportOptions(ReplayHttpVersion.Http2)));
        }

        // A control header is refused also when Connection names it, which the built request would drop.
        [Theory]
        [InlineData("Fiddler-Host: 192.0.2.1")]
        [InlineData("Connection: X-OverrideHost\r\nX-OverrideHost: e.test")]
        public void AControlHeader_IsRefused_WithTheSharedCode(string headers)
        {
            var draft = new RequestReplayDraft { Method = "GET", Url = "https://e.test/", HeadersText = headers };

            CurlExportException refused = Assert.Throws<CurlExportException>(() => Prepare(draft));

            Assert.Equal(ReplayControlHeaders.Code, refused.Code);
            Assert.Equal(ReplayOperation.ControlHeaderCode, refused.Code);
        }

        [Theory]
        [InlineData("Http2", false, 0, "https://e.test/", "Http2Exact", false, false, false)]
        [InlineData("Auto", true, 0, "https://e.test/", "ConnectTo", false, false, false)]
        [InlineData("Auto", false, 0, "https://e.test/", "Proxy", true, false, false)]
        [InlineData("Auto", false, 0, "https://e.test/", "ClientCertificate", false, true, false)]
        [InlineData("Auto", false, 0, "https://e.test/", "WindowsAuth", false, false, true)]
        [InlineData("Http11", false, 0, "https://e.test/", "WindowsAuth", false, false, true)]
        [InlineData("Auto", false, 1, "https://e.test/", "Redirects", false, false, false)]
        [InlineData("Auto", false, 0, "https://e.test/item/#", "NumberedUrl", false, false, false)]
        public void ATransportThatCurlIsNotKnownToMatch_IsRefused(
            string version, bool connectTo, int redirects, string url, string reason, bool proxy, bool clientCertificate, bool windowsAuth)
        {
            Assert.True(ReplayConnectTo.TryParse("192.0.2.10:443", out ReplayConnectTo target));
            Assert.True(ReplayProxy.TryParse("http://192.0.2.20:3128", out ReplayProxy upstream));
            Assert.True(ReplayClientCertificate.TryParse(new string('A', 40), out ReplayClientCertificate certificate));
            var draft = new RequestReplayDraft { Method = "GET", Url = url };

            CurlExportException refused = Assert.Throws<CurlExportException>(() => RequestCurlExporter.Prepare(
                draft,
                new ReplayTransportOptions(
                    Enum.Parse<ReplayHttpVersion>(version),
                    connectTo ? target : null,
                    proxy ? upstream : null,
                    clientCertificate ? certificate : null,
                    windowsAuth),
                redirects,
                CancellationToken.None));

            Assert.Equal(CurlExportCodes.Unsupported, refused.Code);
            Assert.Equal(Enum.Parse<CurlUnsupported>(reason), refused.Reason);
        }

        // Only the export is refused: the same request still replays with its body.
        [Theory]
        [InlineData("HEAD", false, "payload")]
        [InlineData("HEAD", false, " \r\n")]
        [InlineData("HEAD", true, "AP8=")]
        [InlineData("head", false, "x")]
        public async Task HeadWithABody_IsRefused_AndStillReplays(string method, bool bodyIsBase64, string bodyText)
        {
            var draft = new RequestReplayDraft
            {
                Method = method, Url = "https://e.test/", BodyIsBase64 = bodyIsBase64, BodyText = bodyText,
            };

            Assert.Equal(CurlExportCodes.HeadBody, Assert.Throws<CurlExportException>(() => Prepare(draft)).Code);
            using HttpRequestMessage request = draft.CreateRequest();
            Assert.Equal(
                bodyIsBase64 ? Convert.FromBase64String(bodyText) : Encoding.UTF8.GetBytes(bodyText),
                await request.Content.ReadAsByteArrayAsync());
        }

        // A URL that no request could have is refused by the structural checks, before any request is built.
        [Theory]
        [InlineData("file:///C:/capture")]
        [InlineData("ftp://e.test/")]
        [InlineData("https://user:secret@e.test/")]
        public void AnInvalidUrl_IsRefused_BeforeTheRequestIsBuilt(string url)
        {
            Assert.Throws<FormatException>(() => Prepare(new RequestReplayDraft { Method = "GET", Url = url }));
        }

        // A known method in any case goes out as .NET spells it, as XMAT's sender writes it; another token stays as it is.
        [Theory]
        [InlineData("head", "--head", null)]
        [InlineData("patch", "--request", "PATCH")]
        [InlineData("query", "--request", "QUERY")]
        [InlineData("propfind", "--request", "propfind")]
        public void Methods_AreSpelledAsXmatSendsThem(string method, string option, string value)
        {
            CurlRequest request = Prepare(new RequestReplayDraft { Method = method, Url = "https://e.test/" });
            int index = Prefix.Length;

            Assert.Equal(option, request.Arguments[index]);
            Assert.Equal(value ?? "--url", request.Arguments[index + 1]);
        }

        // A lone surrogate is refused, never replaced; other Unicode is exported and needs curl's Unicode feature.
        [Fact]
        public void IllFormedUnicode_IsRefused_AndOtherUnicodeNeedsAUnicodeCurl()
        {
            var lone = new RequestReplayDraft { Method = "GET", Url = "https://e.test/", HeadersText = "X-A: a\ud800b" };
            var unicode = new RequestReplayDraft { Method = "GET", Url = "https://e.test/", HeadersText = "X-A: caf\u00e9 \U0001F600" };

            Assert.Throws<FormatException>(() => Prepare(lone));
            Assert.True(Prepare(unicode).NeedsUnicodeCurl);
        }

        // The structural limits apply before anything of the body's size is made: a Base64 body over 64 MiB is refused from
        // its text, without decoding it.
        [Fact]
        public void TheLimits_ApplyBeforeTheBodyIsMade()
        {
            var many = new RequestReplayDraft
            {
                Method = "GET", Url = "https://e.test/", HeadersText = string.Join("\r\n", Enumerable.Repeat("A: b", RequestFileLimits.MaxHeaders + 1)),
            };
            var large = new RequestReplayDraft
            {
                Method = "PUT", Url = "https://e.test/", BodyIsBase64 = true,
                BodyText = new string('A', 4 * ((RequestFileLimits.MaxBodyBytes / 3) + 1)),
            };

            Assert.Throws<FormatException>(() => Prepare(many));
            long before = GC.GetAllocatedBytesForCurrentThread();
            Assert.Throws<FormatException>(() => Prepare(large));
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.InRange(allocated, 0, 1024 * 1024);
        }

        // The command-line bound: arguments that take exactly the export budget are exported; two units more (the bound
        // moves in steps of two) are refused before anything is written.
        [Fact]
        public void TheCommandLineBudget_IsExact()
        {
            Assert.Equal(32243, RequestCurlExporter.MaxArgumentsBound);
            RequestReplayDraft At(long bound)
            {
                var probe = new RequestReplayDraft { Method = "GET", Url = "https://e.test/", HeadersText = "X-Big: a" };
                long baseBound = Prepare(probe).CommandLineBound;
                int extra = checked((int)((bound - baseBound) / 2));
                return new RequestReplayDraft { Method = "GET", Url = "https://e.test/", HeadersText = "X-Big: a" + new string('b', extra) };
            }

            CurlRequest fits = Prepare(At(RequestCurlExporter.MaxArgumentsBound));
            CurlExportException over = Assert.Throws<CurlExportException>(() => Prepare(At(RequestCurlExporter.MaxArgumentsBound + 2)));

            Assert.Equal(RequestCurlExporter.MaxArgumentsBound, fits.CommandLineBound);
            Assert.Equal(CurlExportCodes.ArgumentsTooLarge, over.Code);
            Assert.Equal(
                fits.Arguments.Sum(argument => (2L * argument.Length) + 3),
                RequestCurlExporter.BoundOf(fits.Arguments));
        }

        [Fact]
        public void APasteableCommand_HoldsAtMost64KiB()
        {
            CurlRequest fits = Prepare(Bytes(RequestCurlExporter.MaxPasteableBodyBytes));
            CurlRequest over = Prepare(Bytes(RequestCurlExporter.MaxPasteableBodyBytes + 1));
            using var stream = new MemoryStream();

            RequestCurlExporter.Write(fits, CurlScriptForm.Pasteable, null, stream, CancellationToken.None);
            Assert.Throws<ArgumentOutOfRangeException>(
                () => RequestCurlExporter.Write(over, CurlScriptForm.Pasteable, null, new MemoryStream(), CancellationToken.None));
            Assert.Throws<ArgumentException>(
                () => RequestCurlExporter.Write(fits, CurlScriptForm.Pasteable, "../bodies/00001.bin", new MemoryStream(), CancellationToken.None));
        }

        // The body is the draft's own bytes, made once, never copied.
        [Fact]
        public void TheBody_IsTheDraftsOwnBytes()
        {
            byte[] body = { 0, 13, 10, 26, 255 };
            var draft = new RequestReplayDraft { Method = "PUT", Url = "https://e.test/" };
            draft.SetBodyBytes(body);

            CurlRequest request = Prepare(draft);

            Assert.True(MemoryMarshal.TryGetArray(request.Body, out ArraySegment<byte> segment));
            Assert.Same(body, segment.Array);
        }

        // Every single quote that PowerShell takes as a quote, curly ones included, is doubled; nothing else changes.
        [Fact]
        public void Literals_DoubleEveryKindOfSingleQuote()
        {
            Assert.Equal(
                "'a''b\u2018\u2018c\u2019\u2019d\u201a\u201ae\u201b\u201bf\"$(x)`\\'",
                RequestCurlExporter.Literal("a'b\u2018c\u2019d\u201ae\u201bf\"$(x)`\\"));
        }

        // A script's lines other than literals and Base64 are fixed: a request's text never appears outside them.
        [Fact]
        public void RequestText_AppearsOnlyInsideLiterals()
        {
            const string Canary = "CANARY-7f3a";
            var draft = new RequestReplayDraft
            {
                Method = "POST", Url = "https://e.test/" + Canary, HeadersText = "X-Secret: " + Canary, BodyText = Canary,
            };
            CurlRequest request = Prepare(draft);

            foreach (CurlScriptForm form in new[] { CurlScriptForm.File, CurlScriptForm.Pasteable })
            {
                string[] lines = CurlScripts.Text(request, form).Split("\r\n");

                Assert.All(
                    lines.Where(line => line.Contains(Canary, StringComparison.Ordinal)),
                    line => Assert.Matches("^\\s*'[^']*'$", line));
            }
        }

        // Exporting starts nothing. Neither the exporter nor xmat curl uses Process or ProcessStartInfo; the scripts
        // name them only as text, which the user runs.
        [Fact]
        public void Export_StartsNoProcess()
        {
            IReadOnlyList<string> used = ForbiddenCallScanner.MembersUsedBy(
                typeof(RequestCurlExporter).Assembly.Location,
                new[] { typeof(RequestCurlExporter).FullName, typeof(XMAT.Cli.CliCurlCommand).FullName });

            Assert.Contains("XMAT.Operations.Replay.RequestCurlExporter::Prepare", used);
            Assert.Contains("XMAT.Operations.Replay.RequestCurlExporter+ScriptWriter::Line", used);
            Assert.DoesNotContain(used, member => member.StartsWith("System.Diagnostics.Process", StringComparison.Ordinal));
        }

        // The body is made once, after every check: preparing a text or Base64 body allocates about one body, never two.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TheBody_IsMadeOnce_FromTextOrBase64(bool base64)
        {
            const int Length = 8 * 1024 * 1024;
            string text = new('b', Length);
            var draft = new RequestReplayDraft
            {
                Method = "POST",
                Url = "https://e.test/",
                BodyIsBase64 = base64,
                BodyText = base64 ? Convert.ToBase64String(Encoding.ASCII.GetBytes(text)) : text,
            };
            _ = Prepare(draft);

            long before = GC.GetAllocatedBytesForCurrentThread();
            CurlRequest request = Prepare(draft);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(Length, request.Body.Length);
            Assert.InRange(allocated, Length, Length + (Length / 2));
        }

        // A body file is opened once and read at most one byte past its length, into a buffer of exactly that length;
        // no other file API reads it, so a body file of any size costs at most its expected length to refuse.
        [Fact]
        public void TheBodyFile_IsOpenedOnce_AndReadAtMostOneBytePastItsLength()
        {
            string script = CurlScripts.Text(Prepare(Bytes(1000)), CurlScriptForm.File, "../bodies/00001.bin");

            Assert.Equal(
                new[] { "Exists", "Open" },
                Regex.Matches(script, @"\[System\.IO\.File\]::(\w+)").Select(match => match.Groups[1].Value));
            Assert.Contains("$xmatBody = [byte[]]::new(1000)", script, StringComparison.Ordinal);
            Assert.Single(Regex.Matches(script, Regex.Escape("$xmatFile.Read(")));
            Assert.Contains("$xmatCount = $xmatFile.Read($xmatBody, $xmatRead, 1000 - $xmatRead)", script, StringComparison.Ordinal);
            Assert.Single(Regex.Matches(script, Regex.Escape("$xmatFile.ReadByte()")));
            Assert.DoesNotMatch("(?i)ReadAll|Get-Content|OpenRead|CopyTo|FileStream", script);
        }

        // A script owns each curl.exe from the moment it starts it, the version probe included, in one expression that
        // an interruption cannot split (a run stopped between a start and its assignment would otherwise leave curl running),
        // and its one finally stops and reaps every owned curl.exe that still runs. Interruption itself is
        // CurlScriptTests.AnInterruptedRun_LeavesAFailure_AndStopsItsOwnChildOnly.
        [Theory]
        [InlineData("File")]
        [InlineData("Pasteable")]
        public void EveryCurl_IsOwnedFromItsStart_AndStoppedInTheFinally(string form)
        {
            string script = CurlScripts.Text(
                Prepare(new RequestReplayDraft { Method = "GET", Url = "http://127.0.0.1:9/" }), Enum.Parse<CurlScriptForm>(form));
            string cleanup = script[script.LastIndexOf("finally {", StringComparison.Ordinal)..];

            Assert.Equal(
                new[] { "$xmatOwned.Add([System.Diagnostics.Process]::Start($xmatStart))" },
                Regex.Matches(script, @"^.*\[System\.Diagnostics\.Process\]::Start\(.*$", RegexOptions.Multiline)
                    .Select(match => match.Value.Trim()));
            Assert.Equal(2, Regex.Matches(script, Regex.Escape("= Start-XmatCurl ")).Count);
            Assert.Contains("foreach ($xmatOwnedProcess in $xmatOwned) {", cleanup, StringComparison.Ordinal);
            Assert.Contains("try { $xmatOwnedProcess.Kill() } catch { }", cleanup, StringComparison.Ordinal);
            Assert.Contains("$xmatOwnedProcess.WaitForExit()", cleanup, StringComparison.Ordinal);
        }

        // Writing stops between lines once cancelled: a script of many lines ends at the next line after the token is
        // cancelled, long before all of it is written.
        [Fact]
        public void Writing_StopsBetweenLines_WhenCancelled()
        {
            CurlRequest request = Prepare(Bytes(4 * 1024 * 1024));
            using var complete = new MemoryStream();
            RequestCurlExporter.Write(request, CurlScriptForm.File, null, complete, CancellationToken.None);
            using var cancellation = new CancellationTokenSource();
            using var partial = new CancellingStream(cancellation);

            Assert.ThrowsAny<OperationCanceledException>(
                () => RequestCurlExporter.Write(request, CurlScriptForm.File, null, partial, cancellation.Token));
            Assert.InRange(partial.Length, 1, complete.Length / 4);
        }

        private static CurlRequest Prepare(RequestReplayDraft draft, ReplayTransportOptions transport = null) =>
            RequestCurlExporter.Prepare(draft, transport ?? new ReplayTransportOptions(), 0, CancellationToken.None);

        private static RequestReplayDraft Bytes(int length)
        {
            var draft = new RequestReplayDraft { Method = "POST", Url = "https://e.test/" };
            draft.SetBodyBytes(Enumerable.Range(0, length).Select(index => (byte)index).ToArray());
            return draft;
        }

        // Cancels its token when the first bytes reach it.
        private sealed class CancellingStream : MemoryStream
        {
            private readonly CancellationTokenSource _cancellation;

            internal CancellingStream(CancellationTokenSource cancellation) => _cancellation = cancellation;

            public override void Write(byte[] buffer, int offset, int count)
            {
                base.Write(buffer, offset, count);
                _cancellation.Cancel();
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                base.Write(buffer);
                _cancellation.Cancel();
            }
        }
    }
}
