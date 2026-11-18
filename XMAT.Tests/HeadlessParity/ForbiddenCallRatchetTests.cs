// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text;

namespace XMAT.Tests.HeadlessParity
{
    // Test C: calls to GUI-only APIs from non-UI code may not grow. The shared operations (XMAT.Operations) and the
    // command line (XMAT.Cli) may not make any, and may not declare WPF types, which would otherwise be exempt. Generate
    // the baseline from a pinned build with
    //   XMAT_PARITY_IL_SOURCE=<assembly path>[;<assembly path>]  XMAT_PARITY_IL_WRITE=<baseline file>
    // and run WritesTheBaseline_WhenRequested; the comparison is skipped while XMAT_PARITY_IL_WRITE is set.
    public class ForbiddenCallRatchetTests
    {
        internal const string SourceVariable = "XMAT_PARITY_IL_SOURCE";
        internal const string WriteVariable = "XMAT_PARITY_IL_WRITE";

        [Fact]
        public void ForbiddenCalls_MatchTheShrinkOnlyBaseline()
        {
            if (GenerationRequested())
            {
                Assert.False(IsContinuousIntegration(), $"{WriteVariable} must not be set in CI.");
                return;
            }

            string baselinePath = ForbiddenCallBaseline.DefaultPath;
            Assert.True(
                File.Exists(baselinePath),
                $"{baselinePath} is missing. Generate it from the pinned build with {SourceVariable} and {WriteVariable}.");

            Dictionary<string, int> baseline = ForbiddenCallBaseline.Load(baselinePath);
            Dictionary<string, int> current = ScanSources(DefaultSources())
                .ToDictionary(call => call.Key, call => call.Count, StringComparer.Ordinal);

            List<string> added = current
                .Where(entry => !baseline.TryGetValue(entry.Key, out int allowed) || entry.Value > allowed)
                .Select(entry => $"  {entry.Key}|{entry.Value} (baseline: {(baseline.TryGetValue(entry.Key, out int was) ? was : 0)})")
                .OrderBy(line => line, StringComparer.Ordinal)
                .ToList();
            List<string> removed = baseline
                .Where(entry => !current.TryGetValue(entry.Key, out int now) || now < entry.Value)
                .Select(entry => $"  {entry.Key}|{(current.TryGetValue(entry.Key, out int now) ? now : 0)} (baseline: {entry.Value})")
                .OrderBy(line => line, StringComparer.Ordinal)
                .ToList();

            var message = new StringBuilder();
            if (added.Count != 0)
            {
                message.AppendLine("New calls to GUI-only APIs from non-UI code. Move them into a UI type or go through an")
                    .AppendLine("operation and its adapters; the baseline may not grow:")
                    .AppendLine(string.Join(Environment.NewLine, added));
            }

            if (removed.Count != 0)
            {
                message.AppendLine($"Calls that are gone or fewer. Update {ForbiddenCallBaseline.FileName} to the current counts")
                    .AppendLine("(delete lines that reached 0):")
                    .AppendLine(string.Join(Environment.NewLine, removed));
            }

            Assert.True(added.Count == 0 && removed.Count == 0, message.ToString());
        }

        [Fact]
        public void HeadlessCode_CallsNoGuiOnlyApis()
        {
            List<string> offenders = ScanSources(DefaultSources())
                .Where(call => ForbiddenCallScanner.IsHeadlessNamespace(call.CallerType))
                .Select(call => call.ToLine())
                .ToList();

            Assert.True(offenders.Count == 0,
                "XMAT.Operations and XMAT.Cli must not use GUI-only APIs:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
        }

        [Fact]
        public void HeadlessCode_DeclaresNoWpfTypes()
        {
            List<string> offenders = DefaultSources().SelectMany(ForbiddenCallScanner.FindHeadlessUiTypes).ToList();

            Assert.True(offenders.Count == 0,
                "XMAT.Operations and XMAT.Cli must not declare WPF types (XAML code-behind or DispatcherObject subclasses):"
                + Environment.NewLine + string.Join(Environment.NewLine, offenders));
        }

        // A WPF base class gives headless code no exemption: the fixture's call is reported and its type is rejected.
        [Fact]
        public void Scanner_GivesHeadlessUiTypesNoExemption()
        {
            string fixture = typeof(XMAT.Operations.ParityFixtures.HeadlessUiFixture).FullName;
            string assembly = typeof(ForbiddenCallFixture).Assembly.Location;

            ForbiddenCall call = Assert.Single(ForbiddenCallScanner.Scan(assembly), item => item.CallerType == fixture);

            Assert.Equal(fixture + "::Forbidden", call.Caller);
            Assert.Equal("XMAT.PublicUtilities::SafeInvoke", call.Callee);
            Assert.True(ForbiddenCallScanner.IsHeadlessNamespace(call.CallerType));
            Assert.Equal(new[] { fixture }, ForbiddenCallScanner.FindHeadlessUiTypes(assembly));
        }

        [Fact]
        public void WritesTheBaseline_WhenRequested()
        {
            if (!GenerationRequested())
            {
                return;
            }

            string sources = Environment.GetEnvironmentVariable(SourceVariable);
            IReadOnlyList<string> paths = string.IsNullOrWhiteSpace(sources)
                ? DefaultSources()
                : sources.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            List<ForbiddenCall> calls = ScanSources(paths);

            Assert.DoesNotContain(calls, call => ForbiddenCallScanner.IsHeadlessNamespace(call.CallerType));
            ForbiddenCallBaseline.Write(Environment.GetEnvironmentVariable(WriteVariable), calls);
        }

        [Fact]
        public void Scanner_AttributesCompilerGeneratedCode_AndExemptsUiTypes()
        {
            const string Fixture = "XMAT.Tests.HeadlessParity.ForbiddenCallFixture::";
            var fixtureTypes = new[] { typeof(ForbiddenCallFixture).FullName, typeof(ForbiddenCallUiFixture).FullName };
            Dictionary<string, int> calls = ForbiddenCallScanner.Scan(typeof(ForbiddenCallFixture).Assembly.Location)
                .Where(call => fixtureTypes.Contains(call.CallerType))
                .ToDictionary(call => call.Caller + "|" + call.Callee, call => call.Count, StringComparer.Ordinal);

            var expected = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [Fixture + "Direct|XMAT.PublicUtilities::SafeInvoke"] = 1,
                [Fixture + "InLambda|XMAT.PublicUtilities::SafeInvoke"] = 1,
                [Fixture + "InAsyncMethod|XMAT.PublicUtilities::SafeInvoke"] = 2,
                [Fixture + "InLocalFunction|System.Windows.Application::get_Current"] = 1,
                [Fixture + "InIterator|System.Windows.Input.CommandManager::InvalidateRequerySuggested"] = 1,
                [Fixture + "AsDelegate|System.Windows.Input.CommandManager::InvalidateRequerySuggested"] = 1,
                [Fixture + "Dialogs|System.Windows.MessageBox::Show"] = 1,
                [Fixture + "Dialogs|System.Windows.Window::ShowDialog"] = 1,
                [Fixture + "Dialogs|Microsoft.Win32.CommonDialog::ShowDialog"] = 1,
                [Fixture + "Dispatching|System.Windows.Threading.Dispatcher::get_CurrentDispatcher"] = 1,
                [Fixture + "Dispatching|System.Windows.Threading.Dispatcher::CheckAccess"] = 1,
            };

            Assert.Equal(expected.OrderBy(entry => entry.Key), calls.OrderBy(entry => entry.Key));
        }

        [Theory]
        [InlineData("<Run>b__3_0", "Run")]
        [InlineData("<Run>g__Local|3_1", "Run")]
        [InlineData("<Run>d__7", "Run")]
        [InlineData("<<Run>b__3_0>d", "Run")]
        [InlineData("<.ctor>b__0_0", ".ctor")]
        [InlineData("<>c__DisplayClass5_0", "")]
        [InlineData("Run", null)]
        public void UserMemberName_ReadsCompilerGeneratedNames(string name, string expected) =>
            Assert.Equal(expected, ForbiddenCallScanner.UserMemberName(name));

        private static bool GenerationRequested() =>
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(WriteVariable));

        private static bool IsContinuousIntegration() =>
            new[] { "CI", "GITHUB_ACTIONS", "TF_BUILD" }.Any(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)));

        // The product assembly and, once the command line is built, xmat.dll.
        internal static IReadOnlyList<string> DefaultSources()
        {
            var sources = new List<string> { typeof(PublicUtilities).Assembly.Location };
            string cli = RepositoryPaths.FindCliDirectory();
            if (cli != null && File.Exists(Path.Combine(cli, "xmat.dll")))
            {
                sources.Add(Path.Combine(cli, "xmat.dll"));
            }

            return sources;
        }

        private static List<ForbiddenCall> ScanSources(IEnumerable<string> paths) =>
            paths.SelectMany(ForbiddenCallScanner.Scan).ToList();
    }
}
