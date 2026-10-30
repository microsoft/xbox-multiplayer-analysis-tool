// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using CaptureAnalysisEngine;
using XMAT.SharedInterfaces;
using XMAT.XboxLiveCaptureAnalysis.Models;
using XMAT.XboxLiveCaptureAnalysis.ReportModels.RuleDataModels;

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
        // Shown text: EndpointName, qualified by its localized kind when another endpoint in
        // the report shares the name. EndpointName itself stays the raw logical name.
        public string DisplayName { get; private set; }
        // Typed identity used for navigation; EndpointName is display text only.
        public EndpointKey Key { get; private set; }
        // Physical host offered by host-list actions, or null when not actionable.
        public string ActionableHost { get; private set; }
        public List<EndpointValidationRuleDetails> ValidationRulesDetails { get; private set; }

        public EndpointValidationDetails(EndpointReportData endpointReportData, string displayName)
        {
            DisplayName = displayName;
            ValidationRulesDetails = new();
            InitializeFrom(endpointReportData);
        }

        private void InitializeFrom(EndpointReportData endpointReportData)
        {
            EndpointName = endpointReportData.UriService;
            Key = endpointReportData.Key;
            ActionableHost = endpointReportData.ActionableHost;

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
            Dictionary<EndpointKey, string> labels =
                EndpointLabels.For(reportDocument.EndpointReportData.Select(data => data.Key));
            foreach (EndpointReportData endpointReportData in reportDocument.EndpointReportData)
            {
                EndpointValidationDetails endpoint = new(endpointReportData, labels[endpointReportData.Key]);
                Endpoints.Add(endpoint);
            }
        }
    }
}
