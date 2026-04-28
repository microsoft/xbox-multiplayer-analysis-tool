// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using CaptureAnalysisEngine;

namespace XMAT.XboxLiveCaptureAnalysis.ReportModels.RuleDataModels
{
    internal class RepeatedCallsRuleDataModel
    {
        public struct DataFields
        {
            public string TotalCalls { get; set; }
            public string Duplicates { get; set; }
            public string Percentage { get; set; }
        }

        public List<DataFields> Data { get; }

        public RepeatedCallsRuleDataModel(RuleReportData ruleReportData)
        {
            Data = new();
            Data.Add(new DataFields()
            {
                TotalCalls = ruleReportData.ResultData[RepeatedCallsRule.TotalCallsDataKey],
                Duplicates = ruleReportData.ResultData[RepeatedCallsRule.DuplicatesDataKey],
                Percentage = ruleReportData.ResultData[RepeatedCallsRule.PercentageDataKey],
            });
        }
    }
}
