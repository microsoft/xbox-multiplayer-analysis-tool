// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.Operations.Replay
{
    // A raw request that may be sent (slice L, contract 2.4): the head, ending with the empty line, then the wire body,
    // in one private array. Only FromBytes and Prepare make one, from a copy of their input, and only after every rule of
    // contract 2.2 passed on that copy. The array never leaves the image: writes go through a scratch buffer of their
    // own, and the Copy methods return new arrays.
    internal sealed class RawRequestImage
    {
        // The most that one write copies from the private array into the scratch buffer.
        internal const int WriteBytes = 16 * 1024;

        private const int HashBlockBytes = 1024 * 1024;

        private readonly byte[] _image;
        private readonly IReadOnlyList<(int Offset, int Length)> _chunks;

        private RawRequestImage(
            ReplayRawTarget target, byte[] image, RawParsedRequest parsed, int convertedLineFeeds, string sha256)
        {
            _image = image;
            _chunks = parsed.Chunks.ToArray();
            Target = target;
            HeadLength = parsed.HeadLength;
            Fields = new ReadOnlyCollection<KeyValuePair<string, string>>(parsed.Fields.ToArray());
            Method = parsed.Method;
            RequestTarget = parsed.RequestTarget;
            RequestUri = parsed.RequestUri;
            Framing = parsed.Framing;
            EntityLength = parsed.EntityLength;
            ConvertedLineFeeds = convertedLineFeeds;
            UsesUnsafeMethod = RawRequestParser.UsesUnsafeMethod(parsed);
            Sha256 = sha256;
        }

        internal ReplayRawTarget Target { get; }

        internal int Length => _image.Length;

        // The head's length, its final CRLF CRLF included.
        internal int HeadLength { get; }

        internal int FieldCount => Fields.Count;

        // Field names as written and values with OWS trimmed, in order.
        internal IReadOnlyList<KeyValuePair<string, string>> Fields { get; }

        internal string Method { get; }

        // The request-target as written.
        internal string RequestTarget { get; }

        // For recording only: the target's origin and the request-target's origin form joined as strings, never
        // resolved or canonicalized.
        internal Uri RequestUri { get; }

        internal RawFraming Framing { get; }

        internal long EntityLength { get; }

        // The line breaks typed as LF that Prepare wrote as CRLF; 0 for FromBytes, which converts nothing.
        internal int ConvertedLineFeeds { get; }

        // A method that can change server state, or GET or OPTIONS with a body or chunked framing (contract 2.2 item 19).
        internal bool UsesUnsafeMethod { get; }

        // The image's SHA-256 in lowercase hexadecimal, computed once, when the image was made.
        internal string Sha256 { get; }

        // A request read as bytes: copied first, then checked in full. With fixContentLength, the copy's one
        // Content-Length is first set to the wire body's length (contract 2.3); a head that does not end within the head
        // limit is left to the checks, which refuse it. afterCopy runs between the copy and the checks (tests only).
        internal static RawImageResult FromBytes(
            ReplayRawTarget target,
            ReadOnlyMemory<byte> request,
            bool fixContentLength,
            RawImageLimits limits,
            CancellationToken token,
            Action afterCopy = null)
        {
            ArgumentNullException.ThrowIfNull(target);
            ArgumentNullException.ThrowIfNull(limits);
            token.ThrowIfCancellationRequested();
            if ((long)request.Length > (long)limits.HeadBytes + limits.EntityBytes + limits.ChunkFramingBytes)
            {
                return Refused(new RawFailure(RawCodes.InputTooLarge, null));
            }

            byte[] image = request.ToArray();
            afterCopy?.Invoke();
            token.ThrowIfCancellationRequested();
            if (fixContentLength)
            {
                RawFailure? failure = FixContentLength(ref image, limits, token);
                if (failure != null)
                {
                    return Refused(failure.Value);
                }
            }

            return Admit(target, image, convertedLineFeeds: 0, limits, token);
        }

        // A request from the editor: its head text, converted as RawRequest.ConvertHead says, then a copy of its wire
        // body; checked in full. afterCopy runs between the copy and the checks, and checkpoint at each check of the token
        // while the head is read (tests only).
        internal static RawImageResult Prepare(
            string headText,
            ReadOnlyMemory<byte> body,
            ReplayRawTarget target,
            RawImageLimits limits,
            CancellationToken token,
            Action afterCopy = null,
            Action<long> checkpoint = null)
        {
            ArgumentNullException.ThrowIfNull(target);
            ArgumentNullException.ThrowIfNull(limits);
            token.ThrowIfCancellationRequested();
            if ((long)body.Length > (long)limits.EntityBytes + limits.ChunkFramingBytes)
            {
                return Refused(new RawFailure(RawCodes.InputTooLarge, null));
            }

            RawFailure? failure = RawRequest.ConvertHead(
                headText, limits, checkpoint, token, out byte[] head, out int convertedLineFeeds);
            if (failure != null)
            {
                return Refused(failure.Value);
            }

            token.ThrowIfCancellationRequested();
            var image = new byte[head.Length + body.Length];
            head.CopyTo(image, 0);
            body.Span.CopyTo(image.AsSpan(head.Length));
            afterCopy?.Invoke();
            return Admit(target, image, convertedLineFeeds, limits, token);
        }

        // The SHA-256 of the private array again, for the operation's recheck at admission (replay.raw.changed).
        internal string ComputeSha256(CancellationToken token) => Hash(_image, token);

        // The normalized request (contract 2.8): the request line as sent, the fields with OWS trimmed, a chunked body's
        // Transfer-Encoding replaced in place by Content-Length with the entity's length, and the entity.
        internal ReplayMessage ToMessage()
        {
            string entityLength = EntityLength.ToString(CultureInfo.InvariantCulture);
            var headers = new List<KeyValuePair<string, string>>(Fields.Count);
            foreach (KeyValuePair<string, string> field in Fields)
            {
                headers.Add(
                    Framing == RawFraming.Chunked &&
                        field.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                        ? new KeyValuePair<string, string>("Content-Length", entityLength)
                        : field);
            }

            return new ReplayMessage(Method + " " + RequestTarget + " HTTP/1.1", headers, null, CopyEntity());
        }

        // Writes the image through a scratch buffer of at most WriteBytes, never handing the stream the private array.
        // completedWrites gets the bytes written so far after each write that completed.
        internal async Task WriteToAsync(Stream stream, IProgress<long> completedWrites, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(stream);
            var scratch = new byte[Math.Min(WriteBytes, Math.Max(1, _image.Length))];
            long written = 0;
            for (int offset = 0; offset < _image.Length; offset += WriteBytes)
            {
                token.ThrowIfCancellationRequested();
                int count = Math.Min(WriteBytes, _image.Length - offset);
                Buffer.BlockCopy(_image, offset, scratch, 0, count);
                await stream.WriteAsync(scratch.AsMemory(0, count), token).ConfigureAwait(false);
                written += count;
                completedWrites?.Report(written);
            }
        }

        // WriteToAsync without progress or cancellation, for files such as request.http.
        internal void WriteTo(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);
            var scratch = new byte[Math.Min(WriteBytes, Math.Max(1, _image.Length))];
            for (int offset = 0; offset < _image.Length; offset += WriteBytes)
            {
                int count = Math.Min(WriteBytes, _image.Length - offset);
                Buffer.BlockCopy(_image, offset, scratch, 0, count);
                stream.Write(scratch, 0, count);
            }
        }

        internal byte[] CopyHead() => _image.AsSpan(0, HeadLength).ToArray();

        internal byte[] CopyWireBody() => _image.AsSpan(HeadLength).ToArray();

        // The entity: the wire body, or a chunked body's data without its framing.
        internal byte[] CopyEntity()
        {
            if (Framing != RawFraming.Chunked)
            {
                return CopyWireBody();
            }

            var entity = new byte[EntityLength];
            int position = 0;
            foreach ((int offset, int length) in _chunks)
            {
                Buffer.BlockCopy(_image, offset, entity, position, length);
                position += length;
            }

            return entity;
        }

        private static RawImageResult Admit(
            ReplayRawTarget target, byte[] image, int convertedLineFeeds, RawImageLimits limits, CancellationToken token)
        {
            RawFailure? failure = RawRequestParser.ReadHead(image, limits, token, out RawParsedRequest parsed) ??
                RawRequestParser.CheckImage(image, parsed, target, limits, token);
            if (failure != null)
            {
                return Refused(failure.Value);
            }

            string sha256 = Hash(image, token);
            return new RawImageResult(new RawRequestImage(target, image, parsed, convertedLineFeeds, sha256), null, null);
        }

        // Fix Content-Length on the copy: the head's text is Latin-1, one character per byte, so every byte but the value's
        // comes back unchanged.
        private static RawFailure? FixContentLength(ref byte[] image, RawImageLimits limits, CancellationToken token)
        {
            int headLength = RawRequestParser.HeadLength(image, Math.Min(image.Length, limits.HeadBytes));
            if (headLength < 0)
            {
                return null;
            }

            RawTextResult fixedHead = RawRequest.FixContentLength(
                Encoding.Latin1.GetString(image, 0, headLength), image.Length - headLength, token);
            if (fixedHead.Code != null)
            {
                return new RawFailure(fixedHead.Code, fixedHead.Line);
            }

            byte[] head = Encoding.Latin1.GetBytes(fixedHead.Text);
            var result = new byte[head.Length + image.Length - headLength];
            head.CopyTo(result, 0);
            Buffer.BlockCopy(image, headLength, result, head.Length, image.Length - headLength);
            image = result;
            return null;
        }

        private static string Hash(byte[] image, CancellationToken token)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (int offset = 0; offset < image.Length; offset += HashBlockBytes)
            {
                token.ThrowIfCancellationRequested();
                hash.AppendData(image, offset, Math.Min(HashBlockBytes, image.Length - offset));
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        private static RawImageResult Refused(RawFailure failure) => new(null, failure.Code, failure.Line);
    }
}
