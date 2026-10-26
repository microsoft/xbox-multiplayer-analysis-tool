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
    }

    internal readonly record struct SanitizedHttpMessage(string Headers, byte[] Body, bool BodyOmitted);

    internal sealed class SazSanitizer
    {
        internal const string Redacted = "[REDACTED]";
        internal const int MaximumBodyBytes = 4 * 1024 * 1024;
        internal const int MaximumStructuredDepth = 64;
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(250);
        private const RegexOptions PatternOptions = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
        private static readonly Regex AssignmentPrefix = new(
            @"(?<name>[A-Za-z_][A-Za-z0-9_.-]*)[""']?[ \t]*(?<separator>[:=])[ \t]*",
            PatternOptions | RegexOptions.IgnoreCase, PatternTimeout);
        private static readonly Regex PrivateKey = new(
            @"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?(?:-----END [A-Z ]*PRIVATE KEY-----|\z)",
            PatternOptions, PatternTimeout);
        private static readonly Regex Url = new(
            @"(?:[A-Za-z][A-Za-z0-9+.-]*:)?//[^\s<>""']+", PatternOptions, PatternTimeout);
        private static readonly Regex UriAuthority = new(
            @"\A(?:[A-Za-z][A-Za-z0-9+.-]*:)?//", PatternOptions, PatternTimeout);
        private static readonly Regex SecretToken = new(
            @"\b(?:Bearer|Basic)\s+[A-Za-z0-9_+/=.-]+|\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*|\b(?:AKIA|ASIA)[A-Z0-9]{16}\b|\bgh[pousr]_[A-Za-z0-9]{20,}\b|\bgithub_pat_[A-Za-z0-9_]{20,}\b|\bsk[-_](?:live_|test_)?[A-Za-z0-9_-]{20,}\b",
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
            "passcode", "securitycode", "verificationcode", "ticket", "devicecode", "usercode", "gamedisplayname"
        };
        private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "Forwarded", "X-Forwarded-For", "X-Real-IP", "X-Client-IP", "X-MS-Client-Principal"
        };
        private static readonly HashSet<string> PrivateJwkMembers = new(StringComparer.OrdinalIgnoreCase)
        {
            "d", "p", "q", "dp", "dq", "qi", "oth", "k"
        };

        internal SanitizedExportSummary Summary { get; } = new();

        internal SanitizedHttpMessage SanitizeMessage(
            string rawHeaders, byte[] body, bool request, string requestMethod = null)
        {
            using var reader = new StringReader(rawHeaders ?? string.Empty);
            var firstLine = reader.ReadLine() ?? string.Empty;
            var headers = new List<KeyValuePair<string, string>>();
            string line;
            while (!string.IsNullOrEmpty(line = reader.ReadLine()))
            {
                if ((line[0] == ' ' || line[0] == '\t') && headers.Count > 0)
                {
                    var previous = headers[^1];
                    headers[^1] = new(previous.Key, previous.Value + " " + line.Trim());
                    continue;
                }
                var colon = line.IndexOf(':');
                if (colon <= 0 || !line.Substring(0, colon).All(IsHeaderNameCharacter))
                {
                    Summary.OmittedHeaders++;
                    continue;
                }
                headers.Add(new(line.Substring(0, colon), line.Substring(colon + 1).Trim()));
            }

            var startLine = SanitizeStartLine(firstLine, request);
            var status = request ? 0 : ParseStatusCode(firstLine);
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
            else
            {
                outputBody = SanitizeBody(originalBody, headers, out contentType);
            }
            var bodyChanged = originalBody.Length > 0;
            var output = new StringBuilder(startLine).Append("\r\n");
            foreach (var header in headers)
            {
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
                var value = IsSensitiveName(header.Key) || SensitiveHeaders.Contains(header.Key)
                    ? Redact(rawValue)
                    : header.Key.Equals("Location", StringComparison.OrdinalIgnoreCase) ||
                      header.Key.Equals("Content-Location", StringComparison.OrdinalIgnoreCase) ||
                      header.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase)
                        ? SanitizeUri(rawValue, 0)
                        : SanitizeText(rawValue, 0);
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
            try
            {
                if (body.Length > MaximumBodyBytes)
                {
                    throw new InvalidDataException();
                }
                var decoded = body;
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
                string sanitized;
                if (mediaType == "application/json" || mediaType.EndsWith("+json", StringComparison.Ordinal))
                {
                    using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = MaximumStructuredDepth });
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
                            if (depthReader.Depth >= MaximumStructuredDepth)
                            {
                                throw new InvalidDataException();
                            }
                        }
                    }
                    using var xmlReader = XmlReader.Create(new StringReader(text), settings);
                    var document = XDocument.Load(xmlReader, LoadOptions.PreserveWhitespace);
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
                Summary.OmittedBodies++;
                return Array.Empty<byte>();
            }
        }

        private static byte[] Decompress(byte[] body, string encoding)
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

        private static byte[] DecodeCompressedBody(byte[] body, string encoding)
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
                if (output.Length + read > MaximumBodyBytes)
                {
                    throw new InvalidDataException();
                }
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }

        private void WriteJson(Utf8JsonWriter writer, JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var namedSecret = element.EnumerateObject().Any(property =>
                        IsDescriptorName(property.Name) &&
                        property.Value.ValueKind == JsonValueKind.String && IsSensitiveName(property.Value.GetString()));
                    var jwk = element.EnumerateObject().Any(property =>
                        property.Name.Equals("kty", StringComparison.OrdinalIgnoreCase) &&
                        property.Value.ValueKind == JsonValueKind.String);
                    writer.WriteStartObject();
                    foreach (var property in element.EnumerateObject())
                    {
                        writer.WritePropertyName(SanitizeText(property.Name, 0));
                        if (IsSensitiveName(property.Name) || (jwk && PrivateJwkMembers.Contains(property.Name)) ||
                            (namedSecret && property.Name.Equals("value", StringComparison.OrdinalIgnoreCase)))
                        {
                            if (property.Value.ValueKind == JsonValueKind.Null)
                            {
                                writer.WriteNullValue();
                            }
                            else
                            {
                                writer.WriteStringValue(Redact(property.Value.GetRawText()));
                            }
                        }
                        else
                        {
                            WriteJson(writer, property.Value);
                        }
                    }
                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var child in element.EnumerateArray())
                    {
                        WriteJson(writer, child);
                    }
                    writer.WriteEndArray();
                    break;
                case JsonValueKind.String:
                    writer.WriteStringValue(SanitizeText(element.GetString(), 0));
                    break;
                default:
                    element.WriteTo(writer);
                    break;
            }
        }

        private void SanitizeXml(XElement element)
        {
            if (element == null)
            {
                return;
            }
            var namedSecret = element.Attributes().Any(attribute =>
                IsDescriptorName(attribute.Name.LocalName) && IsSensitiveName(attribute.Value)) ||
                element.Elements().Any(child => IsDescriptorName(child.Name.LocalName) &&
                    !child.HasElements && IsSensitiveName(child.Value));
            var sensitiveElement = IsSensitiveName(element.Name.LocalName) || namedSecret;
            foreach (var attribute in element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration))
            {
                if (attribute.Name == XNamespace.Xml + "space")
                {
                    continue;
                }
                attribute.Value = sensitiveElement || IsSensitiveName(attribute.Name.LocalName)
                    ? Redact(attribute.Value) : SanitizeText(attribute.Value, 0);
            }
            if (sensitiveElement)
            {
                element.Value = Redact(element.Value);
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
            return SanitizeText(line, 0);
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

        private string SanitizeUri(string value, int depth)
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
                        Summary.RedactedValues++;
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
            var path = SanitizePlainText(prefix.Substring(pathStart), true);
            path = PathSecret.Replace(path, match =>
                match.Groups["prefix"].Value + Uri.EscapeDataString(Redact(match.Value)));
            prefix = string.Concat(prefix.AsSpan(0, pathStart), path.AsSpan());
            if (question < 0)
            {
                return prefix;
            }
            var query = value.Substring(question + 1);
            var hash = query.IndexOf('#');
            return prefix + "?" + SanitizeParameters(hash < 0 ? query : query.Substring(0, hash), depth) +
                (hash < 0 ? string.Empty : query.Substring(hash));
        }

        private string SanitizeParameters(string text, int depth)
        {
            var parts = ParameterSeparator.Split(text);
            for (var index = 0; index < parts.Length; index += 2)
            {
                var equals = parts[index].IndexOf('=');
                if (equals < 0)
                {
                    parts[index] = SanitizeText(parts[index], depth + 1);
                    continue;
                }
                var name = parts[index].Substring(0, equals);
                for (var pass = 0; pass < 3; pass++)
                {
                    var decodedName = WebUtility.UrlDecode(name);
                    if (name == decodedName)
                    {
                        break;
                    }
                    name = decodedName;
                }
                var original = parts[index].Substring(equals + 1);
                var decoded = WebUtility.UrlDecode(original);
                var sanitized = IsSensitiveName(name) || name.Contains('\uFFFD') ||
                    name.Equals("code", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("state", StringComparison.OrdinalIgnoreCase)
                    ? Redact(decoded) : SanitizeText(decoded, depth + 1);
                if (!string.Equals(decoded, sanitized, StringComparison.Ordinal))
                {
                    parts[index] = string.Concat(parts[index].AsSpan(0, equals + 1),
                        Uri.EscapeDataString(sanitized).AsSpan());
                }
            }
            return string.Concat(parts);
        }

        private string SanitizeText(string text, int depth)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }
            if (depth > 8)
            {
                return Redact(text);
            }
            text = PrivateKey.Replace(text, match => Redact(match.Value));
            if ((UriAuthority.IsMatch(text) || text.StartsWith('/') || text.StartsWith("./", StringComparison.Ordinal) ||
                 text.StartsWith("../", StringComparison.Ordinal)) && !text.Any(char.IsWhiteSpace))
            {
                return SanitizeUri(text, depth + 1);
            }
            text = SanitizeAssignments(text, false, FindUrls(text));
            var protectedUrls = FindUrls(text);
            text = SecretToken.Replace(text, match => protectedUrls.Any(url =>
                match.Index >= url.Index && match.Index < url.Index + url.Length) ? match.Value : Redact(match.Value));
            var result = new StringBuilder();
            var position = 0;
            foreach (var match in FindUrls(text))
            {
                result.Append(SanitizePlainText(text.Substring(position, match.Index - position)));
                result.Append(SanitizeUri(match.Value, depth + 1));
                position = match.Index + match.Length;
            }
            result.Append(SanitizePlainText(text.Substring(position)));
            return result.ToString();
        }

        private static Match[] FindUrls(string text) =>
            Url.Matches(text).Cast<Match>().Where(match => match.Index == 0 ||
                (!char.IsLetterOrDigit(text[match.Index - 1]) && !"_-+./".Contains(text[match.Index - 1]))).ToArray();

        private string SanitizePlainText(string text, bool uri = false)
        {
            text = SanitizeAssignments(text, uri);
            text = PathIdentity.Replace(text, match => SanitizeIdentity(match, uri));
            text = SecretToken.Replace(text, match => uri ? Uri.EscapeDataString(Redact(match.Value)) : Redact(match.Value));
            return Email.Replace(text, match => uri ? Uri.EscapeDataString(Redact(match.Value)) : Redact(match.Value));
        }

        private string SanitizeAssignments(string text, bool uri, IReadOnlyList<Match> urls = null)
        {
            var ranges = new List<(int Start, int End, bool Quoted, bool Uri)>();
            foreach (Match match in AssignmentPrefix.Matches(text))
            {
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
                var enclosingUrl = urls?.FirstOrDefault(url =>
                    match.Index >= url.Index && match.Index < url.Index + url.Length);
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
                var end = start;
                var quote = text[start] is '"' or '\'' ? text[start++] : '\0';
                var wholeLine = match.Groups["name"].Value.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
                    match.Groups["name"].Value.Contains("authorization", StringComparison.OrdinalIgnoreCase);
                end = start;
                while (end < text.Length)
                {
                    if (quote != '\0')
                    {
                        if (text[end] == '\\' && end + 1 < text.Length)
                        {
                            end += 2;
                            continue;
                        }
                        if (text[end] == quote)
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

        private string Redact(string value)
        {
            if (string.IsNullOrEmpty(value) || value == Redacted)
            {
                return value;
            }
            Summary.RedactedValues++;
            return Redacted;
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
