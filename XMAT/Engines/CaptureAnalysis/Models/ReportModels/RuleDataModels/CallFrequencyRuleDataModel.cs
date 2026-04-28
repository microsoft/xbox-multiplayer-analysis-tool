// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using CaptureAnalysisEngine;

namespace XMAT.XboxLiveCaptureAnalysis.ReportModels.RuleDataModels
{
    internal class CallFrequencyRuleDataModel
    {
        public struct DataFields
        {
            public string TotalCalls { get; set; }
            public string TimesSustainedExceeded { get; set; }
            public string TimesBurstExceeded { get; set; }
        }

        public List<DataFields> Data { get; }

        public CallFrequencyRuleDataModel(RuleReportData ruleReportData)
        {
            Data = new();
            Data.Add(new DataFields()
            {
                TotalCalls = ruleReportData.ResultData[CallFrequencyRule.TotalCallsDataKey],
                TimesSustainedExceeded = ruleReportData.ResultData[CallFrequencyRule.TimesSustainedExceededDataKey],
                TimesBurstExceeded = ruleReportData.ResultData[CallFrequencyRule.TimesBurstExceededDataKey],
            });
        }
    }
}
