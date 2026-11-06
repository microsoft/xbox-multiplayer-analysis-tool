// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using XMAT.DataAbstractionLayer;
using XMAT.Operations.Analysis;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Fixtures
{
    internal sealed record SyntheticSazOptions(
        int Consoles = 1,
        int RequestsPerConsole = 4,
        bool IncludeCanaries = false,
        bool IncludeXr049Violation = false);

    internal sealed record SyntheticXmatcapOptions(
        int Devices = 1,
        int RequestsPerDevice = 4,
        bool IncludeCanaries = false,
        bool IncludeXr049Violation = false);

    public enum SyntheticSazFrameKind
    {
        Valid,
        MalformedRequestStartLine,
        MalformedResponseStartLine,
        MissingHost,
        MalformedHost,
        EmptyHostWithPort,
    }

    internal static class SyntheticCaptures
    {
        internal const string Canary = "XMATCANARY7f3c";
        private const string PresenceHost = "userpresence.xboxlive.com";
        private static readonly DateTime SyntheticStartUtc =
            new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        internal static string WriteSaz(
            string directory,
            string fileName,
            SyntheticSazOptions options = null)
        {
            options ??= new SyntheticSazOptions();
            ValidateCounts(options.Consoles, options.RequestsPerConsole);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, fileName);
            using var file = File.Create(path);
            using var archive = new ZipArchive(file, ZipArchiveMode.Create);
            WriteTextEntry(
                archive,
                "_index.htm",
                "<html><body>Synthetic capture</body></html>",
                Encoding.UTF8);

            var frame = 0;
            for (var console = 0; console < options.Consoles; console++)
            {
                var clientIp = options.IncludeCanaries
                    ? $"{Canary}-client-{console + 1}"
                    : $"10.0.0.{console + 1}";
                for (var request = 0; request < options.RequestsPerConsole; request++)
                {
                    frame++;
                    var data = CreateRequestData(
                        console,
                        request,
                        options.RequestsPerConsole,
                        options.IncludeCanaries,
                        options.IncludeXr049Violation);
                    WriteTextEntry(
                        archive,
                        $"raw/{frame:D4}_c.txt",
                        data.RequestText,
                        Encoding.ASCII);
                    WriteTextEntry(
                        archive,
                        $"raw/{frame:D4}_s.txt",
                        data.ResponseText,
                        Encoding.ASCII);
                    WriteTextEntry(
                        archive,
                        $"raw/{frame:D4}_m.xml",
                        CreateMetadata(frame, clientIp),
                        Encoding.UTF8);
                }
            }

            return path;
        }

        internal static string WriteXmatcap(
            string directory,
            string fileName,
            SyntheticXmatcapOptions options = null)
        {
            options ??= new SyntheticXmatcapOptions();
            ValidateCounts(options.Devices, options.RequestsPerDevice);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, fileName);

            using var database = new SqLtDatabase();
            database.Initialize();
            var devices = database.CreateTable(
                "CaptureDevices",
                new Field<long>("DeviceType"),
                new Field<string>("DeviceName"),
                new Field<long>("CaptureType"));
            var captures = database.CreateTable(
                WebServiceCaptureMethod.TableName,
                new Field<string>(WebServiceCaptureMethod.FieldKey_DeviceName),
                new Field<long>(WebServiceCaptureMethod.FieldKey_RequestNumber),
                new Field<long>(WebServiceCaptureMethod.FieldKey_ConnectionId),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestTimestamp),
                new Field<string>(WebServiceCaptureMethod.FieldKey_ResponseTimestamp),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestScheme),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestHost),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestPort),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestMethod),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestPath),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestStatus),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders),
                new Field<string>(WebServiceCaptureMethod.FieldKey_RequestBody),
                new Field<string>(WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders),
                new Field<string>(WebServiceCaptureMethod.FieldKey_ResponseBody),
                new Field<string>(WebServiceCaptureMethod.FieldKey_ClientIP));

            long requestNumber = 0;
            for (var device = 0; device < options.Devices; device++)
            {
                var deviceName = $"Synthetic device {device + 1}";
                devices.AddRow(
                    (long)DeviceType.XboxConsole,
                    deviceName,
                    (long)CaptureType.WebProxy);
                var clientIp = options.IncludeCanaries
                    ? $"{Canary}-client-{device + 1}"
                    : $"10.0.0.{device + 1}";
                for (var request = 0; request < options.RequestsPerDevice; request++)
                {
                    requestNumber++;
                    var data = CreateRequestData(
                        device,
                        request,
                        options.RequestsPerDevice,
                        options.IncludeCanaries,
                        options.IncludeXr049Violation);
                    var started = SyntheticStartUtc.AddSeconds(requestNumber);
                    captures.AddRow(
                        deviceName,
                        requestNumber,
                        requestNumber,
                        ProxyConnectionModel.ToDateTimeString(started),
                        ProxyConnectionModel.ToDateTimeString(
                            started.AddMilliseconds(25)),
                        "https",
                        data.Host,
                        "443",
                        "POST",
                        data.PathAndQuery,
                        "200",
                        data.RequestHeaders,
                        Convert.ToBase64String(data.RequestBody),
                        data.ResponseHeaders,
                        Convert.ToBase64String(data.ResponseBody),
                        clientIp);
                }
            }

            database.SaveToFile(path);
            return path;
        }

        internal static string WriteCorruptSaz(
            string directory,
            string fileName)
        {
            Directory.CreateDirectory(directory);
            string path =
                Path.Combine(
                    directory,
                    fileName);
            File.WriteAllBytes(
                path,
                Encoding.ASCII.GetBytes(
                    "not a zip archive"));
            return path;
        }

        internal static string WriteMalformedHeaderSaz(
            string directory,
            string fileName)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, fileName);
            using var file = File.Create(path);
            using var archive = new ZipArchive(file, ZipArchiveMode.Create);
            var data = CreateRequestData(0, 0, 1, false, false);
            WriteTextEntry(
                archive,
                "raw/0001_c.txt",
                data.RequestHeaders +
                $"Malformed request header {Canary}\r\n" +
                "X-Xmat-Request-After-Malformed: retained\r\n\r\n" +
                Encoding.ASCII.GetString(data.RequestBody),
                Encoding.ASCII);
            WriteTextEntry(
                archive,
                "raw/0001_s.txt",
                data.ResponseHeaders +
                $"Malformed response {Canary}: value\r\n" +
                "X-Xmat-Response-After-Malformed: retained\r\n\r\n" +
                Encoding.ASCII.GetString(data.ResponseBody),
                Encoding.ASCII);
            WriteTextEntry(
                archive,
                "raw/0001_m.xml",
                CreateMetadata(1, "10.0.0.1"),
                Encoding.UTF8);
            return path;
        }

        internal static string WriteHeaderLimitSaz(
            string directory,
            string fileName,
            bool response)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, fileName);
            using var file = File.Create(path);
            using var archive = new ZipArchive(file, ZipArchiveMode.Create);
            RequestData data = CreateRequestData(
                0,
                0,
                1,
                false,
                false);
            string oversizedHeader =
                "X-Xmat-Oversized: " +
                Canary +
                new string(
                    'x',
                    FiddlerSazHandler
                        .MaxRawHeaderBytesPerMessage);
            string request = response
                ? data.RequestText
                : AddHeaderBeforeBody(
                    data.RequestText,
                    oversizedHeader);
            string responseText = response
                ? AddHeaderBeforeBody(
                    data.ResponseText,
                    oversizedHeader)
                : data.ResponseText;
            WriteTextEntry(
                archive,
                "raw/0001_c.txt",
                request,
                Encoding.ASCII);
            WriteTextEntry(
                archive,
                "raw/0001_s.txt",
                responseText,
                Encoding.ASCII);
            WriteTextEntry(
                archive,
                "raw/0001_m.xml",
                CreateMetadata(1, "10.0.0.1"),
                Encoding.UTF8);
            return path;
        }

        internal static string WriteSazFrames(
            string directory,
            string fileName,
            IReadOnlyList<SyntheticSazFrameKind> frames,
            int extraResponseBodyBytes = 0)
        {
            ArgumentNullException.ThrowIfNull(frames);
            ArgumentOutOfRangeException.ThrowIfZero(
                frames.Count,
                nameof(frames));
            ArgumentOutOfRangeException.ThrowIfNegative(
                extraResponseBodyBytes);

            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, fileName);
            using var file = File.Create(path);
            using var archive = new ZipArchive(file, ZipArchiveMode.Create);
            for (var index = 0; index < frames.Count; index++)
            {
                int frame = index + 1;
                RequestData data = CreateRequestData(
                    0,
                    index,
                    frames.Count,
                    false,
                    false);
                string request = frames[index] ==
                    SyntheticSazFrameKind.MalformedRequestStartLine
                        ? ReplaceStartLine(
                            data.RequestText,
                            $"MALFORMED-REQUEST-{Canary}")
                        : data.RequestText;
                if (frames[index] ==
                    SyntheticSazFrameKind.MalformedResponseStartLine)
                {
                    request = AddHeaderBeforeBody(
                        request,
                        $"Malformed skipped header {Canary}");
                }
                if (frames[index] ==
                    SyntheticSazFrameKind.MissingHost)
                {
                    request = RemoveHeader(
                        request,
                        "Host:");
                }
                else if (frames[index] ==
                    SyntheticSazFrameKind.MalformedHost)
                {
                    request = ReplaceHeader(
                        request,
                        "Host:",
                        $"Host {Canary}: {data.Host}");
                }
                else if (frames[index] ==
                    SyntheticSazFrameKind.EmptyHostWithPort)
                {
                    request = ReplaceHeader(
                        request,
                        "Host:",
                        "Host: :8080");
                }
                string response = frames[index] ==
                    SyntheticSazFrameKind.MalformedResponseStartLine
                        ? ReplaceStartLine(
                            data.ResponseText,
                            $"MALFORMED-RESPONSE-{Canary}")
                        : data.ResponseText;
                if (extraResponseBodyBytes > 0)
                {
                    response += new string(
                        'x',
                        extraResponseBodyBytes);
                }

                WriteTextEntry(
                    archive,
                    $"raw/{frame:D4}_c.txt",
                    request,
                    Encoding.ASCII);
                WriteTextEntry(
                    archive,
                    $"raw/{frame:D4}_s.txt",
                    response,
                    Encoding.ASCII);
                WriteTextEntry(
                    archive,
                    $"raw/{frame:D4}_m.xml",
                    CreateMetadata(frame, "10.0.0.1"),
                    Encoding.UTF8);
            }

            return path;
        }

        internal static string WriteZipBombSaz(
            string directory,
            string fileName)
        {
            Directory.CreateDirectory(directory);
            string path =
                Path.Combine(
                    directory,
                    fileName);
            using var file =
                File.Create(path);
            using var archive =
                new ZipArchive(
                    file,
                    ZipArchiveMode.Create);
            ZipArchiveEntry entry =
                archive.CreateEntry(
                    "raw/1_c.txt",
                    CompressionLevel.Optimal);
            using Stream stream =
                entry.Open();
            var buffer =
                new byte[64 * 1024];
            long remaining =
                AnalysisLimits
                    .Default
                    .MaxEntryBytes +
                1;
            while (remaining > 0)
            {
                int count =
                    (int)Math.Min(
                        buffer.Length,
                        remaining);
                stream.Write(
                    buffer,
                    0,
                    count);
                remaining -= count;
            }
            return path;
        }

        internal static string WriteZipWithEntries(
            string directory,
            string fileName,
            int entryCount,
            int entryNameLength = 8)
        {
            ValidateCounts(entryCount, entryNameLength);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, fileName);
            using var file = File.Create(path);
            using var archive = new ZipArchive(file, ZipArchiveMode.Create);
            for (var index = 0; index < entryCount; index++)
            {
                var suffix = index.ToString("D8");
                var padding = new string(
                    'x',
                    Math.Max(0, entryNameLength - suffix.Length));
                archive.CreateEntry(
                    padding + suffix,
                    CompressionLevel.NoCompression);
            }
            return path;
        }

        internal static string WriteUnderstatedEntryCountZip(
            string directory,
            string fileName)
        {
            var path = WriteZipWithEntries(
                directory,
                fileName,
                entryCount: 2);
            var bytes = File.ReadAllBytes(path);
            var eocd = FindEndOfCentralDirectory(bytes);
            BinaryPrimitives.WriteUInt16LittleEndian(
                bytes.AsSpan(eocd + 8, 2),
                1);
            BinaryPrimitives.WriteUInt16LittleEndian(
                bytes.AsSpan(eocd + 10, 2),
                1);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        internal static string WriteInvalidCentralDirectoryBoundsZip(
            string directory,
            string fileName,
            bool corruptSize)
        {
            var path = WriteZipWithEntries(
                directory,
                fileName,
                entryCount: 1);
            var bytes = File.ReadAllBytes(path);
            var eocd = FindEndOfCentralDirectory(bytes);
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(eocd + (corruptSize ? 12 : 16), 4),
                checked((uint)eocd));
            File.WriteAllBytes(path, bytes);
            return path;
        }

        internal static string WriteSazWithDecoyEndRecordComment(
            string directory,
            string fileName)
        {
            var path = WriteSaz(
                directory,
                fileName,
                new SyntheticSazOptions(RequestsPerConsole: 1));
            var bytes = File.ReadAllBytes(path);
            var eocd = FindEndOfCentralDirectory(bytes);
            const int commentLength = 32;
            BinaryPrimitives.WriteUInt16LittleEndian(
                bytes.AsSpan(eocd + 20, 2),
                commentLength);
            var withComment = new byte[bytes.Length + commentLength];
            Buffer.BlockCopy(
                bytes,
                0,
                withComment,
                0,
                bytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(
                withComment.AsSpan(bytes.Length + 8, 4),
                0x06054b50);
            File.WriteAllBytes(path, withComment);
            return path;
        }

        internal static string WriteZip64Saz(
            string directory,
            string fileName,
            ulong? zip64RecordOffset = null,
            ulong zip64RecordBodySize = 44)
        {
            var path = WriteSaz(
                directory,
                fileName,
                new SyntheticSazOptions(RequestsPerConsole: 1));
            var bytes = File.ReadAllBytes(path);
            var eocd = FindEndOfCentralDirectory(bytes);
            if (eocd + 22 != bytes.Length)
            {
                throw new InvalidOperationException(
                    "The synthetic ZIP unexpectedly has a comment.");
            }
            ushort entries = BinaryPrimitives.ReadUInt16LittleEndian(
                bytes.AsSpan(eocd + 10, 2));
            uint directorySize =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(eocd + 12, 4));
            uint directoryOffset =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(eocd + 16, 4));

            var zip64 = new byte[56];
            BinaryPrimitives.WriteUInt32LittleEndian(
                zip64.AsSpan(0, 4),
                0x06064b50);
            BinaryPrimitives.WriteUInt64LittleEndian(
                zip64.AsSpan(4, 8),
                zip64RecordBodySize);
            BinaryPrimitives.WriteUInt16LittleEndian(
                zip64.AsSpan(12, 2),
                45);
            BinaryPrimitives.WriteUInt16LittleEndian(
                zip64.AsSpan(14, 2),
                45);
            BinaryPrimitives.WriteUInt64LittleEndian(
                zip64.AsSpan(24, 8),
                entries);
            BinaryPrimitives.WriteUInt64LittleEndian(
                zip64.AsSpan(32, 8),
                entries);
            BinaryPrimitives.WriteUInt64LittleEndian(
                zip64.AsSpan(40, 8),
                directorySize);
            BinaryPrimitives.WriteUInt64LittleEndian(
                zip64.AsSpan(48, 8),
                directoryOffset);

            var locator = new byte[20];
            BinaryPrimitives.WriteUInt32LittleEndian(
                locator.AsSpan(0, 4),
                0x07064b50);
            BinaryPrimitives.WriteUInt64LittleEndian(
                locator.AsSpan(8, 8),
                zip64RecordOffset ?? checked((ulong)eocd));
            BinaryPrimitives.WriteUInt32LittleEndian(
                locator.AsSpan(16, 4),
                1);

            var legacy = bytes.AsSpan(eocd, 22).ToArray();
            BinaryPrimitives.WriteUInt16LittleEndian(
                legacy.AsSpan(8, 2),
                ushort.MaxValue);
            BinaryPrimitives.WriteUInt16LittleEndian(
                legacy.AsSpan(10, 2),
                ushort.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(
                legacy.AsSpan(12, 4),
                uint.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(
                legacy.AsSpan(16, 4),
                uint.MaxValue);

            using var output = new MemoryStream();
            output.Write(bytes, 0, eocd);
            output.Write(zip64);
            output.Write(locator);
            output.Write(legacy);
            File.WriteAllBytes(path, output.ToArray());
            return path;
        }

        // A one-request ZIP64 SAZ whose ZIP64 directory record declares the given number of entries.
        internal static string WriteZip64SazDeclaringEntries(
            string directory,
            string fileName,
            long declaredEntries)
        {
            var path = WriteZip64Saz(
                directory,
                fileName);
            var bytes = File.ReadAllBytes(path);
            var record = -1;
            for (var index = bytes.Length - 4; index >= 0; index--)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(
                        bytes.AsSpan(index, 4)) == 0x06064b50)
                {
                    record = index;
                    break;
                }
            }
            if (record < 0)
            {
                throw new InvalidOperationException(
                    "The synthetic ZIP has no ZIP64 end record.");
            }
            BinaryPrimitives.WriteUInt64LittleEndian(
                bytes.AsSpan(record + 24, 8),
                checked((ulong)declaredEntries));
            BinaryPrimitives.WriteUInt64LittleEndian(
                bytes.AsSpan(record + 32, 8),
                checked((ulong)declaredEntries));
            File.WriteAllBytes(path, bytes);
            return path;
        }

        internal static string WriteOpaqueEncodedBodySaz(
            string directory,
            string fileName)
        {
            Directory.CreateDirectory(directory);
            string path =
                Path.Combine(
                    directory,
                    fileName);
            using var file =
                File.Create(path);
            using var archive =
                new ZipArchive(
                    file,
                    ZipArchiveMode.Create);
            WriteTextEntry(
                archive,
                "_index.htm",
                "<html><body>Synthetic capture</body></html>",
                Encoding.UTF8);
            RequestData request =
                CreateRequestData(
                    0,
                    0,
                    1,
                    false,
                    false);
            WriteTextEntry(
                archive,
                "raw/0001_c.txt",
                request.RequestText,
                Encoding.ASCII);
            byte[] body =
                CompressGzip(
                    new byte[64 * 1024]);
            byte[] headers =
                Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: application/json\r\n" +
                    "Content-Encoding: gzip\r\n" +
                    $"Content-Length: {body.Length}\r\n\r\n");
            var response =
                new byte[
                    headers.Length +
                    body.Length];
            Buffer.BlockCopy(
                headers,
                0,
                response,
                0,
                headers.Length);
            Buffer.BlockCopy(
                body,
                0,
                response,
                headers.Length,
                body.Length);
            WriteBytesEntry(
                archive,
                "raw/0001_s.txt",
                response);
            WriteTextEntry(
                archive,
                "raw/0001_m.xml",
                CreateMetadata(
                    1,
                    "10.0.0.1"),
                Encoding.UTF8);
            return path;
        }

        internal static string WriteMixedIncompleteSaz(
            string directory,
            string fileName)
        {
            Directory.CreateDirectory(directory);
            string path =
                Path.Combine(
                    directory,
                    fileName);
            using var file =
                File.Create(path);
            using var archive =
                new ZipArchive(
                    file,
                    ZipArchiveMode.Create);
            RequestData request =
                CreateRequestData(
                    0,
                    0,
                    1,
                    false,
                    false);
            WriteTextEntry(
                archive,
                "raw/0001_c.txt",
                request.RequestText,
                Encoding.ASCII);
            WriteTextEntry(
                archive,
                "raw/0001_s.txt",
                request.ResponseText,
                Encoding.ASCII);
            WriteTextEntry(
                archive,
                "raw/0001_m.xml",
                CreateMetadata(
                    1,
                    "10.0.0.1"),
                Encoding.UTF8);
            WriteTextEntry(
                archive,
                "raw/0002_c.txt",
                request.RequestText,
                Encoding.ASCII);
            return path;
        }

        internal static string WriteIncompleteSaz(
            string directory,
            string fileName)
        {
            Directory.CreateDirectory(directory);
            string path =
                Path.Combine(
                    directory,
                    fileName);
            using var file =
                File.Create(path);
            using var archive =
                new ZipArchive(
                    file,
                    ZipArchiveMode.Create);
            RequestData request =
                CreateRequestData(
                    0,
                    0,
                    1,
                    false,
                    false);
            WriteTextEntry(
                archive,
                "raw/0001_c.txt",
                request.RequestText,
                Encoding.ASCII);
            return path;
        }

        private static RequestData CreateRequestData(
            int group,
            int request,
            int requestsInGroup,
            bool includeCanaries,
            bool includeXr049Violation)
        {
            var canaryHostIndex = includeXr049Violation && requestsInGroup > 1
                ? 1
                : 0;
            var host = includeCanaries && request == canaryHostIndex
                ? $"{Canary}.example.invalid"
                : PresenceHost;
            var xuid = includeCanaries
                ? Canary
                : $"{group + 1:D4}{request + 1:D4}";
            var query = includeCanaries ? Canary : $"q{request + 1}";
            var pathAndQuery =
                $"/users/xuid({xuid})/devices/current/titles/current?token={query}";
            var bodyText = includeXr049Violation
                ? "{\"state\":\"online\"}"
                : includeCanaries
                    ? $"{{\"activity\":{{}},\"payload\":\"{Canary}\"}}"
                    : "{\"activity\":{}}";
            var requestBody = Encoding.UTF8.GetBytes(bodyText);
            var requestHeaders =
                $"POST https://{host}{pathAndQuery} HTTP/1.1\r\n" +
                $"Host: {host}\r\n" +
                "User-Agent: XboxServicesAPI\r\n" +
                "x-xbl-contract-version: 1\r\n" +
                (includeCanaries
                    ? $"Authorization: XBL3.0 x={Canary}\r\nCookie: session={Canary}\r\n"
                    : string.Empty) +
                "Content-Type: application/json\r\n" +
                $"Content-Length: {requestBody.Length}\r\n";
            var responseBody = Encoding.UTF8.GetBytes("{\"ok\":true}");
            var responseHeaders =
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: application/json\r\n" +
                $"Content-Length: {responseBody.Length}\r\n";
            return new RequestData(
                host,
                pathAndQuery,
                requestHeaders,
                requestBody,
                responseHeaders,
                responseBody);
        }

        private static string CreateMetadata(int frame, string clientIp)
        {
            var started = SyntheticStartUtc.AddSeconds(frame);
            var completed = started.AddMilliseconds(25);
            return
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                $"<Session SID=\"{frame}\" BitFlags=\"2100\">" +
                $"<SessionTimers ClientBeginRequest=\"{started:o}\" " +
                $"ClientDoneResponse=\"{completed:o}\" />" +
                "<SessionFlags>" +
                $"<SessionFlag N=\"x-clientip\" V=\"{clientIp}\" />" +
                "</SessionFlags>" +
                "</Session>";
        }

        private static string ReplaceStartLine(
            string message,
            string replacement)
        {
            int lineEnd = message.IndexOf("\r\n", StringComparison.Ordinal);
            return replacement + message.Substring(lineEnd);
        }

        private static string AddHeaderBeforeBody(
            string message,
            string header)
        {
            int separator = message.IndexOf(
                "\r\n\r\n",
                StringComparison.Ordinal);
            return message.Insert(
                separator + 2,
                header + "\r\n");
        }

        private static string RemoveHeader(
            string message,
            string headerPrefix)
        {
            int start = message.IndexOf(
                "\r\n" + headerPrefix,
                StringComparison.Ordinal) + 2;
            int end = message.IndexOf(
                "\r\n",
                start,
                StringComparison.Ordinal) + 2;
            return message.Remove(
                start,
                end - start);
        }

        private static string ReplaceHeader(
            string message,
            string headerPrefix,
            string replacement)
        {
            int start = message.IndexOf(
                "\r\n" + headerPrefix,
                StringComparison.Ordinal) + 2;
            int end = message.IndexOf(
                "\r\n",
                start,
                StringComparison.Ordinal);
            return message.Remove(
                    start,
                    end - start)
                .Insert(
                    start,
                    replacement);
        }

        private static void WriteTextEntry(
            ZipArchive archive,
            string name,
            string text,
            Encoding encoding)
        {
            WriteBytesEntry(archive, name, encoding.GetBytes(text));
        }

        private static void WriteBytesEntry(
            ZipArchive archive,
            string name,
            byte[] bytes)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(SyntheticStartUtc);
            using var stream = entry.Open();
            stream.Write(bytes, 0, bytes.Length);
        }

        private static byte[] CompressGzip(
            byte[] value)
        {
            using var output =
                new MemoryStream();
            using (var gzip =
                new GZipStream(
                    output,
                    CompressionLevel.Optimal,
                    true))
            {
                gzip.Write(
                    value,
                    0,
                    value.Length);
            }
            return output.ToArray();
        }

        private static int FindEndOfCentralDirectory(byte[] bytes)
        {
            for (var index = bytes.Length - 22; index >= 0; index--)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(
                        bytes.AsSpan(index, 4)) == 0x06054b50 &&
                    index + 22 +
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        bytes.AsSpan(index + 20, 2)) == bytes.Length)
                {
                    return index;
                }
            }
            throw new InvalidOperationException(
                "The synthetic ZIP has no end record.");
        }

        private static void ValidateCounts(int groups, int requests)
        {
            if (groups <= 0 || requests <= 0)
            {
                throw new ArgumentOutOfRangeException();
            }
        }

        private sealed record RequestData(
            string Host,
            string PathAndQuery,
            string RequestHeaders,
            byte[] RequestBody,
            string ResponseHeaders,
            byte[] ResponseBody)
        {
            internal string RequestText =>
                RequestHeaders + "\r\n" + Encoding.ASCII.GetString(RequestBody);

            internal string ResponseText =>
                ResponseHeaders + "\r\n" + Encoding.ASCII.GetString(ResponseBody);
        }
    }
}
