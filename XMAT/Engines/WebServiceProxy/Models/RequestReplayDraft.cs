// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using XMAT.Operations.Replay;
using XMAT.WebServiceCapture.Models;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.WebServiceCapture
{
    /// <summary>
    /// Editable request snapshot that never writes changes back to the captured connection.
    /// </summary>
    internal sealed class RequestReplayDraft
    {
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        // Replaying a request unconditionally removes the validators that would let the server answer 304 or 412.
        private static readonly HashSet<string> ConditionalHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "If-Match", "If-None-Match", "If-Modified-Since", "If-Unmodified-Since", "If-Range"
        };

        // The URL whose origin a change needs consent for (ChangesOrigin), kept whole: Uri's authority (GetLeftPart) drops
        // an IPv6 zone, which comparing origins keeps (ReplayAuthority.SameOrigin).
        private Uri _origin;
        private string _bodyText = "";

        // The exact bytes that SetBodyBytes gave, shared rather than copied, so one large body can serve many drafts.
        private byte[] _sharedBody;

        internal string Method { get; set; } = "GET";
        internal string Url { get; set; } = "";
        internal string HeadersText { get; set; } = "";

        internal string BodyText
        {
            get => _bodyText ?? (_sharedBody == null ? "" : Convert.ToBase64String(_sharedBody));
            set
            {
                _bodyText = value;
                _sharedBody = null;
            }
        }

        internal bool BodyIsBase64 { get; set; }

        // The parts that a request file left out (FromRequestFile); None for every other draft. Immutable and shared by
        // every copy of the draft. Only the Complete methods and ReplaceHeaders clear an omission: editing the text never
        // does, so a placeholder is never sent or exported as if it had been supplied.
        internal RequestOmissions Omissions { get; private set; } = RequestOmissions.None;

        // The source of the stored text that the draft was made from (FromCapture): its capture's, AmbiguousUnmarked for a
        // capture without XMAT's text-format marker; Literal for every other draft, such as a new request, a request
        // file's or a raw request's. Every copy keeps it and no edit changes it, so the operation can refuse to send or
        // export unmarked text without a decision (ReplayOperation.StoredTextAmbiguousCode).
        internal global::XMAT.DataAbstractionLayer.CaptureTextSource CaptureTextSource { get; private set; } =
            global::XMAT.DataAbstractionLayer.CaptureTextSource.Literal;

        // Only exactly GET, HEAD and OPTIONS are safe; any other method, in any case, can change server state. Shared by
        // the operation's policy and the GUI's confirmation, so both count the same requests.
        internal static bool IsUnsafeMethod(string method) => method is not ("GET" or "HEAD" or "OPTIONS");

        internal bool UsesUnsafeMethod => IsUnsafeMethod(Method);

        // True when the URL no longer targets the origin the request was captured for (ReplayAuthority.SameOrigin), while
        // it still carries the captured headers, such as credentials.
        internal bool ChangesOrigin =>
            _origin != null && Uri.TryCreate(Url, UriKind.Absolute, out var uri) && !ReplayAuthority.SameOrigin(_origin, uri);

        // Even a safe method needs confirmation when captured credentials could reach a different origin.
        internal bool RequiresConfirmation => UsesUnsafeMethod || ChangesOrigin;

        // The origin that the request was captured, loaded or first sent for, which ChangesOrigin compares the URL with;
        // null when it has none yet, such as a new request or a request file without a URL.
        internal Uri CapturedOrigin => _origin;

        internal static RequestReplayDraft FromCapture(ProxyConnectionModel capture) =>
            FromCapture(capture, int.MaxValue, CancellationToken.None);

        // Edit and resend's draft of a captured request, which can be made off the UI thread from a row that is no longer
        // changed, such as a row of the Replays tab. A decoded body larger than maxShownBodyBytes stays its exact bytes
        // (SetBodyBytes), and no text or Base64 is made of it; a smaller one is shown as text or Base64 as always.
        // cancellationToken is checked before and after the body is decoded and before it is checked as UTF-8.
        internal static RequestReplayDraft FromCapture(
            ProxyConnectionModel capture, int maxShownBodyBytes, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(capture);
            ArgumentOutOfRangeException.ThrowIfNegative(maxShownBodyBytes);
            if (!CanReplay(capture))
                throw new ArgumentException(
                    "Only HTTP requests without a protocol upgrade, recorded with their request body, can be replayed.");

            string url = CaptureUrl(capture);

            // Keep the raw header lines: the capture's dictionary collapses repeated header names.
            string rawHeaders = capture.RequestLineAndHeaders ?? "";
            int firstLineEnd = rawHeaders.IndexOf('\n');
            cancellationToken.ThrowIfCancellationRequested();
            byte[] body = GetCapturedBody(capture);
            cancellationToken.ThrowIfCancellationRequested();
            var draft = new RequestReplayDraft
            {
                Method = capture.Method,
                Url = url,
                _origin = new Uri(url),
                HeadersText = firstLineEnd < 0 ? "" : rawHeaders[(firstLineEnd + 1)..].TrimEnd('\r', '\n'),
                CaptureTextSource = capture.CaptureTextSource ?? global::XMAT.DataAbstractionLayer.CaptureTextSource.Unknown,
            };
            if (body.LongLength > maxShownBodyBytes)
            {
                draft.SetBodyBytes(body);
                return draft;
            }

            // Encoded payloads must not be treated as editable text just because their bytes decode as UTF-8.
            bool encoded = capture.RequestHeaders.TryGetValue("Content-Encoding", out string encoding) &&
                !string.Equals(encoding, "identity", StringComparison.OrdinalIgnoreCase);
            draft.ShowBody(body, encoded, maxShownBodyBytes, cancellationToken);
            return draft;
        }

        // How a captured body is shown (FromCapture, and RawConversion.ToDraft for a raw request's): its exact bytes when it
        // is larger than maxShownBodyBytes; otherwise text when it is not encoded and is UTF-8 without control characters
        // other than line breaks and tabs, and lossless Base64 when it is not.
        internal void ShowBody(byte[] body, bool encoded, int maxShownBodyBytes, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(body);
            if (body.LongLength > maxShownBodyBytes)
            {
                SetBodyBytes(body);
                return;
            }

            BodyIsBase64 = true;
            BodyText = Convert.ToBase64String(body);
            if (!encoded)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    string text = StrictUtf8.GetString(body);
                    if (!text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
                    {
                        BodyText = text;
                        BodyIsBase64 = false;
                    }
                }
                catch (DecoderFallbackException)
                {
                    // Binary and non-UTF-8 bodies remain editable as lossless Base64.
                }
            }
        }

        // The scheme, host and port of a capture whose path is relative: an IPv6 host is bracketed and keeps its stored zone,
        // and a port is present only when it is not the scheme's default. The URL must round-trip without interpreting any
        // host text as user information, path, query or fragment, or changing the stored port; otherwise it throws.
        internal static string CaptureAuthority(ProxyConnectionModel capture)
        {
            ArgumentNullException.ThrowIfNull(capture);
            try
            {
                string storedHost = capture.Host ?? string.Empty;
                if (!storedHost.Contains(':') &&
                    !storedHost.StartsWith('[') &&
                    storedHost.AsSpan().IndexOfAny('/', '\\') >= 0)
                {
                    throw new UriFormatException();
                }

                var authority = new UriBuilder(capture.Scheme, capture.Host);
                int? storedPort = null;
                if (!string.IsNullOrEmpty(capture.Port))
                {
                    storedPort = int.Parse(capture.Port);
                    authority.Port = storedPort.Value;
                }

                Uri uri = authority.Uri;
                string userInfoWithDelimiter = uri.GetComponents(
                    UriComponents.UserInfo | UriComponents.KeepDelimiter,
                    UriFormat.UriEscaped);
                bool hasEmptyUserInfoDelimiter =
                    string.IsNullOrEmpty(uri.UserInfo) && userInfoWithDelimiter.Length != 0;
                if (hasEmptyUserInfoDelimiter ||
                    !string.IsNullOrEmpty(uri.UserInfo) ||
                    (capture.Host?.StartsWith('[') == true && !capture.Host.EndsWith(']')) ||
                    uri.PathAndQuery != "/" ||
                    !string.IsNullOrEmpty(uri.Fragment) ||
                    (storedPort.HasValue ? uri.Port != storedPort.Value : !uri.IsDefaultPort))
                {
                    throw new UriFormatException();
                }

                return ReplayDestination.AuthorityKeepingZone(uri);
            }
            catch (Exception exception) when (
                exception is ArgumentException or FormatException or InvalidOperationException or OverflowException)
            {
                throw new UriFormatException("The captured host and port do not make a URL authority.");
            }
        }

        // The effective URL of a capture, without reading its body: an absolute target wins over the structured authority,
        // and either form keeps an IPv6 zone as stored data.
        internal static string CaptureUrl(ProxyConnectionModel capture)
        {
            ArgumentNullException.ThrowIfNull(capture);
            string path = capture.Path ?? string.Empty;
            if (Uri.TryCreate(path, UriKind.Absolute, out Uri absolute) &&
                (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
            {
                return ReplayDestination.AbsoluteUriKeepingZone(absolute);
            }

            return CaptureAuthority(capture) + (path.StartsWith('/') ? path : "/" + path);
        }

        // A draft from a request file. What the file left out stays omitted (Omissions) and shows as empty until it is
        // supplied explicitly. The file's URL is the origin that a change needs consent for; a file without one gives none,
        // so the URL supplied for it is the request's first origin. The body shows as text when it is UTF-8 without control
        // characters and has no Content-Encoding, and is kept as its exact bytes, shown as Base64, otherwise.
        internal static RequestReplayDraft FromRequestFile(RequestFileContent content)
        {
            ArgumentNullException.ThrowIfNull(content);
            var draft = new RequestReplayDraft
            {
                Method = content.Method ?? "",
                Url = content.Url ?? "",
                _origin = content.Url == null ? null : OriginOf(content.Url),
                HeadersText = content.Headers == null ? "" : FormatHeaders(content.Headers),
                Omissions = content.Omitted ?? throw new ArgumentException("The content has no omissions.", nameof(content)),
            };

            byte[] body = content.Body ?? Array.Empty<byte>();
            bool encoded = content.Headers != null && content.Headers.Any(header =>
                header.Key.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase) &&
                !header.Value.Trim(' ', '\t').Equals("identity", StringComparison.OrdinalIgnoreCase));
            string text = null;
            if (!encoded)
            {
                try
                {
                    text = StrictUtf8.GetString(body);
                    if (text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
                        text = null;
                }
                catch (DecoderFallbackException)
                {
                    // Binary and non-UTF-8 bodies stay as their exact bytes, shown as Base64.
                }
            }

            if (text != null)
            {
                draft.BodyText = text;
                draft.BodyIsBase64 = false;
            }
            else
            {
                draft.SetBodyBytes(body);
            }

            return draft;
        }

        // An absolute URL, which may be a sequence template ('#' only in its path or query), as the origin that a change
        // needs consent for: only its scheme, host, with any IPv6 zone, and port are compared (ChangesOrigin).
        private static Uri OriginOf(string url) =>
            Uri.TryCreate(url.Replace(ReplaySequence.Placeholder.ToString(), "0"), UriKind.Absolute, out Uri uri)
                ? uri
                : throw new ArgumentException("The URL is not an absolute URL.", nameof(url));

        private static byte[] GetCapturedBody(ProxyConnectionModel capture)
        {
            byte[] body = capture.RequestBody ?? Array.Empty<byte>();
            capture.RequestHeaders.TryGetValue("Transfer-Encoding", out string transferEncoding);
            try
            {
                return ChunkedBodyDecoder.DecodeIfFramed(transferEncoding, body, body.Length);
            }
            catch (InvalidDataException exception)
            {
                throw new FormatException("The captured request contains malformed chunked framing.", exception);
            }
        }

        // Read without making the row's header dictionary (HasRequestHeader), so checking a row whose head is large copies
        // nothing of it; the answer is the dictionary's.
        internal static bool CanReplay(ProxyConnectionModel capture)
        {
            return capture != null &&
                (capture.Scheme == Uri.UriSchemeHttp || capture.Scheme == Uri.UriSchemeHttps) &&
                !string.IsNullOrWhiteSpace(capture.Method) &&
                !string.Equals(capture.Method, "CONNECT", StringComparison.OrdinalIgnoreCase) &&
                !capture.HasRequestHeader("Upgrade") &&
                !capture.HasRequestHeader("Sec-WebSocket-Key") &&
                !capture.HasRequestHeader(RequestBodyNotKeptHeader);
        }

        // The Replays tab records a send without a body that does not fit its body budget and says so with one of these
        // headers in the stored request or response head, which saved captures and exports keep. A request recorded
        // without its body is never sent again: it would go out with an empty body.
        internal const string RequestBodyNotKeptHeader = "X-XMAT-Request-Body-Not-Kept";
        internal const string ResponseBodyNotKeptHeader = "X-XMAT-Response-Body-Not-Kept";

        internal HttpRequestMessage CreateRequest() => CreateRequest(GetBodyBytes());

        // The request with these body bytes in place of the draft's own, which it does not decode: an export builds the
        // headers once over an empty body, and makes the body itself once.
        internal HttpRequestMessage CreateRequest(byte[] body)
        {
            ArgumentNullException.ThrowIfNull(body);
            if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            {
                throw new ArgumentException("Enter an absolute HTTP or HTTPS URL without embedded credentials or a fragment.");
            }
            if (!IsValidMethod(Method))
            {
                throw new ArgumentException("Enter a valid HTTP method other than CONNECT.");
            }

            var headers = ParseHeaders();
            if (headers.Any(h => h.Key.Equals("Upgrade", StringComparison.OrdinalIgnoreCase) ||
                h.Key.Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException("Protocol upgrades and WebSocket handshakes cannot be replayed.");
            }

            // Connection can nominate additional hop-by-hop headers beyond the standard fixed list.
            HashSet<string> connectionHeaders = ConnectionNominations(headers);

            var request = new ClientRequest { Method = Method, BodyBytes = body };
            foreach (var header in headers)
            {
                // The edited URL and body determine the new authority and length, not the captured framing.
                if (ProxyHeaderUtilities.IsHopByHopHeader(header.Key) ||
                    connectionHeaders.Contains(header.Key) ||
                    header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (ProxyHeaderUtilities.IsContentHeader(header.Key))
                    request.ContentHeaders.Add(header.Key, header.Value);
                else
                    request.Headers.Add(header.Key, header.Value);
            }

            // Share the proxy's version negotiation and framing rules, without its capture callbacks.
            var message = ProxyHttpRequestFactory.Create(request, uri);
            try
            {
                // The shared factory uses TryAddWithoutValidation; surface any rejected editor headers.
                foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
                {
                    if (!message.Headers.Contains(header.Key))
                        throw new FormatException($"Header '{header.Key}' is not a request header.");
                }
                foreach (KeyValuePair<string, IEnumerable<string>> header in request.ContentHeaders)
                {
                    if (!message.Content.Headers.Contains(header.Key))
                        throw new FormatException($"Header '{header.Key}' is not a content header.");
                }
                return message;
            }
            catch
            {
                message.Dispose();
                throw;
            }
        }

        internal void SetBodyFormat(bool base64)
        {
            if (base64 == BodyIsBase64)
                return;
            byte[] bytes = GetBodyBytes();
            // Finish conversion before updating either field so a failed decode leaves the draft intact.
            string text = base64 ? Convert.ToBase64String(bytes) : StrictUtf8.GetString(bytes);
            BodyText = text;
            BodyIsBase64 = base64;
        }

        // The body as it is sent. A body from SetBodyBytes is returned as the same array, which callers must not change.
        internal byte[] GetBodyBytes() =>
            _sharedBody ??
            (BodyIsBase64 ? Convert.FromBase64String(BodyText ?? "") : StrictUtf8.GetBytes(BodyText ?? ""));

        // A copy that sends to url, sharing this draft's header text and body rather than copying them.
        internal RequestReplayDraft WithUrl(string url) =>
            new()
            {
                Method = Method,
                Url = url,
                HeadersText = HeadersText,
                BodyIsBase64 = BodyIsBase64,
                _bodyText = _bodyText,
                _sharedBody = _sharedBody,
                _origin = _origin,
                Omissions = Omissions,
                CaptureTextSource = CaptureTextSource,
            };

        // A copy whose captured origin is origin, sharing this draft's header text and body: how a recorded send's origin
        // comes back to a request made from its row (contract 2.8, O4), so that a later send to another origin asks again.
        // A null origin would erase the one that a load established, so it is refused.
        internal RequestReplayDraft WithCapturedOrigin(Uri origin)
        {
            ArgumentNullException.ThrowIfNull(origin);
            if (!origin.IsAbsoluteUri)
                throw new ArgumentException("The origin is not an absolute URL.", nameof(origin));
            RequestReplayDraft copy = WithUrl(Url);
            copy._origin = origin;
            return copy;
        }

        // Replaces the body with these exact bytes, without copying them; BodyText shows them as Base64.
        internal void SetBodyBytes(byte[] body)
        {
            ArgumentNullException.ThrowIfNull(body);
            _sharedBody = body;
            _bodyText = null;
            BodyIsBase64 = true;
        }

        // Replaces every header with this name by a single line, in place of the first one, or appends it.
        internal void SetHeader(string name, string value) => HeadersText = SetHeaderIn(HeadersText, name, value);

        // SetHeader's edit of header text, which it returns without changing anything: FormatException when the text or
        // the line is not a valid header.
        internal static string SetHeaderIn(string headersText, string name, string value)
        {
            ValidateHeader(name, value ?? "");
            var headers = ParseHeaders(headersText);
            int index = headers.FindIndex(header => header.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            headers.RemoveAll(header => header.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            headers.Insert(index < 0 ? headers.Count : index, new(name, (value ?? "").Trim(' ', '\t')));
            return FormatHeaders(headers);
        }

        // The names, in any case, that the Connection lines of these headers nominate: building the request drops every
        // header with one of them (CreateRequest).
        internal static HashSet<string> ConnectionNominations(IEnumerable<KeyValuePair<string, string>> headers)
        {
            var nominated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in headers.Where(h => h.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (string name in header.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    nominated.Add(name);
            }

            return nominated;
        }

        // Whether a Connection line of these headers nominates this name, so that building the request drops it.
        internal static bool IsNominatedByConnection(IEnumerable<KeyValuePair<string, string>> headers, string name) =>
            ConnectionNominations(headers).Contains(name);

        // Header text as the draft reads it: name and value pairs, in order; FormatException when a line is not a valid
        // header.
        internal static IReadOnlyList<KeyValuePair<string, string>> ParseHeaderText(string headersText) =>
            ParseHeaders(headersText);

        internal void RemoveHeader(string name)
        {
            var headers = ParseHeaders();
            if (headers.RemoveAll(header => header.Key.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0)
                HeadersText = FormatHeaders(headers);
        }

        internal void RemoveConditionalHeaders()
        {
            var headers = ParseHeaders();
            if (headers.RemoveAll(header => ConditionalHeaders.Contains(header.Key)) > 0)
                HeadersText = FormatHeaders(headers);
        }

        // Supplies the method that a request file left out.
        internal void CompleteMethod(string method)
        {
            ArgumentNullException.ThrowIfNull(method);
            Method = method;
            Omissions = Omissions with { Method = false };
        }

        // Supplies the URL that a request file left out, which must be a valid request URL. A draft without an origin takes
        // this URL's as its first, so a later change to another origin needs consent; an origin it already has, such as a
        // file's or a capture's URL, is never replaced.
        internal void CompleteUrl(string url)
        {
            ArgumentNullException.ThrowIfNull(url);
            if (!IsValidRequestFileUrl(url))
                throw new ArgumentException(
                    "Enter an absolute HTTP or HTTPS URL without embedded credentials, with a '#' only in its path or query.",
                    nameof(url));
            Url = url;
            _origin ??= OriginOf(url);
            Omissions = Omissions with { Url = false };
        }

        // Replaces every header line, and with it a header block that a request file left out; "" clears them all. The
        // lines are checked first, so invalid text changes nothing.
        internal void ReplaceHeaders(string headersText)
        {
            ArgumentNullException.ThrowIfNull(headersText);
            ParseHeaders(headersText);
            HeadersText = headersText;
            Omissions = Omissions with { Headers = false };
        }

        // Supplies the body that a request file left out, as these exact bytes; an empty array is an explicitly empty body.
        internal void CompleteBody(byte[] body)
        {
            SetBodyBytes(body);
            Omissions = Omissions with { Body = false };
        }

        // The draft as data, as a request file holds it: a method, an absolute HTTP or HTTPS URL (a '#' only in its path
        // or query), valid header lines and a body, within RequestFileLimits. A part that is still omitted is not checked:
        // its placeholder is not data. The header and body limits are checked before anything of their size is made: the
        // header text is scanned in place, and the body's length is counted without decoding it. Sending checks more
        // (Unicode header values, control headers, upgrades); this does not.
        internal void ValidateStructure()
        {
            if (!Omissions.Method && (!IsValidMethod(Method) || Method.Length > RequestFileLimits.MaxMethodChars))
                throw new FormatException("Enter a valid HTTP method other than CONNECT, of at most 64 characters.");
            if (!Omissions.Url && !IsValidRequestFileUrl(Url))
                throw new FormatException(
                    "Enter an absolute HTTP or HTTPS URL without embedded credentials, of at most 65,536 characters, " +
                    "with a '#' only in its path or query.");
            if (!Omissions.Headers)
                CheckHeaderLimits(HeadersText);
            if (!Omissions.Body && GetBodyLength() > RequestFileLimits.MaxBodyBytes)
                throw new FormatException("A request file holds a body of at most 64 MiB.");
        }

        // The body's length in bytes, counted without decoding it: shared bytes as they are, text as its UTF-8, and Base64
        // from its significant characters and padding, checked as Convert.FromBase64String reads it (the Base64 alphabet,
        // spaces, tabs and line breaks ignored, whole groups of four, at most two '=' and only at the end).
        internal long GetBodyLength()
        {
            if (_sharedBody != null)
                return _sharedBody.LongLength;
            string text = _bodyText ?? "";
            if (!BodyIsBase64)
            {
                try
                {
                    return StrictUtf8.GetByteCount(text);
                }
                catch (EncoderFallbackException exception)
                {
                    throw new FormatException("The body is not valid text.", exception);
                }
            }

            long significant = 0;
            int padding = 0;
            foreach (char c in text)
            {
                if (c is ' ' or '\t' or '\r' or '\n')
                    continue;
                if (c == '=')
                    padding++;
                else if (padding > 0 || !(char.IsAsciiLetterOrDigit(c) || c is '+' or '/'))
                    throw new FormatException("The body is not valid Base64.");
                significant++;
            }

            if (significant % 4 != 0 || padding > 2)
                throw new FormatException("The body is not valid Base64.");
            return (significant / 4 * 3) - padding;
        }

        // The characters of body text that TryCountBody reads between two checks of its token.
        internal const int CountSliceChars = 1 << 20;

        // GetBodyLength's count and checks, made in slices of the text with the token checked before each and after the
        // last, and never throwing for invalid data: a body that is not valid text or Base64 gives its problem and a length
        // of 0. A surrogate pair, or Base64 padding, that a slice boundary splits is read as GetBodyLength reads the whole
        // text. Off the UI thread, so a large body is never counted or checked there (L8-A2).
        internal BodyCount TryCountBody(CancellationToken token) => TryCountBody(CountSliceChars, token);

        // TryCountBody in slices of sliceChars characters; afterSlice gets each slice's index once that slice is read
        // (tests).
        internal BodyCount TryCountBody(int sliceChars, CancellationToken token, Action<int> afterSlice = null)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(sliceChars, 1);
            token.ThrowIfCancellationRequested();
            if (_sharedBody != null)
                return new BodyCount(_sharedBody.LongLength, BodyProblem.None);
            string text = _bodyText ?? "";
            return BodyIsBase64
                ? CountBase64(text, sliceChars, afterSlice, token)
                : CountText(text, sliceChars, afterSlice, token);
        }

        // The UTF-8 length of text, which must be valid UTF-16: a high surrogate followed by a low one, wherever a slice
        // boundary falls between them, is one character of 4 bytes, and any other surrogate is invalid.
        private static BodyCount CountText(string text, int sliceChars, Action<int> afterSlice, CancellationToken token)
        {
            long bytes = 0;
            bool highSurrogate = false;
            int slice = 0;
            for (int start = 0; start < text.Length; slice++)
            {
                token.ThrowIfCancellationRequested();
                int end = (int)Math.Min(text.Length, (long)start + sliceChars);
                for (int index = start; index < end; index++)
                {
                    char c = text[index];
                    if (highSurrogate)
                    {
                        if (!char.IsLowSurrogate(c))
                            return new BodyCount(0, BodyProblem.NotText);
                        bytes += 4;
                        highSurrogate = false;
                    }
                    else if (char.IsHighSurrogate(c))
                    {
                        highSurrogate = true;
                    }
                    else if (char.IsLowSurrogate(c))
                    {
                        return new BodyCount(0, BodyProblem.NotText);
                    }
                    else
                    {
                        bytes += c < 0x80 ? 1 : c < 0x800 ? 2 : 3;
                    }
                }

                afterSlice?.Invoke(slice);
                start = end;
            }

            token.ThrowIfCancellationRequested();
            return highSurrogate ? new BodyCount(0, BodyProblem.NotText) : new BodyCount(bytes, BodyProblem.None);
        }

        // GetBodyLength's Base64 rules, with the significant characters and the padding carried across slices.
        private static BodyCount CountBase64(string text, int sliceChars, Action<int> afterSlice, CancellationToken token)
        {
            long significant = 0;
            int padding = 0;
            int slice = 0;
            for (int start = 0; start < text.Length; slice++)
            {
                token.ThrowIfCancellationRequested();
                int end = (int)Math.Min(text.Length, (long)start + sliceChars);
                for (int index = start; index < end; index++)
                {
                    char c = text[index];
                    if (c is ' ' or '\t' or '\r' or '\n')
                        continue;
                    if (c == '=')
                        padding++;
                    else if (padding > 0 || !(char.IsAsciiLetterOrDigit(c) || c is '+' or '/'))
                        return new BodyCount(0, BodyProblem.NotBase64);
                    significant++;
                }

                afterSlice?.Invoke(slice);
                start = end;
            }

            token.ThrowIfCancellationRequested();
            return significant % 4 != 0 || padding > 2
                ? new BodyCount(0, BodyProblem.NotBase64)
                : new BodyCount((significant / 4 * 3) - padding, BodyProblem.None);
        }

        // The header lines as ParseHeaders reads them, checked in place against RequestFileLimits: each line is valid, and
        // there are at most MaxHeaders of them, of at most MaxHeaderBytes of UTF-8 names and values together. Nothing is
        // allocated, so text of any size is refused at no cost.
        private static void CheckHeaderLimits(string headersText)
        {
            var lines = new HeaderLineReader(headersText, null, CancellationToken.None);
            int count = 0;
            long bytes = 0;
            while (lines.Read(out ReadOnlySpan<char> name, out _, out ReadOnlySpan<char> value))
            {
                bytes += Encoding.UTF8.GetByteCount(name) + Encoding.UTF8.GetByteCount(value);
                if (++count > RequestFileLimits.MaxHeaders || bytes > RequestFileLimits.MaxHeaderBytes)
                    throw new FormatException("A request file holds at most 256 headers, of at most 256 KiB together.");
            }
        }

        // Header text read in place, line by line, by ParseHeaders' rules: a line ends with CRLF or LF, and a lone CR is
        // refused; a blank line is skipped; any other line is a token name, a colon and a value without control characters
        // other than tab, read without the spaces and tabs around it. Nothing is copied and each character is read once:
        // the token is checked, and checkpoint (tests) called, each time another RawRequest.CheckChars characters have been
        // read, so that text of any size can be read on a cancellable worker.
        internal ref struct HeaderLineReader
        {
            private readonly ReadOnlySpan<char> _text;
            private int _position;
            private ScanProgress _progress;

            internal HeaderLineReader(string headersText, Action<long> checkpoint, CancellationToken token)
            {
                _text = (headersText ?? string.Empty).AsSpan();
                _position = 0;
                _progress = new ScanProgress(checkpoint, token);
            }

            // The next line that is not blank: its name, the text after its colon as typed, and its value, that text
            // without the spaces and tabs around it. False after the last line; FormatException, with ParseHeaders'
            // message, at the first line that is not a valid header.
            internal bool Read(out ReadOnlySpan<char> name, out ReadOnlySpan<char> raw, out ReadOnlySpan<char> value)
            {
                while (_position < _text.Length)
                {
                    int start = _position;
                    int end = _text.Length;
                    int separator = -1;
                    int first = -1;
                    int last = -1;
                    bool blank = true;
                    bool valid = true;
                    _position = _text.Length;
                    for (int index = start; index < _text.Length; index++)
                    {
                        _progress.Advance(1);
                        char c = _text[index];
                        if (c is '\r' or '\n')
                        {
                            if (c == '\r')
                            {
                                if (index + 1 == _text.Length || _text[index + 1] != '\n')
                                    throw new FormatException(IncompleteLinesMessage);

                                // The CRLF's LF is read too.
                                _progress.Advance(1);
                            }

                            end = index;
                            _position = index + (c == '\r' ? 2 : 1);
                            break;
                        }

                        blank &= char.IsWhiteSpace(c);
                        if (separator < 0)
                        {
                            if (c == ':')
                                separator = index;
                            else
                                valid &= IsTokenCharacter(c);
                        }
                        else
                        {
                            valid &= (!char.IsControl(c) || c == '\t') && c != '\u007f';
                            if (c is not (' ' or '\t'))
                            {
                                first = first < 0 ? index : first;
                                last = index;
                            }
                        }
                    }

                    if (blank)
                        continue;
                    if (separator <= start || !valid)
                        throw new FormatException(InvalidHeaderMessage);
                    name = _text[start..separator];
                    raw = _text[(separator + 1)..end];
                    value = first < 0 ? ReadOnlySpan<char>.Empty : _text[first..(last + 1)];
                    return true;
                }

                name = raw = value = ReadOnlySpan<char>.Empty;
                return false;
            }
        }

        // An absolute HTTP or HTTPS URL without user info or a fragment, of at most MaxUrlChars characters; a URL with a
        // '#' is a sequence template, which has its every '#' in its path or query and is checked with a number in their
        // place.
        internal static bool IsValidRequestFileUrl(string url)
        {
            if (url == null || url.Length > RequestFileLimits.MaxUrlChars)
                return false;
            string probe = url;
            if (url.Contains(ReplaySequence.Placeholder))
            {
                if (!ReplaySequence.IsPathAndQueryTemplate(url))
                    return false;
                probe = url.Replace(ReplaySequence.Placeholder.ToString(), "0");
            }

            return Uri.TryCreate(probe, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment);
        }

        // The header lines as name and value pairs, in order; FormatException when a line is not a valid header.
        internal IReadOnlyList<KeyValuePair<string, string>> GetHeaders() => ParseHeaders();

        // Whether a header line has one of these names, in any case, whether or not the request built from the draft keeps
        // it: a header that Connection nominates is in the headers but never sent.
        internal bool HasAnyHeader(IReadOnlyCollection<string> names) =>
            ParseHeaders().Any(header => names.Contains(header.Key, StringComparer.OrdinalIgnoreCase));

        // A header name is an HTTP token; its value has no control characters other than tab, so no line can be injected.
        internal static bool IsValidHeader(string name, string value) =>
            !string.IsNullOrEmpty(name) && name.All(IsTokenCharacter) &&
            value != null && !value.Any(c => (char.IsControl(c) && c != '\t') || c == '\u007f');

        // The HTTP stack sends header values as ASCII only, and refuses others after it has connected.
        internal static bool IsSendableHeaderValue(string value) =>
            value != null && value.All(c => c == '\t' || (c >= ' ' && c <= '~'));

        internal static bool IsValidMethod(string method) =>
            !string.IsNullOrEmpty(method) && method.All(IsTokenCharacter) &&
            !string.Equals(method, "CONNECT", StringComparison.OrdinalIgnoreCase);

        // The request to send, refused before anything is sent when a header value is one the HTTP stack cannot send.
        // RequestCurlExporter uses CreateRequest instead: curl sends such values as they are.
        internal HttpRequestMessage CreateRequestToSend()
        {
            HttpRequestMessage message = CreateRequest();
            bool sendable = message.Headers.NonValidated.Concat(message.Content.Headers.NonValidated)
                .All(header => header.Value.All(IsSendableHeaderValue));
            if (!sendable)
            {
                message.Dispose();
                throw new FormatException("Header values can only be sent as ASCII characters.");
            }

            return message;
        }

        private static void ValidateHeader(string name, string value)
        {
            if (!IsValidHeader(name, value))
                throw new FormatException(InvalidHeaderMessage);
        }

        private const string InvalidHeaderMessage = "Enter one valid 'Name: value' header per line.";

        private const string IncompleteLinesMessage = "Headers must use complete lines.";

        private static string FormatHeaders(IEnumerable<KeyValuePair<string, string>> headers) =>
            string.Join("\r\n", headers.Select(header => $"{header.Key}: {header.Value}"));

        private List<KeyValuePair<string, string>> ParseHeaders() => ParseHeaders(HeadersText);

        private static List<KeyValuePair<string, string>> ParseHeaders(string headersText)
        {
            var result = new List<KeyValuePair<string, string>>();
            using var reader = new StringReader((headersText ?? "").Replace("\r\n", "\n"));
            // StringReader accepts lone CRs; reject these rather than allowing hidden header injection.
            if ((headersText ?? "").Replace("\r\n", "").Contains('\r'))
                throw new FormatException(IncompleteLinesMessage);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                int separator = line.IndexOf(':');
                if (separator <= 0 || !IsValidHeader(line[..separator], line[(separator + 1)..]))
                {
                    throw new FormatException(InvalidHeaderMessage);
                }
                // Only HTTP's optional whitespace, spaces and tabs, is trimmed; other characters stay and are checked before sending.
                result.Add(new(line[..separator], line[(separator + 1)..].Trim(' ', '\t')));
            }
            return result;
        }

        internal static bool IsTokenCharacter(char c) =>
            char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c);
    }

    // Why a draft's body could not be counted (RequestReplayDraft.TryCountBody).
    internal enum BodyProblem
    {
        None,
        NotText,
        NotBase64,
    }

    // A draft's body length in bytes, as GetBodyLength counts it, or the problem that stopped the count, with a length of 0.
    internal readonly record struct BodyCount(long Length, BodyProblem Problem);
}
