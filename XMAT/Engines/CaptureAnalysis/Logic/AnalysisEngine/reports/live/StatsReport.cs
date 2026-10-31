// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using XMAT;

namespace CaptureAnalysisEngine
{
    public class StatsReportDocument : ReportDocument
    {
        private static readonly JsonSerializerOptions
            SerializerOptions = new()
            {
                IncludeFields = true,
            };

        internal StatsReportDocument()
        {
            StatsResults = new();
        }

        public override string ToJson()
        {
            return JsonSerializer.Serialize(
                this.StatsResults,
                typeof(List<Host>),
                SerializerOptions);
        }

        public override void WriteJson(
            Stream output)
        {
            ArgumentNullException.ThrowIfNull(output);
            JsonSerializer.Serialize(
                output,
                this.StatsResults,
                typeof(List<Host>),
                SerializerOptions);
        }

        public class Host
        {
            [JsonPropertyName("Uri")]
            public string Uri;
            [JsonPropertyName("C")]
            public string EndpointFunctionCall;
            [JsonPropertyName("Call Count")]
            public ulong CallCount;
            [JsonPropertyName("Average Time Between Calls")]
            public ulong AvgTimeBetweenCallsInMs;
            // Identity of the endpoint the statistics were recorded for; Unknown when not recorded.
            [JsonPropertyName("Kind")]
            public EndpointKind Kind;
            // Stable kind and logical-name identity that report consumers group by.
            [JsonPropertyName("EndpointId")]
            public string EndpointId;
            // Display text: the Uri, qualified by its localized kind when another endpoint shares it.
            [JsonPropertyName("Label")]
            public string Label;
        }

        public List<Host> StatsResults { get; }
    }

    [Report]
    public class StatsReport : Report
    {
        public StatsReport()
        {
        }

        public override ReportDocument RunReport(
            IEnumerable<RuleResult> result,
            Dictionary<string, Tuple<string, string, string>> endpoints)
        {
            var document = new StatsReportDocument();

            // Each endpoint keeps the identity its rule ran on, so a captured host and telemetry
            // synthesized under the same name stay separate records.
            var stats = result
                .Where(r => r.RuleName == "StatsRecorder")
                .Where(r => (r.FindResultByKey("Stats") as ServiceCallStats).m_numCalls != 0)
                .ToList();
            Dictionary<EndpointKey, string> labels = EndpointLabels.For(stats.Select(r => r.Identity));

            foreach (var endpointStats in stats)
            {
                var stat = endpointStats.FindResultByKey("Stats") as ServiceCallStats;

                var host = new StatsReportDocument.Host();
                host.Uri = endpointStats.Endpoint;
                host.Kind = endpointStats.Identity.Kind;
                host.EndpointId = endpointStats.Identity.Id;
                host.Label = labels[endpointStats.Identity];

                if (endpoints.ContainsKey(endpointStats.Endpoint))
                {
                    host.EndpointFunctionCall = endpoints[endpointStats.Endpoint].Item3;
                }
                else
                {
                    host.EndpointFunctionCall = Localization.GetLocalizedString("LTA_REPORT_ENDPOINT_UNMAPPED");
                }

                host.CallCount = stat.m_numCalls;
                host.AvgTimeBetweenCallsInMs = stat.m_avgTimeBetweenReqsMs;

                document.StatsResults.Add(host);
            }

            return document;
        }
    }
}
