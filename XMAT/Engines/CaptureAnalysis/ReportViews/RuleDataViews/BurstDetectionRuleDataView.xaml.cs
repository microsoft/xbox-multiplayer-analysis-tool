// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows.Controls;
using CaptureAnalysisEngine;

namespace XMAT.XboxLiveCaptureAnalysis.ReportViews.RuleDataViews
{
    /// <summary>
    /// Interaction logic for BurstDetectionRuleDataView.xaml
    /// </summary>
    public partial class BurstDetectionRuleDataView : UserControl
    {
        public static string AvgCallsPerSecLabel { get { return Localization.GetLocalizedString("LTA_BURST_CALLS_AVERAGE"); } }
        public static string StdDeviationLabel { get { return Localization.GetLocalizedString("LTA_BURST_CALLS_DEVIATION"); } }
        public static string BurstSizeLabel { get { return Localization.GetLocalizedString("LTA_BURST_CALLS_SIZE"); } }
        public static string BurstWindowLabel { get { return Localization.GetLocalizedString("LTA_BURST_CALLS_WINDOW"); } }
        public static string TotalBurstsLabel { get { return Localization.GetLocalizedString("LTA_BURST_CALLS_TOTAL"); } }

        public static string TableName { get { return Localization.GetLocalizedString("LTA_RULE_DATA_TABLE", BurstDetectionRule.DisplayName); } }

        public BurstDetectionRuleDataView()
        {
            InitializeComponent();
        }
    }
}
