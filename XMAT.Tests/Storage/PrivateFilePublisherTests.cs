// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using XMAT.Operations.Storage;
using XMAT.Tests.Cli;

namespace XMAT.Tests.Storage
{
    // PrivateFilePublisher against run-owned folders on the local test volume. Nothing reaches a network: network and
    // device spellings are rejected while they are still only strings.
    public sealed class PrivateFilePublisherTests
    {
        [Fact]
        public void PublishesAFileWithPrivateAccessControl()
        {
            using var directory = new CliTestDirectory();
            string target = Path.Join(directory.Root, "request.json");

            PrivateFilePublication result = PublishText(target, "content");

            Assert.Equal(PrivateFilePublication.Published, result);
            Assert.Equal("content", File.ReadAllText(target));
            FileSecurity security = new FileInfo(target).GetAccessControl();
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            Assert.True(security.AreAccessRulesProtected);
            Assert.Equal(identity.User, security.GetOwner(typeof(SecurityIdentifier)));
            AuthorizationRuleCollection rules =
                security.GetAccessRules(
                    includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier));
            Assert.NotEmpty(rules);
            foreach (FileSystemAccessRule rule in rules)
            {
                Assert.False(rule.IsInherited);
                Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
                Assert.Equal(identity.User, rule.IdentityReference);
                Assert.Equal(InheritanceFlags.None, rule.InheritanceFlags);
                Assert.Equal(PropagationFlags.None, rule.PropagationFlags);
            }

            Assert.True(StorageAdmission.IsPrivateFileDescriptor(security, identity.User));
            AssertNoStaging(directory.Root);
        }

        [Theory]
        [InlineData("file")]
        [InlineData("junction")]
        public void ExistingTarget_IsNeverReplaced(string kind)
        {
            using var directory = new CliTestDirectory();
            string target = Path.Join(directory.Root, "request.json");
            string missing = Path.Join(directory.Root, "missing");
            bool junction = kind == "junction";
            if (junction)
            {
                Links.Junction(target, missing);
            }
            else
            {
                File.WriteAllText(target, "keep");
            }

            bool wrote = false;
            try
            {
                PrivateFilePublication result = PrivateFilePublisher.Publish(
                    target,
                    _ => wrote = true,
                    CancellationToken.None);

                Assert.Equal(PrivateFilePublication.Exists, result);
                Assert.False(wrote);
                if (junction)
                {
                    Assert.True(File.GetAttributes(target).HasFlag(FileAttributes.ReparsePoint));
                }
                else
                {
                    Assert.Equal("keep", File.ReadAllText(target));
                }

                AssertNoStaging(directory.Root);
            }
            finally
            {
                if (junction)
                {
                    Links.Remove(target);
                }
            }
        }

        [Fact]
        public void TargetCreatedBetweenCheckAndRename_IsNeverReplaced()
        {
            using var directory = new CliTestDirectory();
            string target = Path.Join(directory.Root, "request.json");
            var hooks = new PrivateFilePublisherTestHooks(
                Checkpoint: checkpoint =>
                {
                    if (checkpoint == PrivateFilePublisherCheckpoint.BeforeRename)
                    {
                        File.WriteAllText(target, "keep");
                    }
                });

            PrivateFilePublication result = PublishText(target, "ours", hooks);

            Assert.Equal(PrivateFilePublication.Exists, result);
            Assert.Equal("keep", File.ReadAllText(target));
            AssertNoStaging(directory.Root);
        }

        [Fact]
        public void AStagingCollision_IsNeverAdopted()
        {
            using var directory = new CliTestDirectory();
            string target = Path.Join(directory.Root, "request.json");
            string collision = StagingName('A');
            string next = StagingName('B');
            File.WriteAllText(Path.Join(directory.Root, collision), "keep");
            var hooks = new PrivateFilePublisherTestHooks(
                StagingName: attempt => attempt == 1 ? collision : next);

            PrivateFilePublication result = PublishText(target, "published", hooks);

            Assert.Equal(PrivateFilePublication.Published, result);
            Assert.Equal("keep", File.ReadAllText(Path.Join(directory.Root, collision)));
            Assert.Equal("published", File.ReadAllText(target));
            Assert.Equal(
                new[] { collision, "request.json" },
                Directory.GetFileSystemEntries(directory.Root).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal));
        }

        [Fact]
        public void ADanglingJunctionAtAStagingName_IsNeverAdopted()
        {
            using var directory = new CliTestDirectory();
            string target = Path.Join(directory.Root, "request.json");
            string missing = Path.Join(directory.Root, "missing");
            string collision = StagingName('A');
            string next = StagingName('B');
            string link = Path.Join(directory.Root, collision);
            Links.Junction(link, missing);
            int attempts = 0;
            var hooks = new PrivateFilePublisherTestHooks(
                StagingName: attempt =>
                {
                    attempts++;
                    return attempt == 1 ? collision : next;
                });
            try
            {
                PrivateFilePublication result = PublishText(target, "published", hooks);

                Assert.Equal(PrivateFilePublication.Published, result);
                Assert.Equal(2, attempts);
                Assert.Equal("published", File.ReadAllText(target));
                Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint));
                Assert.False(File.Exists(missing));
                Assert.False(Directory.Exists(missing));
            }
            finally
            {
                Links.Remove(link);
            }
        }

        [SymbolicLinkFact]
        public void ADanglingSymlinkAtAStagingName_IsNeverFollowed()
        {
            using var directory = new CliTestDirectory();
            string target = Path.Join(directory.Root, "request.json");
            string missing = Path.Join(directory.Root, "missing.bin");
            string collision = StagingName('A');
            string next = StagingName('B');
            string link = Path.Join(directory.Root, collision);
            _ = File.CreateSymbolicLink(link, missing);
            int attempts = 0;
            bool reachedBeforeRename = false;
            bool missingBeforeRename = false;
            bool nextBeforeRename = false;
            var hooks = new PrivateFilePublisherTestHooks(
                StagingName: attempt =>
                {
                    attempts++;
                    return attempt == 1 ? collision : next;
                },
                Checkpoint: checkpoint =>
                {
                    if (checkpoint == PrivateFilePublisherCheckpoint.BeforeRename)
                    {
                        reachedBeforeRename = true;
                        missingBeforeRename = File.Exists(missing);
                        nextBeforeRename = File.Exists(Path.Join(directory.Root, next));
                    }
                });
            try
            {
                PrivateFilePublication result = PublishText(target, "published", hooks);

                Assert.Equal(PrivateFilePublication.Published, result);
                Assert.Equal(2, attempts);
                Assert.True(reachedBeforeRename);
                Assert.False(missingBeforeRename);
                Assert.True(nextBeforeRename);
                Assert.Equal("published", File.ReadAllText(target));
                Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint));
                Assert.False(File.Exists(missing));
            }
            finally
            {
                File.Delete(link);
                File.Delete(missing);
            }
        }

        [Fact]
        public void FourStagingCollisions_FailWithoutAdoptingAny()
        {
            using var directory = new CliTestDirectory();
            string target = Path.Join(directory.Root, "request.json");
            string[] collisions = { StagingName('A'), StagingName('B'), StagingName('C'), StagingName('D') };
            foreach (string collision in collisions)
            {
                File.WriteAllText(Path.Join(directory.Root, collision), collision);
            }

            bool wrote = false;
            int attempts = 0;
            var hooks = new PrivateFilePublisherTestHooks(StagingName: attempt =>
            {
                attempts++;
                return collisions[attempt - 1];
            });
            PrivateFilePublication result =
                PrivateFilePublisher.Publish(target, _ => wrote = true, hooks, CancellationToken.None);

            Assert.Equal(PrivateFilePublication.Failed, result);
            Assert.Equal(4, attempts);
            Assert.False(wrote);
            Assert.False(File.Exists(target));
            Assert.All(collisions, collision => Assert.Equal(collision, File.ReadAllText(Path.Join(directory.Root, collision))));
        }

        [Fact]
        public void StagingNames_AreFixedLengthCryptographicNamesIndependentOfTheTarget()
        {
            using var directory = new CliTestDirectory();
            var names = new List<string>();
            foreach (string targetName in new[] { "one.json", "a-very-different-target-name.json" })
            {
                string target = Path.Join(directory.Root, targetName);
                PrivateFilePublication result = PrivateFilePublisher.Publish(
                    target,
                    stream =>
                    {
                        names.Add(Assert.Single(StagingEntries(directory.Root)));
                        stream.WriteByte(1);
                    },
                    CancellationToken.None);
                Assert.Equal(PrivateFilePublication.Published, result);
            }

            Assert.Equal(2, names.Count);
            Assert.NotEqual(names[0], names[1]);
            Assert.All(names, name =>
            {
                Assert.Equal(PrivateFilePublisher.StagingNameLength, name.Length);
                Assert.StartsWith(PrivateFilePublisher.StagingPrefix, name, StringComparison.Ordinal);
                Assert.EndsWith(PrivateFilePublisher.StagingSuffix, name, StringComparison.Ordinal);
                Assert.DoesNotContain("target", name, StringComparison.OrdinalIgnoreCase);
                string hex = name[
                    PrivateFilePublisher.StagingPrefix.Length..
                    (name.Length - PrivateFilePublisher.StagingSuffix.Length)];
                Assert.Equal(32, hex.Length);
                Assert.All(hex, character => Assert.True(char.IsAsciiHexDigit(character)));
            });
        }

        [Fact]
        public void StagingSecurityAndStorage_AreVerifiedBeforeWrite()
        {
            using var directory = new CliTestDirectory();
            string target = Path.Join(directory.Root, "request.json");
            bool wrote = false;
            var hooks = new PrivateFilePublisherTestHooks(VerifyStaging: _ => false);

            PrivateFilePublication result =
                PrivateFilePublisher.Publish(target, _ => wrote = true, hooks, CancellationToken.None);

            Assert.Equal(PrivateFilePublication.Unsupported, result);
            Assert.False(wrote);
            Assert.Empty(directory.Entries());
        }

        [Fact]
        public void TheRealSecurityCheck_RefusesAnAdditionalEveryoneRuleBeforeWrite()
        {
            using var directory = new CliTestDirectory();
            string target = Path.Join(directory.Root, "request.json");
            bool wrote = false;
            var hooks = new PrivateFilePublisherTestHooks(
                BeforeVerify: handle =>
                {
                    var file = new FileInfo(AnchoredHandles.FinalPath(handle));
                    FileSecurity security = file.GetAccessControl();
                    security.AddAccessRule(new FileSystemAccessRule(
                        new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                        FileSystemRights.ReadData,
                        InheritanceFlags.None,
                        PropagationFlags.None,
                        AccessControlType.Allow));
                    file.SetAccessControl(security);
                });

            PrivateFilePublication result =
                PrivateFilePublisher.Publish(target, _ => wrote = true, hooks, CancellationToken.None);

            Assert.Equal(PrivateFilePublication.Unsupported, result);
            Assert.False(wrote);
            Assert.Empty(directory.Entries());
        }

        [Theory]
        [InlineData(false, false, false, true)]
        [InlineData(true, false, false, false)]
        [InlineData(false, true, false, false)]
        [InlineData(false, false, true, false)]
        public void IsPrivateFileDescriptor_RequiresOnlyTheCurrentUsersRules(
            bool otherOwner, bool everyoneRule, bool unprotected, bool expected)
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            FileSecurity security = StorageAdmission.CurrentUserFileSecurity();
            if (otherOwner)
            {
                security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            }

            if (everyoneRule)
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                    FileSystemRights.ReadData,
                    AccessControlType.Allow));
            }

            if (unprotected)
            {
                security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            }

            Assert.Equal(expected, StorageAdmission.IsPrivateFileDescriptor(security, identity.User));
        }

        [Fact]
        public void WriteFailure_RemovesTheStagingFile()
        {
            using var directory = new CliTestDirectory();
            string target = Path.Join(directory.Root, "request.json");

            PrivateFilePublication result = PrivateFilePublisher.Publish(
                target,
                _ => throw new InvalidOperationException("The test writer failed."),
                CancellationToken.None);

            Assert.Equal(PrivateFilePublication.Failed, result);
            Assert.Empty(directory.Entries());
        }

        [Fact]
        public void OperationCanceledExceptionFromWrite_IsCancelledAndCleaned()
        {
            using var directory = new CliTestDirectory();
            string target = Path.Join(directory.Root, "request.json");

            PrivateFilePublication result = PrivateFilePublisher.Publish(
                target,
                _ => throw new OperationCanceledException(),
                CancellationToken.None);

            Assert.Equal(PrivateFilePublication.Cancelled, result);
            Assert.Empty(directory.Entries());
        }

        [Fact]
        public void CancellationBeforeCommit_RemovesTheStagingFile()
        {
            using var directory = new CliTestDirectory();
            string target = Path.Join(directory.Root, "request.json");
            using var cancellation = new CancellationTokenSource();
            var hooks = new PrivateFilePublisherTestHooks(
                Checkpoint: checkpoint =>
                {
                    if (checkpoint == PrivateFilePublisherCheckpoint.BeforeRename)
                    {
                        cancellation.Cancel();
                    }
                });

            PrivateFilePublication result =
                PrivateFilePublisher.Publish(target, stream => stream.WriteByte(1), hooks, cancellation.Token);

            Assert.Equal(PrivateFilePublication.Cancelled, result);
            Assert.Empty(directory.Entries());
        }

        [Fact]
        public void CancellationAfterCommit_NeverDeletesThePublishedFile()
        {
            using var directory = new CliTestDirectory();
            string target = Path.Join(directory.Root, "request.json");
            using var cancellation = new CancellationTokenSource();
            var hooks = new PrivateFilePublisherTestHooks(
                Checkpoint: checkpoint =>
                {
                    if (checkpoint == PrivateFilePublisherCheckpoint.AfterCommit)
                    {
                        cancellation.Cancel();
                    }
                });

            PrivateFilePublication result =
                PrivateFilePublisher.Publish(target, stream => stream.WriteByte(1), hooks, cancellation.Token);

            Assert.Equal(PrivateFilePublication.Published, result);
            Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(target));
            AssertNoStaging(directory.Root);
        }

        [Theory]
        [InlineData("cancelled")]
        [InlineData("failed")]
        public void ExceptionsAfterCommit_KeepPublishedResultAndFile(string outcome)
        {
            using var directory = new CliTestDirectory();
            string target = Path.Join(directory.Root, "request.json");
            var hooks = new PrivateFilePublisherTestHooks(
                Checkpoint: checkpoint =>
                {
                    if (checkpoint != PrivateFilePublisherCheckpoint.AfterCommit)
                    {
                        return;
                    }

                    if (outcome == "cancelled")
                    {
                        throw new OperationCanceledException();
                    }

                    throw new InvalidOperationException("The test checkpoint failed.");
                });

            PrivateFilePublication result = PublishText(target, "published", hooks);

            Assert.Equal(PrivateFilePublication.Published, result);
            Assert.Equal("published", File.ReadAllText(target));
            AssertNoStaging(directory.Root);
        }

        [Theory]
        [InlineData("failed")]
        [InlineData("cancelled")]
        public void CleanupFailure_TakesPrecedenceOverFailureAndCancellation(string outcome)
        {
            using var directory = new CliTestDirectory();
            string target = Path.Join(directory.Root, "request.json");
            string stagingName = StagingName('A');
            var hooks = new PrivateFilePublisherTestHooks(
                StagingName: _ => stagingName,
                RemoveStaging: _ => false);

            PrivateFilePublication result = PrivateFilePublisher.Publish(
                target,
                _ =>
                {
                    if (outcome == "cancelled")
                    {
                        throw new OperationCanceledException();
                    }

                    throw new InvalidOperationException("The test writer failed.");
                },
                hooks,
                CancellationToken.None);

            Assert.Equal(PrivateFilePublication.CleanupFailed, result);
            Assert.True(File.Exists(Path.Join(directory.Root, stagingName)));
            Assert.False(File.Exists(target));
            File.Delete(Path.Join(directory.Root, stagingName));
        }

        [Fact]
        public void CloseFailureAfterDeleteDisposition_DoesNotBecomeCleanupFailed()
        {
            using var directory = new CliTestDirectory();
            string target = Path.Join(directory.Root, "request.json");
            var hooks = new PrivateFilePublisherTestHooks(CloseSucceeded: () => false);

            PrivateFilePublication result = PrivateFilePublisher.Publish(
                target,
                _ => throw new InvalidOperationException("The test writer failed."),
                hooks,
                CancellationToken.None);

            Assert.Equal(PrivateFilePublication.Failed, result);
            Assert.Empty(directory.Entries());
        }

        [Fact]
        public void ADisposedWriterAndARetainedStream_CannotOwnOrChangeTheFile()
        {
            using var directory = new CliTestDirectory();
            string disposedTarget = Path.Join(directory.Root, "disposed.json");
            PrivateFilePublication disposed = PrivateFilePublisher.Publish(
                disposedTarget,
                stream =>
                {
                    Assert.IsNotType<FileStream>(stream);
                    using var writer = new StreamWriter(
                        stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 1024, leaveOpen: false);
                    writer.Write("published");
                },
                CancellationToken.None);

            Assert.Equal(PrivateFilePublication.Published, disposed);
            Assert.Equal("published", File.ReadAllText(disposedTarget));

            string retainedTarget = Path.Join(directory.Root, "retained.json");
            Stream retained = null;
            Exception lateWriteByte = null;
            Exception lateWriteArray = null;
            Exception lateWriteSpan = null;
            Exception lateFlush = null;
            Exception lateWriteAsync = null;
            Exception lateWriteMemoryAsync = null;
            using var liveCancellation = new CancellationTokenSource();
            var hooks = new PrivateFilePublisherTestHooks(
                Checkpoint: checkpoint =>
                {
                    if (checkpoint == PrivateFilePublisherCheckpoint.BeforeRename)
                    {
                        lateWriteByte = Record.Exception(() => retained.WriteByte(2));
                        lateWriteArray = Record.Exception(() => retained.Write(new byte[] { 3 }, 0, 1));
                        lateWriteSpan = Record.Exception(() => retained.Write(new byte[] { 4 }.AsSpan()));
                        lateFlush = Record.Exception(retained.Flush);
                        lateWriteAsync = WriteAsyncException(retained).GetAwaiter().GetResult();
                        lateWriteMemoryAsync =
                            WriteMemoryAsyncException(retained, liveCancellation.Token).GetAwaiter().GetResult();
                    }
                });
            PrivateFilePublication kept = PrivateFilePublisher.Publish(
                retainedTarget,
                stream =>
                {
                    retained = stream;
                    stream.WriteByte(1);
                },
                hooks,
                CancellationToken.None);

            Assert.Equal(PrivateFilePublication.Published, kept);
            Assert.IsType<ObjectDisposedException>(lateWriteByte);
            Assert.IsType<ObjectDisposedException>(lateWriteArray);
            Assert.IsType<ObjectDisposedException>(lateWriteSpan);
            Assert.IsType<ObjectDisposedException>(lateFlush);
            Assert.IsType<ObjectDisposedException>(lateWriteAsync);
            Assert.IsType<ObjectDisposedException>(lateWriteMemoryAsync);
            Assert.False(retained.CanWrite);
            Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(retainedTarget));
        }

        [Fact]
        public void ActiveMemoryAsyncWrite_CompletesAndPublishesInOrder()
        {
            using var directory = new CliTestDirectory();
            string target = Path.Join(directory.Root, "request.json");
            using var liveCancellation = new CancellationTokenSource();
            bool completedSynchronously = false;

            PrivateFilePublication result = PrivateFilePublisher.Publish(
                target,
                stream =>
                {
                    stream.WriteByte(1);
                    ValueTask write = stream.WriteAsync(
                        new ReadOnlyMemory<byte>(new byte[] { 2, 3 }), liveCancellation.Token);
                    completedSynchronously = write.IsCompletedSuccessfully;
                    write.GetAwaiter().GetResult();
                    stream.WriteByte(4);
                },
                CancellationToken.None);

            Assert.Equal(PrivateFilePublication.Published, result);
            Assert.True(completedSynchronously);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(target));
        }

        [Fact]
        public async Task CancelledAsyncWrites_ReturnCancelledTasksWithoutWriting()
        {
            using var directory = new CliTestDirectory();
            string target = Path.Join(directory.Root, "request.json");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Task arrayWrite = null;
            ValueTask memoryWrite = default;

            PrivateFilePublication result = PrivateFilePublisher.Publish(
                target,
                stream =>
                {
                    stream.WriteByte(1);
                    arrayWrite = stream.WriteAsync(new byte[] { 2 }, 0, 1, cancellation.Token);
                    memoryWrite = stream.WriteAsync(new ReadOnlyMemory<byte>(new byte[] { 3 }), cancellation.Token);
                },
                CancellationToken.None);

            Assert.Equal(PrivateFilePublication.Published, result);
            Assert.True(arrayWrite.IsCanceled);
            Assert.True(memoryWrite.IsCanceled);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => arrayWrite);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => memoryWrite.AsTask());
            Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(target));
        }

        [Theory]
        [InlineData(@"\\192.0.2.1\share\request.json")]
        [InlineData(@"\\?\UNC\192.0.2.1\share\request.json")]
        [InlineData(@"\\.\C:\request.json")]
        [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume1\request.json")]
        public void NetworkAndDevicePaths_AreUnsupportedWithoutWriting(string target)
        {
            bool wrote = false;

            PrivateFilePublication result =
                PrivateFilePublisher.Publish(target, _ => wrote = true, CancellationToken.None);

            Assert.Equal(PrivateFilePublication.Unsupported, result);
            Assert.False(wrote);
        }

        [Fact]
        public void LinkInParentChain_IsUnsupportedAndNeverTraversed()
        {
            using var directory = new CliTestDirectory();
            string real = Path.Join(directory.Root, "real");
            string link = Path.Join(directory.Root, "link");
            string target = Path.Join(link, "request.json");
            Directory.CreateDirectory(real);
            Links.Junction(link, real);
            bool wrote = false;
            int stagingAttempts = 0;
            var hooks = new PrivateFilePublisherTestHooks(
                StagingName: _ =>
                {
                    stagingAttempts++;
                    return StagingName('A');
                });
            try
            {
                PrivateFilePublication result =
                    PrivateFilePublisher.Publish(target, _ => wrote = true, hooks, CancellationToken.None);

                Assert.Equal(PrivateFilePublication.Unsupported, result);
                Assert.False(wrote);
                Assert.Equal(0, stagingAttempts);
                Assert.False(File.Exists(Path.Join(real, "request.json")));
                AssertNoStaging(real);
            }
            finally
            {
                Links.Remove(link);
            }
        }

        [Fact]
        public void ParentConvertedBeforeStagingCreation_IsNeverWrittenThrough()
        {
            using var directory = new CliTestDirectory();
            string parent = Path.Join(directory.Root, "parent");
            string decoy = Path.Join(directory.Root, "decoy");
            string target = Path.Join(parent, "request.json");
            Directory.CreateDirectory(parent);
            Directory.CreateDirectory(decoy);
            int converted = -1;
            bool wrote = false;
            var hooks = new PrivateFilePublisherTestHooks(
                Checkpoint: checkpoint =>
                {
                    if (checkpoint == PrivateFilePublisherCheckpoint.BeforeCreate && converted < 0)
                    {
                        converted = Links.Convert(parent, decoy, symbolic: false);
                    }
                });
            try
            {
                PrivateFilePublication result =
                    PrivateFilePublisher.Publish(target, _ => wrote = true, hooks, CancellationToken.None);

                Assert.Equal(0, converted);
                Assert.True(
                    result is PrivateFilePublication.Unsupported or PrivateFilePublication.Failed,
                    result.ToString());
                Assert.False(wrote);
                Assert.Empty(Directory.GetFileSystemEntries(decoy));
            }
            finally
            {
                Links.Remove(parent);
            }
        }

        [Fact]
        public void DanglingJunctionCreatedBeforeRename_GivesExistsAndIsNotReplaced()
        {
            using var directory = new CliTestDirectory();
            string target = Path.Join(directory.Root, "request.json");
            string missing = Path.Join(directory.Root, "missing");
            var hooks = new PrivateFilePublisherTestHooks(
                Checkpoint: checkpoint =>
                {
                    if (checkpoint == PrivateFilePublisherCheckpoint.BeforeRename)
                    {
                        Links.Junction(target, missing);
                    }
                });
            try
            {
                PrivateFilePublication result = PublishText(target, "ours", hooks);

                Assert.Equal(PrivateFilePublication.Exists, result);
                Assert.True(File.GetAttributes(target).HasFlag(FileAttributes.ReparsePoint));
                Assert.False(File.Exists(missing));
                Assert.False(Directory.Exists(missing));
                AssertNoStaging(directory.Root);
            }
            finally
            {
                Links.Remove(target);
            }
        }

        private static PrivateFilePublication PublishText(
            string target, string contents, PrivateFilePublisherTestHooks hooks = null)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(contents);
            return PrivateFilePublisher.Publish(
                target,
                stream => stream.Write(bytes),
                hooks,
                CancellationToken.None);
        }

        private static string StagingName(char hex) =>
            PrivateFilePublisher.StagingPrefix + new string(hex, 32) + PrivateFilePublisher.StagingSuffix;

        private static string[] StagingEntries(string directory) =>
            Directory.GetFileSystemEntries(directory)
                .Select(Path.GetFileName)
                .Where(name => name.StartsWith(PrivateFilePublisher.StagingPrefix, StringComparison.Ordinal))
                .ToArray();

        private static async Task<Exception> WriteAsyncException(Stream stream)
        {
            try
            {
                await stream.WriteAsync(new byte[] { 5 }, 0, 1).ConfigureAwait(false);
                return null;
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                return exception;
            }
        }

        private static async Task<Exception> WriteMemoryAsyncException(
            Stream stream, CancellationToken cancellationToken)
        {
            try
            {
                await stream.WriteAsync(
                    new ReadOnlyMemory<byte>(new byte[] { 6 }), cancellationToken).ConfigureAwait(false);
                return null;
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                return exception;
            }
        }

        private static void AssertNoStaging(string directory) => Assert.Empty(StagingEntries(directory));
    }
}
