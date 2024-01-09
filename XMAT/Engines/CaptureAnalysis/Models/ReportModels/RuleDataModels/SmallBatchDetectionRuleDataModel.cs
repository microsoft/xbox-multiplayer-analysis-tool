// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CaptureAnalysisEngine;
using System.Collections.Generic;

namespace XMAT.XboxLiveCaptureAnalysis.ReportModels.RuleDataModels
{
    internal class SmallBatchDetectionRuleDataModel
    {
        public struct DataFields
        {
            public string TotalBatchCalls { get; set; }
            public string MinUsersAllowed { get; set; }
            public string CallsBelowCount { get; set; }
            public string PercentBelowCount { get; set; }
        }

        public List<DataFields> Data { get; }

        public SmallBatchDetectionRuleDataModel(RuleReportData ruleReportData)
        {
            Data = new();
            Data.Add(new DataFields()
            {
                TotalBatchCalls = ruleReportData.ResultData[SmallBatchDetectionRule.TotalBatchCallsDataKey],
                MinUsersAllowed = ruleReportData.ResultData[SmallBatchDetectionRule.MinUsersAllowedDataKey],
                CallsBelowCount = ruleReportData.ResultData[SmallBatchDetectionRule.CallsBelowCountDataKey],
                PercentBelowCount = ruleReportData.ResultData[SmallBatchDetectionRule.PercentBelowCountDataKey]
            });
        }
    }
}
