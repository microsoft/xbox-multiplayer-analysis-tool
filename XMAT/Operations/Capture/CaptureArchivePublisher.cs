// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using XMAT.Operations.Storage;

namespace XMAT.Operations.Capture
{
    internal enum CaptureArchiveKind
    {
        Private,
        Copy,
    }

    internal enum CaptureArchiveEntry
    {
        Absent,
        File,
    }

    internal sealed class CaptureArchiveTarget : IDisposable
    {
        private HeldFolder _destination;
        private HeldFolder _work;

        internal CaptureArchiveTarget(
            CaptureArchiveKind kind,
            bool remote,
            CaptureArchiveEntry entry,
            string fileName,
            string folderName,
            HeldFolder destination,
            HeldFolder work,
            CaptureArchiveFingerprint fingerprint)
        {
            Kind = kind;
            Remote = remote;
            Entry = entry;
            FileName = fileName;
            FolderName = folderName;
            _destination = destination;
            _work = work;
            Fingerprint = fingerprint;
        }

        internal CaptureArchiveKind Kind { get; }

        internal bool Remote { get; }

        internal CaptureArchiveEntry Entry { get; }

        internal string FileName { get; }

        internal string FolderName { get; }

        internal CaptureArchiveFingerprint Fingerprint { get; }

        internal SafeFileHandle DestinationFolder =>
            (_destination ??
             throw new ObjectDisposedException(
                 nameof(CaptureArchiveTarget))).Handle;

        internal SafeFileHandle StagingFolder =>
            Kind == CaptureArchiveKind.Private
                ? DestinationFolder
                : (_work ??
                   throw new ObjectDisposedException(
                       nameof(CaptureArchiveTarget))).Handle;

        internal CaptureArchiveReplaceDecision ConfirmReplace()
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (Entry != CaptureArchiveEntry.File ||
                Fingerprint == null)
            {
                throw new InvalidOperationException(
                    "Only an admitted file can be confirmed for replacement.");
            }

            return new CaptureArchiveReplaceDecision(
                this,
                Fingerprint);
        }

        internal bool IsDisposed =>
            _destination == null;

        public void Dispose()
        {
            HeldFolder work =
                Interlocked.Exchange(ref _work, null);
            HeldFolder destination =
                Interlocked.Exchange(
                    ref _destination,
                    null);
            work?.Dispose();
            destination?.Dispose();
        }
    }

    internal sealed class CaptureArchiveReplaceDecision
    {
        internal CaptureArchiveReplaceDecision(
            CaptureArchiveTarget target,
            CaptureArchiveFingerprint fingerprint)
        {
            Target = target;
            Fingerprint = fingerprint;
        }

        internal CaptureArchiveTarget Target { get; }

        internal CaptureArchiveFingerprint Fingerprint { get; }
    }

    internal enum SazExportPublication
    {
        Written,
        Created,
        Replaced,
        NotPublished,
        Uncertain,
    }

    internal enum SazExportUncertainty
    {
        None,
        Rename,
        CopyCreated,
        CopyReplaced,
        CopyUnverified,
    }

    internal enum SazExportResidueKind
    {
        Staging,
        EarlierFile,
    }

    internal sealed record SazExportResidue(
        SazExportResidueKind Kind,
        string Name,
        string FolderName);

    internal sealed record CaptureArchivePublicationResult(
        SazExportPublication Publication,
        SazExportUncertainty Uncertainty,
        string Code,
        IReadOnlyList<SazExportResidue> Residues);

    internal static class CaptureArchivePublisher
    {
        private const int NameAttempts = 4;
        private const int RandomBytes = 16;
        private const string StagingPrefix = ".xmat-";
        private const string StagingSuffix = ".tmp";

        internal static CaptureArchiveTarget Admit(
            string path,
            string workingDirectory,
            CancellationToken cancellationToken) =>
            Admit(
                path,
                workingDirectory,
                null,
                cancellationToken);

        internal static CaptureArchiveTarget Admit(
            string path,
            string workingDirectory,
            SazExportTestHooks hooks,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(
                    Path.GetExtension(path),
                    ".saz",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw Refusal(
                    SazExportCodes.SazRequired,
                    "The archive destination must end in .saz.");
            }

            string fullPath;
            string folderPath;
            string fileName;
            try
            {
                fullPath = Path.GetFullPath(path);
                folderPath = Path.GetDirectoryName(fullPath);
                fileName = Path.GetFileName(fullPath);
            }
            catch (Exception exception) when (
                exception is ArgumentException or
                    NotSupportedException or
                    IOException)
            {
                throw Refusal(
                    SazExportCodes.UnsupportedLocation,
                    "The archive destination is not supported.");
            }

            if (string.IsNullOrEmpty(folderPath) ||
                !IsName(fileName))
            {
                throw Refusal(
                    SazExportCodes.UnsupportedLocation,
                    "The archive destination is not supported.");
            }

            HeldFolder destination = null;
            HeldFolder work = null;
            try
            {
                destination = HoldDestination(
                    fullPath,
                    folderPath,
                    cancellationToken);
                string fileSystem =
                    AnchoredHandles.FileSystemName(
                        destination.Handle);
                string device =
                    AnchoredHandles.DevicePath(
                        destination.Handle);
                CaptureArchiveKind actualKind =
                    StorageAdmission.IsSupportedStorage(
                        fileSystem,
                        device)
                        ? CaptureArchiveKind.Private
                        : CaptureArchiveKind.Copy;
                CaptureArchiveKind kind =
                    hooks?.AdmitAsKind ??
                    actualKind;
                bool remote =
                    hooks?.AdmitAsRemote ??
                    !StorageAdmission.IsLocalVolumeDevice(
                        device);
                if (kind == CaptureArchiveKind.Copy)
                {
                    if (hooks?.RefuseWorkFolder == true)
                    {
                        throw Refusal(
                            SazExportCodes.NoPrivateStaging,
                            "The private archive staging folder is unavailable.");
                    }

                    work = HoldPrivateWorkFolder(
                        workingDirectory,
                        cancellationToken);
                }

                CaptureArchiveEntry entry;
                CaptureArchiveFingerprint fingerprint = null;
                using (SafeFileHandle existing =
                    AnchoredHandles.OpenEntryForAttributes(
                        destination.Handle,
                        fileName,
                        out AnchoredOpenFailure failure))
                {
                    if (existing == null)
                    {
                        if (failure !=
                            AnchoredOpenFailure.Missing)
                        {
                            throw Refusal(
                                SazExportCodes.AccessDenied,
                                "The archive destination could not be examined.");
                        }

                        entry = CaptureArchiveEntry.Absent;
                    }
                    else
                    {
                        AnchoredFileFacts facts =
                            AnchoredHandles.FileFacts(
                                existing);
                        if (facts.IsDirectory ||
                            facts.IsReparsePoint)
                        {
                            throw Refusal(
                                SazExportCodes.NotAFile,
                                "The archive destination is not a regular file.");
                        }

                        if (facts.IsReadOnly)
                        {
                            throw Refusal(
                                SazExportCodes.ReadOnly,
                                "The archive destination is read-only.");
                        }

                        entry = CaptureArchiveEntry.File;
                        fingerprint =
                            CaptureArchiveFingerprint.From(
                                facts);
                    }
                }

                var target = new CaptureArchiveTarget(
                    kind,
                    remote,
                    entry,
                    fileName,
                    folderPath,
                    destination,
                    work,
                    fingerprint);
                destination = null;
                work = null;
                return target;
            }
            catch (SazExportException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (
                !ExceptionUtilities.IsFatal(exception))
            {
                throw Refusal(
                    SazExportCodes.UnsupportedLocation,
                    "The archive destination is not supported.");
            }
            finally
            {
                work?.Dispose();
                destination?.Dispose();
            }
        }

        internal static CaptureArchivePublication BeginPublication(
            CaptureArchiveTarget target,
            CaptureArchiveReplaceDecision replaceDecision,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(target);
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(
                target.IsDisposed,
                target);
            if (replaceDecision != null &&
                !ReferenceEquals(
                    replaceDecision.Target,
                    target))
            {
                throw Refusal(
                    SazExportCodes.Changed,
                    "The replacement decision does not belong to this destination.");
            }

            SafeFileHandle staging = null;
            try
            {
                for (int attempt = 0;
                    attempt < NameAttempts &&
                    staging == null;
                    attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    staging =
                        AnchoredHandles.CreateNewReadableFile(
                            target.StagingFolder,
                            NewName(StagingSuffix),
                            StorageAdmission
                                .CurrentUserFileSecurity());
                }

                if (staging == null ||
                    !StorageAdmission.IsPrivateFile(
                        staging))
                {
                    throw Refusal(
                        SazExportCodes.WriteFailed,
                        "The private archive staging file could not be created.");
                }

                AnchoredHandles.MarkFileForDeletion(
                    staging);
                var stream = new FileStream(
                    staging,
                    FileAccess.ReadWrite,
                    64 * 1024,
                    isAsync: false);
                staging = null;
                return new CaptureArchivePublication(
                    target,
                    replaceDecision,
                    stream);
            }
            catch
            {
                staging?.Dispose();
                throw;
            }
        }

        internal static string NewName(string suffix)
        {
            Span<byte> random =
                stackalloc byte[RandomBytes];
            RandomNumberGenerator.Fill(random);
            return StagingPrefix +
                Convert.ToHexString(random) +
                suffix;
        }

        private static HeldFolder HoldDestination(
            string fullPath,
            string folderPath,
            CancellationToken cancellationToken)
        {
            if (StorageAdmission.ParseTarget(
                    fullPath,
                    out StorageTarget parsed) == null &&
                StorageAdmission.HoldParent(
                    parsed,
                    out StorageAnchor anchor,
                    null,
                    cancellationToken) == null)
            {
                return new HeldFolder(
                    anchor.Parent,
                    anchor);
            }

            cancellationToken.ThrowIfCancellationRequested();
            SafeFileHandle handle =
                AnchoredHandles.OpenFolderByPath(
                    folderPath);
            return new HeldFolder(
                handle,
                handle);
        }

        private static HeldFolder HoldPrivateWorkFolder(
            string path,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw Refusal(
                    SazExportCodes.NoPrivateStaging,
                    "The private archive staging folder is unavailable.");
            }

            string probe;
            try
            {
                probe = Path.Combine(
                    Path.GetFullPath(path),
                    "capture.saz");
            }
            catch (Exception exception) when (
                exception is ArgumentException or
                    NotSupportedException or
                    IOException)
            {
                throw Refusal(
                    SazExportCodes.NoPrivateStaging,
                    "The private archive staging folder is unavailable.");
            }

            if (StorageAdmission.ParseTarget(
                    probe,
                    out StorageTarget target) != null ||
                StorageAdmission.HoldParent(
                    target,
                    out StorageAnchor anchor,
                    null,
                    cancellationToken) != null)
            {
                throw Refusal(
                    SazExportCodes.NoPrivateStaging,
                    "The private archive staging folder is unavailable.");
            }

            if (!StorageAdmission.IsSupportedStorage(
                    AnchoredHandles.FileSystemName(
                        anchor.Parent),
                    AnchoredHandles.DevicePath(
                        anchor.Parent)))
            {
                anchor.Dispose();
                throw Refusal(
                    SazExportCodes.NoPrivateStaging,
                    "The private archive staging folder is unavailable.");
            }

            return new HeldFolder(
                anchor.Parent,
                anchor);
        }

        private static bool IsName(string name) =>
            name.Length is > 0 and <= 255 &&
            name != "." &&
            name != ".." &&
            name.All(
                static character =>
                    character >= ' ' &&
                    character is not (
                        '"' or '*' or '/' or ':' or
                        '<' or '>' or '?' or '\\' or '|'));

        private static SazExportException Refusal(
            string code,
            string message) =>
            new(code, message);
    }

    internal sealed class CaptureArchivePublication : IDisposable
    {
        private const int CopyBufferBytes = 1024 * 1024;
        private CaptureArchiveTarget _target;
        private readonly CaptureArchiveReplaceDecision _decision;
        private FileStream _stream;
        private bool _prepared;
        private bool _completed;

        internal CaptureArchivePublication(
            CaptureArchiveTarget target,
            CaptureArchiveReplaceDecision decision,
            FileStream stream)
        {
            _target = target;
            _decision = decision;
            _stream = stream;
        }

        internal Stream Stream =>
            _stream ??
            throw new ObjectDisposedException(
                nameof(CaptureArchivePublication));

        internal void PrepareCommit()
        {
            ThrowOpen();
            _stream.Flush();
            AnchoredHandles.FlushFileBuffers(
                _stream.SafeFileHandle);
            _prepared = true;
        }

        internal CaptureArchivePublicationResult Commit()
        {
            ThrowOpen();
            if (!_prepared)
            {
                PrepareCommit();
            }

            CaptureArchivePublicationResult result =
                _target.Kind ==
                    CaptureArchiveKind.Private
                    ? CommitPrivate()
                    : CommitCopy();
            _completed = true;
            Close();
            return result;
        }

        internal CaptureArchivePublicationResult Abort()
        {
            ThrowOpen();
            _completed = true;
            Close();
            return new(
                SazExportPublication.NotPublished,
                SazExportUncertainty.None,
                SazExportCodes.WriteFailed,
                Array.Empty<SazExportResidue>());
        }

        public void Dispose()
        {
            if (!_completed &&
                _stream != null)
            {
                try
                {
                    Abort();
                }
                catch (Exception exception) when (
                    !ExceptionUtilities.IsFatal(exception))
                {
                    Close();
                }
            }
        }

        private CaptureArchivePublicationResult CommitPrivate()
        {
            SafeFileHandle staging =
                _stream.SafeFileHandle;
            try
            {
                AnchoredHandles.ClearFileDeletion(
                    staging);
            }
            catch (IOException)
            {
                return Uncertain(
                    SazExportUncertainty.Rename);
            }

            if (_decision == null)
            {
                if (AnchoredHandles.TryRenameFile(
                    staging,
                    _target.DestinationFolder,
                    _target.FileName))
                {
                    return Published(
                        SazExportPublication.Created);
                }

                MarkStagingForDeletion();
                return NotPublished(
                    SazExportCodes.Exists);
            }

            using SafeFileHandle earlier =
                AnchoredHandles.OpenFileForMove(
                    _target.DestinationFolder,
                    _target.FileName,
                    out AnchoredOpenFailure openFailure);
            if (earlier == null)
            {
                if (openFailure ==
                    AnchoredOpenFailure.Missing)
                {
                    if (AnchoredHandles.TryRenameFile(
                        staging,
                        _target.DestinationFolder,
                        _target.FileName))
                    {
                        return Published(
                            SazExportPublication.Created);
                    }

                    MarkStagingForDeletion();
                    return NotPublished(
                        SazExportCodes.Exists);
                }

                MarkStagingForDeletion();
                return NotPublished(
                    OpenCode(openFailure));
            }

            if (!_decision.Fingerprint.Matches(
                AnchoredHandles.FileFacts(
                    earlier)))
            {
                MarkStagingForDeletion();
                return NotPublished(
                    SazExportCodes.Changed);
            }

            string earlierName =
                CaptureArchivePublisher.NewName(
                    ".old");
            if (!AnchoredHandles.TryRenameFile(
                earlier,
                _target.DestinationFolder,
                earlierName))
            {
                MarkStagingForDeletion();
                return NotPublished(
                    SazExportCodes.Exists);
            }

            if (!AnchoredHandles.TryRenameFile(
                staging,
                _target.DestinationFolder,
                _target.FileName))
            {
                MarkStagingForDeletion();
                if (AnchoredHandles.TryRenameFile(
                    earlier,
                    _target.DestinationFolder,
                    _target.FileName))
                {
                    return NotPublished(
                        SazExportCodes.Exists);
                }

                return new(
                    SazExportPublication.NotPublished,
                    SazExportUncertainty.None,
                    SazExportCodes.Exists,
                    new[]
                    {
                        new SazExportResidue(
                            SazExportResidueKind.EarlierFile,
                            earlierName,
                            _target.FolderName),
                    });
            }

            try
            {
                AnchoredHandles.MarkFileForDeletion(
                    earlier);
                return Published(
                    SazExportPublication.Replaced);
            }
            catch (IOException)
            {
                return new(
                    SazExportPublication.Replaced,
                    SazExportUncertainty.None,
                    null,
                    new[]
                    {
                        new SazExportResidue(
                            SazExportResidueKind.EarlierFile,
                            earlierName,
                            _target.FolderName),
                    });
            }
        }

        private CaptureArchivePublicationResult CommitCopy()
        {
            if (_decision == null)
            {
                using SafeFileHandle createdDestination =
                    AnchoredHandles.CreateNewCopyFile(
                        _target.DestinationFolder,
                        _target.FileName,
                        out AnchoredOpenFailure failure);
                if (createdDestination == null)
                {
                    return NotPublished(
                        failure ==
                            AnchoredOpenFailure.Missing
                            ? SazExportCodes.Exists
                            : OpenCode(failure));
                }

                return Copy(
                    createdDestination,
                    replacing: false);
            }

            using SafeFileHandle destination =
                AnchoredHandles.OpenFileForCopy(
                    _target.DestinationFolder,
                    _target.FileName,
                    out AnchoredOpenFailure openFailure);
            if (destination == null)
            {
                if (openFailure ==
                    AnchoredOpenFailure.Missing)
                {
                    using SafeFileHandle created =
                        AnchoredHandles.CreateNewCopyFile(
                            _target.DestinationFolder,
                            _target.FileName,
                            out AnchoredOpenFailure createFailure);
                    return created == null
                        ? NotPublished(
                            OpenCode(createFailure))
                        : Copy(
                            created,
                            replacing: false);
                }

                return NotPublished(
                    OpenCode(openFailure));
            }

            if (!_decision.Fingerprint.Matches(
                AnchoredHandles.FileFacts(
                    destination)))
            {
                return NotPublished(
                    SazExportCodes.Changed);
            }

            return Copy(
                destination,
                replacing: true);
        }

        private CaptureArchivePublicationResult Copy(
            SafeFileHandle destination,
            bool replacing)
        {
            FileStream output = null;
            try
            {
                _stream.Position = 0;
                output = new FileStream(
                    destination,
                    FileAccess.ReadWrite,
                    64 * 1024,
                    isAsync: false);
                output.SetLength(0);
                byte[] buffer =
                    new byte[CopyBufferBytes];
                int read;
                while ((read = _stream.Read(
                    buffer,
                    0,
                    buffer.Length)) != 0)
                {
                    output.Write(
                        buffer,
                        0,
                        read);
                }

                output.Flush();
                AnchoredHandles.FlushFileBuffers(
                    output.SafeFileHandle);
                return Published(
                    replacing
                        ? SazExportPublication.Replaced
                        : SazExportPublication.Created);
            }
            catch (Exception exception) when (
                exception is IOException or
                    UnauthorizedAccessException)
            {
                if (replacing)
                {
                    return Uncertain(
                        SazExportUncertainty.CopyReplaced);
                }

                try
                {
                    AnchoredHandles.MarkFileForDeletion(
                        output?.SafeFileHandle ??
                        destination);
                    return NotPublished(
                        SazExportCodes.WriteFailed);
                }
                catch (Exception cleanup) when (
                    !ExceptionUtilities.IsFatal(cleanup))
                {
                    return Uncertain(
                        SazExportUncertainty.CopyCreated);
                }
            }
            finally
            {
                output?.Dispose();
            }
        }

        private void MarkStagingForDeletion()
        {
            try
            {
                AnchoredHandles.MarkFileForDeletion(
                    _stream.SafeFileHandle);
            }
            catch (Exception exception) when (
                !ExceptionUtilities.IsFatal(exception))
            {
            }
        }

        private CaptureArchivePublicationResult Published(
            SazExportPublication publication) =>
            new(
                publication,
                SazExportUncertainty.None,
                null,
                Array.Empty<SazExportResidue>());

        private CaptureArchivePublicationResult NotPublished(
            string code) =>
            new(
                SazExportPublication.NotPublished,
                SazExportUncertainty.None,
                code,
                Array.Empty<SazExportResidue>());

        private CaptureArchivePublicationResult Uncertain(
            SazExportUncertainty uncertainty) =>
            new(
                SazExportPublication.Uncertain,
                uncertainty,
                SazExportCodes.Uncertain,
                Array.Empty<SazExportResidue>());

        private static string OpenCode(
            AnchoredOpenFailure failure) =>
            failure switch
            {
                AnchoredOpenFailure.Missing =>
                    SazExportCodes.Changed,
                AnchoredOpenFailure.SharingViolation =>
                    SazExportCodes.InUse,
                _ => SazExportCodes.AccessDenied,
            };

        private void ThrowOpen()
        {
            if (_completed ||
                _stream == null)
            {
                throw new InvalidOperationException(
                    "The archive publication is already complete.");
            }
        }

        private void Close()
        {
            FileStream stream =
                Interlocked.Exchange(
                    ref _stream,
                    null);
            stream?.Dispose();
            _target = null;
        }
    }

    internal sealed class HeldFolder : IDisposable
    {
        private IDisposable _owner;

        internal HeldFolder(
            SafeFileHandle handle,
            IDisposable owner)
        {
            Handle = handle;
            _owner = owner;
        }

        internal SafeFileHandle Handle { get; }

        public void Dispose()
        {
            Interlocked.Exchange(
                ref _owner,
                null)?.Dispose();
        }
    }

    internal sealed record CaptureArchiveFingerprint(
        uint VolumeSerialNumber,
        ulong FileIndex,
        long Length,
        long LastWriteTime,
        uint Attributes)
    {
        internal static CaptureArchiveFingerprint From(
            AnchoredFileFacts facts) =>
            new(
                facts.VolumeSerialNumber,
                facts.FileIndex,
                facts.Length,
                facts.LastWriteTime,
                facts.Attributes);

        internal bool Matches(
            AnchoredFileFacts facts) =>
            this == From(facts);
    }
}
