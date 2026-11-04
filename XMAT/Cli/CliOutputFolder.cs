// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace XMAT.Cli
{
    // The --out folder of a run. xmat writes into a private staging folder next to --out (same parent, so same volume)
    // that only the current user can open, then renames it to --out in one step: --out is complete or absent, and it
    // keeps that access control. A run that does not succeed removes the staging folder and publishes nothing. Only a
    // local NTFS volume keeps that access control and the guarantees below, so CliStagedRun checks the storage and reads
    // the access control back (IsPrivate) before anything is written, and refuses to continue otherwise.
    //
    // From the moment Reserve creates it until it is published or removed, the staging folder is held open through one
    // handle (CliDirectoryHandle), and every path xmat uses is the folder's final path, with junctions and links
    // resolved. While the handle is open, Windows refuses to rename or remove the staging folder or any folder that
    // contains it, so another account cannot swap one of them for a folder of its own between the privacy check and the
    // writes. Publication renames the folder through the handle and cleanup removes it through the handle, so neither
    // looks up a path that could have changed.
    internal sealed class CliOutputFolder : IDisposable
    {
        internal const string StagingPrefix = ".xmat-staging-";
        private const int Attempts = 10;
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

        private SafeFileHandle _handle;
        private bool _published;

        private CliOutputFolder(string target, string stagingPath, SafeFileHandle handle)
        {
            Target = target;
            StagingPath = stagingPath;
            _handle = handle;
        }

        // --out, in the folder that the staging folder really is in.
        internal string Target { get; }

        // The staging folder's final path.
        internal string StagingPath { get; }

        // Where the legacy statics keep files during the run (HeadlessStorage). Removed before publishing.
        internal string WorkPath => Path.Join(StagingPath, "work");

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

        // Creates the private staging folder next to target and holds it open, in one step that never adopts a folder or
        // link that is already there. When the folder's path cannot be read, the new, empty folder goes again.
        internal static CliOutputFolder Reserve(string target, string runId)
        {
            string stagingPath = Path.Join(Path.GetDirectoryName(target), StagingPrefix + runId);
            SafeFileHandle handle = CliDirectoryHandle.CreateNew(stagingPath, CurrentUserOnly());
            try
            {
                string finalPath = CliDirectoryHandle.FinalPath(handle);
                return new CliOutputFolder(
                    Path.Join(Path.GetDirectoryName(finalPath), Path.GetFileName(target)), finalPath, handle);
            }
            catch
            {
                try
                {
                    CliDirectoryHandle.MarkForDeletion(handle);
                }
                catch (IOException)
                {
                    // The folder stays behind, empty and private.
                }

                handle.Dispose();
                throw;
            }
        }

        // Whether the staging folder can keep the output private: it is on a local NTFS volume (IsSupportedStorage), and
        // its owner and access control, read through the handle, are the current user's alone (IsPrivateDescriptor).
        // False in every other case, including when they cannot be read.
        internal bool IsPrivate()
        {
            try
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                return IsSupportedStorage(CliDirectoryHandle.FileSystemName(Handle), CliDirectoryHandle.DevicePath(Handle)) &&
                    IsPrivateDescriptor(CliDirectoryHandle.Security(Handle), identity.User);
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                return false;
            }
        }

        // Only a local NTFS volume (\Device\HarddiskVolumeN) is supported: there Windows keeps the access control and
        // refuses to rename a folder that holds an open handle. Network shares, including mapped drives (\Device\Mup),
        // and other file systems are not.
        internal static bool IsSupportedStorage(string fileSystemName, string devicePath) =>
            string.Equals(fileSystemName, "NTFS", StringComparison.OrdinalIgnoreCase) &&
            devicePath != null &&
            devicePath.StartsWith(@"\Device\HarddiskVolume", StringComparison.OrdinalIgnoreCase);

        // True only when user owns the folder, it inherits nothing, its only entries allow user, and they pass on to
        // everything created in it (container and object inherit, with no propagation limit), since a new file would
        // otherwise get its creator's default access.
        internal static bool IsPrivateDescriptor(DirectorySecurity security, SecurityIdentifier user)
        {
            if (security == null || user == null || !security.AreAccessRulesProtected ||
                !user.Equals(security.GetOwner(typeof(SecurityIdentifier))))
            {
                return false;
            }

            AuthorizationRuleCollection rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));

            // The entries may come back split, for example one per inheritance flag; together they must cover both.
            InheritanceFlags inherited = InheritanceFlags.None;
            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.IsInherited || rule.AccessControlType != AccessControlType.Allow || !user.Equals(rule.IdentityReference))
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

        // Removes the folder where legacy statics kept files during the run; it is never published. A link in it is removed
        // itself and never followed. Cancellation ends the retries (OperationCanceledException), and the run is then
        // discarded.
        internal void RemoveWorkFolder(CancellationToken cancellationToken)
        {
            if (Directory.Exists(WorkPath) || File.Exists(WorkPath))
            {
                Retry(() => DeleteEntry(WorkPath), cancellationToken);
            }
        }

        // The commit: renames the staging folder to --out in one step, through the handle that holds it, retrying for
        // about a second. Cancellation is checked before each attempt (OperationCanceledException); returns false when
        // --out appeared meanwhile, which is never replaced. A rename that succeeded is the publication: from then on
        // nothing removes the folder, whatever follows.
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
                    CliDirectoryHandle.Rename(Handle, Target);
                    _published = true;
                    Release();
                    return true;
                }
                catch (Exception ex) when (attempt < Attempts && IsFileSystemFailure(ex))
                {
                    observer?.CommitAttemptFailed(attempt);
                    cancellationToken.WaitHandle.WaitOne(RetryDelay);
                }
            }
        }

        // Removes the staging folder: what is in it through its final path while it is held open, links themselves and
        // never what they point to, then the folder itself through the handle. False when it could not be removed (exit
        // code 7), which also lets go of the folder. Cleanup is not cancellable: it runs after a cancellation too. A
        // published folder is never removed.
        internal bool TryDiscard()
        {
            if (_published)
            {
                return true;
            }

            if (_handle == null)
            {
                return false;
            }

            try
            {
                Retry(
                    () =>
                    {
                        DeleteContents(StagingPath);
                        CliDirectoryHandle.MarkForDeletion(Handle);
                    },
                    CancellationToken.None);
                return true;
            }
            catch (Exception ex) when (IsFileSystemFailure(ex))
            {
                return false;
            }
            finally
            {
                Release();
            }
        }

        // Closes the handle without removing anything, if a commit or discard has not already.
        public void Dispose() => Release();

        private SafeFileHandle Handle => _handle ?? throw new ObjectDisposedException(nameof(CliOutputFolder));

        private void Release() => Interlocked.Exchange(ref _handle, null)?.Dispose();

        // Deletes a file, or a folder and everything in it; a junction or link is removed itself and never followed.
        private static void DeleteEntry(string path)
        {
            FileAttributes attributes = File.GetAttributes(path);
            bool folder = (attributes & FileAttributes.Directory) != 0;
            if (folder && (attributes & FileAttributes.ReparsePoint) == 0)
            {
                DeleteContents(path);
            }
            else if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }

            if (folder)
            {
                Directory.Delete(path, recursive: false);
            }
            else
            {
                File.Delete(path);
            }
        }

        // Deletes everything in folder, going on past an entry that cannot be deleted yet, as a file that another process
        // still has open, and then throws the first failure, so a retry finds only what is left.
        private static void DeleteContents(string folder)
        {
            Exception first = null;
            foreach (string entry in Directory.GetFileSystemEntries(folder))
            {
                try
                {
                    DeleteEntry(entry);
                }
                catch (Exception ex) when (IsFileSystemFailure(ex))
                {
                    first ??= ex;
                }
            }

            if (first != null)
            {
                ExceptionDispatchInfo.Capture(first).Throw();
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
            security.SetOwner(identity.User);
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
