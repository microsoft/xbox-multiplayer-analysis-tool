// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Windows.Controls;

namespace XMAT.XboxLiveCaptureAnalysis.ReportViews.RuleDataViews
{
    /// <summary>
    /// Interaction logic for BatchFrequencyRuleDataView.xaml
    /// </summary>
    public partial class BatchFrequencyRuleDataView : UserControl
    {
        public static string TotalBatchCallsLabel { get { return Localization.GetLocalizedString("LTA_BATCH_CALLS_DATA_TOTAL"); } }
        public static string AllowedTimeBetweenCallsLabel { get { return Localization.GetLocalizedString("LTA_BATCH_CALLS_DATA_ALLOWED"); } }
        public static string TimesExceededLabel { get { return Localization.GetLocalizedString("LTA_BATCH_CALLS_DATA_EXCEEDED"); } }
        public static string PotentialReducedCallCountLabel { get { return Localization.GetLocalizedString("LTA_BATCH_CALLS_DATA_REDUCE"); } }

        public BatchFrequencyRuleDataView()
        {
            InitializeComponent();
        }
    }
}
