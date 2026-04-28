// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows.Controls;

namespace XMAT.NetworkTraceCaptureAnalysis
{
    /// <summary>
    /// Interaction logic for NetworkTraceAnalysisOptions.xaml
    /// </summary>
    public partial class NetworkTraceAnalysisOptions : UserControl
    {
        public NetworkTraceAnalysisOptions()
        {
            InitializeComponent();

            PacketsPerSecondLabel.Text = Localization.GetLocalizedString("NETCAP_ANALYSIS_PPS");
            MaxMtuLabel.Text = Localization.GetLocalizedString("NETCAP_ANALYSIS_MTU");
            DuplicatePacketLabel.Text = Localization.GetLocalizedString("NETCAP_ANALYSIS_DUP");
        }
    }
}
