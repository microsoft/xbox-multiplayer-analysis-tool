// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Buffers;
using System.Globalization;
using System.Text;

namespace XMAT.WebServiceCapture
{
    // Text that comes from outside XMAT, such as a query parameter's name, which is whatever its URL decodes to, or a file's
    // name, as a window shows it, so that it cannot break, hide or reorder the text around it. Each character that is not
    // visible as itself, or that can change how text reads, is shown as \uXXXX, one per UTF-16 code unit: controls, line
    // and paragraph separators, spaces other than ' ', format characters such as bidirectional controls and zero-width
    // characters, combining marks, private-use and unassigned characters, and lone surrogates. Text longer than maxChars as
    // shown is cut after a whole character or escape, and ends with an ellipsis.
    internal static class UntrustedText
    {
        private const char Ellipsis = '\u2026';

        internal static string Show(string text, int maxChars)
        {
            ArgumentNullException.ThrowIfNull(text);
            ArgumentOutOfRangeException.ThrowIfNegative(maxChars);
            var shown = new StringBuilder(Math.Min(text.Length, maxChars) + 1);
            for (int index = 0; index < text.Length;)
            {
                OperationStatus status = Rune.DecodeFromUtf16(text.AsSpan(index), out Rune rune, out int used);
                used = Math.Max(used, 1);
                string piece = status == OperationStatus.Done && IsShownAsItself(rune)
                    ? text.Substring(index, used)
                    : Escaped(text.AsSpan(index, used));
                if (shown.Length + piece.Length > maxChars)
                {
                    shown.Append(Ellipsis);
                    break;
                }

                shown.Append(piece);
                index += used;
            }

            return shown.ToString();
        }

        // The parts shown as Show shows them joined, read as one text, so that a character whose two halves are in two parts
        // is one character; but the result never has more than maxChars characters, its ellipsis included. Text that does
        // not fit ends with the ellipsis after the whole characters and escapes that leave room for it, and maxChars 0 shows
        // nothing. Each part is read only as far as the result needs, and the parts are never joined, so a very long part,
        // such as a URL's path, costs no more than what is shown of it.
        internal static string ShowParts(int maxChars, params string[] parts)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(maxChars);
            ArgumentNullException.ThrowIfNull(parts);
            var shown = new StringBuilder(Math.Min(maxChars, 256));
            Span<char> units = stackalloc char[2];
            int part = 0;
            int index = 0;
            int lastPiece = 0;
            while (true)
            {
                SkipUsedParts(parts, ref part, ref index);
                if (part == parts.Length)
                {
                    return shown.ToString();
                }

                units[0] = parts[part][index];
                int used = 1;
                if (char.IsHighSurrogate(units[0]) && TryPeekNext(parts, part, index, out char next) && char.IsLowSurrogate(next))
                {
                    units[1] = next;
                    used = 2;
                }

                ReadOnlySpan<char> character = units[..used];
                string escaped = Rune.DecodeFromUtf16(character, out Rune rune, out int decoded) == OperationStatus.Done &&
                    decoded == used && IsShownAsItself(rune)
                    ? null
                    : Escaped(character);
                int pieceLength = escaped?.Length ?? used;
                if (shown.Length + pieceLength > maxChars)
                {
                    if (maxChars == 0)
                    {
                        return string.Empty;
                    }

                    // The ellipsis takes the place of the last whole character or escape when the text so far fills the bound.
                    if (shown.Length == maxChars)
                    {
                        shown.Length -= lastPiece;
                    }

                    return shown.Append(Ellipsis).ToString();
                }

                if (escaped == null)
                {
                    shown.Append(character);
                }
                else
                {
                    shown.Append(escaped);
                }

                lastPiece = pieceLength;
                index++;
                if (used == 2)
                {
                    SkipUsedParts(parts, ref part, ref index);
                    index++;
                }
            }
        }

        private static void SkipUsedParts(string[] parts, ref int part, ref int index)
        {
            while (part < parts.Length && index >= (parts[part]?.Length ?? 0))
            {
                part++;
                index = 0;
            }
        }

        // The code unit after the one at index in parts[part], in that part or at the start of the next part that has any.
        private static bool TryPeekNext(string[] parts, int part, int index, out char next)
        {
            index++;
            SkipUsedParts(parts, ref part, ref index);
            next = part < parts.Length ? parts[part][index] : default;
            return part < parts.Length;
        }

        private static bool IsShownAsItself(Rune rune) =>
            rune.Value == ' ' ||
            Rune.GetUnicodeCategory(rune) is not (
                UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or
                UnicodeCategory.ParagraphSeparator or UnicodeCategory.SpaceSeparator or UnicodeCategory.Surrogate or
                UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.NonSpacingMark or
                UnicodeCategory.EnclosingMark);

        private static string Escaped(ReadOnlySpan<char> units)
        {
            var escaped = new StringBuilder(units.Length * 6);
            foreach (char unit in units)
                escaped.Append("\\u").Append(((int)unit).ToString("X4", CultureInfo.InvariantCulture));
            return escaped.ToString();
        }
    }
}
