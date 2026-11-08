// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Security;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using XMAT.Operations.Storage;

namespace XMAT.Cli
{
    // The --out folder of a run. xmat writes into a private staging folder next to --out (same parent, so same volume)
    // that only the current user can open, then renames it to --out in one step: --out is complete or absent, and it
    // keeps that access control. A run that does not succeed removes the staging folder and publishes nothing. Only a
    // local NTFS volume keeps that access control and the guarantees below, so CliStagedRun checks the storage and reads
    // the access control back (IsPrivate) before anything is written, and refuses to continue otherwise.
    //
    // --out is admitted without following anything: CheckTarget reads it without touching the file system and refuses
    // network and device paths; StorageAdmission holds the walk from the volume root down to the parent of --out,
    // refusing junctions, mounted folders, symbolic links and any reparse point that is not known to leave names alone.
    // AnchoredHandles then creates and holds the staging folder relative to that parent. Held folders cannot be renamed
    // or removed, and each contains the next, so none can become a link either; publication renames the staging folder
    // relative to the parent, and every path xmat uses is the staging folder's verbatim volume path
    // (\\?\Volume{...}\...), which Windows uses as it is.
    internal sealed class CliOutputFolder : IDisposable
    {
        internal const string StagingPrefix = ".xmat-staging-";
        private const int Attempts = 10;
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

        private readonly string _name;
        private StorageAnchor _anchor;
        private SafeFileHandle _handle;
        private bool _published;

        private CliOutputFolder(string name, string stagingPath, StorageAnchor anchor, SafeFileHandle handle)
        {
            _name = name;
            StagingPath = stagingPath;
            _anchor = anchor;
            _handle = handle;
        }

        // The staging folder's verbatim volume path.
        internal string StagingPath { get; }

        // Where the legacy statics keep files during the run (HeadlessStorage). Removed before publishing.
        internal string WorkPath => Path.Join(StagingPath, "work");

        // Reads --out without touching the file system: a path on a drive (C:\...), or a verbatim one on a drive or volume
        // (\\?\C:\..., \\?\Volume{...}\...). Network paths, mapped through UNC or not, and device paths are refused before
        // anything could reach them; ProbeTarget checks existence without creating, and TryReserve repeats it.
        internal static CliFailure CheckTarget(string output, out CliOutputTarget target)
        {
            target = null;
            StorageAdmissionFailure? failure = StorageAdmission.ParseTarget(output, out StorageTarget parsed);
            if (failure != null)
            {
                return AdmissionFailure(failure.Value);
            }

            target = new CliOutputTarget(parsed.Volume, parsed.Folders, parsed.Name);
            return null;
        }

        // Checks the parsed target without creating anything. The held walk refuses links and unsupported devices; the
        // parent itself must be on local NTFS, and the target must not exist. TryReserve repeats every check authoritatively.
        internal static CliFailure ProbeTarget(
            CliOutputTarget target,
            Func<SafeFileHandle, (string FileSystemName, string DevicePath)> probeStorageFacts = null)
        {
            ArgumentNullException.ThrowIfNull(target);
            var parsed =
                new StorageTarget(
                    target.Volume,
                    target.Folders,
                    target.Name);
            try
            {
                StorageAdmissionFailure? admission =
                    StorageAdmission.HoldParent(
                        parsed,
                        out StorageAnchor anchor,
                        cancellationToken:
                            CancellationToken.None);
                if (admission != null)
                {
                    return AdmissionFailure(
                        admission.Value);
                }

                using (anchor)
                {
                    if (AnchoredHandles.Exists(
                            anchor.Parent,
                            target.Name))
                    {
                        return new CliFailure(
                            CliExitCodes.Conflict,
                            "cli.output.exists",
                            "The --out folder already exists; xmat never overwrites output.");
                    }

                    // probeStorageFacts is a test seam; production reads both facts from the held parent handle.
                    (string FileSystemName, string DevicePath) storageFacts =
                        probeStorageFacts != null
                            ? probeStorageFacts(
                                anchor.Parent)
                            : (
                                FileSystemName:
                                    AnchoredHandles.FileSystemName(
                                        anchor.Parent),
                                DevicePath:
                                    AnchoredHandles.DevicePath(
                                        anchor.Parent));
                    if (!StorageAdmission.IsSupportedStorage(
                            storageFacts.FileSystemName,
                            storageFacts.DevicePath))
                    {
                        return Unprotected();
                    }

                    return null;
                }
            }
            catch (Exception ex) when (
                IsFileSystemFailure(ex))
            {
                return Unprotected();
            }
        }

        // Creates the private staging folder next to --out and holds it open, with every folder above it, as the class
        // comment describes, in steps that never follow a link nor adopt anything that is already there. A failure is the
        // reason that --out was refused: its parent does not exist (exit code 2), it exists (5), or it is not where xmat
        // can keep its output private (6). Other errors are thrown, and leave nothing behind but, at worst, an empty private
        // staging folder. checkpoint lets tests act between each check and its use: it is called with the name of each
        // folder above --out once that folder is held and checked, and with the staging folder's name right before it is
        // created.
        internal static CliFailure TryReserve(
            CliOutputTarget target, string runId, out CliOutputFolder folder, Action<string> checkpoint = null)
        {
            ArgumentNullException.ThrowIfNull(target);
            folder = null;
            var parsed = new StorageTarget(target.Volume, target.Folders, target.Name);
            StorageAdmissionFailure? admission =
                StorageAdmission.HoldParent(parsed, out StorageAnchor anchor, checkpoint, CancellationToken.None);
            if (admission != null)
            {
                return AdmissionFailure(admission.Value);
            }

            SafeFileHandle staging = null;
            try
            {
                if (AnchoredHandles.Exists(anchor.Parent, target.Name))
                {
                    return new CliFailure(
                        CliExitCodes.Conflict, "cli.output.exists", "The --out folder already exists; xmat never overwrites output.");
                }

                if (!StorageAdmission.IsSupportedStorage(
                        AnchoredHandles.FileSystemName(
                            anchor.Parent),
                        AnchoredHandles.DevicePath(
                            anchor.Parent)))
                {
                    return Unprotected();
                }

                checkpoint?.Invoke(StagingPrefix + runId);
                staging = AnchoredHandles.CreateNew(
                    anchor.Parent, StagingPrefix + runId, StorageAdmission.CurrentUserDirectorySecurity());

                // A folder that became a link after it was checked fails the creation above. Checked again now that each
                // held folder contains the next and the parent contains the staging folder, so none can become one any more.
                if (!anchor.IsChainAdmitted())
                {
                    Remove(staging);
                    staging = null;
                    return Unprotected();
                }

                folder = new CliOutputFolder(target.Name, AnchoredHandles.FinalPath(staging), anchor, staging);
                return null;
            }
            catch
            {
                if (staging != null)
                {
                    Remove(staging);
                }

                throw;
            }
            finally
            {
                if (folder == null)
                {
                    anchor.Dispose();
                }
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
                return StorageAdmission.IsSupportedStorage(
                        AnchoredHandles.FileSystemName(Handle), AnchoredHandles.DevicePath(Handle)) &&
                    StorageAdmission.IsPrivateDescriptor(AnchoredHandles.Security(Handle), identity.User);
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                return false;
            }
        }

        internal static bool IsFileSystemFailure(Exception exception) =>
            exception is IOException or UnauthorizedAccessException or SecurityException;

        // relativePath uses '/' separators, as in the JSON document.
        internal void WriteFile(string relativePath, byte[] contents) =>
            WriteFile(relativePath, stream => stream.Write(contents, 0, contents.Length));

        // Writes a new file with what write puts in its stream, so large contents need not be assembled in memory first.
        internal void WriteFile(string relativePath, Action<Stream> write)
        {
            using FileStream stream = CreateFile(relativePath);
            write(stream);
            stream.Flush(flushToDisk: true);
        }

        // A new file in the staging folder that the caller writes as the run goes and then closes, such as an archive
        // (relativePath uses '/'): never one that exists already, and opened by nothing else meanwhile. The caller closes it
        // before the run ends, so that a discard can remove it.
        internal FileStream CreateFile(string relativePath)
        {
            string path = Path.Join(StagingPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }

        // A reduced-rights duplicate of the already-held staging folder for an operation that opens one existing root file.
        internal SafeFileHandle DuplicateStagingHandle() =>
            AnchoredHandles.DuplicateDirectoryHandle(
                Handle);

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

        // The commit: renames the staging folder to --out in one step, through the handle that holds it and relative to the
        // held parent, retrying for about a second. Cancellation is checked before each attempt (OperationCanceledException);
        // returns false when --out appeared meanwhile, a dangling link included, which is never replaced. A rename that
        // succeeded is the publication: from then on nothing removes the folder, whatever follows.
        internal bool TryCommit(ICliCommitObserver observer, CancellationToken cancellationToken)
        {
            for (int attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (AnchoredHandles.Exists(Parent, _name))
                {
                    return false;
                }

                try
                {
                    AnchoredHandles.Rename(Handle, Parent, _name);
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
                        AnchoredHandles.MarkForDeletion(Handle);
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

        // Closes the handles without removing anything, if a commit or discard has not already.
        public void Dispose() => Release();

        private SafeFileHandle Handle => _handle ?? throw new ObjectDisposedException(nameof(CliOutputFolder));

        private SafeFileHandle Parent =>
            _anchor?.Parent ?? throw new ObjectDisposedException(nameof(CliOutputFolder));

        // Lets go of the staging folder, then of the folders above it.
        private void Release()
        {
            Interlocked.Exchange(ref _handle, null)?.Dispose();
            Interlocked.Exchange(ref _anchor, null)?.Dispose();
        }

        // Removes a staging folder that is still empty, and lets go of it.
        private static void Remove(SafeFileHandle staging)
        {
            try
            {
                AnchoredHandles.MarkForDeletion(staging);
            }
            catch (IOException)
            {
                // The folder stays behind, empty and private.
            }

            staging.Dispose();
        }

        private static CliFailure InvalidPath() =>
            new(CliExitCodes.Usage, "cli.usage.invalid-value", "--out is not a valid folder path.");

        private static CliFailure ParentMissing() =>
            new(CliExitCodes.Usage, "cli.output.parent-missing", "The folder that should contain the --out folder does not exist.");

        private static CliFailure Unprotected() =>
            new(
                CliExitCodes.Failed,
                "cli.output.unprotected",
                "xmat cannot keep its output private there: the --out folder must be on a local NTFS volume and reached " +
                    "without a network path, a mapped or substituted drive, a junction, a mounted folder or a symbolic link.");

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

        private static CliFailure AdmissionFailure(StorageAdmissionFailure failure) => failure switch
        {
            StorageAdmissionFailure.InvalidPath => InvalidPath(),
            StorageAdmissionFailure.ParentMissing => ParentMissing(),
            _ => Unprotected(),
        };
    }

    // --out as CheckTarget reads it, without touching the file system: the drive (C:) or volume name (Volume{...}) that
    // holds it, the folders from that volume's root down to the one that contains it, and its own name.
    internal sealed record CliOutputTarget(string Volume, IReadOnlyList<string> Folders, string Name);
}
