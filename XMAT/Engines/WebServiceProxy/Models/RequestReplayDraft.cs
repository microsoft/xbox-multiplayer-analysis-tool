// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
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

        private string _originalAuthority;
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

        // Only exactly GET, HEAD and OPTIONS are safe; any other method, in any case, can change server state. Shared by
        // the operation's policy and the GUI's confirmation, so both count the same requests.
        internal static bool IsUnsafeMethod(string method) => method is not ("GET" or "HEAD" or "OPTIONS");

        internal bool UsesUnsafeMethod => IsUnsafeMethod(Method);

        // True when the URL no longer targets the origin the request was captured for, while it still carries the
        // captured headers, such as credentials.
        internal bool ChangesOrigin =>
            _originalAuthority != null && Uri.TryCreate(Url, UriKind.Absolute, out var uri) &&
            !string.Equals(_originalAuthority, uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

        // Even a safe method needs confirmation when captured credentials could reach a different origin.
        internal bool RequiresConfirmation => UsesUnsafeMethod || ChangesOrigin;

        internal static RequestReplayDraft FromCapture(ProxyConnectionModel capture)
        {
            ArgumentNullException.ThrowIfNull(capture);
            if (!CanReplay(capture))
                throw new ArgumentException(
                    "Only HTTP requests without a protocol upgrade, recorded with their request body, can be replayed.");

            // Captures can store either an absolute proxy URL or a path relative to the recorded host.
            string path = capture.Path ?? "";
            string url;
            if (Uri.TryCreate(path, UriKind.Absolute, out var absolute) &&
                (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
            {
                url = absolute.AbsoluteUri;
            }
            else
            {
                var authority = new UriBuilder(capture.Scheme, capture.Host);
                if (!string.IsNullOrEmpty(capture.Port))
                    authority.Port = int.Parse(capture.Port);
                url = authority.Uri.GetLeftPart(UriPartial.Authority) +
                    (path.StartsWith('/') ? path : "/" + path);
            }

            // Keep the raw header lines: the capture's dictionary collapses repeated header names.
            string rawHeaders = capture.RequestLineAndHeaders ?? "";
            int firstLineEnd = rawHeaders.IndexOf('\n');
            byte[] body = GetCapturedBody(capture);
            var draft = new RequestReplayDraft
            {
                Method = capture.Method,
                Url = url,
                _originalAuthority = new Uri(url).GetLeftPart(UriPartial.Authority),
                HeadersText = firstLineEnd < 0 ? "" : rawHeaders[(firstLineEnd + 1)..].TrimEnd('\r', '\n'),
                BodyIsBase64 = true,
                BodyText = Convert.ToBase64String(body)
            };

            // Encoded payloads must not be treated as editable text just because their bytes decode as UTF-8.
            bool encoded = capture.RequestHeaders.TryGetValue("Content-Encoding", out string encoding) &&
                !string.Equals(encoding, "identity", StringComparison.OrdinalIgnoreCase);
            if (!encoded)
            {
                try
                {
                    string text = StrictUtf8.GetString(body);
                    if (!text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
                    {
                        draft.BodyText = text;
                        draft.BodyIsBase64 = false;
                    }
                }
                catch (DecoderFallbackException)
                {
                    // Binary and non-UTF-8 bodies remain editable as lossless Base64.
                }
            }
            return draft;
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
                _originalAuthority = content.Url == null ? null : AuthorityOf(content.Url),
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

        // The scheme, host and port of an absolute URL, which may be a sequence template ('#' only in its path or query).
        private static string AuthorityOf(string url) =>
            Uri.TryCreate(url.Replace(ReplaySequence.Placeholder.ToString(), "0"), UriKind.Absolute, out Uri uri)
                ? uri.GetLeftPart(UriPartial.Authority)
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

        internal static bool CanReplay(ProxyConnectionModel capture)
        {
            return capture != null &&
                (capture.Scheme == Uri.UriSchemeHttp || capture.Scheme == Uri.UriSchemeHttps) &&
                !string.IsNullOrWhiteSpace(capture.Method) &&
                !string.Equals(capture.Method, "CONNECT", StringComparison.OrdinalIgnoreCase) &&
                !capture.RequestHeaders.ContainsKey("Upgrade") &&
                !capture.RequestHeaders.ContainsKey("Sec-WebSocket-Key") &&
                !capture.RequestHeaders.ContainsKey(RequestBodyNotKeptHeader);
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
            var connectionHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in headers.Where(h => h.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (string name in header.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    connectionHeaders.Add(name);
            }

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
                _originalAuthority = _originalAuthority,
                Omissions = Omissions,
            };

        // Replaces the body with these exact bytes, without copying them; BodyText shows them as Base64.
        internal void SetBodyBytes(byte[] body)
        {
            ArgumentNullException.ThrowIfNull(body);
            _sharedBody = body;
            _bodyText = null;
            BodyIsBase64 = true;
        }

        // Replaces every header with this name by a single line, in place of the first one, or appends it.
        internal void SetHeader(string name, string value)
        {
            ValidateHeader(name, value ?? "");
            var headers = ParseHeaders();
            int index = headers.FindIndex(header => header.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            headers.RemoveAll(header => header.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            headers.Insert(index < 0 ? headers.Count : index, new(name, (value ?? "").Trim(' ', '\t')));
            HeadersText = FormatHeaders(headers);
        }

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
            _originalAuthority ??= AuthorityOf(url);
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

        // The header lines as ParseHeaders reads them, checked in place against RequestFileLimits: each line is valid, and
        // there are at most MaxHeaders of them, of at most MaxHeaderBytes of UTF-8 names and values together. Nothing is
        // allocated, so text of any size is refused at no cost.
        private static void CheckHeaderLimits(string headersText)
        {
            ReadOnlySpan<char> rest = (headersText ?? "").AsSpan();
            int count = 0;
            long bytes = 0;
            while (!rest.IsEmpty)
            {
                int end = rest.IndexOfAny('\r', '\n');
                ReadOnlySpan<char> line;
                if (end < 0)
                {
                    line = rest;
                    rest = ReadOnlySpan<char>.Empty;
                }
                else if (rest[end] == '\r' && (end + 1 == rest.Length || rest[end + 1] != '\n'))
                {
                    throw new FormatException("Headers must use complete lines.");
                }
                else
                {
                    line = rest[..end];
                    rest = rest[(end + (rest[end] == '\r' ? 2 : 1))..];
                }

                if (line.IsWhiteSpace())
                    continue;
                int separator = line.IndexOf(':');
                ReadOnlySpan<char> name = separator > 0 ? line[..separator] : ReadOnlySpan<char>.Empty;
                ReadOnlySpan<char> value = separator > 0 ? line[(separator + 1)..] : ReadOnlySpan<char>.Empty;
                if (separator <= 0 || !IsToken(name) || !IsHeaderValue(value))
                    throw new FormatException(InvalidHeaderMessage);
                bytes += Encoding.UTF8.GetByteCount(name) + Encoding.UTF8.GetByteCount(value.Trim(" \t"));
                if (++count > RequestFileLimits.MaxHeaders || bytes > RequestFileLimits.MaxHeaderBytes)
                    throw new FormatException("A request file holds at most 256 headers, of at most 256 KiB together.");
            }
        }

        private static bool IsToken(ReadOnlySpan<char> text)
        {
            foreach (char c in text)
            {
                if (!IsTokenCharacter(c))
                    return false;
            }

            return !text.IsEmpty;
        }

        // As IsValidHeader checks a value: no control character other than tab.
        private static bool IsHeaderValue(ReadOnlySpan<char> text)
        {
            foreach (char c in text)
            {
                if ((char.IsControl(c) && c != '\t') || c == '\u007f')
                    return false;
            }

            return true;
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

        private static string FormatHeaders(IEnumerable<KeyValuePair<string, string>> headers) =>
            string.Join("\r\n", headers.Select(header => $"{header.Key}: {header.Value}"));

        private List<KeyValuePair<string, string>> ParseHeaders() => ParseHeaders(HeadersText);

        private static List<KeyValuePair<string, string>> ParseHeaders(string headersText)
        {
            var result = new List<KeyValuePair<string, string>>();
            using var reader = new StringReader((headersText ?? "").Replace("\r\n", "\n"));
            // StringReader accepts lone CRs; reject these rather than allowing hidden header injection.
            if ((headersText ?? "").Replace("\r\n", "").Contains('\r'))
                throw new FormatException("Headers must use complete lines.");
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

        private static bool IsTokenCharacter(char c) =>
            char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c);
    }
}
