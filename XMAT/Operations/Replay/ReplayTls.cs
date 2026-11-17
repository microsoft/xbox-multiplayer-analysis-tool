// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Security.Cryptography.X509Certificates;

namespace XMAT.Operations.Replay
{
    // How every replay send validates a server's certificate: the handler of Parsed sends, the check before a client
    // certificate is offered, and Raw sends. The chain is built with this PC's trust, or a test's, without checking
    // revocation and without downloading certificates, so validation fetches no issuer certificate from an address that the
    // server's certificate names and no revocation data. A server must therefore send every intermediate certificate that
    // this PC does not already have; Windows' automatic root update may still ask Windows Update about an unknown root.
    internal static class ReplayTls
    {
        // A new policy for each handler, check or handshake, which nothing else shares: a copy of testPolicy's (a
        // test's trust anchors), never the object that the test may keep, or the platform's, with these rules set
        // after it, so no seam turns downloads or revocation back on.
        internal static X509ChainPolicy ChainPolicy(Func<X509ChainPolicy> testPolicy)
        {
            X509ChainPolicy policy = testPolicy?.Invoke()?.Clone() ?? new X509ChainPolicy();
            policy.RevocationMode = X509RevocationMode.NoCheck;
            policy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
            policy.DisableCertificateDownloads = true;
            return policy;
        }
    }
}
