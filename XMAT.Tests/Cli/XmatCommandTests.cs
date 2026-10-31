// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using XMAT.Cli;
using XMAT.Operations.Capabilities;
using XMAT.Tests.Fixtures;
using XMAT.Tests.Proxy;

namespace XMAT.Tests.Cli
{
    // doctor, help, --version and usage errors, run as a child process. None of them may write anything: not in the
    // working folder, not next to xmat.exe and not in the GUI's storage. The collection disables parallelization, so no
    // other test writes the GUI's storage while the read-only GuiStorageProbe watches it.
    [Collection(IsolatedProxyTests.Name)]
    public class XmatCommandTests
    {
        [Fact]
        public async Task Doctor_ForAnalyze_PassesWithoutWritingAnything()
        {
            using var directory = new CliTestDirectory();
            string[] cliFiles = Snapshot(XmatProcess.CliDirectory);
            GuiStorageProbe gui = GuiStorageProbe.Capture();

            XmatRun run = await XmatProcess.RunAsync(directory.Root, "doctor", "--for", "analyze");

            CliAssert.ExitCode(0, run);
            JsonElement json = run.Json();
            Assert.Equal("doctor", json.GetProperty("command").GetString());
            Assert.Equal("succeeded", json.GetProperty("status").GetString());
            Assert.Equal("analyze", json.GetProperty("target").GetString());
            Assert.Equal(
                new[] { "data.apimap", "data.rules", "localization.en-us", "runtime.64-bit" },
                json.GetProperty("checks").EnumerateArray().Select(check => check.GetProperty("id").GetString()));
            Assert.All(
                json.GetProperty("checks").EnumerateArray(),
                check => Assert.Equal("pass", check.GetProperty("status").GetString()));
            Assert.Contains(
                json.GetProperty("capabilities").EnumerateArray(),
                capability => capability.GetProperty("id").GetString() == "analysis.xbox-live"
                    && capability.GetProperty("status").GetString() == "supported");
            JsonElement environment = json.GetProperty("environment");
            Assert.Equal(new[] { "framework", "architecture" }, environment.EnumerateObject().Select(property => property.Name));
            Assert.False(string.IsNullOrEmpty(environment.GetProperty("framework").GetString()));
            Assert.Equal("x64", environment.GetProperty("architecture").GetString());

            Assert.Empty(directory.Entries());
            Assert.Equal(cliFiles, Snapshot(XmatProcess.CliDirectory));
            Assert.False(run.WindowSeen);
            gui.AssertUnchanged();
        }

        [Fact]
        public async Task HelpAndVersion_ComeFromTheRegistryAndTheAssembly()
        {
            using var directory = new CliTestDirectory();

            XmatRun help = await XmatProcess.RunAsync(directory.Root, "help");
            XmatRun version = await XmatProcess.RunAsync(directory.Root, "--version");

            CliAssert.ExitCode(0, help);
            Assert.Equal(
                CliCatalog.Commands(),
                help.Json().GetProperty("commands").EnumerateArray().Select(command => command.GetProperty("name").GetString()));
            foreach (Capability capability in CapabilityRegistry.All.Where(item => item.Status == CapabilityStatus.Supported))
            {
                Assert.Contains("xmat " + capability.CliRoute, help.Stderr, StringComparison.Ordinal);
            }

            CliAssert.ExitCode(0, version);
            Assert.Equal(
                typeof(CliEntry).Assembly.GetName().Version.ToString(),
                version.Json().GetProperty("version").GetString());
            Assert.Empty(directory.Entries());
        }

        // One usage error per process-level concern: no command at all, and unknown words that must never be echoed (the
        // in-process parser tests cover the full matrix).
        [Theory]
        [InlineData("", "cli.usage.no-command")]
        [InlineData("s3cretcanary9", "cli.usage.unknown-command")]
        [InlineData("help s3cretcanary9", "cli.usage.unknown-command")]
        [InlineData("doctor --for s3cretcanary9", "cli.usage.unknown-target")]
        [InlineData("analyze capture.saz --out results --s3cretcanary9=s3cretcanary9", "cli.usage.unknown-option")]
        [InlineData("analyze capture.saz --out results --max-records s3cretcanary9", "cli.usage.invalid-value")]
        public async Task UsageErrors_Return2AndWriteNothing(string commandLine, string code)
        {
            using var directory = new CliTestDirectory();
            GuiStorageProbe gui = GuiStorageProbe.Capture();

            XmatRun run = await XmatProcess.RunAsync(
                directory.Root, commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries));

            CliAssert.ExitCode(2, run);
            JsonElement json = run.Json();
            Assert.Equal("xmat-cli/1", json.GetProperty("schema").GetString());
            Assert.Equal(2, json.GetProperty("exitCode").GetInt32());
            Assert.Equal("failed", json.GetProperty("status").GetString());
            Assert.Equal(new[] { code }, run.ErrorCodes());

            // What the user typed is never repeated unless it is a name from the catalog: even a lowercase word can be
            // a secret.
            Assert.DoesNotContain("s3cretcanary9", run.Stdout + run.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(directory.Entries());
            gui.AssertUnchanged();
        }

        // A damaged installation is a missing prerequisite (exit code 4) for doctor and analyze alike: doctor names the
        // failed check, and analyze stops before it creates anything instead of failing during the analysis. Damaged
        // rules are covered in process (AnalysisAssetsTests).
        [Theory]
        [InlineData(@"Data\XboxLiveTraceAnalyzer.APIMap.csv", "only,three,columns", "data.apimap", "analysis.assets.apimap")]
        [InlineData(@"Langs\en-us.json", "[1, 2, 3]", "localization.en-us", "localization.invalid")]
        public async Task DamagedPackage_IsAMissingPrerequisite(string file, string contents, string check, string code)
        {
            using var directory = new CliTestDirectory();
            string package = CopyPackage(Path.Combine(directory.Root, "package"));
            File.WriteAllText(Path.Combine(package, file), contents);
            string work = Directory.CreateDirectory(Path.Combine(directory.Root, "work")).FullName;
            string input = SyntheticCaptures.WriteSaz(work, "capture.saz");
            string[] packageFiles = Snapshot(package);

            XmatRun doctor = await XmatProcess.RunFromAsync(package, work, "doctor", "--for", "analyze");
            XmatRun analyze = await XmatProcess.RunFromAsync(package, work, "analyze", input, "--out", "results");

            CliAssert.ExitCode(4, doctor);
            JsonElement report = doctor.Json();
            Assert.Equal("failed", report.GetProperty("status").GetString());
            Assert.Equal(new[] { "cli.prerequisite.missing" }, doctor.ErrorCodes());
            JsonElement failed = Assert.Single(
                report.GetProperty("checks").EnumerateArray(), item => item.GetProperty("status").GetString() == "fail");
            Assert.Equal(check, failed.GetProperty("id").GetString());
            Assert.Equal(code, failed.GetProperty("code").GetString());

            CliAssert.ExitCode(4, analyze);
            Assert.Equal("failed", analyze.Json().GetProperty("status").GetString());
            Assert.Equal(new[] { code }, analyze.ErrorCodes());
            Assert.Empty(analyze.Json().GetProperty("artifacts").EnumerateArray());
            Assert.Equal("notNeeded", analyze.Json().GetProperty("cleanup").GetProperty("state").GetString());
            Assert.Equal(new[] { "capture.saz" }, Directory.GetFileSystemEntries(work).Select(Path.GetFileName));
            Assert.Equal(packageFiles, Snapshot(package));
        }

        // Only xmat.exe's packaged runtime configuration asks the GC to conserve memory; the GUI keeps the default.
        [Fact]
        public void PackagedRuntimeConfigs_ConserveMemoryForTheCommandLineOnly()
        {
            string package = XmatProcess.CliDirectory;

            Assert.Equal("9", GcConserveMemory(Path.Combine(package, "xmat.runtimeconfig.json")));
            Assert.Null(GcConserveMemory(Path.Combine(package, "XboxMultiplayerAnalysisTool.runtimeconfig.json")));
        }

        // System.GC.ConserveMemory from a runtimeconfig.json, as written, or null when it is not set.
        private static string GcConserveMemory(string runtimeConfig)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(runtimeConfig));
            return document.RootElement.GetProperty("runtimeOptions").TryGetProperty(
                    "configProperties", out JsonElement properties) &&
                properties.TryGetProperty("System.GC.ConserveMemory", out JsonElement value)
                    ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()
                    : null;
        }

        // A private copy of the built package, which a test may damage.
        private static string CopyPackage(string destination)
        {
            string source = XmatProcess.CliDirectory;
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target);
            }

            return destination;
        }

        // Every file under the folder with its size: a run that wrote a log or a settings file next to xmat.exe shows.
        private static string[] Snapshot(string folder) =>
            Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
                .Select(file => $"{Path.GetRelativePath(folder, file)}|{new FileInfo(file).Length}")
                .OrderBy(entry => entry, StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }
}
