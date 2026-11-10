// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.Operations;
using XMAT.Operations.Capture;
using XMAT.Tests.Fixtures;
using XMAT.Tests.Operations.Capture;
using XMAT.Tests.Proxy;

namespace XMAT.Tests.Cli
{
    // An archive that export-saz may publish (up to 4 GiB) can be larger than xmat analyze's largest input limit
    // (2048 MiB): analyze refuses it before reading, publishes nothing and leaves no staging folder. Gated with the
    // other S2 resource legs because it writes about 2.1 GiB.
    [Collection(IsolatedProxyTests.Name)]
    public class XmatAnalyzeExportedSazLimitTests
    {
        [Fact]
        public async Task Analyze_ExportedSazOverTheLargestInputLimit_Returns3AndPublishesNothing()
        {
            if (!SazExportBudgetTests.GateEnabled())
            {
                return;
            }

            using var directory = new CliTestDirectory();
            string input = Path.Combine(directory.Root, "large.saz");
            OperationResult<SazExportResult> exported = SazExportBudgetTests.PublishToTarget(
                SazExportBudgetTests.HeavySource(17, 0, SazExportBudgetTests.SharedBody()),
                SazExportKind.Plain,
                SazExportBudgets.Default,
                input);
            Assert.True(exported.Succeeded);
            Assert.True(new FileInfo(input).Length > 2048L * 1024 * 1024);

            XmatRun run = await XmatProcess.RunAsync(
                directory.Root, "analyze", input, "--out", "results", "--max-input-mib", "2048");

            CliAssert.ExitCode(3, run);
            Assert.Equal(new[] { "analysis.input.too-large" }, run.ErrorCodes());
            Assert.False(Directory.Exists(Path.Combine(directory.Root, "results")));
            CliAssert.NoStagingFolders(directory.Root);
        }
    }
}
