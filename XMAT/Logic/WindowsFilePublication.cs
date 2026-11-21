// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace XMAT
{
    internal static class WindowsFilePublication
    {
        internal static bool TryPublish(string temporary, string destination, bool replace, out int error)
        {
            using var handle = CreateFile(temporary, 0x10080, 3, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }
            return TryPublish(handle, destination, replace, out error);
        }

        internal static bool TryPublish(SafeFileHandle handle, string destination, bool replace, out int error)
        {
            // FILE_RENAME_INFO has an aligned handle at 8 and its UTF-16 name at 20 on x64.
            var name = Encoding.Unicode.GetBytes(destination + "\0");
            var buffer = Marshal.AllocHGlobal(20 + name.Length);
            try
            {
                Marshal.Copy(new byte[20 + name.Length], 0, buffer, 20 + name.Length);
                Marshal.WriteInt32(buffer, replace ? 1 : 0);
                Marshal.WriteInt32(buffer, 16, name.Length - 2);
                Marshal.Copy(name, 0, buffer + 20, name.Length);
                var result = SetFileInformationByHandle(handle, 3, buffer, 20 + name.Length);
                error = result ? 0 : Marshal.GetLastWin32Error();
                return result;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, IntPtr info, int length);
    }
}
