// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using XMAT.Operations.Replay;
using XMAT.Tests.HeadlessParity;

namespace XMAT.Tests.Replay
{
    // What the tests of generated cURL scripts need: PowerShell 7 and the curl.exe of Windows, a script run as a file
    // (never as -Command text, which has a command-line limit of its own), xmat-echo.exe in place of curl, and a loopback
    // listener that keeps the raw bytes of each request.
    internal static class CurlScripts
    {
        internal static string Curl { get; } = Path.Join(Environment.SystemDirectory, "curl.exe");

        // pwsh.exe, from PATH or its default folder; null when there is none.
        internal static string Pwsh { get; } = FindPwsh();

        // Why a script test cannot run here, or null.
        internal static string MissingRuntime =>
            Pwsh == null ? "PowerShell 7 (pwsh.exe) was not found." :
            !File.Exists(Curl) ? "The curl.exe of Windows was not found." :
            null;

        // On GitHub Actions a missing runtime fails the tests instead of skipping them, so CI never skips them silently.
        internal static bool OnCi =>
            string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);

        internal static string EchoHelper => RepositoryPaths.FindEchoHelper() ??
            throw new InvalidOperationException("xmat-echo.exe was not found. Build the solution (project XMAT.Tests.Echo).");

        internal static string Text(CurlRequest request, CurlScriptForm form, string sidecar = null, CurlScriptTestHooks hooks = null)
        {
            using var stream = new MemoryStream();
            RequestCurlExporter.Write(request, form, sidecar, stream, hooks, CancellationToken.None);
            return new UTF8Encoding(false).GetString(stream.ToArray()).TrimStart('\uFEFF');
        }

        // Writes the script as a file in folder and returns its path.
        internal static string WriteFile(
            string folder, string name, CurlRequest request, CurlScriptForm form, string sidecar = null, CurlScriptTestHooks hooks = null)
        {
            string path = Path.Join(folder, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using var stream = new FileStream(path, FileMode.CreateNew);
            RequestCurlExporter.Write(request, form, sidecar, stream, hooks, CancellationToken.None);
            return path;
        }

        // A pasteable command as a host script: the command, then a line that shows that the host is still running and
        // what the command left in $LASTEXITCODE.
        internal static string WritePasteableHost(string folder, string name, CurlRequest request, CurlScriptTestHooks hooks = null)
        {
            string path = Path.Join(folder, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string text = Text(request, CurlScriptForm.Pasteable, null, hooks) +
                "\r\n[Console]::Out.WriteLine('xmat-host-alive ' + $LASTEXITCODE)\r\nexit 0\r\n";
            File.WriteAllText(path, text, new UTF8Encoding(true));
            return path;
        }

        // Runs a script file with pwsh -File (or host, such as Windows PowerShell) in folder, with its environment changed by
        // environment, and returns its exit code and output; it is stopped after timeout.
        internal static async Task<ScriptRun> RunAsync(
            string script,
            string folder,
            Action<IDictionary<string, string>> environment = null,
            int timeoutSeconds = 120,
            string host = null)
        {
            var start = new ProcessStartInfo(host ?? Pwsh)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = folder,
            };
            foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script })
            {
                start.ArgumentList.Add(argument);
            }

            environment?.Invoke(start.Environment);
            using Process process = Process.Start(start);
            process.StandardInput.Close();
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException($"The script did not finish in {timeoutSeconds} seconds.");
            }

            return new ScriptRun(process.ExitCode, await output, await error);
        }

        // Writes xmat-echo.json in folder, for xmat-echo.exe started there, and returns the record file's path.
        internal static string ConfigureEcho(string folder, string version = "8.21.0", bool unicode = true, string mode = "read")
        {
            string record = Path.Join(folder, "echo-record.jsonl");
            File.WriteAllText(
                Path.Join(folder, "xmat-echo.json"),
                JsonSerializer.Serialize(new { record, version, unicode, mode }));
            return record;
        }

        // The runs that xmat-echo.exe recorded, in order; none when it never ran.
        internal static IReadOnlyList<EchoRun> ReadEcho(string record) =>
            File.Exists(record)
                ? File.ReadAllLines(record).Where(line => line.Length > 0).Select(line => new EchoRun(JsonDocument.Parse(line).RootElement)).ToList()
                : Array.Empty<EchoRun>();

        private static string FindPwsh()
        {
            foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
            {
                if (folder.Length != 0 && File.Exists(Path.Join(folder.Trim('"'), "pwsh.exe")))
                {
                    return Path.Join(folder.Trim('"'), "pwsh.exe");
                }
            }

            string installed = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
            return File.Exists(installed) ? installed : null;
        }
    }

    internal sealed record ScriptRun(int ExitCode, string Output, string Error)
    {
        public override string ToString() => $"exit {ExitCode}; output: {Output}; error: {Error}";
    }

    // One run of xmat-echo.exe.
    internal sealed class EchoRun
    {
        internal EchoRun(JsonElement root)
        {
            Kind = root.GetProperty("kind").GetString();
            ProcessId = root.GetProperty("pid").GetInt32();
            Arguments = root.GetProperty("args").EnumerateArray().Select(value => value.GetString()).ToList();
            NativeArguments = root.GetProperty("nativeArgs").EnumerateArray().Select(value => value.GetString()).ToList();
            CommandLine = root.GetProperty("commandLine").GetString();
            Environment = root.GetProperty("environment").EnumerateObject()
                .ToDictionary(variable => variable.Name, variable => variable.Value.GetString(), StringComparer.OrdinalIgnoreCase);
            StdinBytes = root.TryGetProperty("stdinBytes", out JsonElement bytes) ? bytes.GetInt64() : -1;
            StdinSha256 = root.TryGetProperty("stdinSha256", out JsonElement sha256) ? sha256.GetString() : null;
        }

        internal string Kind { get; }

        internal int ProcessId { get; }

        internal IReadOnlyList<string> Arguments { get; }

        internal IReadOnlyList<string> NativeArguments { get; }

        internal string CommandLine { get; }

        internal IReadOnlyDictionary<string, string> Environment { get; }

        internal long StdinBytes { get; }

        internal string StdinSha256 { get; }
    }

    // A test that runs generated scripts: skipped where PowerShell 7 or the curl.exe of Windows is missing, except on CI,
    // where it runs and fails instead.
    public sealed class CurlScriptFactAttribute : FactAttribute
    {
        public CurlScriptFactAttribute()
        {
            if (CurlScripts.MissingRuntime is string missing && !CurlScripts.OnCi)
            {
                Skip = missing;
            }
        }
    }

    public sealed class CurlScriptTheoryAttribute : TheoryAttribute
    {
        public CurlScriptTheoryAttribute()
        {
            if (CurlScripts.MissingRuntime is string missing && !CurlScripts.OnCi)
            {
                Skip = missing;
            }
        }
    }

    // A loopback listener that answers each request with a 200 holding responseBody (empty by default) and keeps its raw
    // bytes. IPv6 listens on [::1].
    internal sealed class RawLoopback : IDisposable
    {
        private readonly byte[] _ok;
        private readonly TcpListener _listener;

        internal RawLoopback(bool ipv6 = false, string responseBody = "")
        {
            byte[] body = Encoding.UTF8.GetBytes(responseBody);
            _ok = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length.ToString(CultureInfo.InvariantCulture)}\r\nConnection: close\r\n\r\n")
                .Concat(body).ToArray();
            _listener = new TcpListener(ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Origin = ipv6 ? $"http://[::1]:{Port}" : $"http://127.0.0.1:{Port}";
        }

        internal int Port { get; }

        internal string Origin { get; }

        // Whether a connection is waiting that nobody accepted.
        internal bool HasPendingConnection => _listener.Pending();

        // Accepts one connection and reads one request from it; with closeAtOnce, closes it without reading or answering.
        internal async Task<RawRequest> AcceptAsync(bool closeAtOnce = false, int timeoutSeconds = 60)
        {
            using TcpClient client = await _listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(timeoutSeconds));
            if (closeAtOnce)
            {
                client.Client.LingerState = new LingerOption(true, 0);
                return null;
            }

            NetworkStream stream = client.GetStream();
            var buffer = new List<byte>();
            var chunk = new byte[65536];
            int headEnd = -1;
            long need = -1;
            while (need < 0 || buffer.Count < need)
            {
                int read = await stream.ReadAsync(chunk).AsTask().WaitAsync(TimeSpan.FromSeconds(timeoutSeconds));
                if (read == 0)
                {
                    break;
                }

                buffer.AddRange(chunk.AsSpan(0, read).ToArray());
                if (headEnd < 0)
                {
                    int index = buffer.ToArray().AsSpan().IndexOf("\r\n\r\n"u8);
                    if (index >= 0)
                    {
                        headEnd = index + 4;
                        Match length = Regex.Match(
                            Encoding.UTF8.GetString(buffer.ToArray(), 0, headEnd), @"(?im)^Content-Length:[ \t]*(\d+)\r$");
                        need = headEnd + (length.Success ? long.Parse(length.Groups[1].Value, CultureInfo.InvariantCulture) : 0);
                    }
                }
            }

            await stream.WriteAsync(_ok);
            byte[] bytes = buffer.ToArray();
            return new RawRequest(Encoding.UTF8.GetString(bytes, 0, Math.Max(0, headEnd)), bytes[Math.Max(0, headEnd)..]);
        }

        public void Dispose() => _listener.Stop();
    }

    // A request as it reached the listener: its head, as text, and its body.
    internal sealed record RawRequest(string Head, byte[] Body)
    {
        // The request line, then each field line as name, colon and value without SP or HTAB around the value.
        internal IReadOnlyList<string> Lines =>
            Head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                .Select((line, index) => index == 0 || line.IndexOf(':', StringComparison.Ordinal) < 0
                    ? line
                    : line[..line.IndexOf(':', StringComparison.Ordinal)] + ":" +
                        line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim(' ', '\t'))
                .ToList();

        // The raw value of the first field line with this name, as UTF-8 bytes.
        internal byte[] FieldValueBytes(string name)
        {
            string line = Head.Split("\r\n").First(text => text.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase));
            return Encoding.UTF8.GetBytes(line[(name.Length + 1)..].Trim(' ', '\t'));
        }
    }
}
