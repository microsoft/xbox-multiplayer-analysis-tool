// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CaptureAnalysisEngine;
using System.Collections.Generic;

namespace XMAT.XboxLiveCaptureAnalysis.ReportModels.RuleDataModels
{
    internal class BurstDetectionRuleDataModel
    {
        public struct DataFields
        {
            public string AvgCallsPerSec { get; set; }
            public string StdDeviation { get; set; }
            public string BurstSize { get; set; }
            public string BurstWindow { get; set; }
            public string TotalBursts { get; set; }
        }

        public List<DataFields> Data { get; }

        public BurstDetectionRuleDataModel(RuleReportData ruleReportData)
        {
            Data = new();
            Data.Add(new DataFields()
            {
                AvgCallsPerSec = ruleReportData.ResultData[BurstDetectionRule.AvgCallsPerSecDataKey],
                StdDeviation = ruleReportData.ResultData[BurstDetectionRule.StdDeviationDataKey],
                BurstSize = ruleReportData.ResultData[BurstDetectionRule.BurstSizeDataKey],
                BurstWindow = ruleReportData.ResultData[BurstDetectionRule.BurstWindowDataKey],
                TotalBursts = ruleReportData.ResultData[BurstDetectionRule.TotalBurstsDataKey]
            });
        }
    }
}
