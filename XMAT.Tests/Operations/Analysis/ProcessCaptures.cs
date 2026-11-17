// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using XMAT.DataAbstractionLayer;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Operations.Analysis
{
    // One session of a process-aware synthetic capture, stored with its ClientProcess* values as given. Analyzable
    // sessions are Xbox Live calls; the others are web traffic that the analysis skips but the process scope counts. An
    // empty HttpStatus stores a request without a recorded response.
    internal sealed record ProcessSession(
        string ProcessName,
        long ProcessId,
        string Status,
        bool Analyzable = true,
        string Device = ProcessCaptures.Device,
        string StartTimeUtc = "",
        string HttpStatus = "200",
        string PairMarker = null);

    // One frame of a process-aware synthetic SAZ. The process flags are written exactly as given (a null value writes
    // no flag), so tests can vary the flag spelling.
    internal sealed record ProcessSazFrame(
        string ProcessInfoFlag,
        string ProcessInfo,
        string StartTimeUtc = null,
        string Status = null,
        bool Analyzable = true);

    internal static class ProcessCaptures
    {
        internal const string Device = "Process capture PC";
        internal const string ClientIp = "127.0.0.1";
        internal const string SazClientIp = "10.0.0.1";
        private const string PresenceHost = "userpresence.xboxlive.com";
        private static readonly DateTime StartUtc = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        // Game.exe (3 calls and 1 web page), svchost.exe (2 calls), one session without process information and one
        // from a remote device: 7 calls in 8 sessions. Row ids are 1 to 8 in this order.
        internal static IReadOnlyList<ProcessSession> MixedSessions { get; } = new[]
        {
            new ProcessSession("Game.exe", 4100, "local"),
            new ProcessSession("Game.exe", 4100, "local"),
            new ProcessSession("svchost.exe", 900, "local"),
            new ProcessSession("Game.exe", 4100, "local", Analyzable: false),
            new ProcessSession(string.Empty, 0, string.Empty),
            new ProcessSession("svchost.exe", 900, "local"),
            new ProcessSession(string.Empty, 0, "remote"),
            new ProcessSession("Game.exe", 4100, "local"),
        };

        internal static IReadOnlyList<long> MixedGameRowIds { get; } = new long[] { 1, 2, 4, 8 };

        internal static string WriteXmatcap(
            string directory,
            string fileName,
            IReadOnlyList<ProcessSession> sessions)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Join(directory, fileName);
            using var database = new SqLtDatabase();
            database.Initialize();
            IDataTable devices = database.CreateTable(
                "CaptureDevices",
                new Field<long>("DeviceType"),
                new Field<string>("DeviceName"),
                new Field<long>("CaptureType"));
            // The current capture schema; each row below lists its values in the schema's column order.
            IDataTable captures = database.CreateTable(
                WebServiceCaptureMethod.TableName,
                WebProxyCaptureSchema.CreateFields());

            foreach (string device in sessions.Select(session => session.Device).Distinct(StringComparer.Ordinal))
            {
                devices.AddRow((long)DeviceType.LocalPC, device, (long)CaptureType.WebProxy);
            }

            for (var index = 0; index < sessions.Count; index++)
            {
                ProcessSession session = sessions[index];
                long number = index + 1;
                Message message = CreateMessage(number, session.Analyzable);
                if (session.PairMarker is string marker)
                {
                    message = message with
                    {
                        RequestHeaders = message.RequestHeaders + $"X-Marker: request-{marker}\r\n",
                        RequestBody = Encoding.UTF8.GetBytes($"request-body-{marker}"),
                        ResponseHeaders = message.ResponseHeaders + $"X-Marker: response-{marker}\r\n",
                        ResponseBody = Encoding.UTF8.GetBytes($"response-body-{marker}"),
                    };
                }
                DateTime started = StartUtc.AddSeconds(number);
                bool responded = session.HttpStatus.Length != 0;
                captures.AddRow(
                    session.Device,
                    number,
                    number,
                    ProxyConnectionModel.ToDateTimeString(started),
                    responded ? ProxyConnectionModel.ToDateTimeString(started.AddMilliseconds(25)) : string.Empty,
                    "https",
                    message.Host,
                    "443",
                    message.Method,
                    message.PathAndQuery,
                    session.HttpStatus,
                    message.RequestHeaders,
                    Convert.ToBase64String(message.RequestBody),
                    responded ? message.ResponseHeaders : string.Empty,
                    responded ? Convert.ToBase64String(message.ResponseBody) : string.Empty,
                    ClientIp,
                    session.ProcessName ?? string.Empty,
                    session.ProcessId,
                    session.StartTimeUtc ?? string.Empty,
                    session.Status ?? string.Empty);
            }

            database.SaveToFile(path);
            return path;
        }

        internal static string WriteSaz(
            string directory,
            string fileName,
            IReadOnlyList<ProcessSazFrame> frames)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Join(directory, fileName);
            using (FileStream file = File.Create(path))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                for (var index = 0; index < frames.Count; index++)
                {
                    ProcessSazFrame frame = frames[index];
                    int number = index + 1;
                    Message message = CreateMessage(number, frame.Analyzable);
                    WriteEntry(archive, $"raw/{number:D4}_c.txt", Encoding.ASCII.GetBytes(message.RequestText));
                    WriteEntry(archive, $"raw/{number:D4}_s.txt", Encoding.ASCII.GetBytes(message.ResponseText));
                    WriteEntry(archive, $"raw/{number:D4}_m.xml", Encoding.UTF8.GetBytes(Metadata(number, Flags(frame))));
                }
            }

            return path;
        }

        // A frame's metadata built with the XML APIs, so any flag name or value stays well formed.
        internal static string Metadata(int number, IEnumerable<KeyValuePair<string, string>> flags)
        {
            DateTime started = StartUtc.AddSeconds(number);
            var session = new XElement(
                "Session",
                new XAttribute("SID", number),
                new XAttribute("BitFlags", "2100"),
                new XElement(
                    "SessionTimers",
                    new XAttribute("ClientBeginRequest", started.ToString("o", CultureInfo.InvariantCulture)),
                    new XAttribute(
                        "ClientDoneResponse",
                        started.AddMilliseconds(25).ToString("o", CultureInfo.InvariantCulture))),
                new XElement(
                    "SessionFlags",
                    flags.Select(flag => new XElement(
                        "SessionFlag",
                        new XAttribute("N", flag.Key),
                        new XAttribute("V", flag.Value)))));
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + session.ToString(SaveOptions.DisableFormatting);
        }

        // A request and response for FiddlerSazHandler.FromFiddlerFrame, with the given metadata.
        internal static ProxyConnectionModel ImportFrame(IEnumerable<KeyValuePair<string, string>> flags)
        {
            Message message = CreateMessage(1, analyzable: true);
            using var request = new MemoryStream(Encoding.ASCII.GetBytes(message.RequestText), writable: false);
            using var metadata = new MemoryStream(Encoding.UTF8.GetBytes(Metadata(1, flags)), writable: false);
            using var response = new MemoryStream(Encoding.ASCII.GetBytes(message.ResponseText), writable: false);
            return FiddlerSazHandler.FromFiddlerFrame(1, request, metadata, response);
        }

        // A complete, analyzable session for SAZ export.
        internal static ProxyConnectionModel ExportRecord(int number, string clientIp = SazClientIp)
        {
            Message message = CreateMessage(number, analyzable: true);
            DateTime started = StartUtc.AddSeconds(number);
            return new ProxyConnectionModel(
                number,
                "200",
                started,
                TimeSpan.FromMilliseconds(25),
                "https",
                PresenceHost + ":443",
                message.Method,
                message.PathAndQuery,
                message.RequestHeaders + "\r\n",
                message.RequestBody,
                message.ResponseHeaders + "\r\n",
                message.ResponseBody,
                clientIp)
            {
                RecordRowId = number,
            };
        }

        internal static Dictionary<string, byte[]> Entries(string path)
        {
            using ZipArchive archive = ZipFile.OpenRead(path);
            return archive.Entries.ToDictionary(
                entry => entry.FullName,
                entry =>
                {
                    using Stream input = entry.Open();
                    using var buffer = new MemoryStream();
                    input.CopyTo(buffer);
                    return buffer.ToArray();
                },
                StringComparer.Ordinal);
        }

        // The SessionFlag elements of a frame's metadata, which must be well-formed XML.
        internal static IReadOnlyList<KeyValuePair<string, string>> SessionFlags(byte[] metadata)
        {
            XDocument document = XDocument.Parse(Encoding.UTF8.GetString(metadata));
            return document.Root.Element("SessionFlags").Elements("SessionFlag")
                .Select(flag => new KeyValuePair<string, string>((string)flag.Attribute("N"), (string)flag.Attribute("V")))
                .ToList();
        }

        private static IEnumerable<KeyValuePair<string, string>> Flags(ProcessSazFrame frame)
        {
            yield return new KeyValuePair<string, string>("x-clientip", SazClientIp);
            if (frame.ProcessInfo != null)
            {
                yield return new KeyValuePair<string, string>(frame.ProcessInfoFlag, frame.ProcessInfo);
            }

            if (frame.StartTimeUtc != null)
            {
                yield return new KeyValuePair<string, string>("x-xmat-clientprocess-starttime", frame.StartTimeUtc);
            }

            if (frame.Status != null)
            {
                yield return new KeyValuePair<string, string>("x-xmat-clientprocess-status", frame.Status);
            }
        }

        private static void WriteEntry(ZipArchive archive, string name, byte[] bytes)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using Stream stream = entry.Open();
            stream.Write(bytes);
        }

        private static Message CreateMessage(long number, bool analyzable)
        {
            byte[] responseBody = Encoding.UTF8.GetBytes("{\"ok\":true}");
            string responseHeaders =
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: application/json\r\n" +
                $"Content-Length: {responseBody.Length}\r\n";
            if (!analyzable)
            {
                string page = $"/page/{number}";
                return new Message(
                    "www.example.test",
                    "GET",
                    page,
                    $"GET https://www.example.test{page} HTTP/1.1\r\n" +
                    "Host: www.example.test\r\n" +
                    "User-Agent: Browser\r\n",
                    Array.Empty<byte>(),
                    responseHeaders,
                    responseBody);
            }

            string pathAndQuery = $"/users/xuid({number:D8})/devices/current/titles/current";
            byte[] requestBody = Encoding.UTF8.GetBytes("{\"activity\":{}}");
            return new Message(
                PresenceHost,
                "POST",
                pathAndQuery,
                $"POST https://{PresenceHost}{pathAndQuery} HTTP/1.1\r\n" +
                $"Host: {PresenceHost}\r\n" +
                "User-Agent: XboxServicesAPI\r\n" +
                "x-xbl-contract-version: 1\r\n" +
                "Content-Type: application/json\r\n" +
                $"Content-Length: {requestBody.Length}\r\n",
                requestBody,
                responseHeaders,
                responseBody);
        }

        private sealed record Message(
            string Host,
            string Method,
            string PathAndQuery,
            string RequestHeaders,
            byte[] RequestBody,
            string ResponseHeaders,
            byte[] ResponseBody)
        {
            internal string RequestText => RequestHeaders + "\r\n" + Encoding.ASCII.GetString(RequestBody);

            internal string ResponseText => ResponseHeaders + "\r\n" + Encoding.ASCII.GetString(ResponseBody);
        }
    }
}
