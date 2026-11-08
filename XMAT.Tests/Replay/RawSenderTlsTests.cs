// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using XMAT.Operations.Replay;

namespace XMAT.Tests.Replay
{
    // openssl.exe from PATH or from Git for Windows; null when there is none.
    internal static class OpenSsl
    {
        internal const string Missing = "OpenSSL (openssl.exe) was not found on PATH or in Git for Windows.";

        internal static string Executable { get; } = Find();

        private static string Find()
        {
            foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
            {
                string candidate = folder.Length == 0 ? null : Path.Join(folder.Trim('"'), "openssl.exe");
                if (candidate != null && File.Exists(candidate))
                {
                    return candidate;
                }
            }

            string git = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git");
            return new[] { Path.Join(git, "usr", "bin", "openssl.exe"), Path.Join(git, "mingw64", "bin", "openssl.exe") }
                .FirstOrDefault(File.Exists);
        }
    }

    // A test that runs openssl s_server: skipped where OpenSSL is missing, except on CI, where it runs and fails instead.
    public sealed class OpenSslFactAttribute : FactAttribute
    {
        public OpenSslFactAttribute()
        {
            if (OpenSsl.Executable == null && !CurlScripts.OnCi)
            {
                Skip = OpenSsl.Missing;
            }
        }
    }
    // The raw sender over TLS on loopback (slice L, contract 2.6, tests 2.12 item 7): the image's exact bytes over TLS 1.2
    // and 1.3, SNI and the name check, no client certificate, http/1.1 as the only protocol offered, no certificate
    // downloaded from the address that the server's certificate names (AIA), no revocation check even when a seam turns
    // one on, no resumed session, and the handshake's lease, cancellation and deadline.
    public sealed class RawSenderTlsTests
    {
        private const string Ok = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok";
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

        [Theory]
        [InlineData("127.0.0.2", SslProtocols.Tls12)]
        [InlineData("127.0.0.3", SslProtocols.Tls13)]
        public async Task SendsTheImagesExactBytes_OverEachTlsVersion(string address, SslProtocols protocol)
        {
            using var authority = new TestCertificateAuthority();
            using X509Certificate2 certificate = authority.Issue(address: IPAddress.Parse(address));
            using var server = new RawTlsServer(IPAddress.Parse(address), certificate, protocol);
            RawRequestImage image = Image($"https://{address}:{server.Port}", $"POST /p HTTP/1.1\r\nHost: {address}:{server.Port}\r\nContent-Length: 3\r\n\r\nabc");
            server.Expect(image.Length);

            ReplayExchange exchange = await new RawSender(new RawSenderSetup(ChainPolicyFactory: authority.Policy))
                .SendAsync(image, Options(), CancellationToken.None).WaitAsync(Wait);

            RawTlsServer.Served served = await server.NextAsync();
            Assert.Equal((ReplayOutcome.Completed, 200, RawWriteState.Complete), (exchange.Outcome, exchange.StatusCode, exchange.RequestWrite));
            Assert.Equal(image.CopyHead().Concat(image.CopyWireBody()), served.Received);
            Assert.Equal(protocol, served.Protocol);
            Assert.Equal(new Version(1, 1), exchange.Version);
        }

        [Fact]
        public async Task SendsTheTargetsNameAsSni_ChecksTheCertificateAgainstIt_AndOffersOnlyHttp11AndNoClientCertificate()
        {
            using var authority = new TestCertificateAuthority();
            using X509Certificate2 certificate = authority.Issue(dnsName: "xmat-raw.test");
            using var server = new RawTlsServer(
                IPAddress.Parse("127.0.0.4"),
                certificate,
                alpn: new List<SslApplicationProtocol> { SslApplicationProtocol.Http2, SslApplicationProtocol.Http11 },
                requestClientCertificate: true);
            RawRequestImage image = Image($"https://xmat-raw.test:{server.Port}", $"GET / HTTP/1.1\r\nHost: xmat-raw.test:{server.Port}\r\n\r\n");
            server.Expect(image.Length);
            var setup = new RawSenderSetup(ConnectTo: ConnectTo($"127.0.0.4:{server.Port}"), ChainPolicyFactory: authority.Policy);

            ReplayExchange exchange = await new RawSender(setup).SendAsync(image, Options(), CancellationToken.None).WaitAsync(Wait);

            RawTlsServer.Served served = await server.NextAsync();
            Assert.Equal(ReplayOutcome.Completed, exchange.Outcome);
            Assert.Equal("xmat-raw.test", served.ServerName);
            Assert.Equal(SslApplicationProtocol.Http11, served.Alpn);
            Assert.False(served.ClientCertificate);
        }

        [Fact]
        public async Task ACertificateForAnotherName_FailsTheHandshake_WithNothingWritten()
        {
            using var authority = new TestCertificateAuthority();
            using X509Certificate2 certificate = authority.Issue(dnsName: "other.test");
            using var server = new RawTlsServer(IPAddress.Parse("127.0.0.6"), certificate);
            var setup = new RawSenderSetup(ConnectTo: ConnectTo($"127.0.0.6:{server.Port}"), ChainPolicyFactory: authority.Policy);

            ReplayExchange exchange = await new RawSender(setup)
                .SendAsync(Image($"https://xmat-raw.test:{server.Port}", $"GET / HTTP/1.1\r\nHost: xmat-raw.test:{server.Port}\r\n\r\n"), Options(), CancellationToken.None)
                .WaitAsync(Wait);

            Assert.Equal((ReplayOutcome.Failed, ReplaySender.SendFailedCode, RawWriteState.None), (exchange.Outcome, exchange.Code, exchange.RequestWrite));
            Assert.Null(exchange.Error);
        }

        [Fact]
        public async Task AServerThatSpeaksOnlyH2_FailsTheHandshake()
        {
            using var authority = new TestCertificateAuthority();
            using X509Certificate2 certificate = authority.Issue(address: IPAddress.Parse("127.0.0.7"));
            using var server = new RawTlsServer(
                IPAddress.Parse("127.0.0.7"), certificate, alpn: new List<SslApplicationProtocol> { SslApplicationProtocol.Http2 });

            ReplayExchange exchange = await new RawSender(new RawSenderSetup(ChainPolicyFactory: authority.Policy))
                .SendAsync(Image($"https://127.0.0.7:{server.Port}", $"GET / HTTP/1.1\r\nHost: 127.0.0.7:{server.Port}\r\n\r\n"), Options(), CancellationToken.None)
                .WaitAsync(Wait);

            Assert.Equal((ReplayOutcome.Failed, ReplaySender.SendFailedCode, RawWriteState.None), (exchange.Outcome, exchange.Code, exchange.RequestWrite));
        }

        [Fact]
        public async Task ANegotiatedProtocolOtherThanHttp11_IsRefusedBeforeWriting()
        {
            using var authority = new TestCertificateAuthority();
            using X509Certificate2 certificate = authority.Issue(address: IPAddress.Parse("127.0.0.8"));
            using var server = new RawTlsServer(IPAddress.Parse("127.0.0.8"), certificate);
            var setup = new RawSenderSetup(
                ChainPolicyFactory: authority.Policy,
                Hooks: new RawSenderHooks(NegotiatedProtocol: _ => SslApplicationProtocol.Http2));

            ReplayExchange exchange = await new RawSender(setup)
                .SendAsync(Image($"https://127.0.0.8:{server.Port}", $"GET / HTTP/1.1\r\nHost: 127.0.0.8:{server.Port}\r\n\r\n"), Options(), CancellationToken.None)
                .WaitAsync(Wait);

            Assert.Equal((ReplayOutcome.Failed, RawCodes.Alpn, RawWriteState.None), (exchange.Outcome, exchange.Code, exchange.RequestWrite));
            Assert.Empty((await server.NextAsync()).Received);
        }

        // The server is openssl s_server, which sends exactly the certificate that it is given and never completes its
        // chain: Windows' TLS server would fetch the intermediate through the leaf's AIA URL and send it (observed with an
        // OpenSSL client), so no SChannel server can stand for one that does not send it.
        [OpenSslFact]
        public async Task ValidationNeverDownloadsAnIssuerThatTheServersCertificateNames()
        {
            using var authority = new TestCertificateAuthority();
            using X509Certificate2 intermediate = authority.CreateIntermediate();
            using var issuers = new IssuerListener(intermediate);
            (X509Certificate2 leaf, string keyPem) = Leaf(intermediate, IPAddress.Parse("127.0.0.9"), issuerUrl: issuers.Url);
            using (leaf)
            {
                using (OpenSslServer server = await OpenSslServer.StartAsync(IPAddress.Parse("127.0.0.9"), leaf, keyPem))
                {
                    RawRequestImage image = Image($"https://127.0.0.9:{server.Port}", $"GET / HTTP/1.1\r\nHost: 127.0.0.9:{server.Port}\r\n\r\n");

                    // The raw send: validation fails, since the server does not send the intermediate, and nothing is
                    // fetched.
                    ReplayExchange exchange = await new RawSender(new RawSenderSetup(ChainPolicyFactory: authority.Policy))
                        .SendAsync(image, Options(), CancellationToken.None).WaitAsync(Wait);
                    Assert.Equal((ReplaySender.SendFailedCode, RawWriteState.None), (exchange.Code, exchange.RequestWrite));
                    Assert.Equal(0, issuers.Requests);

                    // A seam policy that turns downloads and revocation on is overridden.
                    ReplayExchange overridden = await new RawSender(new RawSenderSetup(ChainPolicyFactory: () =>
                        {
                            X509ChainPolicy policy = authority.Policy();
                            policy.DisableCertificateDownloads = false;
                            policy.RevocationMode = X509RevocationMode.Online;
                            return policy;
                        }))
                        .SendAsync(image, Options(), CancellationToken.None).WaitAsync(Wait);
                    Assert.Equal((ReplaySender.SendFailedCode, RawWriteState.None), (overridden.Code, overridden.RequestWrite));
                    Assert.Equal(0, issuers.Requests);
                }

                // The control, on the same certificates: with downloads on, building the chain contacts the listener and
                // succeeds, so the setup works on this machine and the test cannot pass vacuously.
                using var chain = new X509Chain { ChainPolicy = authority.Policy() };
                chain.ChainPolicy.DisableCertificateDownloads = false;
                using X509Certificate2 publicLeaf = X509CertificateLoader.LoadCertificate(leaf.RawData);
                Assert.True(chain.Build(publicLeaf), string.Join(", ", chain.ChainStatus.Select(status => status.Status)));
                Assert.True(issuers.Requests > 0);
            }
        }

        // A seam policy that turns revocation checking on is overridden (V1, W3): the leaf names a loopback CRL listener,
        // the send completes, and the listener receives no request. The control, building the leaf's chain with revocation
        // checking on, contacts the listener, so the test cannot pass vacuously.
        [OpenSslFact]
        public async Task ValidationNeverChecksRevocation_EvenWhenAPolicySeamTurnsItOn()
        {
            using var authority = new TestCertificateAuthority();
            using var revocation = new IssuerListener(authority.Root);
            (X509Certificate2 leaf, string keyPem) = Leaf(authority.Root, IPAddress.Parse("127.0.0.12"), crlUrl: revocation.Url);
            using (leaf)
            {
                using (OpenSslServer server = await OpenSslServer.StartAsync(IPAddress.Parse("127.0.0.12"), leaf, keyPem, "-www"))
                {
                    var setup = new RawSenderSetup(ChainPolicyFactory: () =>
                    {
                        X509ChainPolicy policy = authority.Policy();
                        policy.RevocationMode = X509RevocationMode.Online;
                        return policy;
                    });

                    ReplayExchange exchange = await new RawSender(setup)
                        .SendAsync(Image($"https://127.0.0.12:{server.Port}", $"GET / HTTP/1.1\r\nHost: 127.0.0.12:{server.Port}\r\n\r\n"), Options(), CancellationToken.None)
                        .WaitAsync(Wait);

                    Assert.Equal((ReplayOutcome.Completed, 200), (exchange.Outcome, exchange.StatusCode));
                    Assert.Equal(0, revocation.Requests);
                }

                using var chain = new X509Chain { ChainPolicy = authority.Policy() };
                chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
                using X509Certificate2 publicLeaf = X509CertificateLoader.LoadCertificate(leaf.RawData);
                chain.Build(publicLeaf);
                Assert.True(revocation.Requests > 0);
            }
        }

        // TLS never resumes a session (2.6). openssl s_server -www says, for each connection, whether its session was new or
        // reused: two sends to it are each a full handshake, over TLS 1.2 and over TLS 1.3, each on an address of its own
        // (SChannel offers a name's cached TLS 1.2 session to a TLS 1.3 server of that name). The control, a client that
        // allows resumption, reuses its session on its second connection, so the test cannot pass vacuously.
        [OpenSslFact]
        public async Task EverySend_IsAFullHandshake_NeverAResumedSession()
        {
            using var authority = new TestCertificateAuthority();
            foreach ((string address, string version) in new[] { ("127.0.0.13", "-tls1_2"), ("127.0.0.14", "-tls1_3") })
            {
                (X509Certificate2 leaf, string keyPem) = Leaf(authority.Root, IPAddress.Parse(address));
                using (leaf)
                using (OpenSslServer server = await OpenSslServer.StartAsync(IPAddress.Parse(address), leaf, keyPem, "-www " + version))
                {
                    RawRequestImage image = Image($"https://{address}:{server.Port}", $"GET / HTTP/1.1\r\nHost: {address}:{server.Port}\r\n\r\n");
                    for (int send = 0; send < 2; send++)
                    {
                        ReplayExchange exchange = await new RawSender(new RawSenderSetup(ChainPolicyFactory: authority.Policy))
                            .SendAsync(image, Options(), CancellationToken.None).WaitAsync(Wait);
                        Assert.True(exchange.Outcome == ReplayOutcome.Completed, $"{version} send {send}: {exchange.Code}");
                        Assert.Contains("\nNew, ", Encoding.ASCII.GetString(exchange.Response.Body));
                    }

                    await StatusPageAsync(address, server.Port, authority);
                    Assert.Contains("\nReused, ", await StatusPageAsync(address, server.Port, authority));
                }
            }
        }

        [Fact]
        public async Task TheHandshakesLease_ACancelAtEachStep_LeavesNothingUnowned()
        {
            using var authority = new TestCertificateAuthority();
            using X509Certificate2 certificate = authority.Issue(address: IPAddress.Parse("127.0.0.10"));
            foreach (RawSendPoint step in new[] { RawSendPoint.BeforeReserve, RawSendPoint.AfterReserve, RawSendPoint.AfterInvoke })
            {
                using var server = new RawTlsServer(IPAddress.Parse("127.0.0.10"), certificate);
                using var source = new CancellationTokenSource();
                var setup = new RawSenderSetup(
                    ChainPolicyFactory: authority.Policy,
                    Hooks: new RawSenderHooks(At: (point, operation) =>
                    {
                        if (operation == RawOperation.Tls && point == step)
                        {
                            source.Cancel();
                        }
                    }));

                ReplayExchange exchange = await new RawSender(setup)
                    .SendAsync(Image($"https://127.0.0.10:{server.Port}", $"GET / HTTP/1.1\r\nHost: 127.0.0.10:{server.Port}\r\n\r\n"), Options(), source.Token)
                    .WaitAsync(Wait);

                Assert.True(exchange.Code == RawCodes.SendCancelled, step.ToString());
                Assert.True(exchange.RequestWrite == RawWriteState.None, step.ToString());
            }
        }

        [Fact]
        public async Task ACancellationOrTheDeadline_DuringAPendingHandshake_IsItself()
        {
            using var silent = new TcpListener(IPAddress.Parse("127.0.0.11"), 0);
            silent.Start();
            int port = ((IPEndPoint)silent.LocalEndpoint).Port;
            var accepted = new List<TcpClient>();
            Task accepting = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient client = await silent.AcceptTcpClientAsync();
                    lock (accepted)
                    {
                        accepted.Add(client);
                    }
                }
            });
            try
            {
                RawRequestImage image = Image($"https://127.0.0.11:{port}", $"GET / HTTP/1.1\r\nHost: 127.0.0.11:{port}\r\n\r\n");
                using var source = new CancellationTokenSource();
                Task<ReplayExchange> cancelled = new RawSender().SendAsync(image, Options(), source.Token);
                await WaitForAsync(() => Count(accepted) == 1);
                source.Cancel();
                Assert.Equal(RawCodes.SendCancelled, (await cancelled.WaitAsync(Wait)).Code);

                var clock = new ManualRawClock();
                Task<ReplayExchange> timedOut = new RawSender(new RawSenderSetup(Hooks: new RawSenderHooks(Clock: clock)))
                    .SendAsync(image, Options(), CancellationToken.None);
                await WaitForAsync(() => Count(accepted) == 2);
                clock.Fire(clock.Scheduled[0]);
                ReplayExchange exchange = await timedOut.WaitAsync(Wait);
                Assert.Equal((ReplayOutcome.TimedOut, ReplaySender.TimeoutCode), (exchange.Outcome, exchange.Code));
            }
            finally
            {
                silent.Stop();
                lock (accepted)
                {
                    accepted.ForEach(client => client.Dispose());
                }
            }
        }

        private static int Count(List<TcpClient> accepted)
        {
            lock (accepted)
            {
                return accepted.Count;
            }
        }

        // openssl s_server -www's status page, read over an SslStream that allows resumption, as .NET's default does.
        private static async Task<string> StatusPageAsync(string address, int port, TestCertificateAuthority authority)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Parse(address), port);
            using var tls = new SslStream(client.GetStream());
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = address,
                CertificateChainPolicy = authority.Policy(),
                AllowTlsResume = true,
            }).WaitAsync(Wait);
            await tls.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.0\r\n\r\n"));
            using var page = new MemoryStream();
            await tls.CopyToAsync(page).WaitAsync(Wait);
            return Encoding.ASCII.GetString(page.ToArray());
        }

        private static async Task WaitForAsync(Func<bool> condition)
        {
            for (int attempt = 0; !condition(); attempt++)
            {
                Assert.True(attempt < 3000, "timed out waiting");
                await Task.Delay(10);
            }
        }

        // A server certificate for address signed by issuer, whose AIA extension names issuerUrl and whose CRL distribution
        // point is crlUrl (each left out when null), with its private key as PEM for a server outside .NET.
        internal static (X509Certificate2 Certificate, string KeyPem) Leaf(
            X509Certificate2 issuer, IPAddress address, string issuerUrl = null, string crlUrl = null)
        {
            using RSA key = RSA.Create(2048);
            var request = new CertificateRequest("CN=" + address, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(address);
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid(TestCertificateAuthority.ServerAuthenticationOid) }, false));
            if (issuerUrl != null)
            {
                request.CertificateExtensions.Add(new X509AuthorityInformationAccessExtension(null, new[] { issuerUrl }));
            }

            if (crlUrl != null)
            {
                request.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension(new[] { crlUrl }));
            }

            using X509Certificate2 issued = request.Create(
                issuer, DateTimeOffset.UtcNow.AddMinutes(-30), DateTimeOffset.UtcNow.AddMinutes(30), RandomNumberGenerator.GetBytes(8));
            using X509Certificate2 withKey = issued.CopyWithPrivateKey(key);
            return (
                X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable),
                key.ExportPkcs8PrivateKeyPem());
        }

        private static RawSendOptions Options() => new(TimeSpan.FromSeconds(30), 1024 * 1024);

        private static ReplayConnectTo ConnectTo(string text)
        {
            Assert.True(ReplayConnectTo.TryParse(text, out ReplayConnectTo connectTo));
            return connectTo;
        }

        private static RawRequestImage Image(string target, string request)
        {
            Assert.True(ReplayRawTarget.TryParse(target, out ReplayRawTarget parsed));
            RawImageResult result = RawRequest.FromBytes(parsed, Encoding.ASCII.GetBytes(request), false, CancellationToken.None);
            Assert.True(result.Code == null, result.Code);
            return result.Image;
        }

        // openssl s_server for one test: it sends exactly the certificate that it is given, with no intermediate, and never
        // completes its chain. With -www, it answers each GET with a status page that says whether that connection's
        // session was new or reused. Ready once it accepts a connection; disposing it ends the process and deletes its
        // folder.
        internal sealed class OpenSslServer : IDisposable
        {
            private readonly System.Diagnostics.Process _process;
            private readonly string _folder;

            private OpenSslServer(System.Diagnostics.Process process, string folder, int port)
            {
                _process = process;
                _folder = folder;
                Port = port;
            }

            internal int Port { get; }

            internal static async Task<OpenSslServer> StartAsync(
                IPAddress address, X509Certificate2 certificate, string keyPem, string options = null)
            {
                Assert.True(OpenSsl.Executable != null, OpenSsl.Missing);
                string folder = Path.Combine(Path.GetTempPath(), "xmat-openssl-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                await File.WriteAllTextAsync(Path.Combine(folder, "leaf.pem"), certificate.ExportCertificatePem());
                await File.WriteAllTextAsync(Path.Combine(folder, "key.pem"), keyPem);
                int port;
                var probe = new TcpListener(address, 0);
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                System.Diagnostics.Process process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    OpenSsl.Executable, $"s_server -accept {address}:{port} -cert leaf.pem -key key.pem" + (options == null ? string.Empty : " " + options))
                {
                    WorkingDirectory = folder,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                var server = new OpenSslServer(process, folder, port);
                try
                {
                    // Its output is buffered when it is not a console, so readiness is a connection that it accepts. Both
                    // pipes are drained so that it never blocks on one.
                    _ = process.StandardOutput.ReadToEndAsync();
                    _ = process.StandardError.ReadToEndAsync();
                    for (int attempt = 0; ; attempt++)
                    {
                        Assert.False(process.HasExited, "openssl s_server ended before it accepted a connection.");
                        try
                        {
                            using var ready = new TcpClient();
                            await ready.ConnectAsync(address, port);
                            break;
                        }
                        catch (SocketException) when (attempt < 300)
                        {
                            await Task.Delay(100);
                        }
                    }

                    return server;
                }
                catch
                {
                    server.Dispose();
                    throw;
                }
            }

            public void Dispose()
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill(entireProcessTree: true);
                    }

                    _process.WaitForExit(10_000);
                }
                catch (Exception)
                {
                    // It has already ended.
                }

                _process.Dispose();
                try
                {
                    Directory.Delete(_folder, recursive: true);
                }
                catch (Exception)
                {
                    // The temporary folder is left for the system to clean.
                }
            }
        }

        // A loopback HTTP listener that serves the intermediate certificate at its one URL and counts the requests.
        internal sealed class IssuerListener : IDisposable
        {
            private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
            private readonly byte[] _certificate;
            private int _requests;

            internal IssuerListener(X509Certificate2 certificate)
            {
                _certificate = certificate.RawData;
                _listener.Start();
                Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/xmat-{Guid.NewGuid():N}.cer";
                _ = Task.Run(ServeAsync);
            }

            internal string Url { get; }

            internal int Requests => Volatile.Read(ref _requests);

            public void Dispose() => _listener.Stop();

            private async Task ServeAsync()
            {
                while (true)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync();
                    }
                    catch (Exception)
                    {
                        return;
                    }

                    Interlocked.Increment(ref _requests);
                    using (client)
                    {
                        try
                        {
                            NetworkStream stream = client.GetStream();
                            var head = new byte[8192];
                            int read = 0;
                            while (read < head.Length && !EndsHead(head, read))
                            {
                                int count = await stream.ReadAsync(head.AsMemory(read));
                                if (count == 0)
                                {
                                    break;
                                }

                                read += count;
                            }

                            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                                $"HTTP/1.1 200 OK\r\nContent-Type: application/pkix-cert\r\nContent-Length: {_certificate.Length}\r\nConnection: close\r\n\r\n"));
                            await stream.WriteAsync(_certificate);
                        }
                        catch (Exception)
                        {
                            // The client went away.
                        }
                    }
                }
            }

            private static bool EndsHead(byte[] buffer, int length) => buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) >= 0;
        }

        // A loopback TLS server for one test: it presents only its certificate, records what each connection's handshake
        // negotiated and the request bytes it read, and answers with a fixed response.
        private sealed class RawTlsServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly X509Certificate2 _certificate;
            private readonly SslProtocols _protocols;
            private readonly List<SslApplicationProtocol> _alpn;
            private readonly bool _requestClientCertificate;
            private readonly System.Threading.Channels.Channel<Served> _served = System.Threading.Channels.Channel.CreateUnbounded<Served>();
            private int _expected;

            internal RawTlsServer(
                IPAddress address,
                X509Certificate2 certificate,
                SslProtocols protocols = SslProtocols.None,
                List<SslApplicationProtocol> alpn = null,
                bool requestClientCertificate = false)
            {
                _certificate = certificate;
                _protocols = protocols;
                _alpn = alpn;
                _requestClientCertificate = requestClientCertificate;
                _listener = new TcpListener(address, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _ = Task.Run(ServeAsync);
            }

            internal int Port { get; }

            internal void Expect(int bytes) => Volatile.Write(ref _expected, bytes);

            internal async Task<Served> NextAsync() => await _served.Reader.ReadAsync().AsTask().WaitAsync(Wait);

            public void Dispose() => _listener.Stop();

            private async Task ServeAsync()
            {
                while (true)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync();
                    }
                    catch (Exception)
                    {
                        return;
                    }

                    _ = Task.Run(() => ServeOneAsync(client));
                }
            }

            private async Task ServeOneAsync(TcpClient client)
            {
                using (client)
                using (var tls = new SslStream(client.GetStream()))
                {
                    var options = new SslServerAuthenticationOptions
                    {
                        ServerCertificateContext = SslStreamCertificateContext.Create(_certificate, null, offline: true),
                        EnabledSslProtocols = _protocols,
                        ApplicationProtocols = _alpn,
                        ClientCertificateRequired = _requestClientCertificate,
                        RemoteCertificateValidationCallback = (_, _, _, _) => true,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    };
                    try
                    {
                        await tls.AuthenticateAsServerAsync(options);
                    }
                    catch (Exception)
                    {
                        _served.Writer.TryWrite(new Served(Array.Empty<byte>(), null, SslProtocols.None, default, false, true));
                        return;
                    }

                    var received = new MemoryStream();
                    try
                    {
                        var buffer = new byte[16 * 1024];
                        while (received.Length < Volatile.Read(ref _expected))
                        {
                            int read = await tls.ReadAsync(buffer);
                            if (read == 0)
                            {
                                break;
                            }

                            received.Write(buffer, 0, read);
                        }

                        if (received.Length > 0)
                        {
                            await tls.WriteAsync(Encoding.ASCII.GetBytes(Ok));
                        }
                    }
                    catch (Exception)
                    {
                        // The client closed the connection.
                    }

                    _served.Writer.TryWrite(new Served(
                        received.ToArray(), tls.TargetHostName, tls.SslProtocol, tls.NegotiatedApplicationProtocol,
                        tls.RemoteCertificate != null, false));
                }
            }

            internal sealed record Served(
                byte[] Received,
                string ServerName,
                SslProtocols Protocol,
                SslApplicationProtocol Alpn,
                bool ClientCertificate,
                bool HandshakeFailed);
        }
    }
}
