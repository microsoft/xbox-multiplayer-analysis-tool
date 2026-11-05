// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using XMAT.WebServiceCapture;

namespace XMAT.Operations.Inspection
{
    // The views that a preview is for. Text is the body as text, its JSON formatted when the content type says JSON, or a
    // byte dump when it is not text; Json is a tree of the JSON documents of the body; Hex is a byte dump of the stored
    // body, whatever it is.
    [Flags]
    internal enum BodyPreviewParts
    {
        None = 0,
        Text = 1,
        Json = 2,
        Hex = 4,
    }

    // Whether a stored body may still carry its chunked transfer framing. A captured or imported message is Advisory: its
    // headers may say chunked over a body that is already de-chunked, so only recognizable framing is decoded, and framing
    // that does not parse is ignored. A body that the HTTP stack de-framed, such as a replay's response, is Decoded and is
    // never de-chunked again.
    internal enum BodyFraming
    {
        Advisory,
        Decoded,
    }

    // What a preview could show of a body in its Text and Json parts; the Hex part has its own notice (HexLimit).
    internal enum BodyPreviewState
    {
        // A genuinely empty body.
        Empty,

        // XMAT did not keep the body (a body-not-kept marker): nothing is shown, and it is never shown as empty.
        NotRetained,

        // All of the body is shown.
        Complete,

        // Part of the body is shown: the stored body is itself a prefix, or a preview limit was reached.
        Truncated,

        // A content coding, transfer coding or charset that the preview does not decode: a byte dump is shown.
        Unsupported,

        // The coding or the text is invalid: a byte dump is shown.
        Malformed,
    }

    // What BodyPreview.Text holds.
    internal enum BodyPreviewTextKind
    {
        // Nothing: the body is empty or was not kept, or Text was not asked for.
        None,

        // The body as text.
        Text,

        // The JSON documents of the body, formatted.
        Json,

        // A byte dump: the body is not text, or could not be decoded.
        Bytes,
    }

    // Why a preview shows what it shows. Notices are separate from the payload: the GUI words them, and they never hold
    // body content.
    internal static class BodyPreviewNotice
    {
        // The stored body is itself only a prefix of the message, such as a WebSocket payload cut at 1 MB.
        internal const string StoredPrefix = "body.preview.stored-prefix";

        // Only the first MaxInputBytes of the stored body were read.
        internal const string InputLimit = "body.preview.input-limit";

        // Decoding stopped at MaxDecodedBytes.
        internal const string DecodedLimit = "body.preview.decoded-limit";

        // The text, JSON formatting or byte dump of the Text part stopped at MaxTextChars, MaxTextLines or
        // MaxByteDumpBytes.
        internal const string TextLimit = "body.preview.text-limit";

        // The Hex part shows only the start of the stored body: at most MaxTextLines lines of 16 bytes. It is a view of its
        // own, so it never changes State.
        internal const string HexLimit = "body.preview.hex-limit";

        // The preview stopped at MaxWork.
        internal const string WorkLimit = "body.preview.work-limit";

        // A Content-Type, Content-Encoding or Transfer-Encoding value longer than MaxHeaderChars was not used.
        internal const string HeaderLimit = "body.preview.header-limit";

        // "deflate" was decoded as raw deflate after the zlib format failed.
        internal const string RawDeflate = "body.preview.raw-deflate";

        // The compressed data ends before its end (a gzip trailer, or the end of a deflate, zlib or Brotli stream): the start
        // that decoded is shown, and the body counts as only partly read.
        internal const string Incomplete = "body.preview.incomplete";

        // Transfer-Encoding said chunked and the body looked framed, but the framing did not parse: the body is shown as
        // stored.
        internal const string FramingIgnored = "body.preview.framing-ignored";

        // A content or transfer coding that the preview does not decode, or more than one content coding.
        internal const string UnsupportedCoding = "body.preview.unsupported-coding";

        // A declared charset that the preview does not decode.
        internal const string UnsupportedCharset = "body.preview.unsupported-charset";

        // The compressed data is invalid.
        internal const string MalformedCoding = "body.preview.malformed-coding";

        // The bytes are not valid in the charset that the message declares, or not valid UTF-8 in a message whose media type
        // says it is text.
        internal const string MalformedText = "body.preview.malformed-text";

        // The body is not text, or not valid UTF-8 in a message that does not say it is text: it is shown as bytes.
        internal const string Binary = "body.preview.binary";

        // The JSON tree, or the JSON formatting, stopped at MaxJsonNodes, MaxJsonDepth or MaxJsonChars.
        internal const string JsonLimit = "body.preview.json-limit";

        // Only part of the body was read, so it is neither formatted nor shown as a JSON tree.
        internal const string JsonIncomplete = "body.preview.json-incomplete";

        // The body is meant as JSON (a JSON media type, or it starts with an object or array) but is not valid JSON, or valid
        // documents are followed by something else: the tree has the complete documents before it, and the text is the body
        // as it is.
        internal const string JsonMalformed = "body.preview.json-malformed";

        // The operation's diagnostic code when the preview was cancelled; never a notice.
        internal const string Cancelled = "body.preview.cancelled";
    }

    // The ceilings of one preview. A request may lower them, as tests do; nothing raises them above the defaults.
    internal sealed record BodyPreviewLimits(
        int MaxInputBytes = 16 * 1024 * 1024,
        int MaxDecodedBytes = 4 * 1024 * 1024,
        int MaxTextChars = 256 * 1024,
        int MaxTextLines = 4096,
        int MaxJsonDepth = 64,
        int MaxJsonNodes = 2048,
        int MaxJsonChars = 256 * 1024,
        int MaxByteDumpBytes = 4 * 1024,
        int MaxHeaderChars = 8 * 1024,
        long MaxWork = 64L * 1024 * 1024)
    {
        internal static BodyPreviewLimits Default { get; } = new();

        // Every limit between 1 and its default.
        internal BodyPreviewLimits Clamped() => new(
            Math.Clamp(MaxInputBytes, 1, Default.MaxInputBytes),
            Math.Clamp(MaxDecodedBytes, 1, Default.MaxDecodedBytes),
            Math.Clamp(MaxTextChars, 1, Default.MaxTextChars),
            Math.Clamp(MaxTextLines, 1, Default.MaxTextLines),
            Math.Clamp(MaxJsonDepth, 1, Default.MaxJsonDepth),
            Math.Clamp(MaxJsonNodes, 1, Default.MaxJsonNodes),
            Math.Clamp(MaxJsonChars, 1, Default.MaxJsonChars),
            Math.Clamp(MaxByteDumpBytes, 1, Default.MaxByteDumpBytes),
            Math.Clamp(MaxHeaderChars, 1, Default.MaxHeaderChars),
            Math.Clamp(MaxWork, 1, Default.MaxWork));
    }

    // One body to preview. Body is borrowed, never changed or kept; the header values are the message's own, unparsed.
    // NotRetained says that XMAT did not keep the body; StoredPrefix that the stored body is only the start of the message.
    internal sealed record BodyPreviewRequest(
        ReadOnlyMemory<byte> Body,
        string ContentType = null,
        string ContentEncoding = null,
        string TransferEncoding = null,
        BodyFraming Framing = BodyFraming.Advisory,
        bool NotRetained = false,
        bool StoredPrefix = false,
        BodyPreviewParts Parts = BodyPreviewParts.Text | BodyPreviewParts.Json,
        BodyPreviewLimits Limits = null)
    {
        // A message as the capture and composer views hold it: Content-Type, Content-Encoding and Transfer-Encoding by name in
        // any case, and NotRetained when one of XMAT's body-not-kept markers is among the headers.
        internal static BodyPreviewRequest FromHeaders(
            IEnumerable<KeyValuePair<string, string>> headers,
            ReadOnlyMemory<byte> body,
            BodyFraming framing = BodyFraming.Advisory,
            BodyPreviewParts parts = BodyPreviewParts.Text | BodyPreviewParts.Json)
        {
            string contentType = null;
            string contentEncoding = null;
            string transferEncoding = null;
            bool notRetained = false;
            foreach (KeyValuePair<string, string> header in headers ?? Enumerable.Empty<KeyValuePair<string, string>>())
            {
                if (header.Key == null)
                {
                    continue;
                }

                if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    contentType ??= header.Value;
                }
                else if (header.Key.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase))
                {
                    contentEncoding ??= header.Value;
                }
                else if (header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                {
                    transferEncoding ??= header.Value;
                }
                else if (header.Key.Equals(RequestReplayDraft.RequestBodyNotKeptHeader, StringComparison.OrdinalIgnoreCase) ||
                    header.Key.Equals(RequestReplayDraft.ResponseBodyNotKeptHeader, StringComparison.OrdinalIgnoreCase))
                {
                    notRetained = true;
                }
            }

            return new BodyPreviewRequest(
                body, contentType, contentEncoding, transferEncoding, framing, notRetained, Parts: parts);
        }
    }

    // One node of a JSON tree, as the JSON tab shows it: "[root]" for a document, a member name or an array index for an
    // object or array, and "name=value" for any other value, its text at most MaxJsonValueChars characters followed by
    // "…" when longer.
    internal sealed record BodyPreviewNode(string Label, IReadOnlyList<BodyPreviewNode> Children)
    {
        internal const int MaxJsonValueChars = 1024;
    }

    // What a preview shows of one body. Text is what the Text part shows (TextKind says what it is); JsonDocuments the roots
    // of the JSON tree, one per document, empty when the body is not JSON or was not completely read; Hex the byte dump of
    // the Hex part. Notices (BodyPreviewNotice) say why. StoredBytes is the stored body's length, InspectedBytes how much of
    // it was read and DecodedBytes how many decoded bytes the preview worked from. Charset is the charset that Text was
    // decoded with ("utf-8", "utf-16le", "utf-16be", "us-ascii" or "iso-8859-1"), or null.
    internal sealed record BodyPreview(
        BodyPreviewState State,
        BodyPreviewTextKind TextKind,
        string Text,
        IReadOnlyList<BodyPreviewNode> JsonDocuments,
        string Hex,
        IReadOnlyList<string> Notices,
        long StoredBytes,
        long InspectedBytes,
        long DecodedBytes,
        string Charset)
    {
        internal bool HasNotice(string code) => Notices.Contains(code, StringComparer.Ordinal);
    }
}
