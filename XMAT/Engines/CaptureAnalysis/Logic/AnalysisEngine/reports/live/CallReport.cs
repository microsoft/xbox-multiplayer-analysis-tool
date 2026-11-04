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
    public class CallReportDocument : ReportDocument
    {
        private static readonly JsonSerializerOptions
            SerializerOptions = new()
            {
                IncludeFields = true,
            };

        internal CallReportDocument()
        {
            CallEndpoints = new();
        }

        public override string ToJson()
        {
            return JsonSerializer.Serialize(
                this,
                this.GetType(),
                SerializerOptions);
        }

        public override void WriteJson(
            Stream output)
        {
            ArgumentNullException.ThrowIfNull(output);
            JsonSerializer.Serialize(
                output,
                this,
                this.GetType(),
                SerializerOptions);
        }

        public class Endpoint
        {
            internal Endpoint()
            {
                Calls = new();
            }

            public class Call
            {
                [JsonPropertyName("Id")]
                public uint Id;
                [JsonPropertyName("ReqTime")]
                public double ReqTime;
                [JsonPropertyName("Uri")]
                public string Uri;
                [JsonPropertyName("C")]
                public string EndpointFunctionCall;
                [JsonPropertyName("Request Body")]
                public string ReqBody;
            }

            [JsonPropertyName("Uri")]
            public string Uri;
            [JsonPropertyName("C")]
            public string EndpointFunctionCall;
            [JsonPropertyName("Calls")]
            public List<Call> Calls;
            // Identity of the endpoint the calls were recorded for; Unknown when not recorded.
            [JsonPropertyName("Kind")]
            public EndpointKind Kind;
            // Stable kind and logical-name identity that report consumers group by.
            [JsonPropertyName("EndpointId")]
            public string EndpointId;
            // Display text: the Uri, qualified by its localized kind when another endpoint shares it.
            [JsonPropertyName("Label")]
            public string Label;
        }

        [JsonPropertyName("Start Time")]
        public double StartTime { get; internal set; }
        [JsonPropertyName("End Time")]
        public double EndTime { get; internal set; }
        [JsonPropertyName("Call List")]
        public List<Endpoint> CallEndpoints { get; }
        [JsonPropertyName("AverageTimeLabel")]
        public string AverageTimeLabel { get { return Localization.GetLocalizedString("LTA_REPORT_AVERAGE_CALLS_LABEL"); } }
        [JsonPropertyName("CallsPerSecondLabel")]
        public string CallsPerSecondLabel { get { return Localization.GetLocalizedString("LTA_REPORT_CALLS_PER_SEC_LABEL"); } }
        [JsonPropertyName("CallsPerEndpointLabel")]
        public string CallsPerEndpointLabel { get { return Localization.GetLocalizedString("LTA_REPORT_CALLS_PER_ENDPOINT"); } }
        [JsonPropertyName("CallCountLabel")]
        public string CallCountLabel { get { return Localization.GetLocalizedString("LTA_REPORT_CALL_COUNT_LABEL"); } }
        [JsonPropertyName("EndpointLabel")]
        public string EndpointLabel { get { return Localization.GetLocalizedString("LTA_REPORT_ENDPOINT_LABEL"); } }
        [JsonPropertyName("SecondsLabel")]
        public string SecondsLabel { get { return Localization.GetLocalizedString("LTA_REPORT_SECONDS_LABEL"); } }
    }

    [Report]
    public class CallReport : Report
    {
        public CallReport()
        {
        }

        public override ReportDocument RunReport(
            IEnumerable<RuleResult> result,
            Dictionary<string, Tuple<string, string, string>> endpoints)
        {
            var document = new CallReportDocument();

            // Each recorded endpoint keeps the identity its rule ran on, so a captured host and
            // telemetry synthesized under the same name stay separate records.
            var calls = result
                .Where(r => r.RuleName == "CallRecorder")
                .Where(r => (r.FindResultByKey("Calls") as IEnumerable<ServiceCallItem>).Any())
                .ToList();
            Dictionary<EndpointKey, string> labels = EndpointLabels.For(calls.Select(r => r.Identity));

            double firstCall = double.MaxValue;
            double lastCall = double.MinValue;

            foreach (var callResult in calls)
            {
                var callList = callResult.FindResultByKey("Calls") as IEnumerable<ServiceCallItem>;

                var host = new CallReportDocument.Endpoint();

                host.Uri = callResult.Endpoint;
                host.Kind = callResult.Identity.Kind;
                host.EndpointId = callResult.Identity.Id;
                host.Label = labels[callResult.Identity];

                if (endpoints.ContainsKey(callResult.Endpoint))
                {
                    host.EndpointFunctionCall = endpoints[callResult.Endpoint].Item3;
                }
                else
                {
                    host.EndpointFunctionCall = Localization.GetLocalizedString("LTA_REPORT_ENDPOINT_UNMAPPED");
                }

                double hostFirstCall = callList.Min(c => c.ReqTimeUTC) / (double)TimeSpan.TicksPerMillisecond;
                double hostLastCall = callList.Max(c => c.ReqTimeUTC) / (double)TimeSpan.TicksPerMillisecond;

                if (hostFirstCall < firstCall)
                    firstCall = hostFirstCall;
                if (hostLastCall > lastCall)
                    lastCall = hostLastCall;

                foreach (var call in callList)
                {
                    var hostCall = new CallReportDocument.Endpoint.Call();

                    hostCall.Id = call.Id;
                    hostCall.ReqTime = call.ReqTimeUTC / (double)TimeSpan.TicksPerMillisecond;
                    hostCall.Uri = call.Uri;
                    if (call.m_xsapiMethods != null)
                    {
                        hostCall.EndpointFunctionCall = call.m_xsapiMethods.Item3;
                    }
                    else
                    {
                        hostCall.EndpointFunctionCall = Localization.GetLocalizedString("LTA_REPORT_ENDPOINT_UNMAPPED");
                    }

                    hostCall.ReqBody = call.ReqBody;

                    host.Calls.Add(hostCall);
                }

                document.CallEndpoints.Add(host);
            }

            document.StartTime = firstCall;
            document.EndTime = lastCall;

            return document;
        }
    }
}
