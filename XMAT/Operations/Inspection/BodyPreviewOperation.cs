// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.Operations.Inspection
{
    // Previews a message body for the GUI's views without ever holding more of it than BodyPreviewLimits allow: it decodes the
    // transfer framing and the content coding, decodes the text and formats JSON or builds its tree, each bounded before
    // anything is allocated or appended. It never changes or keeps the body, never logs it and does no I/O. At most
    // MaxConcurrentPreviews previews run at a time, always on the thread pool.
    internal sealed class BodyPreviewOperation : IOperation<BodyPreviewRequest, BodyPreview>
    {
        internal const int MaxConcurrentPreviews = 2;

        private const int InitialOutputBytes = 64 * 1024;

        // Decoders are asked for at most this much at a time, so work is checked before each step.
        private const int ReadChunkBytes = 64 * 1024;

        private static readonly SemaphoreSlim Workers = new(MaxConcurrentPreviews, MaxConcurrentPreviews);

        // The preview, or Cancelled; never part of a preview.
        public async Task<OperationResult<BodyPreview>> ExecuteAsync(
            BodyPreviewRequest request, OperationContext context, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            try
            {
                await Workers.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    // Task.Run: the work leaves the caller's thread even when the wait completed synchronously on it.
                    BodyPreview preview = await Task.Run(() => Build(request, cancellationToken), cancellationToken)
                        .ConfigureAwait(false);
                    return OperationResult<BodyPreview>.Success(preview);
                }
                finally
                {
                    Workers.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return OperationResult<BodyPreview>.Failure(
                    OperationOutcome.Cancelled,
                    new OperationDiagnostic(
                        BodyPreviewNotice.Cancelled, OperationDiagnosticSeverity.Info, "The preview was cancelled."));
            }
        }

        // The preview, on the calling thread; throws OperationCanceledException when cancelled.
        internal static BodyPreview Build(BodyPreviewRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            return new Builder(request, cancellationToken).Build();
        }

        // The decoded body: the bytes the preview works from, whether they are all of the body, or invalid compressed data.
        private readonly record struct DecodedBody(ReadOnlyMemory<byte> Bytes, bool Complete, bool Malformed);

        private enum TextEnd
        {
            Complete,
            Cut,
            Binary,
            Malformed,
            WorkLimit,
        }

        private sealed class Builder
        {
            private const int DecodeChunkBytes = 64 * 1024;
            private const int DumpBytesPerLine = 16;

            // "XX" per byte, a space between bytes and CRLF between lines.
            private const int DumpCharsPerLine = (DumpBytesPerLine * 3) - 1 + 2;
            private const int MaxSizeLineBytes = 8192;

            private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
            private static readonly Encoding StrictUtf16Le = new UnicodeEncoding(false, false, true);
            private static readonly Encoding StrictUtf16Be = new UnicodeEncoding(true, false, true);
            private static readonly Encoding StrictAscii =
                Encoding.GetEncoding("us-ascii", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

            // Controls other than tab, CR and LF: text that holds one is shown as bytes.
            private static readonly SearchValues<char> NotTextCharacters = SearchValues.Create(NotTextList());

            private static readonly SearchValues<byte> HexDigits = SearchValues.Create("0123456789abcdefABCDEF"u8);

            private readonly BodyPreviewRequest _request;
            private readonly BodyPreviewLimits _limits;
            private readonly BodyPreviewWork _work;
            private readonly List<string> _notices = new();

            internal Builder(BodyPreviewRequest request, CancellationToken cancellationToken)
            {
                _request = request;
                _limits = (request.Limits ?? BodyPreviewLimits.Default).Clamped();
                _work = new BodyPreviewWork(_limits.MaxWork, cancellationToken);
            }

            internal BodyPreview Build()
            {
                ReadOnlyMemory<byte> stored = _request.Body;
                string hex = Wants(BodyPreviewParts.Hex) ? HexPart(stored.Span) : null;
                if (_request.NotRetained)
                {
                    return Result(BodyPreviewState.NotRetained, BodyPreviewTextKind.None, string.Empty, hex);
                }

                if (_request.StoredPrefix)
                {
                    Notice(BodyPreviewNotice.StoredPrefix);
                }

                if (stored.IsEmpty)
                {
                    BodyPreviewState empty = _request.StoredPrefix ? BodyPreviewState.Truncated : BodyPreviewState.Empty;
                    return Result(empty, BodyPreviewTextKind.None, string.Empty, hex);
                }

                if (!Wants(BodyPreviewParts.Text) && !Wants(BodyPreviewParts.Json))
                {
                    BodyPreviewState shown = _request.StoredPrefix ? BodyPreviewState.Truncated : BodyPreviewState.Complete;
                    return Result(shown, BodyPreviewTextKind.None, null, hex);
                }

                ReadOnlyMemory<byte> input = stored;
                if (input.Length > _limits.MaxInputBytes)
                {
                    input = input[.._limits.MaxInputBytes];
                    Notice(BodyPreviewNotice.InputLimit);
                }

                bool prefix = _request.StoredPrefix || input.Length < stored.Length;
                if (!TryHeader(_request.ContentType, out string contentType))
                {
                    contentType = null;
                }

                if (!TryHeader(_request.ContentEncoding, out string contentEncoding) ||
                    !TryContentCoding(contentEncoding, out string coding))
                {
                    Notice(BodyPreviewNotice.UnsupportedCoding);
                    return Bytes(BodyPreviewState.Unsupported, input.Span, complete: false, hex, 0, null, stored: true);
                }

                bool framed = false;
                if (_request.Framing == BodyFraming.Advisory)
                {
                    if (!TryHeader(_request.TransferEncoding, out string transferEncoding) ||
                        !TryTransferCoding(transferEncoding, out bool chunked))
                    {
                        Notice(BodyPreviewNotice.UnsupportedCoding);
                        return Bytes(BodyPreviewState.Unsupported, input.Span, complete: false, hex, 0, null, stored: true);
                    }

                    framed = chunked && LooksFramed(input.Span);
                }

                DecodedBody decoded = Decode(input, framed, coding, prefix);
                if (decoded.Malformed)
                {
                    Notice(BodyPreviewNotice.MalformedCoding);
                    return Bytes(BodyPreviewState.Malformed, input.Span, complete: false, hex, 0, null, stored: true);
                }

                return Present(decoded, contentType, hex);
            }

            private bool Wants(BodyPreviewParts part) => (_request.Parts & part) != 0;

            private void Notice(string code)
            {
                if (!_notices.Contains(code))
                {
                    _notices.Add(code);
                }
            }

            // A header value as it is, or false (and HeaderLimit) when it is too long to be used.
            private bool TryHeader(string value, out string usable)
            {
                usable = value;
                if (value != null && value.Length > _limits.MaxHeaderChars)
                {
                    Notice(BodyPreviewNotice.HeaderLimit);
                    return false;
                }

                return true;
            }

            // At most one content coding that the preview decodes: gzip, x-gzip, deflate or br; null for none or identity.
            private static bool TryContentCoding(string value, out string coding)
            {
                coding = null;
                foreach (string part in (value ?? string.Empty).Split(','))
                {
                    string name = part.Trim();
                    if (name.Length == 0 || name.Equals("identity", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string known =
                        name.Equals("gzip", StringComparison.OrdinalIgnoreCase) || name.Equals("x-gzip", StringComparison.OrdinalIgnoreCase)
                            ? "gzip"
                            : name.Equals("deflate", StringComparison.OrdinalIgnoreCase)
                                ? "deflate"
                                : name.Equals("br", StringComparison.OrdinalIgnoreCase) ? "br" : null;
                    if (known == null || coding != null)
                    {
                        coding = null;
                        return false;
                    }

                    coding = known;
                }

                return true;
            }

            // Only chunked and identity are transfer codings that a stored body can still carry.
            private static bool TryTransferCoding(string value, out bool chunked)
            {
                chunked = false;
                foreach (string part in (value ?? string.Empty).Split(','))
                {
                    string name = part.Trim();
                    if (name.Equals("chunked", StringComparison.OrdinalIgnoreCase))
                    {
                        chunked = true;
                    }
                    else if (name.Length != 0 && !name.Equals("identity", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                return true;
            }

            // Chunk framing is recognized as ChunkedBodyDecoder.DecodeIfFramed recognizes it, the first line read from at
            // most MaxSizeLineBytes bytes: a first line that is a chunk size, or the body ends with the last chunk.
            private static bool LooksFramed(ReadOnlySpan<byte> body)
            {
                int newline = body[..Math.Min(body.Length, MaxSizeLineBytes + 1)].IndexOf((byte)'\n');
                if (newline >= 0)
                {
                    ReadOnlySpan<byte> line = body[..newline].TrimEnd((byte)'\r');
                    int extension = line.IndexOf((byte)';');
                    ReadOnlySpan<byte> size = (extension < 0 ? line : line[..extension]).Trim(" \t"u8);
                    if (!size.IsEmpty && size.IndexOfAnyExcept(HexDigits) < 0)
                    {
                        return true;
                    }
                }

                return body.EndsWith("\r\n0\r\n\r\n"u8);
            }

            // Framing that does not parse is advisory: the body is then decoded as it is stored.
            private DecodedBody Decode(ReadOnlyMemory<byte> input, bool framed, string coding, bool prefix)
            {
                if (framed)
                {
                    try
                    {
                        return Inflate(input, framed: true, coding, prefix);
                    }
                    catch (BodyPreviewFramingException)
                    {
                        Notice(BodyPreviewNotice.FramingIgnored);
                    }
                }

                return Inflate(input, framed: false, coding, prefix);
            }

            private DecodedBody Inflate(ReadOnlyMemory<byte> input, bool framed, string coding, bool prefix)
            {
                if (!framed && coding == null)
                {
                    // The stored bytes themselves, never copied.
                    int byWork = (int)Math.Clamp(_work.Remaining, 0, int.MaxValue);
                    int length = Math.Min(input.Length, Math.Min(_limits.MaxDecodedBytes, byWork));
                    _work.Spend(length);
                    _work.Reach(length);
                    if (length < input.Length)
                    {
                        Notice(length == byWork && byWork < _limits.MaxDecodedBytes
                            ? BodyPreviewNotice.WorkLimit
                            : BodyPreviewNotice.DecodedLimit);
                    }

                    return new DecodedBody(input[..length], !prefix && length == input.Length, false);
                }

                bool rawDeflate = false;
                while (true)
                {
                    var output = new Output(_limits.MaxDecodedBytes);
                    BodyPreviewChunkedStream chunks = framed ? new BodyPreviewChunkedStream(input, prefix, _work) : null;
                    var source = new BodyPreviewSourceStream(chunks ?? (Stream)new BodyPreviewInputStream(input, _work));
                    try
                    {
                        using Stream decoder = Open(source, rawDeflate ? null : coding, rawDeflate);
                        bool more = output.Fill(decoder, _work);
                        bool complete = !prefix && !more;
                        if (more)
                        {
                            Notice(BodyPreviewNotice.DecodedLimit);
                        }
                        else if (coding != null && Incomplete(rawDeflate ? null : coding, source, output.Length))
                        {
                            // The compressed data ends before its end: the start that decoded is shown, and a stored prefix
                            // already says why.
                            if (!prefix)
                            {
                                Notice(BodyPreviewNotice.Incomplete);
                            }

                            complete = false;
                        }
                        else if (chunks != null)
                        {
                            // The framing after the compressed data must parse to its last chunk too.
                            Drain(chunks);
                        }

                        if (rawDeflate)
                        {
                            Notice(BodyPreviewNotice.RawDeflate);
                        }

                        return new DecodedBody(output.Memory, complete, false);
                    }
                    catch (BodyPreviewWorkLimitException)
                    {
                        Notice(BodyPreviewNotice.WorkLimit);
                        return new DecodedBody(output.Memory, false, false);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
                    {
                        // Invalid compressed data (BrotliStream reports it as InvalidOperationException). Once, and only
                        // when nothing decoded: "deflate" sent as raw deflate instead of zlib.
                        if (coding == "deflate" && !rawDeflate && output.Length == 0)
                        {
                            rawDeflate = true;
                            continue;
                        }

                        // The decoders end quietly where their data ends (Incomplete tells that apart), so an error is
                        // invalid data.
                        return new DecodedBody(default, false, true);
                    }
                }
            }

            // Whether the compressed data ended before its end. A deflate, zlib or Brotli decoder asks its source for more
            // only then. A gzip decoder also asks for a next member, so a gzip body is complete when its trailer ends it:
            // ISIZE, the length of the data mod 2^32, in its last four bytes (a body of several members reads as incomplete).
            private static bool Incomplete(string coding, BodyPreviewSourceStream source, int decodedLength)
            {
                if (coding != "gzip")
                {
                    return source.EndRequested;
                }

                ReadOnlySpan<byte> tail = source.Tail;
                return tail.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(tail[4..]) != (uint)decodedLength;
            }

            // Reads the framing to its end, which validates it: a missing last chunk or trailing bytes throw
            // BodyPreviewFramingException.
            private static void Drain(BodyPreviewChunkedStream chunks)
            {
                Span<byte> scratch = stackalloc byte[4096];
                while (chunks.Read(scratch) > 0)
                {
                    // Data after the end of the compressed data is not shown.
                }
            }

            private static Stream Open(Stream source, string coding, bool rawDeflate)
            {
                if (rawDeflate)
                {
                    return new DeflateStream(source, CompressionMode.Decompress);
                }

                return coding switch
                {
                    "gzip" => new GZipStream(source, CompressionMode.Decompress),
                    "deflate" => new ZLibStream(source, CompressionMode.Decompress),
                    "br" => new BrotliStream(source, CompressionMode.Decompress),
                    _ => source,
                };
            }

            private BodyPreview Present(DecodedBody decoded, string contentType, string hex)
            {
                ReadOnlySpan<byte> bytes = decoded.Bytes.Span;
                long decodedBytes = bytes.Length;
                if (!TryCharset(contentType, bytes, out Encoding encoding, out string charset, out int preamble, out bool declared))
                {
                    Notice(BodyPreviewNotice.UnsupportedCharset);
                    return Bytes(BodyPreviewState.Unsupported, bytes, decoded.Complete, hex, decodedBytes, null);
                }

                bool declaredText = declared || IsTextType(contentType);

                ReadOnlySpan<byte> body = bytes[preamble..];
                var text = new BodyPreviewText(_limits.MaxTextChars, _limits.MaxTextLines);
                switch (DecodeText(body, decoded.Complete, encoding, text))
                {
                    // Bytes that are not valid text: malformed when the message says it is text, otherwise binary.
                    case TextEnd.Malformed when declaredText:
                        Notice(BodyPreviewNotice.MalformedText);
                        return Bytes(BodyPreviewState.Malformed, bytes, decoded.Complete, hex, decodedBytes, charset);

                    case TextEnd.Malformed:
                    case TextEnd.Binary:
                        Notice(BodyPreviewNotice.Binary);
                        return Bytes(BodyPreviewState.Complete, bytes, decoded.Complete, hex, decodedBytes, null);

                    case TextEnd.WorkLimit:
                        Notice(BodyPreviewNotice.WorkLimit);
                        return Text(BodyPreviewState.Truncated, BodyPreviewTextKind.Text, text, null, hex, decodedBytes, charset);
                }

                bool jsonType = contentType != null && contentType.Contains("json", StringComparison.OrdinalIgnoreCase);
                bool jsonCharset = ReferenceEquals(encoding, StrictUtf8) || ReferenceEquals(encoding, StrictAscii);
                if (!decoded.Complete || !jsonCharset)
                {
                    // A prefix is never parsed as if it were the whole document.
                    if (!decoded.Complete && (jsonType || BodyPreviewJson.LooksLikeJson(body)))
                    {
                        Notice(BodyPreviewNotice.JsonIncomplete);
                    }

                    BodyPreviewState shown = decoded.Complete ? BodyPreviewState.Complete : BodyPreviewState.Truncated;
                    return Text(shown, BodyPreviewTextKind.Text, text, null, hex, decodedBytes, charset);
                }

                IReadOnlyList<BodyPreviewNode> documents = null;
                BodyPreviewText shownText = text;
                BodyPreviewTextKind kind = BodyPreviewTextKind.Text;
                bool limited = false;

                // JSON by its media type or because it starts with an object or array; plain text that only starts with a
                // number or a word such as true is not JSON.
                bool meantAsJson = jsonType || BodyPreviewJson.LooksLikeJson(body);
                try
                {
                    if (Wants(BodyPreviewParts.Json))
                    {
                        // Each pass reads the body once at most: it starts only when that much work is left.
                        _work.Require(body.Length);
                        documents = BodyPreviewJson.BuildTree(body, _limits, _work, out BodyPreviewJson.TreeEnd treeEnd);
                        if (treeEnd == BodyPreviewJson.TreeEnd.Limit)
                        {
                            Notice(BodyPreviewNotice.JsonLimit);
                            limited = true;
                        }
                        else if (treeEnd is BodyPreviewJson.TreeEnd.Malformed or BodyPreviewJson.TreeEnd.NotJson)
                        {
                            if (meantAsJson)
                            {
                                Notice(BodyPreviewNotice.JsonMalformed);
                            }
                            else
                            {
                                documents = null;
                            }
                        }
                    }

                    if (Wants(BodyPreviewParts.Text) && jsonType)
                    {
                        _work.Require(body.Length);
                        var formatted = new BodyPreviewText(_limits.MaxTextChars, _limits.MaxTextLines);
                        switch (BodyPreviewJson.Format(body, _limits, _work, formatted))
                        {
                            case BodyPreviewJson.FormatEnd.Complete:
                            case BodyPreviewJson.FormatEnd.TextLimit:
                                shownText = formatted;
                                kind = BodyPreviewTextKind.Json;
                                break;

                            case BodyPreviewJson.FormatEnd.DepthLimit:
                                Notice(BodyPreviewNotice.JsonLimit);
                                limited = true;
                                break;

                            case BodyPreviewJson.FormatEnd.Malformed:
                                // Shown as it is, never rewritten into valid JSON.
                                Notice(BodyPreviewNotice.JsonMalformed);
                                break;
                        }
                    }
                }
                catch (BodyPreviewWorkLimitException)
                {
                    Notice(BodyPreviewNotice.WorkLimit);
                    return Text(BodyPreviewState.Truncated, BodyPreviewTextKind.Text, text, null, hex, decodedBytes, charset);
                }

                BodyPreviewState state = limited ? BodyPreviewState.Truncated : BodyPreviewState.Complete;
                return Text(state, kind, shownText, documents, hex, decodedBytes, charset);
            }

            // The body's charset: the one Content-Type declares (declared), else the one its byte order mark says, else UTF-8;
            // false for a declared charset that the preview does not decode. preamble is the length of a byte order mark to
            // skip.
            private static bool TryCharset(
                string contentType,
                ReadOnlySpan<byte> bytes,
                out Encoding encoding,
                out string charset,
                out int preamble,
                out bool declared)
            {
                string name = null;
                if (contentType != null && MediaTypeHeaderValue.TryParse(contentType, out MediaTypeHeaderValue media))
                {
                    name = media.CharSet?.Trim().Trim('"');
                }

                declared = !string.IsNullOrEmpty(name);
                bool utf8Bom = bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]);
                bool littleEndianBom = bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]);
                bool bigEndianBom = bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]);
                switch (declared ? name.ToUpperInvariant() : null)
                {
                    case null:
                        (encoding, charset, preamble) = utf8Bom
                            ? (StrictUtf8, "utf-8", 3)
                            : littleEndianBom
                                ? (StrictUtf16Le, "utf-16le", 2)
                                : bigEndianBom ? (StrictUtf16Be, "utf-16be", 2) : (StrictUtf8, "utf-8", 0);
                        return true;

                    case "UTF-8":
                    case "UTF8":
                        (encoding, charset, preamble) = (StrictUtf8, "utf-8", utf8Bom ? 3 : 0);
                        return true;

                    case "UTF-16":
                        (encoding, charset, preamble) = bigEndianBom
                            ? (StrictUtf16Be, "utf-16be", 2)
                            : (StrictUtf16Le, "utf-16le", littleEndianBom ? 2 : 0);
                        return true;

                    case "UTF-16LE":
                    case "UNICODE":
                        (encoding, charset, preamble) = (StrictUtf16Le, "utf-16le", littleEndianBom ? 2 : 0);
                        return true;

                    case "UTF-16BE":
                        (encoding, charset, preamble) = (StrictUtf16Be, "utf-16be", bigEndianBom ? 2 : 0);
                        return true;

                    case "US-ASCII":
                    case "ASCII":
                        (encoding, charset, preamble) = (StrictAscii, "us-ascii", 0);
                        return true;

                    case "ISO-8859-1":
                    case "ISO_8859-1":
                    case "LATIN1":
                        (encoding, charset, preamble) = (Encoding.Latin1, "iso-8859-1", 0);
                        return true;

                    default:
                        (encoding, charset, preamble) = (null, null, 0);
                        return false;
                }
            }

            // Decodes the text into text until it is full; a body that is only a prefix may end inside a character, which
            // is then left out rather than taken for invalid text.
            private TextEnd DecodeText(ReadOnlySpan<byte> body, bool complete, Encoding encoding, BodyPreviewText text)
            {
                Decoder decoder = encoding.GetDecoder();
                char[] chars = ArrayPool<char>.Shared.Rent(encoding.GetMaxCharCount(DecodeChunkBytes));
                try
                {
                    int position = 0;
                    while (position < body.Length)
                    {
                        int take = Math.Min(DecodeChunkBytes, body.Length - position);
                        bool last = position + take == body.Length;
                        _work.Spend(take);
                        decoder.Convert(body.Slice(position, take), chars, last && complete, out int used, out int produced, out _);
                        position += used;
                        _work.Spend(produced);
                        ReadOnlySpan<char> decoded = chars.AsSpan(0, produced);
                        int shown = Math.Min(decoded.Length, text.Remaining + 1);
                        if (decoded[..shown].IndexOfAny(NotTextCharacters) >= 0)
                        {
                            return TextEnd.Binary;
                        }

                        if (!text.Append(decoded))
                        {
                            return TextEnd.Cut;
                        }

                        if (used == 0)
                        {
                            break;
                        }
                    }

                    return TextEnd.Complete;
                }
                catch (DecoderFallbackException)
                {
                    return TextEnd.Malformed;
                }
                catch (BodyPreviewWorkLimitException)
                {
                    return TextEnd.WorkLimit;
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(chars);
                }
            }

            private BodyPreview Text(
                BodyPreviewState state,
                BodyPreviewTextKind kind,
                BodyPreviewText text,
                IReadOnlyList<BodyPreviewNode> documents,
                string hex,
                long decodedBytes,
                string charset)
            {
                if (text.Cut)
                {
                    Notice(BodyPreviewNotice.TextLimit);
                    state = state == BodyPreviewState.Complete ? BodyPreviewState.Truncated : state;
                }

                bool wantsText = Wants(BodyPreviewParts.Text);
                return Result(
                    state,
                    wantsText ? kind : BodyPreviewTextKind.None,
                    wantsText ? text.ToString() : null,
                    hex,
                    documents,
                    decodedBytes,
                    charset);
            }

            // A byte dump of at most MaxByteDumpBytes bytes in place of the text, of the stored body (stored) or of the decoded
            // bytes. A complete state becomes Truncated when the dump or the bytes are only part of the body.
            private BodyPreview Bytes(
                BodyPreviewState state,
                ReadOnlySpan<byte> bytes,
                bool complete,
                string hex,
                long decodedBytes,
                string charset,
                bool stored = false)
            {
                int max = (int)Math.Min(_limits.MaxByteDumpBytes, (long)DumpLines() * DumpBytesPerLine);
                int wanted = Math.Min(bytes.Length, max);
                int shown = Afford(wanted);
                if (shown < wanted)
                {
                    Notice(BodyPreviewNotice.WorkLimit);
                }

                if (bytes.Length > max)
                {
                    Notice(BodyPreviewNotice.TextLimit);
                }

                if (stored)
                {
                    _work.Reach(shown);
                }

                if (state == BodyPreviewState.Complete && (shown < bytes.Length || !complete))
                {
                    state = BodyPreviewState.Truncated;
                }

                bool wantsText = Wants(BodyPreviewParts.Text);
                return Result(
                    state,
                    wantsText ? BodyPreviewTextKind.Bytes : BodyPreviewTextKind.None,
                    wantsText ? Dump(bytes[..shown]) : null,
                    hex,
                    null,
                    decodedBytes,
                    charset);
            }

            // The Hex part: the start of the stored body, at most MaxTextLines lines that fit in MaxTextChars and the work
            // left.
            private string HexPart(ReadOnlySpan<byte> stored)
            {
                int shown = Afford((int)Math.Min(stored.Length, (long)DumpLines() * DumpBytesPerLine));
                if (shown < stored.Length)
                {
                    Notice(BodyPreviewNotice.HexLimit);
                }

                _work.Reach(shown);
                return Dump(stored[..shown]);
            }

            // At most count units of the work left, spent before the caller does them.
            private int Afford(int count)
            {
                int allowed = (int)Math.Clamp(_work.Remaining, 0, count);
                _work.Spend(allowed);
                return allowed;
            }

            private int DumpLines() => Math.Min(_limits.MaxTextLines, (_limits.MaxTextChars + 2) / DumpCharsPerLine);

            // A media type that says the body is text: text/*, or JSON, XML or JavaScript of any kind.
            private static bool IsTextType(string contentType) =>
                contentType != null &&
                (contentType.TrimStart().StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
                    contentType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
                    contentType.Contains("xml", StringComparison.OrdinalIgnoreCase) ||
                    contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase));

            // "XX XX ...", 16 bytes a line, lines joined by CRLF.
            private static string Dump(ReadOnlySpan<byte> bytes)
            {
                var text = new StringBuilder(bytes.Length * 3);
                for (int index = 0; index < bytes.Length; index++)
                {
                    if (index > 0)
                    {
                        text.Append(index % DumpBytesPerLine == 0 ? "\r\n" : " ");
                    }

                    text.Append("0123456789ABCDEF"[bytes[index] >> 4]).Append("0123456789ABCDEF"[bytes[index] & 0xF]);
                }

                return text.ToString();
            }

            private BodyPreview Result(
                BodyPreviewState state,
                BodyPreviewTextKind kind,
                string text,
                string hex,
                IReadOnlyList<BodyPreviewNode> documents = null,
                long decodedBytes = 0,
                string charset = null) =>
                new(
                    state,
                    kind,
                    text,
                    documents ?? Array.Empty<BodyPreviewNode>(),
                    hex,
                    _notices.ToArray(),
                    _request.Body.Length,
                    _work.Inspected,
                    decodedBytes,
                    charset);

            private static char[] NotTextList()
            {
                var list = new List<char>();
                for (char character = '\0'; character < '\u00A0'; character++)
                {
                    if (char.IsControl(character) && character is not ('\t' or '\r' or '\n'))
                    {
                        list.Add(character);
                    }
                }

                return list.ToArray();
            }
        }

        // The decoded bytes, at most max of them, in a buffer that grows only as far as the decoder fills it.
        private sealed class Output
        {
            private readonly int _max;
            private byte[] _buffer = Array.Empty<byte>();

            internal Output(int max)
            {
                _max = max;
            }

            internal int Length { get; private set; }

            internal ReadOnlyMemory<byte> Memory => _buffer.AsMemory(0, Length);

            // Reads the decoder to its end or to max bytes; true when it stopped at max with more to read.
            internal bool Fill(Stream decoder, BodyPreviewWork work)
            {
                Span<byte> probe = stackalloc byte[1];
                while (true)
                {
                    if (Length == _buffer.Length)
                    {
                        if (Length == _max)
                        {
                            return decoder.Read(probe) > 0;
                        }

                        Array.Resize(
                            ref _buffer, (int)Math.Min(_max, Math.Max(InitialOutputBytes, (long)_buffer.Length * 2)));
                    }

                    int wanted = work.Allow(Math.Min(ReadChunkBytes, _buffer.Length - Length));
                    int read = decoder.Read(_buffer, Length, wanted);
                    if (read == 0)
                    {
                        return false;
                    }

                    work.Spend(read);
                    Length += read;
                }
            }
        }
    }
}
