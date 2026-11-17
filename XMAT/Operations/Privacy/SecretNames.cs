// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;

namespace XMAT.Operations.Privacy
{
    // Names that suggest a secret, of headers, query parameters, JSON members and XML elements, shared by the sanitized
    // export (SazSanitizer) and request files. They recognize likely secrets; they never prove that anything else is free
    // of them.
    internal static class SecretNames
    {
        private static readonly string[] Names =
        {
            "authorization", "authenticate", "authentication", "cookie", "password", "passwd", "passphrase", "secret", "token",
            "credential", "apikey", "subscriptionkey", "accountkey", "accesskey", "privatekey",
            "sharedkey", "functionkey", "functionskey", "encryptionkey", "connectionstring", "signature", "sessionid", "userid", "username",
            "email", "xuid", "gamertag", "deviceid", "codeverifier", "codechallenge", "authcode",
            "passcode", "securitycode", "verificationcode", "ticket", "devicecode", "usercode", "gamedisplayname", "appdisplayname"
        };

        private static readonly HashSet<string> Headers = new(StringComparer.OrdinalIgnoreCase)
        {
            "Forwarded", "X-Forwarded-For", "X-Real-IP", "X-Client-IP", "X-MS-Client-Principal"
        };

        // Lower-case letters and digits only, as IsSensitiveName compares them.
        internal static IReadOnlyList<string> SensitiveNames { get; } = Array.AsReadOnly(Names);

        // A name, compared by its letters and digits in lower case: one of a few short names, one that ends with "auth", or
        // one that contains a sensitive name.
        internal static bool IsSensitiveName(string name)
        {
            var normalized = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            return normalized is "key" or "keys" or "auth" or "sig" or "sas" or "pwd" or "user" or
                "otp" or "pin" or "nonce" or "assertion" or "samlresponse" or "samlrequest" or "gtg" or "xid" ||
                normalized.EndsWith("auth", StringComparison.Ordinal) ||
                Names.Any(normalized.Contains);
        }

        // A header whose value is likely a secret or identifies a person or client.
        internal static bool IsSensitiveHeaderName(string name) => IsSensitiveName(name) || Headers.Contains(name);

        internal static bool IsSensitiveParameterName(string name) => IsSensitiveName(name);
    }
}
