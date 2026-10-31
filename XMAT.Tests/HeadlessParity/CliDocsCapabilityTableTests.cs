// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.Operations.Capabilities;

namespace XMAT.Tests.HeadlessParity
{
    // docs/cli.md shows the capability registry between its xmat-capabilities markers; this fails when the two disagree.
    public class CliDocsCapabilityTableTests
    {
        private const string DocsFile = "docs/cli.md";

        [Fact]
        public void DocsCapabilityTable_MatchesTheRegistry()
        {
            string docs = File.ReadAllText(Path.Combine(RepositoryPaths.Root, "docs", "cli.md"));
            string expected = CapabilityTable.RenderMarkdown(CapabilityRegistry.All);

            string actual = Between(docs, CapabilityTable.BeginMarker, CapabilityTable.EndMarker);

            Assert.True(
                Normalize(actual) == Normalize(expected),
                $"{DocsFile}'s capability table is out of date. Replace the lines between " +
                $"{CapabilityTable.BeginMarker} and {CapabilityTable.EndMarker} with:{Environment.NewLine}{expected}");
        }

        [Fact]
        public void RenderedTable_EscapesPipesInsideCells()
        {
            string table = CapabilityTable.RenderMarkdown(new[]
            {
                Capability.Supported("capture.sample", "Sample", "S2", cli: "convert IN [--sanitize | --raw]"),
            });

            Assert.Contains("`xmat convert IN [--sanitize \\| --raw]`", table, StringComparison.Ordinal);
            Assert.All(
                table.Split('\n', StringSplitOptions.RemoveEmptyEntries),
                line => Assert.Equal(4, line.Replace("\\|", string.Empty, StringComparison.Ordinal).Count(c => c == '|')));
        }

        private static string Between(string text, string begin, string end)
        {
            int start = text.IndexOf(begin, StringComparison.Ordinal);
            int stop = start < 0 ? -1 : text.IndexOf(end, start + begin.Length, StringComparison.Ordinal);
            Assert.True(start >= 0 && stop > start, $"{DocsFile} must contain {begin} followed by {end}.");
            return text.Substring(start + begin.Length, stop - start - begin.Length);
        }

        private static string Normalize(string text) =>
            text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\n', ' ', '\r');
    }
}
