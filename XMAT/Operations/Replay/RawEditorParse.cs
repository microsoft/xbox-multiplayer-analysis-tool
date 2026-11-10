// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;

namespace XMAT.Operations.Replay
{
    // What the Raw-to-Parsed conversion reads (RawRequest.ParseForEditor, then RawConversion.ToDraft). It is never
    // sendable, holds no admission, and no API that sends takes it. Its checks are the limits, the head's
    // syntax, the version, the request-target's syntax, routing and framing; Parsed mode checks the rest in its own words.
    internal sealed class RawEditorParse
    {
        private RawEditorParse(
            ReplayRawTarget target,
            RawParsedRequest parsed,
            ReadOnlyMemory<byte> body,
            IReadOnlyList<int> chunkSizes,
            int convertedLineFeeds)
        {
            Target = target;
            Method = parsed.Method;
            RequestTarget = parsed.RequestTarget;
            AbsoluteForm = parsed.AbsoluteForm;
            OriginForm = parsed.OriginForm;
            Fields = new ReadOnlyCollection<KeyValuePair<string, string>>(parsed.Fields.ToArray());
            FieldText = new ReadOnlyCollection<string>(parsed.FieldText.ToArray());
            Framing = parsed.Framing;
            Body = body;
            ChunkSizes = chunkSizes;
            ConvertedLineFeeds = convertedLineFeeds;
        }

        internal ReplayRawTarget Target { get; }

        internal string Method { get; }

        // The request-target as written.
        internal string RequestTarget { get; }

        // Whether the request-target is an absolute form, which ParseForEditor proved names the target.
        internal bool AbsoluteForm { get; }

        // The request-target's origin form: as written, or after an absolute form's scheme and authority.
        internal string OriginForm { get; }

        // Field names as written and values with OWS trimmed, in order.
        internal IReadOnlyList<KeyValuePair<string, string>> Fields { get; }

        // Each field line as written, without its line break, in order.
        internal IReadOnlyList<string> FieldText { get; }

        internal RawFraming Framing { get; }

        // The entity: a Content-Length body's bytes, or a chunked body's data without its framing.
        internal ReadOnlyMemory<byte> Body { get; }

        internal long EntityLength => Body.Length;

        // The size of each chunk of a chunked body, in order; empty for any other framing.
        internal IReadOnlyList<int> ChunkSizes { get; }

        // The line breaks typed as LF that the conversion of the head wrote as CRLF.
        internal int ConvertedLineFeeds { get; }

        // The editor's head text, converted as RawRequest.ConvertHead says, then a copy of its wire body, checked as the
        // conversion needs. A refusal carries the code and data-free line of the first rule that refused it.
        internal static RawEditorParseResult Parse(
            string headText, ReadOnlyMemory<byte> body, ReplayRawTarget target, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(target);
            token.ThrowIfCancellationRequested();
            RawImageLimits limits = RawImageLimits.Production;
            if ((long)body.Length > (long)limits.EntityBytes + limits.ChunkFramingBytes)
            {
                return Refused(new RawFailure(RawCodes.InputTooLarge, null));
            }

            RawFailure? failure = RawRequest.ConvertHead(
                headText, limits, null, token, out byte[] head, out int convertedLineFeeds);
            if (failure != null)
            {
                return Refused(failure.Value);
            }

            token.ThrowIfCancellationRequested();
            var image = new byte[head.Length + body.Length];
            head.CopyTo(image, 0);
            body.Span.CopyTo(image.AsSpan(head.Length));
            failure = RawRequestParser.ReadHead(image, limits, token, out RawParsedRequest parsed) ??
                RawRequestParser.CheckEditor(image, parsed, target, limits, token);
            if (failure != null)
            {
                return Refused(failure.Value);
            }

            ReadOnlyMemory<byte> entity;
            int[] chunkSizes;
            if (parsed.Framing == RawFraming.Chunked)
            {
                var decoded = new byte[parsed.EntityLength];
                chunkSizes = new int[parsed.Chunks.Count];
                int position = 0;
                for (int index = 0; index < parsed.Chunks.Count; index++)
                {
                    if ((index & 1023) == 0)
                    {
                        token.ThrowIfCancellationRequested();
                    }

                    (int offset, int length) = parsed.Chunks[index];
                    Buffer.BlockCopy(image, offset, decoded, position, length);
                    position += length;
                    chunkSizes[index] = length;
                }

                entity = decoded;
            }
            else
            {
                entity = image.AsMemory(parsed.HeadLength);
                chunkSizes = Array.Empty<int>();
            }

            return new RawEditorParseResult(
                new RawEditorParse(target, parsed, entity, Array.AsReadOnly(chunkSizes), convertedLineFeeds), null, null);
        }

        private static RawEditorParseResult Refused(RawFailure failure) => new(null, failure.Code, failure.Line);
    }
}
