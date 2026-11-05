// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Text;

namespace XMAT.Operations.Inspection
{
    // How a body reads as text (BodyTextRules.Read). Encoding, Charset and Preamble (the length of a byte order mark to
    // skip), or no Encoding for a declared charset that the rules do not decode. Declared: Content-Type declares the charset.
    // TextType: it says text (text/*, or JSON, XML or JavaScript of any kind); JsonType: it says JSON. HeaderCut: the
    // Content-Type was longer than the header limit, so it said nothing.
    internal readonly record struct BodyTextFormat(
        Encoding Encoding,
        string Charset,
        int Preamble,
        bool Declared,
        bool TextType,
        bool JsonType,
        bool HeaderCut)
    {
        internal bool Supported => Encoding != null;

        // Invalid text is malformed only when the message says that it is text; otherwise it is binary.
        internal bool DeclaredText => Declared || TextType;

        // The charsets whose text the JSON views read.
        internal bool JsonCharset =>
            ReferenceEquals(Encoding, BodyTextRules.StrictUtf8) || ReferenceEquals(Encoding, BodyTextRules.StrictAscii);
    }

    // The text rules that the body preview and the verification differences share: the charset that Content-Type declares,
    // else the one that a byte order mark says, else UTF-8, each decoded strictly; and text holds no control character but
    // tab, CR and LF. Content-Type is read only up to the header limit that the caller gives.
    internal static class BodyTextRules
    {
        internal static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        internal static readonly Encoding StrictUtf16Le = new UnicodeEncoding(false, false, true);
        internal static readonly Encoding StrictUtf16Be = new UnicodeEncoding(true, false, true);
        internal static readonly Encoding StrictAscii =
            Encoding.GetEncoding("us-ascii", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

        // Controls other than tab, CR and LF: text that holds one is not text.
        private static readonly SearchValues<char> NotTextCharacters = SearchValues.Create(NotTextList());

        // How bytes, the start of a body or all of it, read as text under contentType, which says nothing when it is longer
        // than maxHeaderChars.
        internal static BodyTextFormat Read(string contentType, int maxHeaderChars, ReadOnlySpan<byte> bytes)
        {
            bool headerCut = contentType != null && contentType.Length > maxHeaderChars;
            if (headerCut)
            {
                contentType = null;
            }

            bool supported = TryCharset(contentType, bytes, out Encoding encoding, out string charset, out int preamble, out bool declared);
            return new BodyTextFormat(
                supported ? encoding : null,
                supported ? charset : null,
                preamble,
                declared,
                IsTextType(contentType),
                contentType != null && contentType.Contains("json", StringComparison.OrdinalIgnoreCase),
                headerCut);
        }

        // Whether text holds a character that text never holds.
        internal static bool HasNotText(ReadOnlySpan<char> text) => text.IndexOfAny(NotTextCharacters) >= 0;

        // The charset that Content-Type declares (declared), else the one that the byte order mark says, else UTF-8; false
        // for a declared charset that the rules do not decode. preamble is the length of a byte order mark to skip.
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

        // A media type that says the body is text: text/*, or JSON, XML or JavaScript of any kind.
        private static bool IsTextType(string contentType) =>
            contentType != null &&
            (contentType.TrimStart().StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
                contentType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
                contentType.Contains("xml", StringComparison.OrdinalIgnoreCase) ||
                contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase));

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
}
