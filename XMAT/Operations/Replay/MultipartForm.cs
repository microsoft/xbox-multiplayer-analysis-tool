// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using XMAT.WebServiceCapture;

namespace XMAT.Operations.Replay
{
    // Why a multipart form was refused. Each code has one fixed message (MultipartFormException), which never holds a name,
    // value, file name, path or type that the user gave.
    internal static class MultipartCodes
    {
        internal const string Empty = "replay.multipart.empty";
        internal const string TooManyParts = "replay.multipart.too-many-parts";
        internal const string InvalidName = "replay.multipart.invalid-name";
        internal const string InvalidFileName = "replay.multipart.invalid-file-name";
        internal const string InvalidMediaType = "replay.multipart.invalid-media-type";
        internal const string InvalidText = "replay.multipart.invalid-text";
        internal const string TooLarge = "replay.multipart.too-large";
        internal const string BoundaryCollision = "replay.multipart.boundary-collision";
        internal const string FileNotFound = "replay.multipart.file-not-found";
        internal const string FileUnreadable = "replay.multipart.file-unreadable";

        // Refusals that depend on the request that a form is applied to, found after its source was read.
        internal const string HeadersOmitted = "replay.multipart.headers-omitted";
        internal const string ContentTypeDropped = "replay.multipart.content-type-dropped";
    }

    internal sealed class MultipartFormException : Exception
    {
        internal MultipartFormException(string code)
            : base(MessageFor(code))
        {
            Code = code;
        }

        internal string Code { get; }

        internal static string MessageFor(string code) =>
            code switch
            {
                MultipartCodes.Empty => "A multipart form needs at least one part.",
                MultipartCodes.TooManyParts => "A multipart form can have at most 128 parts.",
                MultipartCodes.InvalidName => "A form field name must be 1 to 256 printable ASCII characters.",
                MultipartCodes.InvalidFileName =>
                    "A form file name must be a file's own name, without a folder: 1 to 255 characters of well-formed " +
                        "Unicode without control characters.",
                MultipartCodes.InvalidMediaType =>
                    "A form part's media type must be TYPE/SUBTYPE with optional parameters, at most 256 ASCII characters.",
                MultipartCodes.InvalidText => "A form value is not well-formed Unicode text.",
                MultipartCodes.TooLarge => "The multipart form would be larger than 64 MiB.",
                MultipartCodes.BoundaryCollision =>
                    "No multipart boundary could be found that the form's parts do not already contain; nothing was sent.",
                MultipartCodes.FileNotFound => "A form file was not found.",
                MultipartCodes.FileUnreadable => "A form file could not be read.",
                MultipartCodes.HeadersOmitted =>
                    "A multipart form needs the request's headers for its Content-Type, and the request file left them " +
                        "out; supply them first (--clear-headers). Nothing was sent or written.",
                MultipartCodes.ContentTypeDropped =>
                    "The request's Connection header nominates Content-Type, which sending drops, and with it the form's " +
                        "boundary; remove Content-Type from Connection first. Nothing was sent or written.",
                _ => "The multipart form is not valid.",
            };
    }

    internal enum MultipartPartKind
    {
        Text,
        File,
    }

    // The stage that a unit of a form's work belongs to: measuring a text, scanning for the boundary, writing the body.
    internal enum MultipartStage
    {
        Measure,
        Scan,
        Write,
    }

    // Test seam, given per invocation and null in production: called after each bounded unit of work (MultipartForm.WorkChunk)
    // with its stage, the part's index and how many characters or bytes of that part are done.
    internal delegate void MultipartProgress(MultipartStage stage, int partIndex, long done);

    // One part of a form, validated when it is made and never changed after: the CLI builds forms from these, and the GUI
    // keeps them as what it re-edits. A file part borrows its snapshot array: nothing may change that array while a Build
    // runs or a part that holds it is kept. A file part never holds a path.
    internal sealed class MultipartPart
    {
        private const string DefaultFileMediaType = "application/octet-stream";

        private MultipartPart(
            MultipartPartKind kind, string name, string value, string fileName, byte[] snapshot, string mediaType, long length)
        {
            Kind = kind;
            Name = name;
            Value = value;
            FileName = fileName;
            Snapshot = snapshot;
            MediaType = mediaType;
            ContentLength = length;
            HeaderLines = MultipartForm.HeaderLines(kind == MultipartPartKind.File, name, fileName, mediaType);
            FramingLength = MultipartForm.FramingOf(HeaderLines);
        }

        internal MultipartPartKind Kind { get; }

        internal string Name { get; }

        // A text part's value; null for a file part.
        internal string Value { get; }

        // A file part's name as given, not encoded; null for a text part.
        internal string FileName { get; }

        // A file part's snapshot; empty for a text part.
        internal ReadOnlyMemory<byte> Content => Snapshot;

        // The media type as given, or null: a file part without one is sent as application/octet-stream.
        internal string MediaType { get; }

        // The strict UTF-8 byte count of a text part's value, or the length of a file part's snapshot.
        internal long ContentLength { get; }

        // The part's bytes other than its content (MultipartForm.PartFramingLength).
        internal long FramingLength { get; }

        // Content-Disposition, any Content-Type, and the blank line, as written after the part's delimiter line.
        internal string HeaderLines { get; }

        internal byte[] Snapshot { get; }

        internal static string EffectiveMediaType(bool isFile, string mediaType) =>
            mediaType ?? (isFile ? DefaultFileMediaType : null);

        // A text part. Its value is measured with MultipartForm.CountText under the token, after the name and type are
        // checked: OperationCanceledException when cancelled, invalid-text when the value is not well-formed UTF-16.
        internal static MultipartPart Text(
            string name, string value, string mediaType, CancellationToken cancellationToken, MultipartProgress progress = null)
        {
            ArgumentNullException.ThrowIfNull(value);
            CheckName(name);
            CheckMediaType(mediaType);
            long length = MultipartForm.CountText(value, cancellationToken, progress);
            if (length < 0)
                throw new MultipartFormException(MultipartCodes.InvalidText);
            return new MultipartPart(MultipartPartKind.Text, name, value, null, null, mediaType, length);
        }

        // A file part over a snapshot of the file's bytes, which it borrows.
        internal static MultipartPart File(string name, string fileName, byte[] snapshot, string mediaType = null)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            CheckName(name);
            if (!MultipartForm.IsValidFileName(fileName))
                throw new MultipartFormException(MultipartCodes.InvalidFileName);
            CheckMediaType(mediaType);
            return new MultipartPart(MultipartPartKind.File, name, null, fileName, snapshot, mediaType, snapshot.LongLength);
        }

        private static void CheckName(string name)
        {
            if (!MultipartForm.IsValidName(name))
                throw new MultipartFormException(MultipartCodes.InvalidName);
        }

        private static void CheckMediaType(string mediaType)
        {
            if (mediaType != null && !MultipartForm.IsValidMediaType(mediaType))
                throw new MultipartFormException(MultipartCodes.InvalidMediaType);
        }
    }

    // A built form: its bytes, which nothing shares, its boundary and Content-Type, and the parts that it was built from.
    internal sealed class MultipartFormBody
    {
        internal MultipartFormBody(byte[] body, string boundary, IReadOnlyList<MultipartPart> parts)
        {
            Body = body;
            Boundary = boundary;
            ContentType = MultipartForm.ContentTypePrefix + boundary;
            Parts = parts;
        }

        internal byte[] Body { get; }

        internal string Boundary { get; }

        internal string ContentType { get; }

        internal IReadOnlyList<MultipartPart> Parts { get; }
    }

    // Builds multipart/form-data bodies (RFC 7578 over RFC 2046) for xmat's --form options and the composer's Build form.
    // Every size is measured before anything of it is allocated; text is encoded straight into the one body array; the
    // boundary is random and checked against every part's header lines and content; and the work stops, within one unit,
    // when its token is cancelled. Nothing here reads a file, and no text is ever treated as an instruction to include one.
    internal static class MultipartForm
    {
        internal const int MaxParts = 128;
        internal const long MaxBodyBytes = ReplayBodyFile.MaxBytes;
        internal const int MaxNameChars = 256;
        internal const int MaxFileNameChars = 255;
        internal const int MaxMediaTypeChars = 256;
        internal const int RandomByteCount = 16;
        internal const int MaxDraws = 5;

        // The most characters or bytes of a stage's work between two checks of the token.
        internal const int WorkChunk = 1024 * 1024;

        internal const string BoundaryPrefix = "xmat-form-";
        internal const int BoundaryLength = 42;

        // "--" B "--" CRLF, once per form.
        internal const int ClosingLength = 2 + BoundaryLength + 2 + 2;

        internal const string ContentTypePrefix = "multipart/form-data; boundary=";

        // "--" B CRLF before each part's header lines, and the CRLF after its content.
        private const int DelimiterLength = 2 + BoundaryLength + 2;
        private const int AfterContentLength = 2;

        private const string DispositionStart = "Content-Disposition: form-data; name=\"";
        private const string FileNameStart = "; filename=\"";
        private const string ContentTypeStart = "Content-Type: ";

        private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        // A field name: 1 to 256 printable ASCII characters. A quote and a backslash are escaped when written.
        internal static bool IsValidName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > MaxNameChars)
                return false;
            foreach (char c in name)
            {
                if (c < ' ' || c > '~')
                    return false;
            }

            return true;
        }

        // A file's own name: 1 to 255 UTF-16 units of well-formed UTF-16, not "." or "..", without a separator or a control
        // character.
        internal static bool IsValidFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName) || fileName.Length > MaxFileNameChars || fileName is "." or "..")
                return false;
            for (int index = 0; index < fileName.Length; index++)
            {
                char c = fileName[index];
                if (c is '/' or '\\' || c < ' ' || (c >= '\u007f' && c <= '\u009f'))
                    return false;
                if (char.IsHighSurrogate(c))
                {
                    if (index + 1 >= fileName.Length || !char.IsLowSurrogate(fileName[index + 1]))
                        return false;
                    index++;
                }
                else if (char.IsLowSurrogate(c))
                {
                    return false;
                }
            }

            return true;
        }

        // TYPE "/" SUBTYPE *( OWS ";" OWS NAME "=" ( TOKEN / QUOTED-STRING ) ), in ASCII, at most 256 characters, where OWS is
        // spaces and tabs, the names are RFC 9110 tokens, and a quoted-string holds no obs-text.
        internal static bool IsValidMediaType(string mediaType)
        {
            if (string.IsNullOrEmpty(mediaType) || mediaType.Length > MaxMediaTypeChars)
                return false;
            int index = 0;
            if (!Token(mediaType, ref index) || index >= mediaType.Length || mediaType[index] != '/')
                return false;
            index++;
            if (!Token(mediaType, ref index))
                return false;
            while (index < mediaType.Length)
            {
                Whitespace(mediaType, ref index);
                if (index >= mediaType.Length || mediaType[index] != ';')
                    return false;
                index++;
                Whitespace(mediaType, ref index);
                if (!Token(mediaType, ref index) || index >= mediaType.Length || mediaType[index] != '=')
                    return false;
                index++;
                if (index < mediaType.Length && mediaType[index] == '"')
                {
                    if (!QuotedString(mediaType, ref index))
                        return false;
                }
                else if (!Token(mediaType, ref index))
                {
                    return false;
                }
            }

            return true;
        }

        // The strict UTF-8 byte count of a value, or -1 when it is not well-formed UTF-16. It counts at most WorkChunk
        // characters at a time, never splitting a surrogate pair between two units, and after each unit calls progress and
        // checks the token. It allocates nothing of the value's size.
        internal static long CountText(string value, CancellationToken cancellationToken, MultipartProgress progress = null)
        {
            ArgumentNullException.ThrowIfNull(value);
            cancellationToken.ThrowIfCancellationRequested();
            long count = 0;
            int start = 0;
            while (start < value.Length)
            {
                // A unit never ends between the halves of a surrogate pair: a high half at its end waits for the next unit.
                int end = Math.Min(value.Length, start + WorkChunk);
                if (end < value.Length && char.IsHighSurrogate(value[end - 1]))
                    end--;
                for (int index = start; index < end; index++)
                {
                    char c = value[index];
                    if (c < '\u0080')
                    {
                        count += 1;
                    }
                    else if (c < '\u0800')
                    {
                        count += 2;
                    }
                    else if (char.IsHighSurrogate(c))
                    {
                        if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                            return -1;
                        count += 4;
                        index++;
                    }
                    else if (char.IsLowSurrogate(c))
                    {
                        return -1;
                    }
                    else
                    {
                        count += 3;
                    }
                }

                start = end;
                progress?.Invoke(MultipartStage.Measure, 0, start);
                cancellationToken.ThrowIfCancellationRequested();
            }

            return count;
        }

        // A part's bytes other than its content, as Build writes them: its delimiter line, its Content-Disposition line, any
        // Content-Type line, the blank line and the CRLF after its content. A field that is required but missing, or not
        // valid, is charged at its largest spelling, so an unfinished part never costs less than it can.
        internal static long PartFramingLength(bool isFile, string name, string fileName, string mediaType)
        {
            long length = DelimiterLength + DispositionStart.Length + 1 + 2 + 2 + AfterContentLength;
            length += IsValidName(name) ? EscapeName(name).Length : 2L * MaxNameChars;
            if (isFile)
            {
                length += FileNameStart.Length + 1;
                length += IsValidFileName(fileName) ? EncodeFileName(fileName).Length : MaxEncodedFileNameChars;
            }

            string effective = MultipartPart.EffectiveMediaType(isFile, mediaType);
            if (effective != null)
                length += ContentTypeStart.Length + 2 + (IsValidMediaType(effective) ? effective.Length : MaxMediaTypeChars);
            return length;
        }

        // A part's framing with every field at its largest spelling.
        internal static long MaxPartFramingLength => PartFramingLength(true, null, null, "");

        // The form's exact length: each part's framing and content, and the closing delimiter, each counted once.
        internal static long Length(IReadOnlyList<MultipartPart> parts)
        {
            ArgumentNullException.ThrowIfNull(parts);
            long length = ClosingLength;
            foreach (MultipartPart part in parts)
            {
                ArgumentNullException.ThrowIfNull(part, nameof(parts));
                length += part.FramingLength + part.ContentLength;
            }

            return length;
        }

        // The form's bytes, under a boundary that none of its parts' header lines or contents hold. The size is checked
        // before anything of it is allocated; at most MaxDraws boundaries are drawn; the body is allocated once, at its
        // exact length. The token is checked before the work, after each draw, and after each unit of scanning or writing,
        // so a cancelled build does at most the unit that it is in, and returns nothing. randomBytes is a test seam that must
        // give exactly RandomByteCount bytes; production draws from RandomNumberGenerator.
        internal static MultipartFormBody Build(
            IReadOnlyList<MultipartPart> parts,
            CancellationToken cancellationToken,
            Func<byte[]> randomBytes = null,
            MultipartProgress progress = null)
        {
            ArgumentNullException.ThrowIfNull(parts);
            cancellationToken.ThrowIfCancellationRequested();
            if (parts.Count == 0)
                throw new MultipartFormException(MultipartCodes.Empty);
            if (parts.Count > MaxParts)
                throw new MultipartFormException(MultipartCodes.TooManyParts);
            var kept = new MultipartPart[parts.Count];
            for (int index = 0; index < kept.Length; index++)
                kept[index] = parts[index] ?? throw new ArgumentException("A form part is missing.", nameof(parts));
            long length = Length(kept);
            if (length > MaxBodyBytes)
                throw new MultipartFormException(MultipartCodes.TooLarge);

            string boundary = null;
            for (int draw = 1; boundary == null; draw++)
            {
                string drawn = DrawBoundary(randomBytes);
                cancellationToken.ThrowIfCancellationRequested();
                if (!Collides(kept, drawn, progress, cancellationToken))
                    boundary = drawn;
                else if (draw >= MaxDraws)
                    throw new MultipartFormException(MultipartCodes.BoundaryCollision);
            }

            byte[] body = GC.AllocateUninitializedArray<byte>(checked((int)length));
            int written = Write(body, kept, boundary, progress, cancellationToken);
            if (written != body.Length)
                throw new InvalidOperationException("The form's bytes do not match its measured length.");
            return new MultipartFormBody(body, boundary, Array.AsReadOnly(kept));
        }

        // The header text with the form's Content-Type in place of every Content-Type line, at the first one's place or
        // appended, every other line's name, value and order kept, formatted as SetHeader formats it. It changes nothing:
        // FormatException when the header text is not valid, content-type-dropped when a Connection line nominates
        // Content-Type, which building the request would drop (RequestReplayDraft.CreateRequest).
        internal static string WithContentType(string headersText, string contentType)
        {
            ArgumentNullException.ThrowIfNull(contentType);
            if (RequestReplayDraft.IsNominatedByConnection(RequestReplayDraft.ParseHeaderText(headersText), "Content-Type"))
                throw new MultipartFormException(MultipartCodes.ContentTypeDropped);
            return RequestReplayDraft.SetHeaderIn(headersText, "Content-Type", contentType);
        }

        // Applies a built form to a request as one edit: its Content-Type, then its body, which this completes. Refused,
        // with the request unchanged, while the request's header block is left out (headers-omitted), when Connection
        // nominates Content-Type (content-type-dropped), or when the header text is not valid (FormatException).
        internal static void ApplyTo(RequestReplayDraft draft, MultipartFormBody form)
        {
            ArgumentNullException.ThrowIfNull(draft);
            ArgumentNullException.ThrowIfNull(form);
            if (draft.Omissions.Headers)
                throw new MultipartFormException(MultipartCodes.HeadersOmitted);
            string headers = WithContentType(draft.HeadersText, form.ContentType);
            draft.HeadersText = headers;
            draft.CompleteBody(form.Body);
        }

        // Content-Disposition, any Content-Type, and the blank line of a part with valid fields, as written.
        internal static string HeaderLines(bool isFile, string name, string fileName, string mediaType)
        {
            var lines = new StringBuilder();
            lines.Append(DispositionStart).Append(EscapeName(name)).Append('"');
            if (isFile)
                lines.Append(FileNameStart).Append(EncodeFileName(fileName)).Append('"');
            lines.Append("\r\n");
            string effective = MultipartPart.EffectiveMediaType(isFile, mediaType);
            if (effective != null)
                lines.Append(ContentTypeStart).Append(effective).Append("\r\n");
            lines.Append("\r\n");
            return lines.ToString();
        }

        internal static long FramingOf(string headerLines) => DelimiterLength + headerLines.Length + AfterContentLength;

        // A file name's largest spelling: every unit a character of three UTF-8 bytes, each written as %XX.
        private const int MaxEncodedFileNameChars = MaxFileNameChars * 9;

        private static string EscapeName(string name)
        {
            var escaped = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (c is '"' or '\\')
                    escaped.Append('\\');
                escaped.Append(c);
            }

            return escaped.ToString();
        }

        // Strict UTF-8, with every byte from 0x80 and every '%' written as %XX in uppercase hex (RFC 7578 4.2), and a quote
        // escaped; never filename*.
        private static string EncodeFileName(string fileName)
        {
            byte[] bytes = StrictUtf8.GetBytes(fileName);
            var encoded = new StringBuilder(bytes.Length);
            foreach (byte b in bytes)
            {
                if (b >= 0x80 || b == (byte)'%')
                    encoded.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
                else if (b == (byte)'"')
                    encoded.Append("\\\"");
                else
                    encoded.Append((char)b);
            }

            return encoded.ToString();
        }

        private static string DrawBoundary(Func<byte[]> randomBytes)
        {
            byte[] bytes = randomBytes != null ? randomBytes() : RandomNumberGenerator.GetBytes(RandomByteCount);
            if (bytes == null || bytes.Length != RandomByteCount)
                throw new InvalidOperationException("The random source must give exactly 16 bytes.");
            return BoundaryPrefix + Convert.ToHexString(bytes).ToLowerInvariant();
        }

        // Whether the boundary occurs in a part's header lines or content. Content is scanned in windows of at most WorkChunk,
        // each starting BoundaryLength - 1 before the end of the one before, so an occurrence across two windows is found
        // whole in the second; text is scanned as characters, which is the same as scanning its UTF-8 bytes, since the
        // boundary is ASCII and UTF-8 writes every other character as bytes from 0x80. The delimiter lines themselves are not
        // scanned.
        private static bool Collides(
            MultipartPart[] parts, string boundary, MultipartProgress progress, CancellationToken cancellationToken)
        {
            byte[] pattern = Encoding.ASCII.GetBytes(boundary);
            for (int index = 0; index < parts.Length; index++)
            {
                MultipartPart part = parts[index];
                if (part.HeaderLines.Contains(boundary, StringComparison.Ordinal))
                    return true;
                int length = part.Kind == MultipartPartKind.Text ? part.Value.Length : part.Snapshot.Length;
                for (int from = 0; from < length; from += WorkChunk - (BoundaryLength - 1))
                {
                    int end = (int)Math.Min(length, (long)from + WorkChunk);
                    bool found = part.Kind == MultipartPartKind.Text
                        ? part.Value.AsSpan(from, end - from).IndexOf(boundary.AsSpan(), StringComparison.Ordinal) >= 0
                        : part.Snapshot.AsSpan(from, end - from).IndexOf(pattern) >= 0;
                    if (found)
                        return true;
                    progress?.Invoke(MultipartStage.Scan, index, end);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (end == length)
                        break;
                }

                cancellationToken.ThrowIfCancellationRequested();
            }

            return false;
        }

        // Writes the parts and the closing delimiter, a unit of at most WorkChunk characters or bytes of content at a time;
        // returns the bytes written. Each text part has its own encoder, so a surrogate pair split between two units is
        // written whole.
        private static int Write(
            byte[] body, MultipartPart[] parts, string boundary, MultipartProgress progress, CancellationToken cancellationToken)
        {
            int offset = 0;
            for (int index = 0; index < parts.Length; index++)
            {
                MultipartPart part = parts[index];
                offset += Encoding.ASCII.GetBytes("--" + boundary + "\r\n" + part.HeaderLines, body.AsSpan(offset));
                if (part.Kind == MultipartPartKind.Text)
                {
                    Encoder encoder = StrictUtf8.GetEncoder();
                    ReadOnlySpan<char> value = part.Value;
                    for (int start = 0; start < value.Length;)
                    {
                        int end = Math.Min(value.Length, start + WorkChunk);
                        encoder.Convert(
                            value[start..end], body.AsSpan(offset), end == value.Length, out int used, out int bytes, out _);
                        offset += bytes;
                        start += used;
                        progress?.Invoke(MultipartStage.Write, index, start);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }
                else
                {
                    ReadOnlySpan<byte> content = part.Snapshot;
                    for (int start = 0; start < content.Length;)
                    {
                        int count = Math.Min(WorkChunk, content.Length - start);
                        content.Slice(start, count).CopyTo(body.AsSpan(offset));
                        offset += count;
                        start += count;
                        progress?.Invoke(MultipartStage.Write, index, start);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }

                body[offset++] = (byte)'\r';
                body[offset++] = (byte)'\n';
                cancellationToken.ThrowIfCancellationRequested();
            }

            offset += Encoding.ASCII.GetBytes("--" + boundary + "--\r\n", body.AsSpan(offset));
            return offset;
        }

        private static bool Token(string text, ref int index)
        {
            int start = index;
            while (index < text.Length && text[index] < '\u0080' && RequestReplayDraft.IsTokenCharacter(text[index]))
                index++;
            return index > start;
        }

        private static void Whitespace(string text, ref int index)
        {
            while (index < text.Length && text[index] is ' ' or '\t')
                index++;
        }

        // DQUOTE *( qdtext / quoted-pair ) DQUOTE, without obs-text.
        private static bool QuotedString(string text, ref int index)
        {
            index++;
            while (index < text.Length)
            {
                char c = text[index];
                if (c == '"')
                {
                    index++;
                    return true;
                }

                if (c == '\\')
                {
                    if (index + 1 >= text.Length || !IsQuotedPairCharacter(text[index + 1]))
                        return false;
                    index += 2;
                }
                else if (c is '\t' or ' ' or '!' || (c >= '#' && c <= '[') || (c >= ']' && c <= '~'))
                {
                    index++;
                }
                else
                {
                    return false;
                }
            }

            return false;
        }

        private static bool IsQuotedPairCharacter(char c) => c is '\t' or ' ' || (c >= '!' && c <= '~');
    }
}
