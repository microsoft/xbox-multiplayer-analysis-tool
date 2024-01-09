// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using XMAT;

namespace CaptureAnalysisEngine
{
    public class StatsReportDocument : ReportDocument
    {
        internal StatsReportDocument()
        {
            StatsResults = new ();
        }

        public override string ToJson()
        {
            var options = new JsonSerializerOptions
            {
                IncludeFields = true,
            };
            return JsonSerializer.Serialize(
                this.StatsResults,
                typeof(List<Host>),
                options);
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

            var stats = result.Where(r => r.RuleName == "StatsRecorder");

            foreach (var endpointStats in stats)
            {
                var stat = endpointStats.FindResultByKey("Stats") as ServiceCallStats;
                if (stat.m_numCalls == 0)
                {
                    continue;
                }

                var host = new StatsReportDocument.Host();
                host.Uri = endpointStats.Endpoint;

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
