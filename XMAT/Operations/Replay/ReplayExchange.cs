// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace XMAT.Operations.Replay
{
    internal enum ReplayOutcome
    {
        // A response arrived, with any status code, and its body fit the size limit.
        Completed,

        // No response: the connection, the TLS handshake or the protocol failed.
        Failed,

        // No complete response within the timeout.
        TimedOut,

        // The response body exceeded the size limit: its status and headers were kept, its body was not.
        TooLarge,
    }

    // One HTTP message as replay built or received it.
    internal sealed class ReplayMessage
    {
        internal ReplayMessage(
            string startLine,
            IReadOnlyList<KeyValuePair<string, string>> headers,
            IReadOnlyList<KeyValuePair<string, string>> contentHeaders,
            byte[] body)
        {
            StartLine = startLine ?? throw new ArgumentNullException(nameof(startLine));
            Headers = headers ?? Array.Empty<KeyValuePair<string, string>>();
            ContentHeaders = contentHeaders ?? Array.Empty<KeyValuePair<string, string>>();
            Body = body ?? Array.Empty<byte>();
        }

        internal string StartLine { get; }

        internal IReadOnlyList<KeyValuePair<string, string>> Headers { get; }

        internal IReadOnlyList<KeyValuePair<string, string>> ContentHeaders { get; }

        internal byte[] Body { get; }

        // The message in HTTP/1.1 form: the start line, the header lines, an empty line and the body as it is. Header
        // text is written as Latin-1, the encoding the HTTP stack reads it with. The body is written, never copied.
        internal void WriteTo(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);
            var head = new StringBuilder(StartLine).Append("\r\n");
            foreach (KeyValuePair<string, string> header in Headers)
            {
                head.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            }

            foreach (KeyValuePair<string, string> header in ContentHeaders)
            {
                head.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            }

            head.Append("\r\n");
            stream.Write(Encoding.Latin1.GetBytes(head.ToString()));
            stream.Write(Body);
        }

        // WriteTo into memory, for callers that need the whole message at once.
        internal byte[] ToBytes()
        {
            using var bytes = new MemoryStream();
            WriteTo(bytes);
            return bytes.ToArray();
        }

        // The size that the message declared for its body (Content-Length), as a decimal, never checked against the body:
        // a response over the send's size limit comes without its body (ReplayOutcome.TooLarge), and this is all that is
        // known of it. UnknownLength when the message declared none that is a size.
        internal string DeclaredLength()
        {
            foreach (KeyValuePair<string, string> header in ContentHeaders.Concat(Headers))
            {
                if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    return long.TryParse(header.Value, NumberStyles.None, CultureInfo.InvariantCulture, out long length)
                        ? length.ToString(CultureInfo.InvariantCulture)
                        : UnknownLength;
                }
            }

            return UnknownLength;
        }

        internal const string UnknownLength = "unknown";

        // "HTTP/1.1" or "HTTP/1.0" as the version names them, and "HTTP/2" for HTTP/2, which has no minor version.
        internal static string FormatVersion(Version version) =>
            version == null
                ? "HTTP/1.1"
                : version.Major >= 2
                    ? "HTTP/" + version.Major.ToString(CultureInfo.InvariantCulture)
                    : "HTTP/" + version.ToString(2);
    }

    // The result of sending one request, including the redirects replay followed.
    internal sealed class ReplayExchange
    {
        internal ReplayOutcome Outcome { get; init; }

        // A stable code for a failure, or for a redirect that was not followed; null otherwise.
        internal string Code { get; init; }

        // The last request sent, as replay built it.
        internal ReplayMessage Request { get; init; }

        // The URL of the last request sent, after any redirects that were followed.
        internal Uri RequestUri { get; init; }

        // When the first request went out (UTC).
        internal DateTime Started { get; init; }

        // The final response; null when none arrived.
        internal ReplayMessage Response { get; init; }

        internal int StatusCode { get; init; }

        internal string ReasonPhrase { get; init; }

        internal Version Version { get; init; }

        internal TimeSpan Duration { get; init; }

        internal int Redirects { get; init; }

        // The transport failure, for the GUI to show. Its message can name hosts, so the command line never reports it. A
        // Windows-authentication send keeps none: its failures are their codes only.
        internal Exception Error { get; init; }

        // The Authorization scheme label that the final request of a Windows-authentication send carried when the HTTP
        // stack sent it, "Negotiate" or "NTLM", read before the header was removed from the live message; null when it
        // carried neither, and for every other send. It is a header label only: neither proof that authentication
        // succeeded nor the package that was negotiated.
        internal string AuthHeaderSchemeObserved { get; init; }

        // A raw send, whose request went out as its prepared bytes.
        internal bool Raw { get; init; }

        // A raw send's plaintext bytes in write calls to the connection's stream that completed: what XMAT handed to that
        // stream, not what reached the peer. Null for a Parsed send.
        internal long? RequestBytesWritten { get; init; }

        // A raw send's write state: none, partial, uncertain or complete (RawWriteState). Null for a Parsed send.
        internal string RequestWrite { get; init; }

        // A raw send's response framing: content-length, chunked, close or none. Null for a Parsed send, and when no
        // response was read.
        internal string ResponseFraming { get; init; }

        // The fields of a raw response's chunked trailer section: counted, never merged and not kept.
        internal int ResponseTrailerFields { get; init; }

        // The origin that the request inherits: its captured origin, or else the origin that it was sent to.
        internal Uri ProvenanceOrigin { get; init; }

        // The send's verification differences, set once by ReplayOperation before the sink sees the exchange when the run
        // asked for them (ReplayOptions.VerifyDiff) and the send was verified; null otherwise. They can hold captured
        // content, so they travel with the exchange to the sink and never into ReplayResult.
        internal VerifyDiff VerifyDiff { get; private set; }

        internal void SetVerifyDiff(VerifyDiff diff)
        {
            ArgumentNullException.ThrowIfNull(diff);
            if (VerifyDiff != null)
            {
                throw new InvalidOperationException("The exchange already has its verification differences.");
            }

            VerifyDiff = diff;
        }
    }
}
