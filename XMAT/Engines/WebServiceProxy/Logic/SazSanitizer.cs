// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace XMAT.WebServiceCapture
{
    internal sealed class SanitizedExportSummary
    {
        internal int Sessions { get; set; }
        internal int RedactedValues { get; set; }
        internal int OmittedBodies { get; set; }
        internal int OmittedHeaders { get; set; }
        internal int WorkBudgetOmissions { get; set; }
    }

    internal readonly record struct SanitizedHttpMessage(string Headers, byte[] Body, bool BodyOmitted);

    internal sealed class SazSanitizer
    {
        internal const string Redacted = "[REDACTED]";
        internal const int MaximumBodyBytes = 4 * 1024 * 1024;
        internal const int MaximumStructuredDepth = 64;
        private const long MaximumInspectionWork = 64L * 1024 * 1024;
        private const int MaximumRememberedSecrets = 64;
        private readonly CancellationToken _cancellationToken;
        private readonly HashSet<string> _knownSecrets = new(StringComparer.Ordinal);
        private long _remainingWork;
        private Regex _knownSecretPattern;
        private bool _secretCapacityExceeded;
        private bool _unsafeInspection;
        private bool _workBudgetExceeded;
        private int _messageHeaderCount;
        private bool _discovering;
        private bool OpaqueSession => _secretCapacityExceeded || _unsafeInspection;
        private readonly record struct MessageInspection(long RemainingWork, bool OmitBody, int HeaderCount);
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(250);
        private const RegexOptions PatternOptions = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
        private static readonly Regex AssignmentPrefix = new(
            @"(?<name>[A-Za-z_][A-Za-z0-9_.-]*)[""']?[ \t]*(?<separator>[:=])[ \t]*",
            PatternOptions | RegexOptions.IgnoreCase, PatternTimeout);
        private static readonly Regex PrivateKey = new(
            @"-----BEGIN (?:[A-Z ]*PRIVATE KEY|PGP PRIVATE KEY BLOCK)-----[\s\S]*?(?:-----END (?:[A-Z ]*PRIVATE KEY|PGP PRIVATE KEY BLOCK)-----|\z)",
            PatternOptions, PatternTimeout);
        private static readonly Regex Url = new(
            @"(?:[A-Za-z][A-Za-z0-9+.-]*:)?//[^\s<>""']+", PatternOptions, PatternTimeout);
        private static readonly Regex UriAuthority = new(
            @"\A(?:[A-Za-z][A-Za-z0-9+.-]*:)?//", PatternOptions, PatternTimeout);
        private static readonly Regex SecretToken = new(
            @"(?<known>\b(?:Bearer|Basic)\s+[A-Za-z0-9_+/=.-]+|\bXBL3\.0[ \t]+x=[^;\s""<>]+;[A-Za-z0-9._+/=-]+|\b(?:AKIA|ASIA)[A-Z0-9]{16}\b|\bgh[pousr]_[A-Za-z0-9]{20,}\b|\bgithub_pat_[A-Za-z0-9_]{20,}\b|\bsk[-_](?:live_|test_)?[A-Za-z0-9_-]{20,}\b)|(?<jwt>\b[A-Za-z0-9_-]{12,}(?:\.[A-Za-z0-9_-]*){2,})",
            PatternOptions | RegexOptions.IgnoreCase, PatternTimeout);
        private static readonly Regex Email = new(
            @"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}",
            PatternOptions, PatternTimeout);
        private static readonly Regex PathIdentity = new(
            @"(?<name>xuids?|gt|gamertag)\((?<value>[^)]*)\)",
            PatternOptions | RegexOptions.IgnoreCase, PatternTimeout);
        private static readonly Regex PathSecret = new(
            @"(?<prefix>/(?:token|secret|password|api[-_]?key|session[-_]?id)/)[^/?#]+",
            PatternOptions | RegexOptions.IgnoreCase, PatternTimeout);
        private static readonly Regex ParameterSeparator = new(@"([&;])", PatternOptions, PatternTimeout);
        private static readonly string[] SensitiveNames =
        {
            "authorization", "authenticate", "authentication", "cookie", "password", "passwd", "passphrase", "secret", "token",
            "credential", "apikey", "subscriptionkey", "accountkey", "accesskey", "privatekey",
            "sharedkey", "functionkey", "functionskey", "encryptionkey", "connectionstring", "signature", "sessionid", "userid", "username",
            "email", "xuid", "gamertag", "deviceid", "codeverifier", "codechallenge", "authcode",
            "passcode", "securitycode", "verificationcode", "ticket", "devicecode", "usercode", "gamedisplayname", "appdisplayname"
        };
        private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "Forwarded", "X-Forwarded-For", "X-Real-IP", "X-Client-IP", "X-MS-Client-Principal"
        };
        private static readonly HashSet<string> PrivateJwkMembers = new(StringComparer.OrdinalIgnoreCase)
        {
            "d", "p", "q", "dp", "dq", "qi", "oth", "k"
        };
        private static readonly HashSet<string> SchemaNames = new(StringComparer.Ordinal)
        {
            "metadata", "properties", "attributes", "settings", "description", "configuration", "parameters"
        };

        internal SanitizedExportSummary Summary { get; } = new();

        internal SazSanitizer(CancellationToken cancellationToken = default)
        {
            _cancellationToken = cancellationToken;
        }

        internal SanitizedHttpMessage SanitizeMessage(
            string rawHeaders, byte[] body, bool request, string requestMethod = null)
        {
            if (request)
            {
                ResetSession();
            }
            var inspection = InspectMessage(rawHeaders, body, request, requestMethod);
            PrepareKnownSecrets();
            var before = (Summary.RedactedValues, Summary.OmittedBodies, Summary.OmittedHeaders, Summary.WorkBudgetOmissions);
            var result = OpaqueSession ? default : RenderMessage(rawHeaders, body, request, requestMethod, inspection);
            if (!OpaqueSession)
            {
                return result;
            }
            (Summary.RedactedValues, Summary.OmittedBodies, Summary.OmittedHeaders, Summary.WorkBudgetOmissions) = before;
            if (_workBudgetExceeded)
            {
                Summary.WorkBudgetOmissions++;
            }
            return OpaqueMessage(rawHeaders, body, request, requestMethod, inspection);
        }

        internal (SanitizedHttpMessage Request, SanitizedHttpMessage? Response) SanitizeSession(
            string requestHeaders, byte[] requestBody, string responseHeaders, byte[] responseBody, string method)
        {
            ResetSession();
            var request = InspectMessage(requestHeaders, requestBody, true, method);
            MessageInspection? response = string.IsNullOrEmpty(responseHeaders)
                ? null : InspectMessage(responseHeaders, responseBody, false, method);
            PrepareKnownSecrets();
            var before = (Summary.RedactedValues, Summary.OmittedBodies, Summary.OmittedHeaders, Summary.WorkBudgetOmissions);
            var renderedRequest = OpaqueSession ? default : RenderMessage(requestHeaders, requestBody, true, method, request);
            SanitizedHttpMessage? renderedResponse = !OpaqueSession && response is { } inspected
                ? RenderMessage(responseHeaders, responseBody, false, method, inspected) : null;
            if (!OpaqueSession)
            {
                return (renderedRequest, renderedResponse);
            }
            (Summary.RedactedValues, Summary.OmittedBodies, Summary.OmittedHeaders, Summary.WorkBudgetOmissions) = before;
            if (_workBudgetExceeded)
            {
                Summary.WorkBudgetOmissions++;
            }
            return (OpaqueMessage(requestHeaders, requestBody, true, method, request),
                response is { } omitted ? OpaqueMessage(responseHeaders, responseBody, false, method, omitted) : null);
        }

        private void ResetSession()
        {
            _knownSecrets.Clear();
            _knownSecretPattern = null;
            _secretCapacityExceeded = false;
            _unsafeInspection = false;
            _workBudgetExceeded = false;
        }

        private MessageInspection InspectMessage(string rawHeaders, byte[] body, bool request, string method)
        {
            _remainingWork = MaximumInspectionWork;
            _messageHeaderCount = 0;
            if (OpaqueSession)
            {
                return new(_remainingWork, body?.Length > 0, 0);
            }
            var before = (Summary.RedactedValues, Summary.OmittedBodies, Summary.OmittedHeaders, Summary.WorkBudgetOmissions);
            _discovering = true;
            try
            {
                // Reuse every detector without publishing its output or applying learned-value substitutions.
                var inspected = SanitizeMessageCore(rawHeaders, body, request, method);
                return new(_remainingWork, inspected.BodyOmitted, _messageHeaderCount);
            }
            catch (InvalidDataException) when (OpaqueSession)
            {
                return new(_remainingWork, body?.Length > 0, _messageHeaderCount);
            }
            catch (RegexMatchTimeoutException)
            {
                _unsafeInspection = true;
                return new(_remainingWork, body?.Length > 0, _messageHeaderCount);
            }
            finally
            {
                _discovering = false;
                (Summary.RedactedValues, Summary.OmittedBodies, Summary.OmittedHeaders, Summary.WorkBudgetOmissions) = before;
            }
        }

        private SanitizedHttpMessage RenderMessage(
            string rawHeaders, byte[] body, bool request, string method, MessageInspection inspection)
        {
            _remainingWork = inspection.RemainingWork;
            try
            {
                return SanitizeMessageCore(rawHeaders, body, request, method, inspection.OmitBody);
            }
            catch (InvalidDataException) when (OpaqueSession)
            {
                return default;
            }
            catch (RegexMatchTimeoutException)
            {
                _unsafeInspection = true;
                return default;
            }
        }

        private SanitizedHttpMessage OpaqueMessage(
            string rawHeaders, byte[] body, bool request, string method, MessageInspection inspection)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            using var reader = new StringReader(rawHeaders ?? string.Empty);
            var firstLine = reader.ReadLine() ?? string.Empty;
            var status = request ? 0 : ParseStatusCode(firstLine);
            var output = new StringBuilder(OpaqueStartLine(firstLine, request, status)).Append("\r\n");
            if (request)
            {
                output.Append("Host: redacted.invalid\r\n");
            }
            var forbiddenLength = !request && (status < 200 || status == 204 ||
                (status is >= 200 and < 300 && string.Equals(method, "CONNECT", StringComparison.OrdinalIgnoreCase)));
            if (!forbiddenLength)
            {
                output.Append("Content-Length: 0\r\n");
            }
            output.Append("\r\n");
            var omitted = body?.Length > 0;
            if (omitted)
            {
                Summary.OmittedBodies++;
            }
            Summary.OmittedHeaders += Math.Max(inspection.HeaderCount, 1);
            return new(output.ToString(), Array.Empty<byte>(), omitted);
        }

        private SanitizedHttpMessage SanitizeMessageCore(
            string rawHeaders, byte[] body, bool request, string requestMethod,
            bool omitBody = false)
        {
            _messageHeaderCount = 0;
            SpendWork(rawHeaders?.Length ?? 0);
            using var reader = new StringReader(rawHeaders ?? string.Empty);
            var firstLine = reader.ReadLine() ?? string.Empty;
            var headers = new List<KeyValuePair<string, string>>();
            var canContinue = false;
            string line;
            while (!string.IsNullOrEmpty(line = reader.ReadLine()))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (line[0] == ' ' || line[0] == '\t')
                {
                    if (canContinue && headers.Count > 0)
                    {
                        var previous = headers[^1];
                        SpendWork(previous.Value.Length + line.Length);
                        headers[^1] = new(previous.Key, previous.Value + " " + line.Trim());
                    }
                    else
                    {
                        Summary.OmittedHeaders++;
                    }
                    continue;
                }
                _messageHeaderCount++;
                var colon = line.IndexOf(':');
                if (colon <= 0 || !line.Substring(0, colon).All(IsHeaderNameCharacter))
                {
                    Summary.OmittedHeaders++;
                    canContinue = false;
                    continue;
                }
                headers.Add(new(line.Substring(0, colon), line.Substring(colon + 1).Trim()));
                canContinue = true;
            }

            var status = request ? 0 : ParseStatusCode(firstLine);
            var startLine = SanitizeStartLine(firstLine, request);
            var headOrNotModified = !request &&
                (status == 304 || string.Equals(requestMethod, "HEAD", StringComparison.OrdinalIgnoreCase));
            var successfulConnect = !request && status is >= 200 and < 300 &&
                string.Equals(requestMethod, "CONNECT", StringComparison.OrdinalIgnoreCase);
            var forbiddenLength = !request && (status < 200 || status == 204 || successfulConnect);
            var bodyless = forbiddenLength || headOrNotModified || (!request && status == 205);
            var omissionsBefore = Summary.OmittedBodies;
            var originalBody = body ?? Array.Empty<byte>();
            string contentType = null;
            byte[] outputBody;
            if (bodyless)
            {
                outputBody = Array.Empty<byte>();
                if (originalBody.Length > 0)
                {
                    Summary.OmittedBodies++;
                }
            }
            else if (omitBody)
            {
                outputBody = Array.Empty<byte>();
                if (originalBody.Length > 0)
                {
                    Summary.OmittedBodies++;
                }
            }
            else
            {
                outputBody = SanitizeBody(originalBody, headers, out contentType);
            }
            if (OpaqueSession)
            {
                return new(string.Empty, Array.Empty<byte>(), originalBody.Length > 0);
            }
            var bodyChanged = originalBody.Length > 0;
            var output = new StringBuilder(startLine).Append("\r\n");
            foreach (var header in headers)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.Equals("Trailer", StringComparison.OrdinalIgnoreCase) ||
                    (bodyChanged &&
                     (header.Key.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase) ||
                      header.Key.Equals("Content-MD5", StringComparison.OrdinalIgnoreCase) ||
                      header.Key.Equals("Digest", StringComparison.OrdinalIgnoreCase) ||
                      header.Key.Equals("Content-Digest", StringComparison.OrdinalIgnoreCase) ||
                      header.Key.Equals("Repr-Digest", StringComparison.OrdinalIgnoreCase) ||
                      header.Key.Equals("ETag", StringComparison.OrdinalIgnoreCase))))
                {
                    continue;
                }
                var rawValue = header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) && contentType != null
                    ? contentType : header.Value;
                var sensitive = IsSensitiveName(header.Key) || SensitiveHeaders.Contains(header.Key);
                if (_discovering && sensitive)
                {
                    RememberHeaderValue(header.Key, rawValue);
                }
                var value = sensitive
                    ? Redact(rawValue, false)
                    : header.Key.Equals("Location", StringComparison.OrdinalIgnoreCase) ||
                      header.Key.Equals("Content-Location", StringComparison.OrdinalIgnoreCase) ||
                      header.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase)
                        ? SanitizeUri(rawValue, 0)
                        : SanitizeText(rawValue, 0,
                            !header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) &&
                            !header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase));
                if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) &&
                    !MediaTypeHeaderValue.TryParse(value, out _))
                {
                    Summary.OmittedHeaders++;
                    continue;
                }
                output.Append(header.Key).Append(": ").Append(value).Append("\r\n");
            }
            if (!forbiddenLength)
            {
                string length;
                if (headOrNotModified)
                {
                    var lengths = headers.Where(header => header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                        .Select(header => header.Value).Distinct(StringComparer.Ordinal).ToArray();
                    length = lengths.Length == 1 && lengths[0].Length > 0 && lengths[0].All(char.IsAsciiDigit)
                        ? lengths[0] : null;
                }
                else
                {
                    length = outputBody.Length.ToString(CultureInfo.InvariantCulture);
                }
                if (length != null)
                {
                    output.Append("Content-Length: ").Append(length).Append("\r\n");
                }
            }
            output.Append("\r\n");
            return new(output.ToString(), outputBody, Summary.OmittedBodies != omissionsBefore);
        }

        private byte[] SanitizeBody(
            byte[] body, List<KeyValuePair<string, string>> headers, out string contentType)
        {
            contentType = null;
            if (body.Length == 0)
            {
                return body;
            }
            var redactionsBefore = Summary.RedactedValues;
            var workBefore = _remainingWork;
            try
            {
                if (body.Length > MaximumBodyBytes || _secretCapacityExceeded)
                {
                    throw new InvalidDataException();
                }
                var decoded = NormalizeTransferBody(body, headers);
                var encodings = headers.Where(header => header.Key.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(header => header.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    .ToArray();
                if (encodings.Length > 3)
                {
                    throw new InvalidDataException();
                }
                foreach (var encoding in encodings.Reverse())
                {
                    decoded = Decompress(decoded, encoding);
                }
                if (decoded.Length == 0)
                {
                    return Array.Empty<byte>();
                }
                var rawType = headers.FirstOrDefault(
                    header => header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value;
                if (!MediaTypeHeaderValue.TryParse(rawType, out var type))
                {
                    throw new InvalidDataException();
                }
                var encodingName = type.CharSet?.Trim('"', '\'');
                var textEncoding = string.IsNullOrEmpty(encodingName) ? StrictUtf8 :
                    Encoding.GetEncoding(encodingName, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                var offset = 0;
                if (decoded.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }))
                {
                    textEncoding = new UTF32Encoding(false, false, true);
                    offset = 4;
                }
                else if (decoded.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }))
                {
                    textEncoding = new UTF32Encoding(true, false, true);
                    offset = 4;
                }
                else if (decoded.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
                {
                    textEncoding = StrictUtf8;
                    offset = 3;
                }
                else if (decoded.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
                {
                    textEncoding = new UnicodeEncoding(false, false, true);
                    offset = 2;
                }
                else if (decoded.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
                {
                    textEncoding = new UnicodeEncoding(true, false, true);
                    offset = 2;
                }
                var text = textEncoding.GetString(decoded, offset, decoded.Length - offset);
                if (text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
                {
                    throw new InvalidDataException();
                }
                var mediaType = type.MediaType.ToLowerInvariant();
                if (mediaType == "text/html" || mediaType.EndsWith("xhtml+xml", StringComparison.Ordinal))
                {
                    throw new InvalidDataException();
                }
                string sanitized;
                if (mediaType == "application/json" || mediaType.EndsWith("+json", StringComparison.Ordinal))
                {
                    using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = MaximumStructuredDepth });
                    if (_discovering)
                    {
                        CollectJsonSecrets(document.RootElement);
                    }
                    using var buffer = new MemoryStream();
                    using (var writer = new Utf8JsonWriter(buffer))
                    {
                        WriteJson(writer, document.RootElement);
                    }
                    sanitized = StrictUtf8.GetString(buffer.ToArray());
                }
                else if (mediaType is "application/xml" or "text/xml" || mediaType.EndsWith("+xml", StringComparison.Ordinal))
                {
                    var settings = new XmlReaderSettings
                    {
                        DtdProcessing = DtdProcessing.Prohibit,
                        XmlResolver = null,
                        MaxCharactersInDocument = MaximumBodyBytes
                    };
                    using (var depthReader = XmlReader.Create(new StringReader(text), settings))
                    {
                        while (depthReader.Read())
                        {
                            SpendWork(1);
                            if (depthReader.Depth >= MaximumStructuredDepth)
                            {
                                throw new InvalidDataException();
                            }
                            if (depthReader.NamespaceURI == "http://www.w3.org/1999/xhtml")
                            {
                                throw new InvalidDataException();
                            }
                        }
                    }
                    using var xmlReader = XmlReader.Create(new StringReader(text), settings);
                    var document = XDocument.Load(xmlReader, LoadOptions.PreserveWhitespace);
                    if (document.Root?.Name.LocalName.Equals("html", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        throw new InvalidDataException();
                    }
                    if (_discovering)
                    {
                        CollectXmlSecrets(document.Root);
                    }
                    SanitizeXmlNamespaces(document);
                    SanitizeXml(document.Root);
                    document.DescendantNodes().OfType<XComment>().Remove();
                    document.DescendantNodes().OfType<XProcessingInstruction>().Remove();
                    document.Declaration = null;
                    sanitized = document.ToString(SaveOptions.DisableFormatting);
                }
                else if (mediaType == "application/x-www-form-urlencoded")
                {
                    sanitized = SanitizeParameters(text, 0);
                }
                else if (mediaType == "text/plain")
                {
                    sanitized = SanitizeText(text, 0);
                }
                else
                {
                    throw new InvalidDataException();
                }
                type.CharSet = "utf-8";
                contentType = type.ToString();
                return StrictUtf8.GetBytes(sanitized);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or
                ArgumentException or NotSupportedException or InvalidOperationException or JsonException or
                XmlException or RegexMatchTimeoutException)
            {
                Summary.RedactedValues = redactionsBefore;
                if (exception is RegexMatchTimeoutException)
                {
                    _unsafeInspection = true;
                }
                if (_remainingWork < 0)
                {
                    Summary.WorkBudgetOmissions++;
                }
                _remainingWork = workBefore;
                Summary.OmittedBodies++;
                return Array.Empty<byte>();
            }
        }

        private byte[] NormalizeTransferBody(byte[] body, List<KeyValuePair<string, string>> headers)
        {
            var codings = headers.Where(header => header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                .SelectMany(header => header.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToArray();
            if (codings.Length == 0)
            {
                return body;
            }
            SpendWork(body.Length);
            if (!codings[^1].Equals("chunked", StringComparison.OrdinalIgnoreCase) ||
                codings.Any(coding => !coding.Equals("chunked", StringComparison.OrdinalIgnoreCase) &&
                    !coding.Equals("identity", StringComparison.OrdinalIgnoreCase)) ||
                codings.Count(coding => coding.Equals("chunked", StringComparison.OrdinalIgnoreCase)) != 1)
            {
                throw new InvalidDataException();
            }
            var newline = body.AsSpan().IndexOf((byte)10);
            if (newline < 0)
            {
                return body;
            }
            var firstLine = Encoding.ASCII.GetString(body, 0, newline).TrimEnd('\r');
            var extension = firstLine.IndexOf(';');
            var sizeText = extension < 0 ? firstLine : firstLine.Substring(0, extension);
            if (sizeText.Length == 0 || !sizeText.Trim().All(Uri.IsHexDigit))
            {
                var contentType = headers.FirstOrDefault(header =>
                    header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value;
                if (MediaTypeHeaderValue.TryParse(contentType, out var type) &&
                    (type.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
                     type.MediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
                {
                    // JSON parsing rejects malformed framing and preserves already-dechunked structured captures.
                    return body;
                }
                if (HasChunkFraming(body))
                {
                    throw new InvalidDataException();
                }
                return body;
            }

            using var output = new MemoryStream();
            var position = 0;
            while (true)
            {
                SpendWork(1);
                var lineEnd = body.AsSpan(position).IndexOf("\r\n"u8);
                if (lineEnd < 0 || lineEnd > 8192)
                {
                    throw new InvalidDataException();
                }
                var line = Encoding.ASCII.GetString(body, position, lineEnd);
                position += lineEnd + 2;
                extension = line.IndexOf(';');
                sizeText = extension < 0 ? line : line.Substring(0, extension);
                if (!int.TryParse(sizeText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var size) ||
                    size < 0 || size > body.Length - position)
                {
                    throw new InvalidDataException();
                }
                if (size == 0)
                {
                    while (true)
                    {
                        SpendWork(1);
                        lineEnd = body.AsSpan(position).IndexOf("\r\n"u8);
                        if (lineEnd < 0 || lineEnd > 8192)
                        {
                            throw new InvalidDataException();
                        }
                        if (lineEnd == 0)
                        {
                            if (position + 2 != body.Length)
                            {
                                throw new InvalidDataException();
                            }
                            return output.ToArray();
                        }
                        var trailer = body.AsSpan(position, lineEnd);
                        if (trailer.IndexOf((byte)':') <= 0)
                        {
                            throw new InvalidDataException();
                        }
                        position += lineEnd + 2;
                    }
                }
                if (output.Length + size > MaximumBodyBytes || position + size + 2 > body.Length ||
                    body[position + size] != 13 || body[position + size + 1] != 10)
                {
                    throw new InvalidDataException();
                }
                SpendWork(size);
                output.Write(body, position, size);
                position += size + 2;
            }
        }

        private bool HasChunkFraming(ReadOnlySpan<byte> body)
        {
            while (!body.IsEmpty)
            {
                var newline = body.IndexOf((byte)'\n');
                var line = newline < 0 ? body : body.Slice(0, newline);
                if (!line.IsEmpty && line[^1] == (byte)'\r')
                {
                    line = line.Slice(0, line.Length - 1);
                }
                SpendWork(line.Length);
                var extension = line.IndexOf((byte)';');
                var size = extension < 0 ? line : line.Slice(0, extension);
                while (!size.IsEmpty && size[0] is (byte)' ' or (byte)'\t')
                {
                    size = size.Slice(1);
                }
                while (!size.IsEmpty && size[^1] is (byte)' ' or (byte)'\t')
                {
                    size = size.Slice(0, size.Length - 1);
                }
                if (!size.IsEmpty && (size.IndexOfAnyExcept((byte)'0') < 0 ||
                    (size.TrimStart((byte)'0').Length <= 8 && int.TryParse(Encoding.ASCII.GetString(size.TrimStart((byte)'0')), NumberStyles.AllowHexSpecifier,
                        CultureInfo.InvariantCulture, out _))))
                {
                    return true;
                }
                if (newline < 0)
                {
                    return false;
                }
                body = body.Slice(newline + 1);
            }
            return false;
        }

        private byte[] Decompress(byte[] body, string encoding)
        {
            if (encoding.Equals("identity", StringComparison.OrdinalIgnoreCase))
            {
                return body;
            }
            if (encoding.Equals("deflate", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return DecodeCompressedBody(body, "zlib");
                }
                catch (InvalidDataException)
                {
                    // Some older servers send raw deflate instead of HTTP's zlib-wrapped form.
                    return DecodeCompressedBody(body, "raw-deflate");
                }
            }
            return DecodeCompressedBody(body, encoding.ToLowerInvariant());
        }

        private byte[] DecodeCompressedBody(byte[] body, string encoding)
        {
            using var input = new MemoryStream(body);
            using Stream decoder = encoding switch
            {
                "gzip" => new GZipStream(input, CompressionMode.Decompress),
                "zlib" => new ZLibStream(input, CompressionMode.Decompress),
                "raw-deflate" => new DeflateStream(input, CompressionMode.Decompress),
                "br" => new BrotliStream(input, CompressionMode.Decompress),
                _ => throw new InvalidDataException()
            };
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = decoder.Read(buffer, 0, buffer.Length)) != 0)
            {
                SpendWork(read);
                if (output.Length + read > MaximumBodyBytes)
                {
                    throw new InvalidDataException();
                }
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }

        private static (bool NamedSecret, bool Jwk, bool ProfileUser) JsonRules(JsonElement element, string context)
        {
            return (element.EnumerateObject().Any(property =>
                    IsDescriptorName(property.Name) && property.Value.ValueKind == JsonValueKind.String &&
                    IsSensitiveName(property.Value.GetString())),
                element.EnumerateObject().Any(property => property.Name.Equals("kty", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String),
                string.Equals(context, "profileUsers", StringComparison.OrdinalIgnoreCase));
        }

        private static bool SensitiveJsonProperty(string name, (bool NamedSecret, bool Jwk, bool ProfileUser) rules) =>
            IsSensitiveName(name) || (rules.Jwk && PrivateJwkMembers.Contains(name)) ||
            (rules.NamedSecret && name.Equals("value", StringComparison.OrdinalIgnoreCase)) ||
            (rules.ProfileUser && name.Equals("id", StringComparison.OrdinalIgnoreCase));

        private void CollectJsonSecrets(JsonElement element, string context = null, bool sensitive = false)
        {
            SpendWork(1);
            if (element.ValueKind == JsonValueKind.Object)
            {
                var rules = JsonRules(element, context);
                foreach (var property in element.EnumerateObject())
                {
                    if (!sensitive && IsDescriptorName(property.Name) && property.Value.ValueKind == JsonValueKind.String &&
                        IsSensitiveName(property.Value.GetString()))
                    {
                        continue;
                    }
                    CollectJsonSecrets(property.Value, property.Name, sensitive || SensitiveJsonProperty(property.Name, rules));
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in element.EnumerateArray())
                {
                    CollectJsonSecrets(child, context, sensitive);
                }
            }
            else if (sensitive && element.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            {
                RememberCredential(element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText());
            }
        }

        private void RememberCredential(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }
            RememberSecret(value);
            var space = value.AsSpan().IndexOfAny(' ', '\t');
            if (space > 0 &&
                (value.AsSpan(0, space).Equals("Bearer", StringComparison.OrdinalIgnoreCase) ||
                 value.AsSpan(0, space).Equals("Basic", StringComparison.OrdinalIgnoreCase)))
            {
                var parameter = value.Substring(space + 1).Trim();
                RememberSecret(parameter);
                if (value.AsSpan(0, space).Equals("Basic", StringComparison.OrdinalIgnoreCase))
                {
                    var bytes = new byte[parameter.Length];
                    if (Convert.TryFromBase64String(parameter, bytes, out var length))
                    {
                        string decoded;
                        try
                        {
                            decoded = StrictUtf8.GetString(bytes, 0, length);
                        }
                        catch (DecoderFallbackException)
                        {
                            decoded = Encoding.Latin1.GetString(bytes, 0, length);
                        }
                        RememberUserInfo(decoded);
                        var latin = Encoding.Latin1.GetString(bytes, 0, length);
                        if (latin != decoded)
                        {
                            RememberUserInfo(latin);
                        }
                    }
                }
            }
            else if (value.StartsWith("XBL3.0 ", StringComparison.OrdinalIgnoreCase))
            {
                var separator = value.IndexOf(';');
                if (separator > 0)
                {
                    RememberSecret(value.Substring(separator + 1));
                }
            }
        }

        private void RememberUserInfo(string value)
        {
            RememberSecret(value);
            var colon = value.IndexOf(':');
            if (colon >= 0)
            {
                RememberSecret(value.Substring(0, colon));
                RememberSecret(value.Substring(colon + 1));
            }
        }

        private void RememberHeaderValue(string name, string value)
        {
            if (name.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
            {
                var setCookie = name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase);
                foreach (var segment in CookieSegments(value, setCookie))
                {
                    var cookie = segment;
                    if (setCookie)
                    {
                        var semicolon = cookie.IndexOf(';');
                        if (semicolon >= 0)
                        {
                            cookie = cookie.Substring(0, semicolon);
                        }
                    }
                    var equals = cookie.IndexOf('=');
                    var credential = equals >= 0 ? cookie.Substring(equals + 1) : cookie;
                    RememberCredential(credential.Trim(' ', '\t', '"'));
                }
            }
            else
            {
                RememberCredential(value);
                if ((name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)) &&
                    AuthenticationHeaderValue.TryParse(value, out var authorization) &&
                    !string.IsNullOrEmpty(authorization.Parameter))
                {
                    RememberSecret(authorization.Parameter.Trim());
                }
            }
        }

        private IEnumerable<string> CookieSegments(string value, bool setCookie)
        {
            var start = 0;
            var quoted = false;
            for (var index = 0; index < value.Length; index++)
            {
                if ((index & 255) == 0)
                {
                    SpendWork(256);
                }
                if (quoted && value[index] == '\\' && index + 1 < value.Length)
                {
                    index++;
                    continue;
                }
                if (value[index] == '"')
                {
                    quoted = !quoted;
                    continue;
                }
                if (!quoted && ((!setCookie && value[index] == ';') ||
                    (value[index] == ',' && CookieStartsAt(value, index + 1))))
                {
                    if (index > start)
                    {
                        yield return value.Substring(start, index - start).Trim();
                    }
                    start = index + 1;
                }
            }
            if (start < value.Length)
            {
                yield return value.Substring(start).Trim();
            }
        }

        private static bool CookieStartsAt(string value, int start)
        {
            while (start < value.Length && char.IsWhiteSpace(value[start]))
            {
                start++;
            }
            var name = start;
            while (start < value.Length && IsHeaderNameCharacter(value[start]))
            {
                start++;
            }
            return start > name && start < value.Length && value[start] == '=';
        }

        private void RememberSecret(string value)
        {
            if (string.IsNullOrEmpty(value) || value == Redacted)
            {
                return;
            }
            SpendWork(value.Length);
            if (value.Length > 4096 || (_knownSecrets.Count >= MaximumRememberedSecrets && !_knownSecrets.Contains(value)))
            {
                _secretCapacityExceeded = true;
                throw new InvalidDataException();
            }
            _knownSecrets.Add(value);
        }

        private void PrepareKnownSecrets()
        {
            if (_knownSecrets.Count == 0)
            {
                return;
            }
            var alternatives = _knownSecrets.OrderByDescending(value => value.Length).Select(Regex.Escape);
            _knownSecretPattern = new Regex(Regex.Escape(Redacted) +
                @"|(?<![\p{L}\p{N}\p{M}_])(?:" + string.Join("|", alternatives) + @")(?![\p{L}\p{N}\p{M}_])",
                RegexOptions.CultureInvariant, PatternTimeout);
        }

        private void CollectXmlSecrets(XElement element, bool sensitive = false)
        {
            if (element == null)
            {
                return;
            }
            SpendWork(1);
            var namedSecret = element.Attributes().Any(attribute =>
                    IsDescriptorName(attribute.Name.LocalName) && IsSensitiveName(attribute.Value)) ||
                element.Elements().Any(child => IsDescriptorName(child.Name.LocalName) &&
                    !child.HasElements && IsSensitiveName(child.Value));
            var passwordInput = IsPasswordInput(element);
            var sensitiveElement = sensitive || IsSensitiveName(element.Name.LocalName) || namedSecret;
            if (sensitiveElement)
            {
                RememberXmlCredential(element.Value);
                if (element.HasElements)
                {
                    foreach (var text in element.Nodes().OfType<XText>())
                    {
                        RememberXmlCredential(text.Value);
                    }
                }
            }
            foreach (var attribute in element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration))
            {
                if (attribute.Name == XNamespace.Xml + "space" ||
                    (!sensitive && IsDescriptorName(attribute.Name.LocalName) && IsSensitiveName(attribute.Value)))
                {
                    continue;
                }
                if (sensitiveElement || IsSensitiveName(attribute.Name.LocalName) ||
                    (passwordInput && attribute.Name.LocalName.Equals("value", StringComparison.OrdinalIgnoreCase)))
                {
                    RememberXmlCredential(attribute.Value);
                }
            }
            foreach (var child in element.Elements())
            {
                if (!sensitive && namedSecret && IsDescriptorName(child.Name.LocalName) &&
                    !child.HasElements && IsSensitiveName(child.Value))
                {
                    continue;
                }
                CollectXmlSecrets(child, sensitiveElement);
            }
        }

        private void RememberXmlCredential(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }
            RememberCredential(value);
            var trimmed = value.Trim();
            if (trimmed != value)
            {
                RememberCredential(trimmed);
            }
        }

        private void SanitizeXmlNamespaces(XDocument document)
        {
            var namespaces = new Dictionary<XNamespace, XNamespace>();
            foreach (var declaration in document.Descendants().Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            {
                SpendWork(declaration.Value.Length);
                var original = XNamespace.Get(declaration.Value);
                if (original == XNamespace.Xml || original == XNamespace.Xmlns || namespaces.ContainsKey(original))
                {
                    continue;
                }
                var sanitized = SanitizeText(declaration.Value, 0);
                namespaces.Add(original, sanitized == declaration.Value ? original :
                    XNamespace.Get("urn:xmat:sanitized-namespace:" + (namespaces.Count + 1).ToString(CultureInfo.InvariantCulture)));
            }
            foreach (var element in document.Descendants())
            {
                SpendWork(1);
                if (namespaces.TryGetValue(element.Name.Namespace, out var replacement))
                {
                    element.Name = replacement + element.Name.LocalName;
                }
                foreach (var attribute in element.Attributes().ToArray())
                {
                    if (attribute.IsNamespaceDeclaration)
                    {
                        if (namespaces.TryGetValue(XNamespace.Get(attribute.Value), out replacement))
                        {
                            attribute.Value = replacement.NamespaceName;
                        }
                    }
                    else if (namespaces.TryGetValue(attribute.Name.Namespace, out replacement) &&
                        replacement != attribute.Name.Namespace)
                    {
                        var renamed = new XAttribute(replacement + attribute.Name.LocalName, attribute.Value);
                        attribute.Remove();
                        element.Add(renamed);
                    }
                }
            }
        }

        private void WriteJson(Utf8JsonWriter writer, JsonElement element, string context = null)
        {
            SpendWork(1);
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var rules = JsonRules(element, context);
                    var reserved = element.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
                    var emitted = new HashSet<string>(StringComparer.Ordinal);
                    var nextKey = 0;
                    writer.WriteStartObject();
                    foreach (var property in element.EnumerateObject())
                    {
                        string name;
                        if (ContainsLearnedName(property.Name))
                        {
                            _ = Redact(property.Name, false);
                            name = UniqueRedactedKey(reserved, emitted, ref nextKey);
                        }
                        else
                        {
                            name = SanitizeText(property.Name, 0, false);
                            if (name != property.Name && (reserved.Contains(name) || emitted.Contains(name)))
                            {
                                name = UniqueRedactedKey(reserved, emitted, ref nextKey);
                            }
                        }
                        emitted.Add(name);
                        writer.WritePropertyName(name);
                        if (SensitiveJsonProperty(property.Name, rules))
                        {
                            if (property.Value.ValueKind == JsonValueKind.Null)
                            {
                                writer.WriteNullValue();
                            }
                            else
                            {
                                writer.WriteStringValue(Redact(property.Value.GetRawText(), false));
                            }
                        }
                        else
                        {
                            WriteJson(writer, property.Value, property.Name);
                        }
                    }
                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var child in element.EnumerateArray())
                    {
                        WriteJson(writer, child, context);
                    }
                    writer.WriteEndArray();
                    break;
                case JsonValueKind.String:
                    writer.WriteStringValue(SanitizeText(element.GetString(), 0));
                    break;
                case JsonValueKind.Number:
                    var number = element.GetRawText();
                    if (!_discovering && _knownSecrets.Contains(number))
                    {
                        writer.WriteStringValue(Redact(number, false));
                    }
                    else
                    {
                        element.WriteTo(writer);
                    }
                    break;
                default:
                    element.WriteTo(writer);
                    break;
            }
        }

        private bool ContainsLearnedName(string name)
        {
            if (_discovering || _knownSecretPattern == null || IsSchemaName(name))
            {
                return false;
            }
            SpendWork(name.Length);
            foreach (Match match in _knownSecretPattern.Matches(name))
            {
                var value = match.Value;
                if (value != Redacted && !IsSchemaName(value) &&
                    (value.Length >= 8 || (value.Length >= 4 && value.All(char.IsAsciiDigit))))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsSchemaName(string name)
        {
            var normalized = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            return SchemaNames.Contains(normalized) || SensitiveNames.Contains(normalized, StringComparer.Ordinal) ||
                normalized is "id" or "data" or "members" or "value" or "values" or "type" or "name" or "key" or "keys" or
                    "auth" or "sig" or "sas" or "pwd" or "user" or "otp" or "pin" or "nonce";
        }

        private string UniqueRedactedKey(HashSet<string> reserved, HashSet<string> emitted, ref int next)
        {
            string name;
            do
            {
                SpendWork(1);
                name = "[REDACTED-KEY-" + (++next).ToString(CultureInfo.InvariantCulture) + "]";
            }
            while (reserved.Contains(name) || emitted.Contains(name));
            return name;
        }

        private void SanitizeXml(XElement element)
        {
            if (element == null)
            {
                return;
            }
            SpendWork(1);
            var namedSecret = element.Attributes().Any(attribute =>
                IsDescriptorName(attribute.Name.LocalName) && IsSensitiveName(attribute.Value)) ||
                element.Elements().Any(child => IsDescriptorName(child.Name.LocalName) &&
                    !child.HasElements && IsSensitiveName(child.Value));
            var sensitiveElement = IsSensitiveName(element.Name.LocalName) || namedSecret;
            var passwordInput = IsPasswordInput(element);
            foreach (var attribute in element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration))
            {
                if (attribute.Name == XNamespace.Xml + "space")
                {
                    continue;
                }
                attribute.Value = sensitiveElement || IsSensitiveName(attribute.Name.LocalName) ||
                    (passwordInput && attribute.Name.LocalName.Equals("value", StringComparison.OrdinalIgnoreCase))
                    ? Redact(attribute.Value, false) : SanitizeText(attribute.Value, 0);
            }
            if (sensitiveElement)
            {
                element.Value = Redact(element.Value, false);
                return;
            }
            foreach (var child in element.Elements().ToArray())
            {
                SanitizeXml(child);
            }
            foreach (var text in element.Nodes().OfType<XText>())
            {
                text.Value = SanitizeText(text.Value, 0);
            }
            element.Nodes().OfType<XComment>().Remove();
            element.Nodes().OfType<XProcessingInstruction>().Remove();
        }

        private static string OpaqueStartLine(string line, bool request, int status)
        {
            if (!request)
            {
                return "HTTP/1.1 " + status.ToString(CultureInfo.InvariantCulture) + " " + Redacted;
            }
            var space = line.IndexOf(' ');
            if (space <= 0)
            {
                throw new InvalidDataException(Localization.GetLocalizedString("SANITIZED_EXPORT_INVALID_HTTP"));
            }
            var method = line.Substring(0, space);
            return method + " " +
                (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase) ? "redacted.invalid:443" : "/%5BREDACTED%5D") +
                " HTTP/1.1";
        }

        private string SanitizeStartLine(string line, bool request)
        {
            var firstSpace = line.IndexOf(' ');
            var lastSpace = line.LastIndexOf(' ');
            if (request)
            {
                if (firstSpace <= 0 || lastSpace <= firstSpace ||
                    !line.Substring(lastSpace + 1).StartsWith("HTTP/", StringComparison.Ordinal))
                {
                    throw new InvalidDataException(Localization.GetLocalizedString("SANITIZED_EXPORT_INVALID_HTTP"));
                }
                var target = line.Substring(firstSpace + 1, lastSpace - firstSpace - 1);
                var portSeparator = target.LastIndexOf(':');
                if (line.AsSpan(0, firstSpace).Equals("CONNECT", StringComparison.OrdinalIgnoreCase) &&
                    portSeparator > 0 &&
                    ushort.TryParse(target.AsSpan(portSeparator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port) &&
                    port > 0 && Uri.CheckHostName(target.Substring(0, portSeparator).Trim('[', ']')) != UriHostNameType.Unknown)
                {
                    return line;
                }
                return string.Concat(line.AsSpan(0, firstSpace + 1),
                    SanitizeUri(target, 0).AsSpan(),
                    line.AsSpan(lastSpace));
            }
            if (!line.StartsWith("HTTP/", StringComparison.Ordinal) || firstSpace < 0)
            {
                throw new InvalidDataException(Localization.GetLocalizedString("SANITIZED_EXPORT_INVALID_HTTP"));
            }
            var statusStart = firstSpace + 1;
            while (statusStart < line.Length && line[statusStart] == ' ')
            {
                statusStart++;
            }
            var reasonStart = line.IndexOf(' ', statusStart);
            return reasonStart < 0 ? line :
                string.Concat(line.AsSpan(0, reasonStart + 1), SanitizeText(line.Substring(reasonStart + 1), 0).AsSpan());
        }

        private static int ParseStatusCode(string line)
        {
            var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[1].Length != 3 ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var status) ||
                status is < 100 or > 999)
            {
                throw new InvalidDataException(Localization.GetLocalizedString("SANITIZED_EXPORT_INVALID_HTTP"));
            }
            return status;
        }

        private string SanitizeUri(string value, int depth, bool replaceKnown = true)
        {
            if (depth > 4)
            {
                return Redact(value);
            }
            var authority = UriAuthority.Match(value);
            var authorityStart = authority.Success ? authority.Length : -1;
            if (authorityStart >= 0)
            {
                var authorityEnd = value.IndexOfAny(new[] { '/', '?', '#' }, authorityStart);
                if (authorityEnd < 0)
                {
                    authorityEnd = value.Length;
                }
                var originalAuthority = value.Substring(authorityStart, authorityEnd - authorityStart);
                var delimiter = originalAuthority.AsSpan().IndexOfAny(';', ',', '&');
                if (delimiter >= 0)
                {
                    var originalSuffix = originalAuthority.Substring(delimiter);
                    var sanitizedSuffix = SanitizeAssignments(originalSuffix, true);
                    if (sanitizedSuffix != originalSuffix)
                    {
                        var sanitizedAuthority = originalAuthority.LastIndexOf('@') > delimiter
                            ? "redacted.invalid"
                            : string.Concat(originalAuthority.AsSpan(0, delimiter), sanitizedSuffix.AsSpan());
                        value = string.Concat(value.AsSpan(0, authorityStart),
                            sanitizedAuthority.AsSpan(), value.AsSpan(authorityEnd));
                        authorityEnd = authorityStart + sanitizedAuthority.Length;
                    }
                }
                var at = value.LastIndexOf('@', authorityEnd - 1, authorityEnd - authorityStart);
                if (at >= authorityStart)
                {
                    if (at > authorityStart)
                    {
                        var userInfo = value.Substring(authorityStart, at - authorityStart);
                        if (_discovering)
                        {
                            RememberUserInfo(userInfo);
                            try
                            {
                                var (decoded, _) = DecodeRepeated(userInfo, false);
                                if (decoded != userInfo)
                                {
                                    RememberUserInfo(decoded);
                                }
                            }
                            catch (DecoderFallbackException)
                            {
                            }
                        }
                        _ = Redact(userInfo, false);
                    }
                    value = string.Concat(value.AsSpan(0, authorityStart), value.AsSpan(at + 1));
                }
            }
            var fragment = value.IndexOf('#');
            if (fragment >= 0 && fragment < value.Length - 1)
            {
                value = string.Concat(value.AsSpan(0, fragment + 1),
                    Uri.EscapeDataString(Redact(value.Substring(fragment + 1))).AsSpan());
            }
            var question = value.IndexOf('?');
            var prefix = question < 0 ? value : value.Substring(0, question);
            var origin = UriAuthority.Match(prefix);
            var pathStart = origin.Success ? prefix.IndexOf('/', origin.Length) : 0;
            if (pathStart < 0)
            {
                pathStart = prefix.Length;
            }
            if (!_discovering && replaceKnown && origin.Success && _knownSecretPattern != null)
            {
                var host = prefix.Substring(origin.Length, pathStart - origin.Length);
                if (_knownSecretPattern.Matches(host).Cast<Match>().Any(match => match.Value != Redacted))
                {
                    _ = Redact(host, false);
                    var colon = host.LastIndexOf(':');
                    var port = colon > 0 && ushort.TryParse(host.AsSpan(colon + 1), NumberStyles.None,
                        CultureInfo.InvariantCulture, out _) ? host.Substring(colon) : "";
                    var replacement = "redacted.invalid" + port;
                    prefix = string.Concat(prefix.AsSpan(0, origin.Length), replacement.AsSpan(), prefix.AsSpan(pathStart));
                    pathStart = origin.Length + replacement.Length;
                }
            }
            var path = SanitizePlainText(prefix.Substring(pathStart), true, false);
            if (!_discovering && replaceKnown && _knownSecretPattern != null)
            {
                path = _knownSecretPattern.Replace(path, match =>
                    match.Value == Redacted ? Uri.EscapeDataString(Redacted) : Uri.EscapeDataString(Redact(match.Value, false)));
            }
            var segments = path.Split('/');
            for (var index = 0; index < segments.Length; index++)
            {
                try
                {
                    var (decoded, _) = DecodeRepeated(segments[index], false);
                    if (SanitizeText(decoded, depth + 1, replaceKnown) != decoded)
                    {
                        segments[index] = Uri.EscapeDataString(Redacted);
                    }
                }
                catch (DecoderFallbackException)
                {
                    segments[index] = Uri.EscapeDataString(Redact(segments[index], false));
                }
            }
            path = string.Join("/", segments);
            path = PathSecret.Replace(path, match =>
            {
                var pathPrefix = match.Groups["prefix"].Value;
                var (secret, _) = DecodeRepeated(match.Value.Substring(pathPrefix.Length), false);
                return pathPrefix + Uri.EscapeDataString(Redact(secret));
            });
            prefix = string.Concat(prefix.AsSpan(0, pathStart), path.AsSpan());
            if (question < 0)
            {
                return prefix;
            }
            var query = value.Substring(question + 1);
            var hash = query.IndexOf('#');
            return prefix + "?" + SanitizeParameters(hash < 0 ? query : query.Substring(0, hash), depth, replaceKnown) +
                (hash < 0 ? string.Empty : query.Substring(hash));
        }

        private string SanitizeParameters(string text, int depth, bool replaceKnown = true)
        {
            var parts = ParameterSeparator.Split(text);
            for (var index = 0; index < parts.Length; index += 2)
            {
                var equals = parts[index].IndexOf('=');
                if (equals < 0)
                {
                    try
                    {
                        var (flagValue, flagPasses) = DecodeRepeated(parts[index], true);
                        var flagOutput = SanitizeParameterName(flagValue, depth + 1, replaceKnown);
                        if (flagOutput != flagValue)
                        {
                            parts[index] = EncodeParameterValue(flagOutput, flagPasses);
                        }
                    }
                    catch (DecoderFallbackException)
                    {
                        parts[index] = Uri.EscapeDataString(Redact(parts[index], false));
                    }
                    continue;
                }
                string name;
                int namePasses;
                try
                {
                    (name, namePasses) = DecodeRepeated(parts[index].Substring(0, equals), true);
                }
                catch (DecoderFallbackException)
                {
                    parts[index] = Uri.EscapeDataString(Redact(parts[index], false));
                    continue;
                }
                var sanitizedName = SanitizeParameterName(name, depth + 1, replaceKnown);
                var parameterPrefix = sanitizedName == name ? parts[index].Substring(0, equals + 1) :
                    EncodeParameterValue(sanitizedName, namePasses) + "=";
                var sensitive = IsSensitiveName(name) || name.Equals("code", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("state", StringComparison.OrdinalIgnoreCase);
                var original = parts[index].Substring(equals + 1);
                string decoded;
                int passes;
                string literal;
                int literalPasses;
                try
                {
                    (decoded, passes) = DecodeRepeated(original, true);
                    (literal, literalPasses) = DecodeRepeated(original, false);
                }
                catch (DecoderFallbackException)
                {
                    parts[index] = parameterPrefix + Uri.EscapeDataString(Redact(original, sensitive));
                    continue;
                }
                string sanitized;
                var changed = false;
                if (sensitive)
                {
                    if (_discovering && literal != decoded)
                    {
                        RememberCredential(literal);
                    }
                    sanitized = Redact(decoded);
                    changed = sanitized != decoded;
                }
                else
                {
                    sanitized = SanitizeText(literal, depth + 1, replaceKnown);
                    changed = sanitized != literal;
                    if (changed)
                    {
                        passes = literalPasses;
                    }
                    else
                    {
                        sanitized = literal == decoded ? sanitized : SanitizeText(decoded, depth + 1, replaceKnown);
                        changed = sanitized != decoded;
                    }
                }
                parts[index] = parameterPrefix + (changed ? EncodeParameterValue(sanitized, passes) : original);
            }
            return string.Concat(parts);
        }

        private string SanitizeParameterName(string name, int depth, bool replaceKnown)
        {
            if (replaceKnown && ContainsLearnedName(name))
            {
                return Redact(name, false);
            }
            return SanitizeText(name, depth, false);
        }

        private string EncodeParameterValue(string value, int passes)
        {
            for (var pass = 0; pass < Math.Max(1, passes); pass++)
            {
                SpendWork(value.Length);
                value = Uri.EscapeDataString(value);
            }
            return value;
        }

        private string SanitizeText(string text, int depth, bool replaceKnown = true)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }
            if (depth > 8)
            {
                return Redact(text);
            }
            SpendWork(text.Length);
            text = PrivateKey.Replace(text, match => Redact(match.Value));
            if ((UriAuthority.IsMatch(text) || text.StartsWith('/') || text.StartsWith("./", StringComparison.Ordinal) ||
                 text.StartsWith("../", StringComparison.Ordinal)) && !text.Any(char.IsWhiteSpace))
            {
                return SanitizeUri(text, depth + 1, replaceKnown);
            }
            text = SanitizeAssignments(text, false, FindUrls(text));
            var protectedUrls = FindUrls(text);
            var protectedIndex = 0;
            text = SecretToken.Replace(text, match =>
            {
                while (protectedIndex < protectedUrls.Length &&
                    protectedUrls[protectedIndex].Index + protectedUrls[protectedIndex].Length <= match.Index)
                {
                    protectedIndex++;
                }
                return protectedIndex < protectedUrls.Length && protectedUrls[protectedIndex].Index <= match.Index
                    ? match.Value : SanitizeToken(match, false);
            });
            var result = new StringBuilder();
            var position = 0;
            foreach (var match in FindUrls(text))
            {
                result.Append(SanitizePlainText(text.Substring(position, match.Index - position), false, replaceKnown));
                result.Append(SanitizeUri(match.Value, depth + 1, replaceKnown));
                position = match.Index + match.Length;
            }
            result.Append(SanitizePlainText(text.Substring(position), false, replaceKnown));
            return result.ToString();
        }

        private static Match[] FindUrls(string text) =>
            Url.Matches(text).Cast<Match>().Where(match => match.Index == 0 ||
                (!char.IsLetterOrDigit(text[match.Index - 1]) && !"_-+./".Contains(text[match.Index - 1]))).ToArray();

        private string SanitizePlainText(string text, bool uri = false, bool replaceKnown = true)
        {
            SpendWork(text.Length);
            if (!_discovering && replaceKnown && _knownSecretPattern != null)
            {
                text = _knownSecretPattern.Replace(text, match => match.Value == Redacted ? match.Value : Redact(match.Value));
            }
            text = SanitizeAssignments(text, uri);
            text = PathIdentity.Replace(text, match => SanitizeIdentity(match, uri));
            text = SecretToken.Replace(text, match => SanitizeToken(match, uri));
            return Email.Replace(text, match => uri ? Uri.EscapeDataString(Redact(match.Value)) : Redact(match.Value));
        }

        private string SanitizeAssignments(string text, bool uri, IReadOnlyList<Match> urls = null)
        {
            var ranges = new List<(int Start, int End, bool Quoted, bool Uri)>();
            var urlIndex = 0;
            SpendWork(text.Length);
            foreach (var candidate in AssignmentPrefix.EnumerateMatches(text))
            {
                SpendWork(candidate.Length);
                if (ranges.Count > 0 && candidate.Index < ranges[^1].End)
                {
                    continue;
                }
                var match = AssignmentPrefix.Match(text, candidate.Index, candidate.Length);
                if (!IsSensitiveName(match.Groups["name"].Value))
                {
                    continue;
                }
                var start = match.Index + match.Length;
                if (match.Groups["separator"].Value == ":" &&
                    ((match.Index > 0 && text[match.Index - 1] == ':') ||
                     text.AsSpan(start).StartsWith("//", StringComparison.Ordinal)))
                {
                    continue;
                }
                if (start >= text.Length)
                {
                    continue;
                }
                while (urls != null && urlIndex < urls.Count &&
                    urls[urlIndex].Index + urls[urlIndex].Length <= match.Index)
                {
                    urlIndex++;
                }
                var enclosingUrl = urls != null && urlIndex < urls.Count && urls[urlIndex].Index <= match.Index
                    ? urls[urlIndex] : null;
                if (enclosingUrl != null)
                {
                    var authority = UriAuthority.Match(enclosingUrl.Value);
                    var endOfAuthority = enclosingUrl.Value.IndexOfAny(new[] { '/', '?', '#' }, authority.Length);
                    if (endOfAuthority < 0)
                    {
                        endOfAuthority = enclosingUrl.Length;
                    }
                    var at = enclosingUrl.Value.LastIndexOf('@', endOfAuthority - 1, endOfAuthority - authority.Length);
                    var relativeIndex = match.Index - enclosingUrl.Index;
                    if (at >= authority.Length && relativeIndex >= authority.Length && relativeIndex < at)
                    {
                        var prefix = enclosingUrl.Value.AsSpan(authority.Length, relativeIndex - authority.Length);
                        if (prefix.ContainsAny(';', ',', '&'))
                        {
                            var uriEnd = enclosingUrl.Index + enclosingUrl.Length;
                            if (ranges.Count > 0 && enclosingUrl.Index <= ranges[^1].End)
                            {
                                ranges[^1] = (ranges[^1].Start, Math.Max(uriEnd, ranges[^1].End),
                                    ranges[^1].Quoted, false);
                            }
                            else
                            {
                                ranges.Add((enclosingUrl.Index, uriEnd, false, false));
                            }
                            continue;
                        }
                        continue;
                    }
                }
                while (start < text.Length && char.IsWhiteSpace(text[start]))
                {
                    SpendWork(1);
                    start++;
                }
                if (start >= text.Length)
                {
                    continue;
                }
                var end = start;
                var quote = text[start] is '"' or '\'' or '{' ? text[start++] : '\0';
                var closing = quote == '{' ? '}' : quote;
                var wholeLine = match.Groups["name"].Value.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
                    match.Groups["name"].Value.Contains("authorization", StringComparison.OrdinalIgnoreCase);
                end = start;
                while (end < text.Length)
                {
                    if ((end & 255) == 0)
                    {
                        SpendWork(256);
                    }
                    if (quote != '\0')
                    {
                        if (quote != '{' && text[end] == '\\' && end + 1 < text.Length)
                        {
                            end += 2;
                            continue;
                        }
                        if (quote == '{' && text[end] == '}' && end + 1 < text.Length && text[end + 1] == '}')
                        {
                            end += 2;
                            continue;
                        }
                        if (text[end] == closing)
                        {
                            break;
                        }
                    }
                    else if (text[end] is '\r' or '\n' ||
                        (!wholeLine && text[end] is ';' or ',' or '&' or '<' or '>'))
                    {
                        break;
                    }
                    end++;
                }
                if (end > start)
                {
                    if (ranges.Count > 0 && start <= ranges[^1].End)
                    {
                        ranges[^1] = (ranges[^1].Start, Math.Max(end, ranges[^1].End),
                            ranges[^1].Quoted, ranges[^1].Uri && (uri || enclosingUrl != null));
                    }
                    else
                    {
                        ranges.Add((start, end, quote != '\0', uri || enclosingUrl != null));
                    }
                }
            }
            var result = new StringBuilder();
            var position = 0;
            foreach (var range in ranges)
            {
                result.Append(text, position, range.Start - position);
                if (!range.Quoted && !range.Uri)
                {
                    result.Append('"');
                }
                var value = text.Substring(range.Start, range.End - range.Start);
                result.Append(range.Uri ? Uri.EscapeDataString(Redact(WebUtility.UrlDecode(value))) : Redact(value));
                if (!range.Quoted && !range.Uri)
                {
                    result.Append('"');
                }
                position = range.End;
            }
            result.Append(text, position, text.Length - position);
            return result.ToString();
        }

        private (string Value, int Passes) DecodeRepeated(string value, bool form)
        {
            var passes = 0;
            for (; passes < 8; passes++)
            {
                SpendWork(value.Length);
                var decoded = DecodePercentEscapes(value, passes == 0 && form);
                if (decoded == value)
                {
                    return (value, passes);
                }
                value = decoded;
            }
            if (DecodePercentEscapes(value, false) != value)
            {
                throw new DecoderFallbackException();
            }
            return (value, passes);
        }

        private string DecodePercentEscapes(string value, bool form)
        {
            if (!value.Contains('%') && (!form || !value.Contains('+')))
            {
                return value;
            }
            var output = new StringBuilder(value.Length);
            for (var index = 0; index < value.Length;)
            {
                if ((index & 255) == 0)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                }
                if (index + 2 < value.Length && value[index] == '%' &&
                    Uri.IsHexDigit(value[index + 1]) && Uri.IsHexDigit(value[index + 2]))
                {
                    var start = index;
                    do
                    {
                        index += 3;
                    }
                    while (index + 2 < value.Length && value[index] == '%' &&
                        Uri.IsHexDigit(value[index + 1]) && Uri.IsHexDigit(value[index + 2]));
                    var bytes = new byte[(index - start) / 3];
                    for (var offset = 0; offset < bytes.Length; offset++)
                    {
                        if ((offset & 255) == 0)
                        {
                            _cancellationToken.ThrowIfCancellationRequested();
                        }
                        bytes[offset] = byte.Parse(value.AsSpan(start + offset * 3 + 1, 2),
                            NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    }
                    output.Append(StrictUtf8.GetString(bytes));
                }
                else
                {
                    output.Append(form && value[index] == '+' ? ' ' : value[index]);
                    index++;
                }
            }
            return output.ToString();
        }

        private string SanitizeToken(Match match, bool uri)
        {
            SpendWork(match.Length);
            if (!match.Groups["jwt"].Success)
            {
                var redacted = Redact(match.Value);
                return uri ? Uri.EscapeDataString(redacted) : redacted;
            }
            var value = match.Value;
            StringBuilder output = null;
            var copied = 0;
            var start = 0;
            while (start < value.Length)
            {
                var dot = value.IndexOf('.', start);
                if (dot < 0)
                {
                    break;
                }
                SpendWork(dot - start + 1);
                var end = value.Length;
                if (dot - start > 8192 || TryJwtWindow(value, start, dot, out end))
                {
                    output ??= new StringBuilder();
                    output.Append(value, copied, start - copied);
                    var redacted = Redact(value.Substring(start, end - start));
                    output.Append(uri ? Uri.EscapeDataString(redacted) : redacted);
                    copied = end;
                    start = end + 1;
                }
                else
                {
                    start = dot + 1;
                }
            }
            return output == null ? value : output.Append(value, copied, value.Length - copied).ToString();
        }

        private static bool TryJwtWindow(string value, int start, int firstDot, out int end)
        {
            end = 0;
            var headerLength = firstDot - start;
            if (headerLength > 8192)
            {
                return false;
            }
            if (headerLength < 12)
            {
                return false;
            }
            using var header = ReadJwtObject(value.AsSpan(start, headerLength), 8);
            if (header == null || !header.RootElement.TryGetProperty("alg", out var algorithm) ||
                algorithm.ValueKind != JsonValueKind.String)
            {
                return false;
            }
            if (header.RootElement.TryGetProperty("enc", out var encryption) &&
                encryption.ValueKind == JsonValueKind.String && TryJwtEnd(value, firstDot, 5, out end))
            {
                return true;
            }
            if (!TryJwtEnd(value, firstDot, 3, out end))
            {
                return false;
            }
            var secondDot = value.IndexOf('.', firstDot + 1);
            using var claims = ReadJwtObject(value.AsSpan(firstDot + 1, secondDot - firstDot - 1), MaximumStructuredDepth);
            return claims != null;
        }

        private static bool TryJwtEnd(string value, int firstDot, int parts, out int end)
        {
            end = firstDot + 1;
            for (var index = 1; index < parts; index++)
            {
                var dot = value.IndexOf('.', end);
                if (index == parts - 1)
                {
                    end = dot < 0 ? value.Length : dot;
                    return true;
                }
                if (dot < 0)
                {
                    return false;
                }
                end = dot + 1;
            }
            return false;
        }

        private static JsonDocument ReadJwtObject(ReadOnlySpan<char> part, int maximumDepth)
        {
            if (part.IsEmpty || part.Length % 4 == 1)
            {
                return null;
            }
            try
            {
                var normalized = part.ToString().Replace('-', '+').Replace('_', '/');
                var bytes = Convert.FromBase64String(normalized.PadRight((normalized.Length + 3) / 4 * 4, '='));
                var first = 0;
                while (first < bytes.Length && bytes[first] is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t')
                {
                    first++;
                }
                if (first == bytes.Length || bytes[first] != (byte)'{')
                {
                    return null;
                }
                var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = maximumDepth });
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    return document;
                }
                document.Dispose();
                return null;
            }
            catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
            {
                return null;
            }
        }

        private string Redact(string value, bool remember = true)
        {
            if (string.IsNullOrEmpty(value) || value == Redacted)
            {
                return value;
            }
            SpendWork(value.Length);
            if (_discovering)
            {
                if (remember)
                {
                    RememberCredential(value);
                }
                return Redacted;
            }
            Summary.RedactedValues++;
            return Redacted;
        }

        private void SpendWork(int units)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            _remainingWork -= units;
            if (_remainingWork < 0)
            {
                _unsafeInspection = true;
                _workBudgetExceeded = true;
                throw new InvalidDataException();
            }
        }

        private string SanitizeIdentity(Match match, bool uri)
        {
            var value = Redact(WebUtility.UrlDecode(match.Groups["value"].Value));
            return match.Groups["name"].Value + "(" + (uri ? Uri.EscapeDataString(value) : value) + ")";
        }

        private static bool IsDescriptorName(string name) =>
            name.Equals("name", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("key", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("id", StringComparison.OrdinalIgnoreCase);

        private static bool IsPasswordInput(XElement element) =>
            element.Name.LocalName.Equals("input", StringComparison.OrdinalIgnoreCase) &&
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName.Equals("type", StringComparison.OrdinalIgnoreCase) &&
                attribute.Value.Trim().Equals("password", StringComparison.OrdinalIgnoreCase));

        private static bool IsSensitiveName(string name)
        {
            var normalized = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            return normalized is "key" or "keys" or "auth" or "sig" or "sas" or "pwd" or "user" or
                "otp" or "pin" or "nonce" or "assertion" or "samlresponse" or "samlrequest" or "gtg" or "xid" ||
                normalized.EndsWith("auth", StringComparison.Ordinal) ||
                SensitiveNames.Any(normalized.Contains);
        }

        private static bool IsHeaderNameCharacter(char character) =>
            char.IsAsciiLetterOrDigit(character) || "!#$%&'*+-.^_`|~".Contains(character);
    }
}
