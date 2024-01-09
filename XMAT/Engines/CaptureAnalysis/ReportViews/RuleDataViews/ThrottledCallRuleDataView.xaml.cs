// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Windows.Controls;

namespace XMAT.XboxLiveCaptureAnalysis.ReportViews.RuleDataViews
{
    /// <summary>
    /// Interaction logic for ThrottledCallRuleDataView.xaml
    /// </summary>
    public partial class ThrottledCallRuleDataView : UserControl
    {
        public static string TotalCallsLabel { get { return Localization.GetLocalizedString("LTA_THROTTLE_CALLS_TOTAL"); } }
        public static string ThrottledCallsLabel { get { return Localization.GetLocalizedString("LTA_THROTTLE_CALLS_THROTTLED"); } }
        public static string PercentageLabel { get { return Localization.GetLocalizedString("LTA_THROTTLE_CALLS_PERCENT"); } }
        public ThrottledCallRuleDataView()
        {
            InitializeComponent();
        }
    }
}
