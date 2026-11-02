// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

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
            // Buckets are keyed by kind and logical name so captured hosts never collide with
            // synthesized telemetry that shares a logical name.
            public Dictionary<EndpointKey, LinkedList<ServiceCallItem>> m_servicesHistory = new Dictionary<EndpointKey, LinkedList<ServiceCallItem>>();
            public Dictionary<EndpointKey, ServiceCallStats> m_servicesStats = new Dictionary<EndpointKey, ServiceCallStats>();
        }

        private const string InGameEventsEndpoint = "inGameEvents";
        private const string XboxTelemetryEndpoint = "data-vef.xboxlive.com";
        private const string WindowsTelemetryDomain = ".data.microsoft.com";
        private static readonly EndpointKey SyntheticInGameEvents = new(EndpointKind.SyntheticTelemetry, InGameEventsEndpoint);

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

                var xboxServiceFrames = consoleFrames.GroupBy(f => new EndpointKey(f.EndpointKind, f.Host ?? string.Empty))
                                                     .Select(group => new { Endpoint = group.Key, History = group.AsEnumerable() });

                consoleData.m_servicesHistory = xboxServiceFrames.ToDictionary(g => g.Endpoint, g => new LinkedList<ServiceCallItem>(g.History.OrderBy(call => call.ReqTimeUTC)));

                // Xbox telemetry endpoint
                if (consoleData.m_servicesHistory.Keys.Any(key => IsTelemetrySource(key, host => host == XboxTelemetryEndpoint)))
                {
                    ConvertCS1ToEvent(consoleData.m_servicesHistory);
                }

                // Windows Telemetry endpoint
                if (consoleData.m_servicesHistory.Keys.Any(key => IsTelemetrySource(key, IsWindowsTelemetryHost)))
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

        /// <summary>
        /// The physical host that host-list actions may target for an endpoint bucket: only a
        /// captured bucket whose every record has the same valid host qualifies (DNS names
        /// compared without regard to case, a final dot included; IP literals canonical).
        /// Synthesized, unknown, mixed or provenance-less buckets are not actionable, even when
        /// their logical name looks like a host name.
        /// </summary>
        public static string GetActionableHost(EndpointKey endpoint, IEnumerable<ServiceCallItem> calls)
        {
            if (endpoint.Kind != EndpointKind.Captured || calls == null)
            {
                return null;
            }

            string actionableHost = null;
            foreach (ServiceCallItem call in calls)
            {
                if (call.EndpointKind != EndpointKind.Captured ||
                    !XMAT.WebServiceCapture.HostAuthority.TryNormalizeHost(call.PhysicalHost, out string host))
                {
                    return null;
                }
                if (actionableHost == null)
                {
                    actionableHost = host;
                }
                else if (!string.Equals(actionableHost, host, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }
            return actionableHost;
        }

        // Raw telemetry is converted from any non-synthesized bucket with the telemetry host.
        private static bool IsTelemetrySource(EndpointKey endpoint, Func<string, bool> isTelemetryHost) =>
            endpoint.Kind != EndpointKind.SyntheticTelemetry && isTelemetryHost(endpoint.LogicalName);

        private static bool IsWindowsTelemetryHost(string host) =>
            host != null && host.Contains(WindowsTelemetryDomain);

        private static LinkedList<ServiceCallItem> GetSyntheticEvents(Dictionary<EndpointKey, LinkedList<ServiceCallItem>> servicesHistory)
        {
            if (!servicesHistory.TryGetValue(SyntheticInGameEvents, out LinkedList<ServiceCallItem> inGameEvents))
            {
                inGameEvents = new LinkedList<ServiceCallItem>();
                servicesHistory.Add(SyntheticInGameEvents, inGameEvents);
            }
            return inGameEvents;
        }

        private void ConvertCS2ToEvent(Dictionary<EndpointKey, LinkedList<ServiceCallItem>> servicesHistory)
        {
            var eventNameMatch1 = new Regex("Microsoft.XboxLive.T[a-zA-Z0-9]{8}.");
            var eventNameMatch2 = @"Microsoft.Xbox.XceBridge";

            var events = servicesHistory.Where(k => IsTelemetrySource(k.Key, IsWindowsTelemetryHost)).ToList();
            foreach (var endpoint in events)
            {
                servicesHistory.Remove(endpoint.Key);
            }
            LinkedList<ServiceCallItem> inGameEvents = GetSyntheticEvents(servicesHistory);

            foreach (var eventCall in events.SelectMany(e => e.Value))
            {
                var requestBody = eventCall.ReqBody;

                // If there's nothing in the request body, then there was an error with the event and we can't parse it.
                if (string.IsNullOrEmpty(requestBody))
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

                        serviceCall.Host = InGameEventsEndpoint;
                        serviceCall.EndpointKind = EndpointKind.SyntheticTelemetry;
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

        private void ConvertCS1ToEvent(Dictionary<EndpointKey, LinkedList<ServiceCallItem>> servicesHistory)
        {
            var sources = servicesHistory.Where(k => IsTelemetrySource(k.Key, host => host == XboxTelemetryEndpoint)).ToList();
            foreach (var source in sources)
            {
                servicesHistory.Remove(source.Key);
            }

            LinkedList<ServiceCallItem> inGameEvents = GetSyntheticEvents(servicesHistory);

            // Event Name starts with a string in the form of {Publisher}_{TitleId}
            Regex eventNameMatch = new Regex("[a-zA-z]{4}_[a-zA-Z0-9]{8}");

            foreach (var eventCall in sources.SelectMany(source => source.Value))
            {
                var requestBody = eventCall.ReqBody;

                var eventArray = requestBody.Split(Environment.NewLine.ToCharArray());

                foreach (var eventLine in eventArray)
                {
                    var fields = eventLine.Split('|');

                    if (fields.Length < 12)
                    {
                        // This event is not valid as it is missing fields
                        continue;
                    }

                    // The name field is in the form of {Publisher}_{TitleId}.{EventName}
                    var eventNameParts = fields[1].Split('.');

                    if (eventNameParts.Length > 1 && eventNameMatch.IsMatch(eventNameParts[0]))
                    {
                        ServiceCallItem splitEvent = eventCall.Copy();

                        splitEvent.Host = InGameEventsEndpoint;
                        splitEvent.EndpointKind = EndpointKind.SyntheticTelemetry;
                        splitEvent.EventName = eventNameParts[1];
                        splitEvent.ReqTimeUTC = (UInt64)DateTime.Parse(fields[2]).ToFileTimeUtc();
                        splitEvent.ReqBody = String.Empty;
                        splitEvent.Dimensions = CS1PartBC(fields);
                        splitEvent.IsInGameEvent = true;

                        if (splitEvent.EventName.Contains("MultiplayerRoundStart") || splitEvent.EventName.Contains("MultiplayerRoundEnd"))
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

            for (int i = 1; i < fields.Length; ++i)
            {
                result += fields[i] + "|";
            }

            return result;
        }
    }
}
