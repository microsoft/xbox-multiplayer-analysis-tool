// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Windows.Controls;

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

        public CallFrequencyRuleDataView()
        {
            InitializeComponent();
        }
    }
}
