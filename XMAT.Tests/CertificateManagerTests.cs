// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Tests
{
    public class CertificateManagerTests
    {
        private static readonly TimeSpan CertificateExpirationTolerance = TimeSpan.FromMinutes(1);

        [Fact]
        public void CreateRootCertificate_ReturnsTrue_WhenCachedRootCertCanIssueHostCertificates()
        {
            using var manager = new CertificateManager(storeCerts: false);
            using var rootCert = CreateTestRootCertificate(DateTimeOffset.UtcNow.AddYears(4));

            SetRootCertificate(manager, rootCert);

            Assert.True(manager.CreateRootCertificate());
        }

        [Fact]
        public void CreateHostCertificate_ClampsNotAfterToIssuerExpiration()
        {
            using var manager = new CertificateManager(storeCerts: false);
            using var rootCert = CreateTestRootCertificate(DateTimeOffset.UtcNow.AddMonths(6));

            SetRootCertificate(manager, rootCert);

            using var hostCert = manager.CreateHostCertificate("example.test");

            DateTime rootNotAfterUtc = rootCert.NotAfter.ToUniversalTime();
            DateTime hostNotAfterUtc = hostCert.NotAfter.ToUniversalTime();

            Assert.True(hostNotAfterUtc <= rootNotAfterUtc);
            Assert.True(rootNotAfterUtc - hostNotAfterUtc < CertificateExpirationTolerance);
        }

        private static X509Certificate2 CreateTestRootCertificate(DateTimeOffset notAfter)
        {
            using RSA rsa = RSA.Create(4096);

            CertificateRequest req = new CertificateRequest(
                "CN=Test Root Certificate, O=Microsoft",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

            using X509Certificate2 cert = req.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddYears(-1),
                notAfter);

            return X509CertificateLoader.LoadPkcs12(
                cert.Export(X509ContentType.Pfx, string.Empty),
                string.Empty,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
        }

        private static void SetRootCertificate(CertificateManager manager, X509Certificate2 rootCert)
        {
            typeof(CertificateManager)
                .GetField("_rootCert", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(manager, rootCert);
        }
    }
}
