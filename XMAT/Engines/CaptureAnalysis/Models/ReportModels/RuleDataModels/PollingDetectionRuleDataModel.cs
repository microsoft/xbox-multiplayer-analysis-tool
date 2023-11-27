// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CaptureAnalysisEngine;

namespace XMAT.XboxLiveCaptureAnalysis.ReportModels.RuleDataModels
{
    internal class PollingDetectionRuleDataModel
    {
        public string PollingSequencesFound { get; private set; }

        public PollingDetectionRuleDataModel(RuleReportData ruleReportData)
        {
            PollingSequencesFound = ruleReportData.ResultData[PollingDetectionRule.PollingSequencesFoundDataKey];
        }
    }
}
