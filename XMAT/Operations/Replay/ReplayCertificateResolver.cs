// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace XMAT.Operations.Replay
{
    // The client certificate of a run once resolved: a copy of the store's certificate, in no store, whose key is opened
    // silently, and the one origin that may receive it. The run owns it and disposes it when it ends.
    internal sealed class ReplayClientCredential : IDisposable
    {
        private readonly IReplayCertificateNative _native;
        private int _disposed;

        internal ReplayClientCredential(IReplayCertificateNative native, X509Certificate2 certificate, ReplayCredentialOrigin origin)
        {
            _native = native ?? throw new ArgumentNullException(nameof(native));
            Certificate = certificate ?? throw new ArgumentNullException(nameof(certificate));
            Origin = origin ?? throw new ArgumentNullException(nameof(origin));
        }

        internal X509Certificate2 Certificate { get; }

        internal ReplayCredentialOrigin Origin { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _native.Dispose(Certificate);
            }
        }
    }

    // A client certificate that the run cannot use, with its data-free code (ReplayCertificateResolver); nothing was sent.
    internal sealed class ReplayClientCertificateException : Exception
    {
        internal ReplayClientCertificateException(string code)
            : base(ReplayCertificateResolver.Message(code))
        {
            Code = code;
        }

        internal string Code { get; }
    }

    // Resolves a run's client certificate after every check and consent passed, and before anything that can reach the
    // network exists, in steps that may each do only what they list; a refusal at any step stops every later one, and
    // nothing is ever written to a store:
    // 1. The store: the current user's Personal store, opened read-only and only if it exists, and the certificates with
    //    the thumbprint, valid or not: exactly one, or not-found or ambiguous; a store that cannot be read is
    //    store-unavailable.
    // 2. Its public data: not a certificate authority's, allowed for client authentication and digital signatures, an RSA
    //    or ECDSA key, and provider information, which says where its private key is (no-private-key when it has none);
    //    public data that cannot be read is metadata-unavailable.
    // 3. A copy: the provider information read into a buffer that the run owns, a copy of the certificate made from its
    //    encoded form alone, the silent flag set in the buffer, and the buffer set on the copy, never on the store's
    //    certificate, each only after the one before it succeeded. Then the buffer is freed, the store's certificate
    //    disposed and the store closed, before any key is touched.
    // 4. The key, silently: acquired from the copy without any user interface and without healing or caching, one
    //    signature of a fixed digest made silently and verified with the certificate's public key, and the key freed once.
    // A key that would need a PIN, a smart card or consent fails step 4 and is refused (key-unavailable): XMAT itself never
    // asks. The copy keeps the silent flag for the TLS handshake too, which is not verified for every key storage provider:
    // a smart-card provider could still ask then.
    internal static class ReplayCertificateResolver
    {
        internal const string NotFoundCode = "replay.client-cert.not-found";
        internal const string AmbiguousCode = "replay.client-cert.ambiguous";
        internal const string StoreUnavailableCode = "replay.client-cert.store-unavailable";
        internal const string AuthorityCode = "replay.client-cert.ca";
        internal const string NotClientAuthCode = "replay.client-cert.not-client-auth";
        internal const string KeyUsageCode = "replay.client-cert.key-usage";
        internal const string UnsupportedKeyCode = "replay.client-cert.unsupported-key";
        internal const string MetadataUnavailableCode = "replay.client-cert.metadata-unavailable";
        internal const string NoPrivateKeyCode = "replay.client-cert.no-private-key";
        internal const string KeyUnavailableCode = "replay.client-cert.key-unavailable";

        internal const OpenFlags StoreFlags = OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly;

        // CRYPT_ACQUIRE_SILENT_FLAG (0x40) | CRYPT_ACQUIRE_ALLOW_NCRYPT_KEY_FLAG (0x10000) | CRYPT_ACQUIRE_NO_HEALING (0x8),
        // never CRYPT_ACQUIRE_CACHE_FLAG, so the caller frees the key.
        internal const uint AcquireFlags = 0x40 | 0x10000 | 0x8;

        // The key specification of a CNG key (CERT_NCRYPT_KEY_SPEC).
        internal const uint CngKeySpec = 0xFFFFFFFF;

        internal const uint NCryptSilentFlag = 0x40;
        internal const uint NCryptPadPkcs1Flag = 0x2;

        // CRYPT_SILENT in CRYPT_KEY_PROV_INFO.dwFlags: the key is opened without any user interface.
        internal const int CryptSilent = 0x40;

        internal static readonly int ProviderInfoSize = Marshal.SizeOf<ReplayKeyProviderInfo>();

        internal static readonly int ProviderInfoFlagsOffset =
            (int)Marshal.OffsetOf<ReplayKeyProviderInfo>(nameof(ReplayKeyProviderInfo.Flags));

        // What the key signs to show that it can be used silently.
        internal static readonly byte[] KeyCheckDigest = SHA256.HashData("XMAT replay: a client certificate's key, used silently"u8);

        internal static ReplayClientCredential Resolve(
            IReplayCertificateNative native,
            ReplayClientCertificate certificate,
            ReplayCredentialOrigin origin,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(native);
            ArgumentNullException.ThrowIfNull(certificate);
            ArgumentNullException.ThrowIfNull(origin);
            cancellationToken.ThrowIfCancellationRequested();
            X509Certificate2 copy = Copy(native, certificate.Thumbprint, cancellationToken, out ReplayKeyAlgorithm algorithm);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                CheckKey(native, copy, algorithm);
                return new ReplayClientCredential(native, copy, origin);
            }
            catch
            {
                Quietly(() => native.Dispose(copy));
                throw;
            }
        }

        // Steps 1 to 3: the copy, with the store closed and everything else released. Cancellation is checked between the
        // steps, and stays cancellation.
        private static X509Certificate2 Copy(
            IReplayCertificateNative native,
            string thumbprint,
            CancellationToken cancellationToken,
            out ReplayKeyAlgorithm algorithm)
        {
            IReplayCertificateStore store;
            try
            {
                store = native.OpenStore(StoreFlags);
            }
            catch (Exception ex) when (IsFailure(ex))
            {
                throw Refused(StoreUnavailableCode);
            }

            if (store == null)
            {
                throw Refused(StoreUnavailableCode);
            }

            X509Certificate2 match = null;
            X509Certificate2 copy = null;
            IntPtr buffer = IntPtr.Zero;
            bool copied = false;
            try
            {
                IReadOnlyList<X509Certificate2> found;
                try
                {
                    found = store.FindByThumbprint(thumbprint);
                }
                catch (Exception ex) when (IsFailure(ex))
                {
                    throw Refused(StoreUnavailableCode);
                }

                if (found == null)
                {
                    throw Refused(StoreUnavailableCode);
                }

                if (found.Count != 1)
                {
                    foreach (X509Certificate2 certificate in found)
                    {
                        Quietly(() => native.Dispose(certificate));
                    }

                    throw Refused(found.Count == 0 ? NotFoundCode : AmbiguousCode);
                }

                match = found[0];
                cancellationToken.ThrowIfCancellationRequested();
                algorithm = CheckMetadata(native, match);
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!native.QueryProviderInfoSize(match, out uint allocated) || allocated < ProviderInfoSize)
                    {
                        throw Refused(KeyUnavailableCode);
                    }

                    buffer = native.AllocateBuffer(allocated);
                    uint size = allocated;
                    if (buffer == IntPtr.Zero || !native.ReadProviderInfo(match, buffer, ref size) ||
                        size < ProviderInfoSize || size > allocated)
                    {
                        throw Refused(KeyUnavailableCode);
                    }

                    copy = native.CopyDetached(match);
                    if (copy == null)
                    {
                        throw Refused(KeyUnavailableCode);
                    }

                    Marshal.WriteInt32(
                        buffer, ProviderInfoFlagsOffset, Marshal.ReadInt32(buffer, ProviderInfoFlagsOffset) | CryptSilent);
                    if (!native.SetProviderInfo(copy, buffer))
                    {
                        throw Refused(KeyUnavailableCode);
                    }

                    copied = true;
                    return copy;
                }
                catch (Exception ex) when (IsFailure(ex))
                {
                    throw Refused(KeyUnavailableCode);
                }
            }
            finally
            {
                // What exists is released, newest first; the store is always closed.
                if (!copied && copy != null)
                {
                    Quietly(() => native.Dispose(copy));
                }

                if (buffer != IntPtr.Zero)
                {
                    IntPtr owned = buffer;
                    Quietly(() => native.FreeBuffer(owned));
                }

                if (match != null)
                {
                    Quietly(() => native.Dispose(match));
                }

                Quietly(store.Close);
            }
        }

        // Step 2: the certificate's public data and whether it says where its key is; no key access.
        private static ReplayKeyAlgorithm CheckMetadata(IReplayCertificateNative native, X509Certificate2 match)
        {
            ReplayCertificateMetadata metadata;
            try
            {
                metadata = native.ReadMetadata(match);
            }
            catch (Exception ex) when (IsFailure(ex))
            {
                throw Refused(MetadataUnavailableCode);
            }

            if (metadata == null)
            {
                throw Refused(MetadataUnavailableCode);
            }

            string refusal = Refusal(metadata);
            if (refusal != null)
            {
                throw Refused(refusal);
            }

            // False only when the certificate has no provider information; a lookup that fails otherwise throws.
            bool hasProviderInfo;
            try
            {
                hasProviderInfo = native.HasProviderInfo(match);
            }
            catch (Exception ex) when (IsFailure(ex))
            {
                throw Refused(KeyUnavailableCode);
            }

            if (!hasProviderInfo)
            {
                throw Refused(NoPrivateKeyCode);
            }

            return metadata.KeyAlgorithm;
        }

        // Why the public data alone refuses a certificate, or null.
        internal static string Refusal(ReplayCertificateMetadata metadata)
        {
            ArgumentNullException.ThrowIfNull(metadata);
            if (metadata.IsCertificateAuthority)
            {
                return AuthorityCode;
            }

            if (!metadata.AllowsClientAuthentication)
            {
                return NotClientAuthCode;
            }

            if (!metadata.AllowsDigitalSignature)
            {
                return KeyUsageCode;
            }

            return metadata.KeyAlgorithm is ReplayKeyAlgorithm.Rsa or ReplayKeyAlgorithm.Ecdsa ? null : UnsupportedKeyCode;
        }

        // Step 4: the copy's key, acquired and used silently, and freed once by whoever owns it. A handle that the copy
        // owns (callerFrees false) is used only while the copy is alive, and never freed here.
        private static void CheckKey(IReplayCertificateNative native, X509Certificate2 copy, ReplayKeyAlgorithm algorithm)
        {
            bool acquired = false;
            IntPtr key = IntPtr.Zero;
            uint keySpec = 0;
            bool callerFrees = false;
            try
            {
                acquired = native.AcquireKey(copy, AcquireFlags, out key, out keySpec, out callerFrees);
                if (!acquired || key == IntPtr.Zero)
                {
                    throw Refused(KeyUnavailableCode);
                }

                byte[] signature;
                bool signed = keySpec == CngKeySpec
                    ? native.SignCng(
                        key,
                        KeyCheckDigest,
                        NCryptSilentFlag | (algorithm == ReplayKeyAlgorithm.Rsa ? NCryptPadPkcs1Flag : 0),
                        out signature)
                    : TrySignCapi(native, key, keySpec, algorithm, out signature);
                if (!signed || !Verifies(copy, algorithm, signature))
                {
                    throw Refused(KeyUnavailableCode);
                }
            }
            catch (Exception ex) when (IsFailure(ex))
            {
                throw Refused(KeyUnavailableCode);
            }
            finally
            {
                if (acquired && callerFrees && key != IntPtr.Zero)
                {
                    IntPtr owned = key;
                    if (keySpec == CngKeySpec)
                    {
                        Quietly(() => native.FreeNCryptKey(owned));
                    }
                    else
                    {
                        Quietly(() => native.ReleaseCapiContext(owned));
                    }
                }

                GC.KeepAlive(copy);
            }
        }

        // CryptoAPI signs only with RSA keys.
        private static bool TrySignCapi(
            IReplayCertificateNative native, IntPtr key, uint keySpec, ReplayKeyAlgorithm algorithm, out byte[] signature)
        {
            signature = null;
            return algorithm == ReplayKeyAlgorithm.Rsa && native.SignCapi(key, keySpec, KeyCheckDigest, out signature);
        }

        private static bool Verifies(X509Certificate2 certificate, ReplayKeyAlgorithm algorithm, byte[] signature)
        {
            if (signature == null || signature.Length == 0)
            {
                return false;
            }

            if (algorithm == ReplayKeyAlgorithm.Rsa)
            {
                using RSA rsa = certificate.GetRSAPublicKey();
                return rsa != null && rsa.VerifyHash(KeyCheckDigest, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }

            using ECDsa ecdsa = certificate.GetECDsaPublicKey();
            return ecdsa != null && ecdsa.VerifyHash(KeyCheckDigest, signature);
        }

        private static ReplayClientCertificateException Refused(string code) => new(code);

        // A native call's failure, which refuses the certificate: never cancellation, which stays cancellation, never a
        // refusal that is already decided, and never a fatal exception.
        private static bool IsFailure(Exception ex) =>
            !ExceptionUtilities.IsFatal(ex) && ex is not OperationCanceledException && ex is not ReplayClientCertificateException;

        // Data-free: what is wrong with the certificate, never which one it is.
        internal static string Message(string code) => code switch
        {
            NotFoundCode => "No certificate with that thumbprint is in your Personal certificate store; nothing was sent.",
            AmbiguousCode =>
                "More than one certificate with that thumbprint is in your Personal certificate store; nothing was sent.",
            StoreUnavailableCode => "The certificate store could not be read; nothing was sent.",
            AuthorityCode => "The certificate is a certificate authority's, not a client's; nothing was sent.",
            NotClientAuthCode => "The certificate is not valid for client authentication; nothing was sent.",
            KeyUsageCode => "The certificate's key usage does not allow digital signatures; nothing was sent.",
            UnsupportedKeyCode => "The certificate's key is neither an RSA nor an ECDSA key; nothing was sent.",
            MetadataUnavailableCode => "The certificate's authentication properties could not be read; nothing was sent.",
            NoPrivateKeyCode => "This PC has no private key recorded for the certificate; nothing was sent.",
            _ => "The certificate's private key cannot be used without asking for a PIN, a smart card or consent, or " +
                "could not be used at all; XMAT never asks, and nothing was sent.",
        };

        // Releasing something that the run owns never replaces the run's outcome.
        private static void Quietly(Action release)
        {
            try
            {
                release();
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                // Released by the finalizer instead, where it has one.
            }
        }
    }

    // A certificate that the GUI may offer: one that step 2 of the resolution would accept, so it says where its private
    // key is, is not a certificate authority's, is allowed for client authentication and digital signatures, and has an
    // RSA or ECDSA key. Expired certificates are listed too, for the GUI to mark.
    internal sealed record ReplayCertificateListing(
        string Thumbprint,
        string Subject,
        string Issuer,
        DateTimeOffset NotBefore,
        DateTimeOffset NotAfter);

    // The GUI's list of client certificates: the current user's Personal store read-only, the public data of each
    // certificate and whether it says where its key is, never a key or the network. Each send resolves the chosen
    // certificate again (ReplayCertificateResolver).
    internal static class ReplayCertificateCatalog
    {
        internal static IReadOnlyList<ReplayCertificateListing> List(
            IReplayCertificateNative native, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(native);
            cancellationToken.ThrowIfCancellationRequested();
            IReplayCertificateStore store;
            try
            {
                store = native.OpenStore(ReplayCertificateResolver.StoreFlags);
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                return Array.Empty<ReplayCertificateListing>();
            }

            if (store == null)
            {
                return Array.Empty<ReplayCertificateListing>();
            }

            var listings = new List<ReplayCertificateListing>();
            try
            {
                IReadOnlyList<X509Certificate2> certificates;
                try
                {
                    certificates = store.ListAll() ?? Array.Empty<X509Certificate2>();
                }
                catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
                {
                    return Array.Empty<ReplayCertificateListing>();
                }

                foreach (X509Certificate2 certificate in certificates)
                {
                    try
                    {
                        if (!cancellationToken.IsCancellationRequested && Listing(native, certificate) is { } listing)
                        {
                            listings.Add(listing);
                        }
                    }
                    finally
                    {
                        try
                        {
                            native.Dispose(certificate);
                        }
                        catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
                        {
                            // Released by its finalizer instead.
                        }
                    }
                }
            }
            finally
            {
                try
                {
                    store.Close();
                }
                catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
                {
                    // Released by its finalizer instead.
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return listings
                .OrderBy(listing => listing.Subject, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(listing => listing.NotAfter)
                .ThenBy(listing => listing.Thumbprint, StringComparer.Ordinal)
                .ToList()
                .AsReadOnly();
        }

        private static ReplayCertificateListing Listing(IReplayCertificateNative native, X509Certificate2 certificate)
        {
            try
            {
                ReplayCertificateMetadata metadata = native.ReadMetadata(certificate);
                if (metadata == null || ReplayCertificateResolver.Refusal(metadata) != null || !native.HasProviderInfo(certificate))
                {
                    return null;
                }

                return new ReplayCertificateListing(
                    metadata.Thumbprint, metadata.Subject, metadata.Issuer, metadata.NotBefore, metadata.NotAfter);
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                return null;
            }
        }
    }
}
