// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using XMAT.Operations.Replay;

namespace XMAT.Tests.Replay
{
    // The native calls of a client certificate's resolution and of the GUI's list, recorded in order, on real certificates
    // and never on a real certificate store: the store is the test's list of certificates, and each find or list returns
    // new instances over their contexts, which keep their keys' provider information. Every other call is the production
    // call, unless Fake is set, which fakes the provider information and the key (the signature is made with
    // SigningCertificate's private key), or a test fails, throws or replaces it. A call is recorded as its name, then the
    // certificate it was made on ("match", "copy", "listed1"...) and its flags; a name in Failing makes the call fail (false,
    // null or nothing), one in Throwing makes it throw, and an action in OnCall runs when the call is made.
    internal sealed class RecordingCertificateNative : IReplayCertificateNative
    {
        private readonly IReplayCertificateNative _native = ReplayCertificateNative.Instance;
        private readonly List<X509Certificate2> _store;
        private readonly object _gate = new();
        private readonly List<string> _calls = new();
        private readonly Dictionary<object, string> _labels = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<IntPtr> _fakeKeys = new();
        private readonly HashSet<IntPtr> _buffers = new();
        private int _nextKey = 0x5EED;

        internal RecordingCertificateNative(params X509Certificate2[] store)
        {
            _store = store.ToList();
        }

        internal bool Fake { get; set; }

        internal HashSet<string> Failing { get; } = new(StringComparer.Ordinal);

        internal HashSet<string> Throwing { get; } = new(StringComparer.Ordinal);

        internal Dictionary<string, Action> OnCall { get; } = new(StringComparer.Ordinal);

        // Replaces the public data that ReadMetadata returns.
        internal Func<X509Certificate2, ReplayCertificateMetadata> Metadata { get; set; }

        // Replaces whether a certificate has provider information.
        internal bool? ProviderInfo { get; set; }

        // Replaces the size that the size query reports.
        internal uint? ProviderInfoSize { get; set; }

        // Replaces the size that a data read reports having written.
        internal uint? ProviderInfoReadSize { get; set; }

        // With Fake: the key specification and ownership that acquisition reports, and the signature (null: a real one).
        internal uint KeySpec { get; set; } = ReplayCertificateResolver.CngKeySpec;

        internal bool CallerFrees { get; set; } = true;

        internal byte[] Signature { get; set; }

        internal X509Certificate2 SigningCertificate { get; set; }

        internal IReadOnlyList<string> Calls
        {
            get
            {
                lock (_gate)
                {
                    return _calls.ToArray();
                }
            }
        }

        // Buffers allocated and not freed.
        internal int LiveBuffers
        {
            get
            {
                lock (_gate)
                {
                    return _buffers.Count;
                }
            }
        }

        // Adds a line of the test's own to the record, such as a sender's disposal.
        internal void Note(string entry)
        {
            lock (_gate)
            {
                _calls.Add(entry);
            }
        }

        public IReplayCertificateStore OpenStore(OpenFlags flags)
        {
            Call("open", "open:" + (int)flags);
            return Failing.Contains("open") ? null : new Store(this);
        }

        public ReplayCertificateMetadata ReadMetadata(X509Certificate2 certificate)
        {
            Call("metadata", "metadata:" + Label(certificate));
            if (Failing.Contains("metadata"))
            {
                return null;
            }

            return Metadata != null ? Metadata(certificate) : _native.ReadMetadata(certificate);
        }

        public bool HasProviderInfo(X509Certificate2 certificate)
        {
            Call("has-provider-info", "has-provider-info:" + Label(certificate));
            if (ProviderInfo is bool given)
            {
                return given;
            }

            return !Failing.Contains("has-provider-info") && (Fake || _native.HasProviderInfo(certificate));
        }

        public bool QueryProviderInfoSize(X509Certificate2 certificate, out uint size)
        {
            Call("size-query", "size-query:" + Label(certificate));
            size = 0;
            if (Failing.Contains("size-query"))
            {
                return false;
            }

            bool found = true;
            if (Fake)
            {
                size = (uint)Marshal.SizeOf<ReplayKeyProviderInfo>() + 64;
            }
            else
            {
                found = _native.QueryProviderInfoSize(certificate, out size);
            }

            size = ProviderInfoSize ?? size;
            return found;
        }

        public IntPtr AllocateBuffer(uint size)
        {
            Call("buffer", "buffer");
            if (Failing.Contains("buffer"))
            {
                return IntPtr.Zero;
            }

            IntPtr buffer = _native.AllocateBuffer(size);
            lock (_gate)
            {
                _buffers.Add(buffer);
            }

            return buffer;
        }

        public bool ReadProviderInfo(X509Certificate2 certificate, IntPtr buffer, ref uint size)
        {
            Call("data-read", "data-read:" + Label(certificate));
            if (Failing.Contains("data-read"))
            {
                return false;
            }

            if (!Fake)
            {
                bool read = _native.ReadProviderInfo(certificate, buffer, ref size);
                size = ProviderInfoReadSize ?? size;
                return read;
            }

            Marshal.Copy(new byte[size], 0, buffer, (int)size);
            size = ProviderInfoReadSize ?? size;
            return true;
        }

        public X509Certificate2 CopyDetached(X509Certificate2 certificate)
        {
            Call("copy", "copy:" + Label(certificate));
            if (Failing.Contains("copy"))
            {
                return null;
            }

            X509Certificate2 copy = _native.CopyDetached(certificate);
            Labeled(copy, "copy");
            return copy;
        }

        public bool SetProviderInfo(X509Certificate2 certificate, IntPtr buffer)
        {
            int flags = Marshal.ReadInt32(buffer, ReplayCertificateResolver.ProviderInfoFlagsOffset);
            Call("set", "set:" + Label(certificate) + ":" + ((flags & ReplayCertificateResolver.CryptSilent) != 0 ? "silent" : "not-silent"));
            if (Failing.Contains("set"))
            {
                return false;
            }

            return Fake || _native.SetProviderInfo(certificate, buffer);
        }

        public void FreeBuffer(IntPtr buffer)
        {
            Call("free-buffer", "free-buffer");
            lock (_gate)
            {
                _buffers.Remove(buffer);
            }

            _native.FreeBuffer(buffer);
        }

        public void Dispose(X509Certificate2 certificate)
        {
            Call("dispose", "dispose:" + Label(certificate));
            _native.Dispose(certificate);
        }

        public bool AcquireKey(X509Certificate2 certificate, uint flags, out IntPtr key, out uint keySpec, out bool callerFrees)
        {
            Call("acquire", "acquire:" + Label(certificate) + ":0x" + flags.ToString("X8"));
            key = IntPtr.Zero;
            keySpec = 0;
            callerFrees = false;
            if (Failing.Contains("acquire"))
            {
                return false;
            }

            if (!Fake)
            {
                return _native.AcquireKey(certificate, flags, out key, out keySpec, out callerFrees);
            }

            lock (_gate)
            {
                key = new IntPtr(_nextKey++);
                _fakeKeys.Add(key);
            }

            keySpec = KeySpec;
            callerFrees = CallerFrees;
            return true;
        }

        public bool SignCng(IntPtr key, byte[] digest, uint flags, out byte[] signature)
        {
            Call("sign-cng", "sign-cng:0x" + flags.ToString("X8"));
            signature = null;
            if (Failing.Contains("sign-cng"))
            {
                return false;
            }

            if (!IsFake(key))
            {
                return _native.SignCng(key, digest, flags, out signature);
            }

            signature = Signature ?? Sign(digest);
            return true;
        }

        public bool SignCapi(IntPtr key, uint keySpec, byte[] digest, out byte[] signature)
        {
            Call("sign-capi", "sign-capi:" + keySpec);
            signature = null;
            if (Failing.Contains("sign-capi"))
            {
                return false;
            }

            if (!IsFake(key))
            {
                return _native.SignCapi(key, keySpec, digest, out signature);
            }

            signature = Signature ?? Sign(digest);
            return true;
        }

        public void FreeNCryptKey(IntPtr key)
        {
            Call("free-ncrypt", "free-ncrypt");
            if (!IsFake(key))
            {
                _native.FreeNCryptKey(key);
            }
        }

        public void ReleaseCapiContext(IntPtr key)
        {
            Call("release-capi", "release-capi");
            if (!IsFake(key))
            {
                _native.ReleaseCapiContext(key);
            }
        }

        private bool IsFake(IntPtr key)
        {
            lock (_gate)
            {
                return _fakeKeys.Contains(key);
            }
        }

        private byte[] Sign(byte[] digest)
        {
            using RSA rsa = SigningCertificate?.GetRSAPrivateKey();
            if (rsa != null)
            {
                return rsa.SignHash(digest, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }

            using ECDsa ecdsa = SigningCertificate?.GetECDsaPrivateKey() ??
                throw new InvalidOperationException("A fake signature needs SigningCertificate.");
            return ecdsa.SignHash(digest);
        }

        private void Call(string name, string entry)
        {
            Action action;
            lock (_gate)
            {
                _calls.Add(entry);
                OnCall.TryGetValue(name, out action);
            }

            action?.Invoke();
            if (Throwing.Contains(name))
            {
                throw new CryptographicException("A test failed the call " + name + ".");
            }
        }

        private string Label(X509Certificate2 certificate)
        {
            lock (_gate)
            {
                return certificate != null && _labels.TryGetValue(certificate, out string label) ? label : "?";
            }
        }

        private X509Certificate2 Labeled(X509Certificate2 certificate, string label)
        {
            lock (_gate)
            {
                _labels[certificate] = label;
            }

            return certificate;
        }

        private sealed class Store(RecordingCertificateNative owner) : IReplayCertificateStore
        {
            public IReadOnlyList<X509Certificate2> FindByThumbprint(string thumbprint)
            {
                owner.Call("find", "find");
                if (owner.Failing.Contains("find"))
                {
                    return null;
                }

                var found = new List<X509Certificate2>();
                foreach (X509Certificate2 certificate in owner._store.Where(certificate => certificate.Thumbprint == thumbprint))
                {
                    found.Add(owner.Labeled(new X509Certificate2(certificate), found.Count == 0 ? "match" : "match" + (found.Count + 1)));
                }

                return found;
            }

            public IReadOnlyList<X509Certificate2> ListAll()
            {
                owner.Call("list", "list");
                return owner.Failing.Contains("list")
                    ? null
                    : owner._store.Select((certificate, index) => owner.Labeled(new X509Certificate2(certificate), "listed" + (index + 1))).ToList();
            }

            public void Close() => owner.Call("close", "close");
        }
    }
}
