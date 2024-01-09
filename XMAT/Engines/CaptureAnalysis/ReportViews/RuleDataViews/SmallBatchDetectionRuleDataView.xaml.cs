// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Windows.Controls;

namespace XMAT.XboxLiveCaptureAnalysis.ReportViews.RuleDataViews
{
    /// <summary>
    /// Interaction logic for SmallBatcHDetectionRuleDataView.xaml
    /// </summary>
    public partial class SmallBatchDetectionRuleDataView : UserControl
    {
        public static string TotalBatchCallsLabel { get { return Localization.GetLocalizedString("LTA_SMALL_CALLS_TOTAL"); } }
        public static string MinUsersAllowedLabel { get { return Localization.GetLocalizedString("LTA_SMALL_CALLS_MIN"); } }
        public static string CallsBelowCountLabel { get { return Localization.GetLocalizedString("LTA_SMALL_CALLS_COUNT"); } }
        public static string PercentBelowCountLabel { get { return Localization.GetLocalizedString("LTA_SMALL_CALLS_PERCENT"); } }

        public SmallBatchDetectionRuleDataView()
        {
            InitializeComponent();
        }
    }
}
