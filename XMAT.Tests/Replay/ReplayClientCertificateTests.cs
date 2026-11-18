// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using XMAT.Operations;
using XMAT.Operations.Replay;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Replay
{
    // A run that may offer a client certificate, without the network: the thumbprint and the consented origin
    // that are accepted, the checks and the consent that come first and touch no certificate, and the resolution that
    // follows them, as the recorded native calls show it. Each step does only what it may, a refusal at any step stops
    // every later one and releases what exists, the store's certificate is never changed, the key is used silently and
    // freed once by its owner, and the run owns the copy until it ends, after its sender. Certificates are made in memory;
    // keys imported from a PFX live in the user's key store only until disposed; no certificate store is ever written.
    public sealed class ReplayClientCertificateTests : IDisposable
    {
        private const string Url = "https://xmat-client-cert.test/item";
        private const string Acquire = "acquire:copy:0x00010048";

        private static readonly string[] Found = { "open:4", "find" };
        private static readonly string[] Public = { "metadata:match", "has-provider-info:match" };
        private static readonly string[] Copied =
        {
            "size-query:match", "buffer", "data-read:match", "copy:match", "set:copy:silent", "free-buffer", "dispose:match", "close",
        };

        private readonly TestCertificateAuthority _authority = new();

        public void Dispose() => _authority.Dispose();

        [Theory]
        [InlineData("0123456789abcdef0123456789ABCDEF01234567", "0123456789ABCDEF0123456789ABCDEF01234567")]
        [InlineData("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF", "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF")]
        public void Thumbprint_IsFortyHexadecimalDigits_KeptInUpperCase(string text, string thumbprint)
        {
            Assert.True(ReplayClientCertificate.TryParse(text, out ReplayClientCertificate certificate));
            Assert.Equal(thumbprint, certificate.Thumbprint);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("0123456789ABCDEF0123456789ABCDEF0123456")]
        [InlineData("0123456789ABCDEF0123456789ABCDEF012345678")]
        [InlineData("01 23 45 67 89 AB CD EF 01 23 45 67 89 AB CD EF 01 23 45 67")]
        [InlineData("01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67")]
        [InlineData("\u200E0123456789ABCDEF0123456789ABCDEF0123456")]
        [InlineData("0123456789ABCDEF0123456789ABCDEF0123456G")]
        [InlineData("０123456789ABCDEF0123456789ABCDEF01234567")]
        public void Thumbprint_RefusesAnythingElse(string text)
        {
            Assert.False(ReplayClientCertificate.TryParse(text, out ReplayClientCertificate certificate));
            Assert.Null(certificate);
        }

        [Theory]
        [InlineData("https://example.test", "https://example.test:443")]
        [InlineData("https://Example.TEST:8443/", "https://example.test:8443")]
        [InlineData("HTTPS://example.test", "https://example.test:443")]
        [InlineData("https://example.test:443", "https://example.test:443")]
        [InlineData("https://bücher.example", "https://xn--bcher-kva.example:443")]
        [InlineData("https://xn--bcher-kva.example:8443", "https://xn--bcher-kva.example:8443")]
        [InlineData("https://a.test.", "https://a.test.:443")]
        [InlineData("https://localhost:1", "https://localhost:1")]
        [InlineData("https://127.0.0.1:65535", "https://127.0.0.1:65535")]
        [InlineData("https://[::1]", "https://[::1]:443")]
        [InlineData("https://[2001:DB8::1]:8443/", "https://[2001:db8::1]:8443")]
        [InlineData("https://a.0xg", "https://a.0xg:443")]
        [InlineData("https://a.0x1g", "https://a.0x1g:443")]
        [InlineData("https://a.1x", "https://a.1x:443")]
        [InlineData("https://0x.example", "https://0x.example:443")]
        public void CredentialOrigin_AcceptsOneHttpsOrigin_InCanonicalForm(string text, string canonical)
        {
            Assert.True(ReplayCredentialOrigin.TryParse(text, out ReplayCredentialOrigin origin));
            Assert.Equal(canonical, origin.ToString());
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("example.test")]
        [InlineData("http://example.test")]
        [InlineData("ftp://example.test")]
        [InlineData("https://")]
        [InlineData("https:///")]
        [InlineData("https://user@example.test")]
        [InlineData("https://user:password@example.test")]
        [InlineData("https://example.test/path")]
        [InlineData("https://example.test/?q=1")]
        [InlineData("https://example.test?q=1")]
        [InlineData("https://example.test#fragment")]
        [InlineData("https://example.test//")]
        [InlineData("https://example.test\\")]
        [InlineData("https://exa mple.test")]
        [InlineData(" https://example.test")]
        [InlineData("https://example.test:")]
        [InlineData("https://example.test:0")]
        [InlineData("https://example.test:0443")]
        [InlineData("https://example.test:65536")]
        [InlineData("https://example.test:443:1")]
        [InlineData("https://a..test")]
        [InlineData("https://.test")]
        [InlineData("https://a.test..")]
        [InlineData("https://127.1")]
        [InlineData("https://0x7f.0.0.1")]
        [InlineData("https://2130706433")]
        [InlineData("https://1.2.3.04")]
        [InlineData("https://127.0.0.1.")]
        [InlineData("https://a.123")]
        [InlineData("https://a.0x1f")]
        [InlineData("https://a.0x")]
        [InlineData("https://127\u30021")]
        [InlineData("https://a.\uFF11\uFF12")]
        [InlineData("https://a.1\u00AD")]
        [InlineData("https://[fe80::1%253]")]
        [InlineData("https://[fe80::1%3]")]
        [InlineData("https://[::1]x")]
        [InlineData("https://[::1]:")]
        [InlineData("https://[example.test]")]
        public void CredentialOrigin_RefusesAnythingElse(string text)
        {
            Assert.False(ReplayCredentialOrigin.TryParse(text, out ReplayCredentialOrigin origin));
            Assert.Null(origin);
        }

        [Theory]
        [InlineData("https://Example.test/x?y=1", "https://example.test:443")]
        [InlineData("https://bücher.example:8443/x", "https://xn--bcher-kva.example:8443")]
        [InlineData("https://127.1/", "https://127.0.0.1:443")]
        [InlineData("https://[::1]:8443/", "https://[::1]:8443")]
        [InlineData("http://example.test/", null)]
        [InlineData("https://[fe80::1%253]/", null)]
        [InlineData("https://a.1/", null)]
        [InlineData("https://a.0X1F/", null)]
        [InlineData("https://a.0xg/", "https://a.0xg:443")]
        [InlineData("https://127\u30021/", null)]
        [InlineData("https://example\u3002test/", "https://example.test:443")]
        public void CredentialOrigin_OfAUrl_IsWhereItsRequestsGo(string url, string origin)
        {
            Assert.Equal(origin, ReplayCredentialOrigin.Of(new Uri(url))?.ToString());
        }

        // Two spellings of one international host are one origin; a trailing dot or another port is another.
        [Fact]
        public void CredentialOrigins_AreTheSame_WhenTheirAsciiHostsAndPortsAre()
        {
            Assert.Equal(Origin("https://xn--bcher-kva.example"), ReplayCredentialOrigin.Of(new Uri("https://BÜCHER.example:443/")));
            Assert.NotEqual(Origin("https://a.test"), Origin("https://a.test."));
            Assert.NotEqual(Origin("https://a.test"), Origin("https://a.test:8443"));
        }

        // How the requests may reach the server is checked first, in this order, from the options and the requests alone:
        // nothing touches a certificate, and no sender exists.
        [Theory]
        [InlineData(true, true, "Auto", "https://a.test/", null, ReplayOperation.ProxyConnectToCode)]
        [InlineData(false, true, "Auto", "https://a.test/", null, ReplayOperation.ClientCertificateProxyCode)]
        [InlineData(false, false, "Http2", "http://a.test/", null, ReplayOperation.Http2HttpsCode)]
        [InlineData(true, false, "Auto", "http://a.test/", null, ReplayOperation.ConnectToHttpsCode)]
        [InlineData(false, false, "Auto", "http://a.test/", null, ReplayOperation.ClientCertificateHttpsCode)]
        [InlineData(false, false, "Auto", "https://a.test/", "http://b.test/", ReplayOperation.ClientCertificateHttpsCode)]
        [InlineData(false, false, "Auto", "https://[fe80::1%253]/", null, ReplayDestination.ScopedIpv6Code)]
        [InlineData(false, false, "Auto", "https://127\u30021/", null, ReplayOperation.ClientCertificateOriginInvalidCode)]
        [InlineData(false, false, "Auto", "https://a.test/", "https://b.test/", ReplayOperation.ClientCertificateOriginCountCode)]
        [InlineData(false, false, "Auto", "https://a.test/", "https://a.test:8443/", ReplayOperation.ClientCertificateOriginCountCode)]
        [InlineData(false, false, "Auto", "https://a.test/", "https://a.test./", ReplayOperation.ClientCertificateOriginCountCode)]
        [InlineData(false, false, "Auto", "https://bücher.example/a", "https://xn--bcher-kva.example/b", ReplayOperation.ClientCertificatePolicyCode)]
        public async Task HowTheRequestsMayReachTheServer_IsCheckedFirst_InOrder(
            bool connectTo, bool proxy, string version, string first, string second, string code)
        {
            Assert.True(ReplayConnectTo.TryParse("127.0.0.1:443", out ReplayConnectTo endpoint));
            Assert.True(ReplayProxy.TryParse("http://127.0.0.1:3128", out ReplayProxy upstream));
            var native = new RecordingCertificateNative();
            var setups = new List<ReplaySenderSetup>();
            ReplaySource source = second == null
                ? ReplaySource.FromDraft(new RequestReplayDraft { Url = first })
                : ReplaySource.FromCaptures(new[] { Row(1, first), Row(2, second) });

            OperationResult<ReplayResult> result = await Operation(native, setups).ExecuteAsync(
                new ReplayRequest(
                    source,
                    new ReplayOptions(
                        AllowConnectTo: true,
                        AllowProxy: true,
                        Transport: new ReplayTransportOptions(Enum.Parse<ReplayHttpVersion>(version), connectTo ? endpoint : null, proxy ? upstream : null, Thumbprint()))),
                OperationContext.None,
                CancellationToken.None);

            Assert.Equal((OperationOutcome.InvalidInput, code), (result.Outcome, Assert.Single(result.Diagnostics).Code));
            Assert.Empty(native.Calls);
            Assert.Empty(setups);
        }

        // The consent names the one origin that may receive the certificate: without it, or for another origin, nothing is
        // resolved or sent, and the store is never opened.
        [Theory]
        [InlineData(Url, null)]
        [InlineData(Url, "https://xmat-client-cert.test:8443")]
        [InlineData(Url, "https://other.test")]
        [InlineData(Url, "https://xmat-client-cert.test.")]
        [InlineData("https://xmat-client-cert.test./item", "https://xmat-client-cert.test")]
        public async Task WithoutConsentForTheRunsOrigin_NothingIsResolvedOrSent(string url, string consent)
        {
            using X509Certificate2 client = _authority.IssueClient();
            var native = new RecordingCertificateNative(client);
            var setups = new List<ReplaySenderSetup>();

            OperationResult<ReplayResult> result = await Run(
                native, consent == null ? null : Origin(consent), url: url, setups: setups);

            Assert.Equal(
                (OperationOutcome.InvalidInput, ReplayOperation.ClientCertificatePolicyCode),
                (result.Outcome, Assert.Single(result.Diagnostics).Code));
            Assert.Empty(native.Calls);
            Assert.Empty(setups);
        }

        // Consent for the run's origin, spelled either way, is consent: the run goes on to look for the certificate.
        [Theory]
        [InlineData("https://bücher.example/x", "https://xn--bcher-kva.example")]
        [InlineData("https://xn--bcher-kva.example/x", "https://bücher.example:443/")]
        public async Task ConsentForTheRunsOrigin_SpelledEitherWay_IsConsent(string url, string consent)
        {
            var native = new RecordingCertificateNative();

            OperationResult<ReplayResult> result = await Run(native, Origin(consent), url: url);

            Assert.Equal(ReplayCertificateResolver.NotFoundCode, Assert.Single(result.Diagnostics).Code);
            Assert.Equal(Found.Append("close"), native.Calls);
        }

        [Fact]
        public void Preview_SaysThatThePlanUsesAClientCertificate_AndForWhichOrigin()
        {
            var operation = new ReplayOperation();
            ReplaySource source = ReplaySource.FromDraft(new RequestReplayDraft { Url = Url });

            ReplayPlanPreview with = operation.Preview(
                source, new ReplayOptions(Transport: new ReplayTransportOptions(ClientCertificate: Thumbprint()))).Value;
            ReplayPlanPreview without = operation.Preview(source).Value;

            Assert.True(with.UsesClientCertificate);
            Assert.Equal(Origin("https://xmat-client-cert.test"), with.CredentialOrigin);
            Assert.False(without.UsesClientCertificate);
            Assert.Null(without.CredentialOrigin);
        }

        // The whole resolution, in order: the store read-only, the match's public data, the copy with the silent flag set
        // in the run's buffer and set on the copy only, everything else released and the store closed before the key is
        // acquired silently (exactly SILENT | ALLOW_NCRYPT | NO_HEALING), used once silently and freed by the run; then the
        // sender, and the copy disposed after it. The send says that a certificate was configured.
        [Fact]
        public async Task ACertificateThatResolves_IsCopiedAndCheckedSilently_ThenOwnedByTheRunUntilItEnds()
        {
            using X509Certificate2 client = _authority.IssueClient();
            var native = new RecordingCertificateNative(client) { Fake = true, SigningCertificate = client };
            var setups = new List<ReplaySenderSetup>();
            var offered = new List<string>();

            OperationResult<ReplayResult> result = await Run(
                native, Origin("https://xmat-client-cert.test"), client, setups: setups, offered: offered);

            Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
            Assert.Equal(
                Found.Concat(Public).Concat(Copied).Concat(new[]
                {
                    Acquire, "sign-cng:0x00000042", "free-ncrypt", "sender", "sender-disposed", "dispose:copy",
                }),
                native.Calls);
            ReplayItemResult item = Assert.Single(result.Value.Items);
            Assert.True(item.ClientCertificateConfigured);
            Assert.Equal(ReplayTransportOptions.DirectName, item.Transport);
            ReplaySenderSetup setup = Assert.Single(setups);
            Assert.Equal(client.Thumbprint, Assert.Single(offered));
            Assert.Equal(Origin("https://xmat-client-cert.test"), setup.CredentialOrigin);
            Assert.Null(setup.Proxy);
            Assert.Equal(0, native.LiveBuffers);
        }

        // The store: exactly one certificate with the thumbprint; a store that cannot be read is not a missing certificate.
        [Theory]
        [InlineData("none", ReplayCertificateResolver.NotFoundCode, "open:4,find,close")]
        [InlineData("two", ReplayCertificateResolver.AmbiguousCode, "open:4,find,dispose:match,dispose:match2,close")]
        [InlineData("open-throws", ReplayCertificateResolver.StoreUnavailableCode, "open:4")]
        [InlineData("open-fails", ReplayCertificateResolver.StoreUnavailableCode, "open:4")]
        [InlineData("find-throws", ReplayCertificateResolver.StoreUnavailableCode, "open:4,find,close")]
        [InlineData("find-fails", ReplayCertificateResolver.StoreUnavailableCode, "open:4,find,close")]
        public async Task TheStore_GivesExactlyOneCertificate_OrTheRunIsRefused(string store, string code, string calls)
        {
            using X509Certificate2 client = _authority.IssueClient();
            RecordingCertificateNative native = store switch
            {
                "none" => new RecordingCertificateNative(),
                "two" => new RecordingCertificateNative(client, client),
                _ => new RecordingCertificateNative(client),
            };
            if (store.EndsWith("-throws", StringComparison.Ordinal))
            {
                native.Throwing.Add(store[..store.IndexOf('-')]);
            }
            else if (store.EndsWith("-fails", StringComparison.Ordinal))
            {
                native.Failing.Add(store[..store.IndexOf('-')]);
            }

            OperationResult<ReplayResult> result = await Run(native, Origin("https://xmat-client-cert.test"), client);

            Assert.Equal((OperationOutcome.InvalidInput, code), (result.Outcome, Assert.Single(result.Diagnostics).Code));
            Assert.Equal(calls.Split(','), native.Calls);
        }

        // The public data refuses a certificate before its key is touched, and without a provider information lookup
        // when the data alone refuses it.
        [Theory]
        [InlineData("ca", ReplayCertificateResolver.AuthorityCode, false)]
        [InlineData("server-only", ReplayCertificateResolver.NotClientAuthCode, false)]
        [InlineData("key-encipherment-only", ReplayCertificateResolver.KeyUsageCode, false)]
        [InlineData("other-key", ReplayCertificateResolver.UnsupportedKeyCode, false)]
        [InlineData("metadata-throws", ReplayCertificateResolver.MetadataUnavailableCode, false)]
        [InlineData("metadata-fails", ReplayCertificateResolver.MetadataUnavailableCode, false)]
        [InlineData("no-provider-info", ReplayCertificateResolver.NoPrivateKeyCode, true)]
        [InlineData("provider-info-throws", ReplayCertificateResolver.KeyUnavailableCode, true)]
        public async Task ThePublicData_RefusesACertificate_BeforeItsKeyIsTouched(string kind, string code, bool looksUpProviderInfo)
        {
            using X509Certificate2 client = kind switch
            {
                "ca" => _authority.IssueClient(certificateAuthority: true),
                "server-only" => _authority.IssueClient(extendedKeyUsage: TestCertificateAuthority.ServerAuthenticationOid),
                "key-encipherment-only" => _authority.IssueClient(keyUsage: X509KeyUsageFlags.KeyEncipherment),
                _ => _authority.IssueClient(),
            };
            var native = new RecordingCertificateNative(client) { Fake = true, SigningCertificate = client };
            switch (kind)
            {
                case "other-key":
                    native.Metadata = certificate =>
                        ReplayCertificateNative.Instance.ReadMetadata(certificate) with { KeyAlgorithm = ReplayKeyAlgorithm.Other };
                    break;
                case "metadata-throws":
                    native.Throwing.Add("metadata");
                    break;
                case "metadata-fails":
                    native.Failing.Add("metadata");
                    break;
                case "no-provider-info":
                    native.ProviderInfo = false;
                    break;
                case "provider-info-throws":
                    native.Throwing.Add("has-provider-info");
                    break;
            }

            OperationResult<ReplayResult> result = await Run(native, Origin("https://xmat-client-cert.test"), client);

            Assert.Equal((OperationOutcome.InvalidInput, code), (result.Outcome, Assert.Single(result.Diagnostics).Code));
            Assert.Equal(
                Found.Concat(looksUpProviderInfo ? Public : Public.Take(1)).Concat(new[] { "dispose:match", "close" }),
                native.Calls);
        }

        // Without the extensions, or with any purpose, a certificate's public data allows client authentication.
        [Theory]
        [InlineData(null, null)]
        [InlineData("2.5.29.37.0", X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment)]
        public void PublicData_WithoutTheExtensionsOrForAnyPurpose_AllowsClientAuthentication(string usage, X509KeyUsageFlags? keyUsage)
        {
            using X509Certificate2 client = _authority.IssueClient(extendedKeyUsage: usage, keyUsage: keyUsage);

            ReplayCertificateMetadata metadata = ReplayCertificateNative.Instance.ReadMetadata(client);

            Assert.Null(ReplayCertificateResolver.Refusal(metadata));
            Assert.Equal((client.Thumbprint, client.Subject, ReplayKeyAlgorithm.Rsa), (metadata.Thumbprint, metadata.Subject, metadata.KeyAlgorithm));
        }

        // Each call of the copy runs only after the one before it succeeded; at the first that fails, nothing after it
        // runs, only what exists is released, newest first, the store is closed, and no key is touched.
        [Theory]
        [InlineData("size-query", false, "size-query:match")]
        [InlineData("size-too-small", false, "size-query:match")]
        [InlineData("buffer", false, "size-query:match,buffer")]
        [InlineData("data-read", false, "size-query:match,buffer,data-read:match,free-buffer")]
        [InlineData("read-too-small", false, "size-query:match,buffer,data-read:match,free-buffer")]
        [InlineData("read-too-large", false, "size-query:match,buffer,data-read:match,free-buffer")]
        [InlineData("copy", false, "size-query:match,buffer,data-read:match,copy:match,free-buffer")]
        [InlineData("copy", true, "size-query:match,buffer,data-read:match,copy:match,free-buffer")]
        [InlineData("set", false, "size-query:match,buffer,data-read:match,copy:match,set:copy:silent,dispose:copy,free-buffer")]
        [InlineData("set", true, "size-query:match,buffer,data-read:match,copy:match,set:copy:silent,dispose:copy,free-buffer")]
        public async Task TheCopy_StopsAtTheFailingCall_AndReleasesOnlyWhatExists(string call, bool throws, string calls)
        {
            using X509Certificate2 client = _authority.IssueClient();
            var native = new RecordingCertificateNative(client) { Fake = true, SigningCertificate = client };
            uint structure = (uint)Marshal.SizeOf<ReplayKeyProviderInfo>();
            if (call == "size-too-small")
            {
                native.ProviderInfoSize = structure - 1;
            }
            else if (call == "read-too-small")
            {
                native.ProviderInfoReadSize = structure - 1;
            }
            else if (call == "read-too-large")
            {
                native.ProviderInfoReadSize = structure + 65;
            }
            else if (throws)
            {
                native.Throwing.Add(call);
            }
            else
            {
                native.Failing.Add(call);
            }

            OperationResult<ReplayResult> result = await Run(native, Origin("https://xmat-client-cert.test"), client);

            Assert.Equal(
                (OperationOutcome.InvalidInput, ReplayCertificateResolver.KeyUnavailableCode),
                (result.Outcome, Assert.Single(result.Diagnostics).Code));
            Assert.Equal(
                Found.Concat(Public).Concat(calls.Split(',')).Concat(new[] { "dispose:match", "close" }),
                native.Calls);
            Assert.Equal(0, native.LiveBuffers);
        }

        // The key is acquired from the copy with exactly SILENT | ALLOW_NCRYPT | NO_HEALING, signs silently (CNG: with
        // NCRYPT_SILENT_FLAG, and PKCS#1 for RSA), and is freed once, by the function that its kind needs, only when the
        // run owns it.
        [Theory]
        [InlineData("Rsa", ReplayCertificateResolver.CngKeySpec, true, "sign-cng:0x00000042,free-ncrypt")]
        [InlineData("Ecdsa", ReplayCertificateResolver.CngKeySpec, true, "sign-cng:0x00000040,free-ncrypt")]
        [InlineData("Rsa", 1u, true, "sign-capi:1,release-capi")]
        [InlineData("Rsa", 2u, true, "sign-capi:2,release-capi")]
        [InlineData("Rsa", ReplayCertificateResolver.CngKeySpec, false, "sign-cng:0x00000042")]
        [InlineData("Rsa", 1u, false, "sign-capi:1")]
        public async Task TheKey_IsAcquiredAndUsedSilently_AndFreedOnceByItsOwner(
            string keyKind, uint keySpec, bool callerFrees, string calls)
        {
            TestCertificateAuthority.ClientKey key = Enum.Parse<TestCertificateAuthority.ClientKey>(keyKind);
            using X509Certificate2 client = _authority.IssueClient(key: key);
            var native = new RecordingCertificateNative(client)
            {
                Fake = true,
                SigningCertificate = client,
                KeySpec = keySpec,
                CallerFrees = callerFrees,
            };

            OperationResult<ReplayResult> result = await Run(native, Origin("https://xmat-client-cert.test"), client);

            Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
            Assert.Equal(
                Found.Concat(Public).Concat(Copied).Append(Acquire).Concat(calls.Split(','))
                    .Concat(new[] { "sender", "sender-disposed", "dispose:copy" }),
                native.Calls);
        }

        // A key that cannot be used silently, or whose signature does not verify, refuses the run once the key, if it was
        // acquired, was freed, and the copy disposed: nothing is sent, and no sender exists.
        [Theory]
        [InlineData("acquire-fails", "Rsa", ReplayCertificateResolver.CngKeySpec, "")]
        [InlineData("acquire-throws", "Rsa", ReplayCertificateResolver.CngKeySpec, "")]
        [InlineData("sign-cng-fails", "Rsa", ReplayCertificateResolver.CngKeySpec, "sign-cng:0x00000042,free-ncrypt")]
        [InlineData("sign-cng-throws", "Rsa", ReplayCertificateResolver.CngKeySpec, "sign-cng:0x00000042,free-ncrypt")]
        [InlineData("bad-signature", "Rsa", ReplayCertificateResolver.CngKeySpec, "sign-cng:0x00000042,free-ncrypt")]
        [InlineData("bad-signature", "Ecdsa", ReplayCertificateResolver.CngKeySpec, "sign-cng:0x00000040,free-ncrypt")]
        [InlineData("sign-capi-fails", "Rsa", 1u, "sign-capi:1,release-capi")]
        [InlineData("ecdsa-on-capi", "Ecdsa", 2u, "release-capi")]
        public async Task AKeyThatCannotBeUsedSilently_RefusesTheRun_AfterItIsFreed(
            string failure, string keyKind, uint keySpec, string calls)
        {
            TestCertificateAuthority.ClientKey key = Enum.Parse<TestCertificateAuthority.ClientKey>(keyKind);
            using X509Certificate2 client = _authority.IssueClient(key: key);
            var native = new RecordingCertificateNative(client) { Fake = true, SigningCertificate = client, KeySpec = keySpec };
            switch (failure)
            {
                case "bad-signature":
                    native.Signature = RandomNumberGenerator.GetBytes(key == TestCertificateAuthority.ClientKey.Rsa ? 256 : 64);
                    break;
                case "ecdsa-on-capi":
                    break;
                default:
                    string name = failure[..failure.LastIndexOf('-')];
                    (failure.EndsWith("-throws", StringComparison.Ordinal) ? native.Throwing : native.Failing).Add(name);
                    break;
            }

            OperationResult<ReplayResult> result = await Run(native, Origin("https://xmat-client-cert.test"), client);

            Assert.Equal(
                (OperationOutcome.InvalidInput, ReplayCertificateResolver.KeyUnavailableCode),
                (result.Outcome, Assert.Single(result.Diagnostics).Code));
            Assert.Equal(
                Found.Concat(Public).Concat(Copied).Append(Acquire)
                    .Concat(calls.Split(',', StringSplitOptions.RemoveEmptyEntries)).Append("dispose:copy"),
                native.Calls);
        }

        // Cancellation between the steps, and before the sender exists, is the run's cancellation, never a refusal of the
        // certificate; what exists is released as after a refusal.
        [Theory]
        [InlineData("find", "open:4,find,dispose:match,close")]
        [InlineData("has-provider-info", "open:4,find,metadata:match,has-provider-info:match,dispose:match,close")]
        [InlineData("set", "open:4,find,metadata:match,has-provider-info:match,size-query:match,buffer,data-read:match,copy:match,set:copy:silent,free-buffer,dispose:match,close,dispose:copy")]
        [InlineData("free-ncrypt", "open:4,find,metadata:match,has-provider-info:match,size-query:match,buffer,data-read:match,copy:match,set:copy:silent,free-buffer,dispose:match,close,acquire:copy:0x00010048,sign-cng:0x00000042,free-ncrypt,dispose:copy")]
        public async Task CancellationBetweenTheSteps_StaysCancellation_AndReleasesWhatExists(string call, string calls)
        {
            using X509Certificate2 client = _authority.IssueClient();
            using var cancellation = new CancellationTokenSource();
            var native = new RecordingCertificateNative(client) { Fake = true, SigningCertificate = client };
            native.OnCall[call] = cancellation.Cancel;

            OperationResult<ReplayResult> result = await Run(native, Origin("https://xmat-client-cert.test"), client, cancellation.Token);

            Assert.Equal(OperationOutcome.Cancelled, result.Outcome);
            Assert.Equal(calls.Split(','), native.Calls);
            Assert.Equal(0, native.LiveBuffers);
        }

        // A native call that ends in the run's cancellation is cancellation too, never a certificate that cannot be used,
        // and what exists is still released.
        [Fact]
        public async Task ANativeCallCancelled_IsTheRunsCancellation()
        {
            using X509Certificate2 client = _authority.IssueClient();
            using var cancellation = new CancellationTokenSource();
            var native = new RecordingCertificateNative(client) { Fake = true, SigningCertificate = client };
            native.OnCall["data-read"] = () =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            };

            OperationResult<ReplayResult> result = await Run(native, Origin("https://xmat-client-cert.test"), client, cancellation.Token);

            Assert.Equal(OperationOutcome.Cancelled, result.Outcome);
            Assert.Equal(
                Found.Concat(Public).Concat(new[] { "size-query:match", "buffer", "data-read:match", "free-buffer", "dispose:match", "close" }),
                native.Calls);
            Assert.Equal(0, native.LiveBuffers);
        }

        // Whatever ends the run, a failed send, a failed sink or cancellation while sending, the copy is disposed after the
        // run's sender.
        [Theory]
        [InlineData("send-fails")]
        [InlineData("sink-fails")]
        [InlineData("cancelled")]
        public async Task TheRunsCopy_IsDisposedAfterItsSender_WhateverEndsTheRun(string end)
        {
            using X509Certificate2 client = _authority.IssueClient();
            using var cancellation = new CancellationTokenSource();
            var native = new RecordingCertificateNative(client) { Fake = true, SigningCertificate = client };
            var handler = new AnsweringHandler(native, end switch
            {
                "send-fails" => AnsweringHandler.Mode.Fail,
                "cancelled" => AnsweringHandler.Mode.Hang,
                _ => AnsweringHandler.Mode.Ok,
            });
            if (end == "cancelled")
            {
                _ = handler.Sending.ContinueWith(_ => cancellation.Cancel(), TaskScheduler.Default);
            }

            Task<OperationResult<ReplayResult>> running = Run(
                native,
                Origin("https://xmat-client-cert.test"),
                client,
                cancellation.Token,
                handler: handler,
                sink: end == "sink-fails" ? new FailingSink() : null);

            if (end == "sink-fails")
            {
                await Assert.ThrowsAsync<InvalidDataException>(() => running);
            }
            else
            {
                OperationResult<ReplayResult> result = await running;
                Assert.Equal(end == "cancelled" ? OperationOutcome.Cancelled : OperationOutcome.Succeeded, result.Outcome);
                if (end == "send-fails")
                {
                    Assert.Equal(ReplaySender.SendFailedCode, Assert.Single(result.Value.Items).Code);
                }
            }

            Assert.Equal(new[] { "sender", "sender-disposed", "dispose:copy" }, native.Calls.TakeLast(3));
        }

        // The production calls on real keys, imported from a PFX: an RSA and an ECDSA key in the CNG software provider and
        // an RSA key in a CryptoAPI provider are each acquired silently, sign silently and are freed by their owner. The
        // store's certificate keeps its provider information as it was: the silent flag is only ever set on the copy.
        [Theory]
        [InlineData("Rsa", "sign-cng:0x00000042,free-ncrypt")]
        [InlineData("Ecdsa", "sign-cng:0x00000040,free-ncrypt")]
        [InlineData("CapiRsa", "sign-capi:*,release-capi")]
        public async Task TheProductionCalls_ResolveARealKeySilently_WithoutChangingTheStoresCertificate(
            string keyKind, string calls)
        {
            TestCertificateAuthority.ClientKey key = Enum.Parse<TestCertificateAuthority.ClientKey>(keyKind);
            using X509Certificate2 client = _authority.IssueClient(key: key);
            int flagsBefore = ProviderInfoFlags(client);
            var native = new RecordingCertificateNative(client);

            OperationResult<ReplayResult> result = await Run(native, Origin("https://xmat-client-cert.test"), client);

            Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
            string[] expected = Found.Concat(Public).Concat(Copied).Append(Acquire).Concat(calls.Split(','))
                .Concat(new[] { "sender", "sender-disposed", "dispose:copy" }).ToArray();
            Assert.Equal(expected.Length, native.Calls.Count);
            Assert.All(
                expected.Zip(native.Calls),
                pair => Assert.Matches("^" + string.Join(".*", pair.First.Split('*').Select(Regex.Escape)) + "$", pair.Second));
            Assert.Equal(0, flagsBefore & ReplayCertificateResolver.CryptSilent);
            Assert.Equal(flagsBefore, ProviderInfoFlags(client));
        }

        // A certificate without provider information has no private key recorded on this PC, which the production lookup
        // tells apart from a failure.
        [Fact]
        public async Task ACertificateWithoutItsKey_HasNoPrivateKey()
        {
            using X509Certificate2 client = _authority.IssueClient();
            using X509Certificate2 publicOnly = X509CertificateLoader.LoadCertificate(client.RawData);
            var native = new RecordingCertificateNative(publicOnly);

            OperationResult<ReplayResult> result = await Run(native, Origin("https://xmat-client-cert.test"), publicOnly);

            Assert.Equal(ReplayCertificateResolver.NoPrivateKeyCode, Assert.Single(result.Diagnostics).Code);
            Assert.Equal(Found.Concat(Public).Concat(new[] { "dispose:match", "close" }), native.Calls);
        }

        // The current user's real Personal store, opened read-only: a thumbprint that no certificate has is not found.
        [Fact]
        public void TheRealStore_IsOnlyRead_AndAnUnknownThumbprintIsNotFound()
        {
            Assert.True(ReplayClientCertificate.TryParse(RandomNumberGenerator.GetHexString(40), out ReplayClientCertificate unknown));

            ReplayClientCertificateException refused = Assert.Throws<ReplayClientCertificateException>(() =>
                ReplayCertificateResolver.Resolve(
                    ReplayCertificateNative.Instance, unknown, Origin("https://xmat-client-cert.test"), CancellationToken.None));

            Assert.Equal(ReplayCertificateResolver.NotFoundCode, refused.Code);
        }

        // The GUI's list reads the store read-only and each certificate's public data, never a key: it lists only the
        // certificates that the resolution's public-data step would accept, expired ones included, by subject, and
        // releases every certificate and the store.
        [Fact]
        public void TheCatalog_ListsWhatResolutionWouldConsider_FromPublicDataAlone()
        {
            using X509Certificate2 current = _authority.IssueClient("XMAT test client A");
            using X509Certificate2 expired = _authority.IssueClient(
                "XMAT test client B", notBefore: DateTimeOffset.UtcNow.AddDays(-2), notAfter: DateTimeOffset.UtcNow.AddDays(-1));
            using X509Certificate2 authority = _authority.IssueClient("XMAT test client C", certificateAuthority: true);
            using X509Certificate2 serverOnly = _authority.IssueClient(
                "XMAT test client D", extendedKeyUsage: TestCertificateAuthority.ServerAuthenticationOid);
            using X509Certificate2 withKey = _authority.IssueClient("XMAT test client E");
            using X509Certificate2 publicOnly = X509CertificateLoader.LoadCertificate(withKey.RawData);
            var native = new RecordingCertificateNative(expired, publicOnly, serverOnly, current, authority);

            IReadOnlyList<ReplayCertificateListing> listed = ReplayCertificateCatalog.List(native, CancellationToken.None);

            Assert.Equal(new[] { current.Thumbprint, expired.Thumbprint }, listed.Select(listing => listing.Thumbprint));
            Assert.Equal("CN=XMAT test client B", listed[1].Subject);
            Assert.Equal(new DateTimeOffset(expired.NotAfter.ToUniversalTime(), TimeSpan.Zero), listed[1].NotAfter);
            Assert.Equal(
                new[]
                {
                    "open:4", "list",
                    "metadata:listed1", "has-provider-info:listed1", "dispose:listed1",
                    "metadata:listed2", "has-provider-info:listed2", "dispose:listed2",
                    "metadata:listed3", "dispose:listed3",
                    "metadata:listed4", "has-provider-info:listed4", "dispose:listed4",
                    "metadata:listed5", "dispose:listed5",
                    "close",
                },
                native.Calls);
        }

        [Fact]
        public void TheCatalog_IsEmpty_WhenTheStoreCannotBeRead()
        {
            using X509Certificate2 client = _authority.IssueClient();
            var native = new RecordingCertificateNative(client);
            native.Throwing.Add("open");

            Assert.Empty(ReplayCertificateCatalog.List(native, CancellationToken.None));
            Assert.Equal(new[] { "open:4" }, native.Calls);
        }

        // offered gets the thumbprint of the certificate that each sender was made for, while the run still owns it.
        private static ReplayOperation Operation(
            RecordingCertificateNative native, List<ReplaySenderSetup> setups = null, HttpMessageHandler handler = null, List<string> offered = null) =>
            new()
            {
                CertificateNative = native,
                SenderFactory = () => throw new InvalidOperationException("A run with a client certificate has a sender of its own."),
                OwnSenderFactory = setup =>
                {
                    setups?.Add(setup);
                    offered?.Add(setup.ClientCertificate?.Thumbprint);
                    native.Note("sender");
                    return new ReplaySender(new HttpMessageInvoker(handler ?? new AnsweringHandler(native)), ownsInvoker: true, setup);
                },
            };

        private static Task<OperationResult<ReplayResult>> Run(
            RecordingCertificateNative native,
            ReplayCredentialOrigin consent,
            X509Certificate2 certificate = null,
            CancellationToken cancellationToken = default,
            string url = Url,
            List<ReplaySenderSetup> setups = null,
            HttpMessageHandler handler = null,
            IReplayExchangeSink sink = null,
            List<string> offered = null) =>
            Operation(native, setups, handler, offered).ExecuteAsync(
                new ReplayRequest(
                    ReplaySource.FromDraft(new RequestReplayDraft { Url = url }),
                    new ReplayOptions(
                        Transport: new ReplayTransportOptions(ClientCertificate: Thumbprint(certificate)),
                        AllowClientCertificate: consent),
                    Sink: sink),
                OperationContext.None,
                cancellationToken);

        private static ReplayClientCertificate Thumbprint(X509Certificate2 certificate = null)
        {
            Assert.True(ReplayClientCertificate.TryParse(certificate?.Thumbprint ?? new string('0', 40), out ReplayClientCertificate parsed));
            return parsed;
        }

        private static ReplayCredentialOrigin Origin(string text)
        {
            Assert.True(ReplayCredentialOrigin.TryParse(text, out ReplayCredentialOrigin origin));
            return origin;
        }

        // CRYPT_KEY_PROV_INFO.dwFlags of a certificate's context, read with the production calls.
        private static int ProviderInfoFlags(X509Certificate2 certificate)
        {
            Assert.True(ReplayCertificateNative.Instance.QueryProviderInfoSize(certificate, out uint size));
            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                Assert.True(ReplayCertificateNative.Instance.ReadProviderInfo(certificate, buffer, ref size));
                return Marshal.ReadInt32(buffer, ReplayCertificateResolver.ProviderInfoFlagsOffset);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static ProxyConnectionModel Row(long number, string url)
        {
            var uri = new Uri(url);
            return new ProxyConnectionModel(
                number, "200", DateTime.UtcNow, TimeSpan.Zero, uri.Scheme, uri.Authority, "GET", uri.PathAndQuery,
                $"GET {uri.PathAndQuery} HTTP/1.1\r\nHost: {uri.Authority}\r\n", [], "HTTP/1.1 200 OK\r\n", [], "127.0.0.1");
        }

        // Answers 200 without any network, waits until the send is cancelled, or fails it; records its disposal.
        private sealed class AnsweringHandler(RecordingCertificateNative native, AnsweringHandler.Mode mode = AnsweringHandler.Mode.Ok)
            : HttpMessageHandler
        {
            private readonly TaskCompletionSource _sending = new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal enum Mode
            {
                Ok,
                Hang,
                Fail,
            }

            internal Task Sending => _sending.Task;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                _sending.TrySetResult();
                if (mode == Mode.Hang)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                if (mode == Mode.Fail)
                {
                    throw new HttpRequestException("A test failed the send.");
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("ok"u8.ToArray()) };
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    native.Note("sender-disposed");
                }

                base.Dispose(disposing);
            }
        }

        private sealed class FailingSink : IReplayExchangeSink
        {
            public void Add(ReplayItemResult result, ReplayExchange exchange) => throw new InvalidDataException("A test failed the sink.");
        }
    }
}
