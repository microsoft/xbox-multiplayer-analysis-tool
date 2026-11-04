// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using XMAT.Cli;

namespace XMAT.Tests.Cli
{
    // The staging folder is created and held open in one step (CliOutputFolder, CliDirectoryHandle), so that it cannot be
    // swapped for another folder, by renames or junctions, between the privacy check and the writes, the publication or
    // the cleanup. These tests attack as the same user, so they show only what Windows does with the folders' names
    // while the staging folder is held; that another account is refused alike is not tested here.
    public class CliOutputFolderAnchorTests
    {
        private const string Results = "results";

        [Fact]
        public async Task WhileHeld_NeitherTheStagingFolderNorItsParentCanBeRenamed()
        {
            using var directory = new CliTestDirectory();
            string parent = Path.Join(directory.Root, "parent");
            Directory.CreateDirectory(parent);
            var report = NewReport();
            using CliOutputFolder output = CliOutputFolder.Reserve(Path.Join(parent, Results), report.RunId);
            Exception parentRename = null;
            Exception stagingRename = null;

            await CliStagedRun.CompleteAsync(
                report,
                output,
                _ =>
                {
                    parentRename = Record.Exception(() => Directory.Move(parent, parent + "-moved"));
                    stagingRename = Record.Exception(() => Directory.Move(output.StagingPath, output.StagingPath + "-moved"));
                    return Produce(report);
                },
                null,
                CancellationToken.None);

            Assert.True(parentRename is IOException or UnauthorizedAccessException, $"{parentRename}");
            Assert.True(stagingRename is IOException or UnauthorizedAccessException, $"{stagingRename}");
            Assert.Equal(CliExitCodes.Success, report.ExitCode);
            Assert.Equal("{}", File.ReadAllText(Path.Join(parent, Results, "summary.json")));
            Assert.Equal(new[] { "parent" }, directory.Entries());
        }

        // --out is reached through a junction that is pointed elsewhere mid-run, at a folder where a staging folder of the
        // same name waits: the writes and the publication still go to the folder that was created, and the decoy gets
        // nothing.
        [Fact]
        public async Task AJunctionPointedElsewhereMidRun_CannotRedirectTheWritesOrThePublication()
        {
            using var directory = new CliTestDirectory();
            (string real, string decoy, string link) = Folders(directory);
            var report = NewReport();
            string decoyStaging = Path.Join(decoy, CliOutputFolder.StagingPrefix + report.RunId);
            try
            {
                using CliOutputFolder output = CliOutputFolder.Reserve(Path.Join(link, Results), report.RunId);

                await CliStagedRun.CompleteAsync(
                    report,
                    output,
                    _ =>
                    {
                        Junction.Retarget(link, decoy);
                        Directory.CreateDirectory(decoyStaging);
                        return Produce(report);
                    },
                    null,
                    CancellationToken.None);

                Assert.Equal(CliExitCodes.Success, report.ExitCode);
                Assert.Equal("{}", File.ReadAllText(Path.Join(real, Results, "summary.json")));
                Assert.Empty(Directory.GetFileSystemEntries(decoyStaging));
                Assert.False(Directory.Exists(Path.Join(decoy, Results)));
            }
            finally
            {
                Junction.Remove(link);
            }
        }

        // The same swap in a run that fails: cleanup removes the folder that was created, and nothing in the decoy.
        [Fact]
        public async Task AfterAJunctionSwap_CleanupRemovesOnlyTheFolderItCreated()
        {
            using var directory = new CliTestDirectory();
            (string real, string decoy, string link) = Folders(directory);
            var report = NewReport();
            string decoyStaging = Path.Join(decoy, CliOutputFolder.StagingPrefix + report.RunId);
            try
            {
                using CliOutputFolder output = CliOutputFolder.Reserve(Path.Join(link, Results), report.RunId);

                await CliStagedRun.CompleteAsync(
                    report,
                    output,
                    _ =>
                    {
                        File.WriteAllText(Path.Join(output.StagingPath, "partial.bin"), "partial");
                        Junction.Retarget(link, decoy);
                        Directory.CreateDirectory(decoyStaging);
                        File.WriteAllText(Path.Join(decoyStaging, "victim.txt"), "keep");
                        return Fail(report);
                    },
                    null,
                    CancellationToken.None);

                Assert.Equal(CliCleanupStates.Completed, report.CleanupState);
                Assert.Empty(Directory.GetFileSystemEntries(real));
                Assert.Equal("keep", File.ReadAllText(Path.Join(decoyStaging, "victim.txt")));
            }
            finally
            {
                Junction.Remove(link);
            }
        }

        // A folder, even one private to the same user, or a junction already where the staging folder would go is never
        // adopted, nor is the folder that a dangling junction points to created: reserving fails, and nothing changes.
        [Theory]
        [InlineData("folder")]
        [InlineData("junction")]
        [InlineData("dangling-junction")]
        public void AFolderOrJunctionAlreadyThere_IsNeverAdopted(string existing)
        {
            using var directory = new CliTestDirectory();
            string runId = Guid.NewGuid().ToString("N");
            string staging = Path.Join(directory.Root, CliOutputFolder.StagingPrefix + runId);
            string victim = Path.Join(directory.Root, "victim");
            string kept = existing == "folder" ? staging : victim;
            if (existing != "dangling-junction")
            {
                new DirectoryInfo(kept).Create(PrivateTo(CurrentUser()));
                File.WriteAllText(Path.Join(kept, "victim.txt"), "keep");
            }

            if (existing != "folder")
            {
                Junction.Create(staging, victim);
            }

            string[] before = directory.Entries();
            try
            {
                // Disposed if it unexpectedly succeeds, so that the folder it holds can be cleaned up.
                Exception reserving = Record.Exception(
                    () => CliOutputFolder.Reserve(Path.Join(directory.Root, Results), runId).Dispose());

                Assert.IsAssignableFrom<IOException>(reserving);
                Assert.Equal(before, directory.Entries());
                if (existing != "dangling-junction")
                {
                    Assert.Equal(new[] { "victim.txt" }, Directory.GetFileSystemEntries(kept).Select(Path.GetFileName));
                }
            }
            finally
            {
                if (existing != "folder")
                {
                    Junction.Remove(staging);
                }
            }
        }

        // A junction in the staging folder or in its work folder is removed itself, whether the run succeeds or fails,
        // never what it points to.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Cleanup_RemovesJunctionsInTheStagingFolder_NeverWhatTheyPointTo(bool succeeds)
        {
            using var directory = new CliTestDirectory();
            string victim = Path.Join(directory.Root, "victim");
            Directory.CreateDirectory(victim);
            File.WriteAllText(Path.Join(victim, "victim.txt"), "keep");
            var report = NewReport();
            using CliOutputFolder output = CliOutputFolder.Reserve(Path.Join(directory.Root, Results), report.RunId);

            await CliStagedRun.CompleteAsync(
                report,
                output,
                _ =>
                {
                    Directory.CreateDirectory(output.WorkPath);
                    Junction.Create(Path.Join(output.WorkPath, "link"), victim);
                    if (!succeeds)
                    {
                        Junction.Create(Path.Join(output.StagingPath, "link"), victim);
                    }

                    return succeeds ? Produce(report) : Fail(report);
                },
                null,
                CancellationToken.None);

            Assert.Equal(CliCleanupStates.Completed, report.CleanupState);
            Assert.Equal(new[] { "victim.txt" }, Directory.GetFileSystemEntries(victim).Select(Path.GetFileName));
            Assert.Equal(succeeds ? new[] { Results, "victim" } : new[] { "victim" }, directory.Entries());
            if (succeeds)
            {
                Assert.Equal(
                    new[] { "summary.json" },
                    Directory.GetFileSystemEntries(Path.Join(directory.Root, Results)).Select(Path.GetFileName));
            }
        }

        // A folder that appears at --out during the run is never replaced: exit code 5, and the staging folder goes.
        [Fact]
        public async Task AFolderThatAppearsAtTheTarget_IsNeverReplaced()
        {
            using var directory = new CliTestDirectory();
            string target = Path.Join(directory.Root, Results);
            var report = NewReport();
            using CliOutputFolder output = CliOutputFolder.Reserve(target, report.RunId);

            await CliStagedRun.CompleteAsync(
                report,
                output,
                _ =>
                {
                    Directory.CreateDirectory(target);
                    File.WriteAllText(Path.Join(target, "theirs.txt"), "keep");
                    return Produce(report);
                },
                null,
                CancellationToken.None);

            Assert.Equal(CliExitCodes.Conflict, report.ExitCode);
            Assert.Equal(new[] { "cli.output.exists" }, report.Errors.Select(error => error.Code));
            Assert.Equal(CliCleanupStates.Completed, report.CleanupState);
            Assert.Equal(new[] { "theirs.txt" }, Directory.GetFileSystemEntries(target).Select(Path.GetFileName));
            Assert.Equal(new[] { Results }, directory.Entries());
        }

        // The commit checks --out first, but a folder or a file can still appear between that check and the rename.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RenamingThroughTheHandle_NeverReplacesWhatIsAlreadyThere(bool file)
        {
            using var directory = new CliTestDirectory();
            string taken = Path.Join(directory.Root, "taken");
            string kept = file ? taken : Path.Join(taken, "theirs.txt");
            if (!file)
            {
                Directory.CreateDirectory(taken);
            }

            File.WriteAllText(kept, "keep");
            string created = Path.Join(directory.Root, "created");

            using (var handle = CliDirectoryHandle.CreateNew(created, PrivateTo(CurrentUser())))
            {
                Assert.ThrowsAny<IOException>(() => CliDirectoryHandle.Rename(handle, taken));
            }

            Assert.Equal("keep", File.ReadAllText(kept));
            Assert.Equal(new[] { "created", "taken" }, directory.Entries());
        }

        // A successful rename is the publication: a failure right after it does not remove the published folder, and
        // neither does a later discard.
        [Fact]
        public async Task APublishedRun_IsNeverRemoved_EvenWhenSomethingFailsRightAfter()
        {
            using var directory = new CliTestDirectory();
            var report = NewReport();
            using CliOutputFolder output = CliOutputFolder.Reserve(Path.Join(directory.Root, Results), report.RunId);
            var observer = new AfterCommitThrows();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                CliStagedRun.CompleteAsync(report, output, _ => Produce(report), observer, CancellationToken.None));

            Assert.True(output.TryDiscard());
            Assert.Equal("{}", File.ReadAllText(Path.Join(directory.Root, Results, "summary.json")));
            Assert.Equal(new[] { Results }, directory.Entries());
        }

        // Disposing lets go of the staging folder without removing it, and a discard after that removes nothing.
        [Fact]
        public void Dispose_LetsGoOfTheStagingFolderWithoutRemovingIt()
        {
            using var directory = new CliTestDirectory();
            CliOutputFolder output = CliOutputFolder.Reserve(Path.Join(directory.Root, Results), Guid.NewGuid().ToString("N"));
            Assert.ThrowsAny<IOException>(() => Directory.Delete(output.StagingPath));

            output.Dispose();

            Assert.False(output.TryDiscard());
            Assert.True(Directory.Exists(output.StagingPath));
            Directory.Delete(output.StagingPath);
        }

        // The owner can always change the folder's access control, so it must be the current user too.
        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public void IsPrivateDescriptor_RequiresTheCurrentUserAsOwner(bool otherOwner, bool expected)
        {
            SecurityIdentifier user = CurrentUser();
            DirectorySecurity security = PrivateTo(user);
            if (otherOwner)
            {
                security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            }

            Assert.Equal(expected, CliOutputFolder.IsPrivateDescriptor(security, user));
        }

        // Only a local NTFS volume: network shares, mapped drives among them, and other file systems are refused.
        [Theory]
        [InlineData("NTFS", @"\Device\HarddiskVolume3\out", true)]
        [InlineData("NTFS", @"\Device\Mup\server\share\out", false)]
        [InlineData("ReFS", @"\Device\HarddiskVolume3\out", false)]
        [InlineData("FAT32", @"\Device\HarddiskVolume3\out", false)]
        [InlineData(null, @"\Device\HarddiskVolume3\out", false)]
        [InlineData("NTFS", null, false)]
        public void IsSupportedStorage_IsOnlyLocalNtfs(string fileSystem, string devicePath, bool expected) =>
            Assert.Equal(expected, CliOutputFolder.IsSupportedStorage(fileSystem, devicePath));

        private static CliReport NewReport() => new(CliCommands.Analyze, Guid.NewGuid().ToString("N"));

        private static Task<IReadOnlyList<CliStagedFile>> Produce(CliReport report)
        {
            report.SetExitCode(CliExitCodes.Success);
            return Task.FromResult<IReadOnlyList<CliStagedFile>>(new[]
            {
                new CliStagedFile("summary", "summary.json", Encoding.UTF8.GetBytes("{}")),
            });
        }

        private static Task<IReadOnlyList<CliStagedFile>> Fail(CliReport report)
        {
            report.Fail(CliExitCodes.InvalidInput, "test.failed", "The test failed the run.");
            return Task.FromResult<IReadOnlyList<CliStagedFile>>(null);
        }

        // A real folder, a decoy folder, and a junction to the real one.
        private static (string Real, string Decoy, string Link) Folders(CliTestDirectory directory)
        {
            string real = Path.Join(directory.Root, "real");
            string decoy = Path.Join(directory.Root, "decoy");
            string link = Path.Join(directory.Root, "link");
            Directory.CreateDirectory(real);
            Directory.CreateDirectory(decoy);
            Junction.Create(link, real);
            return (real, decoy, link);
        }

        private static SecurityIdentifier CurrentUser()
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return identity.User;
        }

        private static DirectorySecurity PrivateTo(SecurityIdentifier user)
        {
            var security = new DirectorySecurity();
            security.SetOwner(user);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                user,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            return security;
        }

        private sealed class AfterCommitThrows : ICliCommitObserver
        {
            public void BeforeCommit()
            {
                // Nothing to do before the commit.
            }

            public void CommitAttemptFailed(int attempt)
            {
                // No attempt is expected to fail.
            }

            public void AfterCommit() => throw new InvalidOperationException("Injected right after the commit.");
        }

        // mklink /J needs no privilege, unlike a symbolic link.
        private static class Junction
        {
            internal static void Create(string link, string target)
            {
                var start = new ProcessStartInfo(Path.Join(Environment.SystemDirectory, "cmd.exe"))
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (string argument in new[] { "/c", "mklink", "/J", link, target })
                {
                    start.ArgumentList.Add(argument);
                }

                using Process process = Process.Start(start);
                string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                process.WaitForExit();
                Assert.True(process.ExitCode == 0, output);
            }

            internal static void Retarget(string link, string target)
            {
                Remove(link);
                Create(link, target);
            }

            // Removes the junction itself, dangling or not; what it points to stays.
            internal static void Remove(string link)
            {
                try
                {
                    Directory.Delete(link);
                }
                catch (DirectoryNotFoundException)
                {
                    // Already gone.
                }
            }
        }
    }
}
