// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Reflection;
using System.Text.RegularExpressions;
using XMAT.Tests.HeadlessParity;

namespace XMAT.Tests
{
    public class InstalledAgentGuideTests
    {
        [Fact]
        public void DesktopAndConsoleOutputsContainTheSameInstallRelativeGuidanceAndLaunchBinaries()
        {
            var configuration = typeof(InstalledAgentGuideTests).Assembly
                .GetCustomAttribute<AssemblyConfigurationAttribute>().Configuration;
            var desktop = Path.Combine(RepositoryPaths.Root, "XMAT", "bin", "x64", configuration,
                "net10.0-windows10.0.17763.0", "win-x64");
            var cli = RepositoryPaths.FindCliDirectory();
            foreach (var root in new[] { desktop, cli })
            {
                foreach (var relative in new[]
                {
                    "AGENTS.md", "CLAUDE.md", "GEMINI.md", @"docs\agent-access.md", @"docs\cli.md",
                    @"docs\sql-workspace.md", "XboxMultiplayerAnalysisTool.exe", "xmat.exe",
                    "xmat.dll", "xmat.deps.json", "xmat.runtimeconfig.json",
                    @"AgentBridge\XMAT.AgentBridge.exe", @"Resources\Sql\AvalonEdit.LICENSE.txt"
                })
                    Assert.True(File.Exists(Path.Combine(root, relative)), $"Missing distributed artifact: {relative}");
                var guide = File.ReadAllText(Path.Combine(root, "AGENTS.md"));
                foreach (Match link in Regex.Matches(guide, @"\[[^\]]+\]\((?<target>[^)]+)\)"))
                {
                    var target = link.Groups["target"].Value.Split('#')[0].Replace('/', '\\');
                    Assert.True(File.Exists(Path.Combine(root, target)), $"Broken installed guide link: {target}");
                }
                Assert.Equal("@AGENTS.md", File.ReadAllText(Path.Combine(root, "CLAUDE.md")).Trim());
                Assert.Equal("@./AGENTS.md", File.ReadAllText(Path.Combine(root, "GEMINI.md")).Trim());
                Assert.False(File.Exists(Path.Combine(root, ".mcp.json")));
            }
            Assert.Equal(File.ReadAllText(Path.Combine(desktop, "AGENTS.md")), File.ReadAllText(Path.Combine(cli, "AGENTS.md")));
        }
    }
}
