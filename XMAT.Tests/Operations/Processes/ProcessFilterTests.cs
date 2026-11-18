// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using XMAT.Operations;
using XMAT.Operations.Processes;

namespace XMAT.Tests.Operations.Processes
{
    public sealed class ProcessFilterTests
    {
        [Fact]
        public void Matches_RequiresAnIncludeAndLetsAnExcludeWin()
        {
            ProcessFilter filter = Compile(new[] { "game*.exe" }, new[] { "game-helper.exe" });

            Assert.True(filter.Matches(Local("GameClient.EXE", 101)));
            Assert.False(filter.Matches(Local("game-helper.exe", 102)));
            Assert.False(filter.Matches(Local("browser.exe", 103)));
        }

        [Theory]
        [InlineData("game", "GAME.EXE")]
        [InlineData("game.exe", "game")]
        [InlineData("game*.exe", "GameClient.exe")]
        [InlineData("g?me.exe", "GAME.EXE")]
        public void Matches_UsesCaseInsensitiveBasenameGlobsAndOptionalExe(string selector, string imageName)
        {
            ProcessFilter filter = Compile(new[] { selector }, Array.Empty<string>());

            Assert.True(filter.Matches(Local(imageName, 110)));
        }

        [Fact]
        public void Matches_PidOnlyIdentityMatchesOnlyNumericSelectors()
        {
            var pidOnly = new ClientProcessInfo(string.Empty, 4242, null, ClientProcessStatus.Local);

            Assert.True(Compile(["4242"], []).Matches(pidOnly));
            Assert.False(Compile(["*"], []).Matches(pidOnly));
            Assert.False(Compile(["game.exe"], []).Matches(pidOnly));
        }

        [Theory]
        [InlineData(ClientProcessStatus.NotRecorded)]
        [InlineData(ClientProcessStatus.Remote)]
        [InlineData(ClientProcessStatus.Unresolved)]
        public void Matches_UnknownNeverMatchesEvenWildcard(ClientProcessStatus status)
        {
            var unknown = new ClientProcessInfo(string.Empty, 0, null, status);

            Assert.False(Compile(["*"], []).Matches(unknown));
        }

        [Fact]
        public void Matches_EmptySpecificationIncludesUnknown()
        {
            ProcessFilter filter = ProcessFilter.Compile(null);

            Assert.True(filter.Matches(ClientProcessInfo.NotRecorded));
        }

        [Theory]
        [MemberData(nameof(InvalidSelectors))]
        public void Parse_RejectsMalformedSelectorsWithoutEchoingThem(string selector)
        {
            OperationResult<ProcessFilterSpec> result = ProcessFilter.Parse(new[] { selector }, Array.Empty<string>());

            Assert.Equal(OperationOutcome.InvalidInput, result.Outcome);
            if (string.IsNullOrWhiteSpace(selector))
            {
                OperationDiagnostic diagnostic = Assert.Single(result.Diagnostics);
                Assert.Equal("process-filter.invalid-selector", diagnostic.Code);
                Assert.Equal("A process selector is invalid.", diagnostic.Message);
                return;
            }
            Assert.All(
                result.Diagnostics,
                diagnostic => Assert.DoesNotContain(selector, diagnostic.Message, StringComparison.Ordinal));
        }

        [Fact]
        public void Parse_BoundsSelectorCountAt64PerSide()
        {
            string[] selectors = Enumerable.Range(1, 65).Select(index => $"game-{index}.exe").ToArray();

            OperationResult<ProcessFilterSpec> result = ProcessFilter.Parse(selectors, Array.Empty<string>());

            Assert.Equal(OperationOutcome.InvalidInput, result.Outcome);
        }

        [Fact]
        public void Parse_BoundsSelectorLengthAt260Characters()
        {
            string selector = new string('a', 257) + ".exe";

            OperationResult<ProcessFilterSpec> result = ProcessFilter.Parse(new[] { selector }, Array.Empty<string>());

            Assert.Equal(OperationOutcome.InvalidInput, result.Outcome);
        }

        [Fact]
        public void Compile_RevalidatesExternallyConstructedSpecifications()
        {
            var invalid = new ProcessFilterSpec(
                ImmutableArray.Create(@"C:\private\game.exe"),
                ImmutableArray<string>.Empty);

            Assert.Throws<ArgumentException>(() => ProcessFilter.Compile(invalid));
        }

        public static IEnumerable<object[]> InvalidSelectors()
        {
            yield return [string.Empty];
            yield return ["0"];
            yield return ["-1"];
            yield return ["2147483648"];
            yield return [@"C:\private\game.exe"];
            yield return ["folder/game.exe"];
            yield return ["game:name.exe"];
            yield return ["game\0.exe"];
        }

        private static ProcessFilter Compile(IEnumerable<string> include, IEnumerable<string> exclude)
        {
            OperationResult<ProcessFilterSpec> result = ProcessFilter.Parse(include, exclude);
            Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
            return ProcessFilter.Compile(result.Value);
        }

        private static ClientProcessInfo Local(string name, int processId) => new(
            name,
            processId,
            DateTimeOffset.Parse("2026-11-03T20:00:00.0000000+00:00", System.Globalization.CultureInfo.InvariantCulture),
            ClientProcessStatus.Local);
    }
}
