// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CaptureAnalysisEngine;
using System.Collections.Generic;

namespace XMAT.XboxLiveCaptureAnalysis.ReportModels.RuleDataModels
{
    internal class BatchFrequencyRuleDataModel
    {
        public struct DataFields
        {
            public string TotalBatchCalls { get; set; }
            public string AllowedTimeBetweenCalls { get; set; }
            public string TimesExceeded { get; set; }
            public string PotentialReducedCallCount { get; set; }
        }

        public List<DataFields> Data { get; }

        public BatchFrequencyRuleDataModel(RuleReportData ruleReportData)
        {
            Data = new();
            Data.Add(new DataFields()
            {
                TotalBatchCalls = ruleReportData.ResultData[BatchFrequencyRule.TotalBatchCallsDataKey],
                AllowedTimeBetweenCalls = ruleReportData.ResultData[BatchFrequencyRule.AllowedTimeBetweenCallsDataKey],
                TimesExceeded = ruleReportData.ResultData[BatchFrequencyRule.TimesExceededDataKey],
                PotentialReducedCallCount = ruleReportData.ResultData[BatchFrequencyRule.PotentialReducedCallCountDataKey]
            });
        }
    }
}
