// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using XMAT.WebServiceCapture;

namespace XMAT.Operations.Replay
{
    // How one request is sent: how long to wait, how much of the response to keep, how many redirects to follow and which
    // HTTP version to ask for.
    internal sealed record ReplaySendOptions(
        TimeSpan Timeout,
        long MaxResponseBytes,
        int MaxRedirects = 0,
        ReplayHttpVersion HttpVersion = ReplayHttpVersion.Auto)
    {
        internal const long DefaultMaxResponseBytes = 16L * 1024 * 1024;

        internal static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(30);

        internal static ReplaySendOptions Default { get; } = new(DefaultTimeout, DefaultMaxResponseBytes);
    }

    // What a sender's handler is made for (ReplaySender.CreateHandler): directly to the URL's host, to one connect-to
    // endpoint, or through one proxy, never both of the last two; and, for a run that may offer a client certificate, the
    // resolved certificate (ReplayClientCredential's copy) and the one origin that may receive it, never with a proxy.
    // ChainPolicyFactory is null except in tests: it gives the handler and its client-certificate check each a new chain
    // policy, so they trust the same roots; null keeps the platform's validation.
    internal sealed record ReplaySenderSetup(
        ReplayConnectTo ConnectTo = null,
        ReplayProxy Proxy = null,
        X509Certificate2 ClientCertificate = null,
        ReplayCredentialOrigin CredentialOrigin = null,
        Func<X509ChainPolicy> ChainPolicyFactory = null)
    {
        internal static ReplaySenderSetup Direct { get; } = new();

        internal static ReplaySenderSetup For(
            ReplayTransportOptions transport,
            ReplayClientCredential credential = null,
            Func<X509ChainPolicy> chainPolicyFactory = null) =>
            new(
                (transport ?? throw new ArgumentNullException(nameof(transport))).ConnectTo,
                transport.Proxy,
                credential?.Certificate,
                credential?.Origin,
                chainPolicyFactory);
    }

    // The one way XMAT sends a replayed or composed request, for the GUI and xmat.exe alike. A replay goes through a proxy
    // only when its sender was made for one (so Local PC capture does not record it otherwise), never sends or keeps
    // cookies of its own, never answers an authentication challenge with the user's Windows credentials or a proxy's with
    // any, never decompresses (so inspectors see the bytes the server sent) and keeps the platform's certificate
    // validation. It offers a client certificate only when its sender was made for one, and then only to a server that
    // asks for one and proves, before the certificate is sent, that it is the one origin that may receive it. Replay
    // follows redirects itself, at most MaxRedirects of them, and only within the origin it sent to: a server never chooses
    // where the request's headers and body go next, so a hop to another origin, from HTTPS to HTTP or to anything but HTTP
    // and HTTPS is never followed, nor, with a client certificate, a hop to any origin but the one that may receive it. A
    // sender made for a connect-to endpoint connects only there, and one made for a proxy sends only through it, for every
    // request and redirect; neither falls back to the URL's host. Each hop is recorded, and a redirect built, from what
    // replay put in the request before the HTTP stack sent it, never from what the stack added while sending.
    internal sealed class ReplaySender : IDisposable
    {
        // Why a redirect was not followed; the redirect response is then the result.
        internal const string RedirectOriginChangeCode = "replay.redirect.origin-change";
        internal const string RedirectInsecureCode = "replay.redirect.insecure";
        internal const string RedirectUnsupportedCode = "replay.redirect.unsupported";
        internal const string RedirectLimitCode = "replay.redirect.limit";

        internal const string SendFailedCode = "replay.send.failed";
        internal const string TimeoutCode = "replay.response.timeout";
        internal const string TooLargeCode = "replay.response.too-large";

        // Why a send through a proxy failed: an HTTP 407 was received (Proxy Authentication Required), which is never
        // answered or retried and never taken for the server's response, wherever it came from; the proxy refused the
        // tunnel to an HTTPS server; or it could not be reached.
        internal const string ProxyAuthRequiredCode = "replay.proxy.auth-required";
        internal const string ProxyTunnelFailedCode = "replay.proxy.tunnel-failed";
        internal const string ProxyUnreachableCode = "replay.proxy.unreachable";

        private readonly HttpMessageInvoker _invoker;
        private readonly bool _ownsInvoker;
        private readonly ReplaySenderSetup _setup;

        internal ReplaySender()
            : this(ReplaySenderSetup.Direct)
        {
        }

        // A sender of its own for its setup, which no other sender shares.
        internal ReplaySender(ReplaySenderSetup setup)
            : this(
                new HttpMessageInvoker(CreateHandler(setup ?? throw new ArgumentNullException(nameof(setup))), disposeHandler: true),
                ownsInvoker: true,
                setup)
        {
        }

        // An injected invoker, such as an HttpClient or one over a test handler, belongs to its caller unless ownsInvoker.
        // setup says what its handler was made for (ReplaySenderSetup.Direct when null), which decides how a proxy's
        // failures are reported.
        internal ReplaySender(HttpMessageInvoker invoker, bool ownsInvoker = false, ReplaySenderSetup setup = null)
        {
            _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
            _ownsInvoker = ownsInvoker;
            _setup = setup ?? ReplaySenderSetup.Direct;
        }

        // Directly, the handler connects to the URL's host; for a connect-to endpoint, only to that endpoint; for a proxy,
        // only to the proxy (FixedProxy), never with proxy credentials, so a 407 is the result. Never the system's or the
        // environment's proxy. Credentials stay unset, so an NTLM or Negotiate challenge is the result, never answered with
        // the user's Windows credentials. With a client certificate, nothing is offered up front (ClientCertificates stays
        // unset) and TLS sessions are never resumed, so every connection makes a full handshake in which a server that
        // asks for a certificate shows its own first: the certificate goes only to one that passes
        // ReplayServerCertificateCheck for the consented origin.
        internal static SocketsHttpHandler CreateHandler(ReplaySenderSetup setup = null)
        {
            setup ??= ReplaySenderSetup.Direct;
            if (setup.ConnectTo != null && setup.Proxy != null)
            {
                throw new ArgumentException("A sender connects to another endpoint or sends through a proxy, not both.", nameof(setup));
            }

            if (setup.ClientCertificate != null && (setup.Proxy != null || setup.CredentialOrigin == null))
            {
                throw new ArgumentException(
                    "A client certificate needs the one origin that may receive it, and never goes through a proxy.", nameof(setup));
            }

            var handler = new SocketsHttpHandler
            {
                UseProxy = setup.Proxy != null,
                UseCookies = false,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
            };
            if (setup.Proxy != null)
            {
                handler.Proxy = new FixedProxy(setup.Proxy);
                handler.DefaultProxyCredentials = null;
            }

            if (setup.ConnectTo != null)
            {
                handler.ConnectCallback = ConnectOnlyTo(setup.ConnectTo);
            }

            if (setup.ChainPolicyFactory != null)
            {
                handler.SslOptions.CertificateChainPolicy = setup.ChainPolicyFactory();
            }

            if (setup.ClientCertificate != null)
            {
                // Without a factory the check builds its chain as SslStream does without a policy: the handler's revocation
                // mode, frozen here, the root excluded, the platform's trust (SslStream.Protocol.cs, VerifyRemoteCertificate).
                SslClientAuthenticationOptions ssl = handler.SslOptions;
                X509RevocationMode revocation = ssl.CertificateRevocationCheckMode;
                var check = new ReplayServerCertificateCheck(
                    setup.CredentialOrigin,
                    setup.ChainPolicyFactory ?? (() => new X509ChainPolicy
                    {
                        RevocationMode = revocation,
                        RevocationFlag = X509RevocationFlag.ExcludeRoot,
                    }));
                X509Certificate2 certificate = setup.ClientCertificate;
                ssl.AllowTlsResume = false;
                ssl.LocalCertificateSelectionCallback =
                    (_, targetHost, _, remoteCertificate, _) => Offer(check, certificate, targetHost, remoteCertificate);
            }

            return handler;
        }

        // The run's certificate when the server passes the check, and only a certificate that has its private key: given
        // one without, SslStream would look for one with the same hash in the certificate stores and use that one's key
        // as the store opens it. Otherwise, and on any failure, none.
        private static X509Certificate Offer(
            ReplayServerCertificateCheck check, X509Certificate2 certificate, string targetHost, X509Certificate remoteCertificate)
        {
            try
            {
                return check.Passes(targetHost, remoteCertificate) && certificate.HasPrivateKey ? certificate : null;
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                return null;
            }
        }

        // A response with any status code, a transport failure, a timeout and an oversized response are all results. Only
        // the caller's cancellation throws (OperationCanceledException); an invalid draft throws as CreateRequestToSend does.
        internal async Task<ReplayExchange> SendAsync(
            RequestReplayDraft draft,
            ReplaySendOptions options,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(draft);
            ArgumentNullException.ThrowIfNull(options);
            HttpRequestMessage request = draft.CreateRequestToSend();
            ApplyVersion(request, options.HttpVersion);

            // The same bytes as the request content: a body from SetBodyBytes is shared, not copied.
            byte[] body = draft.GetBodyBytes();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Timeout);
            DateTime started = DateTime.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            int redirects = 0;
            ReplayMessage sent = null;
            try
            {
                while (true)
                {
                    if (request.Content == null)
                    {
                        body = Array.Empty<byte>();
                    }

                    Hop hop = Hop.Of(request, body);
                    sent = DescribeRequest(hop, AttemptedVersion(hop));
                    using HttpResponseMessage response =
                        await _invoker.SendAsync(request, timeout.Token).ConfigureAwait(false);
                    sent = DescribeRequest(hop, response.Version);

                    // A 407 on a run through a proxy asks for proxy authentication, which XMAT never gives, whichever hop it
                    // answers and whether the proxy or a server inside its tunnel sent it: it is never the server's response.
                    if (_setup.Proxy != null && response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                    {
                        return Failure(
                            ReplayOutcome.Failed, ProxyAuthRequiredCode, sent, hop.Uri, started, redirects, stopwatch, error: null);
                    }

                    string stopCode = null;
                    if (options.MaxRedirects > 0 && IsRedirect(response))
                    {
                        Uri location = ResolveLocation(hop.Uri, response.Headers.NonValidated);
                        stopCode = CheckRedirect(hop.Uri, location);
                        if (stopCode == null && !MayReceiveCredentials(location))
                        {
                            stopCode = RedirectOriginChangeCode;
                        }

                        if (stopCode == null && redirects >= options.MaxRedirects)
                        {
                            stopCode = RedirectLimitCode;
                        }

                        if (stopCode == null)
                        {
                            HttpRequestMessage next = CreateRedirect(hop, response.StatusCode, location);
                            request.Dispose();
                            request = next;
                            redirects++;
                            continue;
                        }
                    }

                    // HEAD, 1xx, 204 and 304 responses have no body, whatever their Content-Length says.
                    bool hasBody = hop.Method != HttpMethod.Head && (int)response.StatusCode >= 200 &&
                        response.StatusCode is not (HttpStatusCode.NoContent or HttpStatusCode.NotModified);
                    (byte[] responseBody, bool tooLarge) = hasBody
                        ? await ReadBoundedAsync(response.Content, options.MaxResponseBytes, timeout.Token).ConfigureAwait(false)
                        : (Array.Empty<byte>(), false);
                    return new ReplayExchange
                    {
                        Outcome = tooLarge ? ReplayOutcome.TooLarge : ReplayOutcome.Completed,
                        Code = tooLarge ? TooLargeCode : stopCode,
                        Request = sent,
                        RequestUri = hop.Uri,
                        Started = started,
                        Response = DescribeResponse(response, responseBody),
                        StatusCode = (int)response.StatusCode,
                        ReasonPhrase = response.ReasonPhrase,
                        Version = response.Version,
                        Duration = stopwatch.Elapsed,
                        Redirects = redirects,
                    };
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Failure(ReplayOutcome.TimedOut, TimeoutCode, sent, request.RequestUri, started, redirects, stopwatch, error: null);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                // A 407 is the proxy's answer, never an error of the send: auth-required keeps no exception, whether the
                // 407 answered the request or the CONNECT of its tunnel.
                string code = FailureCode(ex);
                return Failure(
                    ReplayOutcome.Failed, code, sent, request.RequestUri, started, redirects, stopwatch,
                    code == ProxyAuthRequiredCode ? null : ex);
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex) && ex is not OperationCanceledException)
            {
                // Anything else a server can provoke, such as a Location the URL parser refuses, ends this send only.
                return Failure(ReplayOutcome.Failed, SendFailedCode, sent, request.RequestUri, started, redirects, stopwatch, ex);
            }
            finally
            {
                request.Dispose();
            }
        }

        public void Dispose()
        {
            if (_ownsInvoker)
            {
                _invoker.Dispose();
            }
        }

        private static ReplayExchange Failure(
            ReplayOutcome outcome,
            string code,
            ReplayMessage sent,
            Uri requestUri,
            DateTime started,
            int redirects,
            Stopwatch stopwatch,
            Exception error) =>
            new()
            {
                Outcome = outcome,
                Code = code,
                Request = sent,
                RequestUri = requestUri,
                Started = started,
                Duration = stopwatch.Elapsed,
                Redirects = redirects,
                Error = error,
            };

        private static bool IsRedirect(HttpResponseMessage response) =>
            response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

        // Through a proxy, every connection goes to the proxy, so a connection or name failure is the proxy's, and the HTTP
        // stack reports the CONNECT of an HTTPS request that the proxy did not answer with 200 as a tunnel error with that
        // status. Any other failure, and every failure of a direct or connect-to send, is SendFailedCode.
        private string FailureCode(Exception ex)
        {
            if (_setup.Proxy != null && ex is HttpRequestException failure)
            {
                switch (failure.HttpRequestError)
                {
                    case HttpRequestError.ProxyTunnelError:
                        return failure.StatusCode == HttpStatusCode.ProxyAuthenticationRequired
                            ? ProxyAuthRequiredCode
                            : ProxyTunnelFailedCode;
                    case HttpRequestError.ConnectionError:
                    case HttpRequestError.NameResolutionError:
                        return ProxyUnreachableCode;
                }
            }

            return SendFailedCode;
        }

        // An explicit version is asked for exactly: the request fails rather than go out with another. Auto keeps what the
        // request factory chose, HTTP/2 or lower, as replay has always sent. Redirects keep the version of the request.
        private static void ApplyVersion(HttpRequestMessage request, ReplayHttpVersion version)
        {
            if (version == ReplayHttpVersion.Auto)
            {
                return;
            }

            request.Version = version == ReplayHttpVersion.Http2 ? HttpVersion.Version20 : HttpVersion.Version11;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }

        // Connects every connection of the handler to target, whatever the URL's host resolves to, and to nothing else: a
        // failure fails the send. The handler then sets up TLS for the URL's host, so the server name sent and the
        // certificate name checked stay the URL's.
        private static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> ConnectOnlyTo(
            ReplayConnectTo target) =>
            async (_, cancellationToken) =>
            {
                IPEndPoint endpoint = target.ToEndPoint();
                var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };

        // The version a request goes out with before any response says otherwise: cleartext HTTP never upgrades to
        // HTTP/2 when a lower version is allowed, so only HTTPS can start with the requested version.
        private static Version AttemptedVersion(Hop hop) =>
            hop.Uri.Scheme == Uri.UriSchemeHttp && hop.VersionPolicy != HttpVersionPolicy.RequestVersionExact
                ? HttpVersion.Version11
                : hop.Version;

        // The absolute target of the Location header, or null when there is none or it cannot be resolved.
        private static Uri ResolveLocation(Uri current, HttpHeadersNonValidated headers)
        {
            if (!headers.TryGetValues("Location", out HeaderStringValues values) || values.Count != 1)
            {
                return null;
            }

            string location = values.First().Trim();
            return Uri.TryCreate(current, location, out Uri target) && target.IsAbsoluteUri ? target : null;
        }

        // Null when the redirect may be followed; otherwise the code that says why not. Redirect admission requires both the
        // legacy authority comparison and equality of scheme, canonical IDN host and effective port
        // (ReplayAuthority.SameOrigin); failed canonicalization never authorizes a redirect.
        private static string CheckRedirect(Uri from, Uri to)
        {
            if (to == null || (to.Scheme != Uri.UriSchemeHttp && to.Scheme != Uri.UriSchemeHttps) ||
                !string.IsNullOrEmpty(to.UserInfo))
            {
                return RedirectUnsupportedCode;
            }

            if (from.Scheme == Uri.UriSchemeHttps && to.Scheme == Uri.UriSchemeHttp)
            {
                return RedirectInsecureCode;
            }

            return ReplayAuthority.SameOrigin(from, to) ? null : RedirectOriginChangeCode;
        }

        // Credentialed runs additionally require equality with the frozen consented origin: a sender that may offer
        // credentials follows a redirect only to the one origin that may receive them, compared as the consent compares
        // origins (ReplayCredentialOrigin). CheckRedirect already holds each hop to the one before it, and a run's first
        // hop is to that origin; this holds every hop to the origin itself.
        private bool MayReceiveCredentials(Uri location) =>
            _setup.CredentialOrigin == null || _setup.CredentialOrigin.Equals(ReplayCredentialOrigin.Of(location));

        // As browsers and HttpClient do, 303 makes the next request a GET (a HEAD stays a HEAD) without the body, and 301
        // and 302 turn a POST into a GET without the body; otherwise the method and body go to the same-origin target. The
        // next request is built from the hop as replay made it, never from the message that the HTTP stack sent.
        private static HttpRequestMessage CreateRedirect(Hop hop, HttpStatusCode status, Uri location)
        {
            HttpMethod method = hop.Method;
            bool keepBody = true;
            if (status == HttpStatusCode.SeeOther)
            {
                method = method == HttpMethod.Head ? HttpMethod.Head : HttpMethod.Get;
                keepBody = false;
            }
            else if ((status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found) && method == HttpMethod.Post)
            {
                method = HttpMethod.Get;
                keepBody = false;
            }

            // The resolved target as it is: re-parsing its escaped form can fail, and a fragment is never sent.
            var next = new HttpRequestMessage(method, location)
            {
                Version = hop.Version,
                VersionPolicy = hop.VersionPolicy,
            };
            foreach (KeyValuePair<string, string[]> header in hop.Headers)
            {
                next.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (keepBody && hop.ContentHeaders != null)
            {
                next.Content = new ByteArrayContent(hop.Body);
                foreach (KeyValuePair<string, string[]> header in hop.ContentHeaders)
                {
                    if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    {
                        next.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }
            }

            return next;
        }

        // Reads at most maxBytes of the body; a larger body is not kept.
        private static async Task<(byte[] Body, bool TooLarge)> ReadBoundedAsync(
            HttpContent content, long maxBytes, CancellationToken cancellationToken)
        {
            if (content == null)
            {
                return (Array.Empty<byte>(), false);
            }

            if (content.Headers.ContentLength > maxBytes)
            {
                return (Array.Empty<byte>(), true);
            }

            using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    return (Array.Empty<byte>(), true);
                }

                buffer.Write(chunk, 0, read);
            }

            return (buffer.ToArray(), false);
        }

        // The request in HTTP/1.1 form, with the Host and Content-Length values the HTTP stack sends: Host is the URL's
        // authority in ASCII (ReplayAuthority). Only the hop as replay made it is described, never what the stack added.
        private static ReplayMessage DescribeRequest(Hop hop, Version version)
        {
            var headers = new List<KeyValuePair<string, string>> { new("Host", ReplayAuthority.Ascii(hop.Uri)) };
            headers.AddRange(Flatten(hop.Headers));
            var contentHeaders = hop.ContentHeaders == null
                ? new List<KeyValuePair<string, string>>()
                : Flatten(hop.ContentHeaders)
                    .Where(header => !header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            if (hop.Body.Length > 0)
            {
                contentHeaders.Add(new("Content-Length", hop.Body.Length.ToString(CultureInfo.InvariantCulture)));
            }

            return new ReplayMessage(
                $"{hop.Method.Method} {hop.Uri.PathAndQuery} {ReplayMessage.FormatVersion(version)}",
                headers,
                contentHeaders,
                hop.Body);
        }

        // The HTTP stack removes the chunked framing from the body, so Transfer-Encoding no longer describes it.
        private static ReplayMessage DescribeResponse(HttpResponseMessage response, byte[] body) =>
            new(
                $"{ReplayMessage.FormatVersion(response.Version)} {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd(),
                Flatten(response.Headers.NonValidated)
                    .Where(header => !header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                    .ToList(),
                response.Content == null ? null : Flatten(response.Content.Headers.NonValidated),
                body);

        private static List<KeyValuePair<string, string>> Flatten(HttpHeadersNonValidated headers)
        {
            var result = new List<KeyValuePair<string, string>>();
            foreach (KeyValuePair<string, HeaderStringValues> header in headers)
            {
                foreach (string value in header.Value)
                {
                    result.Add(new(header.Key, value));
                }
            }

            return result;
        }

        private static List<KeyValuePair<string, string>> Flatten(IReadOnlyList<KeyValuePair<string, string[]>> headers) =>
            headers.SelectMany(header => header.Value.Select(value => new KeyValuePair<string, string>(header.Key, value))).ToList();

        // One hop as replay made it, taken before the HTTP stack sends it: the stack can add to the message while it
        // sends (an authentication header, for one), and none of that is recorded or sent again on a redirect. Headers
        // keep their order and values; ContentHeaders is null without content; Body is the content's own array, or empty.
        private sealed record Hop(
            HttpMethod Method,
            Uri Uri,
            Version Version,
            HttpVersionPolicy VersionPolicy,
            IReadOnlyList<KeyValuePair<string, string[]>> Headers,
            IReadOnlyList<KeyValuePair<string, string[]>> ContentHeaders,
            byte[] Body)
        {
            internal static Hop Of(HttpRequestMessage request, byte[] body) =>
                new(
                    request.Method,
                    request.RequestUri,
                    request.Version,
                    request.VersionPolicy,
                    Snapshot(request.Headers.NonValidated),
                    request.Content == null ? null : Snapshot(request.Content.Headers.NonValidated),
                    request.Content == null ? Array.Empty<byte>() : body);

            private static List<KeyValuePair<string, string[]>> Snapshot(HttpHeadersNonValidated headers) =>
                headers.Select(header => new KeyValuePair<string, string[]>(header.Key, header.Value.ToArray())).ToList();
        }
    }
}
