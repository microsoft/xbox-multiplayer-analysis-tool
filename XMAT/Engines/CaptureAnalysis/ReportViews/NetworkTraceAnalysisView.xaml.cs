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
