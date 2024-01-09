// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XMAT;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace CaptureAnalysisEngine
{
    public interface IEndpointReportData
    {
        int Warnings { get; }
        int Errors { get; }
        string Result { get; }
    }

    public class ReportMetadata : IEndpointReportData
    {
        public int Warnings { get; internal set; }
        public int Errors { get; internal set; }
        public string Result { get; internal set; }
    }

    public class ViolationCallData
    {
        public uint Id { get; set; }
        public string UriMethod { get; set; }
        public string EndpointFunctionCall { get; set; }
    }

    public class RuleViolationData
    {
        internal RuleViolationData()
        {
            CallData = new();
        }

        public string Level { get; set; }
        public string Summary { get; set; }
        public List<ViolationCallData> CallData { get; }
    }

    public class RuleReportData : IEndpointReportData
    {
        public int Warnings { get { return Metadata.Warnings; } }
        public int Errors { get { return Metadata.Errors; } }
        public string Result { get { return Metadata.Result; } }

        internal RuleReportData()
        {
            ResultData = new();
            Violations = new();
        }

        public string Name;
        public string Description;
        public ReportMetadata Metadata = new();
        public Dictionary<string, string> ResultData { get; }
        public List<RuleViolationData> Violations { get; }
    }

    public class EndpointReportData : IEndpointReportData
    {
        public int Warnings { get { return Metadata.Warnings; } }
        public int Errors { get { return Metadata.Errors; } }
        public string Result { get { return Metadata.Result; } }

        internal EndpointReportData()
        {
            RuleReportData = new ();
        }

        public string UriService;
        public string EndpointFunctionCall;
        public ReportMetadata Metadata = new ();
        public List<RuleReportData> RuleReportData { get; }
    }

    public class PerEndpointReportDocument : ReportDocument, IEndpointReportData
    {
        public int Warnings { get { return Metadata.Warnings; } }
        public int Errors { get { return Metadata.Errors; } }
        public string Result { get { return Metadata.Result; } }

        internal PerEndpointReportDocument()
        {
            EndpointReportData = new ();
        }

        public override string ToJson()
        {
            var options = new JsonSerializerOptions
            {
                IncludeFields = true,
            };
            return JsonSerializer.Serialize(
                this,
                this.GetType(),
                options);
        }

        public ReportMetadata Metadata = new();
        public List<EndpointReportData> EndpointReportData { get; }
    }

    [Report]
    public class PerEndpointReport : Report
    {
        private static String[] SupportedRules = new String[] {
            BatchFrequencyRule.DisplayName,
            BurstDetectionRule.DisplayName,
            CallFrequencyRule.DisplayName,
            PollingDetectionRule.DisplayName,
            RepeatedCallsRule.DisplayName,
            SmallBatchDetectionRule.DisplayName,
            ThrottledCallsRule.DisplayName
        };

        public PerEndpointReport()
        {
        }

        public override ReportDocument RunReport(
            IEnumerable<RuleResult> result,
            Dictionary<String, Tuple<String, String, String>> endpoints)
        {
            var document = new PerEndpointReportDocument();

            var endpointRules = result.GroupBy(r => r.Endpoint);

            foreach (var endpoint in endpoints)
            {
                var endpointReportData = ReportEndpoint(endpointRules, endpoint);
                if (endpointReportData != null)
                {
                    document.EndpointReportData.Add(endpointReportData);
                }
            }

            GenerateMetaData(ref document.Metadata, document.EndpointReportData);

            return document;
        }

        private static IEnumerable<RuleResult> GetEndpointResults(
            IEnumerable<IGrouping<String, RuleResult>> ruleGroups,
            String endpoint)
        {
            return ruleGroups.Where(g => g.Key == endpoint).SelectMany(g => g.AsEnumerable());
        }

        public EndpointReportData ReportEndpoint(
            IEnumerable<IGrouping<String, RuleResult>> ruleGroups,
            KeyValuePair<String, Tuple<String,String,String>> endpoint)
        {
            var endpointList = GetEndpointResults(ruleGroups, endpoint.Key);

            if (endpointList.Count() == 0)
            {
                return null;
            }

            var endpointReportData = new EndpointReportData();

            endpointReportData.UriService = endpoint.Key;
            endpointReportData.EndpointFunctionCall = endpoint.Value.Item3;

            foreach(var rule in endpointList)
            {
                var ruleReportData = ReportRule(rule);
                if (ruleReportData != null)
                {
                    endpointReportData.RuleReportData.Add(ruleReportData);
                }
            }

            GenerateMetaData(ref endpointReportData.Metadata, endpointReportData.RuleReportData);

            return endpointReportData;
        }

        public RuleReportData ReportRule(RuleResult rule)
        {
            if (rule == null)
            {
                return null;
            }

            if (!SupportedRules.Contains(rule.RuleName))
            {
                return null;
            }

            var ruleReportData = new RuleReportData();

            ruleReportData.Name = rule.RuleName;
            ruleReportData.Description = rule.RuleDescription;

            ruleReportData.Metadata.Errors = rule.Violations.Count(v => v.m_level == ViolationLevel.Error);
            ruleReportData.Metadata.Warnings = rule.Violations.Count(v => v.m_level == ViolationLevel.Warning);

            DetermineMetadaResult(ref ruleReportData.Metadata);

            // rule result data
            foreach (var data in rule.Results)
            {
                ruleReportData.ResultData.Add(data.Key, data.Value.ToString());
            }

            // rule violation data
            foreach (var violation in rule.Violations)
            {
                RuleViolationData ruleViolationData = new();

                switch(violation.m_level)
                {
                    case ViolationLevel.Error:
                        ruleViolationData.Level = Localization.GetLocalizedString("LTA_VIOLATION_LEVEL_ERROR");
                        break;
                    case ViolationLevel.Warning:
                        ruleViolationData.Level = Localization.GetLocalizedString("LTA_VIOLATION_LEVEL_WARNING");
                        break;
                    case ViolationLevel.Info:
                        ruleViolationData.Level = Localization.GetLocalizedString("LTA_VIOLATION_LEVEL_INFO");
                        break;
                    default:
                        ruleViolationData.Level = Localization.GetLocalizedString("LTA_VIOLATION_LEVEL_UNKNOWN");
                        break;
                }

                ruleViolationData.Summary = violation.m_summary;

                // rule violation call data
                foreach (var call in violation.m_violatingCalls)
                {
                    ViolationCallData violationCallData = new();

                    violationCallData.Id = call.Id;
                    violationCallData.UriMethod = call.Uri;

                    if (call.m_xsapiMethods != null)
                    {
                        violationCallData.EndpointFunctionCall = call.m_xsapiMethods.Item3;
                    }
                    else
                    {
                        violationCallData.EndpointFunctionCall = @"[UNMAPPED]";
                    }

                    ruleViolationData.CallData.Add(violationCallData);
                }

                ruleReportData.Violations.Add(ruleViolationData);
            }

            return ruleReportData;
        }

        private void DetermineMetadaResult(ref ReportMetadata reportMetadata)
        {
            if (reportMetadata.Errors > 0)
            {
                reportMetadata.Result = "Error";
            }
            else if (reportMetadata.Warnings > 0)
            {
                reportMetadata.Result = "Warning";
            }
            else
            {
                reportMetadata.Result = "Pass";
            }
        }

        private void GenerateMetaData(
            ref ReportMetadata reportMetadata,
            IEnumerable<IEndpointReportData> endpointResults)
        {
            int warningCounts = 0;
            int errorCounts = 0;

            foreach(var result in endpointResults)
            {
                warningCounts += result.Warnings;
                errorCounts += result.Errors;
            }

            reportMetadata.Warnings = warningCounts;
            reportMetadata.Errors = errorCounts;

            DetermineMetadaResult(ref reportMetadata);
        }
    }
}
