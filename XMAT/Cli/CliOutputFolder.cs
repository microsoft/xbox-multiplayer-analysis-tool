// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace XMAT.Cli
{
    // The --out folder of a run. xmat writes into a private staging folder next to --out (same parent, so same volume)
    // that only the current user can open, then renames it to --out in one step: --out is complete or absent, and it
    // keeps that access control. A run that does not succeed removes the staging folder and publishes nothing. Storage
    // without Windows permissions (FAT, exFAT, some network shares) silently drops the access control that Reserve sets,
    // so CliStagedRun reads it back (IsPrivate) before anything is written and refuses to continue otherwise.
    internal sealed class CliOutputFolder
    {
        internal const string StagingPrefix = ".xmat-staging-";
        private const int Attempts = 10;
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

        private CliOutputFolder(string target, string stagingPath)
        {
            Target = target;
            StagingPath = stagingPath;
        }

        internal string Target { get; }

        internal string StagingPath { get; }

        // Where the legacy statics keep files during the run (HeadlessStorage). Removed before publishing.
        internal string WorkPath => Path.Combine(StagingPath, "work");

        // Checks --out without writing anything: it must not exist yet, and the folder that contains it must.
        internal static CliFailure CheckTarget(string output, out string target)
        {
            target = null;
            string fullPath;
            try
            {
                fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(output));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or SecurityException)
            {
                return new CliFailure(CliExitCodes.Usage, "cli.usage.invalid-value", "--out is not a valid folder path.");
            }

            if (Directory.Exists(fullPath) || File.Exists(fullPath))
            {
                return new CliFailure(
                    CliExitCodes.Conflict, "cli.output.exists", "The --out folder already exists; xmat never overwrites output.");
            }

            string parent = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            {
                return new CliFailure(
                    CliExitCodes.Usage, "cli.output.parent-missing", "The folder that should contain the --out folder does not exist.");
            }

            target = fullPath;
            return null;
        }

        internal static CliOutputFolder Reserve(string target, string runId)
        {
            string stagingPath = Path.Combine(Path.GetDirectoryName(target), StagingPrefix + runId);
            if (Directory.Exists(stagingPath) || File.Exists(stagingPath))
            {
                throw new IOException("The staging folder already exists.");
            }

            new DirectoryInfo(stagingPath).Create(CurrentUserOnly());
            return new CliOutputFolder(target, stagingPath);
        }

        // Reads back the access control of the staging folder. True only when it inherits nothing, its only entries allow the
        // current user, and they pass on to everything created in it (container and object inherit, with no propagation
        // limit), since a new file would otherwise get its creator's default access. False in every other case, including
        // when the access control cannot be read.
        internal bool IsPrivate()
        {
            try
            {
                DirectorySecurity security = new DirectoryInfo(StagingPath).GetAccessControl(AccessControlSections.Access);
                if (!security.AreAccessRulesProtected)
                {
                    return false;
                }

                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                AuthorizationRuleCollection rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
                if (identity.User == null || rules.Count == 0)
                {
                    return false;
                }

                // The entries may come back split, for example one per inheritance flag; together they must cover both.
                InheritanceFlags inherited = InheritanceFlags.None;
                foreach (FileSystemAccessRule rule in rules)
                {
                    if (rule.IsInherited ||
                        rule.AccessControlType != AccessControlType.Allow ||
                        !identity.User.Equals(rule.IdentityReference))
                    {
                        return false;
                    }

                    if (rule.PropagationFlags == PropagationFlags.None)
                    {
                        inherited |= rule.InheritanceFlags;
                    }
                }

                return inherited == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                return false;
            }
        }

        internal static bool IsFileSystemFailure(Exception exception) =>
            exception is IOException or UnauthorizedAccessException or SecurityException;

        // relativePath uses '/' separators, as in the JSON document.
        internal void WriteFile(string relativePath, byte[] contents)
        {
            string path = Path.Combine(StagingPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(contents, 0, contents.Length);
            stream.Flush(flushToDisk: true);
        }

        // Removes the folder where legacy statics kept files during the run; it is never published. Cancellation ends the
        // retries (OperationCanceledException), and the run is then discarded.
        internal void RemoveWorkFolder(CancellationToken cancellationToken)
        {
            if (Directory.Exists(WorkPath))
            {
                Retry(() => Directory.Delete(WorkPath, recursive: true), cancellationToken);
            }
        }

        // The commit: renames the staging folder to --out in one step, retrying for about a second. Cancellation is
        // checked before each attempt (OperationCanceledException); returns false when --out appeared meanwhile.
        internal bool TryCommit(ICliCommitObserver observer, CancellationToken cancellationToken)
        {
            for (int attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.Exists(Target) || File.Exists(Target))
                {
                    return false;
                }

                try
                {
                    Directory.Move(StagingPath, Target);
                    return true;
                }
                catch (Exception ex) when (attempt < Attempts && IsFileSystemFailure(ex))
                {
                    observer?.CommitAttemptFailed(attempt);
                    cancellationToken.WaitHandle.WaitOne(RetryDelay);
                }
            }
        }

        // Removes the staging folder; false when it could not be removed (exit code 7). Cleanup is not cancellable: it
        // runs after a cancellation too.
        internal bool TryDiscard()
        {
            try
            {
                Retry(
                    () =>
                    {
                        if (Directory.Exists(StagingPath))
                        {
                            Directory.Delete(StagingPath, recursive: true);
                        }
                    },
                    CancellationToken.None);
                return true;
            }
            catch (Exception ex) when (IsFileSystemFailure(ex))
            {
                return false;
            }
        }

        // Antivirus and indexers briefly open new files, which makes renames and deletes fail; retry for about a second.
        // Cancellation ends the retries with OperationCanceledException.
        private static void Retry(Action action, CancellationToken cancellationToken)
        {
            for (int attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    action();
                    return;
                }
                catch (Exception ex) when (attempt < Attempts && IsFileSystemFailure(ex))
                {
                    cancellationToken.WaitHandle.WaitOne(RetryDelay);
                }
            }
        }

        private static DirectorySecurity CurrentUserOnly()
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                identity.User,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            return security;
        }
    }
}
