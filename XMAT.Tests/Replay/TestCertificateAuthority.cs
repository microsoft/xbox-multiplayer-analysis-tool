// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace XMAT.Tests.Replay
{
    // A certificate authority that exists only in memory, for HTTPS loopback tests. Its root is never added to a Windows
    // certificate store: a test trusts it on one handler of its own (Trust), so the platform's validation, which XMAT's
    // senders keep, still refuses every certificate it issues.
    internal sealed class TestCertificateAuthority : IDisposable
    {
        private readonly RSA _key = RSA.Create(2048);

        internal TestCertificateAuthority()
        {
            var request = new CertificateRequest(
                "CN=XMAT test root " + Guid.NewGuid().ToString("N"), _key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            Root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));
        }

        internal X509Certificate2 Root { get; }

        // A server certificate for a host name or an IP address, signed by the root, with a private key that SslStream can
        // use on Windows. The caller disposes it.
        internal X509Certificate2 Issue(string dnsName = null, IPAddress address = null)
        {
            if (dnsName == null && address == null)
            {
                throw new ArgumentException("A DNS name or an IP address is required.");
            }

            using RSA key = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=" + (dnsName ?? address.ToString()), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            if (dnsName != null)
            {
                names.AddDnsName(dnsName);
            }

            if (address != null)
            {
                names.AddIpAddress(address);
            }

            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
            using X509Certificate2 issued = request.Create(
                Root, DateTimeOffset.UtcNow.AddMinutes(-30), DateTimeOffset.UtcNow.AddMinutes(30), RandomNumberGenerator.GetBytes(8));
            using X509Certificate2 withKey = issued.CopyWithPrivateKey(key);
            return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
        }

        // Trusts the root on this handler only; the certificate's name is still checked against the URL's host.
        internal void Trust(SocketsHttpHandler handler)
        {
            var policy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.NoCheck,
            };
            policy.CustomTrustStore.Add(Root);
            handler.SslOptions.CertificateChainPolicy = policy;
        }

        public void Dispose()
        {
            Root.Dispose();
            _key.Dispose();
        }
    }
}
