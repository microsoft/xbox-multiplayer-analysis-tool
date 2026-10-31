// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using XMAT.Tests.Fixtures;
using XMAT.Tests.HeadlessParity;

namespace XMAT.Tests.Cli
{
    // A run-owned folder under the test output (never the OS temporary folder); only the test that made it removes it.
    internal sealed class CliTestDirectory : IDisposable
    {
        internal CliTestDirectory()
        {
            Name = Guid.NewGuid().ToString("N");
            Root = Path.Combine(AppContext.BaseDirectory, "HeadlessTestArtifacts", "Cli", Name);
            Directory.CreateDirectory(Root);
        }

        // Unique, so finding it in xmat's output means that xmat leaked a path.
        internal string Name { get; }

        internal string Root { get; }

        internal string[] Entries() =>
            Directory.GetFileSystemEntries(Root).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToArray();

        public void Dispose()
        {
            for (int attempt = 1; Directory.Exists(Root); attempt++)
            {
                try
                {
                    Directory.Delete(Root, recursive: true);
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 20)
                {
                    Thread.Sleep(100);
                }
            }
        }
    }

    internal sealed record XmatRun(int ExitCode, string Stdout, string Stderr, bool WindowSeen)
    {
        // Standard output must be exactly one JSON document: JsonDocument.Parse rejects anything after it.
        internal JsonElement Json()
        {
            using JsonDocument document = JsonDocument.Parse(Stdout);
            return document.RootElement.Clone();
        }

        internal string[] ErrorCodes() =>
            Json().GetProperty("errors").EnumerateArray().Select(error => error.GetProperty("code").GetString()).ToArray();
    }

    // Runs the built xmat.exe as a child process, the way scripts and CI run it.
    internal static class XmatProcess
    {
        internal static string CliDirectory
        {
            get
            {
                string directory = RepositoryPaths.FindCliDirectory();
                Assert.True(
                    directory != null && File.Exists(Path.Combine(directory, "xmat.exe")),
                    $"xmat.exe was not found. Build the solution (project XMAT.Cli) or set {RepositoryPaths.CliDirectoryVariable}.");
                return directory;
            }
        }

        internal static Task<XmatRun> RunAsync(string workingDirectory, params string[] args) =>
            RunFromAsync(CliDirectory, workingDirectory, args);

        // Runs the xmat.exe in cliDirectory, such as a copy of the package that a test changed.
        internal static async Task<XmatRun> RunFromAsync(string cliDirectory, string workingDirectory, params string[] args)
        {
            using XmatChild child = Start(cliDirectory, workingDirectory, args);
            return await child.CompleteAsync();
        }

        internal static XmatChild Start(string cliDirectory, string workingDirectory, params string[] args)
        {
            var startInfo = new ProcessStartInfo(Path.Combine(cliDirectory, "xmat.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = workingDirectory,
            };
            foreach (string arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            Process process = Process.Start(startInfo);
            Assert.NotNull(process);
            return new XmatChild(process, string.Join(' ', args));
        }
    }

    // A running xmat.exe. CreateNoWindow gives it a console of its own without a window, as in CI.
    internal sealed class XmatChild : IDisposable
    {
        private static readonly TimeSpan ExitTimeout = TimeSpan.FromMinutes(2);

        private readonly Process _process;
        private readonly string _commandLine;
        private readonly Task<string> _stdout;
        private readonly Task<string> _stderr;

        internal XmatChild(Process process, string commandLine)
        {
            _process = process;
            _commandLine = commandLine;
            _process.StandardInput.Close();
            _stdout = process.StandardOutput.ReadToEndAsync();
            _stderr = process.StandardError.ReadToEndAsync();
        }

        internal int Id => _process.Id;

        internal bool HasExited => _process.HasExited;

        internal async Task<XmatRun> CompleteAsync()
        {
            // A console tool never has a window. Sample while it runs; a GUI start would also never exit on its own.
            bool windowSeen = false;
            var elapsed = Stopwatch.StartNew();
            while (!_process.HasExited)
            {
                _process.Refresh();
                windowSeen |= _process.MainWindowHandle != IntPtr.Zero;
                if (elapsed.Elapsed > ExitTimeout)
                {
                    _process.Kill(entireProcessTree: true);
                    Assert.Fail($"xmat {_commandLine} did not exit within {ExitTimeout}.");
                }

                await Task.Delay(25);
            }

            await _process.WaitForExitAsync();
            return new XmatRun(_process.ExitCode, await _stdout, await _stderr, windowSeen);
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // It exited meanwhile.
            }

            _process.Dispose();
        }
    }

    // A read-only snapshot of the GUI's storage under %APPDATA% (settings and logs), to prove that xmat.exe leaves it
    // alone; the probe itself never writes there. Had xmat opened a GUI log (Logger opens it with FileMode.Create), the
    // file would have been truncated: its first bytes would differ or it would be shorter. The test host's own loggers
    // only append, so a longer file with the same first bytes is fine. The tests that use the probe run in a collection
    // that runs alone, so no other test writes these files meanwhile.
    internal sealed class GuiStorageProbe
    {
        private const int PrefixLength = 4096;

        private static readonly string StorageRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Xbox Multiplayer Analysis Tool");

        private readonly string[] _storageEntries;
        private readonly string[] _logEntries;
        private readonly string _settings;
        private readonly Dictionary<string, (long Length, byte[] Prefix)> _logs;

        private GuiStorageProbe()
        {
            _storageEntries = Entries(StorageRoot);
            _logEntries = Entries(LogsDirectory);
            _settings = Fingerprint(SettingsFile);
            _logs = new Dictionary<string, (long Length, byte[] Prefix)>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(LogsDirectory))
            {
                foreach (string log in Directory.GetFiles(LogsDirectory, "*.log"))
                {
                    long length = new FileInfo(log).Length;
                    _logs[log] = (length, ReadPrefix(log, (int)Math.Min(PrefixLength, length)));
                }
            }
        }

        private static string LogsDirectory => Path.Combine(StorageRoot, "Logs");

        private static string SettingsFile => Path.Combine(StorageRoot, "app-settings.json");

        internal static GuiStorageProbe Capture() => new();

        internal void AssertUnchanged()
        {
            Assert.Equal(_storageEntries, Entries(StorageRoot));
            Assert.Equal(_logEntries, Entries(LogsDirectory));
            Assert.Equal(_settings, Fingerprint(SettingsFile));
            foreach (KeyValuePair<string, (long Length, byte[] Prefix)> log in _logs)
            {
                long length = new FileInfo(log.Key).Length;
                if (length < log.Value.Length)
                {
                    Assert.Fail($"{Path.GetFileName(log.Key)} in the GUI's Logs folder shrank from {log.Value.Length} to {length} bytes.");
                }

                Assert.Equal(log.Value.Prefix, ReadPrefix(log.Key, log.Value.Prefix.Length));
            }
        }

        private static string[] Entries(string directory) =>
            Directory.Exists(directory)
                ? Directory.GetFileSystemEntries(directory).Select(Path.GetFileName).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray()
                : Array.Empty<string>();

        private static string Fingerprint(string file)
        {
            if (!File.Exists(file))
            {
                return "absent";
            }

            var info = new FileInfo(file);
            return $"{info.Length}|{info.LastWriteTimeUtc.Ticks}|{CliAssert.Sha256(file)}";
        }

        // Shares the file with its writers and reads at most count bytes from its start.
        private static byte[] ReadPrefix(string file, int count)
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[count];
            int read = stream.ReadAtLeast(buffer, count, throwOnEndOfStream: false);
            return buffer.AsSpan(0, read).ToArray();
        }
    }

    internal static class CliAssert
    {
        // Shows xmat's output when the exit code is not the expected one.
        internal static void ExitCode(int expected, XmatRun run)
        {
            if (run.ExitCode != expected)
            {
                Assert.Fail(
                    $"xmat exited with {run.ExitCode}, expected {expected}.{Environment.NewLine}"
                    + $"stderr:{Environment.NewLine}{run.Stderr}{Environment.NewLine}stdout:{Environment.NewLine}{run.Stdout}");
            }
        }

        internal static string Sha256(string file)
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        // Nothing that a capture or the caller supplied may appear in xmat's output: no canary from the synthetic
        // capture, no path (the run's unique folder name) and no untranslated string.
        internal static void NoLeaks(XmatRun run, CliTestDirectory directory, string publishedFolder = null)
        {
            var texts = new List<(string Source, string Text)> { ("stdout", run.Stdout), ("stderr", run.Stderr) };
            if (publishedFolder != null)
            {
                foreach (string file in Directory.GetFiles(publishedFolder, "*", SearchOption.AllDirectories))
                {
                    texts.Add((file, Path.GetRelativePath(publishedFolder, file)));
                    texts.Add((file, File.ReadAllText(file)));
                }
            }

            foreach ((string source, string text) in texts)
            {
                foreach (string secret in new[] { SyntheticCaptures.Canary, directory.Name, "NO_IMPL_" })
                {
                    if (text.Contains(secret, StringComparison.OrdinalIgnoreCase))
                    {
                        Assert.Fail($"{source} contains {secret}.");
                    }
                }
            }
        }

        internal static void NoStagingFolders(string directory) =>
            Assert.DoesNotContain(
                Directory.GetFileSystemEntries(directory),
                entry => Path.GetFileName(entry).StartsWith(".xmat-staging-", StringComparison.Ordinal));

        // The published folder keeps the staging folder's access control: only the current user, nothing inherited.
        internal static void OnlyCurrentUserCanOpen(string folder)
        {
            DirectorySecurity security = new DirectoryInfo(folder).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            AuthorizationRuleCollection rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
            Assert.NotEmpty(rules);
            foreach (FileSystemAccessRule rule in rules)
            {
                Assert.Equal(identity.User, rule.IdentityReference);
            }
        }
    }
}
