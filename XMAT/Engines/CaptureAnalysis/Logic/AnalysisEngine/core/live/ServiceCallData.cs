// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;

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
        private static readonly InputRegex Cs2EventName =
            new(
                "Microsoft.XboxLive.T[a-zA-Z0-9]{8}.",
                cacheLastResult: false);
        private static readonly InputRegex Cs1EventName =
            new(
                "[a-zA-z]{4}_[a-zA-Z0-9]{8}",
                cacheLastResult: false);

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
            CreateFromServiceCallItems(frameData, CancellationToken.None);
        }

        internal void CreateFromServiceCallItems(
            IEnumerable<ServiceCallItem> frameData,
            CancellationToken cancellationToken)
        {
            IEnumerable<ServiceCallItem>
                identifiedFrames =
                    EnsureBodyIdentities(
                        frameData,
                        cancellationToken);
            var consoleGroups = from f in identifiedFrames
                                group f by f.ConsoleIP;

            foreach (var consoleFrames in consoleGroups.Where(g => g.Key != String.Empty))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var consoleData = new PerConsoleData();

                var xboxServiceFrames = consoleFrames.GroupBy(f => new EndpointKey(f.EndpointKind, f.Host ?? string.Empty))
                                                     .Select(group => new { Endpoint = group.Key, History = group.AsEnumerable() });

                consoleData.m_servicesHistory = xboxServiceFrames.ToDictionary(g => g.Endpoint, g => new LinkedList<ServiceCallItem>(g.History.OrderBy(call => call.ReqTimeUTC)));

                // Xbox telemetry endpoint
                if (consoleData.m_servicesHistory.Keys.Any(key => IsTelemetrySource(key, host => host == XboxTelemetryEndpoint)))
                {
                    ConvertCS1ToEvent(
                        consoleData.m_servicesHistory,
                        cancellationToken);
                }

                // Windows Telemetry endpoint
                if (consoleData.m_servicesHistory.Keys.Any(key => IsTelemetrySource(key, IsWindowsTelemetryHost)))
                {
                    ConvertCS2ToEvent(
                        consoleData.m_servicesHistory,
                        cancellationToken);
                }

                // clear empty items
                consoleData.m_servicesHistory = consoleData.m_servicesHistory.Where(o => o.Value.Count > 0).ToDictionary(x => x.Key, x => x.Value);

                foreach (var endpoint in consoleData.m_servicesHistory)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    consoleData.m_servicesStats.Add(endpoint.Key, new ServiceCallStats(endpoint.Value));
                }

                m_perConsoleData.Add(consoleFrames.Key, consoleData);
            }
        }

        private static IEnumerable<ServiceCallItem>
            EnsureBodyIdentities(
                IEnumerable<ServiceCallItem> items,
                CancellationToken cancellationToken)
        {
            RequestBodyIdentityInterner identities = null;
            foreach (ServiceCallItem item in items)
            {
                cancellationToken
                    .ThrowIfCancellationRequested();
                if (item.BodyIdentity == null)
                {
                    identities ??=
                        new RequestBodyIdentityInterner();
                    item.BodyIdentity =
                        item.ReqBody == null
                            ? identities.CreateUnique()
                            : identities.GetOrAdd(
                                item.ReqBody);
                }
                yield return item;
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

        private void ConvertCS2ToEvent(
            Dictionary<EndpointKey, LinkedList<ServiceCallItem>> servicesHistory,
            CancellationToken cancellationToken)
        {
            var eventNameMatch2 = @"Microsoft.Xbox.XceBridge";

            var events = servicesHistory.Where(k => IsTelemetrySource(k.Key, IsWindowsTelemetryHost)).ToList();
            foreach (var endpoint in events)
            {
                servicesHistory.Remove(endpoint.Key);
            }
            LinkedList<ServiceCallItem> inGameEvents = GetSyntheticEvents(servicesHistory);

            foreach (var eventCall in events.SelectMany(e => e.Value))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requestBody = eventCall.ReqBody;

                // If there's nothing in the request body, then there was an error with the event and we can't parse it.
                if (string.IsNullOrEmpty(requestBody))
                {
                    continue;
                }

                var eventArray = requestBody.Split(Environment.NewLine.ToCharArray(), StringSplitOptions.RemoveEmptyEntries);

                foreach (var eventLine in eventArray)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    JsonElement requestBodyJson;
                    JsonDocument eventDocument;

                    try
                    {
                        eventDocument =
                            JsonDocument.Parse(
                                eventLine);
                        requestBodyJson = eventDocument.RootElement;
                    }
                    catch (JsonException)
                    {
                        // TODO: for now swallow the bad json, but perhaps some indicator
                        // should exist for this
                        continue;
                    }

                    using (eventDocument)
                    {
                        string eventName = string.Empty;
                        Utils.SafeAssign<string>(requestBodyJson, @"name", ref eventName);

                        if (Cs2EventName.IsMatch(eventName) || eventNameMatch2.StartsWith(eventName))
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
        }

        private void ConvertCS1ToEvent(
            Dictionary<EndpointKey, LinkedList<ServiceCallItem>> servicesHistory,
            CancellationToken cancellationToken)
        {
            var sources = servicesHistory.Where(k => IsTelemetrySource(k.Key, host => host == XboxTelemetryEndpoint)).ToList();
            foreach (var source in sources)
            {
                servicesHistory.Remove(source.Key);
            }

            LinkedList<ServiceCallItem> inGameEvents = GetSyntheticEvents(servicesHistory);

            foreach (var eventCall in sources.SelectMany(source => source.Value))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requestBody = eventCall.ReqBody;

                var eventArray = requestBody.Split(Environment.NewLine.ToCharArray());

                foreach (var eventLine in eventArray)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fields = eventLine.Split('|');

                    if (fields.Length < 12)
                    {
                        // This event is not valid as it is missing fields
                        continue;
                    }

                    // The name field is in the form of {Publisher}_{TitleId}.{EventName}
                    var eventNameParts = fields[1].Split('.');

                    if (eventNameParts.Length > 1 && Cs1EventName.IsMatch(eventNameParts[0]))
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
            return fields.Length <= 1
                ? string.Empty
                : string.Join(
                    "|",
                    fields,
                    1,
                    fields.Length - 1) +
                    "|";
        }
    }
}
