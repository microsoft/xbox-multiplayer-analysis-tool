// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using XMAT.Cli;
using XMAT.Tests.Fixtures;
using XMAT.Tests.Proxy;

namespace XMAT.Tests.Cli
{
    // A real console signal to a running xmat analyze, made deterministic without test hooks in xmat: the test keeps a
    // file open in xmat's staging folder, so xmat can neither publish (rename the folder to --out) nor remove the folder
    // until the test lets go, and the signal always lands before publication however fast the analysis is. Removing the
    // folder deletes the test's marker file first; once it is gone, xmat has given up publishing, the test lets go, and
    // xmat must end with exit code 130, nothing published and its cleanup completed. The signal is Ctrl+Break, which xmat
    // handles like Ctrl+C; unlike Ctrl+C, a parent process cannot leave it disabled for its children. CliStagedRunTests
    // cover the publication boundary itself with in-process barriers.
    [Collection(IsolatedProxyTests.Name)]
    public class XmatSignalTests
    {
        private static readonly TimeSpan WaitLimit = TimeSpan.FromMinutes(2);

        [Fact]
        public async Task CtrlBreak_BeforePublication_CancelsAndPublishesNothing()
        {
            using var directory = new CliTestDirectory();
            using var helperDirectory = new CliTestDirectory();
            string input = SyntheticCaptures.WriteSaz(
                directory.Root, "capture.saz", new SyntheticSazOptions(Consoles: 4, RequestsPerConsole: 5000));
            using ConsoleSignal signal = await ConsoleSignal.StartAsync(helperDirectory.Root);
            using XmatChild xmat = XmatProcess.Start(
                XmatProcess.CliDirectory, directory.Root, "analyze", input, "--out", "results");

            string staging = await WaitForStagingFolderAsync(directory.Root, xmat);
            using (StagingHold hold = StagingHold.Take(staging))
            {
                await signal.SendAsync(xmat.Id, ConsoleSignal.CtrlBreak);
                await hold.WaitUntilRemovalStartsAsync(xmat);
            }

            XmatRun run = await xmat.CompleteAsync();

            CliAssert.ExitCode(130, run);
            JsonElement json = run.Json();
            Assert.Equal("cancelled", json.GetProperty("status").GetString());
            Assert.Contains(run.ErrorCodes(), code => code is "cli.cancelled" or "analysis.cancelled");
            Assert.Empty(json.GetProperty("artifacts").EnumerateArray());
            Assert.Equal("completed", json.GetProperty("cleanup").GetProperty("state").GetString());
            Assert.Equal(new[] { "capture.saz" }, directory.Entries());
        }

        // xmat creates its staging folder after it has checked its prerequisites and --out, and before it analyzes.
        private static async Task<string> WaitForStagingFolderAsync(string directory, XmatChild xmat)
        {
            var elapsed = Stopwatch.StartNew();
            while (true)
            {
                string[] staging = Directory.GetDirectories(directory, CliOutputFolder.StagingPrefix + "*");
                if (staging.Length != 0)
                {
                    return staging[0];
                }

                if (xmat.HasExited)
                {
                    XmatRun run = await xmat.CompleteAsync();
                    Assert.Fail(
                        $"xmat exited with {run.ExitCode} before the signal was sent.{Environment.NewLine}{run.Stderr}{run.Stdout}");
                }

                if (elapsed.Elapsed > WaitLimit)
                {
                    Assert.Fail("xmat did not create its staging folder.");
                }

                await Task.Delay(5);
            }
        }

        // Keeps xmat's staging folder in place: a file in it stays open without delete sharing, so the folder can be
        // neither renamed nor removed. A second, closed marker file shows when xmat starts removing the folder, because
        // the removal deletes every file it can before it fails on the open one.
        private sealed class StagingHold : IDisposable
        {
            private readonly FileStream _hold;
            private readonly string _marker;

            private StagingHold(FileStream hold, string marker)
            {
                _hold = hold;
                _marker = marker;
            }

            internal static StagingHold Take(string staging)
            {
                FileStream hold;
                try
                {
                    hold = new FileStream(Path.Combine(staging, "test-hold.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                }
                catch (DirectoryNotFoundException ex)
                {
                    throw new InvalidOperationException(
                        "xmat published or removed its staging folder before the test could hold it.", ex);
                }

                string marker = Path.Combine(staging, "test-marker.txt");
                File.WriteAllText(marker, "removed when xmat starts removing its staging folder");
                return new StagingHold(hold, marker);
            }

            // xmat removes the folder after it has seen the cancellation, or after it failed otherwise; the exit code
            // tells which. It retries the removal for about a second, so letting go right after is in time.
            internal async Task WaitUntilRemovalStartsAsync(XmatChild xmat)
            {
                var elapsed = Stopwatch.StartNew();
                while (File.Exists(_marker) && !xmat.HasExited)
                {
                    if (elapsed.Elapsed > WaitLimit)
                    {
                        Assert.Fail("xmat did not start removing its staging folder after the signal.");
                    }

                    await Task.Delay(5);
                }
            }

            public void Dispose() => _hold.Dispose();
        }
    }

    // Raises a console control event in another process's console, as pressing Ctrl+Break in its window would. Only a
    // process attached to a console can raise events there, so a Windows PowerShell helper follows the sequence that the
    // GitHub Actions runner uses to stop steps: free its own console, attach to the target's, register a handler that
    // swallows the event, raise the event in the whole console, then detach. The handler must be registered after the
    // switch: detaching from a console drops the process's handlers, and without one the default handler ends the helper
    // when the event reaches it. The helper writes each step with its Win32 error code to standard error, and starts
    // (compiling its P/Invoke code) before the target, so sending takes milliseconds.
    internal sealed class ConsoleSignal : IDisposable
    {
        internal const int CtrlBreak = 1;

        private const string Source = """
            using System;
            using System.Runtime.InteropServices;
            using System.Threading;

            public static class XmatConsoleSignal
            {
                private delegate bool ControlHandler(uint controlType);

                [DllImport("kernel32.dll", SetLastError = true)]
                private static extern bool FreeConsole();

                [DllImport("kernel32.dll", SetLastError = true)]
                private static extern bool AttachConsole(uint processId);

                [DllImport("kernel32.dll", SetLastError = true)]
                private static extern bool SetConsoleCtrlHandler(ControlHandler handler, bool add);

                [DllImport("kernel32.dll", SetLastError = true)]
                private static extern bool GenerateConsoleCtrlEvent(uint controlEvent, uint processGroupId);

                // Returns TRUE for every event, so neither the default handler (which ends the process) nor
                // PowerShell's own handler runs when the helper receives the event that it raised.
                private static readonly ControlHandler Swallow = delegate (uint controlType)
                {
                    Console.Error.WriteLine("swallowed event " + controlType);
                    return true;
                };

                private static int lastError;

                public static string Send(uint processId, uint controlEvent)
                {
                    Console.Error.WriteLine("request process " + processId + " event " + controlEvent);
                    Step("free", FreeConsole());
                    if (!Step("attach", AttachConsole(processId)))
                    {
                        return "error attach " + lastError;
                    }

                    if (!Step("handler", SetConsoleCtrlHandler(Swallow, true)))
                    {
                        int handlerError = lastError;
                        Step("detach", FreeConsole());
                        return "error handler " + handlerError;
                    }

                    if (!Step("send", GenerateConsoleCtrlEvent(controlEvent, 0)))
                    {
                        int sendError = lastError;
                        Step("detach", FreeConsole());
                        return "error send " + sendError;
                    }

                    Console.Out.WriteLine("sent");

                    // Stay attached, with the handler registered, until the event has reached this process too.
                    // Detaching also drops the handler.
                    Thread.Sleep(1000);
                    Step("detach", FreeConsole());
                    return "done";
                }

                private static bool Step(string name, bool succeeded)
                {
                    lastError = succeeded ? 0 : Marshal.GetLastWin32Error();
                    Console.Error.WriteLine(succeeded ? name + " ok" : name + " failed with Win32 error " + lastError);
                    return succeeded;
                }
            }
            """;

        private static readonly TimeSpan LineTimeout = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(10);

        private readonly Process _process;
        private readonly Task<string> _stderr;

        private ConsoleSignal(Process process)
        {
            _process = process;
            _stderr = process.StandardError.ReadToEndAsync();
        }

        // What Windows PowerShell and Add-Type write (compiler files and caches) goes to workDirectory, a run-owned
        // folder, instead of the OS temporary folder or the user's profile.
        internal static async Task<ConsoleSignal> StartAsync(string workDirectory)
        {
            string temp = Directory.CreateDirectory(Path.Combine(workDirectory, "temp")).FullName;
            string localAppData = Directory.CreateDirectory(Path.Combine(workDirectory, "localappdata")).FullName;
            string script = string.Join(
                "\n",
                "$ErrorActionPreference = 'Stop'",
                "$ProgressPreference = 'SilentlyContinue'",
                "Add-Type -TypeDefinition @'",
                Source,
                "'@",
                "[Console]::Out.WriteLine('ready')",
                "$request = [Console]::In.ReadLine().Split(' ')",
                "[Console]::Out.WriteLine([XmatConsoleSignal]::Send([uint32]$request[0], [uint32]$request[1]))");
            var startInfo = new ProcessStartInfo(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workDirectory,
            };
            startInfo.Environment["TEMP"] = temp;
            startInfo.Environment["TMP"] = temp;
            startInfo.Environment["LOCALAPPDATA"] = localAppData;
            foreach (string arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand" })
            {
                startInfo.ArgumentList.Add(arg);
            }

            startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            Process process = Process.Start(startInfo);
            Assert.NotNull(process);
            var signal = new ConsoleSignal(process);
            await signal.ExpectLineAsync("ready", "start");
            return signal;
        }

        internal async Task SendAsync(int processId, int controlEvent)
        {
            await _process.StandardInput.WriteLineAsync(
                string.Create(CultureInfo.InvariantCulture, $"{processId} {controlEvent}"));
            await _process.StandardInput.FlushAsync();
            await ExpectLineAsync("sent", "signal xmat");
        }

        // Lets the helper finish (it detaches about a second after sending); one that is still waiting for a request
        // ends when its standard input closes.
        public void Dispose()
        {
            try
            {
                _process.StandardInput.Close();
                if (!_process.WaitForExit(ExitTimeout))
                {
                    _process.Kill();
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                // It exited meanwhile.
            }

            _process.Dispose();
        }

        private async Task ExpectLineAsync(string expected, string step)
        {
            string line;
            try
            {
                line = await _process.StandardOutput.ReadLineAsync().WaitAsync(LineTimeout);
            }
            catch (TimeoutException)
            {
                line = $"nothing within {LineTimeout}";
            }

            if (line != expected)
            {
                Assert.Fail(
                    $"The console signal helper could not {step}: it wrote '{line ?? "nothing (it ended)"}'; "
                    + await DescribeAsync());
            }
        }

        // How the helper ended and its step-by-step trace, for failure messages.
        private async Task<string> DescribeAsync()
        {
            string ending;
            if (_process.WaitForExit(ExitTimeout))
            {
                // 0xC000013A (STATUS_CONTROL_C_EXIT) means that a console control event ended it.
                ending = string.Create(CultureInfo.InvariantCulture, $"it exited with 0x{_process.ExitCode:X8}");
            }
            else
            {
                _process.Kill();
                ending = "it was still running, so the test stopped it";
            }

            string trace;
            try
            {
                trace = await _stderr.WaitAsync(ExitTimeout);
            }
            catch (TimeoutException)
            {
                trace = "(unavailable)";
            }

            return ending + ". Its trace:" + Environment.NewLine + (trace.Length == 0 ? "(empty)" : trace);
        }
    }
}
