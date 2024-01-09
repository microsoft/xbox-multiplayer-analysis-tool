// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Windows.Controls;

namespace XMAT.XboxLiveCaptureAnalysis.ReportViews.RuleDataViews
{
    /// <summary>
    /// Interaction logic for PollingDetectionRuleDataView.xaml
    /// </summary>
    public partial class PollingDetectionRuleDataView : UserControl
    {
        public PollingDetectionRuleDataView()
        {
            InitializeComponent();
            PollingTitle.Text = Localization.GetLocalizedString("LTA_POLL_CALLS_SEQUENCES_TITLE");
        }
    }
}
