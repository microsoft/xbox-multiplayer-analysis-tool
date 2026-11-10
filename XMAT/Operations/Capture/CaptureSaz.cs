// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml;
using XMAT.WebServiceCapture;

namespace XMAT.Operations.Capture
{
    internal enum CaptureSazDisposition
    {
        Session,
        NoRequest,
        Unreadable,
        Oversized,
    }

    internal sealed record CaptureSazProjection(
        CaptureSazDisposition Disposition,
        SazSession Session,
        int BodiesNotKept,
        int BodiesUndecodable,
        long ReplacedHeadCharacters,
        long DroppedHeaderLines,
        long ReformattedHeaderLines,
        int AbsoluteTargets,
        int DiscardedClientAddresses,
        int AdjustedTimes);

    internal sealed class CaptureSazException : Exception
    {
        internal CaptureSazException(string code, string message)
            : base(message)
        {
            Code = code;
        }

        internal string Code { get; }
    }

    internal static class CaptureSaz
    {
        private const string RequestBodyMarker =
            "X-XMAT-Request-Body-Not-Kept";
        private const string ResponseBodyMarker =
            "X-XMAT-Response-Body-Not-Kept";
        private const string ClientAddressFlag =
            "x-clientip";
        private const string RequestBodyOmittedFlag =
            "x-xmat-request-body-omitted";
        private const string ResponseBodyOmittedFlag =
            "x-xmat-response-body-omitted";
        private static readonly char[] ValueTrimCharacters =
        {
            '\t',
            '\n',
            '\v',
            '\f',
            '\r',
            ' ',
        };

        internal static CaptureSazProjection ToSession(
            StoredRow row,
            long sessionId,
            SazExportKind kind,
            SazSanitizer sanitizer,
            SazExportBudgets budgets)
        {
            ArgumentNullException.ThrowIfNull(row);
            ArgumentNullException.ThrowIfNull(budgets);
            if (row.State == StoredRowState.Unreadable)
            {
                return Empty(CaptureSazDisposition.Unreadable);
            }

            if (row.State == StoredRowState.Oversized)
            {
                return Empty(CaptureSazDisposition.Oversized);
            }

            ProjectedHead storedRequest =
                ProjectHead(row.RequestHead);
            if (string.IsNullOrEmpty(storedRequest.StartLine))
            {
                return Empty(CaptureSazDisposition.NoRequest);
            }

            RequestLine requestLine =
                ParseRequestLine(storedRequest.StartLine);
            ProjectedHead storedResponse =
                string.IsNullOrEmpty(row.ResponseHead)
                    ? null
                    : ProjectHead(row.ResponseHead);
            if (storedResponse != null)
            {
                ParseStatusLine(storedResponse.StartLine);
            }

            ProjectedHead requestHead;
            ProjectedHead responseHead;
            ReadOnlyMemory<byte> requestBody;
            ReadOnlyMemory<byte> responseBody;
            bool requestBodyOmitted = false;
            bool responseBodyOmitted = false;
            int bodiesNotKept = 0;
            int bodiesUndecodable = 0;
            if (kind == SazExportKind.Sanitized)
            {
                ArgumentNullException.ThrowIfNull(sanitizer);
                var sanitized = sanitizer.SanitizeSession(
                    row.RequestHead,
                    Sanitizer(row.RequestBody),
                    row.ResponseHead,
                    Sanitizer(row.ResponseBody),
                    requestLine.Method);
                requestHead =
                    ProjectHead(sanitized.Request.Headers);
                requestBody = sanitized.Request.Body;
                requestBodyOmitted =
                    sanitized.Request.BodyOmitted;
                if (sanitized.Response is { } response)
                {
                    responseHead =
                        ProjectHead(response.Headers);
                    responseBody = response.Body;
                    responseBodyOmitted =
                        response.BodyOmitted;
                }
                else
                {
                    responseHead = null;
                    responseBody =
                        ReadOnlyMemory<byte>.Empty;
                }
            }
            else
            {
                requestHead = storedRequest;
                responseHead = storedResponse;
                requestBody = PlainBody(
                    row.RequestBody,
                    requestHead.Fields,
                    RequestBodyMarker,
                    ref bodiesNotKept,
                    ref bodiesUndecodable);
                responseBody = responseHead == null
                    ? ReadOnlyMemory<byte>.Empty
                    : PlainBody(
                        row.ResponseBody,
                        responseHead.Fields,
                        ResponseBodyMarker,
                        ref bodiesNotKept,
                        ref bodiesUndecodable);
            }

            requestLine =
                ParseRequestLine(requestHead.StartLine);
            if (responseHead != null)
            {
                ParseStatusLine(responseHead.StartLine);
            }

            int absoluteTargets = PromoteTarget(
                requestHead,
                requestLine,
                row.Scheme);
            if (!Fits(requestHead, budgets) ||
                (responseHead != null &&
                 !Fits(responseHead, budgets)))
            {
                return new(
                    CaptureSazDisposition.Oversized,
                    null,
                    bodiesNotKept,
                    bodiesUndecodable,
                    requestHead.ReplacedCharacters +
                        (responseHead?.ReplacedCharacters ?? 0),
                    requestHead.DroppedLines +
                        (responseHead?.DroppedLines ?? 0),
                    requestHead.ReformattedLines +
                        (responseHead?.ReformattedLines ?? 0),
                    absoluteTargets,
                    0,
                    0);
            }

            var flags =
                new List<KeyValuePair<string, string>>();
            int discardedClientAddresses = 0;
            if (kind == SazExportKind.Sanitized)
            {
                flags.Add(
                    new(
                        ClientAddressFlag,
                        "0.0.0.0"));
                if (row.ClientAddressUnavailable ||
                    (!string.IsNullOrEmpty(
                        row.ClientAddress) &&
                     row.ClientAddress != "0.0.0.0"))
                {
                    sanitizer.Summary.RedactedValues++;
                }

                if (requestBodyOmitted)
                {
                    flags.Add(
                        new(
                            RequestBodyOmittedFlag,
                            "uninspectable"));
                }

                if (responseBodyOmitted)
                {
                    flags.Add(
                        new(
                            ResponseBodyOmittedFlag,
                            "uninspectable"));
                }
            }
            else if (CanWriteClientAddress(
                row.ClientAddress,
                row.ClientAddressUnavailable))
            {
                flags.Add(
                    new(
                        ClientAddressFlag,
                        row.ClientAddress ?? string.Empty));
            }
            else
            {
                discardedClientAddresses++;
            }

            (DateTime? started,
                TimeSpan duration,
                int adjustedTimes) =
                ProjectTimes(row);
            var request = new SazMessage(
                requestHead.StartLine,
                requestHead.Fields,
                requestBody);
            SazMessage responseMessage =
                responseHead == null
                    ? null
                    : new SazMessage(
                        responseHead.StartLine,
                        responseHead.Fields,
                        responseBody);
            return new(
                CaptureSazDisposition.Session,
                new SazSession(
                    sessionId,
                    request,
                    responseMessage,
                    started,
                    duration,
                    flags),
                bodiesNotKept,
                bodiesUndecodable,
                requestHead.ReplacedCharacters +
                    (responseHead?.ReplacedCharacters ?? 0),
                requestHead.DroppedLines +
                    (responseHead?.DroppedLines ?? 0),
                requestHead.ReformattedLines +
                    (responseHead?.ReformattedLines ?? 0),
                absoluteTargets,
                discardedClientAddresses,
                adjustedTimes);
        }

        private static CaptureSazProjection Empty(
            CaptureSazDisposition disposition) =>
            new(
                disposition,
                null,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0);

        private static SanitizerBody Sanitizer(
            StoredBody body) =>
            body.State switch
            {
                StoredBodyState.Empty =>
                    SanitizerBody.Empty,
                StoredBodyState.Bytes =>
                    SanitizerBody.Of(body.Bytes),
                _ => SanitizerBody.Uninspected,
            };

        private static ReadOnlyMemory<byte> PlainBody(
            StoredBody body,
            List<KeyValuePair<string, string>> fields,
            string marker,
            ref int bodiesNotKept,
            ref int bodiesUndecodable)
        {
            if (body.State == StoredBodyState.Bytes)
            {
                return body.Bytes;
            }

            if (body.State == StoredBodyState.NotRead)
            {
                bodiesNotKept++;
                if (body.Undecodable)
                {
                    bodiesUndecodable++;
                }

                fields.Add(
                    new(
                        marker,
                        body.KnownLength?.ToString(
                            CultureInfo.InvariantCulture) ??
                        "unknown"));
            }

            return ReadOnlyMemory<byte>.Empty;
        }

        private static ProjectedHead ProjectHead(
            string raw)
        {
            string replaced = ReplaceCharacters(
                raw ?? string.Empty,
                out long replacedCharacters);
            var fields =
                new List<KeyValuePair<string, string>>();
            long dropped = 0;
            long reformatted = 0;
            string startLine = string.Empty;
            int offset = 0;
            int lineNumber = 0;
            bool ended = false;
            while (offset <= replaced.Length)
            {
                int newline =
                    replaced.IndexOf(
                        '\n',
                        offset);
                bool hasNewline = newline >= 0;
                int end = hasNewline
                    ? newline
                    : replaced.Length;
                bool carriageReturn =
                    end > offset &&
                    replaced[end - 1] == '\r';
                string line = replaced.Substring(
                    offset,
                    end -
                        offset -
                        (carriageReturn ? 1 : 0));
                bool bareLf =
                    hasNewline &&
                    !carriageReturn;
                offset = hasNewline
                    ? newline + 1
                    : replaced.Length + 1;
                if (lineNumber++ == 0)
                {
                    startLine = line;
                }
                else if (ended)
                {
                    if (line.Length != 0)
                    {
                        dropped++;
                    }
                }
                else if (line.Length == 0)
                {
                    ended = true;
                }
                else if (TryProjectField(
                    line,
                    out KeyValuePair<string, string> field))
                {
                    fields.Add(field);
                    string written =
                        field.Key +
                        ": " +
                        field.Value;
                    if (bareLf ||
                        !string.Equals(
                            line,
                            written,
                            StringComparison.Ordinal))
                    {
                        reformatted++;
                    }
                }
                else
                {
                    dropped++;
                }

                if (!hasNewline)
                {
                    break;
                }
            }

            return new(
                startLine,
                fields,
                replacedCharacters,
                dropped,
                reformatted);
        }

        private static string ReplaceCharacters(
            string text,
            out long replaced)
        {
            replaced = 0;
            StringBuilder output = null;
            for (int index = 0;
                index < text.Length;
                index++)
            {
                char character = text[index];
                if (character <= '\u00ff')
                {
                    output?.Append(character);
                    continue;
                }

                output ??= new StringBuilder(
                    text.Length)
                    .Append(
                        text,
                        0,
                        index);
                output.Append('?');
                replaced++;
                if (char.IsHighSurrogate(character) &&
                    index + 1 < text.Length &&
                    char.IsLowSurrogate(
                        text[index + 1]))
                {
                    index++;
                }
            }

            return output?.ToString() ??
                text;
        }

        private static bool TryProjectField(
            string line,
            out KeyValuePair<string, string> field)
        {
            field = default;
            int colon = line.IndexOf(':');
            if (colon < 0)
            {
                return false;
            }

            for (int index = 0;
                index < line.Length;
                index++)
            {
                char value = line[index];
                if (value == 127 ||
                    (value < ' ' &&
                     value != '\t'))
                {
                    return false;
                }
            }

            for (int index = 0;
                index < colon;
                index++)
            {
                if (!IsHeaderNameCharacter(
                    line[index]))
                {
                    return false;
                }
            }

            field = new(
                line[..colon],
                line[(colon + 1)..].Trim(
                    ValueTrimCharacters));
            return true;
        }

        private static bool IsHeaderNameCharacter(
            char value) =>
            value > ' ' &&
            value < 127 &&
            value is not (
                '(' or ')' or '<' or '>' or
                '@' or ',' or ';' or ':' or
                '\\' or '"' or '/' or '[' or
                ']' or '?' or '=' or '{' or '}');

        private static RequestLine ParseRequestLine(
            string line)
        {
            int methodEnd =
                line.IndexOf(' ');
            int versionStart =
                line.LastIndexOf(' ');
            if (methodEnd <= 0 ||
                versionStart <= methodEnd + 1 ||
                line.IndexOf(
                    ' ',
                    methodEnd + 1,
                    versionStart -
                        methodEnd -
                        1) >= 0)
            {
                throw InvalidStartLine();
            }

            string method = line[..methodEnd];
            string target = line[
                (methodEnd + 1)..versionStart];
            string version = line[
                (versionStart + 1)..];
            if (!method.All(IsMethodCharacter) ||
                !target.All(IsTargetCharacter) ||
                !IsHttpVersion(version))
            {
                throw InvalidStartLine();
            }

            return new(
                method,
                target,
                version);
        }

        private static void ParseStatusLine(
            string line)
        {
            int versionEnd =
                line.IndexOf(' ');
            if (versionEnd <= 0 ||
                !IsHttpVersion(
                    line[..versionEnd]))
            {
                throw InvalidStartLine();
            }

            ReadOnlySpan<char> rest =
                line.AsSpan(versionEnd + 1);
            if (rest.Length < 3 ||
                !char.IsAsciiDigit(rest[0]) ||
                !char.IsAsciiDigit(rest[1]) ||
                !char.IsAsciiDigit(rest[2]) ||
                rest[0] == '0' ||
                (rest.Length > 3 &&
                 rest[3] != ' '))
            {
                throw InvalidStartLine();
            }

            ReadOnlySpan<char> reason =
                rest.Length > 3
                    ? rest[4..]
                    : ReadOnlySpan<char>.Empty;
            foreach (char value in reason)
            {
                if (value is not ('\t' or ' ') &&
                    (value < '!' ||
                     (value > '~' &&
                      value < '\u0080')))
                {
                    throw InvalidStartLine();
                }
            }
        }

        private static bool IsHttpVersion(
            string value) =>
            value.Length is 6 or 8 &&
            value.StartsWith(
                "HTTP/",
                StringComparison.Ordinal) &&
            char.IsAsciiDigit(value[5]) &&
            (value.Length == 6 ||
             (value[6] == '.' &&
              char.IsAsciiDigit(value[7])));

        private static bool IsMethodCharacter(
            char value) =>
            char.IsAsciiLetterOrDigit(value) ||
            value is
                '!' or '#' or '$' or '%' or
                '&' or '\'' or '*' or '+' or
                '-' or '.' or '^' or '_' or
                '`' or '|' or '~';

        private static bool IsTargetCharacter(
            char value) =>
            value is >= '\u0021' and <= '\u007e' ||
            value is >= '\u0080' and <= '\u00ff';

        private static CaptureSazException InvalidStartLine() =>
            new(
                SazExportCodes.InvalidStartLine,
                "An HTTP start line is invalid.");

        private static int PromoteTarget(
            ProjectedHead head,
            RequestLine line,
            string scheme)
        {
            if (line.Method == "CONNECT" ||
                line.Target.Length == 0 ||
                line.Target[0] != '/' ||
                !(string.Equals(
                    scheme,
                    "http",
                    StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(
                    scheme,
                    "https",
                    StringComparison.OrdinalIgnoreCase)))
            {
                return 0;
            }

            KeyValuePair<string, string>[] hosts =
                head.Fields.Where(
                    static field =>
                        string.Equals(
                            field.Key,
                            "Host",
                            StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            if (hosts.Length != 1 ||
                !HostAuthority.TryParse(
                    hosts[0].Value,
                    out string host,
                    out _) ||
                !HostAuthority.TryNormalizeHost(
                    host,
                    out _))
            {
                return 0;
            }

            string candidate =
                scheme.ToLowerInvariant() +
                "://" +
                hosts[0].Value +
                line.Target;
            if (!SazRequestTarget.TryReadAbsoluteTarget(
                line.Method,
                candidate,
                out _))
            {
                return 0;
            }

            head.StartLine =
                line.Method +
                " " +
                candidate +
                " " +
                line.Version;
            return 1;
        }

        private static bool Fits(
            ProjectedHead head,
            SazExportBudgets budgets)
        {
            if (head.Fields.Count >
                budgets.MaxHeadFieldLines)
            {
                return false;
            }

            long bytes =
                head.StartLine.Length +
                2 +
                2;
            foreach (KeyValuePair<string, string> field in
                head.Fields)
            {
                bytes = checked(
                    bytes +
                    field.Key.Length +
                    2 +
                    field.Value.Length +
                    2);
                if (bytes >
                    budgets.MaxHeadBytes)
                {
                    return false;
                }
            }

            return bytes <=
                budgets.MaxHeadBytes;
        }

        private static bool CanWriteClientAddress(
            string value,
            bool unavailable)
        {
            if (unavailable ||
                (value?.Length ?? 0) >
                    SazArchiveWriter.MaxFlagValueChars)
            {
                return false;
            }

            try
            {
                XmlConvert.VerifyXmlChars(
                    value ?? string.Empty);
                return true;
            }
            catch (XmlException)
            {
                return false;
            }
        }

        private static (
            DateTime? Started,
            TimeSpan Duration,
            int Adjusted) ProjectTimes(
            StoredRow row)
        {
            int adjusted = 0;
            DateTime? start = ReadInstant(
                row.RequestTimestamp,
                row.RequestTimestampUnavailable,
                ref adjusted);
            DateTime? end = ReadInstant(
                row.ResponseTimestamp,
                row.ResponseTimestampUnavailable,
                ref adjusted);
            if (!start.HasValue)
            {
                if (end.HasValue)
                {
                    adjusted++;
                }

                return (
                    null,
                    TimeSpan.Zero,
                    adjusted);
            }

            if (!end.HasValue)
            {
                return (
                    start,
                    TimeSpan.Zero,
                    adjusted);
            }

            TimeSpan duration;
            try
            {
                duration = end.Value -
                    start.Value;
                if (duration < TimeSpan.Zero ||
                    start.Value >
                        DateTime.MaxValue -
                        duration)
                {
                    adjusted++;
                    duration = TimeSpan.Zero;
                }
            }
            catch (ArgumentOutOfRangeException)
            {
                adjusted++;
                duration = TimeSpan.Zero;
            }

            return (
                start,
                duration,
                adjusted);
        }

        private static DateTime? ReadInstant(
            string text,
            bool unavailable,
            ref int adjusted)
        {
            if (unavailable)
            {
                adjusted++;
                return null;
            }

            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            try
            {
                DateTime value =
                    DateTime.Parse(text);
                if (value.Kind ==
                        DateTimeKind.Unspecified &&
                    value == DateTime.MinValue)
                {
                    return null;
                }

                if (value.Kind == DateTimeKind.Utc)
                {
                    return value;
                }

                return TimeZoneInfo.ConvertTimeToUtc(
                    DateTime.SpecifyKind(
                        value,
                        DateTimeKind.Local));
            }
            catch (Exception exception) when (
                exception is FormatException or
                    ArgumentException)
            {
                adjusted++;
                return null;
            }
        }

        private sealed class ProjectedHead
        {
            internal ProjectedHead(
                string startLine,
                List<KeyValuePair<string, string>> fields,
                long replacedCharacters,
                long droppedLines,
                long reformattedLines)
            {
                StartLine = startLine;
                Fields = fields;
                ReplacedCharacters = replacedCharacters;
                DroppedLines = droppedLines;
                ReformattedLines = reformattedLines;
            }

            internal string StartLine { get; set; }

            internal List<KeyValuePair<string, string>> Fields { get; }

            internal long ReplacedCharacters { get; }

            internal long DroppedLines { get; }

            internal long ReformattedLines { get; }
        }

        private sealed record RequestLine(
            string Method,
            string Target,
            string Version);
    }
}
