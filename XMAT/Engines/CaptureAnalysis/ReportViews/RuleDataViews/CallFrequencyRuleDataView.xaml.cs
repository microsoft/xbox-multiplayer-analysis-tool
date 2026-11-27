// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows.Controls;
using CaptureAnalysisEngine;

namespace XMAT.XboxLiveCaptureAnalysis.ReportViews.RuleDataViews
{
    /// <summary>
    /// Interaction logic for CallFrequencyRuleDataView.xaml
    /// </summary>
    public partial class CallFrequencyRuleDataView : UserControl
    {
        public static string TotalCallsLabel { get { return Localization.GetLocalizedString("LTA_FREQ_CALLS_TOTAL"); } }
        public static string TimesSustainedExceededLabel { get { return Localization.GetLocalizedString("LTA_FREQ_CALLS_SUSTAINED"); } }
        public static string TimesBurstExceededLabel { get { return Localization.GetLocalizedString("LTA_FREQ_CALLS_BURST"); } }

        public static string TableName { get { return Localization.GetLocalizedString("LTA_RULE_DATA_TABLE", CallFrequencyRule.DisplayName); } }

        public CallFrequencyRuleDataView()
        {
            InitializeComponent();
        }
    }
}
