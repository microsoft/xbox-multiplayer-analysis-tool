// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CaptureAnalysisEngine;
using XMAT.SharedInterfaces;
using XMAT.XboxLiveCaptureAnalysis.Models;
using System.Collections.Generic;

namespace XMAT.XboxLiveCaptureAnalysis.ReportModels.PerEndpointReport
{
    internal class EndpointValidationCounts
    {
        // NOTE: this data structure to explicitly coupled with the available
        // types of validation rules found within the CaptureAnalysisEngine
        public string EndpointName { get; private set; }
        public ValidationIssueCount BatchFrequencyIssueCount { get; private set; }
        public ValidationIssueCount BurstDetectionIssueCount { get; private set; }
        public ValidationIssueCount CallFrequencyIssueCount { get; private set; }
        public ValidationIssueCount PollingDetectionIssueCount { get; private set; }
        public ValidationIssueCount RepeatedCallsIssueCount { get; private set; }
        public ValidationIssueCount SmallBatchDetectionIssueCount { get; private set; }
        public ValidationIssueCount ThrottledCallIssueCount { get; private set; }

        public EndpointValidationCounts(EndpointReportData endpointReportData)
        {
            InitializeFrom(endpointReportData);
        }

        private void InitializeFrom(EndpointReportData endpointReportData)
        {
            EndpointName = endpointReportData.UriService;
            BatchFrequencyIssueCount  = new(BatchFrequencyRule.DisplayName);
            BurstDetectionIssueCount = new(BurstDetectionRule.DisplayName);
            CallFrequencyIssueCount = new(CallFrequencyRule.DisplayName);
            PollingDetectionIssueCount = new(PollingDetectionRule.DisplayName);
            RepeatedCallsIssueCount = new(RepeatedCallsRule.DisplayName);
            SmallBatchDetectionIssueCount = new(SmallBatchDetectionRule.DisplayName);
            ThrottledCallIssueCount = new(ThrottledCallsRule.DisplayName);

            // TODO: this is gross and I honestly do not know what to do about it
            // right now.
            foreach (RuleReportData ruleReportData in endpointReportData.RuleReportData)
            {
                BatchFrequencyIssueCount.InitializeFrom(ruleReportData);
                BurstDetectionIssueCount.InitializeFrom(ruleReportData);
                CallFrequencyIssueCount.InitializeFrom(ruleReportData);
                PollingDetectionIssueCount.InitializeFrom(ruleReportData);
                RepeatedCallsIssueCount.InitializeFrom(ruleReportData);
                SmallBatchDetectionIssueCount.InitializeFrom(ruleReportData);
                ThrottledCallIssueCount.InitializeFrom(ruleReportData);
            }
        }
    }

    internal class EndpointValidationIssuesSummary : ReportViewModel
    {
        public override string ReportName { get { return Localization.GetLocalizedString("LTA_ENDPOINT_REPORT_SUMMARY"); } }
        public List<EndpointValidationCounts> Endpoints { get; }

        public EndpointValidationIssuesSummary(
            ICaptureAppModel captureAppModel,
            string sourceCaptureName,
            PerEndpointReportDocument reportDocument) :
            base(captureAppModel, sourceCaptureName, reportDocument)
        {
            Endpoints = new();
            InitializeFrom(ReportDocument as PerEndpointReportDocument);
        }

        private void InitializeFrom(PerEndpointReportDocument reportDocument)
        {
            Endpoints.Clear();
            foreach (EndpointReportData endpointReportData in reportDocument.EndpointReportData)
            {
                EndpointValidationCounts endpoint = new(endpointReportData);
                Endpoints.Add(endpoint);
            }
        }
    }
}
