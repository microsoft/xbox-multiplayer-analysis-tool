// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace XMAT.Operations
{
    internal static class PrivateFolder
    {
        internal static void Create(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            if (Path.Exists(path))
            {
                throw new IOException("The private folder already exists.");
            }
            new DirectoryInfo(path).Create(CurrentUserOnly());
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
    }
}
