// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.IO;

namespace XMAT
{
    internal static class WindowsPath
    {
        // Use only after the caller has resolved the Windows path. Adding the prefix changes how Win32 interprets it.
        internal static string ToVerbatim(string fullPath)
        {
            if (!Path.IsPathFullyQualified(fullPath))
            {
                throw new ArgumentException("Expected a fully qualified filename.", nameof(fullPath));
            }

            if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) ||
                fullPath.StartsWith(@"\\.\", StringComparison.Ordinal))
            {
                return fullPath;
            }

            return fullPath.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + fullPath[2..]
                : @"\\?\" + fullPath;
        }
    }
}
