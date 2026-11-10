// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace XMAT.Operations
{
    internal static class PrivateFolder
    {
        internal static void Create(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            string nativePath = NativePath(path);
            byte[] descriptor = CurrentUserOnly().GetSecurityDescriptorBinaryForm();
            IntPtr descriptorPointer = Marshal.AllocHGlobal(descriptor.Length);
            try
            {
                Marshal.Copy(descriptor, 0, descriptorPointer, descriptor.Length);
                var attributes = new SecurityAttributes
                {
                    Length = Marshal.SizeOf<SecurityAttributes>(),
                    SecurityDescriptor = descriptorPointer,
                };
                if (!CreateDirectory(nativePath, ref attributes))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw error is 80 or 183
                        ? new IOException("The private folder already exists.")
                        : new IOException("The private folder could not be created.", new Win32Exception(error));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(descriptorPointer);
            }
        }

        internal static bool IsPrivate(string path)
        {
            try
            {
                DirectorySecurity security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access);
                if (!security.AreAccessRulesProtected)
                {
                    return false;
                }

                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                AuthorizationRuleCollection rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
                if (identity.User == null || rules.Count == 0)
                {
                    return false;
                }

                InheritanceFlags inherited = InheritanceFlags.None;
                foreach (FileSystemAccessRule rule in rules)
                {
                    if (rule.IsInherited ||
                        rule.AccessControlType != AccessControlType.Allow ||
                        !identity.User.Equals(rule.IdentityReference))
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
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                return false;
            }
        }

        private static DirectorySecurity CurrentUserOnly()
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                identity.User,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            return security;
        }

        internal static string NativePath(string path)
        {
            path = Path.GetFullPath(path);
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
                path.StartsWith(@"\\.\", StringComparison.Ordinal))
            {
                return path;
            }
            return path.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + path[2..]
                : @"\\?\" + path;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            internal int Length;
            internal IntPtr SecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)]
            internal bool InheritHandle;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, ExactSpelling = true,
            SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateDirectory(string path, ref SecurityAttributes securityAttributes);
    }
}
