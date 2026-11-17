// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace XMAT.Operations.Storage
{
    // The anchored folder and file operations behind protected storage, each relative to a folder that xmat holds open,
    // so that no path is looked up again: opening a folder never follows a link, creating an entry never adopts what is
    // already there, and renaming one never replaces anything. A folder held through OpenVolumeRoot or OpenFolder cannot
    // be renamed or removed while it is held, nor can any folder that contains it. Errors are IOException without paths
    // in their messages.
    internal static class AnchoredHandles
    {
        private const uint Delete = 0x00010000;
        private const uint ReadControl = 0x00020000;
        private const uint Synchronize = 0x00100000;
        private const uint ListDirectory = 0x0001;
        private const uint WriteData = 0x0002;
        private const uint AddFile = 0x0002;
        private const uint AddSubdirectory = 0x0004;
        private const uint Traverse = 0x0020;
        private const uint ReadAttributes = 0x0080;
        private const uint ShareRead = 0x00000001;
        private const uint ShareWrite = 0x00000002;
        private const uint ShareDelete = 0x00000004;
        private const uint FileOpen = 1;
        private const uint FileCreate = 2;
        private const uint DirectoryFile = 0x00000001;
        private const uint SynchronousIoNonAlert = 0x00000020;
        private const uint NonDirectoryFile = 0x00000040;
        private const uint OpenReparsePoint = 0x00200000;
        private const uint ObjectCaseInsensitive = 0x00000040;
        private const uint FileAttributeReparsePoint = 0x00000400;
        private const uint VolumeNameGuid = 0x1;
        private const uint VolumeNameNt = 0x2;
        private const int FileDispositionInfoClass = 4;
        private const int FileAttributeTagInfoClass = 9;
        private const int FileRenameInformationClass = 10;
        private const int FileObject = 1;
        private const uint OwnerSecurityInformation = 0x1;
        private const uint DaclSecurityInformation = 0x4;
        private const int ErrorFileNotFound = 2;
        private const int ErrorInsufficientBuffer = 122;
        private const int StatusObjectNameNotFound = unchecked((int)0xC0000034);
        private const int StatusObjectNameCollision = unchecked((int)0xC0000035);
        private const int StatusObjectPathNotFound = unchecked((int)0xC000003A);
        private const int StatusNotADirectory = unchecked((int)0xC0000103);
        private const int StatusAccessDenied = unchecked((int)0xC0000022);
        private const int StatusSharingViolation = unchecked((int)0xC0000043);
        private const int StatusDeletePending = unchecked((int)0xC0000056);

        // A held folder can be listed and shares reading and writing but not deletion: while it is held, it cannot be
        // renamed or removed. Its own share mode counts only because of the listing access.
        private const uint HoldAccess = ListDirectory | ReadAttributes | Synchronize;
        private const uint HoldShare = ShareRead | ShareWrite;

        // The NT device that a drive (C:) or volume name (Volume{...}) stands for in this logon session, such as
        // \Device\HarddiskVolume3, or null when it stands for none. Reads only the name's link: no volume is opened.
        internal static string QueryDosDevice(string name)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
            for (int size = 512; ; size *= 2)
            {
                var buffer = new char[size];
                uint length = NativeMethods.QueryDosDeviceW(name, buffer, (uint)buffer.Length);
                if (length != 0)
                {
                    int end = Array.IndexOf(buffer, '\0');
                    return new string(buffer, 0, end < 0 ? (int)length : end);
                }

                int error = Marshal.GetLastWin32Error();
                if (error == ErrorFileNotFound)
                {
                    return null;
                }

                if (error != ErrorInsufficientBuffer || size >= 32768)
                {
                    throw new IOException("The volume could not be identified.", new Win32Exception(error));
                }
            }
        }

        // Opens and holds the root folder of the volume that devicePath (\Device\HarddiskVolumeN) names, by that path, so
        // no drive letter or volume name is looked up again.
        internal static SafeFileHandle OpenVolumeRoot(string devicePath)
        {
            ArgumentException.ThrowIfNullOrEmpty(devicePath);
            SafeFileHandle handle = Open(
                null,
                devicePath + @"\",
                HoldAccess,
                HoldShare,
                FileOpen,
                DirectoryFile | SynchronousIoNonAlert | OpenReparsePoint,
                null,
                out int status);
            return handle ?? throw Failure("The volume could not be opened.", status);
        }

        // Opens and holds the folder name in parent as itself: a link is opened, never followed. Null when parent has no
        // folder by that name.
        internal static SafeFileHandle OpenFolder(SafeFileHandle parent, string name)
        {
            ArgumentNullException.ThrowIfNull(parent);
            ArgumentException.ThrowIfNullOrEmpty(name);
            SafeFileHandle handle = Open(
                parent,
                name,
                HoldAccess,
                HoldShare,
                FileOpen,
                DirectoryFile | SynchronousIoNonAlert | OpenReparsePoint,
                null,
                out int status);
            if (handle != null)
            {
                return handle;
            }

            if (status is StatusObjectNameNotFound or StatusObjectPathNotFound or StatusNotADirectory)
            {
                return null;
            }

            throw Failure("A folder in the held path could not be opened.", status);
        }

        // The reparse tag of the folder, or null when it has none.
        internal static uint? ReparseTag(SafeFileHandle handle)
        {
            ArgumentNullException.ThrowIfNull(handle);
            if (!NativeMethods.GetFileInformationByHandleEx(
                    handle,
                    FileAttributeTagInfoClass,
                    out FileAttributeTagInformation information,
                    (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
            {
                throw new IOException("The folder could not be examined.", new Win32Exception(Marshal.GetLastWin32Error()));
            }

            return (information.FileAttributes & FileAttributeReparsePoint) != 0 ? information.ReparseTag : null;
        }

        // Whether parent has an entry by that name, of any kind, a dangling link included. Follows nothing.
        internal static bool Exists(SafeFileHandle parent, string name)
        {
            ArgumentNullException.ThrowIfNull(parent);
            ArgumentException.ThrowIfNullOrEmpty(name);
            SafeFileHandle handle = Open(
                parent,
                name,
                ReadAttributes | Synchronize,
                ShareRead | ShareWrite | ShareDelete,
                FileOpen,
                SynchronousIoNonAlert | OpenReparsePoint,
                null,
                out int status);
            if (handle != null)
            {
                handle.Dispose();
                return true;
            }

            return status switch
            {
                StatusObjectNameNotFound => false,

                // There, but not openable now.
                StatusAccessDenied or StatusSharingViolation or StatusDeletePending => true,
                _ => throw Failure("The held parent could not be examined.", status),
            };
        }

        // Creates the folder name in parent with security, and returns the handle to it. Never opens a folder or link
        // that is already there, nor creates the folder that a dangling link points to: then it fails, and nothing is
        // changed.
        internal static SafeFileHandle CreateNew(SafeFileHandle parent, string name, DirectorySecurity security)
        {
            ArgumentNullException.ThrowIfNull(parent);
            ArgumentException.ThrowIfNullOrEmpty(name);
            ArgumentNullException.ThrowIfNull(security);
            SafeFileHandle handle = Open(
                parent,
                name,
                Delete | ReadControl | Synchronize | ListDirectory | AddFile | AddSubdirectory | Traverse | ReadAttributes,
                ShareRead | ShareWrite,
                FileCreate,
                DirectoryFile | SynchronousIoNonAlert | OpenReparsePoint,
                security.GetSecurityDescriptorBinaryForm(),
                out int status);
            return handle ?? throw Failure("The folder could not be created.", status);
        }

        // Creates a file name in parent with security and returns the handle to it. Never opens a file or link that is
        // already there, nor creates the file that a dangling link points to: null then, and nothing is changed.
        internal static SafeFileHandle CreateNewFile(SafeFileHandle parent, string name, FileSecurity security)
        {
            ArgumentNullException.ThrowIfNull(parent);
            ArgumentException.ThrowIfNullOrEmpty(name);
            ArgumentNullException.ThrowIfNull(security);
            SafeFileHandle handle = Open(
                parent,
                name,
                Delete | ReadControl | Synchronize | WriteData | ReadAttributes,
                ShareRead,
                FileCreate,
                NonDirectoryFile | SynchronousIoNonAlert | OpenReparsePoint,
                security.GetSecurityDescriptorBinaryForm(),
                out int status);
            if (handle != null)
            {
                return handle;
            }

            return status == StatusObjectNameCollision || Exists(parent, name)
                ? null
                : throw Failure("The file could not be created.", status);
        }

        // The folder's path with its volume's name, \\?\Volume{...}\..., which Windows uses as it is: no drive letter or
        // mounted folder is looked up, and no folder name loses a trailing dot.
        internal static string FinalPath(SafeFileHandle handle) => FinalPath(handle, VolumeNameGuid);

        // The folder's path on its device, such as \Device\HarddiskVolume3\... or, on a network share, \Device\Mup\....
        internal static string DevicePath(SafeFileHandle handle) => FinalPath(handle, VolumeNameNt);

        // The file system of the folder's volume, such as NTFS.
        internal static string FileSystemName(SafeFileHandle handle)
        {
            ArgumentNullException.ThrowIfNull(handle);
            var name = new StringBuilder(64);
            if (!NativeMethods.GetVolumeInformationByHandleW(
                    handle, null, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, name, (uint)name.Capacity))
            {
                throw new IOException(
                    "The folder's volume could not be examined.", new Win32Exception(Marshal.GetLastWin32Error()));
            }

            return name.ToString();
        }

        // The folder's owner and access control, read through the handle.
        internal static DirectorySecurity Security(SafeFileHandle handle)
        {
            byte[] descriptor = SecurityDescriptor(handle, "The folder's access control could not be read.");
            var security = new DirectorySecurity();
            security.SetSecurityDescriptorBinaryForm(descriptor, AccessControlSections.Owner | AccessControlSections.Access);
            return security;
        }

        // The file's owner and access control, read through the handle.
        internal static FileSecurity SecurityForFile(SafeFileHandle handle)
        {
            byte[] descriptor = SecurityDescriptor(handle, "The file's access control could not be read.");
            var security = new FileSecurity();
            security.SetSecurityDescriptorBinaryForm(descriptor, AccessControlSections.Owner | AccessControlSections.Access);
            return security;
        }

        // Renames the folder that the handle holds to name in parent, which must not exist, without looking up any path.
        internal static void Rename(SafeFileHandle handle, SafeFileHandle parent, string name)
        {
            int status = RenameCore(handle, parent, name);
            if (status < 0)
            {
                throw Failure("The folder could not be renamed.", status);
            }
        }

        // Renames the file that the handle holds to name in parent without looking up any path. False when an entry by that
        // name already exists, a dangling link included; it is never replaced.
        internal static bool TryRenameFile(SafeFileHandle handle, SafeFileHandle parent, string name)
        {
            int status = RenameCore(handle, parent, name);
            if (status >= 0)
            {
                return true;
            }

            return status == StatusObjectNameCollision || Exists(parent, name)
                ? false
                : throw Failure("The file could not be renamed.", status);
        }

        private static int RenameCore(SafeFileHandle handle, SafeFileHandle parent, string name)
        {
            ArgumentNullException.ThrowIfNull(handle);
            ArgumentNullException.ThrowIfNull(parent);
            ArgumentException.ThrowIfNullOrEmpty(name);
            byte[] fileName = Encoding.Unicode.GetBytes(name);
            int offset = (int)Marshal.OffsetOf<FileRenameInformation>(nameof(FileRenameInformation.FileName));
            int size = offset + fileName.Length + sizeof(char);
            IntPtr buffer = Marshal.AllocHGlobal(size);
            bool added = false;
            try
            {
                for (int index = 0; index < size; index++)
                {
                    Marshal.WriteByte(buffer, index, 0);
                }

                // ReplaceIfExists stays zero: never replace.
                Marshal.WriteByte(
                    buffer,
                    (int)Marshal.OffsetOf<FileRenameInformation>(nameof(FileRenameInformation.ReplaceIfExists)),
                    0);
                parent.DangerousAddRef(ref added);
                Marshal.WriteIntPtr(
                    buffer,
                    (int)Marshal.OffsetOf<FileRenameInformation>(nameof(FileRenameInformation.RootDirectory)),
                    parent.DangerousGetHandle());
                Marshal.WriteInt32(
                    buffer,
                    (int)Marshal.OffsetOf<FileRenameInformation>(nameof(FileRenameInformation.FileNameLength)),
                    fileName.Length);
                Marshal.Copy(fileName, 0, buffer + offset, fileName.Length);
                return NativeMethods.NtSetInformationFile(
                    handle, out IoStatusBlock _, buffer, (uint)size, FileRenameInformationClass);
            }
            finally
            {
                if (added)
                {
                    parent.DangerousRelease();
                }

                Marshal.FreeHGlobal(buffer);
            }
        }

        // Marks the empty folder that the handle holds for deletion: it goes when the handle is closed.
        internal static void MarkForDeletion(SafeFileHandle handle)
        {
            MarkForDeletion(handle, "The folder could not be removed.");
        }

        // Marks the file that the handle holds for deletion: it goes when the handle is closed.
        internal static void MarkFileForDeletion(SafeFileHandle handle)
        {
            MarkForDeletion(handle, "The file could not be removed.");
        }

        private static void MarkForDeletion(SafeFileHandle handle, string message)
        {
            ArgumentNullException.ThrowIfNull(handle);
            IntPtr buffer = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(buffer, 1);
                if (!NativeMethods.SetFileInformationByHandle(handle, FileDispositionInfoClass, buffer, sizeof(int)))
                {
                    throw new IOException(message, new Win32Exception(Marshal.GetLastWin32Error()));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        // Flushes all buffered file data and metadata through the handle.
        internal static void FlushFileBuffers(SafeFileHandle handle)
        {
            ArgumentNullException.ThrowIfNull(handle);
            if (!NativeMethods.FlushFileBuffers(handle))
            {
                throw new IOException("The file could not be flushed.", new Win32Exception(Marshal.GetLastWin32Error()));
            }
        }

        private static byte[] SecurityDescriptor(SafeFileHandle handle, string message)
        {
            ArgumentNullException.ThrowIfNull(handle);
            uint error = NativeMethods.GetSecurityInfo(
                handle,
                FileObject,
                OwnerSecurityInformation | DaclSecurityInformation,
                out _,
                out _,
                out _,
                out _,
                out IntPtr descriptor);
            if (error != 0)
            {
                throw new IOException(message, new Win32Exception((int)error));
            }

            try
            {
                byte[] bytes = new byte[NativeMethods.GetSecurityDescriptorLength(descriptor)];
                Marshal.Copy(descriptor, bytes, 0, bytes.Length);
                return bytes;
            }
            finally
            {
                NativeMethods.LocalFree(descriptor);
            }
        }

        // NtCreateFile for name: a single name in root, or a full NT path when root is null. Null on failure, with status.
        private static SafeFileHandle Open(
            SafeFileHandle root,
            string name,
            uint access,
            uint share,
            uint disposition,
            uint options,
            byte[] descriptor,
            out int status)
        {
            IntPtr nameBuffer = Marshal.StringToHGlobalUni(name);
            IntPtr descriptorBuffer = descriptor == null ? IntPtr.Zero : Marshal.AllocHGlobal(descriptor.Length);
            IntPtr unicodeString = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            bool added = false;
            try
            {
                if (descriptor != null)
                {
                    Marshal.Copy(descriptor, 0, descriptorBuffer, descriptor.Length);
                }

                Marshal.StructureToPtr(
                    new UnicodeString
                    {
                        Length = checked((ushort)(name.Length * sizeof(char))),
                        MaximumLength = checked((ushort)((name.Length + 1) * sizeof(char))),
                        Buffer = nameBuffer,
                    },
                    unicodeString,
                    false);
                if (root != null)
                {
                    root.DangerousAddRef(ref added);
                }

                var attributes = new ObjectAttributes
                {
                    Length = Marshal.SizeOf<ObjectAttributes>(),
                    RootDirectory = root == null ? IntPtr.Zero : root.DangerousGetHandle(),
                    ObjectName = unicodeString,
                    Attributes = ObjectCaseInsensitive,
                    SecurityDescriptor = descriptorBuffer,
                };
                status = NativeMethods.NtCreateFile(
                    out SafeFileHandle handle,
                    access,
                    ref attributes,
                    out IoStatusBlock _,
                    IntPtr.Zero,
                    0,
                    share,
                    disposition,
                    options,
                    IntPtr.Zero,
                    0);
                if (status < 0)
                {
                    handle.Dispose();
                    return null;
                }

                return handle;
            }
            finally
            {
                if (added)
                {
                    root.DangerousRelease();
                }

                Marshal.FreeHGlobal(unicodeString);
                if (descriptorBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(descriptorBuffer);
                }

                Marshal.FreeHGlobal(nameBuffer);
            }
        }

        private static IOException Failure(string message, int status) =>
            new(message, new Win32Exception(NativeMethods.RtlNtStatusToDosError(status)));

        private static string FinalPath(SafeFileHandle handle, uint flags)
        {
            ArgumentNullException.ThrowIfNull(handle);
            var buffer = new StringBuilder(512);
            while (true)
            {
                uint length = NativeMethods.GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, flags);
                if (length == 0)
                {
                    throw new IOException(
                        "The folder's path could not be read.", new Win32Exception(Marshal.GetLastWin32Error()));
                }

                if (length < buffer.Capacity)
                {
                    return buffer.ToString();
                }

                buffer.Capacity = (int)length + 1;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct UnicodeString
        {
            internal ushort Length;
            internal ushort MaximumLength;
            internal IntPtr Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ObjectAttributes
        {
            internal int Length;
            internal IntPtr RootDirectory;
            internal IntPtr ObjectName;
            internal uint Attributes;
            internal IntPtr SecurityDescriptor;
            internal IntPtr SecurityQualityOfService;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoStatusBlock
        {
            internal IntPtr Status;
            internal IntPtr Information;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileAttributeTagInformation
        {
            internal uint FileAttributes;
            internal uint ReparseTag;
        }

        // FILE_RENAME_INFORMATION, for its field offsets; the name follows FileNameLength.
        [StructLayout(LayoutKind.Sequential)]
        private struct FileRenameInformation
        {
            internal uint ReplaceIfExists;
            internal IntPtr RootDirectory;
            internal uint FileNameLength;
            internal char FileName;
        }

        private static class NativeMethods
        {
            [DllImport("ntdll.dll", ExactSpelling = true)]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            internal static extern int NtCreateFile(
                out SafeFileHandle fileHandle,
                uint desiredAccess,
                ref ObjectAttributes objectAttributes,
                out IoStatusBlock ioStatusBlock,
                IntPtr allocationSize,
                uint fileAttributes,
                uint shareAccess,
                uint createDisposition,
                uint createOptions,
                IntPtr eaBuffer,
                uint eaLength);

            [DllImport("ntdll.dll", ExactSpelling = true)]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            internal static extern int NtSetInformationFile(
                SafeFileHandle fileHandle,
                out IoStatusBlock ioStatusBlock,
                IntPtr fileInformation,
                uint length,
                int fileInformationClass);

            [DllImport("ntdll.dll", ExactSpelling = true)]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            internal static extern int RtlNtStatusToDosError(int status);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            internal static extern uint QueryDosDeviceW(string deviceName, [Out] char[] targetPath, uint max);

            [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GetFileInformationByHandleEx(
                SafeFileHandle file,
                int fileInformationClass,
                out FileAttributeTagInformation information,
                uint bufferSize);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            internal static extern uint GetFinalPathNameByHandleW(
                SafeFileHandle file, StringBuilder filePath, uint filePathLength, uint flags);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GetVolumeInformationByHandleW(
                SafeFileHandle file,
                StringBuilder volumeName,
                uint volumeNameSize,
                IntPtr volumeSerialNumber,
                IntPtr maximumComponentLength,
                IntPtr fileSystemFlags,
                StringBuilder fileSystemName,
                uint fileSystemNameSize);

            [DllImport("advapi32.dll", ExactSpelling = true)]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            internal static extern uint GetSecurityInfo(
                SafeFileHandle handle,
                int objectType,
                uint securityInfo,
                out IntPtr owner,
                out IntPtr group,
                out IntPtr dacl,
                out IntPtr sacl,
                out IntPtr securityDescriptor);

            [DllImport("advapi32.dll", ExactSpelling = true)]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            internal static extern uint GetSecurityDescriptorLength(IntPtr securityDescriptor);

            [DllImport("kernel32.dll", ExactSpelling = true)]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            internal static extern IntPtr LocalFree(IntPtr memory);

            [DllImport("kernel32.dll", SetLastError = true)]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetFileInformationByHandle(
                SafeFileHandle file, int informationClass, IntPtr information, uint informationLength);

            [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool FlushFileBuffers(SafeFileHandle file);
        }
    }
}
