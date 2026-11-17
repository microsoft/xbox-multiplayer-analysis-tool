// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace XMAT.Operations.Replay
{
    // Whether the server of a TLS handshake may receive the run's client certificate: decided while the handshake runs,
    // before the certificate is sent, from the certificate that the server presented. It passes only when the TLS target
    // host, normalized as SslStream normalizes it, is the consented origin's host, and the server's certificate would pass
    // SslStream's own Windows validation for that name: the chain built with the run's trust (a fresh policy each time,
    // with server authentication required when the policy names no application policy), then the native SSL policy
    // (CERT_CHAIN_POLICY_SSL, AUTHTYPE_SERVER, every error ignored but the name's), whose status must be 0. Anything else,
    // an exception included, withholds the certificate. Stricter than SslStream where it would use an empty or unmapped
    // name, or ignore a policy status other than the name's.
    internal sealed class ReplayServerCertificateCheck
    {
        private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";
        private const uint AuthTypeServer = 2;

        // CERT_CHAIN_POLICY_IGNORE_ALL_FLAG without CERT_CHAIN_POLICY_IGNORE_INVALID_NAME_FLAG, as SslStream passes it.
        private const uint IgnoreAllButInvalidName = 0xFFF & ~0x40u;

        private static readonly IntPtr SslPolicyOid = new(4);
        private static readonly IdnMapping Idn = new();

        private readonly string _host;
        private readonly Func<X509ChainPolicy> _policyFactory;
        private readonly Func<X509Chain, string, (bool Called, uint Status)> _sslPolicy;

        // policyFactory gives a new policy for every check, never one that the handler uses; sslPolicy replaces the native
        // policy call in tests.
        internal ReplayServerCertificateCheck(
            ReplayCredentialOrigin origin,
            Func<X509ChainPolicy> policyFactory,
            Func<X509Chain, string, (bool Called, uint Status)> sslPolicy = null)
        {
            ArgumentNullException.ThrowIfNull(origin);
            _host = NormalizeHostName(origin.Host);
            _policyFactory = policyFactory ?? throw new ArgumentNullException(nameof(policyFactory));
            _sslPolicy = sslPolicy ?? NativeSslPolicy;
        }

        internal bool Passes(string targetHost, X509Certificate remoteCertificate)
        {
            X509Chain chain = null;
            try
            {
                if (remoteCertificate is not X509Certificate2 remote)
                {
                    return false;
                }

                string name = NormalizeHostName(targetHost);
                if (name == null || _host == null || !string.Equals(name, _host, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                chain = new X509Chain();
                chain.ChainPolicy = _policyFactory() ?? throw new InvalidOperationException("The chain policy factory returned no policy.");
                if (chain.ChainPolicy.ApplicationPolicy.Count == 0)
                {
                    chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ServerAuthenticationOid));
                }

                if (!chain.Build(remote) || chain.ChainContext == IntPtr.Zero)
                {
                    return false;
                }

                (bool called, uint status) = _sslPolicy(chain, name);
                return called && status == 0;
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                return false;
            }
            finally
            {
                Release(chain);
            }
        }

        // The chain and the certificates that it made, as SslStream releases them after its own validation; a failure to
        // release them changes no verdict.
        private static void Release(X509Chain chain)
        {
            if (chain == null)
            {
                return;
            }

            try
            {
                foreach (X509ChainElement element in chain.ChainElements)
                {
                    element.Certificate.Dispose();
                }

                chain.Dispose();
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                // Released by the finalizer instead.
            }
        }

        // The name that SslStream checks a server's certificate against (TargetHostNameHelper.NormalizeHostName): trailing
        // dots removed, then IdnMapping.GetAscii. Null, where SslStream would use an empty name or keep a name that
        // IdnMapping refuses, so the certificate is withheld.
        internal static string NormalizeHostName(string host)
        {
            if (string.IsNullOrEmpty(host))
            {
                return null;
            }

            host = host.TrimEnd('.');
            try
            {
                return Idn.GetAscii(host);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        // CertificateValidation.Windows.cs: the SSL chain policy for a server, every error ignored but a name that the
        // certificate does not match.
        private static (bool Called, uint Status) NativeSslPolicy(X509Chain chain, string host)
        {
            IntPtr name = IntPtr.Zero;
            IntPtr extra = IntPtr.Zero;
            try
            {
                name = Marshal.StringToHGlobalUni(host);
                extra = Marshal.AllocHGlobal(Marshal.SizeOf<SslExtraPolicyPara>());
                Marshal.StructureToPtr(
                    new SslExtraPolicyPara
                    {
                        Size = (uint)Marshal.SizeOf<SslExtraPolicyPara>(),
                        AuthType = AuthTypeServer,
                        Checks = 0,
                        ServerName = name,
                    },
                    extra,
                    fDeleteOld: false);
                var parameters = new ChainPolicyPara
                {
                    Size = (uint)Marshal.SizeOf<ChainPolicyPara>(),
                    Flags = IgnoreAllButInvalidName,
                    ExtraPolicyPara = extra,
                };
                var status = new ChainPolicyStatus { Size = (uint)Marshal.SizeOf<ChainPolicyStatus>() };
                bool called = NativeMethods.CertVerifyCertificateChainPolicy(
                    SslPolicyOid, chain.ChainContext, ref parameters, ref status);
                GC.KeepAlive(chain);
                return (called, status.Error);
            }
            finally
            {
                Marshal.FreeHGlobal(extra);
                Marshal.FreeHGlobal(name);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SslExtraPolicyPara
        {
            internal uint Size;
            internal uint AuthType;
            internal uint Checks;
            internal IntPtr ServerName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ChainPolicyPara
        {
            internal uint Size;
            internal uint Flags;
            internal IntPtr ExtraPolicyPara;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ChainPolicyStatus
        {
            internal uint Size;
            internal uint Error;
            internal int ChainIndex;
            internal int ElementIndex;
            internal IntPtr ExtraPolicyStatus;
        }

        private static class NativeMethods
        {
            [DllImport("crypt32.dll", SetLastError = true, ExactSpelling = true)]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CertVerifyCertificateChainPolicy(
                IntPtr policyOid, IntPtr chainContext, ref ChainPolicyPara policyPara, ref ChainPolicyStatus policyStatus);
        }
    }
}
