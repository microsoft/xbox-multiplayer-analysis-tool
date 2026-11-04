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

namespace XMAT.Cli
{
    // Creates a folder and acts on it only through the handle that creating it returned (CliOutputFolder). The handle
    // shares reading and writing but not deletion, so while it is open Windows refuses to rename or remove the folder or
    // any folder that contains it. Errors are IOException without paths in their messages.
    internal static class CliDirectoryHandle
    {
        private const uint Delete = 0x00010000;
        private const uint ReadControl = 0x00020000;
        private const uint Synchronize = 0x00100000;
        private const uint ListDirectory = 0x0001;
        private const uint AddFile = 0x0002;
        private const uint AddSubdirectory = 0x0004;
        private const uint Traverse = 0x0020;
        private const uint ReadAttributes = 0x0080;
        private const uint ShareRead = 0x00000001;
        private const uint ShareWrite = 0x00000002;
        private const uint FileCreate = 2;
        private const uint DirectoryFile = 0x00000001;
        private const uint SynchronousIoNonAlert = 0x00000020;
        private const uint OpenReparsePoint = 0x00200000;
        private const uint ObjectCaseInsensitive = 0x00000040;
        private const uint VolumeNameNt = 0x2;
        private const int FileRenameInfoClass = 3;
        private const int FileDispositionInfoClass = 4;
        private const int FileObject = 1;
        private const uint OwnerSecurityInformation = 0x1;
        private const uint DaclSecurityInformation = 0x4;

        // Creates a new folder at path with security, and returns the handle to it. Never opens a folder or link that is
        // already there, nor creates the folder that a dangling link points to: then it fails, and nothing is changed.
        // Links in the folders that contain path are followed.
        internal static SafeFileHandle CreateNew(string path, DirectorySecurity security)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            ArgumentNullException.ThrowIfNull(security);
            byte[] descriptor = security.GetSecurityDescriptorBinaryForm();
            string name = ToNtPath(Path.GetFullPath(path));
            IntPtr nameBuffer = Marshal.StringToHGlobalUni(name);
            IntPtr descriptorBuffer = Marshal.AllocHGlobal(descriptor.Length);
            IntPtr unicodeString = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            try
            {
                Marshal.Copy(descriptor, 0, descriptorBuffer, descriptor.Length);
                Marshal.StructureToPtr(
                    new UnicodeString
                    {
                        Length = checked((ushort)(name.Length * sizeof(char))),
                        MaximumLength = checked((ushort)((name.Length + 1) * sizeof(char))),
                        Buffer = nameBuffer,
                    },
                    unicodeString,
                    false);
                var attributes = new ObjectAttributes
                {
                    Length = Marshal.SizeOf<ObjectAttributes>(),
                    ObjectName = unicodeString,
                    Attributes = ObjectCaseInsensitive,
                    SecurityDescriptor = descriptorBuffer,
                };
                int status = NativeMethods.NtCreateFile(
                    out SafeFileHandle handle,
                    Delete | ReadControl | Synchronize | ListDirectory | AddFile | AddSubdirectory | Traverse | ReadAttributes,
                    ref attributes,
                    out IoStatusBlock _,
                    IntPtr.Zero,
                    0,
                    ShareRead | ShareWrite,
                    FileCreate,
                    DirectoryFile | SynchronousIoNonAlert | OpenReparsePoint,
                    IntPtr.Zero,
                    0);
                if (status < 0)
                {
                    handle.Dispose();
                    throw new IOException(
                        "The folder could not be created.",
                        new Win32Exception(NativeMethods.RtlNtStatusToDosError(status)));
                }

                return handle;
            }
            finally
            {
                Marshal.FreeHGlobal(unicodeString);
                Marshal.FreeHGlobal(descriptorBuffer);
                Marshal.FreeHGlobal(nameBuffer);
            }
        }

        // The folder's path with every junction and link resolved, as a plain drive or UNC path.
        internal static string FinalPath(SafeFileHandle handle) => WithoutExtendedPrefix(FinalPath(handle, 0));

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
                throw new IOException("The folder's access control could not be read.", new Win32Exception((int)error));
            }

            try
            {
                byte[] bytes = new byte[NativeMethods.GetSecurityDescriptorLength(descriptor)];
                Marshal.Copy(descriptor, bytes, 0, bytes.Length);
                var security = new DirectorySecurity();
                security.SetSecurityDescriptorBinaryForm(bytes, AccessControlSections.Owner | AccessControlSections.Access);
                return security;
            }
            finally
            {
                NativeMethods.LocalFree(descriptor);
            }
        }

        // Renames the folder that the handle holds to target, which must not exist, without looking up its current path.
        internal static void Rename(SafeFileHandle handle, string target)
        {
            ArgumentNullException.ThrowIfNull(handle);
            ArgumentException.ThrowIfNullOrEmpty(target);
            byte[] name = Encoding.Unicode.GetBytes(ToNtPath(target));
            int offset = (int)Marshal.OffsetOf<FileRenameInformation>(nameof(FileRenameInformation.FileName));
            int size = offset + name.Length + sizeof(char);
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                for (int index = 0; index < size; index++)
                {
                    Marshal.WriteByte(buffer, index, 0);
                }

                // ReplaceIfExists and RootDirectory stay zero: never replace, and the name is a full path.
                Marshal.WriteInt32(
                    buffer,
                    (int)Marshal.OffsetOf<FileRenameInformation>(nameof(FileRenameInformation.FileNameLength)),
                    name.Length);
                Marshal.Copy(name, 0, buffer + offset, name.Length);
                if (!NativeMethods.SetFileInformationByHandle(handle, FileRenameInfoClass, buffer, (uint)size))
                {
                    throw new IOException("The folder could not be renamed.", new Win32Exception(Marshal.GetLastWin32Error()));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        // Marks the empty folder that the handle holds for deletion: it goes when the handle is closed.
        internal static void MarkForDeletion(SafeFileHandle handle)
        {
            ArgumentNullException.ThrowIfNull(handle);
            IntPtr buffer = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(buffer, 1);
                if (!NativeMethods.SetFileInformationByHandle(handle, FileDispositionInfoClass, buffer, sizeof(int)))
                {
                    throw new IOException("The folder could not be removed.", new Win32Exception(Marshal.GetLastWin32Error()));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

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

        private static string WithoutExtendedPrefix(string path)
        {
            if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            {
                return @"\\" + path[8..];
            }

            return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        }

        // The NT name of a drive or UNC path, which NtCreateFile and renaming through a handle need.
        private static string ToNtPath(string path)
        {
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                return @"\??\" + path[4..];
            }

            return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\??\UNC\" + path[2..] : @"\??\" + path;
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

        // FILE_RENAME_INFO, for its field offsets; the name follows FileNameLength.
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
            internal static extern int RtlNtStatusToDosError(int status);

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
        }
    }
}
