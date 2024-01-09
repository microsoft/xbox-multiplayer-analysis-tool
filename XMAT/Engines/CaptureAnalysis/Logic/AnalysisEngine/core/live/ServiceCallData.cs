// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace CaptureAnalysisEngine
{
    public class ServiceCallData
    {   
        public class DataTelemetry
        {
            // How many calls were processed by LTA

            public int m_totalCalls;
            public int m_callsProcessed;
            public int m_callsSkipped
            {
                get
                {
                    return m_totalCalls - m_callsProcessed;
                }
            }
        }

        public class PerConsoleData
        {
            public Dictionary<String, LinkedList<ServiceCallItem>> m_servicesHistory = new Dictionary<string, LinkedList<ServiceCallItem>>();
            public Dictionary<String, ServiceCallStats> m_servicesStats = new Dictionary<string, ServiceCallStats>();
        }

        public static bool m_allEndpoints = false;

        public Dictionary<String, PerConsoleData> m_perConsoleData = new Dictionary<string, PerConsoleData>();
        public Dictionary<String, Tuple<String, String, String>> m_endpointToService;

        public DataTelemetry m_dataTelemetry = new DataTelemetry();

        public ServiceCallData(bool allEndpoints)
        {
            m_allEndpoints = allEndpoints;
        }

        public void CreateFromServiceCallItems(IEnumerable<ServiceCallItem> frameData)
        {
            var consoleGroups = from f in frameData
                                group f by f.ConsoleIP;

            foreach (var consoleFrames in consoleGroups.Where(g => g.Key != String.Empty))
            {
                var consoleData = new PerConsoleData();

                var xboxServiceFrames = consoleFrames.GroupBy(f => f.Host)
                                                     .Select(group => new { Host = group.Key, History = group.AsEnumerable() });

                consoleData.m_servicesHistory = xboxServiceFrames.ToDictionary(g => g.Host, g => new LinkedList<ServiceCallItem>(g.History.OrderBy(call => call.ReqTimeUTC)));

                // Xbox telemetry endpoint
                if (consoleData.m_servicesHistory.ContainsKey("data-vef.xboxlive.com"))
                {
                    ConvertCS1ToEvent(consoleData.m_servicesHistory);
                }

                // Windows Telemetry endpoint
                if (consoleData.m_servicesHistory.Any(k => k.Key.Contains(".data.microsoft.com")))
                {
                    ConvertCS2ToEvent(consoleData.m_servicesHistory);
                }

                // clear empty items
                consoleData.m_servicesHistory = consoleData.m_servicesHistory.Where(o => o.Value.Count > 0).ToDictionary(x => x.Key, x => x.Value);

                foreach (var endpoint in consoleData.m_servicesHistory)
                {
                    consoleData.m_servicesStats.Add(endpoint.Key, new ServiceCallStats(endpoint.Value));
                }

                m_perConsoleData.Add(consoleFrames.Key, consoleData);
            }
        }

        private void ConvertCS2ToEvent(Dictionary<string, LinkedList<ServiceCallItem>> servicesHistory)
        {
            var eventNameMatch1 = new Regex("Microsoft.XboxLive.T[a-zA-Z0-9]{8}.");
            var eventNameMatch2 = @"Microsoft.Xbox.XceBridge";

            var events = servicesHistory.Where(k => k.Key.Contains(".data.microsoft.com")).ToList();
            foreach (var endpoint in events)
            {
                servicesHistory.Remove(endpoint.Key);
            }
            LinkedList<ServiceCallItem> inGameEvents = null;
            if (servicesHistory.ContainsKey("inGameEvents"))
            {
                inGameEvents = servicesHistory["inGameEvents"];
            }
            else
            {
                inGameEvents = new LinkedList<ServiceCallItem>();
                servicesHistory.Add("inGameEvents", inGameEvents);
            }

            foreach (var eventCall in events.SelectMany(e => e.Value))
            {
                var requestBody = eventCall.ReqBody;

                // If there's nothing in the request body, then there was an error with the event and we can't parse it.
                if(string.IsNullOrEmpty(requestBody))
                {
                    continue;
                }

                var eventArray = requestBody.Split(Environment.NewLine.ToCharArray(), StringSplitOptions.RemoveEmptyEntries);

                foreach (var eventLine in eventArray)
                {
                    JsonElement requestBodyJson;

                    try
                    {
                        JsonDocument eventDocument = JsonDocument.Parse(eventLine);
                        requestBodyJson = eventDocument.RootElement;
                    }
                    catch (JsonException)
                    {
                        // TODO: for now swallow the bad json, but perhaps some indicator
                        // should exist for this
                        continue;
                    }

                    string eventName = string.Empty;
                    Utils.SafeAssign<string>(requestBodyJson, @"name", ref eventName);

                    if (eventNameMatch1.IsMatch(eventName) || eventNameMatch2.StartsWith(eventName))
                    {
                        var serviceCall = eventCall.Copy();
                        var eventNameParts = eventName.Split('.');

                        string timeAsString = string.Empty;
                        Utils.SafeAssign<string>(requestBodyJson, @"time", ref timeAsString);

                        serviceCall.Host = "inGameEvents";
                        serviceCall.EventName = eventNameParts.Last();
                        serviceCall.ReqTimeUTC = (UInt64)DateTime.Parse(timeAsString).ToFileTimeUtc();
                        serviceCall.ReqBody = String.Empty;

                        JsonElement dataObject;
                        if (requestBodyJson.TryGetProperty(@"data", out dataObject))
                        {
                            JsonElement baseDataObject;
                            if (dataObject.TryGetProperty(@"baseData", out baseDataObject))
                            {
                                //            var measurements = baseData["measurements"];
                                string measurements = string.Empty;
                                Utils.SafeAssign<string>(baseDataObject, @"mesasurements", ref measurements);
                                serviceCall.Measurements = measurements;

                                if (serviceCall.EventName.Contains("MultiplayerRoundStart") ||
                                    serviceCall.EventName.Contains("MultiplayerRoundEnd"))
                                {
                                    string playerSessionId = string.Empty;
                                    Utils.SafeAssign<string>(baseDataObject, @"playerSessionId", ref playerSessionId);

                                    JsonElement propertiesObject;
                                    if (baseDataObject.TryGetProperty(@"properties", out propertiesObject))
                                    {
                                        string multiplayerCorrelationId = string.Empty;
                                        Utils.SafeAssign<string>(propertiesObject, @"MultiplayerCorrelationId", ref multiplayerCorrelationId);
                                        serviceCall.MultiplayerCorrelationId = multiplayerCorrelationId;
                                    }
                                }
                            }
                        }
                        inGameEvents.AddLast(serviceCall);
                    }
                }
            }
        }

        private void ConvertCS1ToEvent(Dictionary<string, LinkedList<ServiceCallItem>> servicesHistory)
        {
            var events = servicesHistory["data-vef.xboxlive.com"];
            servicesHistory.Remove("data-vef.xboxlive.com");

            LinkedList<ServiceCallItem> inGameEvents = null;
            if (servicesHistory.ContainsKey("inGameEvents"))
            {
                inGameEvents = servicesHistory["inGameEvents"];
            }
            else
            {
                inGameEvents = new LinkedList<ServiceCallItem>();
                servicesHistory.Add("inGameEvents", inGameEvents);
            }

            // Event Name starts with a string in the form of {Publisher}_{TitleId}
            Regex eventNameMatch = new Regex("[a-zA-z]{4}_[a-zA-Z0-9]{8}");

            foreach(var eventCall in events)
            {
                var requestBody = eventCall.ReqBody;

                var eventArray = requestBody.Split(Environment.NewLine.ToCharArray());

                foreach(var eventLine in eventArray)
                {
                    var fields = eventLine.Split('|');

                    if(fields.Length < 12)
                    {
                        // This event is not valid as it is missing fields
                        continue;
                    }

                    // The name field is in the form of {Publisher}_{TitleId}.{EventName}
                    var eventNameParts = fields[1].Split('.');

                    if(eventNameParts.Length > 1 && eventNameMatch.IsMatch(eventNameParts[0]))
                    {
                        ServiceCallItem splitEvent = eventCall.Copy();

                        splitEvent.Host = "inGameEvents";
                        splitEvent.EventName = eventNameParts[1];
                        splitEvent.ReqTimeUTC = (UInt64)DateTime.Parse(fields[2]).ToFileTimeUtc();
                        splitEvent.ReqBody = String.Empty;
                        splitEvent.Dimensions = CS1PartBC(fields);
                        splitEvent.IsInGameEvent = true;
                        
                        if(splitEvent.EventName.Contains("MultiplayerRoundStart") || splitEvent.EventName.Contains("MultiplayerRoundEnd"))
                        {
                            splitEvent.PlayerSessionId = fields[15];
                            splitEvent.MultiplayerCorrelationId = fields[16];
                        }

                        inGameEvents.AddLast(splitEvent);
                    }
                }
            }
        }

        private static string CS1PartBC(string[] fields)
        {
            string result = "";

            for(int i = 1; i < fields.Length; ++i)
            {
                result += fields[i] + "|";
            }

            return result;
        }
    }
}
