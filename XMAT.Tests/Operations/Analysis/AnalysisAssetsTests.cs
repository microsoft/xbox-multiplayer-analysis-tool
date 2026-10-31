// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Reflection;
using System.Text.Json;
using CaptureAnalysisEngine;
using XMAT.Operations;
using XMAT.Operations.Analysis;
using XMAT.Tests.Fixtures;

namespace XMAT.Tests.Operations.Analysis
{
    public class AnalysisAssetsTests
    {
        [Fact]
        public void Load_ValidAssets_ReturnsReusableSetWithoutWriting()
        {
            using var directory = new TestDirectory();
            SyntheticAnalysisAssets.CopyShipped(directory.Path);
            var before = Directory.GetFileSystemEntries(
                directory.Path,
                "*",
                SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();

            var assets = AnalysisAssets.Load(directory.Path);
            var checks = AnalysisAssets.Check(directory.Path);
            var first = assets.CreateAnalyzer(false);
            var second = assets.CreateAnalyzer(false);

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.NotSame(first, second);
            Assert.All(checks, check => Assert.True(check.Passed));
            Assert.Equal(
                before,
                Directory.GetFileSystemEntries(
                    directory.Path,
                    "*",
                    SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.Ordinal));
        }

        [Fact]
        public async Task Load_ShippedAssets_AreIdenticalAcrossPolicies()
        {
            using var directory = new TestDirectory();
            SyntheticAnalysisAssets.CopyShipped(directory.Path);
            var input = SyntheticCaptures.WriteSaz(
                directory.Path,
                "shipped-assets.saz",
                new SyntheticSazOptions(
                    Consoles: 2,
                    RequestsPerConsole: 3,
                    IncludeXr049Violation: true));

            var strict = await Analyze(
                AnalysisAssets.Load(
                    directory.Path,
                    AnalysisAssetLoadPolicy.Strict),
                input);
            var gui = await Analyze(
                AnalysisAssets.Load(
                    directory.Path,
                    AnalysisAssetLoadPolicy.GuiCompatible),
                input);

            AssertEquivalent(strict, gui);
        }

        [Fact]
        public async Task Load_GuiCompatiblePolicy_PreservesLegacyHandEditedBehavior()
        {
            using var directory = new TestDirectory();
            SyntheticAnalysisAssets.CopyShipped(directory.Path);
            var shipped = AnalysisAssets.Load(
                directory.Path,
                AnalysisAssetLoadPolicy.Strict);
            SyntheticAnalysisAssets.AddHandEditedRules(
                directory.Path);
            SyntheticAnalysisAssets.AddMalformedApiMapLine(
                directory.Path);
            var handEdited = AnalysisAssets.Load(
                directory.Path,
                AnalysisAssetLoadPolicy.GuiCompatible);
            IReadOnlyList<Rule> shippedRules = LoadedRules(shipped);
            IReadOnlyList<Rule> handEditedRules = LoadedRules(
                handEdited);
            var input = SyntheticCaptures.WriteSaz(
                directory.Path,
                "hand-edited-assets.saz",
                new SyntheticSazOptions(
                    RequestsPerConsole: 3,
                    IncludeXr049Violation: true));

            var expected = await Analyze(shipped, input);
            var actual = await Analyze(handEdited, input);

            Assert.Equal(
                shippedRules.Count + 2,
                handEditedRules.Count);
            Rule keptWithoutEndpoint = Assert.Single(
                handEditedRules,
                rule => rule.Name == "HandEditedMissingEndpoint");
            Assert.Null(keptWithoutEndpoint.Endpoint);
            Rule keptWithoutName = Assert.Single(
                handEditedRules,
                rule => rule.Name == null &&
                    rule.Endpoint == "*");
            Assert.Null(keptWithoutName.Name);
            Assert.DoesNotContain(
                handEditedRules,
                rule => rule.Name is
                    "HandEditedUnknownType" or
                    "HandEditedMissingType");
            AssertEquivalent(expected, actual);
        }

        [Theory]
        [InlineData(
            "StatsRecorderRule",
            "HandEditedMissingEndpoint",
            null)]
        [InlineData(
            "StatsRecorderRule",
            null,
            "*")]
        [InlineData(
            "UnknownHandEditedRule",
            "HandEditedUnknownType",
            "*")]
        [InlineData(
            null,
            "HandEditedMissingType",
            "*")]
        public void Load_StrictPolicy_RejectsEachHandEditedRule(
            string type,
            string name,
            string endpoint)
        {
            using var directory = new TestDirectory();
            SyntheticAnalysisAssets.CopyShipped(directory.Path);
            SyntheticAnalysisAssets.AddHandEditedRule(
                directory.Path,
                type,
                name,
                endpoint);

            var exception = Assert.Throws<AnalysisAssetException>(
                () => AnalysisAssets.Load(
                    directory.Path,
                    AnalysisAssetLoadPolicy.Strict));
            var check = Assert.Single(
                AnalysisAssets.Check(directory.Path),
                item => item.Id == "data.rules");

            Assert.Equal(
                "analysis.assets.rules",
                exception.Code);
            Assert.False(check.Passed);
            Assert.Equal(
                exception.Code,
                check.Code);
        }

        [Fact]
        public void Load_StrictPolicy_RejectsMalformedApiMapLine()
        {
            using var directory = new TestDirectory();
            SyntheticAnalysisAssets.CopyShipped(directory.Path);
            SyntheticAnalysisAssets.AddMalformedApiMapLine(
                directory.Path);

            var exception = Assert.Throws<AnalysisAssetException>(
                () => AnalysisAssets.Load(
                    directory.Path,
                    AnalysisAssetLoadPolicy.Strict));
            var check = Assert.Single(
                AnalysisAssets.Check(directory.Path),
                item => item.Id == "data.apimap");

            Assert.Equal(
                "analysis.assets.apimap",
                exception.Code);
            Assert.False(check.Passed);
            Assert.Equal(
                exception.Code,
                check.Code);
        }

        [Fact]
        public void Load_InvalidJson_FailsUnderBothPolicies()
        {
            using var directory = new TestDirectory();
            SyntheticAnalysisAssets.CopyShipped(directory.Path);
            File.WriteAllText(
                System.IO.Path.Combine(
                    directory.Path,
                    "Data",
                    "XboxLiveTraceAnalyzer.Rules.json"),
                "not json");

            var strict = Assert.Throws<AnalysisAssetException>(
                () => AnalysisAssets.Load(
                    directory.Path,
                    AnalysisAssetLoadPolicy.Strict));
            var gui = Assert.Throws<AnalysisAssetException>(
                () => AnalysisAssets.Load(
                    directory.Path,
                    AnalysisAssetLoadPolicy.GuiCompatible));

            Assert.Equal(
                "analysis.assets.rules",
                strict.Code);
            Assert.Equal(
                "analysis.assets.invalid",
                gui.Code);
        }

        [Fact]
        public void Check_MalformedRules_ReturnsFailureWithoutThrowing()
        {
            using var directory = new TestDirectory();
            SyntheticAnalysisAssets.CopyShipped(directory.Path);
            File.WriteAllText(
                System.IO.Path.Combine(
                    directory.Path,
                    "Data",
                    "XboxLiveTraceAnalyzer.Rules.json"),
                "not json");

            var checks = AnalysisAssets.Check(directory.Path);

            var rules = Assert.Single(
                checks,
                check => check.Id == "data.rules");
            Assert.False(rules.Passed);
            Assert.Equal("analysis.assets.rules", rules.Code);
        }

        [Fact]
        public void Load_MissingAssets_ThrowsDataFreeException()
        {
            using var directory = new TestDirectory();

            var exception = Assert.Throws<AnalysisAssetException>(
                () => AnalysisAssets.Load(directory.Path));

            Assert.StartsWith("analysis.assets.", exception.Code);
            Assert.DoesNotContain(
                directory.Path,
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
        }

        private static Task<OperationResult<AnalysisResult>> Analyze(
            AnalysisAssetSet assets,
            string input)
        {
            return new AnalyzeCaptureOperation(assets).ExecuteAsync(
                new AnalyzeCaptureRequest(
                    AnalysisInput.FromFile(input),
                    new AnalysisOptions()),
                OperationContext.None,
                CancellationToken.None);
        }

        private static IReadOnlyList<Rule> LoadedRules(
            AnalysisAssetSet assets)
        {
            var analyzer = assets.CreateAnalyzer(false);
            FieldInfo field = typeof(TraceAnalyzer).GetField(
                "m_rules",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            return Assert.IsAssignableFrom<IReadOnlyList<Rule>>(
                field.GetValue(analyzer));
        }

        private static void AssertEquivalent(
            OperationResult<AnalysisResult> expected,
            OperationResult<AnalysisResult> actual)
        {
            Assert.Equal(
                expected.Outcome,
                actual.Outcome);
            Assert.Equal(
                OperationOutcome.Succeeded,
                expected.Outcome);
            Assert.Equal(
                expected.Diagnostics.Select(
                    diagnostic =>
                        $"{diagnostic.Severity}|{diagnostic.Code}|{diagnostic.Message}"),
                actual.Diagnostics.Select(
                    diagnostic =>
                        $"{diagnostic.Severity}|{diagnostic.Code}|{diagnostic.Message}"));
            Assert.Equal(
                JsonSerializer.Serialize(expected.Value?.Summary),
                JsonSerializer.Serialize(actual.Value?.Summary));
        }

        private sealed class TestDirectory : IDisposable
        {
            internal TestDirectory()
            {
                Path = System.IO.Path.Combine(
                    AppContext.BaseDirectory,
                    "HeadlessTestArtifacts",
                    $"XMAT-analysis-assets-{Guid.NewGuid():N}");
                Directory.CreateDirectory(Path);
            }

            internal string Path { get; }

            public void Dispose()
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, true);
                }
            }
        }
    }
}
