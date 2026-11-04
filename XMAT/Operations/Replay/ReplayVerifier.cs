// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
        internal static ReplayVerification Compare(ProxyConnectionModel original, ReplayExchange replay, long maxDecodedBytes)
        {
            ArgumentNullException.ThrowIfNull(original);
            ArgumentNullException.ThrowIfNull(replay);
            var differences = new List<string>();
            if (!int.TryParse(original.Status, NumberStyles.None, CultureInfo.InvariantCulture, out int status) ||
                status != replay.StatusCode)
            {
                differences.Add(StatusDifference);
            }

            original.ResponseHeaders.TryGetValue("Transfer-Encoding", out string transferEncoding);
            original.ResponseHeaders.TryGetValue("Content-Encoding", out string originalEncoding);
            byte[] originalBody = original.ResponseBody ?? Array.Empty<byte>();
            try
            {
                originalBody = ChunkedBodyDecoder.DecodeIfFramed(
                    transferEncoding, originalBody, (int)Math.Min(maxDecodedBytes, int.MaxValue));
            }
            catch (InvalidDataException)
            {
                // Malformed framing is compared as stored.
            }

            string replayEncoding = replay.Response?.ContentHeaders
                .Where(header => header.Key.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase))
                .Select(header => header.Value)
                .LastOrDefault();
            byte[] replayBody = replay.Response?.Body ?? Array.Empty<byte>();
            if (!Decode(originalEncoding, originalBody, maxDecodedBytes).AsSpan().SequenceEqual(
                Decode(replayEncoding, replayBody, maxDecodedBytes)))
            {
                differences.Add(BodyDifference);
            }

            return new ReplayVerification(differences);
        }

        // The body without its content coding, or the body as it is when the coding is not one replay decodes or the
        // decoded content is malformed or larger than maxBytes.
        private static byte[] Decode(string contentEncoding, byte[] body, long maxBytes)
        {
            string coding = contentEncoding?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(coding) || coding == "identity" || body.Length == 0)
            {
                return body;
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
                return body;
            }

            try
            {
                using Stream input = decoder(new MemoryStream(body, writable: false));
                using var output = new MemoryStream();
                var buffer = new byte[81920];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (output.Length + read > maxBytes)
                    {
                        return body;
                    }

                    output.Write(buffer, 0, read);
                }

                return output.ToArray();
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException)
            {
                // Content that does not decode is compared as it is.
                return body;
            }
        }
    }
}
