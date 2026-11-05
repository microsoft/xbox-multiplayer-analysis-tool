// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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

        // "HTTP/1.1", or "HTTP/2" when the minor version is 0, as the proxy shows versions.
        internal static string FormatVersion(Version version) =>
            version == null
                ? "HTTP/1.1"
                : version.Minor == 0
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

        // The transport failure, for the GUI to show. Its message can name hosts, so the command line never reports it.
        internal Exception Error { get; init; }

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
