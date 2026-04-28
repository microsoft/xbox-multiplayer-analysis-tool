// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using CaptureAnalysisEngine;

namespace XMAT.XboxLiveCaptureAnalysis.ReportModels.RuleDataModels
{
    internal class ThrottledCallRuleDataModel
    {
        public struct DataFields
        {
            public string TotalCalls { get; set; }
            public string ThrottledCalls { get; set; }
            public string Percentage { get; set; }
        }

        public List<DataFields> Data { get; }

        public ThrottledCallRuleDataModel(RuleReportData ruleReportData)
        {
            Data = new();
            Data.Add(new DataFields()
            {
                TotalCalls = ruleReportData.ResultData[ThrottledCallsRule.TotalCallsDataKey],
                ThrottledCalls = ruleReportData.ResultData[ThrottledCallsRule.ThrottledCallsDataKey],
                Percentage = ruleReportData.ResultData[ThrottledCallsRule.PercentageDataKey],
            });
        }
    }
}
