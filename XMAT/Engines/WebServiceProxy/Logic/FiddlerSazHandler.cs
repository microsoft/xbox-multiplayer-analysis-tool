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
using System.Xml;
using System.Xml.Linq;
using XMAT.DataAbstractionLayer;
using XMAT.Operations;
using XMAT.Operations.Export;
using XMAT.Operations.Processes;
using XMAT.WebServiceCapture.Models;

namespace XMAT.WebServiceCapture
{
    internal sealed record FiddlerSazImportResult(
        ProxyConnectionsCollection Records,
        int SkippedIncompleteFrames,
        int SkippedMalformedHeaderLines,
        ImportedCaptureSource Source);

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

        // XMAT's exact values, which an import restores when the flag is present and valid. A sanitized archive never
        // carries the client address, the request path (it can hold query secrets) or the process.
        private const string RequestNumberFlag = "x-xmat-request-number";
        private const string ConnectionIdFlag = "x-xmat-connection-id";
        private const string RequestTimestampFlag = "x-xmat-request-timestamp";
        private const string ResponseTimestampFlag = "x-xmat-response-timestamp";
        private const string SchemeFlag = "x-xmat-scheme";
        private const string RequestPathFlag = "x-xmat-request-path";
        private const string ClientIpFlag = "x-xmat-clientip";

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
                        if (TryReadAbsoluteTarget(method, call, out Uri target, out targetAuthority))
                        {
                            call = target.PathAndQuery;
                            scheme = target.Scheme;
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
            long requestNumber = id;
            long connectionId = id;
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
                    var xmatFlags = new Dictionary<string, string>(StringComparer.Ordinal);
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
                        else if (flagName != null)
                        {
                            xmatFlags.TryAdd(flagName, (string)flag.Attribute("V"));
                        }
                    }

                    // XMAT's flags restore its exact values. The file is untrusted: a missing or invalid flag leaves its
                    // field as Fiddler's data gives it.
                    if (TryReadPositive(xmatFlags.GetValueOrDefault(RequestNumberFlag), out long exactNumber))
                    {
                        requestNumber = exactNumber;
                    }
                    if (TryReadPositive(xmatFlags.GetValueOrDefault(ConnectionIdFlag), out long exactConnection))
                    {
                        connectionId = exactConnection;
                    }
                    // XMAT's flags keep full ticks where Fiddler's timers keep five digits of a second; the duration
                    // is the time between the instants that the flags, or the timers without them, name.
                    bool exactStartRead = TryReadTimestamp(
                        xmatFlags.GetValueOrDefault(RequestTimestampFlag), out DateTime exactStart);
                    bool exactEndRead = TryReadTimestamp(
                        xmatFlags.GetValueOrDefault(ResponseTimestampFlag), out DateTime exactEnd);
                    if (exactStartRead)
                    {
                        requestTime = exactStart;
                    }
                    if (exactStartRead || exactEndRead)
                    {
                        DateTime? start = exactStartRead ? exactStart.ToUniversalTime() : begin?.UtcDateTime;
                        DateTime? end = exactEndRead ? exactEnd.ToUniversalTime() : done?.UtcDateTime;
                        if (start != null && end != null)
                        {
                            duration = end.Value - start.Value;
                        }
                    }
                    string exactScheme = xmatFlags.GetValueOrDefault(SchemeFlag);
                    scheme = Uri.CheckSchemeName(exactScheme) ? exactScheme : scheme;
                    string exactPath = xmatFlags.GetValueOrDefault(RequestPathFlag);
                    call = string.IsNullOrEmpty(exactPath) ? call : exactPath;
                    string exactClientIp = xmatFlags.GetValueOrDefault(ClientIpFlag);
                    clientIP = string.IsNullOrEmpty(exactClientIp) ? clientIP : exactClientIp;

                    clientProcess = ReadClientProcess(processInfo, processStartTime, processStatus);
                }
            }

            ProxyConnectionModel model = new(
                requestNumber,
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
            model.SetImportedIdentity(requestNumber, connectionId);
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

        private static bool TryReadPositive(string value, out long number) =>
            long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;

        // XMAT stores times as round-trip strings (ProxyConnectionModel.ToDateTimeString); like a loaded capture, the
        // model holds them as local time. The unknown time (UnknownTimeText) is no time, and neither is an instant that
        // this machine cannot show as a local time (ParseTimer).
        private static bool TryReadTimestamp(string value, out DateTime timestamp)
        {
            bool valid = DateTime.TryParseExact(
                value, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime exact)
                && !(exact.Kind == DateTimeKind.Unspecified && exact == DateTime.MinValue);
            DateTime utc = exact.Kind == DateTimeKind.Local
                ? exact.ToUniversalTime()
                : DateTime.SpecifyKind(exact, DateTimeKind.Utc);
            DateTime local = utc.ToLocalTime();
            valid = valid && local.ToUniversalTime() == utc;
            timestamp = valid ? local : DateTime.MinValue;
            return valid;
        }

        // A request target in absolute form whose scheme makes a URL (UriUtils.IsUrlScheme), and its authority written as a
        // Host header would be: the host as Uri reads it (a name in lower case, an IDN in punycode, an IPv4 address dotted,
        // an IPv6 address canonical and in brackets, with its zone's lexical spelling kept), and the port only when it is
        // not the scheme's default. User information is no part of it. A CONNECT's target is not one, nor is a target whose
        // host is no host identity, has no IDN form, or whose port is outside 1-65535.
        private static bool TryReadAbsoluteTarget(string method, string target, out Uri uri, out string authority)
        {
            authority = null;
            if (method != "CONNECT" &&
                StoredAuthority.TryParseAbsoluteTarget(target, out _, out _) &&
                Uri.TryCreate(target, UriKind.Absolute, out uri) &&
                UriUtils.IsUrlScheme(uri.Scheme) &&
                TryGetHostText(uri, out string hostText))
            {
                string written;
                if (uri.HostNameType == UriHostNameType.IPv6)
                {
                    written = "[" + hostText + "]";
                }
                else
                {
                    if (!HostAuthority.TryNormalizeHost(hostText, out string normalizedHost))
                    {
                        uri = null;
                        return false;
                    }

                    written = normalizedHost;
                }

                if (!uri.IsDefaultPort)
                {
                    written += ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
                }

                if (StoredAuthority.TryParse(written, out string storedHost, out int? storedPort))
                {
                    authority = StoredAuthority.Format(storedHost, storedPort);
                    return true;
                }
            }

            uri = null;
            return false;
        }

        // The host text that Uri gives a host identity: an IPv4 address dotted, an IPv6 address with its zone, or a name in
        // lower case and punycode. A name that Uri parses but whose IDN conversion fails, such as a label longer than 63
        // characters in punycode, has none: Uri.IdnHost throws UriFormatException for it or, for a name that Uri keeps as a
        // Basic host, gives it unconverted, which the caller's HostAuthority.TryNormalizeHost then refuses, as it refuses
        // any name that is not in ASCII.
        private static bool TryGetHostText(Uri uri, out string host)
        {
            try
            {
                host = uri.HostNameType == UriHostNameType.IPv6 ? uri.DnsSafeHost : uri.IdnHost;
                return true;
            }
            catch (UriFormatException)
            {
                host = null;
                return false;
            }
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
            string sourcePath = Path.GetFullPath(filename);
            var list = new ProxyConnectionsCollection();
            var skippedIncompleteFrames = 0;
            var skippedMalformedHeaderLines = 0;
            SqLtFileIdentity sourceIdentity;

            // Open the SAZ, checking its directory before ZipArchive reads it
            using (var input = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                sourceIdentity = SqLtFileIdentity.FromHandle(input.SafeFileHandle);
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
                skippedMalformedHeaderLines,
                new ImportedCaptureSource(sourcePath, sourceIdentity));
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

        // The SAZ writer of the shared export operation, sanitizing when asked. Frames are numbered in export order and
        // zero-padded to the digits of expectedRows, so repeated request numbers never collide; SID stays the row id.
        internal static ICaptureExportWriter CreateExportWriter(string stagedPath, bool sanitized, long expectedRows) =>
            new SazExportWriter(stagedPath, sanitized, expectedRows);

        private sealed class SazExportWriter : ICaptureExportWriter
        {
            private readonly ZipArchive _archive;
            private readonly bool _sanitized;
            private readonly string _frameFormat;
            private SazSanitizer _sanitizer;
            private long _rows;

            internal SazExportWriter(string path, bool sanitized, long expectedRows)
            {
                FileStream stream = null;
                Writing(() => stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None));
                _archive = new ZipArchive(stream, ZipArchiveMode.Create);
                _sanitized = sanitized;
                _frameFormat = "D" + Math.Max(expectedRows, 1).ToString(CultureInfo.InvariantCulture).Length
                    .ToString(CultureInfo.InvariantCulture);
            }

            public void Write(RawExportRow row, CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(row);
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyDictionary<string, object> values = row.Values;
                string method = Text(values, WebServiceCaptureMethod.FieldKey_RequestMethod);
                string requestHeaders = Text(values, WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders);
                string responseHeaders = Text(values, WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders);
                byte[] requestBody = Body(values, WebServiceCaptureMethod.FieldKey_RequestBody);
                byte[] responseBody = Body(values, WebServiceCaptureMethod.FieldKey_ResponseBody);
                string clientIp = Text(values, WebServiceCaptureMethod.FieldKey_ClientIP);
                ClientProcessInfo process = ClientProcessRecordCodec.FromValues(
                    Text(values, WebProxyCaptureSchema.ClientProcessName),
                    Number(values, WebProxyCaptureSchema.ClientProcessId),
                    Text(values, WebProxyCaptureSchema.ClientProcessStartTime),
                    Text(values, WebProxyCaptureSchema.ClientProcessStatus));
                string requested = Text(values, WebServiceCaptureMethod.FieldKey_RequestTimestamp);
                string responded = Text(values, WebServiceCaptureMethod.FieldKey_ResponseTimestamp);
                bool started = TryReadTimestamp(requested, out DateTime start);
                bool ended = TryReadTimestamp(responded, out DateTime end);
                string scheme = Text(values, WebServiceCaptureMethod.FieldKey_RequestScheme);

                var metadata = new FiddlerMetaData(row.RowId)
                {
                    ClientBeginRequest = start,
                    ClientDoneResponse = ended ? end : start,
                };
                metadata.SetClientIP(_sanitized ? "0.0.0.0" : clientIp);
                metadata.SetPositive(RequestNumberFlag, Number(values, WebServiceCaptureMethod.FieldKey_RequestNumber));
                metadata.SetPositive(ConnectionIdFlag, Number(values, WebServiceCaptureMethod.FieldKey_ConnectionId));
                metadata.SetExact(RequestTimestampFlag, started ? requested : null);
                metadata.SetExact(ResponseTimestampFlag, ended ? responded : null);
                metadata.SetExact(SchemeFlag, Uri.CheckSchemeName(scheme) ? scheme : null);

                SanitizedHttpMessage request;
                SanitizedHttpMessage? response;
                if (_sanitized)
                {
                    _sanitizer ??= new SazSanitizer(cancellationToken);
                    try
                    {
                        (request, response) = _sanitizer.SanitizeSession(
                            requestHeaders, requestBody, responseHeaders, responseBody, method);
                    }
                    catch (InvalidDataException ex)
                    {
                        throw new CaptureExportException(CaptureExportErrorCodes.InvalidHttp, OperationOutcome.InvalidInput, ex);
                    }

                    // The client address and the process metadata are never written; each omission counts once per session.
                    if (!string.IsNullOrEmpty(clientIp) && clientIp != "0.0.0.0")
                    {
                        _sanitizer.Summary.RedactedValues++;
                    }
                    if (FiddlerMetaData.HasProcessMetadata(process))
                    {
                        _sanitizer.Summary.RedactedValues++;
                    }
                    if (request.BodyOmitted)
                    {
                        metadata.SessionFlags["x-xmat-request-body-omitted"] = "uninspectable";
                    }
                    if (response?.BodyOmitted == true)
                    {
                        metadata.SessionFlags["x-xmat-response-body-omitted"] = "uninspectable";
                    }
                }
                else
                {
                    request = new SanitizedHttpMessage(requestHeaders, requestBody, false);
                    response = string.IsNullOrEmpty(responseHeaders)
                        ? null
                        : new SanitizedHttpMessage(responseHeaders, responseBody, false);
                    metadata.SetExact(ClientIpFlag, clientIp);
                    metadata.SetExact(RequestPathFlag, Text(values, WebServiceCaptureMethod.FieldKey_RequestPath));
                    metadata.SetClientProcess(process);
                }

                string frame = (++_rows).ToString(_frameFormat, CultureInfo.InvariantCulture);
                Writing(() =>
                {
                    AddEntry(_archive, EntryType.Request, frame, request.Headers, request.Body, cancellationToken);
                    AddMetaEntry(_archive, frame, metadata, cancellationToken);
                    if (response is { } written)
                    {
                        AddEntry(_archive, EntryType.Response, frame, written.Headers, written.Body, cancellationToken);
                    }
                });
                if (_sanitized)
                {
                    _sanitizer.Summary.Sessions++;
                }
            }

            public ExportWriteSummary Complete(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Writing(_archive.Dispose);
                return new ExportWriteSummary(
                    _rows,
                    _sanitized ? _sanitizer?.Summary ?? new SanitizedExportSummary() : null);
            }

            public void Dispose() => _archive.Dispose();

            // A write that fails is coded; cancellation and anything unexpected keep their own classification.
            private static void Writing(Action write)
            {
                try
                {
                    write();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new CaptureExportException(CaptureExportErrorCodes.WriteFailed, OperationOutcome.Failed, ex);
                }
            }

            private static string Text(IReadOnlyDictionary<string, object> values, string name) =>
                values.TryGetValue(name, out object value)
                    ? value switch
                    {
                        null => string.Empty,
                        string text => text,
                        byte[] bytes => Encoding.UTF8.GetString(bytes),
                        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
                    }
                    : string.Empty;

            private static long Number(IReadOnlyDictionary<string, object> values, string name) =>
                long.TryParse(Text(values, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out long number)
                    ? number
                    : 0;

            // Bodies are stored as base64 text (a raw BLOB is written as it is); anything else is invalid input.
            private static byte[] Body(IReadOnlyDictionary<string, object> values, string name)
            {
                if (values.TryGetValue(name, out object value) && value is byte[] bytes)
                {
                    return bytes;
                }
                try
                {
                    return Convert.FromBase64String(Text(values, name));
                }
                catch (FormatException ex)
                {
                    throw new CaptureExportException(CaptureExportErrorCodes.InvalidBody, OperationOutcome.InvalidInput, ex);
                }
            }
        }

        private static void AddMetaEntry(ZipArchive archive, string frame, FiddlerMetaData metaDataToSerialize,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ZipArchiveEntry entry = archive.CreateEntry($"raw/{frame}{EntryTypeMap[EntryType.Meta]}");
            using Stream s = entry.Open();
            string xmlText = metaDataToSerialize.ToXmlString();
            var xmlTextBytes = Encoding.UTF8.GetBytes(xmlText);
            s.Write(xmlTextBytes);
        }

        private static void AddEntry(ZipArchive archive, EntryType entryType, string frame, string headers, byte[] body,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ZipArchiveEntry entry = archive.CreateEntry($"raw/{frame}{EntryTypeMap[entryType]}");
            using Stream s = entry.Open();
            var headerBytes = Encoding.ASCII.GetBytes(headers ?? string.Empty);
            s.Write(headerBytes);
            ReadOnlySpan<byte> bodySpan = body;
            for (var offset = 0; offset < bodySpan.Length; offset += 8192)
            {
                cancellationToken.ThrowIfCancellationRequested();
                s.Write(bodySpan.Slice(offset, Math.Min(8192, bodySpan.Length - offset)));
            }
        }

        private class FiddlerMetaData
        {
            public const long DefaultFiddlerBitFlags = 2100;

            public long SID { get; }
            public long BitFlags { get; }
            public DateTime ClientBeginRequest { private get; set; }
            public DateTime ClientDoneResponse { private get; set; }
            public Dictionary<string, string> SessionFlags { get; }

            public string DateTimeFormat(DateTime dateTime)
            {
                // NOTE: this is very *close* to ISO8601, which would require
                // to just call .ToString("o") but ... Fiddler only uses 5 decimal
                // places after the seconds rather than 7.
                return dateTime.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss.fffffK", CultureInfo.InvariantCulture);
            }

            public FiddlerMetaData(Int64 sid, Int64 bitflags = DefaultFiddlerBitFlags)
            {
                SessionFlags = new();
                SID = sid;
                BitFlags = bitflags;
            }

            public void SetClientIP(string clientIP)
            {
                SessionFlags["x-clientip"] = clientIP;
            }

            // An exact value is written only when XML can hold it unchanged; the import otherwise keeps Fiddler's data.
            public void SetExact(string name, string value)
            {
                if (!string.IsNullOrEmpty(value) && XmlText(value) == value)
                {
                    SessionFlags[name] = value;
                }
            }

            public void SetPositive(string name, long value)
            {
                if (value > 0)
                {
                    SessionFlags[name] = value.ToString(CultureInfo.InvariantCulture);
                }
            }

            // Whether SetClientProcess writes any process flag: the identity, with its start time, or the attribution
            // state.
            internal static bool HasProcessMetadata(ClientProcessInfo process) =>
                !ProcessFilter.IsUnknown(process) ||
                ClientProcessRecordCodec.StatusToken(process?.Status ?? ClientProcessStatus.NotRecorded).Length != 0;

            // Writes Fiddler's "name:pid", the start time and the attribution state. A session without process
            // information gets no flag, and a pending lookup is never written.
            public void SetClientProcess(ClientProcessInfo process)
            {
                if (!ProcessFilter.IsUnknown(process))
                {
                    SessionFlags[ProcessInfoFlag] = process.ProcessId > 0
                        ? process.Name + ":" + process.ProcessId.ToString(CultureInfo.InvariantCulture)
                        : process.Name;
                    if (process.StartTimeUtc is DateTimeOffset started)
                    {
                        SessionFlags[ProcessStartTimeFlag] =
                            started.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
                    }
                }

                string status = ClientProcessRecordCodec.StatusToken(
                    process?.Status ?? ClientProcessStatus.NotRecorded);
                if (status.Length != 0)
                {
                    SessionFlags[ProcessStatusFlag] = status;
                }
            }

            // Built with the XML APIs, so every value stays data whatever characters it holds.
            public string ToXmlString()
            {
                string requested = DateTimeFormat(ClientBeginRequest);
                string responded = DateTimeFormat(ClientDoneResponse);
                var session = new XElement(
                    "Session",
                    new XAttribute("SID", SID),
                    new XAttribute("BitFlags", BitFlags),
                    new XElement(
                        "SessionTimers",
                        new XAttribute("ClientConnected", requested),
                        new XAttribute("ClientBeginRequest", requested),
                        new XAttribute("GotRequestHeaders", requested),
                        new XAttribute("ClientDoneRequest", requested),
                        new XAttribute("GatewayTime", "0"),
                        new XAttribute("DNSTime", "0"),
                        new XAttribute("TCPConnectTime", "0"),
                        new XAttribute("HTTPSHandshakeTime", "0"),
                        new XAttribute("ServerConnected", responded),
                        new XAttribute("FiddlerBeginRequest", responded),
                        new XAttribute("ServerGotRequest", responded),
                        new XAttribute("ServerBeginResponse", responded),
                        new XAttribute("GotResponseHeaders", responded),
                        new XAttribute("ServerDoneResponse", responded),
                        new XAttribute("ClientBeginResponse", responded),
                        new XAttribute("ClientDoneResponse", responded)),
                    new XElement(
                        "SessionFlags",
                        SessionFlags.Select(sessionFlag => new XElement(
                            "SessionFlag",
                            new XAttribute("N", XmlText(sessionFlag.Key)),
                            new XAttribute("V", XmlText(sessionFlag.Value))))));
                return "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + Environment.NewLine + session;
            }

            // XML 1.0 cannot hold some control characters even escaped; they are dropped rather than failing the export.
            private static string XmlText(string value)
            {
                if (string.IsNullOrEmpty(value))
                {
                    return string.Empty;
                }

                StringBuilder text = null;
                for (int index = 0; index < value.Length; index++)
                {
                    char current = value[index];
                    if (XmlConvert.IsXmlChar(current))
                    {
                        text?.Append(current);
                    }
                    else if (index + 1 < value.Length && XmlConvert.IsXmlSurrogatePair(value[index + 1], current))
                    {
                        text?.Append(current).Append(value[index + 1]);
                        index++;
                    }
                    else
                    {
                        text ??= new StringBuilder(value, 0, index, value.Length);
                    }
                }

                return text?.ToString() ?? value;
            }
        }
    }
}
