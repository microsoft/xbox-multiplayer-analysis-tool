// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Text;
using System.IO;
using System.IO.Compression;
using System.Net;
using XMAT;

namespace CaptureAnalysisEngine
{
    public class ServiceCallItem
    {
        public uint Id { get; set; }
        public string BreadCrumb { get; set; }
        public string Host { get; set; }
        public string Uri { get; set; }
        public string XboxUserId { get; set; }
        public string ReqHeader { get; set; }
        public string ReqBody { get; set; }
        public string RspHeader { get; set; }
        public string RspBody { get; set; }
        public string ConsoleIP { get; set; }
        public uint HttpStatusCode { get; set; }
        public ulong ReqBodyHash { get; set; }
        public ulong ElapsedCallTimeMs { get; set; }
        public ulong ReqTimeUTC { get; set; }
        public string Method { get; set; }
        public string MultiplayerCorrelationId { get; set; }
        public string PlayerSessionId { get; set; }
        public string EventName { get; set; }
        public string Dimensions { get; set; }
        public string Measurements { get; set; }
        public bool IsShoulderTap { get; set; }
        public bool IsInGameEvent { get; set; }

        // note: filled in automatically by the rules engine
        internal Tuple<string, string, string> m_xsapiMethods;

        private static UInt32 s_id = 0;

        public string Stringify()
        {
            return string.Join(",",
                Id,
                Host,
                Uri,
                XboxUserId,
                ConsoleIP,
                HttpStatusCode,
                Method,
                MultiplayerCorrelationId,
                PlayerSessionId,
                EventName,
                Dimensions,
                Measurements,
                IsShoulderTap,
                IsInGameEvent
            );
        }

        public ServiceCallItem() : this(s_id)
        {
        }

        public ServiceCallItem(UInt32 id)
        {
            Id = id;
            BreadCrumb = Guid.NewGuid().ToString();
            ConsoleIP = string.Empty;
            Reset();

            s_id = Math.Max(s_id, id + 1);
        }

        void Reset()
        {
            HttpStatusCode = 0;
            ReqBodyHash = 0;
            ElapsedCallTimeMs = 0;
            ReqTimeUTC = 0;

            IsShoulderTap = false;
            IsInGameEvent = false;
        }

        public ServiceCallItem Copy()
        {
            var copy = new ServiceCallItem();

            copy.BreadCrumb = BreadCrumb;

            // note: stuff that is part of API call metadata
            copy.Host = Host;
            copy.Uri = Uri;
            copy.XboxUserId = XboxUserId;
            copy.MultiplayerCorrelationId = MultiplayerCorrelationId;
            copy.ReqHeader = ReqHeader;
            copy.ReqBody = ReqBody;
            copy.RspHeader = RspHeader;
            copy.RspBody = RspBody;
            copy.ConsoleIP = ConsoleIP;
            copy.HttpStatusCode = HttpStatusCode;
            copy.ReqBodyHash = ReqBodyHash;
            copy.ElapsedCallTimeMs = ElapsedCallTimeMs;
            copy.ReqTimeUTC = ReqTimeUTC;
            copy.Method = Method;

            // note: stuff filled in by ServiceCallData
            copy.m_xsapiMethods = m_xsapiMethods;
            copy.EventName = EventName;
            copy.PlayerSessionId = PlayerSessionId;
            copy.Dimensions = Dimensions;
            copy.IsShoulderTap = IsShoulderTap;
            copy.IsInGameEvent = IsInGameEvent;

            return copy;
        }

        #region Factory Methods
        public static ServiceCallItem FromFiddlerFrame(UInt32 frameId, ZipArchiveEntry cFileStream, ZipArchiveEntry mFileStream, ZipArchiveEntry sFileStream, Func<WebHeaderCollection, bool> filterCallback)
        {
            ServiceCallItem frame = new ServiceCallItem();
            frame.Id = frameId;

            // Read the client part of the frame (###_c.txt)
            using (var cFileMemory = Utils.DecompressToMemory(cFileStream))
            {
                using (var cFile = new BinaryReader(cFileMemory))
                {
                    var fileLine = cFile.ReadLine();

                    var firstLineSplit = fileLine.Split(' ');

                    // CONNECT Frames should not be in the analysis.
                    if (firstLineSplit[0] == "CONNECT")
                    {
                        PublicUtilities.AppLog(LogLevel.INFO, "CONNECT Frames should not be in the analysis.");
                        return null;
                    }

                    // Fiddler Test Frames can cause LTA to break.  This filters out those fames.
                    if (firstLineSplit[1].StartsWith("http:///", true, null))
                    {
                        PublicUtilities.AppLog(LogLevel.INFO, "Fiddler Test Frames should not be in the analysis.");
                        return null;
                    }

                    frame.Method = firstLineSplit[0];

                    // Extract the XUID (if any) from the first line of the client side of the frame
                    // POST https://userpresence.xboxlive.com/users/xuid(2669321029139235)/devices/current HTTP/1.1	
                    frame.XboxUserId = Utils.GetXboxUserID(firstLineSplit[1]);

                    // Grab just the url from the line
                    frame.Uri = firstLineSplit[1];

                    // Read the Request Headers
                    fileLine = cFile.ReadLine();
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

                    PublicUtilities.AppLog(LogLevel.INFO, "Analyzing " + frame.Uri);
                    // Filter calls with headers
                    if (filterCallback!= null && !filterCallback(reqHeaders))
                    {
                        return null;
                    }

                    frame.Host = reqHeaders["Host"];

                    // Read the Request Body
                    string contentEncoding = reqHeaders["Content-Encoding"];
                    if (!string.IsNullOrWhiteSpace(contentEncoding) && contentEncoding.Equals("deflate", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var memory = Utils.InflateData(cFile.ReadToEnd()))
                        {
                            using (var data = new BinaryReader(memory))
                            {
                                fileLine = Encoding.ASCII.GetString(data.ReadToEnd());
                            }
                        }
                    }
                    else
                    {
                        fileLine = Encoding.ASCII.GetString(cFile.ReadToEnd());
                    }

                    frame.ReqHeader = reqHeaders.ToString();
                    frame.ReqBody = fileLine;
                    frame.ReqBodyHash = (UInt64)frame.ReqBody.GetHashCode();
                }
            }

            // Read the frame metadata (###_m.xml)
            using(var mFile = new StreamReader(mFileStream.Open()))
            {
                string rawData = mFile.ReadToEnd();
                var xmldata = System.Xml.Linq.XDocument.Parse(rawData);

                var sessionTimers = xmldata.Element("Session").Element("SessionTimers");
                var reqTime = DateTime.Parse((string)sessionTimers.Attribute("ClientBeginRequest")).ToUniversalTime();
                frame.ReqTimeUTC = (UInt64)reqTime.ToFileTimeUtc();

                var endTime = DateTime.Parse((string)sessionTimers.Attribute("ClientDoneResponse")).ToUniversalTime();
                frame.ElapsedCallTimeMs = (UInt64)(endTime - reqTime).TotalMilliseconds;

                var sessionFlags = xmldata.Element("Session").Element("SessionFlags");
                
                foreach(var flag in sessionFlags.Descendants())
                {
                    if((string)flag.Attribute("N") == "x-clientip")
                    {
                        frame.ConsoleIP = (string)flag.Attribute("V");
                        frame.ConsoleIP = frame.ConsoleIP.Substring(frame.ConsoleIP.LastIndexOf(':') + 1);
                        break;
                    }
                }
            }

            //Read the server part of the frame(###_s.txt)
            using (var sFileMemory = Utils.DecompressToMemory(sFileStream))
            {
                using (var sFile = new BinaryReader(sFileMemory))
                {
                    var fileLine = sFile.ReadLine();

                    if (string.IsNullOrEmpty(fileLine) == false)
                    {
                        var statusCodeLine = fileLine.Split(' ');

                        frame.HttpStatusCode = UInt32.Parse(statusCodeLine[1]);
                    }

                    // Read the Response Headers
                    var headers = new WebHeaderCollection();
                    fileLine = sFile.ReadLine();
                    while (!string.IsNullOrWhiteSpace(fileLine))
                    {
                        headers.Add(fileLine);
                        fileLine = sFile.ReadLine();
                    }

                    // Read the Response Body
                    string contentEncoding = headers["Content-Encoding"];
                    if (!string.IsNullOrWhiteSpace(contentEncoding) && contentEncoding.Equals("deflate", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var memory = Utils.InflateData(sFile.ReadToEnd()))
                        {
                            using (var data = new BinaryReader(memory))
                            {
                                fileLine = Encoding.ASCII.GetString(data.ReadToEnd());
                            }
                        }
                    }
                    else
                    {
                        fileLine = Encoding.ASCII.GetString(sFile.ReadToEnd());
                    }

                    frame.RspHeader = headers.ToString();

                    // Read the Response Body
                    frame.RspBody = fileLine;
                }
            }

            return frame;
        }
        #endregion
    }
}
