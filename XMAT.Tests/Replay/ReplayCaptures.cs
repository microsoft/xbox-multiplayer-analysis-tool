// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.IO.Compression;
using System.Text;
using XMAT.DataAbstractionLayer;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Replay
{
    // One captured exchange for a replay test. Url points at a loopback test server; for CONNECT only its host and port
    // count. Headers holds complete header lines ("Name: value\r\n"); a Host line replaces the default Host, which is
    // Uri.Host and therefore has no IPv6 zone. Number replaces the request number that an XMAT capture would give it
    // (its position from 1). WriteXmatcap refuses a zoned URL, and both writers refuse a zoned CONNECT, because Uri.Host
    // would erase the zone; a test of a scoped stored row must write RequestHost explicitly.
    internal sealed record CapturedExchange(
        string Method,
        string Url,
        string Headers = "",
        byte[] Body = null,
        string ResponseHead = "HTTP/1.1 200 OK\r\n",
        byte[] ResponseBody = null,
        long? Number = null);

    // Capture files whose requests target loopback servers, so replay tests never reach real services.
    internal static class ReplayCaptures
    {
        private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // A DNS host name of exactly this many characters, of 'a' labels no longer than 63 characters, which Uri accepts
        // whatever its length.
        internal static string LongDnsName(int length)
        {
            char[] name = new char[length];
            for (int index = 0; index < length; index++)
            {
                name[index] = index % 63 == 62 && index < length - 1 ? '.' : 'a';
            }

            return new string(name);
        }

        // A Fiddler archive whose frames are numbered from 1, in order.
        internal static string WriteSaz(string directory, string fileName, params CapturedExchange[] exchanges)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Join(directory, fileName);
            using FileStream file = File.Create(path);
            using var archive = new ZipArchive(file, ZipArchiveMode.Create);
            for (int index = 0; index < exchanges.Length; index++)
            {
                int frame = index + 1;
                CapturedExchange exchange = exchanges[index];
                Write(archive, $"raw/{frame:D4}_c.txt", Join(RequestHead(exchange), exchange.Body));
                Write(archive, $"raw/{frame:D4}_s.txt", Join(exchange.ResponseHead, exchange.ResponseBody));
                Write(archive, $"raw/{frame:D4}_m.xml", Encoding.UTF8.GetBytes(Metadata(frame)));
            }

            return path;
        }

        // An XMAT capture with these devices, each numbering its requests from 1.
        internal static string WriteXmatcap(
            string directory, string fileName, params (string Device, CapturedExchange[] Exchanges)[] devices)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Join(directory, fileName);
            using var database = new SqLtDatabase();
            database.Initialize();
            var deviceTable = database.CreateTable(
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
            foreach ((string device, CapturedExchange[] exchanges) in devices)
            {
                deviceTable.AddRow((long)DeviceType.XboxConsole, device, (long)CaptureType.WebProxy);
                for (int index = 0; index < exchanges.Length; index++)
                {
                    CapturedExchange exchange = exchanges[index];
                    var uri = new Uri(exchange.Url);
                    if (uri.HostNameType == UriHostNameType.IPv6 &&
                        uri.DnsSafeHost.Contains('%'))
                    {
                        throw new ArgumentException(
                            "Uri.Host drops an IPv6 zone; write the scoped host into the stored row explicitly.",
                            nameof(devices));
                    }

                    DateTime started = Start.AddSeconds(index + 1);
                    long number = exchange.Number ?? index + 1;
                    captures.AddRow(
                        device,
                        number,
                        (long)(index + 1),
                        ProxyConnectionModel.ToDateTimeString(started),
                        ProxyConnectionModel.ToDateTimeString(started.AddMilliseconds(25)),
                        uri.Scheme,
                        uri.Host,
                        uri.Port.ToString(CultureInfo.InvariantCulture),
                        exchange.Method,
                        uri.PathAndQuery,
                        exchange.ResponseHead.Split(' ')[1],
                        RequestHead(exchange),
                        Convert.ToBase64String(exchange.Body ?? Array.Empty<byte>()),
                        exchange.ResponseHead,
                        Convert.ToBase64String(exchange.ResponseBody ?? Array.Empty<byte>()),
                        "10.0.0.1");
                }
            }

            database.SaveToFile(path);
            return path;
        }

        private static string RequestHead(CapturedExchange exchange)
        {
            var uri = new Uri(exchange.Url);
            if (exchange.Method == "CONNECT" &&
                uri.HostNameType == UriHostNameType.IPv6 &&
                uri.DnsSafeHost.Contains('%'))
            {
                throw new ArgumentException(
                    "A scoped CONNECT fixture must provide its lexical authority without passing through Uri.Host.",
                    nameof(exchange));
            }

            // The default Host models what an RFC 6874 client sends: Uri.Host's bracketed IPv6 address without its local
            // zone. An explicitly supplied Host replaces it, so absolute-target precedence tests write exactly one Host.
            string authority = $"{uri.Host}:{uri.Port.ToString(CultureInfo.InvariantCulture)}";
            string target = exchange.Method == "CONNECT" ? authority : exchange.Url;
            string headers = HasHeader(exchange.Headers, "Host")
                ? exchange.Headers
                : $"Host: {authority}\r\n{exchange.Headers}";
            return $"{exchange.Method} {target} HTTP/1.1\r\n{headers}";
        }

        private static bool HasHeader(string headers, string expected)
        {
            foreach (string line in (headers ?? string.Empty).Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = line.IndexOf(':');
                if (colon > 0 &&
                    line.AsSpan(0, colon).Trim().Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static byte[] Join(string head, byte[] body)
        {
            var message = new MemoryStream();
            message.Write(Encoding.Latin1.GetBytes(head + "\r\n"));
            message.Write(body ?? Array.Empty<byte>());
            return message.ToArray();
        }

        private static string Metadata(int frame)
        {
            DateTime started = Start.AddSeconds(frame);
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                $"<Session SID=\"{frame}\" BitFlags=\"2100\">" +
                $"<SessionTimers ClientBeginRequest=\"{started:o}\" ClientDoneResponse=\"{started.AddMilliseconds(25):o}\" />" +
                "<SessionFlags><SessionFlag N=\"x-clientip\" V=\"10.0.0.1\" /></SessionFlags>" +
                "</Session>";
        }

        private static void Write(ZipArchive archive, string name, byte[] bytes)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(Start);
            using Stream stream = entry.Open();
            stream.Write(bytes, 0, bytes.Length);
        }
    }
}
