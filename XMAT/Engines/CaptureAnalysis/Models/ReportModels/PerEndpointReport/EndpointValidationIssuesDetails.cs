// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CaptureAnalysisEngine;
using System;
using System.Collections.Generic;
using XMAT.XboxLiveCaptureAnalysis.ReportModels.RuleDataModels;
using XMAT.XboxLiveCaptureAnalysis.Models;
using XMAT.SharedInterfaces;

namespace XMAT.XboxLiveCaptureAnalysis.ReportModels.PerEndpointReport
{
    internal class RuleViolationDetail
    {

    }

    internal class EndpointValidationRuleDetails
    {
        public ValidationIssueCount IssueCount { get; }
        public string RuleDescription { get; }
        public object RuleDataModel { get; private set; }
        public RuleReportData SourceRuleReportData { get; private set; }

        public EndpointValidationRuleDetails(RuleReportData ruleReportData)
        {
            IssueCount = new(ruleReportData.Name);
            RuleDescription = ruleReportData.Description;
            InitializeFrom(ruleReportData);
        }

        private void InitializeFrom(RuleReportData ruleReportData)
        {
            IssueCount.InitializeFrom(ruleReportData);
            RuleDataModel = RuleNameToDataModel[ruleReportData.Name](ruleReportData);
            SourceRuleReportData = ruleReportData;
        }

        private static readonly Dictionary<string, Func<RuleReportData, object>> RuleNameToDataModel =
            new()
            {
                {
                    BatchFrequencyRule.DisplayName,
                    ruleReportData => { return new BatchFrequencyRuleDataModel(ruleReportData); }
                },
                {
                    BurstDetectionRule.DisplayName,
                    ruleReportData => { return new BurstDetectionRuleDataModel(ruleReportData); }
                },
                {
                    CallFrequencyRule.DisplayName,
                    ruleReportData => { return new CallFrequencyRuleDataModel(ruleReportData); }
                },
                {
                    PollingDetectionRule.DisplayName,
                    ruleReportData => { return new PollingDetectionRuleDataModel(ruleReportData); }
                },
                {
                    RepeatedCallsRule.DisplayName,
                    ruleReportData => { return new RepeatedCallsRuleDataModel(ruleReportData); }
                },
                {
                    SmallBatchDetectionRule.DisplayName,
                    ruleReportData => { return new SmallBatchDetectionRuleDataModel(ruleReportData); }
                },
                {
                    ThrottledCallsRule.DisplayName,
                    ruleReportData => { return new ThrottledCallRuleDataModel(ruleReportData); }
                }
            };
    }

    internal class EndpointValidationDetails
    {
        public string EndpointName { get; private set; }
        public List<EndpointValidationRuleDetails> ValidationRulesDetails { get; private set; }

        public EndpointValidationDetails(EndpointReportData endpointReportData)
        {
            ValidationRulesDetails = new();
            InitializeFrom(endpointReportData);
        }

        private void InitializeFrom(EndpointReportData endpointReportData)
        {
            EndpointName = endpointReportData.UriService;

            ValidationRulesDetails.Clear();
            foreach (RuleReportData ruleReportData in endpointReportData.RuleReportData)
            {
                EndpointValidationRuleDetails rule = new(ruleReportData);
                ValidationRulesDetails.Add(rule);
            }
        }
    }

    internal class EndpointValidationIssuesDetails : ReportViewModel
    {
        public override string ReportName { get { return Localization.GetLocalizedString("LTA_ENDPOINT_REPORT_DETAILS"); } }
        public List<EndpointValidationDetails> Endpoints { get; }

        public EndpointValidationIssuesDetails(
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
                EndpointValidationDetails endpoint = new(endpointReportData);
                Endpoints.Add(endpoint);
            }
        }
    }
}
