// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XMAT.WebServiceCapture;

namespace XMAT.Operations.Replay
{
    // How a raw send's reader ended: a complete final response, TooLarge included; a protocol failure in the
    // bytes that it read, an early end of stream included; or a read call that failed. A failure carries the code of where
    // it happened.
    internal enum RawReadKind
    {
        Response,
        ReaderFailure,
        ReadFailure,
    }

    internal sealed record RawReadResult(RawReadKind Kind, RawResponse Response, string Code);

    // A raw send's response: the status line as received, the fields in order (obs-text as Latin-1), and the
    // decoded body. When chunked framing was consumed, every Transfer-Encoding and Trailer field is removed and the decoded
    // length is added as Content-Length. A TooLarge response keeps its head as received and no body.
    internal sealed class RawResponse
    {
        internal RawResponse(
            string statusLine,
            int statusCode,
            string reasonPhrase,
            Version version,
            IReadOnlyList<KeyValuePair<string, string>> fields,
            byte[] body,
            bool tooLarge,
            string framing,
            int trailerFields)
        {
            StatusLine = statusLine;
            StatusCode = statusCode;
            ReasonPhrase = reasonPhrase;
            Version = version;
            Fields = fields;
            Body = body;
            TooLarge = tooLarge;
            Framing = framing;
            TrailerFields = trailerFields;
        }

        internal string StatusLine { get; }

        internal int StatusCode { get; }

        internal string ReasonPhrase { get; }

        // As received: HTTP/1.2 to HTTP/1.9 keep their minor version.
        internal Version Version { get; }

        internal IReadOnlyList<KeyValuePair<string, string>> Fields { get; }

        internal byte[] Body { get; }

        internal bool TooLarge { get; }

        // content-length, chunked, close or none.
        internal string Framing { get; }

        // The fields of a chunked body's trailer section: counted, never merged and not kept.
        internal int TrailerFields { get; }

        internal ReplayMessage ToMessage() => new(StatusLine, Fields, null, Body);
    }

    // Reads one HTTP/1.x response from a raw send's connection, within fixed budgets and without reading
    // past it. Interim responses other than 101 are skipped; the final response uses HTTP framing precedence. An end
    // of stream, or a read that fails, is classified by where it happens: before a complete final status line,
    // replay.send.failed; inside the final head, replay.raw.response-invalid; inside a framed body,
    // replay.raw.response-incomplete. In a close-delimited body, the end of stream completes the response, and a failed
    // read is replay.raw.response-incomplete.
    internal sealed class RawResponseReader
    {
        internal const int MaxHeadBytes = 65_536;
        internal const int MaxFieldLines = 256;
        internal const int MaxInterimResponses = 8;
        internal const int MaxChunkLineBytes = 8_192;
        internal const int MaxChunkFramingBytes = 1_048_576;
        internal const int MaxTrailerBytes = 16_384;
        internal const int MaxTrailerFields = 64;

        internal const string ContentLengthFraming = "content-length";
        internal const string ChunkedFraming = "chunked";
        internal const string CloseFraming = "close";
        internal const string NoFraming = "none";

        private const int ReadBytes = 16 * 1024;

        private readonly Stream _stream;
        private readonly long _maxBodyBytes;
        private readonly bool _headRequest;
        private byte[] _buffer = new byte[ReadBytes];
        private int _start;
        private int _end;
        private Phase _phase = Phase.Status;
        private int _headBytes;

        // headRequest: the request was HEAD, so the final response has no body. maxBodyBytes: the response limit, beyond
        // which the response is TooLarge.
        internal RawResponseReader(Stream stream, long maxBodyBytes, bool headRequest)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            ArgumentOutOfRangeException.ThrowIfNegative(maxBodyBytes);
            _maxBodyBytes = Math.Min(maxBodyBytes, Array.MaxLength);
            _headRequest = headRequest;
        }

        private enum Phase
        {
            Status,
            FinalHead,
            FramedBody,
            CloseBody,
        }

        // The response, or how reading it failed. Never throws: a read call that fails, or a cancellation, which only the
        // arbiter's own token can cause after it committed, is a ReadFailure.
        internal async Task<RawReadResult> ReadAsync(CancellationToken token)
        {
            try
            {
                return new RawReadResult(RawReadKind.Response, await ReadResponseAsync(token).ConfigureAwait(false), null);
            }
            catch (InvalidResponseException failure)
            {
                return new RawReadResult(RawReadKind.ReaderFailure, null, failure.Code);
            }
            catch (EndOfResponseException)
            {
                return new RawReadResult(RawReadKind.ReaderFailure, null, EndCode());
            }
            catch (Exception)
            {
                return new RawReadResult(RawReadKind.ReadFailure, null, EndCode());
            }
        }

        private string EndCode() => _phase switch
        {
            Phase.Status => ReplaySender.SendFailedCode,
            Phase.FinalHead => RawCodes.ResponseInvalid,
            _ => RawCodes.ResponseIncomplete,
        };

        private async Task<RawResponse> ReadResponseAsync(CancellationToken token)
        {
            int interim = 0;
            while (true)
            {
                byte[] statusLine = await ReadHeadLineAsync(token).ConfigureAwait(false);
                (Version version, int statusCode, string reason) = ParseStatusLine(statusLine);
                bool isInterim = statusCode < 200;
                if (statusCode == 101 || (isInterim && ++interim > MaxInterimResponses))
                {
                    throw Invalid();
                }

                if (!isInterim)
                {
                    _phase = Phase.FinalHead;
                }

                List<KeyValuePair<string, string>> fields = await ReadHeadFieldsAsync(token).ConfigureAwait(false);
                CheckFramingWithoutBody(version, fields);
                if (!isInterim)
                {
                    return await ReadFinalAsync(Latin1(statusLine), statusCode, reason, version, fields, token)
                        .ConfigureAwait(false);
                }
            }
        }

        // The final response's framing, in precedence order: HEAD or no-body status, chunked encoding, Content-Length,
        // then connection close.
        private async Task<RawResponse> ReadFinalAsync(
            string statusLine,
            int statusCode,
            string reason,
            Version version,
            List<KeyValuePair<string, string>> fields,
            CancellationToken token)
        {
            RawResponse Make(IReadOnlyList<KeyValuePair<string, string>> kept, byte[] body, bool tooLarge, string framing,
                int trailers = 0) =>
                new(statusLine, statusCode, reason, version, kept.ToArray(), body, tooLarge, framing, trailers);

            if (_headRequest || statusCode is 204 or 304)
            {
                return Make(fields, Array.Empty<byte>(), false, NoFraming);
            }

            List<string> transferEncodings = Values(fields, "Transfer-Encoding");
            if (transferEncodings.Count > 0)
            {
                if (transferEncodings.Count != 1 ||
                    !transferEncodings[0].Equals("chunked", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidResponseException(RawCodes.ResponseCoding);
                }

                _phase = Phase.FramedBody;
                (byte[] body, int trailers) = await ReadChunkedAsync(token).ConfigureAwait(false);
                if (body == null)
                {
                    return Make(fields, Array.Empty<byte>(), true, ChunkedFraming);
                }

                var normalized = fields
                    .Where(field => !Is(field.Key, "Transfer-Encoding") && !Is(field.Key, "Trailer"))
                    .Append(new KeyValuePair<string, string>(
                        "Content-Length", body.LongLength.ToString(CultureInfo.InvariantCulture)))
                    .ToList();
                return Make(normalized, body, false, ChunkedFraming, trailers);
            }

            List<string> contentLengths = Values(fields, "Content-Length");
            if (contentLengths.Count > 0)
            {
                long length = ParseContentLength(contentLengths);
                if (length > _maxBodyBytes)
                {
                    return Make(fields, Array.Empty<byte>(), true, ContentLengthFraming);
                }

                _phase = Phase.FramedBody;
                var body = new byte[length];
                await ReadExactAsync(body, 0, body.Length, token).ConfigureAwait(false);
                return Make(fields, body, false, ContentLengthFraming);
            }

            _phase = Phase.CloseBody;
            byte[] rest = await ReadToEndAsync(token).ConfigureAwait(false);
            return rest == null
                ? Make(fields, Array.Empty<byte>(), true, CloseFraming)
                : Make(fields, rest, false, CloseFraming);
        }

        // Framing checks for final and interim response heads: HTTP/1.0 with any Transfer-Encoding, and
        // Transfer-Encoding with Content-Length, are invalid.
        private static void CheckFramingWithoutBody(Version version, List<KeyValuePair<string, string>> fields)
        {
            bool transferEncoding = fields.Any(field => Is(field.Key, "Transfer-Encoding"));
            if (transferEncoding && (version.Minor == 0 || fields.Any(field => Is(field.Key, "Content-Length"))))
            {
                throw Invalid();
            }
        }

        // Content-Length values, each field a list of 1*DIGIT of at most 19 digits that fits in 64 bits, all equal.
        private static long ParseContentLength(List<string> values)
        {
            long? length = null;
            foreach (string value in values)
            {
                foreach (string element in value.Split(','))
                {
                    string digits = element.Trim(' ', '\t');
                    if (digits.Length is 0 or > 19 || !digits.All(char.IsAsciiDigit) ||
                        !long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) ||
                        (length != null && length != parsed))
                    {
                        throw Invalid();
                    }

                    length = parsed;
                }
            }

            return length.Value;
        }

        // A chunked body, decoded, and the number of its trailer fields; a null body when it would pass the response
        // limit (TooLarge). Chunk lines count against their own limit and, with each chunk's CRLF, against the chunk
        // framing budget; the trailer section, its empty line included, against the trailer budgets.
        private async Task<(byte[] Body, int Trailers)> ReadChunkedAsync(CancellationToken token)
        {
            using var body = new MemoryStream();
            long framing = 0;
            while (true)
            {
                byte[] line = await ReadLineAsync(MaxChunkLineBytes, token).ConfigureAwait(false);
                framing += line.Length + 2;
                if (framing > MaxChunkFramingBytes)
                {
                    throw Invalid();
                }

                long size = ParseChunkLine(line);
                if (size == 0)
                {
                    break;
                }

                if (size > _maxBodyBytes - body.Length)
                {
                    return (null, 0);
                }

                var chunk = new byte[size];
                await ReadExactAsync(chunk, 0, chunk.Length, token).ConfigureAwait(false);
                body.Write(chunk);
                byte[] end = new byte[2];
                await ReadExactAsync(end, 0, 2, token).ConfigureAwait(false);
                framing += 2;
                if (end[0] != '\r' || end[1] != '\n' || framing > MaxChunkFramingBytes)
                {
                    throw Invalid();
                }
            }

            int trailerBytes = 0;
            int trailers = 0;
            while (true)
            {
                byte[] line = await ReadLineAsync(MaxTrailerBytes - trailerBytes, token).ConfigureAwait(false);
                trailerBytes += line.Length + 2;
                if (line.Length == 0)
                {
                    return (body.ToArray(), trailers);
                }

                ParseField(line);
                if (++trailers > MaxTrailerFields)
                {
                    throw Invalid();
                }
            }
        }

        // chunk-size [ chunk-ext ]: 1*HEXDIG without overflow, then extensions as RFC 9112 7.1.1 has them, BWS around ';'
        // and '=' included, checked and ignored.
        private static long ParseChunkLine(byte[] line)
        {
            long size = 0;
            int index = 0;
            for (; index < line.Length; index++)
            {
                int digit = RawRequestParser.HexValue((char)line[index]);
                if (digit < 0)
                {
                    break;
                }

                if (size > (long.MaxValue >> 4))
                {
                    throw Invalid();
                }

                size = (size << 4) | (uint)digit;
            }

            if (index == 0)
            {
                throw Invalid();
            }

            while (index < line.Length)
            {
                int semicolon = SkipWhitespace(line, index);
                if (semicolon >= line.Length || line[semicolon] != ';')
                {
                    throw Invalid();
                }

                index = SkipWhitespace(line, semicolon + 1);
                int nameEnd = SkipToken(line, index);
                if (nameEnd == index)
                {
                    throw Invalid();
                }

                index = nameEnd;
                int equals = SkipWhitespace(line, index);
                if (equals < line.Length && line[equals] == '=')
                {
                    int value = SkipWhitespace(line, equals + 1);
                    int valueEnd = value < line.Length && line[value] == '"'
                        ? SkipQuotedString(line, value)
                        : SkipToken(line, value);
                    if (valueEnd <= value)
                    {
                        throw Invalid();
                    }

                    index = valueEnd;
                }
            }

            return size;
        }

        // The status line: HTTP-version SP 3DIGIT SP reason-phrase. HTTP/1.0 to HTTP/1.9 are read, and a status of 100 to
        // 599; the reason is HTAB, SP, VCHAR and obs-text.
        private static (Version Version, int StatusCode, string Reason) ParseStatusLine(byte[] line)
        {
            if (line.Length < 13 ||
                !line.AsSpan(0, 5).SequenceEqual("HTTP/"u8) ||
                line[5] != '1' || line[6] != '.' || !char.IsAsciiDigit((char)line[7]) ||
                line[8] != ' ' ||
                !char.IsAsciiDigit((char)line[9]) || !char.IsAsciiDigit((char)line[10]) ||
                !char.IsAsciiDigit((char)line[11]) ||
                line[12] != ' ')
            {
                throw Invalid();
            }

            int statusCode = ((line[9] - '0') * 100) + ((line[10] - '0') * 10) + (line[11] - '0');
            if (statusCode is < 100 or > 599 || !line.AsSpan(13).ToArray().All(IsFieldValueByte))
            {
                throw Invalid();
            }

            return (new Version(1, line[7] - '0'), statusCode, Latin1(line.AsSpan(13)));
        }

        // name ":" OWS value OWS: a token name with nothing before its colon, never a folded line, and a value of VCHAR,
        // SP, HTAB and obs-text, read as Latin-1.
        private static KeyValuePair<string, string> ParseField(byte[] line)
        {
            if (line.Length == 0 || line[0] is (byte)' ' or (byte)'\t')
            {
                throw Invalid();
            }

            int colon = Array.IndexOf(line, (byte)':');
            if (colon <= 0 || SkipToken(line, 0) != colon)
            {
                throw Invalid();
            }

            int start = colon + 1;
            int end = line.Length;
            while (start < end && line[start] is (byte)' ' or (byte)'\t')
            {
                start++;
            }

            while (end > start && line[end - 1] is (byte)' ' or (byte)'\t')
            {
                end--;
            }

            for (int index = colon + 1; index < line.Length; index++)
            {
                if (!IsFieldValueByte(line[index]))
                {
                    throw Invalid();
                }
            }

            return new KeyValuePair<string, string>(
                Encoding.ASCII.GetString(line, 0, colon), Latin1(line.AsSpan(start, end - start)));
        }

        private async Task<List<KeyValuePair<string, string>>> ReadHeadFieldsAsync(CancellationToken token)
        {
            var fields = new List<KeyValuePair<string, string>>();
            while (true)
            {
                byte[] line = await ReadHeadLineAsync(token).ConfigureAwait(false);
                if (line.Length == 0)
                {
                    return fields;
                }

                if (fields.Count == MaxFieldLines)
                {
                    throw Invalid();
                }

                fields.Add(ParseField(line));
            }
        }

        // A line of a head, which counts with its CRLF against the budget of every head together.
        private async Task<byte[]> ReadHeadLineAsync(CancellationToken token)
        {
            byte[] line = await ReadLineAsync(MaxHeadBytes - _headBytes, token).ConfigureAwait(false);
            _headBytes += line.Length + 2;
            return line;
        }

        // A line ended by CRLF, of at most maxBytes with its CRLF, without them; a bare CR or LF in it is invalid.
        private async Task<byte[]> ReadLineAsync(int maxBytes, CancellationToken token)
        {
            int scanned = 0;
            while (true)
            {
                int lineFeed = Array.IndexOf(_buffer, (byte)'\n', _start + scanned, _end - _start - scanned);
                if (lineFeed >= 0)
                {
                    int length = lineFeed + 1 - _start;
                    if (length > maxBytes || length < 2 || _buffer[lineFeed - 1] != '\r' ||
                        Array.IndexOf(_buffer, (byte)'\r', _start, length - 2) >= 0)
                    {
                        throw Invalid();
                    }

                    byte[] line = _buffer.AsSpan(_start, length - 2).ToArray();
                    _start = lineFeed + 1;
                    return line;
                }

                scanned = _end - _start;
                if (scanned >= maxBytes)
                {
                    throw Invalid();
                }

                if (!await FillAsync(maxBytes, token).ConfigureAwait(false))
                {
                    throw new EndOfResponseException();
                }
            }
        }

        // Exactly count bytes into target: the buffered ones first, then read straight into it.
        private async Task ReadExactAsync(byte[] target, int offset, int count, CancellationToken token)
        {
            int buffered = Math.Min(count, _end - _start);
            Buffer.BlockCopy(_buffer, _start, target, offset, buffered);
            _start += buffered;
            offset += buffered;
            count -= buffered;
            while (count > 0)
            {
                int read = await _stream.ReadAsync(target.AsMemory(offset, count), token).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfResponseException();
                }

                offset += read;
                count -= read;
            }
        }

        // A close-delimited body: everything until the end of stream, or null once it passes the response limit.
        private async Task<byte[]> ReadToEndAsync(CancellationToken token)
        {
            using var body = new MemoryStream();
            body.Write(_buffer, _start, _end - _start);
            _start = _end;
            var chunk = new byte[ReadBytes];
            while (body.Length <= _maxBodyBytes)
            {
                int read = await _stream.ReadAsync(chunk, token).ConfigureAwait(false);
                if (read == 0)
                {
                    return body.ToArray();
                }

                body.Write(chunk, 0, read);
            }

            return null;
        }

        // Reads more into the buffer, keeping the unread bytes and growing it up to what a line of maxBytes needs; false at
        // the end of stream.
        private async Task<bool> FillAsync(int maxBytes, CancellationToken token)
        {
            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }

            if (_end == _buffer.Length)
            {
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, Math.Min(maxBytes, MaxHeadBytes)));
            }

            int read = await _stream.ReadAsync(_buffer.AsMemory(_end), token).ConfigureAwait(false);
            _end += read;
            return read > 0;
        }

        private static List<string> Values(List<KeyValuePair<string, string>> fields, string name) =>
            fields.Where(field => Is(field.Key, name)).Select(field => field.Value).ToList();

        // HTAB, SP, VCHAR and obs-text.
        private static bool IsFieldValueByte(byte value) => value == '\t' || (value >= 0x20 && value != 0x7F);

        private static int SkipWhitespace(byte[] line, int index)
        {
            while (index < line.Length && line[index] is (byte)' ' or (byte)'\t')
            {
                index++;
            }

            return index;
        }

        private static int SkipToken(byte[] line, int index)
        {
            while (index < line.Length && RequestReplayDraft.IsTokenCharacter((char)line[index]))
            {
                index++;
            }

            return index;
        }

        // DQUOTE *( qdtext / quoted-pair ) DQUOTE: the index after it, or -1 when it is not one.
        private static int SkipQuotedString(byte[] line, int index)
        {
            for (index++; index < line.Length; index++)
            {
                byte value = line[index];
                if (value == '"')
                {
                    return index + 1;
                }

                if (value == '\\')
                {
                    if (++index >= line.Length || !IsFieldValueByte(line[index]))
                    {
                        return -1;
                    }
                }
                else if (!IsFieldValueByte(value))
                {
                    return -1;
                }
            }

            return -1;
        }

        private static bool Is(string name, string expected) =>
            string.Equals(name, expected, StringComparison.OrdinalIgnoreCase);

        private static string Latin1(ReadOnlySpan<byte> bytes) => Encoding.Latin1.GetString(bytes);

        private static InvalidResponseException Invalid() => new(RawCodes.ResponseInvalid);

        private sealed class InvalidResponseException(string code) : Exception
        {
            internal string Code { get; } = code;
        }

        private sealed class EndOfResponseException : Exception
        {
        }
    }
}
