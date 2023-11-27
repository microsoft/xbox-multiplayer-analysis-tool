// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CaptureAnalysisEngine;

namespace XMAT.XboxLiveCaptureAnalysis.Models
{
    internal class ValidationIssueCount
    {
        public string RuleName { get; }
        public int Warnings = 0;
        public int Errors = 0;

        public int ErrorsAndWarnings
        {
            get
            {
                return Errors != 0 ? Errors : Warnings;
            }
        }

        public ViolationLevel IssueLevel
        {
            get
            {
                return
                    Errors != 0 ? ViolationLevel.Error :
                    Warnings != 0 ? ViolationLevel.Warning :
                    ViolationLevel.Info;
            }
        }

        public ValidationIssueCount(string ruleName)
        {
            RuleName = ruleName;
        }

        public void InitializeFrom(RuleReportData ruleReportData)
        {
            if (RuleName == ruleReportData.Name)
            {
                Warnings = ruleReportData.Warnings;
                Errors = ruleReportData.Errors;
            }
        }
    }
}
