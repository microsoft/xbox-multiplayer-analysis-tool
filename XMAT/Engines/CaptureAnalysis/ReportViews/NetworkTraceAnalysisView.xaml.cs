// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Windows.Controls;

namespace XMAT.NetworkTraceCaptureAnalysis
{
    /// <summary>
    /// Interaction logic for NetworkTraceAnalysisView.xaml
    /// </summary>
    public partial class NetworkTraceAnalysisView : UserControl
    {
        public NetworkTraceAnalysisView()
        {
            InitializeComponent();

            NetAnalysisTitle.Text = Localization.GetLocalizedString("NETCAP_ANALYSIS_TITLE");
            TotalPacketsLabel.Text = Localization.GetLocalizedString("NETCAP_ANALYSIS_TOTAL");
        }
    }
}
