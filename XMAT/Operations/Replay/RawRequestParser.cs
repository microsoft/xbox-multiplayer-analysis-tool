// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using XMAT.WebServiceCapture;

namespace XMAT.Operations.Replay
{
    // How a raw request frames its body.
    internal enum RawFraming
    {
        None,
        ContentLength,
        Chunked,
    }

    // A failure of Raw mode's checks: its code (RawCodes, or the control-header code) and, for a fault in the head, the
    // head's 1-based line. The line is data-free.
    internal readonly record struct RawFailure(string Code, int? Line);

    // What the checks read from a raw request's private copy. Offsets are positions in that copy.
    internal sealed class RawParsedRequest
    {
        internal int HeadLength { get; init; }

        internal string Method { get; init; }

        internal string RequestTarget { get; init; }

        internal string Version { get; init; }

        // Field names as written, values with OWS trimmed, in order.
        internal IReadOnlyList<KeyValuePair<string, string>> Fields { get; init; }

        // Each field line as written, without its CRLF, in order.
        internal IReadOnlyList<string> FieldText { get; init; }

        // The head line of each field.
        internal IReadOnlyList<int> FieldLines { get; init; }

        internal bool AbsoluteForm { get; set; }

        // An absolute form's authority as written, which Host must equal exactly; null for an origin form.
        internal string AbsoluteAuthority { get; set; }

        // The request-target in origin form: as written, or after an absolute form's scheme and authority.
        internal string OriginForm { get; set; }

        // For recording: the target's origin and the origin form joined as strings, never resolved or canonicalized.
        internal Uri RequestUri { get; set; }

        internal RawFraming Framing { get; set; }

        internal long EntityLength { get; set; }

        // The data of each chunk of a chunked body.
        internal IReadOnlyList<(int Offset, int Length)> Chunks { get; set; } = Array.Empty<(int, int)>();
    }

    // Raw mode's request rules over a private copy, in their refusal order: limits, head syntax, method,
    // target, version, duplicates, control headers, Host, Connection, Expect and Upgrade, Trailer, framing, and methods
    // and bodies. ReadHead does the first two; CheckImage the rest for a send-admitted image (RawRequestImage), and
    // CheckEditor the subset that the Raw-to-Parsed conversion needs (RawEditorParse).
    internal static class RawRequestParser
    {
        private const string ExpectedVersion = "HTTP/1.1";

        private static readonly string[] StandardMethods =
            { "GET", "HEAD", "POST", "PUT", "DELETE", "OPTIONS", "TRACE", "PATCH" };

        private static readonly string[] ConnectionFields = { "Keep-Alive", "Proxy-Connection", "TE" };

        private static readonly string[] UpgradeFields =
        {
            "Upgrade",
            "HTTP2-Settings",
            "Sec-WebSocket-Key",
            "Sec-WebSocket-Version",
            "Sec-WebSocket-Protocol",
            "Sec-WebSocket-Extensions",
        };

        // Stages 1 and 2: the limits, then the head's syntax. Stage 1 checks every limit that can be read at that point:
        // the whole request's size, the head's limits, and, once the head is known to be well formed, the body budgets of
        // the one framing that it declares (CheckBodyBudgets). A head that is not well formed declares no framing, so its
        // syntax is refused next.
        internal static RawFailure? ReadHead(
            byte[] data, RawImageLimits limits, CancellationToken token, out RawParsedRequest parsed)
        {
            parsed = null;
            token.ThrowIfCancellationRequested();
            if ((long)data.Length > (long)limits.HeadBytes + limits.EntityBytes + limits.ChunkFramingBytes)
            {
                return Fail(RawCodes.InputTooLarge);
            }

            int headLength = HeadLength(data, Math.Min(data.Length, limits.HeadBytes));
            if (headLength < 0 && data.Length >= limits.HeadBytes)
            {
                return Fail(RawCodes.InputTooLarge);
            }

            int scanEnd = headLength < 0 ? data.Length : headLength;
            RawFailure? limit = CheckHeadLimits(data, scanEnd, limits);
            if (limit != null)
            {
                return limit;
            }

            token.ThrowIfCancellationRequested();
            RawFailure? syntax = ReadHeadSyntax(data, scanEnd, headLength, token, out RawParsedRequest head);
            if (syntax != null)
            {
                return syntax;
            }

            limit = CheckBodyBudgets(data, head, limits, token);
            if (limit != null)
            {
                return limit;
            }

            parsed = head;
            return null;
        }

        // Stages 3 to 13 for an image that may be sent.
        internal static RawFailure? CheckImage(
            byte[] data, RawParsedRequest parsed, ReplayRawTarget target, RawImageLimits limits, CancellationToken token)
        {
            RawFailure? failure = CheckMethod(parsed.Method) ?? CheckTarget(parsed, target) ?? CheckVersion(parsed);
            if (failure != null)
            {
                return failure;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < parsed.Fields.Count; index++)
            {
                if (!seen.Add(parsed.Fields[index].Key))
                {
                    return Fail(RawCodes.DuplicateField, parsed.FieldLines[index]);
                }
            }

            failure = FirstField(parsed, name => ReplayControlHeaders.Names.Contains(name, StringComparer.OrdinalIgnoreCase),
                ReplayControlHeaders.Code);
            if (failure != null)
            {
                return failure;
            }

            int host = IndexOf(parsed, "Host");
            if (host < 0)
            {
                return Fail(RawCodes.Host);
            }

            if (!NamesTarget(parsed.Fields[host].Value, parsed, target))
            {
                return Fail(RawCodes.Host, parsed.FieldLines[host]);
            }

            for (int index = 0; index < parsed.Fields.Count; index++)
            {
                string name = parsed.Fields[index].Key;
                if ((Is(name, "Connection") && !string.Equals(parsed.Fields[index].Value, "close", StringComparison.Ordinal)) ||
                    ConnectionFields.Any(field => Is(name, field)))
                {
                    return Fail(RawCodes.Connection, parsed.FieldLines[index]);
                }
            }

            for (int index = 0; index < parsed.Fields.Count; index++)
            {
                string name = parsed.Fields[index].Key;
                if (Is(name, "Expect"))
                {
                    return Fail(RawCodes.Expect, parsed.FieldLines[index]);
                }

                if (UpgradeFields.Any(field => Is(name, field)))
                {
                    return Fail(RawCodes.Upgrade, parsed.FieldLines[index]);
                }
            }

            failure = FirstField(parsed, name => Is(name, "Trailer"), RawCodes.Trailer) ??
                ReadFraming(data, parsed, limits, token);
            if (failure != null)
            {
                return failure;
            }

            bool hasBody = parsed.Framing == RawFraming.Chunked || parsed.EntityLength > 0;
            return parsed.Method is "HEAD" or "TRACE" && hasBody ? Fail(RawCodes.BodyNotAllowed, 1) : null;
        }

        // The checks of the Raw-to-Parsed conversion, in this order: the version, the target's
        // syntax, routing and framing. Parsed mode checks the rest in its own words.
        internal static RawFailure? CheckEditor(
            byte[] data, RawParsedRequest parsed, ReplayRawTarget target, RawImageLimits limits, CancellationToken token)
        {
            RawFailure? failure = CheckVersion(parsed) ?? CheckTarget(parsed, target);
            if (failure != null)
            {
                return failure;
            }

            for (int index = 0; index < parsed.Fields.Count; index++)
            {
                if (Is(parsed.Fields[index].Key, "Host") && !NamesTarget(parsed.Fields[index].Value, parsed, target))
                {
                    return Fail(RawCodes.Host, parsed.FieldLines[index]);
                }
            }

            return ReadFraming(data, parsed, limits, token);
        }

        // Whether a method is a raw request's: a token of 1 to 64 characters, never CONNECT, and a standard method only in
        // its own case.
        internal static RawFailure? CheckMethod(string method)
        {
            if (method.Length == 0 || method.Length > RawRequest.MaxMethodChars ||
                !method.All(RequestReplayDraft.IsTokenCharacter) ||
                method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase) ||
                StandardMethods.Any(standard =>
                    method.Equals(standard, StringComparison.OrdinalIgnoreCase) &&
                    !method.Equals(standard, StringComparison.Ordinal)))
            {
                return Fail(RawCodes.Method, 1);
            }

            return null;
        }

        // A request whose method can change server state: anything but GET, HEAD and OPTIONS, and GET or OPTIONS with a
        // body or chunked framing.
        internal static bool UsesUnsafeMethod(RawParsedRequest parsed) =>
            RequestReplayDraft.IsUnsafeMethod(parsed.Method) ||
            (parsed.Method is "GET" or "OPTIONS" &&
                (parsed.Framing == RawFraming.Chunked || parsed.EntityLength > 0));

        // The length of the head, up to and including the CRLF CRLF that ends it, if it ends within the first limit bytes;
        // -1 otherwise.
        internal static int HeadLength(byte[] data, int limit)
        {
            for (int index = 0; index + 3 < limit; index++)
            {
                if (data[index] == '\r' && data[index + 1] == '\n' && data[index + 2] == '\r' && data[index + 3] == '\n')
                {
                    return index + 4;
                }
            }

            return -1;
        }

        private static RawFailure? CheckHeadLimits(byte[] data, int scanEnd, RawImageLimits limits)
        {
            int requestLineEnd = IndexOfCrLf(data, 0, Math.Min(scanEnd, limits.RequestLineBytes));
            if (requestLineEnd < 0 && scanEnd >= limits.RequestLineBytes)
            {
                return Fail(RawCodes.InputTooLarge);
            }

            int lineEnd = requestLineEnd < 0 ? scanEnd : requestLineEnd;
            int method = Array.IndexOf(data, (byte)' ', 0, lineEnd);
            if ((method < 0 ? lineEnd : method) > limits.MethodChars)
            {
                return Fail(RawCodes.InputTooLarge);
            }

            int breaks = 0;
            for (int index = 0; index + 1 < scanEnd; index++)
            {
                if (data[index] == '\r' && data[index + 1] == '\n')
                {
                    breaks++;
                    index++;
                }
            }

            // The request line's break and the empty line's are not field lines.
            int fieldLines = Math.Max(0, breaks - 2);
            return fieldLines > limits.FieldLines ? Fail(RawCodes.InputTooLarge) : null;
        }

        private static RawFailure? ReadHeadSyntax(
            byte[] data, int scanEnd, int headLength, CancellationToken token, out RawParsedRequest parsed)
        {
            parsed = null;
            string method = null;
            string requestTarget = null;
            string version = null;
            var fields = new List<KeyValuePair<string, string>>();
            var fieldText = new List<string>();
            var fieldLines = new List<int>();
            int line = 1;
            int position = 0;
            while (position < scanEnd)
            {
                if ((line & 63) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }

                int lineEnd = -1;
                for (int index = position; index < scanEnd; index++)
                {
                    byte value = data[index];
                    if (value == '\r')
                    {
                        if (index + 1 < scanEnd && data[index + 1] == '\n')
                        {
                            lineEnd = index;
                            break;
                        }

                        return Fail(RawCodes.Invalid, line);
                    }

                    if (value == '\n' || value >= 0x7F || (value < 0x20 && value != '\t'))
                    {
                        return Fail(RawCodes.Invalid, line);
                    }
                }

                if (lineEnd < 0)
                {
                    return Fail(RawCodes.Invalid, line);
                }

                if (line == 1)
                {
                    if (!ReadRequestLine(data, position, lineEnd, out method, out requestTarget, out version))
                    {
                        return Fail(RawCodes.Invalid, line);
                    }
                }
                else if (lineEnd == position)
                {
                    // The empty line, which only the end of the head can be: HeadLength found the first CRLF CRLF.
                    break;
                }
                else
                {
                    if (!ReadField(data, position, lineEnd, out KeyValuePair<string, string> field))
                    {
                        return Fail(RawCodes.Invalid, line);
                    }

                    fields.Add(field);
                    fieldText.Add(Ascii(data, position, lineEnd));
                    fieldLines.Add(line);
                }

                position = lineEnd + 2;
                line++;
            }

            if (headLength < 0)
            {
                return Fail(RawCodes.Invalid, line);
            }

            parsed = new RawParsedRequest
            {
                HeadLength = headLength,
                Method = method,
                RequestTarget = requestTarget,
                Version = version,
                Fields = fields,
                FieldText = fieldText,
                FieldLines = fieldLines,
            };
            return null;
        }

        // method SP request-target SP version, with single spaces and nothing else. The method, the target and the
        // version are checked in their own stages.
        private static bool ReadRequestLine(
            byte[] data, int start, int end, out string method, out string requestTarget, out string version)
        {
            method = requestTarget = version = null;
            int first = Array.IndexOf(data, (byte)' ', start, end - start);
            if (first <= start)
            {
                return false;
            }

            int second = Array.IndexOf(data, (byte)' ', first + 1, end - first - 1);
            if (second <= first + 1 || second + 1 >= end ||
                Array.IndexOf(data, (byte)' ', second + 1, end - second - 1) >= 0 ||
                Array.IndexOf(data, (byte)'\t', start, end - start) >= 0)
            {
                return false;
            }

            method = Ascii(data, start, first);
            requestTarget = Ascii(data, first + 1, second);
            version = Ascii(data, second + 1, end);
            return true;
        }

        // name ":" OWS value OWS: a token name with nothing between it and the colon, never a folded line, and a value
        // of visible characters, spaces and tabs (control characters are refused before).
        private static bool ReadField(byte[] data, int start, int end, out KeyValuePair<string, string> field)
        {
            field = default;
            if (data[start] is (byte)' ' or (byte)'\t')
            {
                return false;
            }

            int colon = Array.IndexOf(data, (byte)':', start, end - start);
            if (colon <= start)
            {
                return false;
            }

            for (int index = start; index < colon; index++)
            {
                if (!RequestReplayDraft.IsTokenCharacter((char)data[index]))
                {
                    return false;
                }
            }

            int valueStart = colon + 1;
            int valueEnd = end;
            while (valueStart < valueEnd && data[valueStart] is (byte)' ' or (byte)'\t')
            {
                valueStart++;
            }

            while (valueEnd > valueStart && data[valueEnd - 1] is (byte)' ' or (byte)'\t')
            {
                valueEnd--;
            }

            field = new KeyValuePair<string, string>(Ascii(data, start, colon), Ascii(data, valueStart, valueEnd));
            return true;
        }

        // origin-form, or an absolute form of the target's scheme and authority, exactly as the target writes them (lower
        // case; the authority with or without its port), followed by an origin-form. Either must also join the target's
        // origin as a URL for recording. An HTTP server that reads absolute-form, Kestrel included, recognizes only a
        // lower-case scheme and compares Host with the URI's lower-case authority.
        private static RawFailure? CheckTarget(RawParsedRequest parsed, ReplayRawTarget target)
        {
            string text = parsed.RequestTarget;
            if (text.Length > 0 && text[0] == '/')
            {
                if (!IsOriginForm(text))
                {
                    return Fail(RawCodes.Target, 1);
                }

                parsed.AbsoluteForm = false;
                parsed.OriginForm = text;
                return JoinRequestUri(parsed, target);
            }

            int schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd <= 0 || !string.Equals(text[..schemeEnd], target.Scheme, StringComparison.Ordinal))
            {
                return Fail(RawCodes.Target, 1);
            }

            int authorityStart = schemeEnd + 3;
            int slash = text.IndexOf('/', authorityStart);
            if (slash < 0)
            {
                return Fail(RawCodes.Target, 1);
            }

            string authority = text[authorityStart..slash];
            string originForm = text[slash..];
            if (!(string.Equals(authority, target.Authority, StringComparison.Ordinal) ||
                    string.Equals(authority, target.AuthorityWithPort, StringComparison.Ordinal)) ||
                !IsOriginForm(originForm))
            {
                return Fail(RawCodes.Target, 1);
            }

            parsed.AbsoluteForm = true;
            parsed.AbsoluteAuthority = authority;
            parsed.OriginForm = originForm;
            return JoinRequestUri(parsed, target);
        }

        // The target's origin and the origin form joined as strings, with no canonicalization of the path or query.
        private static RawFailure? JoinRequestUri(RawParsedRequest parsed, ReplayRawTarget target)
        {
            var options = new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true };
            if (!Uri.TryCreate(target.Scheme + "://" + target.Authority + parsed.OriginForm, in options, out Uri uri))
            {
                return Fail(RawCodes.Target, 1);
            }

            parsed.RequestUri = uri;
            return null;
        }

        // A path of pchar and '/', never starting with "//", then an optional '?' and a query of pchar, '/' and '?'; '%'
        // only with two hexadecimal digits, and never a fragment. A '?' may appear anywhere after the first '/', since
        // the query admits it too, so one pass over pchar, '/' and '?' checks both.
        private static bool IsOriginForm(string text)
        {
            if (text.Length == 0 || text[0] != '/' || text.StartsWith("//", StringComparison.Ordinal))
            {
                return false;
            }

            for (int index = 0; index < text.Length; index++)
            {
                char character = text[index];
                if (character == '%')
                {
                    if (index + 2 >= text.Length ||
                        !char.IsAsciiHexDigit(text[index + 1]) ||
                        !char.IsAsciiHexDigit(text[index + 2]))
                    {
                        return false;
                    }

                    index += 2;
                }
                else if (character is not ('/' or '?') && !IsPathCharacter(character))
                {
                    return false;
                }
            }

            return true;
        }

        // RFC 3986 pchar, besides pct-encoded: unreserved, sub-delims, ':' and '@'.
        private static bool IsPathCharacter(char character) =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '.' or '_' or '~' or '!' or '$' or '&' or '\'' or '(' or ')' or '*' or '+' or ',' or
                ';' or '=' or ':' or '@';

        private static RawFailure? CheckVersion(RawParsedRequest parsed) =>
            string.Equals(parsed.Version, ExpectedVersion, StringComparison.Ordinal) ? null : Fail(RawCodes.Version, 1);

        // Stage 12: Content-Length or chunked framing, never both, and nothing after the body. The entity and chunk
        // budgets are checked as the body's bytes are read, before any allocation that they bound.
        private static RawFailure? ReadFraming(
            byte[] data, RawParsedRequest parsed, RawImageLimits limits, CancellationToken token)
        {
            int contentLength = -1;
            int transferEncoding = -1;
            for (int index = 0; index < parsed.Fields.Count; index++)
            {
                string name = parsed.Fields[index].Key;
                if (Is(name, "Content-Length"))
                {
                    if (contentLength >= 0 || transferEncoding >= 0)
                    {
                        return Fail(RawCodes.Framing, parsed.FieldLines[index]);
                    }

                    contentLength = index;
                }
                else if (Is(name, "Transfer-Encoding"))
                {
                    if (contentLength >= 0 || transferEncoding >= 0)
                    {
                        return Fail(RawCodes.Framing, parsed.FieldLines[index]);
                    }

                    transferEncoding = index;
                }
            }

            long wireBody = data.Length - parsed.HeadLength;
            if (transferEncoding >= 0)
            {
                return string.Equals(parsed.Fields[transferEncoding].Value, "chunked", StringComparison.Ordinal)
                    ? ReadChunked(data, parsed, limits, token)
                    : Fail(RawCodes.Framing, parsed.FieldLines[transferEncoding]);
            }

            if (contentLength >= 0)
            {
                string value = parsed.Fields[contentLength].Value;
                if (!IsContentLength(value, out long length))
                {
                    return Fail(RawCodes.Framing, parsed.FieldLines[contentLength]);
                }

                if (wireBody > limits.EntityBytes)
                {
                    return Fail(RawCodes.InputTooLarge);
                }

                if (length != wireBody)
                {
                    return Fail(RawCodes.Framing, parsed.FieldLines[contentLength]);
                }

                parsed.Framing = RawFraming.ContentLength;
                parsed.EntityLength = length;
                return null;
            }

            if (wireBody != 0)
            {
                return Fail(RawCodes.Framing);
            }

            parsed.Framing = RawFraming.None;
            parsed.EntityLength = 0;
            return null;
        }

        // chunk-size CRLF chunk-data CRLF, sizes of 1 to 8 hexadecimal digits without a leading zero, then 0 CRLF CRLF:
        // no chunk extension and no trailer field (replay.raw.trailer), and nothing after it.
        private static RawFailure? ReadChunked(
            byte[] data, RawParsedRequest parsed, RawImageLimits limits, CancellationToken token)
        {
            var chunks = new List<(int, int)>();
            RawFailure? failure = ScanChunks(
                data, parsed.HeadLength, limits, budgetsOnly: false, chunks, token, out long entity);
            if (failure != null)
            {
                return failure;
            }

            parsed.Framing = RawFraming.Chunked;
            parsed.EntityLength = entity;
            parsed.Chunks = chunks;
            return null;
        }

        // Stage 1's body budgets, which only a well-formed head can declare, with exactly one framing field: a
        // Content-Length body over the entity budget, or a chunked body over a budget before its first structural fault.
        // Any other framing is stage 12's to refuse.
        private static RawFailure? CheckBodyBudgets(
            byte[] data, RawParsedRequest parsed, RawImageLimits limits, CancellationToken token)
        {
            int framing = -1;
            for (int index = 0; index < parsed.Fields.Count; index++)
            {
                string name = parsed.Fields[index].Key;
                if (Is(name, "Content-Length") || Is(name, "Transfer-Encoding"))
                {
                    if (framing >= 0)
                    {
                        return null;
                    }

                    framing = index;
                }
            }

            if (framing < 0)
            {
                return null;
            }

            if (Is(parsed.Fields[framing].Key, "Content-Length"))
            {
                return data.Length - parsed.HeadLength > limits.EntityBytes ? Fail(RawCodes.InputTooLarge) : null;
            }

            return string.Equals(parsed.Fields[framing].Value, "chunked", StringComparison.Ordinal)
                ? ScanChunks(data, parsed.HeadLength, limits, budgetsOnly: true, chunks: null, token, out _)
                : null;
        }

        // A chunked body from start, in the order of its bytes: each chunk line, its data and CRLF, then the last chunk
        // and the empty line. Each budget is checked as its bytes are read, before what it bounds is kept. With
        // budgetsOnly, only a budget fails and the first structural fault ends the scan, since stage 12 refuses it;
        // otherwise that fault is refused as framing, or as a trailer.
        private static RawFailure? ScanChunks(
            byte[] data,
            int start,
            RawImageLimits limits,
            bool budgetsOnly,
            List<(int, int)> chunks,
            CancellationToken token,
            out long entity)
        {
            entity = 0;
            long framing = 0;
            int position = start;
            RawFailure? Fault(string code) => budgetsOnly ? null : Fail(code);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int window = (int)Math.Min(data.Length, (long)position + limits.ChunkLineBytes);
                int lineEnd = IndexOfCrLf(data, position, window);
                if (lineEnd < 0)
                {
                    return data.Length - position >= limits.ChunkLineBytes
                        ? Fail(RawCodes.InputTooLarge)
                        : Fault(RawCodes.Framing);
                }

                framing += lineEnd - position + 2;
                if (framing > limits.ChunkFramingBytes)
                {
                    return Fail(RawCodes.InputTooLarge);
                }

                if (Array.IndexOf(data, (byte)';', position, lineEnd - position) >= 0)
                {
                    return Fault(RawCodes.Trailer);
                }

                if (!IsChunkSize(data, position, lineEnd, out long size))
                {
                    return Fault(RawCodes.Framing);
                }

                position = lineEnd + 2;
                if (size == 0)
                {
                    if (position + 1 < data.Length && data[position] == '\r' && data[position + 1] == '\n')
                    {
                        framing += 2;
                        if (framing > limits.ChunkFramingBytes)
                        {
                            return Fail(RawCodes.InputTooLarge);
                        }

                        position += 2;
                        break;
                    }

                    // Anything before the next line break is a trailer field.
                    int next = IndexOfCrLf(data, position, data.Length);
                    return next > position ? Fault(RawCodes.Trailer) : Fault(RawCodes.Framing);
                }

                entity += size;
                if (entity > limits.EntityBytes)
                {
                    return Fail(RawCodes.InputTooLarge);
                }

                if (data.Length - position < size + 2)
                {
                    return Fault(RawCodes.Framing);
                }

                chunks?.Add((position, (int)size));
                position += (int)size;
                if (data[position] != '\r' || data[position + 1] != '\n')
                {
                    return Fault(RawCodes.Framing);
                }

                framing += 2;
                if (framing > limits.ChunkFramingBytes)
                {
                    return Fail(RawCodes.InputTooLarge);
                }

                position += 2;
            }

            return position != data.Length ? Fault(RawCodes.Framing) : null;
        }

        // 1*DIGIT of at most 19 digits, without a leading zero unless it is 0, that fits in 64 bits.
        private static bool IsContentLength(string value, out long length)
        {
            length = 0;
            return value.Length is >= 1 and <= 19 &&
                value.All(char.IsAsciiDigit) &&
                (value.Length == 1 || value[0] != '0') &&
                long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out length);
        }

        private static bool IsChunkSize(byte[] data, int start, int end, out long size)
        {
            size = 0;
            int length = end - start;
            if (length is < 1 or > 8 || (length > 1 && data[start] == '0'))
            {
                return false;
            }

            for (int index = start; index < end; index++)
            {
                int digit = HexValue((char)data[index]);
                if (digit < 0)
                {
                    return false;
                }

                size = (size << 4) | (uint)digit;
            }

            return true;
        }

        // The value of a hexadecimal digit; -1 for any other character.
        internal static int HexValue(char character) => character switch
        {
            >= '0' and <= '9' => character - '0',
            >= 'a' and <= 'f' => character - 'a' + 10,
            >= 'A' and <= 'F' => character - 'A' + 10,
            _ => -1,
        };

        // The index of a CRLF that lies wholly between start and limit; -1 if none does.
        private static int IndexOfCrLf(byte[] data, int start, int limit)
        {
            for (int index = start; index + 1 < limit; index++)
            {
                if (data[index] == '\r' && data[index + 1] == '\n')
                {
                    return index;
                }
            }

            return -1;
        }

        private static RawFailure? FirstField(RawParsedRequest parsed, Func<string, bool> matches, string code)
        {
            for (int index = 0; index < parsed.Fields.Count; index++)
            {
                if (matches(parsed.Fields[index].Key))
                {
                    return Fail(code, parsed.FieldLines[index]);
                }
            }

            return null;
        }

        private static int IndexOf(RawParsedRequest parsed, string name)
        {
            for (int index = 0; index < parsed.Fields.Count; index++)
            {
                if (Is(parsed.Fields[index].Key, name))
                {
                    return index;
                }
            }

            return -1;
        }

        // Host names the target. With an absolute form, it is exactly the authority written there, port form included (RFC
        // 9112 3.2); with an origin form, the target's authority with or without the port, ignoring ASCII case.
        private static bool NamesTarget(string host, RawParsedRequest parsed, ReplayRawTarget target) =>
            parsed.AbsoluteForm
                ? string.Equals(host, parsed.AbsoluteAuthority, StringComparison.Ordinal)
                : string.Equals(host, target.Authority, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(host, target.AuthorityWithPort, StringComparison.OrdinalIgnoreCase);

        private static bool Is(string name, string expected) =>
            string.Equals(name, expected, StringComparison.OrdinalIgnoreCase);

        private static string Ascii(byte[] data, int start, int end) =>
            System.Text.Encoding.ASCII.GetString(data, start, end - start);

        private static RawFailure Fail(string code, int? line = null) => new(code, line);
    }
}
