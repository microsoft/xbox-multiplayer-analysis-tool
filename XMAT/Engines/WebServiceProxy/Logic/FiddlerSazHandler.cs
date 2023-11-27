// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Xml.Linq;
using XMAT.WebServiceCapture.Models;

namespace XMAT.WebServiceCapture
{
    public static class FiddlerSazHandler
    {
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
            if(cFileStream != null)
            {
                using (MemoryStream cFileMemory = PublicUtilities.DecompressZipEntryToMemory(cFileStream))
                {
                    using (var cFile = new BinaryReader(cFileMemory))
                    {
                        var firstLine = cFile.ReadLine();

                        if (!string.IsNullOrEmpty(firstLine))
                        {
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
                                if(Uri.TryCreate(firstLineSplit[1], UriKind.Absolute, out Uri uri))
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

                        // Read the Request Headers
                        var fileLine = cFile.ReadLine();
                        var reqHeaders = new WebHeaderCollection();
                        while (string.IsNullOrWhiteSpace(fileLine) == false)
                        {
                            try
                            {
                                reqHeaders.Add(fileLine);
                            }
                            catch (Exception)
                            {
                                // This will throw if a header value contains invalid characters
                                // Ignore and continue
                            }

                            fileLine = cFile.ReadLine();
                        }

                        // HOST
                        hostAndPort = reqHeaders["Host"];

                        // REQUEST BODY
                        requestBody = cFile.ReadToEnd();

                        // REQUEST HEADERS
                        requestHeaders = $"{firstLine}\r\n{reqHeaders}";
                    }
                }
            }

            // Read the server part of the frame(###_s.txt)
            if(sFileStream != null)
            {
                using (MemoryStream sFileMemory = PublicUtilities.DecompressZipEntryToMemory(sFileStream))
                {
                    using (var sFile = new BinaryReader(sFileMemory))
                    {
                        var firstLine = sFile.ReadLine();

                        if (!string.IsNullOrEmpty(firstLine))
                        {
                            var firstLineSplit = firstLine.Split(' ');

                            // STATUS
                            status = firstLineSplit[1];
                        }

                        // Read the Response Headers
                        var headers = new WebHeaderCollection();
                        var fileLine = sFile.ReadLine();
                        while (!string.IsNullOrWhiteSpace(fileLine))
                        {
                            headers.Add(fileLine);
                            fileLine = sFile.ReadLine();
                        }

                        // RESPONSE BODY
                        responseBody = sFile.ReadToEnd();

                        // RESPONSE HEADERS
                        responseHeaders = $"{firstLine}\r\n{headers}";
                    }
                }
            }

            // Read the frame metadata (###_m.xml)
            if(mFileStream != null)
            {
                using (var mFile = new StreamReader(mFileStream.Open()))
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
            var list = new ProxyConnectionsCollection();

            // Open the SAZ
            using (var archive = ZipFile.Open(filename, ZipArchiveMode.Read))
            {
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

                    ZipArchiveEntry requestFileArchive  = null;
                    ZipArchiveEntry responseFileArchive = null;
                    ZipArchiveEntry metaFileArchive     = null;

                    // Grab the individual files
                    foreach(ZipArchiveEntry file in group.Files)
                    {
                        if(file.Name.EndsWith(EntryTypeMap[EntryType.Request]))
                        {
                            requestFileArchive = file;
                        }
                        else if(file.Name.EndsWith(EntryTypeMap[EntryType.Response]))
                        {
                            responseFileArchive = file;
                        }
                        else if(file.Name.EndsWith(EntryTypeMap[EntryType.Meta]))
                        {
                            metaFileArchive = file;
                        }
                    }

                    var proxyConnectionModel = FromFiddlerFrame((UInt32)group.Frame, requestFileArchive, metaFileArchive, responseFileArchive);

                    // skip if the frame could not be converted
                    if (proxyConnectionModel == null)
                    {
                        continue;
                    }

                    list.Add(proxyConnectionModel);
                }
            }

            return list;
        }

        public static void CreateSazFile(string filename, IEnumerable<ProxyConnectionModel> records)
        {
            // always overwrite
            ZipArchive archive = new ZipArchive(File.Create(filename), ZipArchiveMode.Update);

            foreach(var record in records)
            {
                AddEntry(archive, EntryType.Request, record.RequestNumber, record.RequestLineAndHeaders, record.RequestBody);

                var metaData = new FiddlerMetaData(record.RecordRowId);
                metaData.SetClientIP(record.ClientIP);
                metaData.ClientBeginRequest = record.RequestTime;
                metaData.ClientDoneResponse = record.RequestTime + record.Duration;
                AddMetaEntry(archive, record.RequestNumber, metaData);

                if(!string.IsNullOrEmpty(record.ResponseLineAndHeaders))
                {
                    AddEntry(archive, EntryType.Response, record.RequestNumber, record.ResponseLineAndHeaders, record.ResponseBody);
                }
            }
            archive.Dispose();
        }

        private static void AddMetaEntry(ZipArchive archive, long id, FiddlerMetaData metaDataToSerialize)
        {
            ZipArchiveEntry entry = archive.CreateEntry($"raw/{id}{EntryTypeMap[EntryType.Meta]}");
            Stream s = entry.Open();
            string xmlText = metaDataToSerialize.ToXmlString();
            var xmlTextBytes = Encoding.UTF8.GetBytes(xmlText);
            s.Write(xmlTextBytes);
            s.Close();
        }

        private static void AddEntry(ZipArchive archive, EntryType entryType, long id, string headers, byte[] body)
        {
            ZipArchiveEntry entry = archive.CreateEntry($"raw/{id}{EntryTypeMap[entryType]}");
            Stream s = entry.Open();
            var headerBytes = Encoding.ASCII.GetBytes(headers);
            s.Write(headerBytes);
            s.Write(body);
            s.Close();
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
                foreach(var sessionFlag in SessionFlags)
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
