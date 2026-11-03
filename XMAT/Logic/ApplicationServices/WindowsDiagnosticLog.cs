// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using XMAT.SharedInterfaces;

namespace XMAT.ApplicationServices
{
    internal static class WindowsDiagnosticLog
    {
        internal static FileStream Open(WindowsExportFolder directory, string fileName)
        {
            var path = directory.Destination(fileName);
            if (!Path.GetExtension(fileName).Equals(".log", StringComparison.OrdinalIgnoreCase))
                throw new AdministrationException(AdministrationFailure.InvalidRequest);
            // Logs stay writable by the logger, but cannot be replaced while their prefix is copied.
            var handle = CreateFile(path, 0x80000080, 3, IntPtr.Zero, 3, 0x40200000, IntPtr.Zero);
            try
            {
                if (handle.IsInvalid)
                    throw new AdministrationException(AdministrationFailure.Unavailable);
                if (!GetFileInformationByHandle(handle, out var information))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if ((information.Attributes & (0x10 | 0x400)) != 0 || information.Links != 1)
                    throw new AdministrationException(AdministrationFailure.InvalidRequest);
                return new FileStream(handle, FileAccess.Read, 81920, isAsync: true);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            internal uint Attributes;
            internal uint CreationLow;
            internal uint CreationHigh;
            internal uint AccessLow;
            internal uint AccessHigh;
            internal uint WriteLow;
            internal uint WriteHigh;
            internal uint Volume;
            internal uint SizeHigh;
            internal uint SizeLow;
            internal uint Links;
            internal uint IndexHigh;
            internal uint IndexLow;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle handle, out FileInformation information);
    }
}
