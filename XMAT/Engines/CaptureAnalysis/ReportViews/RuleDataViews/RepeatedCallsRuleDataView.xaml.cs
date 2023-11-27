// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Windows.Controls;

namespace XMAT.XboxLiveCaptureAnalysis.ReportViews.RuleDataViews
{
    /// <summary>
    /// Interaction logic for RepeatedCallsRuleDataView.xaml
    /// </summary>
    public partial class RepeatedCallsRuleDataView : UserControl
    {
        public static string TotalCallsLabel { get { return Localization.GetLocalizedString("LTA_REPEATED_CALLS_DATA_TOTAL"); } }
        public static string DuplicatesLabel { get { return Localization.GetLocalizedString("LTA_REPEATED_CALLS_DATA_DUPS"); } }
        public static string PercentageLabel { get { return Localization.GetLocalizedString("LTA_REPEATED_CALLS_DATA_PERCENT"); } }

        public RepeatedCallsRuleDataView()
        {
            InitializeComponent();
        }
    }
}
