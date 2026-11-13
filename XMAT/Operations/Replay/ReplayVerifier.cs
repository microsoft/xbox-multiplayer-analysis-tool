// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Operations.Replay
{
    // Differences lists what did not match, in order: "status", "body". Empty when the replay matched.
    internal sealed record ReplayVerification(IReadOnlyList<string> Differences)
    {
        internal bool Matches => Differences.Count == 0;
    }

    // Reissue and verify: compares a completed replay with the response captured for its original request.
    internal static class ReplayVerifier
    {
        internal const string StatusDifference = "status";
        internal const string BodyDifference = "body";

        // Compares the status code, and the bodies once the stored chunked framing and a gzip, deflate or Brotli content
        // coding on either side are removed, each within maxDecodedBytes; a body that cannot be decoded is compared as it
        // is. Headers are not compared: dates, request ids and cookies differ on every response.
        internal static ReplayVerification Compare(
            ProxyConnectionModel original,
            ReplayExchange replay,
            long maxDecodedBytes,
            CancellationToken cancellationToken = default) =>
            CompareSides(original, replay, maxDecodedBytes, cancellationToken).Verification;

        // Compare's verdict, and what it compared on each side and how it got it (ReplayComparison), for
        // VerifyDiffOperation to explain. The comparison borrows the stored bodies and is not kept.
        internal static ReplayComparison CompareSides(
            ProxyConnectionModel original,
            ReplayExchange replay,
            long maxDecodedBytes,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(original);
            ArgumentNullException.ThrowIfNull(replay);
            var differences = new List<string>();
            int? originalStatus =
                int.TryParse(original.Status, NumberStyles.None, CultureInfo.InvariantCulture, out int status) ? status : null;
            if (originalStatus != replay.StatusCode)
            {
                differences.Add(StatusDifference);
            }

            original.ResponseHeaders.TryGetValue("Transfer-Encoding", out string transferEncoding);
            original.ResponseHeaders.TryGetValue("Content-Encoding", out string originalEncoding);
            original.ResponseHeaders.TryGetValue("Content-Type", out string originalType);
            byte[] stored = original.ResponseBody ?? Array.Empty<byte>();
            byte[] originalBody = stored;
            VerifyFraming framing;
            try
            {
                originalBody = ChunkedBodyDecoder.DecodeIfFramed(
                    transferEncoding, stored, (int)Math.Min(maxDecodedBytes, int.MaxValue));
                framing = ReferenceEquals(originalBody, stored) ? VerifyFraming.None : VerifyFraming.Removed;
            }
            catch (InvalidDataException)
            {
                // Malformed framing, or framed data over the limit, is compared as stored.
                framing = VerifyFraming.NotRemoved;
            }

            string replayEncoding = ContentHeader(replay.Response, "Content-Encoding");
            byte[] replayBody = replay.Response?.Body ?? Array.Empty<byte>();
            (byte[] originalCompared, VerifyBodyBasis originalBasis) =
                Decode(originalEncoding, originalBody, maxDecodedBytes, cancellationToken);
            (byte[] actualCompared, VerifyBodyBasis actualBasis) =
                Decode(replayEncoding, replayBody, maxDecodedBytes, cancellationToken);
            if (!originalCompared.AsSpan().SequenceEqual(actualCompared))
            {
                differences.Add(BodyDifference);
            }

            return new ReplayComparison(
                new ReplayVerification(differences.AsReadOnly()),
                originalStatus,
                replay.StatusCode,
                new VerifyBody(
                    originalCompared, originalBasis, framing, stored, originalType, originalEncoding, transferEncoding),
                new VerifyBody(
                    actualCompared,
                    actualBasis,
                    VerifyFraming.None,
                    replayBody,
                    ContentHeader(replay.Response, "Content-Type"),
                    replayEncoding,
                    null));
        }

        private static string ContentHeader(ReplayMessage response, string name) =>
            response?.ContentHeaders
                .Where(header => header.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                .Select(header => header.Value)
                .LastOrDefault();

        // The body without its content coding, or the body as it is when the coding is not one replay decodes or the
        // decoded content is malformed or larger than maxBytes; and which of these it is.
        private static (byte[] Bytes, VerifyBodyBasis Basis) Decode(
            string contentEncoding, byte[] body, long maxBytes, CancellationToken cancellationToken)
        {
            string coding = contentEncoding?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(coding) || coding == "identity" || body.Length == 0)
            {
                return (body, VerifyBodyBasis.Plain);
            }

            Func<Stream, Stream> decoder = coding switch
            {
                "gzip" or "x-gzip" => stream => new GZipStream(stream, CompressionMode.Decompress),
                "deflate" => stream => new ZLibStream(stream, CompressionMode.Decompress),
                "br" => stream => new BrotliStream(stream, CompressionMode.Decompress),
                _ => null,
            };
            if (decoder == null)
            {
                return (body, VerifyBodyBasis.UnsupportedCoding);
            }

            try
            {
                using Stream input = decoder(new MemoryStream(body, writable: false));
                using var output = new MemoryStream();
                var buffer = new byte[81920];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (output.Length + read > maxBytes)
                    {
                        return (body, VerifyBodyBasis.DecodedLimit);
                    }

                    output.Write(buffer, 0, read);
                }

                return (output.ToArray(), VerifyBodyBasis.Decoded);
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException)
            {
                // Content that does not decode is compared as it is.
                return (body, VerifyBodyBasis.MalformedCoding);
            }
        }
    }
}
