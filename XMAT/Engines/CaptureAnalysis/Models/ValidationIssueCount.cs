// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

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

        // The count with its level in words, such as "3 errors", or "No issues".
        public string Description => IssueLevel switch
        {
            ViolationLevel.Error => Localization.GetLocalizedString(
                Errors == 1 ? "LTA_ISSUE_COUNT_ERROR" : "LTA_ISSUE_COUNT_ERRORS", Errors),
            ViolationLevel.Warning => Localization.GetLocalizedString(
                Warnings == 1 ? "LTA_ISSUE_COUNT_WARNING" : "LTA_ISSUE_COUNT_WARNINGS", Warnings),
            _ => Localization.GetLocalizedString("LTA_ISSUE_COUNT_NONE")
        };

        // The rule and its Description, for a control that is not beside the rule's name.
        public string AccessibleName => Localization.GetLocalizedString("LTA_ISSUE_COUNT_NAME", RuleName, Description);

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
