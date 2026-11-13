// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Text;
using System.Threading;

namespace XMAT.WebServiceCapture
{
    // A Raw mode body as the composer froze it: the exact bytes that the window holds for it (Held: an uploaded file, a
    // form, or a body larger than the editor shows), never changed in place; otherwise the draft's text, and whether that
    // text is Base64.
    internal readonly record struct RawBodySource(string Text, bool Base64, byte[] Held)
    {
        // The body of a frozen copy of the draft, whose held bytes the window gives, if it holds any. Off the UI thread: the
        // text of a draft that holds bytes that the window does not show by their size, such as a Base64 body that Use this
        // body supplied, is made from them.
        internal static RawBodySource Of(RequestReplayDraft draft, byte[] held)
        {
            ArgumentNullException.ThrowIfNull(draft);
            return held != null
                ? new RawBodySource(null, false, held)
                : new RawBodySource(draft.BodyText ?? string.Empty, draft.BodyIsBase64, null);
        }
    }

    // The bytes of a Raw mode body, built off the UI thread once TryCountBody has counted the
    // same body and found no problem: exactly the bytes that RequestReplayDraft.GetBodyBytes makes of the whole text, or the
    // held bytes themselves, never copied. Text and Base64 are read in slices, with the token checked before each: text
    // through one strict UTF-8 encoder, whose state carries a surrogate pair that two slices split and which is flushed
    // only after the last; Base64 through one decoder that carries an unfinished group of four across slices, skips spaces,
    // tabs and line breaks wherever they are, and takes '=' only at the end, as Convert.FromBase64String reads it.
    internal static class RawBodyBytes
    {
        // The characters of a text or Base64 body read between two checks of the token.
        internal const int DefaultSliceChars = 1 << 20;

        private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        // count is TryCountBody's count of this body: the result has its length. afterSlice gets each slice's index once
        // that slice is read (tests).
        internal static byte[] Build(
            RawBodySource source,
            BodyCount count,
            CancellationToken token,
            int sliceChars = DefaultSliceChars,
            Action<int> afterSlice = null)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(sliceChars, 1);
            if (count.Problem != BodyProblem.None)
            {
                throw new ArgumentException("The body was not counted as valid.", nameof(count));
            }

            token.ThrowIfCancellationRequested();
            if (source.Held != null)
            {
                return source.Held.LongLength == count.Length
                    ? source.Held
                    : throw new ArgumentException("The count is not the held body's length.", nameof(count));
            }

            ArgumentOutOfRangeException.ThrowIfGreaterThan(count.Length, Array.MaxLength, nameof(count));
            var bytes = new byte[count.Length];
            string text = source.Text ?? string.Empty;
            int written = source.Base64
                ? DecodeBase64(text, bytes, sliceChars, afterSlice, token)
                : EncodeText(text, bytes, sliceChars, afterSlice, token);
            return written == bytes.Length
                ? bytes
                : throw new ArgumentException("The count is not the body's length.", nameof(count));
        }

        private static int EncodeText(
            string text, byte[] bytes, int sliceChars, Action<int> afterSlice, CancellationToken token)
        {
            Encoder encoder = StrictUtf8.GetEncoder();
            int written = 0;
            int slice = 0;
            for (int start = 0; start < text.Length; slice++)
            {
                token.ThrowIfCancellationRequested();
                int end = (int)Math.Min(text.Length, (long)start + sliceChars);
                written += encoder.GetBytes(
                    text.AsSpan(start, end - start), bytes.AsSpan(written), flush: end == text.Length);
                afterSlice?.Invoke(slice);
                start = end;
            }

            token.ThrowIfCancellationRequested();
            return written;
        }

        private static int DecodeBase64(
            string text, byte[] bytes, int sliceChars, Action<int> afterSlice, CancellationToken token)
        {
            // The group of four read so far, six bits a character, how many characters it holds, and how many of them are
            // '='; the bits of a padded group past its bytes are not read, as Convert.FromBase64String does not read them.
            int group = 0;
            int characters = 0;
            int padding = 0;
            int written = 0;
            int slice = 0;
            for (int start = 0; start < text.Length; slice++)
            {
                token.ThrowIfCancellationRequested();
                int end = (int)Math.Min(text.Length, (long)start + sliceChars);
                for (int index = start; index < end; index++)
                {
                    char character = text[index];
                    if (character is ' ' or '\t' or '\r' or '\n')
                    {
                        continue;
                    }

                    if (character == '=')
                    {
                        padding++;
                        group <<= 6;
                    }
                    else if (padding > 0)
                    {
                        throw new ArgumentException("The body is not valid Base64.", nameof(text));
                    }
                    else
                    {
                        group = (group << 6) | Sextet(character);
                    }

                    if (++characters == 4)
                    {
                        bytes[written++] = (byte)(group >> 16);
                        if (padding < 2)
                        {
                            bytes[written++] = (byte)(group >> 8);
                        }

                        if (padding < 1)
                        {
                            bytes[written++] = (byte)group;
                        }

                        group = 0;
                        characters = 0;
                    }
                }

                afterSlice?.Invoke(slice);
                start = end;
            }

            token.ThrowIfCancellationRequested();
            return characters == 0
                ? written
                : throw new ArgumentException("The body is not valid Base64.", nameof(text));
        }

        private static int Sextet(char character) => character switch
        {
            >= 'A' and <= 'Z' => character - 'A',
            >= 'a' and <= 'z' => character - 'a' + 26,
            >= '0' and <= '9' => character - '0' + 52,
            '+' => 62,
            '/' => 63,
            _ => throw new ArgumentException("The body is not valid Base64.", nameof(character)),
        };
    }
}
