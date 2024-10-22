// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;

namespace XMAT.WebServiceCapture.Proxy
{
    internal partial class CertificateManager : IDisposable, ICertificateManager
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

        private readonly Dictionary<string, X509Certificate2> _certCache = new();
        private readonly bool _storeCerts;
        private readonly object _rootLock = new();
        private readonly object _hostLock = new();
        
        public CertificateManager(bool storeCerts)
        {
            _storeCerts = storeCerts;

            // cache objects to talk to the root and personal store
            _rootStore = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            _rootStore.Open(OpenFlags.ReadWrite);

            // only open the personal store if we plan on writing to it
            if(_storeCerts)
            {
                _myStore = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                _myStore.Open(OpenFlags.ReadWrite);
            }
        }

        public void Dispose()
        {
            if(_rootStore != null)
                _rootStore.Close();

            if(_myStore != null)
                _myStore.Close();
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

            // find our root cert and cache it
            X509Certificate2Collection certs = _rootStore.Certificates.Find(X509FindType.FindBySubjectDistinguishedName, $"CN={AuthorityName}, O={IssuerName}", false);
            if(certs.Count > 0)
                _rootCert = certs[0];

            if(_myStore != null)
            {
                // find all host certs we "own" and cache them
                certs = _myStore.Certificates.Find(X509FindType.FindByIssuerName, IssuerName, false);
                foreach (var cert in certs)
                    _certCache.Add(cert.GetNameInfo(X509NameType.SimpleName, false), cert);
            }
        }

        public X509Certificate2 GetCertificateForHost(string hostname)
        {
            // strip the hostname down to the bare host/domain (no ports, etc.)
            hostname = GetHostname(hostname);

            lock(_hostLock)
            {
                // find the cert in the cache, or create a new one
                if(_certCache.ContainsKey(hostname))
                {
                    return _certCache[hostname];
                }
                else
                {
                    // create, add to store, and add to our in-memory cache
                    X509Certificate2 cert = CreateHostCertificate(hostname);
                    _certCache.Add(hostname, cert);
                    if(_myStore != null)
                    {
                        _myStore.Add(cert);
                    }
                    return cert;
                }
            }
        }

        public bool CreateRootCertificate()
        {
            lock(_rootLock)
            {
                // don't create one if we already have one
                if(_rootCert != null)
                    return true;

                // return the existing root if there is one
                var certs = _rootStore.Certificates.Find(X509FindType.FindBySubjectDistinguishedName, $"CN={AuthorityName}, O={IssuerName}", false);
                if(certs.Count > 0)
                {
                    _rootCert = certs[0];
                    return true;
                }

                try
                {
                    MessageBox.Show(Localization.GetLocalizedString("PROXY_ROOT_CERT_WARNING_MESSAGE"), Localization.GetLocalizedString("PROXY_ROOT_CERT_WARNING_TITLE"), MessageBoxButton.OK, MessageBoxImage.Information);

                    RSA rsa = RSA.Create(4096);

                    // overall, create a cert that's usable for SSL.  This mostly mirrors the type of cert Fiddler would create with the same params
                    CertificateRequest req = new CertificateRequest(
                        $"CN={AuthorityName}, O={IssuerName}",
                        rsa,
                        HashAlgorithmName.SHA256,
                        RSASignaturePadding.Pkcs1);

                    req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                    req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
                    req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
                    req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

                    // allow a wide range of time for the cert to be valid
                    var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddYears(-1), DateTimeOffset.UtcNow.AddYears(4));
                    cert.FriendlyName = AuthorityName;

                    // create a 2nd cert that has the private keys exportable and persistable (there appears to be no way to do this in one step)
                    _rootCert = new X509Certificate2(cert.Export(X509ContentType.Pfx, CertPassword), CertPassword, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
                    _rootStore.Add(_rootCert);

                    return true;
                }
                catch(Exception)
                {
                    return false;
                }
            }
        }

        public X509Certificate2 CreateHostCertificate(string hostname)
        {
            lock(_hostLock)
            {
                RSA rsa = RSA.Create(2048);

                // overall, create a cert that's usable for SSL.  This mostly mirrors the type of cert Fiddler would create with the same params
                CertificateRequest req = new CertificateRequest(
                    $"CN={hostname}, O={IssuerName}",
                    rsa,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);

                SubjectAlternativeNameBuilder sanBuilder = new SubjectAlternativeNameBuilder();
                sanBuilder.AddDnsName(hostname);
                req.CertificateExtensions.Add(sanBuilder.Build());
                req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
                req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment, true));
                req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
                req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

                // get a unique serial number
                byte[] serial = RandomNumberGenerator.GetBytes(20);

                // allow a wide range of time for the cert to be valid (note end date must be <= than root's end date)
                X509Certificate2 cert = req.Create(_rootCert, DateTimeOffset.UtcNow.AddYears(-1), DateTimeOffset.UtcNow.AddYears(3), serial);

                // create a 2nd cert with the private key attached
                X509Certificate2 certWithKey = cert.CopyWithPrivateKey(rsa);
                certWithKey.FriendlyName = hostname;

                // create a 3rd cert with the private key exportable and persistable (no way to do this in one step)
                X509Certificate2 certWithExportableKey = new X509Certificate2(certWithKey.Export(X509ContentType.Pfx, CertPassword), CertPassword, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
                return certWithExportableKey;
            }
        }

        private string GetHostname(string path)
        {
            string host = path;
            if(path.Contains(":"))
                host = path.Split(":")[0];

            return host;
        }

        public void ExportRootCertificate(string path)
        {
            // if we don't have a root cert, create one
            if(_rootCert == null)
                CreateRootCertificate();

            // write it out to the path specified
            byte[] buff = _rootCert.Export(X509ContentType.Cert);
            File.WriteAllBytes(Path.Combine(path, CertFileName), buff);

            // For back compat with our own tools
            File.WriteAllBytes(Path.Combine(path, CertFileNameFiddler), buff);

            // write it out to the path specified in PEM format
            string pem = "-----BEGIN CERTIFICATE-----\n" + Convert.ToBase64String(buff) + "\n-----END CERTIFICATE-----\n";
            File.WriteAllText(Path.Combine(path, CertFileNamePem), pem);
            File.WriteAllText(Path.Combine(path, CertFileNameFiddlerPem), pem);

        }

        public void RemoveRootCertificate()
        {
            if(_rootStore == null)
                return;

            X509Certificate2Collection certs = _rootStore.Certificates.Find(X509FindType.FindBySubjectName, AuthorityName, false);
            _rootStore.RemoveRange(certs);
        }

        public void RemoveHostCertificates()
        {
            if(_myStore == null)
                return;

            X509Certificate2Collection certs = _myStore.Certificates.Find(X509FindType.FindByIssuerName, IssuerName, false);
            _myStore.RemoveRange(certs);
        }
    }
}
