// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32.SafeHandles;

namespace XMAT.DataAbstractionLayer
{
    internal readonly record struct SqLtFileIdentity(
        uint VolumeSerialNumber,
        ulong FileIndex)
    {
        internal static SqLtFileIdentity FromPath(
            string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                path);
            using var stream =
                new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite |
                    FileShare.Delete);
            return FromHandle(
                stream.SafeFileHandle);
        }

        internal static SqLtFileIdentity FromHandle(
            SafeFileHandle handle)
        {
            ArgumentNullException.ThrowIfNull(handle);
            if (!NativeMethods.GetFileInformationByHandle(
                    handle,
                    out ByHandleFileInformation information))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error());
            }
            ulong fileIndex =
                ((ulong)information.FileIndexHigh <<
                32) |
                information.FileIndexLow;
            return new SqLtFileIdentity(
                information.VolumeSerialNumber,
                fileIndex);
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ByHandleFileInformation
        {
            internal uint FileAttributes;
            internal FILETIME CreationTime;
            internal FILETIME LastAccessTime;
            internal FILETIME LastWriteTime;
            internal uint VolumeSerialNumber;
            internal uint FileSizeHigh;
            internal uint FileSizeLow;
            internal uint NumberOfLinks;
            internal uint FileIndexHigh;
            internal uint FileIndexLow;
        }

        private static class NativeMethods
        {
            [DllImport(
                "kernel32.dll",
                SetLastError = true)]
            [DefaultDllImportSearchPaths(
                DllImportSearchPath.System32)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool
                GetFileInformationByHandle(
                    SafeFileHandle file,
                    out ByHandleFileInformation information);
        }
    }
}
