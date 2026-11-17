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
using System.Net.Sockets;
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

    // The one way XMAT sends a replayed or composed request, for the GUI and xmat.exe alike. A replay never goes through a
    // proxy (so Local PC capture does not record it), never sends or keeps cookies of its own, never answers an
    // authentication challenge with the user's Windows credentials, never decompresses (so inspectors see the bytes the
    // server sent) and keeps the platform's certificate validation. Replay follows redirects itself, at most MaxRedirects
    // of them, and only within the origin it sent to: a server never chooses where the request's headers and body go next,
    // so a hop to another origin, from HTTPS to HTTP or to anything but HTTP and HTTPS is never followed. A sender made for
    // a connect-to endpoint connects only there, for every request and redirect, and never falls back to the URL's host.
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

        private readonly HttpMessageInvoker _invoker;
        private readonly bool _ownsInvoker;

        internal ReplaySender()
            : this(new HttpMessageInvoker(CreateHandler(), disposeHandler: true), ownsInvoker: true)
        {
        }

        // A sender of its own for one connect-to endpoint, which no direct send shares.
        internal ReplaySender(ReplayConnectTo connectTo)
            : this(
                new HttpMessageInvoker(
                    CreateHandler(connectTo ?? throw new ArgumentNullException(nameof(connectTo))), disposeHandler: true),
                ownsInvoker: true)
        {
        }

        // An injected invoker, such as an HttpClient or one over a test handler, belongs to its caller unless ownsInvoker.
        internal ReplaySender(HttpMessageInvoker invoker, bool ownsInvoker = false)
        {
            _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
            _ownsInvoker = ownsInvoker;
        }

        // Without connectTo, the handler connects to the URL's host; with it, only to that endpoint. Credentials stay unset,
        // so an NTLM or Negotiate challenge is the result, never answered with the user's Windows credentials.
        internal static SocketsHttpHandler CreateHandler(ReplayConnectTo connectTo = null)
        {
            var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                UseCookies = false,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
            };
            if (connectTo != null)
            {
                handler.ConnectCallback = ConnectOnlyTo(connectTo);
            }

            return handler;
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

                    sent = DescribeRequest(request, body, AttemptedVersion(request));
                    using HttpResponseMessage response =
                        await _invoker.SendAsync(request, timeout.Token).ConfigureAwait(false);
                    sent = DescribeRequest(request, body, response.Version);
                    string stopCode = null;
                    if (options.MaxRedirects > 0 && IsRedirect(response))
                    {
                        Uri location = ResolveLocation(request.RequestUri, response.Headers.NonValidated);
                        stopCode = CheckRedirect(request.RequestUri, location);
                        if (stopCode == null && redirects >= options.MaxRedirects)
                        {
                            stopCode = RedirectLimitCode;
                        }

                        if (stopCode == null)
                        {
                            HttpRequestMessage next = CreateRedirect(request, response.StatusCode, location, body);
                            request.Dispose();
                            request = next;
                            redirects++;
                            continue;
                        }
                    }

                    // HEAD, 1xx, 204 and 304 responses have no body, whatever their Content-Length says.
                    bool hasBody = request.Method != HttpMethod.Head && (int)response.StatusCode >= 200 &&
                        response.StatusCode is not (HttpStatusCode.NoContent or HttpStatusCode.NotModified);
                    (byte[] responseBody, bool tooLarge) = hasBody
                        ? await ReadBoundedAsync(response.Content, options.MaxResponseBytes, timeout.Token).ConfigureAwait(false)
                        : (Array.Empty<byte>(), false);
                    return new ReplayExchange
                    {
                        Outcome = tooLarge ? ReplayOutcome.TooLarge : ReplayOutcome.Completed,
                        Code = tooLarge ? TooLargeCode : stopCode,
                        Request = sent,
                        RequestUri = request.RequestUri,
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
                return Failure(ReplayOutcome.Failed, SendFailedCode, sent, request.RequestUri, started, redirects, stopwatch, ex);
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
        private static Version AttemptedVersion(HttpRequestMessage request) =>
            request.RequestUri.Scheme == Uri.UriSchemeHttp && request.VersionPolicy != HttpVersionPolicy.RequestVersionExact
                ? HttpVersion.Version11
                : request.Version;

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

        // Null when the redirect may be followed; otherwise the code that says why not.
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

            return SameOrigin(from, to) ? null : RedirectOriginChangeCode;
        }

        private static bool SameOrigin(Uri first, Uri second) =>
            string.Equals(
                first.GetLeftPart(UriPartial.Authority),
                second.GetLeftPart(UriPartial.Authority),
                StringComparison.OrdinalIgnoreCase);

        // As browsers and HttpClient do, 303 makes the next request a GET (a HEAD stays a HEAD) without the body, and 301
        // and 302 turn a POST into a GET without the body; otherwise the method and body go to the same-origin target.
        private static HttpRequestMessage CreateRedirect(
            HttpRequestMessage request, HttpStatusCode status, Uri location, byte[] body)
        {
            HttpMethod method = request.Method;
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
                Version = request.Version,
                VersionPolicy = request.VersionPolicy,
            };
            foreach (KeyValuePair<string, HeaderStringValues> header in request.Headers.NonValidated)
            {
                next.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (keepBody && request.Content != null)
            {
                next.Content = new ByteArrayContent(body);
                foreach (KeyValuePair<string, HeaderStringValues> header in request.Content.Headers.NonValidated)
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
        // authority in ASCII (ReplayAuthority).
        private static ReplayMessage DescribeRequest(HttpRequestMessage request, byte[] body, Version version)
        {
            Uri uri = request.RequestUri;
            var headers = new List<KeyValuePair<string, string>> { new("Host", ReplayAuthority.Ascii(uri)) };
            headers.AddRange(Flatten(request.Headers.NonValidated));
            var contentHeaders = request.Content == null
                ? new List<KeyValuePair<string, string>>()
                : Flatten(request.Content.Headers.NonValidated)
                    .Where(header => !header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            if (body.Length > 0)
            {
                contentHeaders.Add(new("Content-Length", body.Length.ToString(CultureInfo.InvariantCulture)));
            }

            return new ReplayMessage(
                $"{request.Method.Method} {uri.PathAndQuery} {ReplayMessage.FormatVersion(version)}",
                headers,
                contentHeaders,
                body);
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
    }
}
