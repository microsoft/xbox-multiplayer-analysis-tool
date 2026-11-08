// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using XMAT.Operations.Replay;
using XMAT.WebServiceCapture;
using Xunit.Abstractions;

namespace XMAT.Tests.Replay
{
    // Generated cURL scripts run by PowerShell 7, against loopback listeners and xmat-echo.exe only: what reaches the
    // wire matches XMAT's sender (T1); literals and arguments arrive unchanged (T2); a body file must be the exported body
    // (T5); each child gets only the allowlisted environment and a child that is still running when the script fails is
    // stopped and reaped (T6); and the runtime is recorded (T9).
    public sealed class CurlScriptTests : IDisposable
    {
        // Quotes of every kind, PowerShell's own syntax, shell metacharacters, Unicode, and runs of backslashes before a
        // quote and at the end.
        private const string Hostile =
            "q'\u2018\u2019\u201a\u201b\"\u201c\u201d\u201e $(Get-Date) `n ; & | < > %PATH% ^ ! caf\u00e9 \u65e5\u672c " +
            "\U0001F600 a\\b \\\\\" c\\\\";

        private static readonly string[] Poison =
        {
            "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "CURL_CA_BUNDLE", "SSL_CERT_FILE", "SSLKEYLOGFILE",
            "CURL_HOME", "HOME", "XMAT_POISON",
        };

        private readonly ITestOutputHelper _output;
        private readonly string _folder;

        public CurlScriptTests(ITestOutputHelper output)
        {
            _output = output;
            _folder = Path.Join(Path.GetTempPath(), "xmat-curl-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
        }

        public static TheoryData<string> WireCases => new()
        {
            "get", "head", "post-content", "binary", "empty-post", "empty-get-content-type", "patch", "lower-method",
            "custom-method", "repeated", "empty-value", "dash-name", "unusual-path", "ipv6", "http11", "large-body",
        };

        public void Dispose()
        {
            try
            {
                Directory.Delete(_folder, recursive: true);
            }
            catch (IOException)
            {
                // A child that a failed test left running may hold a file; the temporary folder goes later.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        // T1: the same draft through XMAT's sender and through each generated form (a script holding its body, a script
        // with a body file, a pasteable command): the listener sees the same request line, header lines in the same order,
        // and body bytes, an empty value's whitespace aside.
        [CurlScriptTheory]
        [MemberData(nameof(WireCases))]
        public async Task GeneratedScripts_SendWhatXmatSends(string name)
        {
            (bool ipv6, ReplayHttpVersion version, Func<string, RequestReplayDraft> make) = Case(name);
            using var server = new RawLoopback(ipv6);
            RequestReplayDraft draft = make(server.Origin);
            Task<ReceivedRequest> sent = server.AcceptAsync();
            using (var sender = new ReplaySender())
            {
                _ = await sender.SendAsync(
                    draft,
                    new ReplaySendOptions(TimeSpan.FromSeconds(30), ReplaySendOptions.DefaultMaxResponseBytes, 0, version),
                    CancellationToken.None);
            }

            ReceivedRequest xmat = await sent;
            CurlRequest request = RequestCurlExporter.Prepare(draft, new ReplayTransportOptions(version), 0, CancellationToken.None);
            var scripts = new List<(string Form, string Path)>
            {
                ("file", CurlScripts.WriteFile(_folder, name + "\\inline.ps1", request, CurlScriptForm.File)),
            };
            if (!request.Body.IsEmpty)
            {
                string body = Path.Join(_folder, name, "bodies", "00001.bin");
                Directory.CreateDirectory(Path.GetDirectoryName(body));
                File.WriteAllBytes(body, request.Body.ToArray());
                scripts.Add(("sidecar", CurlScripts.WriteFile(
                    _folder, name + "\\scripts\\00001.ps1", request, CurlScriptForm.File, "../bodies/00001.bin")));
            }

            if (request.Body.Length <= RequestCurlExporter.MaxPasteableBodyBytes)
            {
                scripts.Add(("pasteable", CurlScripts.WritePasteableHost(_folder, name + "\\pasteable.ps1", request)));
            }

            foreach ((string form, string script) in scripts)
            {
                Task<ReceivedRequest> received = server.AcceptAsync();
                ScriptRun run = await CurlScripts.RunAsync(script, _folder);
                ReceivedRequest curl = await received;

                _output.WriteLine($"{name} {form}: {run}");
                Assert.True(run.ExitCode == 0, $"{form}: {run}");
                Assert.Equal(xmat.Lines, curl.Lines);
                Assert.Equal(xmat.Body, curl.Body);
                if (form == "pasteable")
                {
                    Assert.Contains("xmat-host-alive 0", run.Output, StringComparison.Ordinal);
                }
            }
        }

        // T2: hostile text reaches curl.exe as the exact arguments, as .NET and the Windows command-line parser read them
        // (xmat-echo.exe), from both forms; and curl.exe puts the header value on the wire as its UTF-8 bytes.
        [CurlScriptFact]
        public async Task HostileText_ArrivesUnchanged()
        {
            var draft = new RequestReplayDraft
            {
                Method = "POST",
                Url = "http://127.0.0.1:9/it's/%E2%80%98q%E2%80%99?a=$(x)&b='c'",
                HeadersText = "X-Hostile: " + Hostile + "\r\nX-Tail: ends\\\\",
                BodyText = Hostile,
            };
            CurlRequest request = RequestCurlExporter.Prepare(draft, new ReplayTransportOptions(), 0, CancellationToken.None);
            Assert.True(request.NeedsUnicodeCurl);
            var hooks = new CurlScriptTestHooks(CurlScripts.EchoHelper);

            foreach ((string form, string script) in new[]
            {
                ("file", CurlScripts.WriteFile(_folder, "hostile\\file.ps1", request, CurlScriptForm.File, null, hooks)),
                ("pasteable", CurlScripts.WritePasteableHost(_folder, "hostile\\pasteable.ps1", request, hooks)),
            })
            {
                string record = CurlScripts.ConfigureEcho(_folder);
                File.Delete(record);
                ScriptRun run = await CurlScripts.RunAsync(script, _folder);
                EchoRun send = Assert.Single(CurlScripts.ReadEcho(record), echo => echo.Kind == "send");

                Assert.True(run.ExitCode == 0, run.ToString());
                if (form == "pasteable")
                {
                    Assert.Contains("xmat-host-alive 0", run.Output, StringComparison.Ordinal);
                }

                Assert.Equal(request.Arguments, send.Arguments);
                Assert.Equal(request.Arguments, send.NativeArguments);
                Assert.Equal(request.Body.Length, send.StdinBytes);
                Assert.Equal(Convert.ToHexString(SHA256.HashData(request.Body.Span)), send.StdinSha256);
            }

            using var server = new RawLoopback();
            var wire = new RequestReplayDraft { Method = "POST", Url = server.Origin + "/x", HeadersText = "X-Hostile: " + Hostile, BodyText = Hostile };
            CurlRequest wireRequest = RequestCurlExporter.Prepare(wire, new ReplayTransportOptions(), 0, CancellationToken.None);
            Task<ReceivedRequest> received = server.AcceptAsync();
            ScriptRun sent = await CurlScripts.RunAsync(CurlScripts.WriteFile(_folder, "hostile\\wire.ps1", wireRequest, CurlScriptForm.File), _folder);
            ReceivedRequest curl = await received;

            Assert.True(sent.ExitCode == 0, sent.ToString());
            Assert.Equal(Encoding.UTF8.GetBytes(Hostile), curl.FieldValueBytes("X-Hostile"));
            Assert.Equal(Encoding.UTF8.GetBytes(Hostile), curl.Body);
        }

        // T2: arguments that take exactly the export budget reach the child unchanged: the command line fits.
        [CurlScriptFact]
        public async Task ArgumentsAtTheBudget_Fit()
        {
            CurlRequest request = AtBudget(RequestCurlExporter.MaxArgumentsBound, "\"\\");
            string record = CurlScripts.ConfigureEcho(_folder);

            ScriptRun run = await CurlScripts.RunAsync(
                CurlScripts.WriteFile(_folder, "budget.ps1", request, CurlScriptForm.File, null, new CurlScriptTestHooks(CurlScripts.EchoHelper)),
                _folder);
            EchoRun send = Assert.Single(CurlScripts.ReadEcho(record), echo => echo.Kind == "send");

            Assert.True(run.ExitCode == 0, run.ToString());
            Assert.Equal(request.Arguments, send.Arguments);
            Assert.Equal(request.Arguments, send.NativeArguments);
            Assert.True(send.CommandLine.Length <= RequestCurlExporter.MaxCommandLine);
        }

        // T6: the script checks the budget again with the curl.exe path that it runs, before it starts anything.
        [CurlScriptFact]
        public async Task ALongerCurlPath_IsRefusedBeforeAnythingStarts()
        {
            string deep = Path.Join(_folder, string.Join("\\", Enumerable.Repeat(new string('d', 60), 7)));
            string fake = @"\\?\" + Path.Join(deep, "xmat-echo.exe");
            Directory.CreateDirectory(@"\\?\" + deep);
            File.WriteAllBytes(fake, Array.Empty<byte>());
            string record = CurlScripts.ConfigureEcho(_folder);
            CurlRequest request = AtBudget(RequestCurlExporter.MaxArgumentsBound, "x");

            ScriptRun run = await CurlScripts.RunAsync(
                CurlScripts.WriteFile(_folder, "long-path.ps1", request, CurlScriptForm.File, null, new CurlScriptTestHooks(fake)),
                _folder);

            Assert.True(fake.Length > RequestCurlExporter.ReservedExecutablePath);
            Assert.Equal(1, run.ExitCode);
            Assert.Contains("too large for a Windows command line", run.Error, StringComparison.Ordinal);
            Assert.Empty(CurlScripts.ReadEcho(record));
        }

        // T5: the body file must be the exported body: changed, truncated, extended or missing, the script starts nothing.
        [CurlScriptTheory]
        [InlineData("changed")]
        [InlineData("truncated")]
        [InlineData("extended")]
        [InlineData("missing")]
        public async Task AChangedBodyFile_SendsNothing(string damage)
        {
            byte[] body = Enumerable.Range(0, 1000).Select(index => (byte)(index * 7)).ToArray();
            var draft = new RequestReplayDraft { Method = "PUT", Url = "http://127.0.0.1:9/" };
            draft.SetBodyBytes(body);
            CurlRequest request = RequestCurlExporter.Prepare(draft, new ReplayTransportOptions(), 0, CancellationToken.None);
            string file = Path.Join(_folder, "out", "bodies", "00001.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            byte[] stored = damage switch
            {
                "changed" => body.Select((value, index) => index == 500 ? (byte)(value ^ 1) : value).ToArray(),
                "truncated" => body[..^1],
                "extended" => body.Append((byte)0).ToArray(),
                _ => null,
            };
            if (stored != null)
            {
                File.WriteAllBytes(file, stored);
            }

            string record = CurlScripts.ConfigureEcho(_folder);
            ScriptRun run = await CurlScripts.RunAsync(
                CurlScripts.WriteFile(_folder, "out\\scripts\\00001.ps1", request, CurlScriptForm.File, "../bodies/00001.bin", new CurlScriptTestHooks(CurlScripts.EchoHelper)),
                _folder);

            Assert.Equal(1, run.ExitCode);
            Assert.Empty(CurlScripts.ReadEcho(record));
        }

        // T6: both children get only the allowlisted environment, whatever the script's own holds, and the script's
        // environment is unchanged afterwards; for a script file and a pasteable command alike.
        [CurlScriptFact]
        public async Task Children_GetOnlyTheAllowlistedEnvironment()
        {
            var draft = new RequestReplayDraft { Method = "POST", Url = "http://127.0.0.1:9/", HeadersText = "X-A: caf\u00e9", BodyText = "b" };
            CurlRequest request = RequestCurlExporter.Prepare(draft, new ReplayTransportOptions(), 0, CancellationToken.None);
            var hooks = new CurlScriptTestHooks(CurlScripts.EchoHelper);
            string file = CurlScripts.WriteFile(_folder, "env\\file.ps1", request, CurlScriptForm.File, null, hooks);
            string pasteable = CurlScripts.WritePasteableHost(_folder, "env\\pasteable.ps1", request, hooks);

            foreach (string script in new[] { file, pasteable })
            {
                string wrapper = Path.Join(_folder, "env", "wrapper-" + Path.GetFileName(script));
                File.WriteAllText(
                    wrapper,
                    "$xmatBefore = (Get-ChildItem env: | Sort-Object Name | ForEach-Object { $_.Name + '=' + $_.Value }) -join \"`n\"\r\n" +
                    "& " + RequestCurlExporter.Literal(script) + "\r\n" +
                    "$xmatAfter = (Get-ChildItem env: | Sort-Object Name | ForEach-Object { $_.Name + '=' + $_.Value }) -join \"`n\"\r\n" +
                    "[Console]::Out.WriteLine('xmat-env-same ' + ($xmatBefore -ceq $xmatAfter))\r\n",
                    new UTF8Encoding(true));
                string record = CurlScripts.ConfigureEcho(_folder);
                File.Delete(record);

                ScriptRun run = await CurlScripts.RunAsync(wrapper, _folder, environment =>
                {
                    foreach (string name in Poison)
                    {
                        environment[name] = "xmat-poison";
                    }
                });
                IReadOnlyList<EchoRun> runs = CurlScripts.ReadEcho(record);

                Assert.Contains("xmat-env-same True", run.Output, StringComparison.Ordinal);
                Assert.Equal(new[] { "probe", "send" }, runs.Select(echo => echo.Kind));
                foreach (EchoRun child in runs)
                {
                    Assert.Empty(child.Environment.Keys.Except(RequestCurlExporter.ChildEnvironment, StringComparer.OrdinalIgnoreCase));
                    Assert.Equal(Environment.GetEnvironmentVariable("SystemRoot"), child.Environment["SystemRoot"]);
                }
            }
        }

        // T6: when the script fails while its own child still runs (the child closed its stdin, so writing the body
        // fails), it stops and reaps that child, and only that one: an unrelated process keeps running.
        [CurlScriptFact]
        public async Task AFailingScript_StopsItsOwnChildOnly()
        {
            using Process decoy = StartDecoy(Path.Join(_folder, "decoy"));
            string record = CurlScripts.ConfigureEcho(_folder, mode: "hold");
            try
            {
                var draft = new RequestReplayDraft { Method = "PUT", Url = "http://127.0.0.1:9/" };
                draft.SetBodyBytes(RandomNumberGenerator.GetBytes(8 * 1024 * 1024));
                CurlRequest request = RequestCurlExporter.Prepare(draft, new ReplayTransportOptions(), 0, CancellationToken.None);

                ScriptRun run = await CurlScripts.RunAsync(
                    CurlScripts.WriteFile(_folder, "hold.ps1", request, CurlScriptForm.File, null, new CurlScriptTestHooks(CurlScripts.EchoHelper)),
                    _folder);
                EchoRun send = Assert.Single(CurlScripts.ReadEcho(record), echo => echo.Kind == "send");

                Assert.Equal(1, run.ExitCode);
                Assert.False(CurlScripts.IsAlive(send), "The script's own child is still running.");
                Assert.False(decoy.HasExited, "An unrelated process was stopped.");
            }
            finally
            {
                try
                {
                    CurlScripts.StopRecorded(record);
                }
                finally
                {
                    Stop(decoy);
                }
            }
        }

        // T6: a run that is interrupted while its own curl.exe still runs (as Ctrl+C stops it: the pipeline stops, its host
        // keeps running) leaves a failure, never an earlier result such as 0, and stops and reaps that curl.exe only. The
        // pipeline runs in a runspace of its own, which is queried afterwards; for a pasteable command and a script file.
        [CurlScriptTheory]
        [InlineData("pasteable")]
        [InlineData("file")]
        public async Task AnInterruptedRun_LeavesAFailure_AndStopsItsOwnChildOnly(string form)
        {
            using Process decoy = StartDecoy(Path.Join(_folder, "decoy"));
            string record = CurlScripts.ConfigureEcho(_folder, mode: "hold");
            try
            {
                CurlRequest request = RequestCurlExporter.Prepare(
                    new RequestReplayDraft { Method = "GET", Url = "http://127.0.0.1:9/" }, new ReplayTransportOptions(), 0, CancellationToken.None);
                var hooks = new CurlScriptTestHooks(CurlScripts.EchoHelper);
                string command;
                if (form == "pasteable")
                {
                    string text = Path.Join(_folder, "interrupted.txt");
                    File.WriteAllText(text, CurlScripts.Text(request, CurlScriptForm.Pasteable, null, hooks), new UTF8Encoding(false));
                    command = "[System.IO.File]::ReadAllText(" + RequestCurlExporter.Literal(text) + ")";
                }
                else
                {
                    string file = CurlScripts.WriteFile(_folder, "interrupted.ps1", request, CurlScriptForm.File, null, hooks);
                    command = RequestCurlExporter.Literal("& " + RequestCurlExporter.Literal(file));
                }

                string driver = Path.Join(_folder, "interrupt-" + form + ".ps1");
                File.WriteAllText(driver, InterruptingDriver(record, command), new UTF8Encoding(true));
                ScriptRun run = await CurlScripts.RunAsync(driver, _folder);
                EchoRun send = Assert.Single(CurlScripts.ReadEcho(record), echo => echo.Kind == "send");

                Assert.True(run.ExitCode == 0, run.ToString());
                Assert.True(run.Output.Contains("xmat-interrupted Stopped 1", StringComparison.Ordinal), run.ToString());
                Assert.False(CurlScripts.IsAlive(send), "The script's own child is still running.");
                Assert.False(decoy.HasExited, "An unrelated process was stopped.");
            }
            finally
            {
                try
                {
                    CurlScripts.StopRecorded(record);
                }
                finally
                {
                    Stop(decoy);
                }
            }
        }

        // The echo helper records which variables a child got but not what they held, apart from the system folders: a
        // secret in the environment of whoever runs the tests never reaches a record, while the names that
        // Children_GetOnlyTheAllowlistedEnvironment checks still do.
        [Fact]
        public async Task TheEchoHelper_RecordsNoSecretFromItsEnvironment()
        {
            const string Canary = "xmat-secret-canary-58e1";
            string record = CurlScripts.ConfigureEcho(_folder);
            var start = new ProcessStartInfo(CurlScripts.EchoHelper, "send")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                WorkingDirectory = _folder,
            };
            start.Environment["XMAT_SECRET_CANARY"] = Canary;
            using (Process helper = Process.Start(start))
            {
                helper.StandardInput.Close();
                await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            }

            EchoRun run = Assert.Single(CurlScripts.ReadEcho(record));
            Assert.DoesNotContain(Canary, File.ReadAllText(record), StringComparison.Ordinal);
            Assert.True(run.Environment.ContainsKey("XMAT_SECRET_CANARY"));
            Assert.Null(run.Environment["XMAT_SECRET_CANARY"]);
            Assert.Equal(Environment.GetEnvironmentVariable("SystemRoot"), run.Environment["SystemRoot"]);
        }

        // A record names a process by its id and creation time. A record that gives a running xmat-echo.exe's id with
        // another creation time, as the record of an earlier process whose id Windows gave to this one would, stops
        // nothing and does not count it as running; the record that this process wrote itself stops it.
        [Fact]
        public async Task StopRecorded_StopsOnlyTheProcessThatItsRecordNames()
        {
            string folder = Path.Join(_folder, "decoy");
            using Process decoy = StartDecoy(folder);
            try
            {
                string record = CurlScripts.RecordOf(folder);
                EchoRun own = await WaitForRecordAsync(record);
                JsonObject line = JsonNode.Parse(File.ReadAllLines(record)[0]).AsObject();
                line["created"] = own.Created - 1;
                string stale = Path.Join(_folder, "stale-record.jsonl");
                File.WriteAllText(stale, line.ToJsonString() + "\n");
                EchoRun other = Assert.Single(CurlScripts.ReadEcho(stale));

                CurlScripts.StopRecorded(stale);

                Assert.Equal(decoy.Id, own.ProcessId);
                Assert.False(decoy.WaitForExit(500), "A record of another process with this id stopped it.");
                Assert.True(CurlScripts.IsAlive(own));
                Assert.False(CurlScripts.IsAlive(other));

                CurlScripts.StopRecorded(record);

                Assert.True(decoy.WaitForExit(30_000), "The record that this process wrote did not stop it.");
                Assert.False(CurlScripts.IsAlive(own));
            }
            finally
            {
                Stop(decoy);
            }
        }

        // A recorded process that has not exited once it was stopped is an error that names it, raised only after every
        // record has been tried, so such a process leaves none of the others running.
        [Fact]
        public async Task StopRecorded_TriesEveryRecord_ThenThrowsForOneThatDidNotExit()
        {
            using Process first = StartDecoy(Path.Join(_folder, "first"));
            using Process second = StartDecoy(Path.Join(_folder, "second"));
            try
            {
                EchoRun firstRun = await WaitForRecordAsync(CurlScripts.RecordOf(Path.Join(_folder, "first")));
                EchoRun secondRun = await WaitForRecordAsync(CurlScripts.RecordOf(Path.Join(_folder, "second")));
                string records = Path.Join(_folder, "records.jsonl");
                File.WriteAllLines(
                    records,
                    new[] { "first", "second" }.Select(name => File.ReadAllLines(CurlScripts.RecordOf(Path.Join(_folder, name)))[0]));
                int calls = 0;

                InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => CurlScripts.StopRecorded(
                    records,
                    (_, _) =>
                    {
                        // The first stop does nothing; the second stops its process.
                        if (++calls == 2)
                        {
                            second.Kill();
                            second.WaitForExit();
                        }

                        return true;
                    },
                    100));

                Assert.Equal(2, calls);
                Assert.Contains($"process {first.Id}:", error.Message, StringComparison.Ordinal);
                Assert.DoesNotContain($"process {second.Id}:", error.Message, StringComparison.Ordinal);
                Assert.True(CurlScripts.IsAlive(firstRun));
                Assert.False(CurlScripts.IsAlive(secondRun));
            }
            finally
            {
                try
                {
                    Stop(first);
                }
                finally
                {
                    Stop(second);
                }
            }
        }

        // T6: a server that closes the connection at once is curl's own failure: the script's result is curl's nonzero
        // exit code, and a pasteable command leaves it in $LASTEXITCODE with its host still running.
        [CurlScriptFact]
        public async Task CurlsOwnFailure_IsTheResult()
        {
            using var server = new RawLoopback();
            var draft = new RequestReplayDraft { Method = "GET", Url = server.Origin + "/" };
            CurlRequest request = RequestCurlExporter.Prepare(draft, new ReplayTransportOptions(), 0, CancellationToken.None);

            Task<ReceivedRequest> closed = server.AcceptAsync(closeAtOnce: true);
            ScriptRun file = await CurlScripts.RunAsync(CurlScripts.WriteFile(_folder, "closed.ps1", request, CurlScriptForm.File), _folder);
            await closed;
            closed = server.AcceptAsync(closeAtOnce: true);
            ScriptRun pasteable = await CurlScripts.RunAsync(CurlScripts.WritePasteableHost(_folder, "closed-host.ps1", request), _folder);
            await closed;

            Assert.NotEqual(0, file.ExitCode);
            Assert.NotEqual(1, file.ExitCode);
            Match alive = Regex.Match(pasteable.Output, "xmat-host-alive (\\d+)");
            Assert.True(alive.Success, pasteable.ToString());
            int code = int.Parse(alive.Groups[1].Value, CultureInfo.InvariantCulture);
            Assert.NotEqual(0, code);
            Assert.NotEqual(1, code);
        }

        // T6: a curl.exe older than the floor, or one without the Unicode feature for a non-ASCII value, gets no request:
        // the script fails after the probe alone, and a pasteable command leaves 1 with its host running.
        [CurlScriptTheory]
        [InlineData("8.20.9", true)]
        [InlineData("8.21.0", false)]
        public async Task AnUnsupportedCurl_SendsNothing(string version, bool unicode)
        {
            var draft = new RequestReplayDraft { Method = "GET", Url = "http://127.0.0.1:9/", HeadersText = "X-A: caf\u00e9" };
            CurlRequest request = RequestCurlExporter.Prepare(draft, new ReplayTransportOptions(), 0, CancellationToken.None);
            var hooks = new CurlScriptTestHooks(CurlScripts.EchoHelper);

            string record = CurlScripts.ConfigureEcho(_folder, version, unicode);
            ScriptRun file = await CurlScripts.RunAsync(CurlScripts.WriteFile(_folder, "old.ps1", request, CurlScriptForm.File, null, hooks), _folder);
            ScriptRun pasteable = await CurlScripts.RunAsync(CurlScripts.WritePasteableHost(_folder, "old-host.ps1", request, hooks), _folder);

            Assert.Equal(1, file.ExitCode);
            Assert.Contains("xmat-host-alive 1", pasteable.Output, StringComparison.Ordinal);
            Assert.Equal(new[] { "probe", "probe" }, CurlScripts.ReadEcho(record).Select(echo => echo.Kind));
        }

        // G11b: only a value that is not ASCII needs curl's Unicode feature: an ASCII request runs with a curl.exe without
        // it, from both forms.
        [CurlScriptFact]
        public async Task AnAsciiRequest_RunsWithACurlWithoutUnicode()
        {
            var draft = new RequestReplayDraft { Method = "GET", Url = "http://127.0.0.1:9/", HeadersText = "X-A: cafe" };
            CurlRequest request = RequestCurlExporter.Prepare(draft, new ReplayTransportOptions(), 0, CancellationToken.None);
            var hooks = new CurlScriptTestHooks(CurlScripts.EchoHelper);

            string record = CurlScripts.ConfigureEcho(_folder, unicode: false);
            ScriptRun file = await CurlScripts.RunAsync(CurlScripts.WriteFile(_folder, "ascii.ps1", request, CurlScriptForm.File, null, hooks), _folder);
            ScriptRun pasteable = await CurlScripts.RunAsync(CurlScripts.WritePasteableHost(_folder, "ascii-host.ps1", request, hooks), _folder);

            Assert.False(request.NeedsUnicodeCurl);
            Assert.True(file.ExitCode == 0, file.ToString());
            Assert.Contains("xmat-host-alive 0", pasteable.Output, StringComparison.Ordinal);
            Assert.Equal(new[] { "probe", "send", "probe", "send" }, CurlScripts.ReadEcho(record).Select(echo => echo.Kind));
        }

        // T6: a poisoned environment changes nothing that curl does. With CURL_CA_BUNDLE and SSL_CERT_FILE naming the root
        // that issued the server's certificate, it stays untrusted; HTTPS_PROXY and ALL_PROXY get no connection; and
        // SSLKEYLOGFILE is never written. The inbox curl.exe (Schannel) was observed to ignore both CA variables anyway, so
        // Children_GetOnlyTheAllowlistedEnvironment is the test that the isolation itself would fail; this one pins the
        // outcomes, and that the request still goes straight to the server.
        [CurlScriptFact]
        public async Task APoisonedEnvironment_ChangesNothingThatCurlDoes()
        {
            using var authority = new TestCertificateAuthority();
            using X509Certificate2 certificate = authority.Issue(address: IPAddress.Loopback);
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok("xmat-tls-reached"), new LoopbackTls(certificate));
            using var proxy = new RawLoopback();
            string bundle = Path.Join(_folder, "root.pem");
            File.WriteAllText(bundle, authority.Root.ExportCertificatePem());
            string keyLog = Path.Join(_folder, "keys.log");
            CurlRequest request = RequestCurlExporter.Prepare(
                new RequestReplayDraft { Method = "GET", Url = server.Origin + "/" }, new ReplayTransportOptions(), 0, CancellationToken.None);
            void Poison(IDictionary<string, string> environment)
            {
                environment["CURL_CA_BUNDLE"] = bundle;
                environment["SSL_CERT_FILE"] = bundle;
                environment["HTTPS_PROXY"] = proxy.Origin;
                environment["ALL_PROXY"] = proxy.Origin;

                // An inherited exemption, such as for loopback, would let a leaked proxy go unused; Windows names are
                // case-insensitive, so this replaces no_proxy too.
                environment["NO_PROXY"] = "xmat-no-match.invalid";
                environment["SSLKEYLOGFILE"] = keyLog;
            }

            ScriptRun file = await CurlScripts.RunAsync(CurlScripts.WriteFile(_folder, "poisoned.ps1", request, CurlScriptForm.File), _folder, Poison);
            int afterFile = server.Connections;
            ScriptRun pasteable = await CurlScripts.RunAsync(CurlScripts.WritePasteableHost(_folder, "poisoned-host.ps1", request), _folder, Poison);
            Match alive = Regex.Match(pasteable.Output, "xmat-host-alive (\\d+)");

            Assert.True(file.ExitCode is 35 or 60, file.ToString());
            Assert.True(alive.Success && alive.Groups[1].Value is "35" or "60", pasteable.ToString());
            Assert.DoesNotContain("xmat-tls-reached", file.Output + pasteable.Output, StringComparison.Ordinal);
            Assert.True(afterFile >= 1 && server.Connections > afterFile, "The request did not go straight to the server.");
            Assert.False(proxy.HasPendingConnection, "A proxy from the environment was used.");
            Assert.False(File.Exists(keyLog), "A key log from the environment was written.");
        }

        // U5: curl's own output is the script's: the response body reaches standard output and curl's error message
        // standard error, from a script file and a pasteable command alike.
        [CurlScriptFact]
        public async Task CurlsOutput_IsTheScriptsOutput()
        {
            const string Canary = "xmat-response-7c1d";
            using var server = new RawLoopback(responseBody: Canary);
            CurlRequest request = RequestCurlExporter.Prepare(
                new RequestReplayDraft { Method = "GET", Url = server.Origin + "/" }, new ReplayTransportOptions(), 0, CancellationToken.None);
            CurlRequest refused = RequestCurlExporter.Prepare(
                new RequestReplayDraft { Method = "GET", Url = $"http://127.0.0.1:{ClosedPort()}/" },
                new ReplayTransportOptions(),
                0,
                CancellationToken.None);

            foreach (string script in new[]
            {
                CurlScripts.WriteFile(_folder, "output\\file.ps1", request, CurlScriptForm.File),
                CurlScripts.WritePasteableHost(_folder, "output\\pasteable.ps1", request),
            })
            {
                Task<ReceivedRequest> received = server.AcceptAsync();
                ScriptRun run = await CurlScripts.RunAsync(script, _folder);
                await received;

                Assert.True(run.Output.Contains(Canary, StringComparison.Ordinal), run.ToString());
            }

            foreach (string script in new[]
            {
                CurlScripts.WriteFile(_folder, "output\\refused-file.ps1", refused, CurlScriptForm.File),
                CurlScripts.WritePasteableHost(_folder, "output\\refused-pasteable.ps1", refused),
            })
            {
                ScriptRun run = await CurlScripts.RunAsync(script, _folder);

                Assert.True(run.Error.Contains("curl: (7)", StringComparison.Ordinal), run.ToString());
            }
        }

        // G19: a pasteable command holds a body of 64 KiB exactly and runs it: curl.exe gets every byte on stdin, and the
        // host keeps running with curl's result.
        [CurlScriptFact]
        public async Task APasteableCommand_SendsTheLargestBodyItHolds()
        {
            var draft = new RequestReplayDraft { Method = "POST", Url = "http://127.0.0.1:9/" };
            draft.SetBodyBytes(RandomNumberGenerator.GetBytes(RequestCurlExporter.MaxPasteableBodyBytes));
            CurlRequest request = RequestCurlExporter.Prepare(draft, new ReplayTransportOptions(), 0, CancellationToken.None);
            string record = CurlScripts.ConfigureEcho(_folder);

            ScriptRun run = await CurlScripts.RunAsync(
                CurlScripts.WritePasteableHost(_folder, "largest.ps1", request, new CurlScriptTestHooks(CurlScripts.EchoHelper)), _folder);
            EchoRun send = Assert.Single(CurlScripts.ReadEcho(record), echo => echo.Kind == "send");

            Assert.Contains("xmat-host-alive 0", run.Output, StringComparison.Ordinal);
            Assert.Equal(RequestCurlExporter.MaxPasteableBodyBytes, send.StdinBytes);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(request.Body.Span)), send.StdinSha256);
        }

        // Windows PowerShell 5.1, which every Windows has, runs neither form: the file's #Requires refuses it, and the
        // pasteable command says that it needs PowerShell 7.2 and leaves 1 with its host running. Nothing starts.
        [CurlScriptFact]
        public async Task WindowsPowerShell_RunsNeitherForm()
        {
            string windowsPowerShell = Path.Join(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            CurlRequest request = RequestCurlExporter.Prepare(
                new RequestReplayDraft { Method = "GET", Url = "http://127.0.0.1:9/" }, new ReplayTransportOptions(), 0, CancellationToken.None);
            var hooks = new CurlScriptTestHooks(CurlScripts.EchoHelper);
            string record = CurlScripts.ConfigureEcho(_folder);

            ScriptRun file = await CurlScripts.RunAsync(
                CurlScripts.WriteFile(_folder, "legacy.ps1", request, CurlScriptForm.File, null, hooks), _folder, host: windowsPowerShell);
            ScriptRun pasteable = await CurlScripts.RunAsync(
                CurlScripts.WritePasteableHost(_folder, "legacy-host.ps1", request, hooks), _folder, host: windowsPowerShell);

            Assert.True(file.ExitCode != 0 && file.Error.Contains("7.2", StringComparison.Ordinal), file.ToString());
            Assert.Contains("xmat-host-alive 1", pasteable.Output, StringComparison.Ordinal);
            Assert.True(pasteable.Error.Contains("needs PowerShell 7.2", StringComparison.Ordinal), pasteable.ToString());
            Assert.Empty(CurlScripts.ReadEcho(record));
        }

        // T9: what runs the scripts here is recorded in the test output, and meets the floor that the scripts check.
        [CurlScriptFact]
        public async Task TheRuntime_IsRecorded_AndMeetsTheFloor()
        {
            string script = Path.Join(_folder, "runtime.ps1");
            File.WriteAllText(
                script,
                "[Console]::Out.WriteLine('pwsh ' + $PSVersionTable.PSVersion.ToString())\r\n" +
                "& (Join-Path ([System.Environment]::SystemDirectory) 'curl.exe') -q --version\r\n");

            ScriptRun run = await CurlScripts.RunAsync(script, _folder);
            _output.WriteLine(run.Output);
            Match pwsh = Regex.Match(run.Output, "pwsh (\\d+)\\.(\\d+)");
            Match curl = Regex.Match(run.Output, "^curl (\\d+\\.\\d+\\.\\d+)", RegexOptions.Multiline);

            Assert.Equal(0, run.ExitCode);
            Assert.True(new Version(int.Parse(pwsh.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(pwsh.Groups[2].Value, CultureInfo.InvariantCulture)) >=
                Version.Parse(RequestCurlExporter.MinimumPowerShellVersion));
            Assert.True(Version.Parse(curl.Groups[1].Value) >= Version.Parse(RequestCurlExporter.MinimumCurlVersion));
            Assert.Matches("(?m)^Features:.* Unicode( |$)", run.Output);
        }

        // A loopback port that nothing listens on: a listener's own, just stopped.
        private static int ClosedPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        // An unrelated process that a script must never stop: an xmat-echo.exe that holds, started with only the system
        // variables that a script's own children get, so nothing of the test's environment reaches its record.
        private static Process StartDecoy(string folder)
        {
            Directory.CreateDirectory(folder);
            CurlScripts.ConfigureEcho(folder, mode: "hold");
            var start = new ProcessStartInfo(CurlScripts.EchoHelper, "decoy")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                WorkingDirectory = folder,
            };
            start.Environment.Clear();
            foreach (string name in RequestCurlExporter.ChildEnvironment)
            {
                if (Environment.GetEnvironmentVariable(name) is string value)
                {
                    start.Environment[name] = value;
                }
            }

            return Process.Start(start);
        }

        private static void Stop(Process process)
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit();
            }
        }

        // The first run in record, once xmat-echo.exe has written it.
        private static async Task<EchoRun> WaitForRecordAsync(string record)
        {
            var elapsed = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    if (CurlScripts.ReadEcho(record) is { Count: > 0 } runs)
                    {
                        return runs[0];
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException)
                {
                    // Still being written.
                }

                if (elapsed.Elapsed > TimeSpan.FromSeconds(60))
                {
                    throw new TimeoutException("xmat-echo.exe did not write its record.");
                }

                await Task.Delay(20);
            }
        }

        // A PowerShell script that runs command in a runspace of its own with $global:LASTEXITCODE set to 0, waits until
        // xmat-echo.exe records the send, stops that pipeline as Ctrl+C would, and writes the pipeline's state and the
        // runspace's $global:LASTEXITCODE afterwards.
        private static string InterruptingDriver(string record, string command) =>
            string.Join(
                "\r\n",
                "$ErrorActionPreference = 'Stop'",
                "$xmatRecord = " + RequestCurlExporter.Literal(record),
                "$xmatRunspace = [System.Management.Automation.Runspaces.RunspaceFactory]::CreateRunspace()",
                "$xmatRunspace.Open()",
                "try {",
                "    $xmatSeed = [System.Management.Automation.PowerShell]::Create()",
                "    $xmatSeed.Runspace = $xmatRunspace",
                "    $null = $xmatSeed.AddScript('$global:LASTEXITCODE = 0').Invoke()",
                "    $xmatSeed.Dispose()",
                "    $xmatRun = [System.Management.Automation.PowerShell]::Create()",
                "    $xmatRun.Runspace = $xmatRunspace",
                "    $null = $xmatRun.AddScript(" + command + ")",
                "    $xmatAsync = $xmatRun.BeginInvoke()",
                "    $xmatDeadline = [DateTime]::UtcNow.AddSeconds(60)",
                "    while ($true) {",
                "        $xmatText = ''",
                "        try { $xmatText = [System.IO.File]::ReadAllText($xmatRecord) } catch { }",
                "        if ($xmatText.Contains('\"kind\":\"send\"')) { break }",
                "        if ($xmatAsync.IsCompleted) { throw 'The run ended before its curl.exe started.' }",
                "        if ([DateTime]::UtcNow -gt $xmatDeadline) { throw 'The run did not start its curl.exe.' }",
                "        Start-Sleep -Milliseconds 20",
                "    }",
                "    Start-Sleep -Milliseconds 300",
                "    $xmatRun.Stop()",
                "    $xmatState = $xmatRun.InvocationStateInfo.State",
                "    $xmatRun.Dispose()",
                "    $xmatQuery = [System.Management.Automation.PowerShell]::Create()",
                "    $xmatQuery.Runspace = $xmatRunspace",
                "    $xmatCode = $xmatQuery.AddScript('$global:LASTEXITCODE').Invoke()",
                "    $xmatQuery.Dispose()",
                "    [Console]::Out.WriteLine('xmat-interrupted ' + $xmatState + ' ' + $xmatCode[0])",
                "}",
                "finally {",
                "    $xmatRunspace.Dispose()",
                "}",
                string.Empty);

        // A GET whose arguments take exactly bound on the command line, with filler made of these characters.
        private static CurlRequest AtBudget(long bound, string filler)
        {
            var probe = new RequestReplayDraft { Method = "GET", Url = "http://127.0.0.1:9/", HeadersText = "X-Big: a" };
            long baseBound = RequestCurlExporter.Prepare(probe, new ReplayTransportOptions(), 0, CancellationToken.None).CommandLineBound;
            int extra = checked((int)((bound - baseBound) / 2));
            string fill = string.Concat(Enumerable.Repeat(filler, (extra / filler.Length) + 1))[..extra];
            var draft = new RequestReplayDraft { Method = "GET", Url = "http://127.0.0.1:9/", HeadersText = "X-Big: a" + fill };
            CurlRequest request = RequestCurlExporter.Prepare(draft, new ReplayTransportOptions(), 0, CancellationToken.None);
            Assert.Equal(bound, request.CommandLineBound);
            return request;
        }

        private static (bool Ipv6, ReplayHttpVersion Version, Func<string, RequestReplayDraft> Make) Case(string name) => name switch
        {
            "get" => (false, ReplayHttpVersion.Auto, origin => Draft("GET", origin + "/p?q=1", "X-First: 1\r\nAccept: x/y")),
            "head" => (false, ReplayHttpVersion.Auto, origin => Draft("HEAD", origin + "/p", "X-First: 1")),
            "post-content" => (false, ReplayHttpVersion.Auto, origin => Draft(
                "POST", origin + "/p",
                "X-First: 1\r\nContent-Type: text/plain\r\nX-Second: 2\r\nContent-Language: en\r\nAccept: x/y\r\nContent-Disposition: inline\r\nX-Third: 3",
                "hello")),
            "binary" => (false, ReplayHttpVersion.Auto, origin => Draft(
                "PUT", origin + "/b", "Content-Type: application/octet-stream", bytes: new byte[] { 0, 13, 10, 26, 255, 65, 10, 13, 34, 39 })),
            "empty-post" => (false, ReplayHttpVersion.Auto, origin => Draft("POST", origin + "/e", string.Empty)),
            "empty-get-content-type" => (false, ReplayHttpVersion.Auto, origin => Draft("GET", origin + "/e", "Content-Type: text/plain\r\nX-After: 1")),
            "patch" => (false, ReplayHttpVersion.Auto, origin => Draft("PATCH", origin + "/p", "Content-Type: application/json", "{}")),
            "lower-method" => (false, ReplayHttpVersion.Auto, origin => Draft("patch", origin + "/p", string.Empty, "x")),
            "custom-method" => (false, ReplayHttpVersion.Auto, origin => Draft("PROPFIND", origin + "/d", "Depth: 1")),
            "repeated" => (false, ReplayHttpVersion.Auto, origin => Draft(
                "POST", origin + "/r",
                "X-A: one\r\nCookie: a=1\r\nX-A: two\r\nCookie: b=2\r\nUser-Agent: UA1\r\nUser-Agent: UA2\r\nAccept: x/y\r\nAccept: z/w",
                "AB")),
            "empty-value" => (false, ReplayHttpVersion.Auto, origin => Draft("GET", origin + "/v", "X-Empty:\r\nX-B: b")),
            "dash-name" => (false, ReplayHttpVersion.Auto, origin => Draft("GET", origin + "/d", "-X-Dash: 1")),
            "unusual-path" => (false, ReplayHttpVersion.Auto, origin => Draft(
                "GET", origin + "/a%20b/c;d=e/%7Euser/x+y/(z)!*'?q=1&r=%C3%A9&s=a+b&t=%2F&u=%5B1%5D", string.Empty)),
            "ipv6" => (true, ReplayHttpVersion.Auto, origin => Draft("GET", origin + "/six", "X-A: 1")),
            "http11" => (false, ReplayHttpVersion.Http11, origin => Draft("GET", origin + "/one", "X-A: 1")),
            "large-body" => (false, ReplayHttpVersion.Auto, origin => Draft(
                "POST", origin + "/l", "Content-Type: application/octet-stream",
                bytes: Enumerable.Range(0, 100 * 1024).Select(index => (byte)(index * 31)).ToArray())),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };

        private static RequestReplayDraft Draft(string method, string url, string headers, string text = null, byte[] bytes = null)
        {
            var draft = new RequestReplayDraft { Method = method, Url = url, HeadersText = headers, BodyText = text ?? string.Empty };
            if (bytes != null)
            {
                draft.SetBodyBytes(bytes);
            }

            return draft;
        }
    }
}
