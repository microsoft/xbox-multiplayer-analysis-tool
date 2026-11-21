// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.Windows.Controls;
using XMAT.NetworkTraceCaptureAnalysis.Models;

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

        internal void CommitDraft()
        {
            if (!int.TryParse(PacketsPerSecondValue.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var threshold) ||
                !int.TryParse(MaximumMtuValue.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var mtu) ||
                !int.TryParse(DuplicateWindowValue.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var window))
                throw new ArgumentException(Localization.GetLocalizedString("CTRL_PREF_INVALID"));
            var draft = (NetworkTraceAnalysisOptionsModel)DataContext;
            draft.PacketsPerSecondThreshold = threshold;
            draft.MaximumMTU = mtu;
            draft.DuplicatePacketWindow = window;
        }
    }
}
