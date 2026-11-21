// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using XMAT.AgentConnection;

namespace XMAT.AgentAccess
{
    internal static class AgentLocalPipe
    {
        private const uint RejectRemoteClients = 8;

        internal static NamedPipeServerStream Create(Guid instanceId, int maximumInstances)
        {
            if (instanceId == Guid.Empty || maximumInstances is < 1 or > 254)
                throw new ArgumentOutOfRangeException(nameof(instanceId));
            var user = AgentProcessIdentity.Current();
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                $"D:P(A;;GA;;;{user.UserSid})", 1, out var descriptor, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var attributes = new SecurityAttributes
                {
                    Length = Marshal.SizeOf<SecurityAttributes>(),
                    Descriptor = descriptor,
                    InheritHandle = false
                };
                var handle = CreateNamedPipe($@"\\.\pipe\XMAT.Agent.{instanceId:N}",
                    0x40000003, RejectRemoteClients, (uint)maximumInstances, 65536, 65536, 0, ref attributes);
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    throw new Win32Exception(error);
                }
                try
                { return new(PipeDirection.InOut, true, false, handle); }
                catch { handle.Dispose(); throw; }
            }
            finally { LocalFree(descriptor); }
        }

        internal static bool IsAllowedClient(NamedPipeServerStream pipe) =>
            GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId) &&
            AgentProcessIdentity.MatchesCurrentUser(processId);

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            internal int Length;
            internal IntPtr Descriptor;
            [MarshalAs(UnmanagedType.Bool)]
            internal bool InheritHandle;
        }

        [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW",
            CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
            string value, uint revision, out IntPtr descriptor, out uint size);

        [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafePipeHandle CreateNamedPipe(
            string name, uint access, uint mode, uint maximumInstances, uint outputSize, uint inputSize,
            uint timeout, ref SecurityAttributes attributes);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr value);
    }
}
