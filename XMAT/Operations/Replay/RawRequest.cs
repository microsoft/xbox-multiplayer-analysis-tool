// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.Threading;

namespace XMAT.Operations.Replay
{
    // Limits for tests, which may only lower Raw mode's limits: a value above the production limit, or below 1, throws.
    // So every image, under test limits too, is within the production limits.
    internal sealed class RawImageLimits
    {
        internal RawImageLimits(
            int headBytes = RawRequest.MaxHeadBytes,
            int requestLineBytes = RawRequest.MaxRequestLineBytes,
            int fieldLines = RawRequest.MaxFieldLines,
            int methodChars = RawRequest.MaxMethodChars,
            int entityBytes = RawRequest.MaxEntityBytes,
            int chunkFramingBytes = RawRequest.MaxChunkFramingBytes,
            int chunkLineBytes = RawRequest.MaxChunkLineBytes)
        {
            HeadBytes = Lower(headBytes, RawRequest.MaxHeadBytes, nameof(headBytes));
            RequestLineBytes = Lower(requestLineBytes, RawRequest.MaxRequestLineBytes, nameof(requestLineBytes));
            FieldLines = Lower(fieldLines, RawRequest.MaxFieldLines, nameof(fieldLines));
            MethodChars = Lower(methodChars, RawRequest.MaxMethodChars, nameof(methodChars));
            EntityBytes = Lower(entityBytes, RawRequest.MaxEntityBytes, nameof(entityBytes));
            ChunkFramingBytes = Lower(chunkFramingBytes, RawRequest.MaxChunkFramingBytes, nameof(chunkFramingBytes));
            ChunkLineBytes = Lower(chunkLineBytes, RawRequest.MaxChunkLineBytes, nameof(chunkLineBytes));
        }

        internal static RawImageLimits Production { get; } = new();

        internal int HeadBytes { get; }

        internal int RequestLineBytes { get; }

        internal int FieldLines { get; }

        internal int MethodChars { get; }

        internal int EntityBytes { get; }

        internal int ChunkFramingBytes { get; }

        internal int ChunkLineBytes { get; }

        private static int Lower(int value, int production, string name) =>
            value is >= 1 && value <= production ? value : throw new ArgumentOutOfRangeException(name);
    }

    // An image, or the code and data-free line of the rule that refused it.
    internal sealed record RawImageResult(RawRequestImage Image, string Code, int? Line);

    // A Raw-to-Parsed parse, or the code and data-free line of the rule that refused it.
    internal sealed record RawEditorParseResult(RawEditorParse Parse, string Code, int? Line);

    // What the editor's head text would make, counted without allocating in proportion to it: the head's bytes (its end
    // included), the request line's bytes (its CRLF included), the field lines, the method's characters, the bare LFs
    // that would be written as CRLF, and whether any of them is over Raw mode's production limits.
    internal sealed record RawHeadCounts(
        long HeadBytes, long RequestLineBytes, int FieldLines, long MethodChars, int LineFeedsToConvert, bool OverLimit);

    // A head text, or the code and data-free line of the rule that refused it.
    internal sealed record RawTextResult(string Text, string Code, int? Line);

    // Raw mode's request API: the only ways to an image that may be sent (FromBytes, Prepare), the
    // parse for the Raw-to-Parsed conversion, the counts for the editor, Fix Content-Length, and the fixed messages.
    internal static class RawRequest
    {
        internal const int MaxHeadBytes = 65_536;
        internal const int MaxRequestLineBytes = 16_384;
        internal const int MaxFieldLines = 256;
        internal const int MaxMethodChars = 64;
        internal const int MaxEntityBytes = 67_108_864;
        internal const int MaxChunkFramingBytes = 1_048_576;
        internal const int MaxChunkLineBytes = 8_192;

        // The most that a raw request file may hold: a whole head, a whole entity and all of its chunk framing.
        internal const long MaxFileBytes = (long)MaxHeadBytes + MaxEntityBytes + MaxChunkFramingBytes;

        // A request read as bytes, as the command line reads its file: copied first, then checked in full. With
        // fixContentLength, its one Content-Length is first set to its body's length (Fix Content-Length).
        internal static RawImageResult FromBytes(
            ReplayRawTarget target,
            ReadOnlyMemory<byte> request,
            bool fixContentLength,
            CancellationToken token,
            RawImageLimits limits = null) =>
            RawRequestImage.FromBytes(target, request, fixContentLength, limits ?? RawImageLimits.Production, token);

        // A request from the editor: its head text, with CRLF or LF line ends, and its wire body. The head is converted as
        // ConvertHead says, then the image is checked in full.
        internal static RawImageResult Prepare(
            string headText,
            ReadOnlyMemory<byte> body,
            ReplayRawTarget target,
            CancellationToken token,
            RawImageLimits limits = null) =>
            RawRequestImage.Prepare(headText, body, target, limits ?? RawImageLimits.Production, token);

        // The parse that the Raw-to-Parsed conversion reads. It is never sendable.
        internal static RawEditorParseResult ParseForEditor(
            string headText, ReadOnlyMemory<byte> body, ReplayRawTarget target, CancellationToken token) =>
            RawEditorParse.Parse(headText, body, target, token);

        internal static RawHeadCounts CountHead(string headText) =>
            Scan(headText ?? string.Empty, RawImageLimits.Production, stopEarly: false, null, CancellationToken.None, out _);

        // CountHead for a check that only needs to know whether a head is within these limits: its token is checked, and
        // checkpoint (tests) called, each time another CheckChars characters have been read, and it stops as soon as a limit
        // is passed, with OverLimit and the counts so far.
        internal static RawHeadCounts CountHead(
            string headText, RawImageLimits limits, CancellationToken token, Action<long> checkpoint = null)
        {
            ArgumentNullException.ThrowIfNull(limits);
            return Scan(headText ?? string.Empty, limits, stopEarly: true, checkpoint, token, out _);
        }

        // The characters that a cancellable scan reads between two checks of its token.
        internal const int CheckChars = 65_536;

        private static RawHeadCounts Scan(
            string headText,
            RawImageLimits limits,
            bool stopEarly,
            Action<long> checkpoint,
            CancellationToken token,
            out int contentEnd)
        {
            var progress = new ScanProgress(checkpoint, token);
            contentEnd = ContentEnd(headText, ref progress);
            long bytes = 0;
            long requestLine = -1;
            long method = -1;
            int breaks = 0;
            int lineFeeds = 0;
            for (int index = 0; index < contentEnd; index++)
            {
                progress.Advance(1);
                char character = headText[index];
                if (character == '\r' && index + 1 < contentEnd && headText[index + 1] == '\n')
                {
                    index++;
                    progress.Advance(1);
                }
                else if (character == '\n')
                {
                    lineFeeds++;
                }
                else
                {
                    if (character == ' ' && requestLine < 0 && method < 0)
                    {
                        method = bytes;
                    }

                    bytes++;
                    if (stopEarly && Passed(bytes, requestLine, method, breaks, limits))
                    {
                        return Over(bytes, requestLine, method, breaks, lineFeeds);
                    }

                    continue;
                }

                bytes += 2;
                breaks++;
                if (requestLine < 0)
                {
                    requestLine = bytes;
                }

                if (stopEarly && Passed(bytes, requestLine, method, breaks, limits))
                {
                    return Over(bytes, requestLine, method, breaks, lineFeeds);
                }
            }

            if (requestLine < 0)
            {
                requestLine = bytes + 2;
            }

            if (method < 0)
            {
                method = requestLine - 2;
            }

            // The line breaks at the end, each CRLF or LF: the first two are written as the last line's CRLF and the empty
            // line's, and any more are dropped, so only an LF among the first two is converted.
            int trailing = 0;
            for (int index = contentEnd; index < headText.Length && trailing < 2; index++, trailing++)
            {
                if (headText[index] == '\r')
                {
                    index++;
                }
                else
                {
                    lineFeeds++;
                }
            }

            // The last line's CRLF and the empty line.
            bytes += 4;
            bool overLimit = bytes > limits.HeadBytes || requestLine > limits.RequestLineBytes ||
                breaks > limits.FieldLines || method > limits.MethodChars;
            return new RawHeadCounts(bytes, requestLine, breaks, method, lineFeeds, overLimit);
        }

        // Whether the counts so far already pass a limit, whatever follows: each final count is at least its lower bound here
        // (the head gets its last CRLF and the empty line; a request line that has not ended yet gets its CRLF).
        private static bool Passed(long bytes, long requestLine, long method, int breaks, RawImageLimits limits) =>
            bytes + 4 > limits.HeadBytes ||
            breaks > limits.FieldLines ||
            (requestLine >= 0 ? requestLine : bytes + 2) > limits.RequestLineBytes ||
            (method >= 0 ? method : requestLine >= 0 ? requestLine - 2 : bytes) > limits.MethodChars;

        private static RawHeadCounts Over(long bytes, long requestLine, long method, int breaks, int lineFeeds) =>
            new(bytes + 4, requestLine >= 0 ? requestLine : bytes + 2, breaks, method >= 0 ? method : bytes, lineFeeds, true);

        // Sets the one Content-Length field's value to bodyLength, keeping its name, the OWS around the value and every
        // other character of the text. It needs exactly one Content-Length field and no Transfer-Encoding; a head over
        // Raw mode's limits is refused first. The result is then checked in full by Prepare or FromBytes. The text is
        // never changed in place. checkpoint (tests) is called at each check of the token while the limits are counted.
        internal static RawTextResult FixContentLength(
            string headText, long bodyLength, CancellationToken token, Action<long> checkpoint = null)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(bodyLength);
            headText ??= string.Empty;
            token.ThrowIfCancellationRequested();
            if (CountHead(headText, RawImageLimits.Production, token, checkpoint).OverLimit)
            {
                return new RawTextResult(null, RawCodes.InputTooLarge, null);
            }

            int count = 0;
            int valueStart = -1;
            int valueEnd = -1;
            bool transferEncoding = false;
            int lineStart = 0;
            int line = 1;
            while (lineStart < headText.Length)
            {
                if ((line & 63) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }

                int newline = headText.IndexOf('\n', lineStart);
                int lineEnd = newline < 0 ? headText.Length : newline;
                int contentEnd = lineEnd > lineStart && headText[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;
                if (line > 1)
                {
                    if (contentEnd == lineStart)
                    {
                        // The empty line ends the head.
                        break;
                    }

                    int colon = headText.IndexOf(':', lineStart, contentEnd - lineStart);
                    if (colon > lineStart)
                    {
                        ReadOnlySpan<char> name = headText.AsSpan(lineStart, colon - lineStart);
                        if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                        {
                            count++;
                            valueStart = colon + 1;
                            while (valueStart < contentEnd && headText[valueStart] is ' ' or '\t')
                            {
                                valueStart++;
                            }

                            valueEnd = contentEnd;
                            while (valueEnd > valueStart && headText[valueEnd - 1] is ' ' or '\t')
                            {
                                valueEnd--;
                            }
                        }
                        else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                        {
                            transferEncoding = true;
                        }
                    }
                }

                lineStart = newline < 0 ? headText.Length : newline + 1;
                line++;
            }

            if (count != 1 || transferEncoding)
            {
                return new RawTextResult(null, RawCodes.FixContentLength, null);
            }

            string text = string.Concat(
                headText.AsSpan(0, valueStart),
                bodyLength.ToString(CultureInfo.InvariantCulture),
                headText.AsSpan(valueEnd));
            return new RawTextResult(text, null, null);
        }

        // The fixed message of a Raw mode code (RawCodes); null for any other code.
        internal static string Message(string code) => RawCodes.Message(code);

        // The editor's head text as image bytes: CRLF or LF line ends written as CRLF, the bare LFs counted, and the line
        // breaks at the end making exactly one empty line. A lone CR, an inner empty line or a character outside ASCII is
        // refused, never repaired. The limits are checked first, by a cancellable count that stops at the first one passed,
        // before anything is allocated; the token is checked, and checkpoint (tests) called, every CheckChars characters.
        internal static RawFailure? ConvertHead(
            string headText,
            RawImageLimits limits,
            Action<long> checkpoint,
            CancellationToken token,
            out byte[] head,
            out int convertedLineFeeds)
        {
            head = null;
            convertedLineFeeds = 0;
            headText ??= string.Empty;
            RawHeadCounts counts = Scan(headText, limits, stopEarly: true, checkpoint, token, out int contentEnd);
            if (counts.OverLimit)
            {
                return new RawFailure(RawCodes.InputTooLarge, null);
            }

            if (contentEnd == 0)
            {
                return new RawFailure(RawCodes.Invalid, 1);
            }

            var progress = new ScanProgress(checkpoint, token);
            var bytes = new byte[counts.HeadBytes];
            int written = 0;
            int line = 1;
            bool lineEmpty = true;
            for (int index = 0; index < contentEnd; index++)
            {
                progress.Advance(1);
                char character = headText[index];
                if (character is '\r' or '\n')
                {
                    if (character == '\r')
                    {
                        if (index + 1 >= contentEnd || headText[index + 1] != '\n')
                        {
                            return new RawFailure(RawCodes.Invalid, line);
                        }

                        index++;
                        progress.Advance(1);
                    }

                    if (lineEmpty)
                    {
                        return new RawFailure(RawCodes.Invalid, line);
                    }

                    bytes[written++] = (byte)'\r';
                    bytes[written++] = (byte)'\n';
                    line++;
                    lineEmpty = true;
                    continue;
                }

                if (character > 0x7F)
                {
                    return new RawFailure(RawCodes.Invalid, line);
                }

                bytes[written++] = (byte)character;
                lineEmpty = false;
            }

            bytes[written++] = (byte)'\r';
            bytes[written++] = (byte)'\n';
            bytes[written++] = (byte)'\r';
            bytes[written++] = (byte)'\n';
            head = bytes;
            convertedLineFeeds = counts.LineFeedsToConvert;
            return null;
        }

        // The end of the text before its trailing line breaks, CRLF or LF. A lone CR is no line break, so it stays and is
        // refused. The breaks are read from the end, as many as there are.
        private static int ContentEnd(string text, ref ScanProgress progress)
        {
            int end = text.Length;
            while (end > 0 && text[end - 1] == '\n')
            {
                end--;
                progress.Advance(1);
                if (end > 0 && text[end - 1] == '\r')
                {
                    end--;
                    progress.Advance(1);
                }
            }

            return end;
        }
    }

    // A scan's progress over a text: the characters read so far, its token checked, and checkpoint (tests) called with that
    // count, each time another RawRequest.CheckChars characters have been read. Raw mode's head scans share it with the
    // scans of Parsed mode's header text (RequestReplayDraft.HeaderLineReader, RawConversion.FromDraft).
    internal struct ScanProgress
    {
        private readonly CancellationToken _token;
        private readonly Action<long> _checkpoint;
        private long _read;
        private long _nextCheck;

        internal ScanProgress(Action<long> checkpoint, CancellationToken token)
        {
            _token = token;
            _checkpoint = checkpoint;
            _read = 0;
            _nextCheck = RawRequest.CheckChars;
        }

        internal void Advance(int characters)
        {
            _read += characters;
            if (_read >= _nextCheck)
            {
                _nextCheck = _read + RawRequest.CheckChars;
                _token.ThrowIfCancellationRequested();
                _checkpoint?.Invoke(_read);
            }
        }
    }
}
