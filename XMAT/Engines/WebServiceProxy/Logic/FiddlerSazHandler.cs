// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Xml.Linq;
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

                        // CALL & SCHEME
                        if (method == "CONNECT")
                        {
                            call = firstLineSplit[1];
                            scheme = @"http";
                        }
                        else
                        {
                            if (Uri.TryCreate(firstLineSplit[1], UriKind.Absolute, out Uri uri))
                            {
                                call = uri.LocalPath;
                                scheme = uri.Scheme;
                            }
                            else
                            {
                                scheme = firstLineSplit[0];
                                call = firstLineSplit[1];
                            }
                        }
                    }

                    WebHeaderCollection reqHeaders = ReadHeaders(
                        cFile,
                        ref requestHeaderBytes,
                        ref skippedMalformedHeaderLines);

                    hostAndPort = reqHeaders["Host"];

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

                    WebHeaderCollection headers = ReadHeaders(
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

                    requestTime = DateTime.Parse((string)sessionTimers.Attribute("ClientBeginRequest"));

                    DateTime endTime = DateTime.Parse((string)sessionTimers.Attribute("ClientDoneResponse"));

                    duration = endTime - requestTime;

                    var sessionFlags = xmldata.Element("Session").Element("SessionFlags");

                    foreach (var flag in sessionFlags.Descendants())
                    {
                        if ((string)flag.Attribute("N") == "x-clientip")
                        {
                            // CLIENT IP
                            clientIP = (string)flag.Attribute("V");
                            clientIP = clientIP.Substring(clientIP.LastIndexOf(':') + 1);
                            break;
                        }
                    }
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

            return model;
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

        private static WebHeaderCollection ReadHeaders(
            BinaryReader reader,
            ref int rawHeaderBytes,
            ref int skippedMalformedHeaderLines)
        {
            var headers = new WebHeaderCollection();
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
                if (string.IsNullOrWhiteSpace(line))
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

        private static string ReadHeaderLine(
            BinaryReader reader,
            ref int rawHeaderBytes)
        {
            try
            {
                string line = reader.ReadLine(
                    MaxRawHeaderBytesPerMessage - rawHeaderBytes,
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

        private static void AddHeaderOrSkip(
            WebHeaderCollection headers,
            string line,
            ref int skippedMalformedHeaderLines)
        {
            if (!IsValidHeaderLine(line))
            {
                skippedMalformedHeaderLines++;
                return;
            }

            try
            {
                headers.Add(line);
            }
            catch (ArgumentException)
            {
                skippedMalformedHeaderLines++;
            }
        }

        // Mirrors .NET 10 WebHeaderCollection.Add(string) validation without using exceptions for control flow.
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
                    ':' or '\\' or '"' or '\'' or '/' or '[' or ']' or
                    '?' or '=' or '{' or '}');
        }

        private static bool IsHeaderValueTrimCharacter(char value) =>
            value is '\t' or '\n' or '\v' or '\f' or '\r' or ' ';

        public static void CreateSazFile(string filename, IEnumerable<ProxyConnectionModel> records)
        {
            // always overwrite
            ZipArchive archive = new ZipArchive(File.Create(filename), ZipArchiveMode.Update);

            foreach (var record in records)
            {
                AddEntry(archive, EntryType.Request, record.RequestNumber, record.RequestLineAndHeaders, record.RequestBody);

                var metaData = new FiddlerMetaData(record.RecordRowId);
                metaData.SetClientIP(record.ClientIP);
                metaData.ClientBeginRequest = record.RequestTime;
                metaData.ClientDoneResponse = record.RequestTime + record.Duration;
                AddMetaEntry(archive, record.RequestNumber, metaData);

                if (!string.IsNullOrEmpty(record.ResponseLineAndHeaders))
                {
                    AddEntry(archive, EntryType.Response, record.RequestNumber, record.ResponseLineAndHeaders, record.ResponseBody);
                }
            }
            archive.Dispose();
        }

        internal static SanitizedExportSummary CreateSanitizedSazFile(
            string filename, IEnumerable<ProxyConnectionModel> records, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(Path.GetExtension(filename), ".saz", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(Localization.GetLocalizedString("SANITIZED_EXPORT_SAZ_REQUIRED"), nameof(filename));
            }
            var destination = Path.GetFullPath(filename);
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var sanitizer = new SazSanitizer(cancellationToken);
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    foreach (var record in records)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var (request, response) = sanitizer.SanitizeSession(
                            record.RequestLineAndHeaders, record.RequestBody,
                            record.ResponseLineAndHeaders, record.ResponseBody, record.Method);
                        AddEntry(archive, EntryType.Request, record.RequestNumber, request.Headers, request.Body, cancellationToken);
                        var metadata = new FiddlerMetaData(record.RecordRowId)
                        {
                            ClientBeginRequest = record.RequestTime,
                            ClientDoneResponse = record.RequestTime + record.Duration
                        };
                        metadata.SetClientIP("0.0.0.0");
                        if (!string.IsNullOrEmpty(record.ClientIP) && record.ClientIP != "0.0.0.0")
                        {
                            sanitizer.Summary.RedactedValues++;
                        }
                        if (request.BodyOmitted)
                        {
                            metadata.SessionFlags["x-xmat-request-body-omitted"] = "uninspectable";
                        }
                        if (response?.BodyOmitted == true)
                        {
                            metadata.SessionFlags["x-xmat-response-body-omitted"] = "uninspectable";
                        }
                        AddMetaEntry(archive, record.RequestNumber, metadata, cancellationToken);
                        if (response is { } sanitizedResponse)
                        {
                            AddEntry(archive, EntryType.Response, record.RequestNumber,
                                sanitizedResponse.Headers, sanitizedResponse.Body, cancellationToken);
                        }
                        sanitizer.Summary.Sessions++;
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, destination, true);
                return sanitizer.Summary;
            }
            finally
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    PublicUtilities.AppLog(LogLevel.ERROR, "Could not remove a sanitized export temporary file.");
                }
            }
        }

        private static void AddMetaEntry(ZipArchive archive, long id, FiddlerMetaData metaDataToSerialize,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ZipArchiveEntry entry = archive.CreateEntry($"raw/{id}{EntryTypeMap[EntryType.Meta]}");
            using Stream s = entry.Open();
            string xmlText = metaDataToSerialize.ToXmlString();
            var xmlTextBytes = Encoding.UTF8.GetBytes(xmlText);
            s.Write(xmlTextBytes);
        }

        private static void AddEntry(ZipArchive archive, EntryType entryType, long id, string headers, byte[] body,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ZipArchiveEntry entry = archive.CreateEntry($"raw/{id}{EntryTypeMap[entryType]}");
            using Stream s = entry.Open();
            var headerBytes = Encoding.ASCII.GetBytes(headers);
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
                return dateTime.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss.fffffK");
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

            public string ToXmlString()
            {
                StringBuilder sb = new();
                sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
                sb.AppendLine($"<Session SID=\"{SID}\" BitFlags=\"{BitFlags}\">");

                sb.Append("<SessionTimers ");
                sb.Append($"ClientConnected=\"{DateTimeFormat(ClientBeginRequest)}\" ");
                sb.Append($"ClientBeginRequest=\"{DateTimeFormat(ClientBeginRequest)}\" ");
                sb.Append($"GotRequestHeaders=\"{DateTimeFormat(ClientBeginRequest)}\" ");
                sb.Append($"ClientDoneRequest=\"{DateTimeFormat(ClientBeginRequest)}\" ");
                sb.Append("GatewayTime=\"0\" ");
                sb.Append("DNSTime=\"0\" ");
                sb.Append("TCPConnectTime=\"0\" ");
                sb.Append("HTTPSHandshakeTime=\"0\" ");
                sb.Append($"ServerConnected=\"{DateTimeFormat(ClientDoneResponse)}\" ");
                sb.Append($"FiddlerBeginRequest=\"{DateTimeFormat(ClientDoneResponse)}\" ");
                sb.Append($"ServerGotRequest=\"{DateTimeFormat(ClientDoneResponse)}\" ");
                sb.Append($"ServerBeginResponse=\"{DateTimeFormat(ClientDoneResponse)}\" ");
                sb.Append($"GotResponseHeaders=\"{DateTimeFormat(ClientDoneResponse)}\" ");
                sb.Append($"ServerDoneResponse=\"{DateTimeFormat(ClientDoneResponse)}\" ");
                sb.Append($"ClientBeginResponse=\"{DateTimeFormat(ClientDoneResponse)}\" ");
                sb.Append($"ClientDoneResponse=\"{DateTimeFormat(ClientDoneResponse)}\" ");
                sb.Append("/>");

                sb.AppendLine();
                sb.AppendLine($"  <SessionFlags>");
                foreach (var sessionFlag in SessionFlags)
                {
                    sb.AppendLine($"    <SessionFlag N=\"{sessionFlag.Key}\" V=\"{sessionFlag.Value}\" />");
                }
                sb.AppendLine($"  </SessionFlags>");
                sb.AppendLine($"</Session>");
                return sb.ToString();
            }
        }
    }
}
