// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace XMAT.Operations.Storage
{
    // Why a path cannot be admitted to protected local storage. Callers give these neutral reasons their own diagnostics
    // and exit codes.
    internal enum StorageAdmissionFailure
    {
        InvalidPath,
        ParentMissing,
        Unsupported,
    }

    // A target path read without touching the file system: the drive (C:) or volume name (Volume{...}) that holds it, the
    // folders from that volume's root down to the one that contains it, and its own name.
    internal sealed record StorageTarget(string Volume, IReadOnlyList<string> Folders, string Name);

    // The held walk from a volume root to a target's parent. Every handle was opened relative to the preceding one without
    // following a link, and none of the held folders can be renamed or removed until this anchor is disposed.
    internal sealed class StorageAnchor : IDisposable
    {
        private SafeFileHandle[] _folders;

        internal StorageAnchor(SafeFileHandle[] folders)
        {
            _folders = folders ?? throw new ArgumentNullException(nameof(folders));
        }

        internal SafeFileHandle Parent =>
            _folders?[^1] ?? throw new ObjectDisposedException(nameof(StorageAnchor));

        // A folder that became a link after its first check is refused. The volume root itself is not a name in a parent.
        internal bool IsChainAdmitted()
        {
            SafeFileHandle[] folders = _folders ?? throw new ObjectDisposedException(nameof(StorageAnchor));
            return folders.Skip(1).All(handle => StorageAdmission.IsAdmittedReparseTag(AnchoredHandles.ReparseTag(handle)));
        }

        public void Dispose()
        {
            foreach (SafeFileHandle handle in Interlocked.Exchange(ref _folders, null) ?? Array.Empty<SafeFileHandle>())
            {
                handle.Dispose();
            }
        }
    }

    // Admission to storage where xmat can keep content private and pin every namespace component. Parsing touches no file
    // system. Holding starts from the volume's NT device, refuses network, substituted and unsupported devices, and opens
    // every folder relative to the one already held without following name-surrogate reparse points.
    internal static class StorageAdmission
    {
        private const string LocalVolumePrefix = @"\Device\HarddiskVolume";

        // Reads a target without touching the file system: a path on a drive (C:\...), or a verbatim one on a drive or
        // volume (\\?\C:\..., \\?\Volume{...}\...). Network and device paths are refused before anything could reach them.
        internal static StorageAdmissionFailure? ParseTarget(string path, out StorageTarget target)
        {
            target = null;
            string fullPath;
            try
            {
                fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or SecurityException)
            {
                return StorageAdmissionFailure.InvalidPath;
            }

            bool verbatim = fullPath.StartsWith(@"\\?\", StringComparison.Ordinal);
            string rest = verbatim ? fullPath[4..] : fullPath;
            string volume = DriveName(rest) ?? (verbatim ? VolumeName(rest) : null);
            if (volume == null)
            {
                return StorageAdmissionFailure.Unsupported;
            }

            rest = rest.Length > volume.Length + 1 ? rest[(volume.Length + 1)..] : string.Empty;
            string[] names = rest.Length == 0 ? Array.Empty<string>() : rest.Split('\\');
            if (names.Length == 0)
            {
                return StorageAdmissionFailure.ParentMissing;
            }

            if (!names.All(IsName))
            {
                return StorageAdmissionFailure.InvalidPath;
            }

            target = new StorageTarget(volume, names[..^1], names[^1]);
            return null;
        }

        // Opens and holds a target's parent from its volume root. checkpoint runs after each named folder is held and
        // checked, at the same point used by the existing namespace-race tests.
        internal static StorageAdmissionFailure? HoldParent(
            StorageTarget target,
            out StorageAnchor anchor,
            Action<string> checkpoint = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(target);
            anchor = null;
            cancellationToken.ThrowIfCancellationRequested();
            string device = AnchoredHandles.QueryDosDevice(target.Volume);
            if (device == null)
            {
                return StorageAdmissionFailure.ParentMissing;
            }

            if (!IsLocalVolumeDevice(device))
            {
                return StorageAdmissionFailure.Unsupported;
            }

            var folders = new List<SafeFileHandle>();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                folders.Add(AnchoredHandles.OpenVolumeRoot(device));
                foreach (string name in target.Folders)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SafeFileHandle next = AnchoredHandles.OpenFolder(folders[^1], name);
                    if (next == null)
                    {
                        return StorageAdmissionFailure.ParentMissing;
                    }

                    folders.Add(next);
                    if (!IsAdmittedReparseTag(AnchoredHandles.ReparseTag(next)))
                    {
                        return StorageAdmissionFailure.Unsupported;
                    }

                    checkpoint?.Invoke(name);
                }

                anchor = new StorageAnchor(folders.ToArray());
                return null;
            }
            finally
            {
                if (anchor == null)
                {
                    foreach (SafeFileHandle handle in folders)
                    {
                        handle.Dispose();
                    }
                }
            }
        }

        // The device of a local volume, as a drive or volume name stands for it: \Device\HarddiskVolumeN, and nothing else,
        // such as a network redirector, a substituted drive (\??\C:\...) or a shadow copy.
        internal static bool IsLocalVolumeDevice(string device) =>
            device != null &&
            device.Length > LocalVolumePrefix.Length &&
            device.StartsWith(LocalVolumePrefix, StringComparison.OrdinalIgnoreCase) &&
            device[LocalVolumePrefix.Length..].All(char.IsAsciiDigit);

        // A held folder may be a reparse point only of a kind that leaves names alone: Cloud Files (OneDrive), data
        // deduplication and Windows Overlay Filter compression. Junctions, mounted folders, symbolic links, any other name
        // surrogate and any unknown kind are refused.
        internal static bool IsAdmittedReparseTag(uint? tag) =>
            tag is null ||
            (tag.Value & 0xFFFF0FFF) == 0x9000001A ||
            tag.Value is 0x80000013 or 0x80000017;

        // Only a local NTFS volume (\Device\HarddiskVolumeN) is supported. Network shares, including mapped drives, and
        // other file systems are not.
        internal static bool IsSupportedStorage(string fileSystemName, string devicePath) =>
            string.Equals(fileSystemName, "NTFS", StringComparison.OrdinalIgnoreCase) &&
            devicePath != null &&
            devicePath.StartsWith(LocalVolumePrefix, StringComparison.OrdinalIgnoreCase);

        // True only when user owns the folder, it inherits nothing, its only entries allow user, and they pass on to
        // everything created in it (container and object inherit, with no propagation limit).
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

        // True only when user owns the file, it inherits nothing, and every explicit entry is an allow entry for user with
        // no inheritance or propagation flags. At least one such entry must be present.
        internal static bool IsPrivateFileDescriptor(FileSecurity security, SecurityIdentifier user)
        {
            if (security == null || user == null || !security.AreAccessRulesProtected ||
                !user.Equals(security.GetOwner(typeof(SecurityIdentifier))))
            {
                return false;
            }

            AuthorizationRuleCollection rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
            bool found = false;
            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.IsInherited ||
                    rule.AccessControlType != AccessControlType.Allow ||
                    !user.Equals(rule.IdentityReference) ||
                    rule.InheritanceFlags != InheritanceFlags.None ||
                    rule.PropagationFlags != PropagationFlags.None)
                {
                    return false;
                }

                found = true;
            }

            return found;
        }

        // A private owner-only descriptor for a directory and everything created below it.
        internal static DirectorySecurity CurrentUserDirectorySecurity()
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

        // A private owner-only descriptor for one file.
        internal static FileSecurity CurrentUserFileSecurity()
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            var security = new FileSecurity();
            security.SetOwner(identity.User);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                identity.User,
                FileSystemRights.FullControl,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow));
            return security;
        }

        // Reads a file through its handle and verifies local NTFS storage, current-user ownership and an owner-only DACL.
        // False when any of them cannot be read.
        internal static bool IsPrivateFile(SafeFileHandle handle)
        {
            try
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                return IsSupportedStorage(AnchoredHandles.FileSystemName(handle), AnchoredHandles.DevicePath(handle)) &&
                    IsPrivateFileDescriptor(AnchoredHandles.SecurityForFile(handle), identity.User);
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                return false;
            }
        }

        // "C:" when path starts with a drive's root (C:\) or is the drive itself.
        private static string DriveName(string path) =>
            path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path.Length == 2 || path[2] == '\\')
                ? path[..2]
                : null;

        // "Volume{...}" when path starts with a volume name, as in \\?\Volume{...}\.
        private static string VolumeName(string path)
        {
            const string Prefix = "Volume{";
            int length = Prefix.Length + 37;
            return path.Length >= length &&
                path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) &&
                path[length - 1] == '}' &&
                (path.Length == length || path[length] == '\\') &&
                Guid.TryParseExact(path[Prefix.Length..(length - 1)], "D", out _)
                ? path[..length]
                : null;
        }

        // One path component, used as it is, so nothing that Windows would read otherwise: not "." or "..", and no
        // separator, stream (':'), wildcard or control character.
        private static bool IsName(string name) =>
            name.Length is > 0 and <= 255 &&
            name != "." &&
            name != ".." &&
            name.All(character => character >= ' ' && character is not ('"' or '*' or '/' or ':' or '<' or '>' or '?' or '\\' or '|'));
    }
}
