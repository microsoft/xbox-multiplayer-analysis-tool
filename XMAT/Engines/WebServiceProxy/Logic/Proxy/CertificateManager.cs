// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;

namespace XMAT.WebServiceCapture.Proxy
{
    internal class CertificateManager : IDisposable, ICertificateManager
    {
        private readonly string AuthorityName = "Xbox Multiplayer Analysis Tool Root Cert Authority";
        private readonly string IssuerName = "Microsoft";

        private readonly string CertFileName = "XMATRoot.cer";
        private readonly string CertFileNameFiddler = "FiddlerRoot.cer";
        private readonly string CertFileNamePem = "XMATRoot.pem";
        private readonly string CertFileNameFiddlerPem = "FiddlerRoot.pem";
        private readonly string CertPassword = string.Empty;

        private readonly X509Store _rootStore;
        private readonly X509Store _myStore;

        private X509Certificate2 _rootCert;

        private readonly ConcurrentDictionary<string, X509Certificate2> _certCache = new();
        private readonly bool _storeCerts;
        private readonly object _rootLock = new();

        public CertificateManager(bool storeCerts)
        {
            _storeCerts = storeCerts;

            _rootStore = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            _rootStore.Open(OpenFlags.ReadWrite);

            if (_storeCerts)
            {
                _myStore = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                _myStore.Open(OpenFlags.ReadWrite);
            }
        }

        public void Dispose()
        {
            _rootStore?.Close();
            _myStore?.Close();
        }

        public bool Initialize()
        {
            bool success = CreateRootCertificate();

            if (success)
            {
                RebuildCertificateCache();
            }

            return success;
        }

        private void RebuildCertificateCache()
        {
            _certCache.Clear();

            X509Certificate2Collection certs = _rootStore.Certificates.Find(
                X509FindType.FindBySubjectDistinguishedName,
                $"CN={AuthorityName}, O={IssuerName}", false);
            if (certs.Count > 0)
                _rootCert = certs[0];

            if (_myStore != null)
            {
                certs = _myStore.Certificates.Find(X509FindType.FindByIssuerName, IssuerName, false);
                foreach (var cert in certs)
                    _certCache.TryAdd(cert.GetNameInfo(X509NameType.SimpleName, false), cert);
            }
        }

        public X509Certificate2 GetCertificateForHost(string hostname)
        {
            hostname = GetHostname(hostname);

            return _certCache.GetOrAdd(hostname, host =>
            {
                X509Certificate2 cert = CreateHostCertificate(host);
                _myStore?.Add(cert);
                return cert;
            });
        }

        public bool CreateRootCertificate()
        {
            lock (_rootLock)
            {
                if (_rootCert != null)
                    return true;

                var certs = _rootStore.Certificates.Find(
                    X509FindType.FindBySubjectDistinguishedName,
                    $"CN={AuthorityName}, O={IssuerName}", false);
                if (certs.Count > 0)
                {
                    _rootCert = certs[0];
                    return true;
                }

                try
                {
                    MessageBox.Show(
                        Localization.GetLocalizedString("PROXY_ROOT_CERT_WARNING_MESSAGE"),
                        Localization.GetLocalizedString("PROXY_ROOT_CERT_WARNING_TITLE"),
                        MessageBoxButton.OK, MessageBoxImage.Information);

                    RSA rsa = RSA.Create(4096);

                    CertificateRequest req = new CertificateRequest(
                        $"CN={AuthorityName}, O={IssuerName}",
                        rsa,
                        HashAlgorithmName.SHA256,
                        RSASignaturePadding.Pkcs1);

                    req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                    req.CertificateExtensions.Add(new X509KeyUsageExtension(
                        X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
                    req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                        new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
                    req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

                    var cert = req.CreateSelfSigned(
                        DateTimeOffset.UtcNow.AddYears(-1),
                        DateTimeOffset.UtcNow.AddYears(4));
                    cert.FriendlyName = AuthorityName;

                    _rootCert = new X509Certificate2(
                        cert.Export(X509ContentType.Pfx, CertPassword),
                        CertPassword,
                        X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
                    _rootStore.Add(_rootCert);

                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public X509Certificate2 CreateHostCertificate(string hostname)
        {
            RSA rsa = RSA.Create(2048);

            CertificateRequest req = new CertificateRequest(
                $"CN={hostname}, O={IssuerName}",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            SubjectAlternativeNameBuilder sanBuilder = new SubjectAlternativeNameBuilder();
            sanBuilder.AddDnsName(hostname);
            req.CertificateExtensions.Add(sanBuilder.Build());
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment, true));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
            req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

            byte[] serial = RandomNumberGenerator.GetBytes(20);

            X509Certificate2 cert = req.Create(
                _rootCert,
                DateTimeOffset.UtcNow.AddYears(-1),
                DateTimeOffset.UtcNow.AddYears(3),
                serial);

            X509Certificate2 certWithKey = cert.CopyWithPrivateKey(rsa);
            certWithKey.FriendlyName = hostname;

            X509Certificate2 certWithExportableKey = new X509Certificate2(
                certWithKey.Export(X509ContentType.Pfx, CertPassword),
                CertPassword,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
            return certWithExportableKey;
        }

        private static string GetHostname(string path)
        {
            string host = path;
            if (path.Contains(':'))
                host = path.Split(':')[0];
            return host;
        }

        public void ExportRootCertificate(string path)
        {
            if (_rootCert == null)
                CreateRootCertificate();

            byte[] buff = _rootCert.Export(X509ContentType.Cert);
            File.WriteAllBytes(Path.Combine(path, CertFileName), buff);
            File.WriteAllBytes(Path.Combine(path, CertFileNameFiddler), buff);

            string pem = "-----BEGIN CERTIFICATE-----\n" + Convert.ToBase64String(buff) + "\n-----END CERTIFICATE-----\n";
            File.WriteAllText(Path.Combine(path, CertFileNamePem), pem);
            File.WriteAllText(Path.Combine(path, CertFileNameFiddlerPem), pem);
        }

        public void RemoveRootCertificate()
        {
            if (_rootStore == null)
                return;

            X509Certificate2Collection certs = _rootStore.Certificates.Find(
                X509FindType.FindBySubjectName, AuthorityName, false);
            _rootStore.RemoveRange(certs);
        }

        public void RemoveHostCertificates()
        {
            if (_myStore == null)
                return;

            X509Certificate2Collection certs = _myStore.Certificates.Find(
                X509FindType.FindByIssuerName, IssuerName, false);
            _myStore.RemoveRange(certs);
        }
    }
}
