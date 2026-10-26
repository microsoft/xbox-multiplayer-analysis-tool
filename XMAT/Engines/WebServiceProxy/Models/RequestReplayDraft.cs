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
        private string _originalAuthority;

        internal string Method { get; set; } = "GET";
        internal string Url { get; set; } = "";
        internal string HeadersText { get; set; } = "";
        internal string BodyText { get; set; } = "";
        internal bool BodyIsBase64 { get; set; }
        // Even a safe method needs confirmation when captured credentials could reach a different origin.
        internal bool RequiresConfirmation =>
            Method is not ("GET" or "HEAD" or "OPTIONS") ||
            (_originalAuthority != null && Uri.TryCreate(Url, UriKind.Absolute, out var uri) &&
                !string.Equals(_originalAuthority, uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase));

        internal static RequestReplayDraft FromCapture(ProxyConnectionModel capture)
        {
            ArgumentNullException.ThrowIfNull(capture);
            if (!CanReplay(capture))
                throw new ArgumentException("Only HTTP requests without a protocol upgrade can be replayed.");

            // Captures can store either an absolute proxy URL or a path relative to the recorded host.
            string url;
            if (Uri.TryCreate(capture.Path, UriKind.Absolute, out var absolute) &&
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
                    (capture.Path.StartsWith('/') ? capture.Path : "/" + capture.Path);
            }

            // Keep the raw header lines: the capture's dictionary collapses repeated header names.
            string rawHeaders = capture.RequestLineAndHeaders ?? "";
            int firstLineEnd = rawHeaders.IndexOf('\n');
            var draft = new RequestReplayDraft
            {
                Method = capture.Method,
                Url = url,
                _originalAuthority = new Uri(url).GetLeftPart(UriPartial.Authority),
                HeadersText = firstLineEnd < 0 ? "" : rawHeaders[(firstLineEnd + 1)..].TrimEnd('\r', '\n'),
                BodyIsBase64 = true,
                BodyText = Convert.ToBase64String(capture.RequestBody ?? Array.Empty<byte>())
            };

            // Encoded payloads must not be treated as editable text just because their bytes decode as UTF-8.
            bool encoded = capture.RequestHeaders.TryGetValue("Content-Encoding", out string encoding) &&
                !string.Equals(encoding, "identity", StringComparison.OrdinalIgnoreCase);
            if (!encoded)
            {
                try
                {
                    string text = StrictUtf8.GetString(capture.RequestBody ?? Array.Empty<byte>());
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

        internal static bool CanReplay(ProxyConnectionModel capture)
        {
            return capture != null &&
                (capture.Scheme == Uri.UriSchemeHttp || capture.Scheme == Uri.UriSchemeHttps) &&
                !string.IsNullOrWhiteSpace(capture.Method) &&
                !string.Equals(capture.Method, "CONNECT", StringComparison.OrdinalIgnoreCase) &&
                !capture.RequestHeaders.ContainsKey("Upgrade") &&
                !capture.RequestHeaders.ContainsKey("Sec-WebSocket-Key");
        }

        internal HttpRequestMessage CreateRequest()
        {
            if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            {
                throw new ArgumentException("Enter an absolute HTTP or HTTPS URL without embedded credentials or a fragment.");
            }
            if (string.IsNullOrEmpty(Method) || !Method.All(IsTokenCharacter) ||
                string.Equals(Method, "CONNECT", StringComparison.OrdinalIgnoreCase))
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

            byte[] body = GetBodyBytes();
            if (body.Length == 0)
                return command.ToString();

            // Pass body bytes through a temporary file, not shell text; finally removes sensitive payloads.
            return "& {\r\n$xmatBodyFile = [System.IO.Path]::GetTempFileName()\r\ntry {\r\n" +
                $"[System.IO.File]::WriteAllBytes($xmatBodyFile, [Convert]::FromBase64String('{Convert.ToBase64String(body)}'))\r\n" +
                command + " --data-binary \"@$xmatBodyFile\"\r\n" +
                "} finally {\r\n[System.IO.File]::Delete($xmatBodyFile)\r\n}\r\n}";
        }

        private byte[] GetBodyBytes() =>
            BodyIsBase64 ? Convert.FromBase64String(BodyText ?? "") : StrictUtf8.GetBytes(BodyText ?? "");

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
                if (separator <= 0 || !line[..separator].All(IsTokenCharacter) ||
                    line[(separator + 1)..].Any(c => (char.IsControl(c) && c != '\t') || c == '\u007f'))
                {
                    throw new FormatException("Enter one valid 'Name: value' header per line.");
                }
                result.Add(new(line[..separator], line[(separator + 1)..].Trim()));
            }
            return result;
        }

        private static bool IsTokenCharacter(char c) =>
            char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c);

        private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
    }
}
