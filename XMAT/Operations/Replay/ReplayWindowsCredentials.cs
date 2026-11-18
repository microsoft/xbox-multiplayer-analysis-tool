// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Net;

namespace XMAT.Operations.Replay
{
    // The credentials of a run that may authenticate with Windows: the default network credentials, those of the account
    // that XMAT runs with, for the one origin that may receive them and only for its Negotiate and NTLM challenges; none
    // for any other origin or scheme, Basic, Digest and Kerberos labels included, so those challenges are the result. The
    // origin is compared as its consent was (ReplayCredentialOrigin.Of: scheme, IDN host and effective port), whatever
    // spelling a URL of that origin uses; CredentialCache matches the prefix on Uri.Host as written, so a cache made for
    // the canonical origin would never answer a URL written with its Unicode host. Total: a null or relative URI, a URL
    // without such an origin and any failure give none.
    internal sealed class ReplayWindowsCredentials : ICredentials
    {
        private readonly ReplayCredentialOrigin _origin;

        internal ReplayWindowsCredentials(ReplayCredentialOrigin origin)
        {
            _origin = origin ?? throw new ArgumentNullException(nameof(origin));
        }

        public NetworkCredential GetCredential(Uri uri, string authType)
        {
            try
            {
                if (uri == null || !IsWindowsScheme(authType))
                {
                    return null;
                }

                ReplayCredentialOrigin origin = ReplayCredentialOrigin.Of(uri);
                return origin != null && origin.Equals(_origin) ? CredentialCache.DefaultNetworkCredentials : null;
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                return null;
            }
        }

        private static bool IsWindowsScheme(string authType) =>
            string.Equals(authType, "Negotiate", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(authType, "NTLM", StringComparison.OrdinalIgnoreCase);
    }
}
