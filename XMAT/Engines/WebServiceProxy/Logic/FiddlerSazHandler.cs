// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using XMAT.Operations.Processes;
using XMAT.WebServiceCapture.Models;

namespace XMAT.WebServiceCapture
{
    internal sealed record FiddlerSazImportResult(
        ProxyConnectionsCollection Records,
        int SkippedIncompleteFrames,
        int SkippedMalformedHeaderLines);

    internal sealed class FiddlerSazHeaderLimitException : Exception
    {
        internal FiddlerSazHeaderLimitException(Exception innerException = null)
            : base(
                "The SAZ message headers exceed a supported safety limit.",
                innerException)
        {
        }
    }

    public static class FiddlerSazHandler
    {
        // Per request or response: header lines exclude the start and empty/whitespace separator lines; raw bytes
        // include the start line, every header line and their CRLFs, and the separator line. Malformed lines count.
        // These limits cover tested sanitizer cases, not every possible writer output.
        internal const int MaxHeaderLinesPerMessage = 16_384;
        internal const int MaxRawHeaderBytesPerMessage = 16 * 1024 * 1024;
        private const int EntryReadBufferBytes = 64 * 1024;

        // Fiddler's process flag ("name:pid") and XMAT's flags for the process start time and attribution state.
        internal const string ProcessInfoFlag = "x-ProcessInfo";
        internal const string ProcessStartTimeFlag = "x-xmat-clientprocess-starttime";
        internal const string ProcessStatusFlag = "x-xmat-clientprocess-status";

        enum EntryType
        {
            Request,
            Response,
            Meta
        }

        private static readonly Dictionary<EntryType, string> EntryTypeMap = new()
        {
            {EntryType.Request,   "_c.txt" },
            {EntryType.Response,  "_s.txt" },
            {EntryType.Meta,      "_m.xml" },
        };

        public static int GetFrameNumber(string filename)
        {
            string number = filename.Substring(0, filename.IndexOf('_'));
            return int.Parse(number);
        }

        public static ProxyConnectionModel FromFiddlerFrame(UInt32 frameId, ZipArchiveEntry cFileStream, ZipArchiveEntry mFileStream, ZipArchiveEntry sFileStream)
        {
            return FromFiddlerFrame(
                frameId,
                cFileStream,
                mFileStream,
                sFileStream,
                out _);
        }

        private static ProxyConnectionModel FromFiddlerFrame(
            UInt32 frameId,
            ZipArchiveEntry cFileStream,
            ZipArchiveEntry mFileStream,
            ZipArchiveEntry sFileStream,
            out int skippedMalformedHeaderLines)
        {
            // Requests and responses are parsed as they decompress, so the header limits stop an entry before the rest
            // of it is read; the small metadata entry is read whole.
            using Stream request = OpenEntryForParsing(cFileStream);
            using var metadata = mFileStream == null
                ? null
                : ZipUtilities.DecompressEntryToMemory(mFileStream);
            using Stream response = OpenEntryForParsing(sFileStream);

            return FromFiddlerFrame(
                frameId,
                request,
                metadata,
                response,
                out skippedMalformedHeaderLines);
        }

        private static Stream OpenEntryForParsing(ZipArchiveEntry entry)
        {
            return entry == null
                ? null
                : new LookaheadReadStream(
                    new BufferedStream(
                        entry.Open(),
                        EntryReadBufferBytes));
        }

        internal static ProxyConnectionModel FromFiddlerFrame(
            UInt32 frameId,
            Stream requestStream,
            Stream metadataStream,
            Stream responseStream)
        {
            return FromFiddlerFrame(
                frameId,
                requestStream,
                metadataStream,
                responseStream,
                out _);
        }

        internal static ProxyConnectionModel FromFiddlerFrame(
            UInt32 frameId,
            Stream requestStream,
            Stream metadataStream,
            Stream responseStream,
            out int skippedMalformedHeaderLines)
        {
            skippedMalformedHeaderLines = 0;
            string status = string.Empty;
            DateTime requestTime = DateTime.MinValue;
            TimeSpan duration = TimeSpan.FromMilliseconds(0);
            string scheme = string.Empty;
            string hostAndPort = string.Empty;
            string targetAuthority = null;
            string method = string.Empty;
            string call = string.Empty;
            string requestHeaders = string.Empty;
            byte[] requestBody = null;
            string responseHeaders = string.Empty;
            byte[] responseBody = null;
            string clientIP = @"0.0.0.0";

            // REQUEST NUMBER & ID
            Int64 id = Convert.ToInt64(frameId);

            // Read the client part of the frame (###_c.txt)
            if (requestStream != null)
            {
                RewindIfSeekable(requestStream);
                using (var cFile = new BinaryReader(requestStream, Encoding.ASCII, true))
                {
                    var requestHeaderBytes = 0;
                    var firstLine = ReadHeaderLine(
                        cFile,
                        ref requestHeaderBytes);

                    if (!string.IsNullOrEmpty(firstLine))
                    {
                        if (firstLine.IndexOf(' ') < 0)
                        {
                            return null;
                        }
                        var firstLineSplit = firstLine.Split(' ');

                        // METHOD
                        method = firstLineSplit[0];

                        // CALL & SCHEME: an absolute http, https or ftp URL gives the scheme, the path and query, and the
                        // authority, which takes precedence over the Host header (RFC 9112 3.2.2). Any other target, such as a
                        // path, *, a CONNECT's authority, or what Uri reads as a file or UNC path, is kept as written, with an
                        // unknown scheme rather than a guess.
                        call = firstLineSplit[1];
                        if (SazRequestTarget.TryReadAbsoluteTarget(
                                method,
                                call,
                                out SazAbsoluteTarget target))
                        {
                            call = target.Uri.PathAndQuery;
                            scheme = target.Uri.Scheme;
                            targetAuthority = target.Authority;
                        }
                    }

                    SazHeaderFields reqHeaders = ReadHeaders(
                        cFile,
                        ref requestHeaderBytes,
                        ref skippedMalformedHeaderLines);

                    hostAndPort = targetAuthority ?? reqHeaders["Host"];

                    // REQUEST BODY
                    requestBody = cFile.ReadToEnd();

                    // REQUEST HEADERS
                    requestHeaders = $"{firstLine}\r\n{reqHeaders}";
                }
            }

            // Read the server part of the frame(###_s.txt)
            if (responseStream != null)
            {
                RewindIfSeekable(responseStream);
                using (var sFile = new BinaryReader(responseStream, Encoding.ASCII, true))
                {
                    var responseHeaderBytes = 0;
                    var firstLine = ReadHeaderLine(
                        sFile,
                        ref responseHeaderBytes);

                    if (!string.IsNullOrEmpty(firstLine))
                    {
                        if (firstLine.IndexOf(' ') < 0)
                        {
                            // A skipped frame contributes no malformed-header count.
                            skippedMalformedHeaderLines = 0;
                            return null;
                        }
                        var firstLineSplit = firstLine.Split(' ');

                        // STATUS
                        status = firstLineSplit[1];
                    }

                    SazHeaderFields headers = ReadHeaders(
                        sFile,
                        ref responseHeaderBytes,
                        ref skippedMalformedHeaderLines);

                    // RESPONSE BODY
                    responseBody = sFile.ReadToEnd();

                    // RESPONSE HEADERS
                    responseHeaders = $"{firstLine}\r\n{headers}";
                }
            }

            // Read the frame metadata (###_m.xml)
            ClientProcessInfo clientProcess = ClientProcessInfo.NotRecorded;
            if (metadataStream != null)
            {
                RewindIfSeekable(metadataStream);
                using (var mFile = new StreamReader(
                    metadataStream,
                    Encoding.UTF8,
                    true,
                    1024,
                    true))
                {
                    string rawData = mFile.ReadToEnd();
                    XDocument xmldata = XDocument.Parse(rawData);

                    XElement sessionTimers = xmldata.Element("Session").Element("SessionTimers");

                    // Each timer is an instant: one with Z or an offset is exact, and one without, as XMAT's own export
                    // writes for a time of unspecified kind, is this machine's local time. The duration is the time between
                    // the two instants, whatever their offsets, and the request time is the first one in local time. A
                    // timer that was never set, or that names no instant this machine can show as a local time (ParseTimer),
                    // is no time: the request time stays unknown, and the duration zero.
                    DateTimeOffset? begin = ParseTimer((string)sessionTimers.Attribute("ClientBeginRequest"));
                    DateTimeOffset? done = ParseTimer((string)sessionTimers.Attribute("ClientDoneResponse"));
                    if (begin != null)
                    {
                        requestTime = begin.Value.LocalDateTime;
                    }

                    if (begin != null && done != null)
                    {
                        duration = done.Value.UtcDateTime - begin.Value.UtcDateTime;
                    }

                    var sessionFlags = xmldata.Element("Session").Element("SessionFlags");

                    var clientIPRead = false;
                    string processInfo = null;
                    string processStartTime = null;
                    string processStatus = null;
                    foreach (var flag in sessionFlags.Descendants())
                    {
                        var flagName = (string)flag.Attribute("N");
                        if (flagName == "x-clientip")
                        {
                            if (!clientIPRead)
                            {
                                // CLIENT IP, as written, except that an IPv4 address written in its IPv6 form
                                // (::ffff:a.b.c.d) is kept as IPv4.
                                clientIP = (string)flag.Attribute("V") ??
                                    throw new FormatException("x-clientip has no value");
                                if (IPAddress.TryParse(clientIP, out IPAddress address) && address.IsIPv4MappedToIPv6)
                                {
                                    clientIP = address.MapToIPv4().ToString();
                                }
                                clientIPRead = true;
                            }
                        }
                        else if (string.Equals(flagName, ProcessInfoFlag, StringComparison.OrdinalIgnoreCase))
                        {
                            processInfo ??= (string)flag.Attribute("V") ?? string.Empty;
                        }
                        else if (string.Equals(flagName, ProcessStartTimeFlag, StringComparison.OrdinalIgnoreCase))
                        {
                            processStartTime ??= (string)flag.Attribute("V");
                        }
                        else if (string.Equals(flagName, ProcessStatusFlag, StringComparison.OrdinalIgnoreCase))
                        {
                            processStatus ??= (string)flag.Attribute("V");
                        }
                    }

                    clientProcess = ReadClientProcess(processInfo, processStartTime, processStatus);
                }
            }

            ProxyConnectionModel model = new(
                id,
                status,
                requestTime,
                duration,
                scheme,
                hostAndPort,
                method,
                call,
                requestHeaders,
                requestBody,
                responseHeaders,
                responseBody,
                clientIP);
            model.SetClientProcess(clientProcess);

            return model;
        }

        // Fiddler's x-ProcessInfo holds "name:pid". A last ':' starts the id only when a positive number follows it, so a
        // drive path without an id stays a name. The shared codec keeps the name's spelling but only its image name,
        // and anything else leaves the session without process information. A file never shows that a process ran on
        // this PC, so every identity it holds is imported.
        private static ClientProcessInfo ReadClientProcess(string processInfo, string startTime, string status)
        {
            if (string.IsNullOrEmpty(processInfo))
            {
                return status?.Trim() switch
                {
                    "remote" => new ClientProcessInfo(string.Empty, 0, null, ClientProcessStatus.Remote),
                    "unresolved" => new ClientProcessInfo(string.Empty, 0, null, ClientProcessStatus.Unresolved),
                    _ => ClientProcessInfo.NotRecorded,
                };
            }

            string name = processInfo;
            long processId = 0;
            int separator = processInfo.LastIndexOf(':');
            if (separator >= 0 && TryParseProcessId(processInfo.AsSpan(separator + 1), out long suffix))
            {
                name = processInfo.Substring(0, separator);
                processId = suffix;
            }

            // A start time that cannot be read is dropped; the identity stays.
            string start = DateTimeOffset.TryParse(
                startTime,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out DateTimeOffset started)
                ? started.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)
                : string.Empty;
            return ClientProcessRecordCodec.FromValues(
                name,
                processId,
                start,
                ClientProcessRecordCodec.StatusToken(ClientProcessStatus.Imported));
        }

        private static bool TryParseProcessId(ReadOnlySpan<char> text, out long processId)
        {
            processId = 0;
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value <= 0)
            {
                return false;
            }

            processId = value;
            return true;
        }

        public static ProxyConnectionsCollection ImportSazFile(
            string filename,
            CancellationToken cancelToken)
        {
            return ImportSazFileWithDiagnostics(
                filename,
                cancelToken).Records;
        }

        internal static FiddlerSazImportResult ImportSazFileWithDiagnostics(
            string filename,
            CancellationToken cancelToken)
        {
            var list = new ProxyConnectionsCollection();
            var skippedIncompleteFrames = 0;
            var skippedMalformedHeaderLines = 0;

            // Open the SAZ, checking its directory before ZipArchive reads it
            using (var input = new FileStream(
                filename,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                SazArchivePreflight.Validate(
                    input,
                    cancelToken);
                input.Seek(0, SeekOrigin.Begin);
                using var archive = new ZipArchive(
                    input,
                    ZipArchiveMode.Read,
                    true);

                // Group the archive entries by frame number
                var result = from e in archive.Entries
                             where e.Name.Contains("_c.txt") || e.Name.Contains("_s.txt") || e.Name.Contains("_m.xml")
                             group e by GetFrameNumber(e.Name) into g
                             select new { Frame = g.Key, Files = g };
                var totalCalls = result.Count();

                // Process data per frame
                foreach (var group in result)
                {
                    // abort if a cancellation was requested by the caller
                    cancelToken.ThrowIfCancellationRequested();

                    ZipArchiveEntry requestFileArchive = null;
                    ZipArchiveEntry responseFileArchive = null;
                    ZipArchiveEntry metaFileArchive = null;

                    // Grab the individual files
                    foreach (ZipArchiveEntry file in group.Files)
                    {
                        if (file.Name.EndsWith(EntryTypeMap[EntryType.Request]))
                        {
                            requestFileArchive = file;
                        }
                        else if (file.Name.EndsWith(EntryTypeMap[EntryType.Response]))
                        {
                            responseFileArchive = file;
                        }
                        else if (file.Name.EndsWith(EntryTypeMap[EntryType.Meta]))
                        {
                            metaFileArchive = file;
                        }
                    }

                    var proxyConnectionModel = FromFiddlerFrame(
                        (UInt32)group.Frame,
                        requestFileArchive,
                        metaFileArchive,
                        responseFileArchive,
                        out var skippedInFrame);
                    skippedMalformedHeaderLines += skippedInFrame;

                    // skip if the frame could not be converted
                    if (proxyConnectionModel == null)
                    {
                        skippedIncompleteFrames++;
                        continue;
                    }

                    list.Add(proxyConnectionModel);
                }
            }

            return new FiddlerSazImportResult(
                list,
                skippedIncompleteFrames,
                skippedMalformedHeaderLines);
        }

        private static SazHeaderFields ReadHeaders(
            BinaryReader reader,
            ref int rawHeaderBytes,
            ref int skippedMalformedHeaderLines)
        {
            var headers = new SazHeaderFields();
            var headerLines = 0;
            while (true)
            {
                if (headerLines == MaxHeaderLinesPerMessage)
                {
                    ConsumeHeaderSeparator(
                        reader,
                        ref rawHeaderBytes);
                    return headers;
                }

                string line = ReadHeaderLine(
                    reader,
                    ref rawHeaderBytes);
                if (IsBlankHeadLine(line))
                {
                    return headers;
                }

                headerLines++;
                AddHeaderOrSkip(
                    headers,
                    line,
                    ref skippedMalformedHeaderLines);
            }
        }

        // Head lines are decoded as Latin-1, which keeps every byte: a byte from 0x80 to 0xFF is that character, never '?'.
        private static string ReadHeaderLine(
            BinaryReader reader,
            ref int rawHeaderBytes)
        {
            try
            {
                string line = reader.ReadLine(
                    MaxRawHeaderBytesPerMessage - rawHeaderBytes,
                    Encoding.Latin1,
                    out int bytesRead);
                rawHeaderBytes += bytesRead;
                return line;
            }
            catch (BinaryReadLimitException ex)
            {
                throw new FiddlerSazHeaderLimitException(ex);
            }
        }

        // Once the line count is full, consume only an ASCII-whitespace separator without retaining a 16,385th line.
        private static void ConsumeHeaderSeparator(
            BinaryReader reader,
            ref int rawHeaderBytes)
        {
            var previousWasCarriageReturn = false;
            while (true)
            {
                if (rawHeaderBytes == MaxRawHeaderBytesPerMessage &&
                    reader.BaseStream.HasMoreBytes())
                {
                    throw new FiddlerSazHeaderLimitException();
                }

                int value = reader.BaseStream.ReadByte();
                if (value < 0)
                {
                    return;
                }

                rawHeaderBytes++;
                byte current = (byte)value;
                if (!IsAsciiWhitespace(current))
                {
                    throw new FiddlerSazHeaderLimitException();
                }
                if (current == '\n' &&
                    previousWasCarriageReturn &&
                    reader.BaseStream.HasMoreBytes())
                {
                    return;
                }

                previousWasCarriageReturn = current == '\r';
            }
        }

        private static void RewindIfSeekable(Stream stream)
        {
            if (stream.CanSeek)
            {
                stream.Seek(0, SeekOrigin.Begin);
            }
        }

        private static bool IsAsciiWhitespace(byte value) =>
            value == (byte)'\t' ||
            value == (byte)'\n' ||
            value == (byte)'\v' ||
            value == (byte)'\f' ||
            value == (byte)'\r' ||
            value == (byte)' ';

        // The blank line that ends a head: empty, or only the ASCII whitespace that string.IsNullOrWhiteSpace saw when heads
        // were read as ASCII. That method would also end a head at a line of 0x85 or 0xA0, which Latin-1 reads as U+0085 and
        // U+00A0, and make the headers after that line part of the body.
        private static bool IsBlankHeadLine(string line)
        {
            foreach (char value in line)
            {
                if (value is not ('\t' or '\n' or '\v' or '\f' or '\r' or ' '))
                {
                    return false;
                }
            }

            return true;
        }

        private static void AddHeaderOrSkip(
            SazHeaderFields headers,
            string line,
            ref int skippedMalformedHeaderLines)
        {
            if (!IsValidHeaderLine(line))
            {
                skippedMalformedHeaderLines++;
                return;
            }

            headers.Add(line);
        }

        // WebHeaderCollection.Add(string)'s rules, checked without exceptions, with one difference: a name may have an
        // apostrophe, which HTTP allows (RFC 9110 tokens) and replay sends, so a request that XMAT sent reads back whole.
        internal static bool IsValidHeaderLine(string line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return false;
            }

            int colon = line.IndexOf(':');
            if (colon < 0)
            {
                return false;
            }

            for (var index = 0; index < colon; index++)
            {
                if (!IsValidHeaderNameCharacter(line[index]))
                {
                    return false;
                }
            }

            int valueStart = colon + 1;
            int valueEnd = line.Length - 1;
            while (valueStart <= valueEnd &&
                IsHeaderValueTrimCharacter(line[valueStart]))
            {
                valueStart++;
            }
            while (valueEnd >= valueStart &&
                IsHeaderValueTrimCharacter(line[valueEnd]))
            {
                valueEnd--;
            }

            var state = 0;
            for (int index = valueStart; index <= valueEnd; index++)
            {
                char value = (char)(line[index] & 0xff);
                switch (state)
                {
                    case 0:
                        if (value == '\r')
                        {
                            state = 1;
                        }
                        else if (value == '\n')
                        {
                            state = 2;
                        }
                        else if (value == 127 ||
                            (value < ' ' && value != '\t'))
                        {
                            return false;
                        }
                        break;

                    case 1:
                        if (value != '\n')
                        {
                            return false;
                        }
                        state = 2;
                        break;

                    case 2:
                        if (value != ' ' &&
                            value != '\t')
                        {
                            return false;
                        }
                        state = 0;
                        break;
                }
            }

            return state == 0;
        }

        private static bool IsValidHeaderNameCharacter(char value)
        {
            return value > ' ' &&
                value < 127 &&
                value is not (
                    '(' or ')' or '<' or '>' or '@' or ',' or ';' or
                    ':' or '\\' or '"' or '/' or '[' or ']' or
                    '?' or '=' or '{' or '}');
        }

        // A timer as an instant, or null for one that names no time this machine can keep. A time without an offset that reads
        // as DateTime.MinValue is the unknown time: Fiddler writes it for a timer that was never set, and XMAT's export for a
        // time it does not know, so it reads back as unknown in any time zone. A time that names no instant is no time either:
        // one whose UTC falls outside years 1 to 9999. DateTime reads such a time only in the time zones whose offset brings it
        // back within those years, so it is found from its clock and its offset alone, and every zone reads it alike. So is an
        // instant that this machine cannot show as a local time: the model keeps local times, and DateTime would move such a
        // time to its first or last value, as it does the first hours of the year 1 west of UTC, so the request time and the
        // end would no longer name the instants. Anything else that is not a time throws, as DateTime.Parse did.
        private static DateTimeOffset? ParseTimer(string value)
        {
            bool isTime = DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime clock);
            if (isTime && clock.Kind == DateTimeKind.Unspecified && clock == DateTime.MinValue)
            {
                return null;
            }

            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTimeOffset instant))
            {
                return instant.LocalDateTime.ToUniversalTime() == instant.UtcDateTime ? instant : null;
            }

            if (isTime || NamesNoInstant(value))
            {
                return null;
            }

            // Throws the exception that the value is not a time, or ArgumentNullException without one.
            return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal);
        }

        // Whether the value is a clock and an offset whose UTC falls outside years 1 to 9999. The offset is the hours, and the
        // minutes if any, after the value's last sign, at most 14 hours as DateTimeOffset allows; the clock is the time before
        // it, which must have no offset of its own. Text that is no time in any zone is neither, so it still throws.
        private static bool NamesNoInstant(string value)
        {
            ReadOnlySpan<char> text = value.AsSpan().Trim();
            int sign = text.LastIndexOfAny('+', '-');
            if (sign <= 0 ||
                !TryReadOffset(text[(sign + 1)..], out TimeSpan offset) ||
                !DateTime.TryParse(text[..sign], CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime clock) ||
                clock.Kind != DateTimeKind.Unspecified)
            {
                return false;
            }

            long utc = text[sign] == '-' ? clock.Ticks + offset.Ticks : clock.Ticks - offset.Ticks;
            return utc < DateTime.MinValue.Ticks || utc > DateTime.MaxValue.Ticks;
        }

        // An offset's hours as hh, with minutes as hhmm or hh:mm.
        private static bool TryReadOffset(ReadOnlySpan<char> text, out TimeSpan offset)
        {
            offset = TimeSpan.Zero;
            if (text.Length is not (2 or 4 or 5) || (text.Length == 5 && text[2] != ':'))
            {
                return false;
            }

            ReadOnlySpan<char> minutesText = text.Length == 2 ? "00" : text[^2..];
            if (!int.TryParse(text[..2], NumberStyles.None, CultureInfo.InvariantCulture, out int hours) ||
                !int.TryParse(minutesText, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) ||
                minutes > 59)
            {
                return false;
            }

            offset = new TimeSpan(hours, minutes, 0);
            return offset <= TimeSpan.FromHours(14);
        }

        private static bool IsHeaderValueTrimCharacter(char value) =>
            value is '\t' or '\n' or '\v' or '\f' or '\r' or ' ';
    }
}
