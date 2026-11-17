// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XMAT.WebServiceCapture;

namespace XMAT.Operations.Replay
{
    // Reads a request file (xmat-request/1) strictly, as untrusted data: one JSON object with exactly its eight members,
    // no comments, trailing commas or byte order mark, within RequestFileLimits. Every limit is checked before the
    // allocation that it bounds: the file's length before and while it is read, a string's decoded size from its raw
    // value before the string is made, and the body's decoded length before it is decoded. Failures carry a
    // RequestFileCodes code and never any content.
    internal static class RequestFileReader
    {
        internal const long MaxFileBytes = RequestFileLimits.MaxFileBytes;

        private const string TemplateProfile = "template";
        private const string ContentProfile = "content";

        private static readonly string[] OmissionKinds = { "method", "url", "headers", "body" };

        private static readonly SearchValues<byte> Base64Alphabet =
            SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/"u8);

        // Reads the file off the calling thread, at most MaxFileBytes of it, then parses it there too. Cancellation
        // propagates as OperationCanceledException.
        internal static Task<RequestFileReadResult> ReadFileAsync(string path, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            return Task.Run(() => ReadFileCoreAsync(path, cancellationToken), cancellationToken);
        }

        internal static RequestFileReadResult Read(ReadOnlySpan<byte> json)
        {
            if (json.Length > MaxFileBytes)
            {
                return RequestFileReadResult.Failed(RequestFileCodes.TooLarge);
            }

            try
            {
                return Parse(json);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                // Not JSON, a string that is not valid text (an escaped lone surrogate), or a value of the wrong kind.
                return RequestFileReadResult.Failed(RequestFileCodes.Invalid);
            }
        }

        private static async Task<RequestFileReadResult> ReadFileCoreAsync(string path, CancellationToken cancellationToken)
        {
            byte[] buffer;
            int length;
            try
            {
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length > MaxFileBytes)
                {
                    return RequestFileReadResult.Failed(RequestFileCodes.TooLarge);
                }

                // The file may grow after its length was read: one byte more than the limit is enough to tell.
                buffer = new byte[(int)stream.Length + 1];
                length = 0;
                while (true)
                {
                    if (length == buffer.Length)
                    {
                        if (buffer.Length > MaxFileBytes)
                        {
                            return RequestFileReadResult.Failed(RequestFileCodes.TooLarge);
                        }

                        Array.Resize(ref buffer, (int)Math.Min(MaxFileBytes + 1, Math.Max(4096L, buffer.Length * 2L)));
                    }

                    int read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    length += read;
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return RequestFileReadResult.Failed(RequestFileCodes.NotFound);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return RequestFileReadResult.Failed(RequestFileCodes.Unreadable);
            }

            if (length > MaxFileBytes)
            {
                return RequestFileReadResult.Failed(RequestFileCodes.TooLarge);
            }

            RequestFileReadResult result = Read(buffer.AsSpan(0, length));
            return result.Code != null
                ? result
                : result with { Sha256 = Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, length))).ToLowerInvariant() };
        }

        private static RequestFileReadResult Parse(ReadOnlySpan<byte> json)
        {
            if (json.StartsWith("\uFEFF"u8))
            {
                return RequestFileReadResult.Failed(RequestFileCodes.Invalid);
            }

            var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = RequestFileLimits.MaxDepth });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return RequestFileReadResult.Failed(RequestFileCodes.Invalid);
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            bool unknown = false;
            string schema = null;
            string profile = null;
            string method = null;
            string url = null;
            string httpVersion = null;
            List<KeyValuePair<string, string>> headers = null;
            byte[] body = null;
            List<string> omitted = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                string member = reader.ValueSpan.Length <= 16 && !reader.ValueIsEscaped ? reader.GetString() : null;
                if (member == null || !seen.Add(member))
                {
                    // An escaped or long name is never one of the members; a duplicate is never allowed.
                    if (member != null)
                    {
                        return RequestFileReadResult.Failed(RequestFileCodes.Invalid);
                    }

                    unknown = true;
                    reader.Read();
                    reader.Skip();
                    continue;
                }

                reader.Read();
                switch (member)
                {
                    case "schema":
                        // Checked as soon as it is read: a later version's other members need not be valid here.
                        schema = ShortString(ref reader, 64);
                        if (schema != RequestFileLimits.Schema)
                        {
                            return RequestFileReadResult.Failed(RequestFileCodes.Unsupported);
                        }

                        break;
                    case "profile":
                        profile = ShortString(ref reader, 16);
                        break;
                    case "method":
                        method = LimitedString(ref reader, RequestFileLimits.MaxMethodChars);
                        break;
                    case "url":
                        url = LimitedString(ref reader, RequestFileLimits.MaxUrlChars);
                        break;
                    case "httpVersion":
                        httpVersion = ShortString(ref reader, 8);
                        break;
                    case "headers":
                        headers = Headers(ref reader);
                        break;
                    case "body":
                        body = Body(ref reader);
                        break;
                    case "omitted":
                        omitted = Omitted(ref reader);
                        break;
                    default:
                        unknown = true;
                        reader.Skip();
                        break;
                }
            }

            // The object ends here; anything after it, as a second value, is invalid (the reader throws).
            if (reader.TokenType != JsonTokenType.EndObject || reader.Read())
            {
                return RequestFileReadResult.Failed(RequestFileCodes.Invalid);
            }

            if (schema != RequestFileLimits.Schema)
            {
                return RequestFileReadResult.Failed(schema == null ? RequestFileCodes.Invalid : RequestFileCodes.Unsupported);
            }

            if (unknown || seen.Count != 8)
            {
                return RequestFileReadResult.Failed(RequestFileCodes.Invalid);
            }

            RequestFileContent content = Content(profile, method, url, httpVersion, headers, body, omitted);
            return content == null
                ? RequestFileReadResult.Failed(RequestFileCodes.Invalid)
                : new RequestFileReadResult(content, null);
        }

        // The members checked together: each value, and that the omitted kinds are exactly the nulls and fit the profile.
        private static RequestFileContent Content(
            string profile,
            string method,
            string url,
            string httpVersion,
            List<KeyValuePair<string, string>> headers,
            byte[] body,
            List<string> omitted)
        {
            RequestFileProfile kind;
            switch (profile)
            {
                case TemplateProfile:
                    kind = RequestFileProfile.Template;
                    break;
                case ContentProfile:
                    kind = RequestFileProfile.Content;
                    break;
                default:
                    return null;
            }

            ReplayHttpVersion version;
            switch (httpVersion)
            {
                case "auto":
                    version = ReplayHttpVersion.Auto;
                    break;
                case "1.1":
                    version = ReplayHttpVersion.Http11;
                    break;
                case "2":
                    version = ReplayHttpVersion.Http2;
                    break;
                default:
                    return null;
            }

            var omissions = new RequestOmissions(
                omitted.Contains("method"), omitted.Contains("url"), omitted.Contains("headers"), omitted.Contains("body"));
            if (omissions.Method != (method == null) || omissions.Url != (url == null) ||
                omissions.Headers != (headers == null) || omissions.Body != (body == null))
            {
                return null;
            }

            if (method != null &&
                (method.Length > RequestFileLimits.MaxMethodChars || !RequestReplayDraft.IsValidMethod(method)))
            {
                return null;
            }

            if (url != null && !RequestReplayDraft.IsValidRequestFileUrl(url))
            {
                return null;
            }

            if (kind == RequestFileProfile.Content ? omissions.Any : !omissions.Url)
            {
                // A content file leaves nothing out; a template always leaves out the URL.
                return null;
            }

            if (kind == RequestFileProfile.Template &&
                ((!omissions.Method && !RequestFileWriter.IsCanonicalMethod(method)) ||
                 (!omissions.Headers && headers.Count > 0) ||
                 (!omissions.Body && body.Length > 0)))
            {
                // A template keeps only a canonical method, and headers and a body only when there are none.
                return null;
            }

            return new RequestFileContent(kind, method, url, version, headers?.AsReadOnly(), body, omissions);
        }

        // A string of at most maxChars characters once its escapes are decoded, counted from its raw value before the string
        // is made: an escaped spelling of a value, such as "\u0075rl" for url, is that value.
        private static string ShortString(ref Utf8JsonReader reader, int maxChars)
        {
            if (reader.TokenType != JsonTokenType.String || DecodedCharLength(reader.ValueSpan) > maxChars)
            {
                throw new FormatException();
            }

            return reader.GetString();
        }

        // Null, or a string of at most maxChars characters, counted from its raw value before the string is made.
        private static string LimitedString(ref Utf8JsonReader reader, int maxChars)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            if (reader.TokenType != JsonTokenType.String || DecodedCharLength(reader.ValueSpan) > maxChars)
            {
                throw new FormatException();
            }

            return reader.GetString();
        }

        // The UTF-16 length of a JSON string's raw value once its escapes are decoded, without decoding it: one character
        // for each escape (a \u escape is one UTF-16 unit) and for each UTF-8 sequence, two for a four-byte one.
        internal static long DecodedCharLength(ReadOnlySpan<byte> raw)
        {
            long length = 0;
            int index = 0;
            while (index < raw.Length)
            {
                byte value = raw[index];
                if (value == (byte)'\\')
                {
                    if (index + 1 >= raw.Length)
                    {
                        throw new FormatException();
                    }

                    bool unicodeEscape = raw[index + 1] == (byte)'u';
                    int escapeLength = unicodeEscape ? 6 : 2;
                    if (raw.Length - index < escapeLength)
                    {
                        throw new FormatException();
                    }

                    index += escapeLength;
                    length++;
                }
                else
                {
                    OperationStatus status = Rune.DecodeFromUtf8(raw[index..], out Rune rune, out int consumed);
                    if (status != OperationStatus.Done)
                    {
                        throw new FormatException();
                    }

                    index += consumed;
                    length += rune.Utf16SequenceLength;
                }
            }

            return length;
        }

        // At most MaxHeaders pairs of a name and a value, whose decoded UTF-8 size together is at most MaxHeaderBytes:
        // each string's size is counted from its raw value before the string is made. A value has no whitespace at either
        // end, which a header line would not keep.
        private static List<KeyValuePair<string, string>> Headers(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            if (reader.TokenType != JsonTokenType.StartArray)
            {
                throw new FormatException();
            }

            var headers = new List<KeyValuePair<string, string>>();
            long budget = RequestFileLimits.MaxHeaderBytes;
            while (reader.Read() && reader.TokenType == JsonTokenType.StartArray)
            {
                if (headers.Count == RequestFileLimits.MaxHeaders)
                {
                    throw new FormatException();
                }

                reader.Read();
                string name = HeaderString(ref reader, ref budget);
                reader.Read();
                string value = HeaderString(ref reader, ref budget);
                if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray ||
                    !RequestReplayDraft.IsValidHeader(name, value) || value != value.Trim(' ', '\t'))
                {
                    throw new FormatException();
                }

                headers.Add(new(name, value));
            }

            if (reader.TokenType != JsonTokenType.EndArray)
            {
                throw new FormatException();
            }

            return headers;
        }

        private static string HeaderString(ref Utf8JsonReader reader, ref long budget)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new FormatException();
            }

            long size = reader.ValueIsEscaped ? DecodedUtf8Length(reader.ValueSpan) : reader.ValueSpan.Length;
            if (size > budget)
            {
                throw new FormatException();
            }

            budget -= size;
            return reader.GetString();
        }

        // The UTF-8 length of a JSON string's raw value once its escapes are decoded, without decoding it. The reader has
        // already checked the escapes; a lone surrogate counts as three bytes, and getting the string then fails.
        internal static long DecodedUtf8Length(ReadOnlySpan<byte> raw)
        {
            long length = 0;
            int index = 0;
            while (index < raw.Length)
            {
                if (raw[index] != (byte)'\\')
                {
                    length++;
                    index++;
                    continue;
                }

                if (raw[index + 1] != (byte)'u')
                {
                    length++;
                    index += 2;
                    continue;
                }

                int code = Hex(raw.Slice(index + 2, 4));
                index += 6;
                if (code is >= 0xD800 and <= 0xDBFF && index + 6 <= raw.Length && raw[index] == (byte)'\\' &&
                    raw[index + 1] == (byte)'u' && Hex(raw.Slice(index + 2, 4)) is >= 0xDC00 and <= 0xDFFF)
                {
                    length += 4;
                    index += 6;
                    continue;
                }

                length += code < 0x80 ? 1 : code < 0x800 ? 2 : 3;
            }

            return length;
        }

        private static int Hex(ReadOnlySpan<byte> digits)
        {
            int value = 0;
            foreach (byte digit in digits)
            {
                value = (value << 4) | (digit <= (byte)'9' ? digit - '0' : (digit | 0x20) - 'a' + 10);
            }

            return value;
        }

        // Standard Base64 with padding, written without whitespace or escapes, so its decoded length is known before it
        // is decoded, and that is at most MaxBodyBytes.
        private static byte[] Body(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            if (reader.TokenType != JsonTokenType.String || reader.ValueIsEscaped)
            {
                throw new FormatException();
            }

            ReadOnlySpan<byte> text = reader.ValueSpan;
            if (text.Length % 4 != 0)
            {
                throw new FormatException();
            }

            int padding = text.EndsWith("=="u8) ? 2 : text.EndsWith("="u8) ? 1 : 0;
            long decoded = (text.Length / 4 * 3L) - padding;
            if (decoded > RequestFileLimits.MaxBodyBytes || text[..^padding].IndexOfAnyExcept(Base64Alphabet) >= 0)
            {
                throw new FormatException();
            }

            var body = new byte[decoded];
            if (Base64.DecodeFromUtf8(text, body, out int consumed, out int written) != OperationStatus.Done ||
                consumed != text.Length || written != body.Length)
            {
                throw new FormatException();
            }

            return body;
        }

        // The kinds that the file left out: distinct, known, and in the order method, url, headers, body.
        private static List<string> Omitted(ref Utf8JsonReader reader)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                throw new FormatException();
            }

            var kinds = new List<string>();
            int next = 0;
            while (reader.Read() && reader.TokenType == JsonTokenType.String)
            {
                string kind = ShortString(ref reader, 8);
                int position = Array.IndexOf(OmissionKinds, kind);
                if (position < next)
                {
                    throw new FormatException();
                }

                next = position + 1;
                kinds.Add(kind);
            }

            if (reader.TokenType != JsonTokenType.EndArray)
            {
                throw new FormatException();
            }

            return kinds;
        }
    }
}
