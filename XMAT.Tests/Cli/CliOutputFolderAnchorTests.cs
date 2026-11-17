// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using XMAT.Cli;
using XMAT.Operations.Storage;

namespace XMAT.Tests.Cli
{
    // --out is admitted without following anything, and the staging folder is created and held relative to the held
    // folders above it (CliOutputFolder, AnchoredHandles): no rename, junction, symbolic link or name that Windows would
    // read otherwise sends the writes, the publication or the cleanup elsewhere. These tests attack as the same user, so
    // they show what Windows does with the folders while xmat holds them; that another account is refused alike is not
    // tested here. Nothing reaches a network: 192.0.2.1 is reserved for documentation and never routed.
    public class CliOutputFolderAnchorTests
    {
        private const string Results = "results";
        private const int ErrorDirectoryNotEmpty = 145;

        [Fact]
        public async Task WhileHeld_NeitherTheStagingFolderNorTheFoldersAboveItCanBeRenamed()
        {
            using var directory = new CliTestDirectory();
            string parent = Path.Join(directory.Root, "parent");
            Directory.CreateDirectory(parent);
            var report = NewReport();
            using CliOutputFolder output = CliTestOutput.Reserve(Path.Join(parent, Results), report.RunId);
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

        // A folder above --out is held from the moment it is checked, before the staging folder exists: it cannot be
        // renamed away while the folders below it are opened.
        [Fact]
        public void AFolderAboveTheOutFolder_CannotBeRenamedOnceItIsHeld()
        {
            using var directory = new CliTestDirectory();
            string parent = Path.Join(directory.Root, "parent");
            Directory.CreateDirectory(parent);
            Exception rename = null;

            using CliOutputFolder output = CliTestOutput.Reserve(
                Path.Join(parent, Results),
                Guid.NewGuid().ToString("N"),
                name =>
                {
                    if (name == "parent")
                    {
                        rename = Record.Exception(() => Directory.Move(parent, parent + "-moved"));
                    }
                });

            Assert.True(rename is IOException or UnauthorizedAccessException, $"{rename}");
            Assert.True(output.TryDiscard());
            Assert.Equal(new[] { "parent" }, directory.Entries());
            Assert.Empty(Directory.GetFileSystemEntries(parent));
        }

        // A junction anywhere above --out, dangling or not, is refused before anything is created, and never followed.
        [Theory]
        [InlineData("parent")]
        [InlineData("ancestor")]
        [InlineData("dangling")]
        public void AJunctionAboveTheOutFolder_IsRefusedAndNeverFollowed(string where)
        {
            using var directory = new CliTestDirectory();
            string real = Path.Join(directory.Root, "real");
            Directory.CreateDirectory(Path.Join(real, "sub"));
            string link = Path.Join(directory.Root, "link");
            Links.Junction(link, where == "dangling" ? Path.Join(directory.Root, "missing") : real);
            try
            {
                (CliFailure failure, Exception error) = CliTestOutput.Attempt(
                    where == "ancestor" ? Path.Join(link, "sub", Results) : Path.Join(link, Results));

                Assert.Null(error);
                Assert.Equal(("cli.output.unprotected", CliExitCodes.Failed), (failure?.Code, failure?.ExitCode));
                Assert.Equal(new[] { "sub" }, Directory.GetFileSystemEntries(real).Select(Path.GetFileName));
                Assert.Empty(Directory.GetFileSystemEntries(Path.Join(real, "sub")));
                Assert.Equal(new[] { "link", "real" }, directory.Entries());
            }
            finally
            {
                Links.Remove(link);
            }
        }

        // The same for a symbolic link, even one to a network share, which following would make Windows sign in to: the
        // link is refused without being opened through, so the refusal is immediate.
        [SymbolicLinkTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void ASymbolicLinkAboveTheOutFolder_IsRefusedAndNeverFollowed(bool remote)
        {
            using var directory = new CliTestDirectory();
            string real = Path.Join(directory.Root, "real");
            Directory.CreateDirectory(real);
            string link = Path.Join(directory.Root, "link");
            Links.SymbolicLink(link, remote ? @"\\192.0.2.1\share" : real);
            try
            {
                var watch = Stopwatch.StartNew();
                (CliFailure failure, Exception error) = CliTestOutput.Attempt(Path.Join(link, Results));

                Assert.Null(error);
                Assert.Equal("cli.output.unprotected", failure?.Code);
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"{watch.Elapsed}");
                Assert.Empty(Directory.GetFileSystemEntries(real));
            }
            finally
            {
                Links.Remove(link);
            }
        }

        // An empty parent that becomes a junction or a symbolic link right after xmat checked it, or right before xmat
        // creates the staging folder in it: nothing can be created through the folder that xmat holds, so reserving fails,
        // and what the link points to is never touched, not even for a moment.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AParentThatBecomesAJunctionAfterItWasChecked_IsNeverWrittenThrough(bool beforeCreating) =>
            ConversionRace(symbolic: false, beforeCreating);

        [SymbolicLinkTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void AParentThatBecomesASymbolicLinkAfterItWasChecked_IsNeverWrittenThrough(bool beforeCreating) =>
            ConversionRace(symbolic: true, beforeCreating);

        // Once the staging folder exists, no folder above it can become a junction or a symbolic link: each contains the
        // next, and Windows turns only empty folders into links.
        [Fact]
        public void AParentThatHoldsTheStagingFolder_CannotBecomeAJunction() => NoConversion(symbolic: false);

        [SymbolicLinkFact]
        public void AParentThatHoldsTheStagingFolder_CannotBecomeASymbolicLink() => NoConversion(symbolic: true);

        // A folder whose name ends with a dot can be reached only by a verbatim path (\\?\...); Windows removes that dot
        // from an ordinary path, which then names another folder. xmat uses verbatim paths only, so the writes, the
        // publication and the cleanup stay in the folder it created, and the decoy that the ordinary path names stays
        // empty. A trailing space, which Windows keeps in the folders above, behaves the same.
        [Theory]
        [InlineData("real.", true)]
        [InlineData("real.", false)]
        [InlineData("real ", true)]
        public async Task ATrailingDotOrSpace_NeverRedirectsTheRunToAnotherFolder(string name, bool succeeds)
        {
            using var directory = new CliTestDirectory();
            string real = Path.Join(@"\\?\" + directory.Root, name);
            string decoy = Path.Join(directory.Root, "real");
            Directory.CreateDirectory(real);
            Directory.CreateDirectory(decoy);
            var report = NewReport();
            try
            {
                using CliOutputFolder output = CliTestOutput.Reserve(Path.Join(real, Results), report.RunId);

                await CliStagedRun.CompleteAsync(
                    report,
                    output,
                    _ =>
                    {
                        Directory.CreateDirectory(output.WorkPath);
                        File.WriteAllText(Path.Join(output.WorkPath, "work.bin"), "work");
                        return succeeds ? Produce(report) : Fail(report);
                    },
                    null,
                    CancellationToken.None);

                Assert.Equal(CliCleanupStates.Completed, report.CleanupState);
                Assert.Empty(Directory.GetFileSystemEntries(decoy));
                Assert.Equal(
                    succeeds ? new[] { Results } : Array.Empty<string>(),
                    Directory.GetFileSystemEntries(real).Select(Path.GetFileName));
                if (succeeds)
                {
                    Assert.Equal(
                        new[] { "summary.json" },
                        Directory.GetFileSystemEntries(Path.Join(real, Results)).Select(Path.GetFileName));
                }
            }
            finally
            {
                // Only its verbatim path names it; an ordinary one would remove the decoy instead.
                Directory.Delete(real, recursive: true);
            }
        }

        // --out given by its volume's name rather than a drive letter is used as it is, and every path xmat uses is a
        // verbatim volume path, never one that a drive letter or mounted folder could change.
        [Fact]
        public async Task AVolumePath_IsUsedAsItIs()
        {
            using var directory = new CliTestDirectory();
            var report = NewReport();
            string output = Path.Join(Volumes.NameOf(directory.Root), Volumes.Relative(directory.Root), Results);
            using CliOutputFolder folder = CliTestOutput.Reserve(output, report.RunId);

            await CliStagedRun.CompleteAsync(report, folder, _ => Produce(report), null, CancellationToken.None);

            Assert.StartsWith(Volumes.NameOf(directory.Root), folder.StagingPath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(CliExitCodes.Success, report.ExitCode);
            Assert.Equal("{}", File.ReadAllText(Path.Join(directory.Root, Results, "summary.json")));
            Assert.Equal(new[] { Results }, directory.Entries());
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
                Links.Junction(staging, victim);
            }

            string[] before = directory.Entries();
            try
            {
                Assert.Null(CliOutputFolder.CheckTarget(Path.Join(directory.Root, Results), out CliOutputTarget target));
                Exception reserving = Record.Exception(() =>
                {
                    CliOutputFolder.TryReserve(target, runId, out CliOutputFolder folder);
                    folder?.Dispose();
                });

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
                    Links.Remove(staging);
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
            string staging = Path.Join(directory.Root, CliOutputFolder.StagingPrefix + report.RunId);
            using CliOutputFolder output = CliTestOutput.Reserve(Path.Join(directory.Root, Results), report.RunId);

            await CliStagedRun.CompleteAsync(
                report,
                output,
                _ =>
                {
                    Directory.CreateDirectory(output.WorkPath);
                    Links.Junction(Path.Join(staging, "work", "link"), victim);
                    if (!succeeds)
                    {
                        Links.Junction(Path.Join(staging, "link"), victim);
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

        // A folder, or a link that leads nowhere, that appears at --out during the run is never replaced: exit code 5, and
        // the staging folder goes.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AFolderOrLinkThatAppearsAtTheTarget_IsNeverReplaced(bool link)
        {
            using var directory = new CliTestDirectory();
            string target = Path.Join(directory.Root, Results);
            var report = NewReport();
            using CliOutputFolder output = CliTestOutput.Reserve(target, report.RunId);
            try
            {
                await CliStagedRun.CompleteAsync(
                    report,
                    output,
                    _ =>
                    {
                        if (link)
                        {
                            Links.Junction(target, Path.Join(directory.Root, "missing"));
                        }
                        else
                        {
                            Directory.CreateDirectory(target);
                            File.WriteAllText(Path.Join(target, "theirs.txt"), "keep");
                        }

                        return Produce(report);
                    },
                    null,
                    CancellationToken.None);

                Assert.Equal(CliExitCodes.Conflict, report.ExitCode);
                Assert.Equal(new[] { "cli.output.exists" }, report.Errors.Select(error => error.Code));
                Assert.Equal(CliCleanupStates.Completed, report.CleanupState);
                Assert.Equal(new[] { Results }, directory.Entries());
                if (link)
                {
                    Assert.True(File.GetAttributes(target).HasFlag(FileAttributes.ReparsePoint));
                }
                else
                {
                    Assert.Equal(new[] { "theirs.txt" }, Directory.GetFileSystemEntries(target).Select(Path.GetFileName));
                }
            }
            finally
            {
                if (link)
                {
                    Links.Remove(target);
                }
            }
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
            SafeFileHandle[] held = Hold(directory.Root);
            try
            {
                using SafeFileHandle created = AnchoredHandles.CreateNew(held[^1], "created", PrivateTo(CurrentUser()));
                Assert.ThrowsAny<IOException>(() => AnchoredHandles.Rename(created, held[^1], "taken"));
            }
            finally
            {
                foreach (SafeFileHandle handle in held)
                {
                    handle.Dispose();
                }
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
            using CliOutputFolder output = CliTestOutput.Reserve(Path.Join(directory.Root, Results), report.RunId);
            var observer = new AfterCommitThrows();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                CliStagedRun.CompleteAsync(report, output, _ => Produce(report), observer, CancellationToken.None));

            Assert.True(output.TryDiscard());
            Assert.Equal("{}", File.ReadAllText(Path.Join(directory.Root, Results, "summary.json")));
            Assert.Equal(new[] { Results }, directory.Entries());
        }

        // Disposing lets go of the staging folder and the folders above it without removing anything, and a discard after
        // that removes nothing.
        [Fact]
        public void Dispose_LetsGoOfTheStagingFolderWithoutRemovingIt()
        {
            using var directory = new CliTestDirectory();
            string parent = Path.Join(directory.Root, "parent");
            Directory.CreateDirectory(parent);
            CliOutputFolder output = CliTestOutput.Reserve(Path.Join(parent, Results), Guid.NewGuid().ToString("N"));
            Assert.ThrowsAny<IOException>(() => Directory.Delete(output.StagingPath));

            output.Dispose();

            Assert.False(output.TryDiscard());
            Assert.True(Directory.Exists(output.StagingPath));
            Directory.Delete(output.StagingPath);
            Directory.Move(parent, parent + "-moved");
        }

        // --out is read without touching the file system: network and device paths are refused before anything could reach
        // them (exit code 6), names that Windows would read otherwise are not valid (2), and a volume's root has no parent
        // to create --out in (2).
        [Theory]
        [InlineData(@"\\192.0.2.1\share\results", "cli.output.unprotected")]
        [InlineData(@"\\?\UNC\192.0.2.1\share\results", "cli.output.unprotected")]
        [InlineData(@"\\.\C:\results", "cli.output.unprotected")]
        [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume1\results", "cli.output.unprotected")]
        [InlineData(@"\\?\C:\folder\..\results", "cli.usage.invalid-value")]
        [InlineData(@"\\?\C:\folder\stream:x\results", "cli.usage.invalid-value")]
        [InlineData(@"C:\folder\stream:x\results", "cli.usage.invalid-value")]
        [InlineData(@"C:\", "cli.output.parent-missing")]
        [InlineData(@"\\?\C:\", "cli.output.parent-missing")]
        public void CheckTarget_RefusesWithoutTouchingTheFileSystem(string output, string code) =>
            Assert.Equal(code, CliOutputFolder.CheckTarget(output, out _)?.Code);

        // A verbatim path keeps every name as it is written, a trailing dot or space included.
        [Theory]
        [InlineData(@"C:\folder\results", "C:", "folder", "results")]
        [InlineData(@"\\?\C:\folder.\results ", "C:", "folder.", "results ")]
        [InlineData(
            @"\\?\Volume{01234567-89ab-cdef-0123-456789abcdef}\folder\results",
            "Volume{01234567-89ab-cdef-0123-456789abcdef}",
            "folder",
            "results")]
        public void CheckTarget_ReadsTheVolumeTheFoldersAndTheName(string output, string volume, string folder, string name)
        {
            Assert.Null(CliOutputFolder.CheckTarget(output, out CliOutputTarget target));
            Assert.Equal(volume, target.Volume);
            Assert.Equal(new[] { folder }, target.Folders);
            Assert.Equal(name, target.Name);
        }

        // Only a local volume's device: not a network redirector, a substituted drive or a shadow copy.
        [Theory]
        [InlineData(@"\Device\HarddiskVolume3", true)]
        [InlineData(@"\Device\HarddiskVolume12", true)]
        [InlineData(@"\Device\HarddiskVolumeShadowCopy1", false)]
        [InlineData(@"\Device\HarddiskVolume", false)]
        [InlineData(@"\??\C:\work", false)]
        [InlineData(@"\Device\LanmanRedirector\;Z:0000000000012345\server\share", false)]
        [InlineData(@"\Device\Mup\server\share", false)]
        [InlineData(@"\Device\CdRom0", false)]
        [InlineData(null, false)]
        public void IsLocalVolumeDevice_IsOnlyAHarddiskVolume(string device, bool expected) =>
            Assert.Equal(expected, StorageAdmission.IsLocalVolumeDevice(device));

        // Folders above --out may be reparse points only of the kinds that leave names alone.
        [Theory]
        [InlineData(null, true)]
        [InlineData(0x9000001Au, true)]
        [InlineData(0x9000F01Au, true)]
        [InlineData(0x80000013u, true)]
        [InlineData(0x80000017u, true)]
        [InlineData(0xA0000003u, false)]
        [InlineData(0xA000000Cu, false)]
        [InlineData(0x8000001Bu, false)]
        public void IsAdmittedReparseTag_AdmitsOnlyKindsThatLeaveNamesAlone(uint? tag, bool expected) =>
            Assert.Equal(expected, StorageAdmission.IsAdmittedReparseTag(tag));

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

            Assert.Equal(expected, StorageAdmission.IsPrivateDescriptor(security, user));
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
            Assert.Equal(expected, StorageAdmission.IsSupportedStorage(fileSystem, devicePath));

        private static void ConversionRace(bool symbolic, bool beforeCreating)
        {
            using var directory = new CliTestDirectory();
            string parent = Path.Join(directory.Root, "parent");
            string decoy = Path.Join(directory.Root, "decoy");
            Directory.CreateDirectory(parent);
            Directory.CreateDirectory(decoy);

            // Adding or removing an entry would set the decoy's last write time to now.
            var untouched = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Directory.SetLastWriteTimeUtc(decoy, untouched);
            int converted = -1;
            try
            {
                (CliFailure failure, Exception error) = CliTestOutput.Attempt(
                    Path.Join(parent, Results),
                    name =>
                    {
                        if (beforeCreating ? name.StartsWith(CliOutputFolder.StagingPrefix, StringComparison.Ordinal) : name == "parent")
                        {
                            converted = Links.Convert(parent, decoy, symbolic);
                        }
                    });

                Assert.Equal(0, converted);
                Assert.True(error is IOException || failure?.Code == "cli.output.unprotected", $"{failure?.Code} {error}");
                Assert.Empty(Directory.GetFileSystemEntries(decoy));
                Assert.Equal(untouched, Directory.GetLastWriteTimeUtc(decoy));
            }
            finally
            {
                Links.Remove(parent);
            }
        }

        private static void NoConversion(bool symbolic)
        {
            using var directory = new CliTestDirectory();
            string parent = Path.Join(directory.Root, "parent");
            string decoy = Path.Join(directory.Root, "decoy");
            Directory.CreateDirectory(parent);
            Directory.CreateDirectory(decoy);
            using CliOutputFolder output = CliTestOutput.Reserve(Path.Join(parent, Results), Guid.NewGuid().ToString("N"));

            Assert.Equal(ErrorDirectoryNotEmpty, Links.Convert(parent, decoy, symbolic));
            Assert.False(File.GetAttributes(parent).HasFlag(FileAttributes.ReparsePoint));
            Assert.True(output.TryDiscard());
            Assert.Empty(Directory.GetFileSystemEntries(parent));
        }

        // Holds folder and every folder above it, as xmat does.
        private static SafeFileHandle[] Hold(string folder)
        {
            Assert.Null(CliOutputFolder.CheckTarget(Path.Join(folder, Results), out CliOutputTarget target));
            var held = new List<SafeFileHandle>
            {
                AnchoredHandles.OpenVolumeRoot(AnchoredHandles.QueryDosDevice(target.Volume)),
            };
            foreach (string name in target.Folders)
            {
                held.Add(AnchoredHandles.OpenFolder(held[^1], name));
            }

            return held.ToArray();
        }

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
    }
}
