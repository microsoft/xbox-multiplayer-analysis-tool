// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace XMAT.Operations.Replay
{
    internal enum ReplayKeyAlgorithm
    {
        Other,
        Rsa,
        Ecdsa,
    }

    // A certificate's public data, read without touching its key: who it names and who issued it, when it is valid, and
    // what its extensions allow. AllowsClientAuthentication is false when an enhanced key usage extension names neither
    // client authentication nor any purpose; AllowsDigitalSignature is false when a key usage extension leaves out digital
    // signatures. Both are true without the extension.
    internal sealed record ReplayCertificateMetadata(
        string Thumbprint,
        string Subject,
        string Issuer,
        DateTimeOffset NotBefore,
        DateTimeOffset NotAfter,
        bool IsCertificateAuthority,
        bool AllowsClientAuthentication,
        bool AllowsDigitalSignature,
        ReplayKeyAlgorithm KeyAlgorithm);

    // The current user's Personal store, open read-only.
    internal interface IReplayCertificateStore
    {
        // Every certificate with that SHA-1 thumbprint, valid or not; the caller disposes each.
        IReadOnlyList<X509Certificate2> FindByThumbprint(string thumbprint);

        // Every certificate of the store; the caller disposes each.
        IReadOnlyList<X509Certificate2> ListAll();

        void Close();
    }

    // The native calls of a client certificate's resolution (ReplayCertificateResolver) and of the GUI's certificate list
    // (ReplayCertificateCatalog), one method per call, so that what each step may do can be shown by recording them. The
    // caller decides every flag, the order of the calls and what their results mean; these only make the calls.
    internal interface IReplayCertificateNative
    {
        IReplayCertificateStore OpenStore(OpenFlags flags);

        ReplayCertificateMetadata ReadMetadata(X509Certificate2 certificate);

        // Whether the certificate's context has a CERT_KEY_PROV_INFO_PROP_ID, which says where its private key is: false
        // only when it has none; a lookup that fails for another reason throws.
        bool HasProviderInfo(X509Certificate2 certificate);

        bool QueryProviderInfoSize(X509Certificate2 certificate, out uint size);

        IntPtr AllocateBuffer(uint size);

        bool ReadProviderInfo(X509Certificate2 certificate, IntPtr buffer, ref uint size);

        // A copy of the certificate made from its encoded form alone, in no store and without any property.
        X509Certificate2 CopyDetached(X509Certificate2 certificate);

        bool SetProviderInfo(X509Certificate2 certificate, IntPtr buffer);

        void FreeBuffer(IntPtr buffer);

        void Dispose(X509Certificate2 certificate);

        bool AcquireKey(X509Certificate2 certificate, uint flags, out IntPtr key, out uint keySpec, out bool callerFrees);

        // A CNG signature of digest; with NCRYPT_PAD_PKCS1_FLAG in flags, PKCS#1 v1.5 for SHA-256.
        bool SignCng(IntPtr key, byte[] digest, uint flags, out byte[] signature);

        // A CryptoAPI signature of a SHA-256 digest, returned big-endian as the other signatures are.
        bool SignCapi(IntPtr key, uint keySpec, byte[] digest, out byte[] signature);

        void FreeNCryptKey(IntPtr key);

        void ReleaseCapiContext(IntPtr key);
    }

    // The provider information property of a certificate (CRYPT_KEY_PROV_INFO), as CertGetCertificateContextProperty
    // writes it at the start of its buffer.
    [StructLayout(LayoutKind.Sequential)]
    internal struct ReplayKeyProviderInfo
    {
        internal IntPtr ContainerName;
        internal IntPtr ProviderName;
        internal uint ProviderType;
        internal uint Flags;
        internal uint ProviderParameterCount;
        internal IntPtr ProviderParameters;
        internal uint KeySpec;
    }

    // The Windows calls themselves.
    internal sealed class ReplayCertificateNative : IReplayCertificateNative
    {
        private const uint KeyProviderInfoPropertyId = 2;

        // CRYPT_E_NOT_FOUND: the context has no such property.
        private const int CryptNotFound = unchecked((int)0x80092004);
        private const string RsaOid = "1.2.840.113549.1.1.1";
        private const string EcPublicKeyOid = "1.2.840.10045.2.1";
        private const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";
        private const string AnyExtendedKeyUsageOid = "2.5.29.37.0";
        private const uint NCryptPadPkcs1 = 0x2;
        private const uint CalgSha256 = 0x800C;
        private const uint HashValueParameter = 2;

        private ReplayCertificateNative()
        {
        }

        internal static ReplayCertificateNative Instance { get; } = new();

        public IReplayCertificateStore OpenStore(OpenFlags flags)
        {
            var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            try
            {
                store.Open(flags);
                return new Store(store);
            }
            catch
            {
                store.Dispose();
                throw;
            }
        }

        public ReplayCertificateMetadata ReadMetadata(X509Certificate2 certificate)
        {
            ArgumentNullException.ThrowIfNull(certificate);
            bool authority = false;
            bool clientAuthentication = true;
            bool digitalSignature = true;
            foreach (X509Extension extension in certificate.Extensions)
            {
                switch (extension)
                {
                    case X509BasicConstraintsExtension constraints:
                        authority |= constraints.CertificateAuthority;
                        break;
                    case X509EnhancedKeyUsageExtension usage:
                        clientAuthentication &= usage.EnhancedKeyUsages.Cast<Oid>()
                            .Any(oid => oid.Value is ClientAuthenticationOid or AnyExtendedKeyUsageOid);
                        break;
                    case X509KeyUsageExtension keyUsage:
                        digitalSignature &= (keyUsage.KeyUsages & X509KeyUsageFlags.DigitalSignature) != 0;
                        break;
                }
            }

            ReplayKeyAlgorithm algorithm = certificate.PublicKey.Oid.Value switch
            {
                RsaOid => ReplayKeyAlgorithm.Rsa,
                EcPublicKeyOid => ReplayKeyAlgorithm.Ecdsa,
                _ => ReplayKeyAlgorithm.Other,
            };
            return new ReplayCertificateMetadata(
                certificate.Thumbprint,
                certificate.Subject,
                certificate.Issuer,
                new DateTimeOffset(certificate.NotBefore.ToUniversalTime(), TimeSpan.Zero),
                new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero),
                authority,
                clientAuthentication,
                digitalSignature,
                algorithm);
        }

        public bool HasProviderInfo(X509Certificate2 certificate)
        {
            ArgumentNullException.ThrowIfNull(certificate);
            uint size = 0;
            bool found = NativeMethods.CertGetCertificateContextProperty(
                certificate.Handle, KeyProviderInfoPropertyId, IntPtr.Zero, ref size);
            int error = Marshal.GetLastPInvokeError();
            GC.KeepAlive(certificate);
            if (found)
            {
                return true;
            }

            if (error == CryptNotFound)
            {
                return false;
            }

            throw new CryptographicException(error);
        }

        public bool QueryProviderInfoSize(X509Certificate2 certificate, out uint size)
        {
            ArgumentNullException.ThrowIfNull(certificate);
            size = 0;
            bool found = NativeMethods.CertGetCertificateContextProperty(
                certificate.Handle, KeyProviderInfoPropertyId, IntPtr.Zero, ref size);
            GC.KeepAlive(certificate);
            return found;
        }

        public IntPtr AllocateBuffer(uint size) => Marshal.AllocHGlobal(checked((int)size));

        public bool ReadProviderInfo(X509Certificate2 certificate, IntPtr buffer, ref uint size)
        {
            ArgumentNullException.ThrowIfNull(certificate);
            bool read = NativeMethods.CertGetCertificateContextProperty(
                certificate.Handle, KeyProviderInfoPropertyId, buffer, ref size);
            GC.KeepAlive(certificate);
            return read;
        }

        public X509Certificate2 CopyDetached(X509Certificate2 certificate)
        {
            ArgumentNullException.ThrowIfNull(certificate);
            return X509CertificateLoader.LoadCertificate(certificate.RawDataMemory.Span);
        }

        public bool SetProviderInfo(X509Certificate2 certificate, IntPtr buffer)
        {
            ArgumentNullException.ThrowIfNull(certificate);
            bool set = NativeMethods.CertSetCertificateContextProperty(
                certificate.Handle, KeyProviderInfoPropertyId, 0, buffer);
            GC.KeepAlive(certificate);
            return set;
        }

        public void FreeBuffer(IntPtr buffer) => Marshal.FreeHGlobal(buffer);

        public void Dispose(X509Certificate2 certificate) => certificate?.Dispose();

        public bool AcquireKey(X509Certificate2 certificate, uint flags, out IntPtr key, out uint keySpec, out bool callerFrees)
        {
            ArgumentNullException.ThrowIfNull(certificate);
            bool acquired = NativeMethods.CryptAcquireCertificatePrivateKey(
                certificate.Handle, flags, IntPtr.Zero, out key, out keySpec, out callerFrees);
            GC.KeepAlive(certificate);
            return acquired;
        }

        public bool SignCng(IntPtr key, byte[] digest, uint flags, out byte[] signature)
        {
            ArgumentNullException.ThrowIfNull(digest);
            signature = null;
            IntPtr algorithm = IntPtr.Zero;
            IntPtr padding = IntPtr.Zero;
            try
            {
                if ((flags & NCryptPadPkcs1) != 0)
                {
                    algorithm = Marshal.StringToHGlobalUni("SHA256");
                    padding = Marshal.AllocHGlobal(IntPtr.Size);
                    Marshal.WriteIntPtr(padding, algorithm);
                }

                if (NativeMethods.NCryptSignHash(key, padding, digest, digest.Length, null, 0, out int length, flags) != 0 ||
                    length <= 0)
                {
                    return false;
                }

                var buffer = new byte[length];
                if (NativeMethods.NCryptSignHash(key, padding, digest, digest.Length, buffer, buffer.Length, out length, flags) != 0 ||
                    length <= 0 || length > buffer.Length)
                {
                    return false;
                }

                signature = buffer.AsSpan(0, length).ToArray();
                return true;
            }
            finally
            {
                if (padding != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(padding);
                }

                if (algorithm != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(algorithm);
                }
            }
        }

        public bool SignCapi(IntPtr key, uint keySpec, byte[] digest, out byte[] signature)
        {
            ArgumentNullException.ThrowIfNull(digest);
            signature = null;
            if (!NativeMethods.CryptCreateHash(key, CalgSha256, IntPtr.Zero, 0, out IntPtr hash))
            {
                return false;
            }

            try
            {
                uint length = 0;
                if (!NativeMethods.CryptSetHashParam(hash, HashValueParameter, digest, 0) ||
                    !NativeMethods.CryptSignHashW(hash, keySpec, IntPtr.Zero, 0, null, ref length) ||
                    length == 0)
                {
                    return false;
                }

                var buffer = new byte[length];
                if (!NativeMethods.CryptSignHashW(hash, keySpec, IntPtr.Zero, 0, buffer, ref length) ||
                    length == 0 || length > buffer.Length)
                {
                    return false;
                }

                // CryptoAPI writes signatures little-endian.
                signature = buffer.AsSpan(0, (int)length).ToArray();
                Array.Reverse(signature);
                return true;
            }
            finally
            {
                _ = NativeMethods.CryptDestroyHash(hash);
            }
        }

        public void FreeNCryptKey(IntPtr key) => _ = NativeMethods.NCryptFreeObject(key);

        public void ReleaseCapiContext(IntPtr key) => _ = NativeMethods.CryptReleaseContext(key, 0);

        private sealed class Store(X509Store store) : IReplayCertificateStore
        {
            // Find returns new instances, so every certificate that the store's collection made is disposed here.
            public IReadOnlyList<X509Certificate2> FindByThumbprint(string thumbprint)
            {
                X509Certificate2Collection all = store.Certificates;
                try
                {
                    return all.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false).ToList();
                }
                finally
                {
                    foreach (X509Certificate2 certificate in all)
                    {
                        certificate.Dispose();
                    }
                }
            }

            public IReadOnlyList<X509Certificate2> ListAll() => store.Certificates.ToList();

            public void Close() => store.Dispose();
        }

        private static class NativeMethods
        {
            [DllImport("crypt32.dll", SetLastError = true, ExactSpelling = true)]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CertGetCertificateContextProperty(
                IntPtr certificateContext, uint propertyId, IntPtr data, ref uint dataSize);

            [DllImport("crypt32.dll", SetLastError = true, ExactSpelling = true)]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CertSetCertificateContextProperty(
                IntPtr certificateContext, uint propertyId, uint flags, IntPtr data);

            [DllImport("crypt32.dll", SetLastError = true, ExactSpelling = true)]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CryptAcquireCertificatePrivateKey(
                IntPtr certificateContext,
                uint flags,
                IntPtr parameters,
                out IntPtr key,
                out uint keySpec,
                [MarshalAs(UnmanagedType.Bool)] out bool callerFrees);

            [DllImport("ncrypt.dll", ExactSpelling = true)]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            internal static extern int NCryptSignHash(
                IntPtr key,
                IntPtr paddingInfo,
                byte[] hashValue,
                int hashValueSize,
                byte[] signature,
                int signatureSize,
                out int resultSize,
                uint flags);

            [DllImport("ncrypt.dll", ExactSpelling = true)]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            internal static extern int NCryptFreeObject(IntPtr handle);

            [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CryptCreateHash(IntPtr provider, uint algorithm, IntPtr key, uint flags, out IntPtr hash);

            [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CryptSetHashParam(IntPtr hash, uint parameter, byte[] data, uint flags);

            [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CryptSignHashW(
                IntPtr hash, uint keySpec, IntPtr description, uint flags, byte[] signature, ref uint signatureSize);

            [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CryptDestroyHash(IntPtr hash);

            [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CryptReleaseContext(IntPtr provider, uint flags);
        }
    }
}
