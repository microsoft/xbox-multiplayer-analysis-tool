// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Net;

namespace XMAT
{
    // Whether text names a console that New Capture can add: an IPv4 address, or a hostname.
    internal static class ConsoleAddress
    {
        internal static bool IsValid(string text) => text != null && (IsIPv4Address(text) || IsHostname(text));

        // IPAddress accepts int64 IP addresses in string format as network order byte representations of IP's... so we
        // have to validate the string first (e.g. "123" is a valid IP according to C#)
        private static bool IsIPv4Address(string text) => text.Split('.').Length == 4 && IPAddress.TryParse(text, out _);

        // Basic validation for hostname format
        private static bool IsHostname(string text) =>
            !string.IsNullOrWhiteSpace(text) && text.Length <= 255 && !text.Contains(' ');
    }
}
