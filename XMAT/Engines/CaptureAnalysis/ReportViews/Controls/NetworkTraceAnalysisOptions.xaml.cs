// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

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
