// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
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
        public async Task SuppliedAuthorityIssuesUsableTlsCertificateWithinItsValidity()
        {
            using RSA rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=XMAT synthetic CA", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            using X509Certificate2 root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            using var manager = new CertificateManager(root);
            X509Certificate2 leaf = manager.GetCertificateForHost("localhost");
            Assert.True(leaf.HasPrivateKey);
            Assert.InRange(leaf.NotBefore, root.NotBefore, root.NotAfter);
            Assert.InRange(leaf.NotAfter, root.NotBefore, root.NotAfter);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Task server = ServeAsync();
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, cancellation.Token);
            using var tls = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
            await Task.WhenAll(server, tls.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = "localhost" }, cancellation.Token));
            string keyName;
            CngProvider provider;
            using (RSA privateKey = leaf.GetRSAPrivateKey())
            {
                var cng = Assert.IsType<RSACng>(privateKey);
                keyName = cng.Key.KeyName;
                provider = cng.Key.Provider;
            }
            Assert.True(CngKey.Exists(keyName, provider));
            manager.Dispose();
            Assert.False(CngKey.Exists(keyName, provider));

            async Task ServeAsync()
            {
                using TcpClient accepted = await listener.AcceptTcpClientAsync(cancellation.Token);
                using var serverTls = new SslStream(accepted.GetStream());
                await serverTls.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions { ServerCertificate = leaf }, cancellation.Token);
            }
        }

        [Fact]
        public async Task ConcurrentRequestsForOneHostShareOneCertificate()
        {
            using RSA rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=XMAT synthetic CA", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            using X509Certificate2 root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            using var manager = new CertificateManager(root);
            using var start = new Barrier(8);
            X509Certificate2[] issued = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Factory.StartNew(() =>
            {
                start.SignalAndWait();
                return manager.GetCertificateForHost("concurrent.example");
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)));
            Assert.All(issued, certificate => Assert.Same(issued[0], certificate));
            Assert.NotSame(issued[0], manager.GetCertificateForHost("other.example"));
        }

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

        [Theory]
        [InlineData("::1", "::1")]
        [InlineData("[2001:db8::1]", "2001:db8::1")]
        [InlineData("10.0.0.1", "10.0.0.1")]
        public void HostCertificates_ForIpLiterals_NameTheAddress(string host, string address)
        {
            using var manager = new CertificateManager(storeCerts: false);
            using var rootCert = CreateTestRootCertificate(DateTimeOffset.UtcNow.AddYears(4));
            SetRootCertificate(manager, rootCert);

            X509Certificate2 hostCert = manager.GetCertificateForHost(host);

            var names = Assert.Single(hostCert.Extensions.OfType<X509SubjectAlternativeNameExtension>());
            Assert.Contains(IPAddress.Parse(address), names.EnumerateIPAddresses());
            Assert.Equal(address, hostCert.GetNameInfo(X509NameType.SimpleName, false));
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
