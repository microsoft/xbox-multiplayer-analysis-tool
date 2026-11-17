// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace XMAT.AgentConnection
{
    internal readonly record struct AgentProcessIdentity(string UserSid, bool Elevated)
    {
        internal static AgentProcessIdentity Current()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new(identity.User.Value, IsElevated(identity.AccessToken));
        }

        internal static bool MatchesCurrentUser(uint processId)
            => Read(processId) == Current();

        internal static AgentProcessIdentity? Read(uint processId)
        {
            try
            {
                using var process = OpenProcess(0x1000, false, processId);
                if (process.IsInvalid || !OpenProcessToken(process, 8, out var token))
                    return null;
                using (token)
                using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                    return new(identity.User.Value, IsElevated(token));
            }
            catch (Exception exception) when (exception is Win32Exception or UnauthorizedAccessException or ArgumentException)
            { return null; }
        }

        internal static string ExecutablePath(uint processId)
        {
            using var process = OpenProcess(0x1000, false, processId);
            if (process.IsInvalid)
                return null;
            var length = 32768;
            var path = new StringBuilder(length);
            return QueryFullProcessImageName(process, 0, path, ref length) ? path.ToString() : null;
        }

        private static bool IsElevated(SafeAccessTokenHandle token)
        {
            if (!GetTokenInformation(token, 20, out var elevated, sizeof(int), out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return elevated != 0;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int length);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(
            SafeAccessTokenHandle token, int informationClass, out int information, int length, out int returnedLength);
    }
}
