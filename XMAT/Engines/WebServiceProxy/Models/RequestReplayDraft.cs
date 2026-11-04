// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
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

        internal HttpRequestMessage CreateRequest()
        {
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

            var request = new ClientRequest { Method = Method, BodyBytes = GetBodyBytes() };
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

        internal string ToCurlCommand()
        {
            using var request = CreateRequest();
            byte[] body = GetBodyBytes();
            if (Method == "HEAD" && body.Length > 0)
                throw new ArgumentException("HEAD requests with a nonempty body cannot be exported to cURL.");

            var command = new StringBuilder("curl.exe --globoff --noproxy '*'");
            // --request HEAD alone does not tell curl to stop reading after the response headers.
            if (Method == "HEAD")
                command.Append(" --head");
            command.Append(" --request ").Append(Quote(Method)).Append(" --url ").Append(Quote(request.RequestUri.AbsoluteUri));
            foreach (var header in request.Headers.Concat(request.Content.Headers))
            {
                if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (string value in header.Value)
                    // curl uses "Name;" to send an empty header; "Name:" removes the header instead.
                    command.Append(" --header ").Append(Quote(
                        string.IsNullOrEmpty(value) ? header.Key + ";" : $"{header.Key}: {value}"));
            }
            // curl adds these by default, whereas replay sends only explicitly supplied values.
            foreach (string name in new[] { "Content-Type", "Accept", "User-Agent" })
            {
                if (!request.Headers.Concat(request.Content.Headers).Any(
                    header => header.Key.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    command.Append(" --header ").Append(Quote(name + ":"));
            }

            if (body.Length == 0)
                return command.ToString();

            // Pass body bytes through a temporary file, not shell text; finally removes sensitive payloads.
            return "& {\r\n$xmatBodyFile = [System.IO.Path]::GetTempFileName()\r\ntry {\r\n" +
                $"[System.IO.File]::WriteAllBytes($xmatBodyFile, [Convert]::FromBase64String('{Convert.ToBase64String(body)}'))\r\n" +
                command + " --data-binary \"@$xmatBodyFile\"\r\n" +
                "} finally {\r\n[System.IO.File]::Delete($xmatBodyFile)\r\n}\r\n}";
        }

        // The body as it is sent. A body from SetBodyBytes is returned as the same array, which callers must not change.
        internal byte[] GetBodyBytes() =>
            _sharedBody ??
            (BodyIsBase64 ? Convert.FromBase64String(BodyText ?? "") : StrictUtf8.GetBytes(BodyText ?? ""));

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
        // ToCurlCommand uses CreateRequest instead: curl sends such values as they are.
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

        private List<KeyValuePair<string, string>> ParseHeaders()
        {
            var result = new List<KeyValuePair<string, string>>();
            using var reader = new StringReader((HeadersText ?? "").Replace("\r\n", "\n"));
            // StringReader accepts lone CRs; reject these rather than allowing hidden header injection.
            if ((HeadersText ?? "").Replace("\r\n", "").Contains('\r'))
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

        private static string Quote(string value) => "'" + value.Replace("'", "''")
            .Replace("\u2018", "\u2018\u2018").Replace("\u2019", "\u2019\u2019")
            .Replace("\u201a", "\u201a\u201a").Replace("\u201b", "\u201b\u201b") + "'";
    }
}
