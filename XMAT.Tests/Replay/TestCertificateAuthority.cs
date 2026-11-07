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
        private readonly List<CspParameters> _capiContainers = new();

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
            handler.SslOptions.CertificateChainPolicy = Policy();
        }

        // A new chain policy that trusts only this root, without revocation checks: what a test gives a handler, and a
        // client-certificate check, as the trust they share. Every call makes a new instance.
        internal X509ChainPolicy Policy()
        {
            var policy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.NoCheck,
            };
            policy.CustomTrustStore.Add(Root);
            return policy;
        }

        // A server certificate with exactly these names: a common name, DNS names and IP addresses in its subject
        // alternative names (none at all when both are empty), for the given extended key usage, signed by the root or by
        // issuer, an intermediate certificate authority with its private key.
        internal X509Certificate2 IssueServer(
            string commonName,
            IReadOnlyList<string> dnsNames,
            IReadOnlyList<IPAddress> addresses = null,
            string extendedKeyUsage = ServerAuthenticationOid,
            X509Certificate2 issuer = null)
        {
            using RSA key = RSA.Create(2048);
            var request = new CertificateRequest("CN=" + commonName, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            if (dnsNames.Count + (addresses?.Count ?? 0) > 0)
            {
                var names = new SubjectAlternativeNameBuilder();
                foreach (string dnsName in dnsNames)
                {
                    names.AddDnsName(dnsName);
                }

                foreach (IPAddress address in addresses ?? Array.Empty<IPAddress>())
                {
                    names.AddIpAddress(address);
                }

                request.CertificateExtensions.Add(names.Build());
            }

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid(extendedKeyUsage) }, false));
            using X509Certificate2 issued = request.Create(
                issuer ?? Root, DateTimeOffset.UtcNow.AddMinutes(-30), DateTimeOffset.UtcNow.AddMinutes(30), RandomNumberGenerator.GetBytes(8));
            using X509Certificate2 withKey = issued.CopyWithPrivateKey(key);
            return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
        }

        // An intermediate certificate authority under the root, with its private key in memory. The caller disposes it.
        internal X509Certificate2 CreateIntermediate()
        {
            using RSA key = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=XMAT test intermediate " + Guid.NewGuid().ToString("N"), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            using X509Certificate2 issued = request.Create(
                Root, DateTimeOffset.UtcNow.AddMinutes(-50), DateTimeOffset.UtcNow.AddMinutes(50), RandomNumberGenerator.GetBytes(8));
            return issued.CopyWithPrivateKey(key);
        }

        // A client certificate signed by the root, which is never added to a certificate store. Its private key lives in the
        // current user's key store until the certificate, or for a CryptoAPI key this authority, is disposed, as a key of a
        // certificate in the Personal store lives there: an RSA or ECDSA key is imported from a PFX into the CNG software
        // provider, and a CryptoAPI RSA key is made in a named container of the Microsoft Enhanced RSA and AES provider.
        // Extensions are those of a client certificate unless the arguments say otherwise; a null usage leaves the
        // extension out. The validity can lie outside the root's.
        internal X509Certificate2 IssueClient(
            string subject = "XMAT test client",
            ClientKey key = ClientKey.Rsa,
            string extendedKeyUsage = ClientAuthenticationOid,
            X509KeyUsageFlags? keyUsage = X509KeyUsageFlags.DigitalSignature,
            bool certificateAuthority = false,
            DateTimeOffset? notBefore = null,
            DateTimeOffset? notAfter = null)
        {
            using AsymmetricAlgorithm algorithm = key switch
            {
                ClientKey.Ecdsa => ECDsa.Create(ECCurve.NamedCurves.nistP256),
                ClientKey.CapiRsa => CapiKey(),
                _ => RSA.Create(2048),
            };
            CertificateRequest request = algorithm is ECDsa ecdsa
                ? new CertificateRequest("CN=" + subject, ecdsa, HashAlgorithmName.SHA256)
                : new CertificateRequest("CN=" + subject, (RSA)algorithm, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority, false, 0, false));
            if (keyUsage is X509KeyUsageFlags usage)
            {
                request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, false));
            }

            if (extendedKeyUsage != null)
            {
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid(extendedKeyUsage) }, false));
            }

            // Signed with the root's RSA key whatever the certificate's own key is.
            using X509Certificate2 issued = request.Create(
                Root.SubjectName,
                X509SignatureGenerator.CreateForRSA(_key, RSASignaturePadding.Pkcs1),
                notBefore ?? DateTimeOffset.UtcNow.AddMinutes(-30),
                notAfter ?? DateTimeOffset.UtcNow.AddMinutes(30),
                RandomNumberGenerator.GetBytes(8));
            if (algorithm is RSACryptoServiceProvider capi)
            {
                return issued.CopyWithPrivateKey(capi);
            }

            using X509Certificate2 withKey = algorithm is ECDsa signer
                ? issued.CopyWithPrivateKey(signer)
                : issued.CopyWithPrivateKey((RSA)algorithm);
            return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
        }

        internal const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";
        internal const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";

        internal enum ClientKey
        {
            Rsa,
            Ecdsa,
            CapiRsa,
        }

        public void Dispose()
        {
            Root.Dispose();
            _key.Dispose();
            foreach (CspParameters container in _capiContainers)
            {
                // Deletes the container (PersistKeyInCsp false), so no key outlives the test.
                using var key = new RSACryptoServiceProvider(container) { PersistKeyInCsp = false };
                key.Clear();
            }
        }

        // A persisted RSA key in a CryptoAPI container of its own (PROV_RSA_AES), which Dispose deletes.
        private RSACryptoServiceProvider CapiKey()
        {
            var container = new CspParameters(24, "Microsoft Enhanced RSA and AES Cryptographic Provider", "XMAT-test-" + Guid.NewGuid().ToString("N"))
            {
                Flags = CspProviderFlags.NoPrompt,
                KeyNumber = (int)KeyNumber.Exchange,
            };
            _capiContainers.Add(container);
            return new RSACryptoServiceProvider(2048, container) { PersistKeyInCsp = true };
        }
    }
}
