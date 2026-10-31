// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

namespace XMAT.Tests.HeadlessParity
{
    // Locates the repository and build outputs from the test binaries, so checks that compare checked-in files
    // (docs/cli.md, IL baseline) and the command-line build work from any configuration.
    internal static class RepositoryPaths
    {
        internal const string SolutionFileName = "XboxMultiplayerAnalysisTool.sln";
        internal const string CliDirectoryVariable = "XMAT_CLI_DIR";

        private static readonly Lazy<string> RootDirectory = new(FindRoot);

        internal static string Root => RootDirectory.Value;

        // The folder that holds the built xmat.exe, or null when the command line has not been built. The test
        // binaries live in XMAT.Tests\bin\<platform>\<configuration>\<framework>\<runtime>\, and the command line
        // builds to the same relative folder under XMAT.Cli.
        internal static string FindCliDirectory()
        {
            string configured = Environment.GetEnvironmentVariable(CliDirectoryVariable);
            if (!string.IsNullOrEmpty(configured))
            {
                return Directory.Exists(configured) ? Path.GetFullPath(configured) : null;
            }

            string testDirectory = AppContext.BaseDirectory;
            if (File.Exists(Path.Combine(testDirectory, "xmat.exe")))
            {
                return testDirectory;
            }

            string relative = Path.GetRelativePath(Path.Combine(Root, "XMAT.Tests"), testDirectory);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                return null;
            }

            string candidate = Path.Combine(Root, "XMAT.Cli", relative);
            return File.Exists(Path.Combine(candidate, "xmat.exe")) ? Path.GetFullPath(candidate) : null;
        }

        private static string FindRoot()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
                {
                    return directory.FullName;
                }
            }

            throw new InvalidOperationException(
                $"Could not find {SolutionFileName} in {AppContext.BaseDirectory} or any parent folder.");
        }
    }
}
