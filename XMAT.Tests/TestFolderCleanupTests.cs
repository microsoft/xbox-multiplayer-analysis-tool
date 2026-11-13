// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Security.AccessControl;
using XMAT.Tests.Fixtures;

namespace XMAT.Tests
{
    public class TestFolderCleanupTests
    {
        // A file that is held while the first delete runs, as a scanner can hold one, is let go before the next: the folder
        // goes, and nothing is thrown.
        [Fact]
        public void AFileHeldForAMoment_IsDeletedOnALaterAttempt()
        {
            string folder = NewFolder();
            FileStream held = Hold(folder);
            var attempts = new List<int>();

            TestFolderCleanup.Delete(
                folder,
                attempt =>
                {
                    attempts.Add(attempt);
                    if (attempt == 2)
                    {
                        held.Dispose();
                    }
                },
                attempts: 3,
                pause: TimeSpan.Zero);

            Assert.Equal(new[] { 1, 2 }, attempts);
            Assert.False(Directory.Exists(folder));
        }

        // A file that stays held, as a handle that a test leaked does, still fails the cleanup once every attempt is spent.
        [Fact]
        public void AFileThatStaysHeld_FailsTheCleanup_AfterTheLastAttempt()
        {
            string folder = NewFolder();
            var attempts = new List<int>();
            try
            {
                using (Hold(folder))
                {
                    Assert.Throws<IOException>(() => TestFolderCleanup.Delete(
                        folder, attempts.Add, attempts: 3, pause: TimeSpan.Zero));
                }

                Assert.Equal(new[] { 1, 2, 3 }, attempts);
                Assert.True(Directory.Exists(folder));
            }
            finally
            {
                TestFolderCleanup.Delete(folder);
            }
        }

        // By default the attempts are 100 ms apart, and there are enough of them for a file held through two to be deleted.
        [Fact]
        public void ByDefault_TheAttemptsWaitBetweenThem()
        {
            string folder = NewFolder();
            FileStream held = Hold(folder);
            var attempts = new List<int>();
            var clock = System.Diagnostics.Stopwatch.StartNew();

            TestFolderCleanup.Delete(
                folder,
                attempt =>
                {
                    attempts.Add(attempt);
                    if (attempt == 3)
                    {
                        held.Dispose();
                    }
                });

            Assert.Equal(new[] { 1, 2, 3 }, attempts);
            Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(150), clock.Elapsed.ToString());
            Assert.False(Directory.Exists(folder));
        }

        // An access error, from the delete or from prepare, is tried again like a sharing error.
        [Fact]
        public void AnAccessErrorFromPrepare_IsTriedAgain()
        {
            string folder = NewFolder();
            var attempts = new List<int>();

            TestFolderCleanup.Delete(
                folder,
                attempt =>
                {
                    attempts.Add(attempt);
                    if (attempt == 1)
                    {
                        throw new UnauthorizedAccessException("denied for the test");
                    }
                },
                attempts: 3,
                pause: TimeSpan.Zero);

            Assert.Equal(new[] { 1, 2 }, attempts);
            Assert.False(Directory.Exists(folder));
        }

        // An analysis report fixture whose own folder denies the current user listing it is still deleted: the first
        // attempt fails to clear its attributes, and the next removes that deny rule first.
        [Fact]
        public void AnOwnedFolderThatDeniesListing_IsDeleted()
        {
            string root = AnalysisReportTestPaths.CreateOwnedDirectory("xtc");
            File.WriteAllText(Path.Combine(root, "file.txt"), "x");
            var deny = new DirectoryAccessDenyScope(root, FileSystemRights.ListDirectory);
            try
            {
                AnalysisReportTestPaths.DeleteOwnedDirectory(root);

                Assert.False(Directory.Exists(root));
            }
            finally
            {
                deny.Dispose();
                TestFolderCleanup.Delete(root);
            }
        }

        // A file held through the first attempt is deleted by a later one, by default after a pause; one held throughout
        // fails the cleanup.
        [Fact]
        public void AHeldFile_IsDeletedOnceLetGo_AndFailsTheCleanupWhileHeld()
        {
            string folder = NewFolder();
            string path = Path.Combine(folder, "held.bin");
            var attempts = new List<int>();
            try
            {
                using (Hold(folder))
                {
                    Assert.Throws<IOException>(() => TestFolderCleanup.DeleteFile(
                        path, attempts.Add, attempts: 2, pause: TimeSpan.Zero));
                    Assert.True(File.Exists(path));
                }

                Assert.Equal(new[] { 1, 2 }, attempts);
                attempts.Clear();
                FileStream held = Hold(folder, FileMode.Open);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                TestFolderCleanup.DeleteFile(
                    path,
                    attempt =>
                    {
                        attempts.Add(attempt);
                        if (attempt == 2)
                        {
                            held.Dispose();
                        }
                    });

                Assert.Equal(new[] { 1, 2 }, attempts);
                Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(50), clock.Elapsed.ToString());
                Assert.False(File.Exists(path));
            }
            finally
            {
                TestFolderCleanup.Delete(folder);
            }
        }

        private static string NewFolder()
        {
            string folder = Path.Combine(AppContext.BaseDirectory, "CleanupTestArtifacts", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            return folder;
        }

        // Opens a file in the folder that nothing else may open, delete included, until it is disposed.
        private static FileStream Hold(string folder, FileMode mode = FileMode.CreateNew) =>
            new(Path.Combine(folder, "held.bin"), mode, FileAccess.ReadWrite, FileShare.None);
    }
}
