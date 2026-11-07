// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using XMAT.Operations.Capture;
using XMAT.WebServiceCapture;

namespace XMAT.Operations.Replay
{
    // A send as a session of a Fiddler archive (SazArchiveWriter), as xmat replay and compose save it with --save-saz: a
    // transcript of what replay sent and got, normalized, not a capture of the wire.
    // - The request's start line has its method and HTTP version as sent, and its target in absolute form: the final URL,
    //   after the redirects that were followed, as {scheme}://{authority}{path and query}, in ASCII and escaped, without
    //   user information or a fragment (ReplayAuthority). Host comes first with that same authority, whatever Host was
    //   recorded, then the other fields that replay recorded, in order, one line each: a reader joins repeated ones.
    // - A response keeps its head and its body, with the chunked framing removed and its content encoding as it came. One
    //   larger than the response size limit (TooLarge) came without its body: its head ends with
    //   X-XMAT-Response-Body-Not-Kept, the size that the response declared or "unknown", and its body is empty. A send
    //   without a complete response (Failed, TimedOut) has no response.
    // - The session's Id is the send's id, and its flags name the send: Flag* below, in that order, each value in the
    //   alphabet [0-9A-Za-z._:-] and at most 64 characters long, which ToSession checks. TransportFlag names a send that
    //   was not direct ("connect-to" or "proxy"), never its endpoint or proxy; a direct send has none.
    //   ClientCertificateFlag ("true") marks a send that could offer a client certificate, never which one, nor whether a
    //   server received it; a send without one has none.
    internal static class ReplaySaz
    {
        internal const string ClientIpFlag = "x-clientip";
        internal const string IdFlag = "x-xmat-replay-id";
        internal const string RequestFlag = "x-xmat-replay-of";
        internal const string DeviceFlag = "x-xmat-replay-device";
        internal const string SequenceFlag = "x-xmat-replay-sequence";
        internal const string OutcomeFlag = "x-xmat-replay-outcome";
        internal const string CodeFlag = "x-xmat-replay-code";
        internal const string RedirectsFlag = "x-xmat-replay-redirects";
        internal const string TransportFlag = "x-xmat-replay-transport";
        internal const string ClientCertificateFlag = "x-xmat-replay-client-certificate-configured";
        internal const int MaxFlagChars = 64;

        // The session of one send; null for a send that recorded no request, which has nothing to save, as it has no
        // request.http either.
        internal static SazSession ToSession(ReplayItemResult result, ReplayExchange exchange)
        {
            ArgumentNullException.ThrowIfNull(result);
            ArgumentNullException.ThrowIfNull(exchange);
            ReplayMessage request = exchange.Request;
            Uri uri = exchange.RequestUri;
            if (request == null || uri == null)
            {
                return null;
            }

            string startLine = request.StartLine;
            int methodEnd = startLine.IndexOf(' ');
            int versionStart = startLine.LastIndexOf(' ');
            if (methodEnd <= 0 || versionStart <= methodEnd)
            {
                throw new InvalidOperationException("A recorded request line has a method, a target and a version.");
            }

            string authority = ReplayAuthority.Ascii(uri);
            string target = uri.Scheme + "://" + authority + uri.PathAndQuery;
            var requestFields = new List<KeyValuePair<string, string>> { new("Host", authority) };
            requestFields.AddRange(
                Fields(request).Where(field => !string.Equals(field.Key, "Host", StringComparison.OrdinalIgnoreCase)));
            var requestMessage = new SazMessage(
                startLine[..methodEnd] + " " + target + startLine[versionStart..],
                requestFields,
                request.Body);
            SazMessage responseMessage = null;
            ReplayMessage response = exchange.Response;
            if (response != null && exchange.Outcome is ReplayOutcome.Completed or ReplayOutcome.TooLarge)
            {
                List<KeyValuePair<string, string>> fields = Fields(response);
                bool tooLarge = exchange.Outcome == ReplayOutcome.TooLarge;
                if (tooLarge)
                {
                    fields.Add(new(RequestReplayDraft.ResponseBodyNotKeptHeader, response.DeclaredLength()));
                }

                responseMessage = new SazMessage(
                    response.StartLine, fields, tooLarge ? ReadOnlyMemory<byte>.Empty : response.Body);
            }

            return new SazSession(
                result.Id,
                requestMessage,
                responseMessage,
                AsUtc(exchange.Started),
                exchange.Duration,
                Flags(result, exchange));
        }

        internal static string OutcomeName(ReplayOutcome outcome) => outcome switch
        {
            ReplayOutcome.Completed => "completed",
            ReplayOutcome.TimedOut => "timed-out",
            ReplayOutcome.TooLarge => "too-large",
            _ => "failed",
        };

        private static List<KeyValuePair<string, string>> Fields(ReplayMessage message) =>
            message.Headers.Concat(message.ContentHeaders).ToList();

        private static List<KeyValuePair<string, string>> Flags(ReplayItemResult result, ReplayExchange exchange)
        {
            var flags = new List<KeyValuePair<string, string>>
            {
                new(ClientIpFlag, "0.0.0.0"),
                new(IdFlag, Number(result.Id)),
            };
            if (result.RequestNumber is long number)
            {
                flags.Add(new(RequestFlag, Number(number)));
            }

            if (result.DeviceId != null)
            {
                flags.Add(new(DeviceFlag, result.DeviceId));
            }

            if (result.Sequence is long sequence)
            {
                flags.Add(new(SequenceFlag, Number(sequence)));
            }

            flags.Add(new(OutcomeFlag, OutcomeName(exchange.Outcome)));
            if (exchange.Code != null)
            {
                flags.Add(new(CodeFlag, exchange.Code));
            }

            flags.Add(new(RedirectsFlag, Number(exchange.Redirects)));
            if (result.Transport != ReplayTransportOptions.DirectName)
            {
                flags.Add(new(TransportFlag, result.Transport));
            }

            if (result.ClientCertificateConfigured)
            {
                flags.Add(new(ClientCertificateFlag, "true"));
            }

            foreach (KeyValuePair<string, string> flag in flags)
            {
                if (!IsFlagText(flag.Key) || !IsFlagText(flag.Value))
                {
                    throw new InvalidOperationException("A replay's session flag is outside its alphabet.");
                }
            }

            return flags;
        }

        private static bool IsFlagText(string text) =>
            !string.IsNullOrEmpty(text) && text.Length <= MaxFlagChars &&
            text.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

        private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

        // Replay records its start in UTC; a time of another kind is taken as UTC when unspecified, or converted.
        private static DateTime AsUtc(DateTime time) => time.Kind switch
        {
            DateTimeKind.Utc => time,
            DateTimeKind.Local => time.ToUniversalTime(),
            _ => DateTime.SpecifyKind(time, DateTimeKind.Utc),
        };
    }
}
