// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Linq;
using XMAT.NetworkTraceCaptureAnalysis.Models;

namespace XMAT.DesignSystem
{
    public partial class HistogramsStory : Story
    {
        public HistogramsStory()
        {
            InitializeComponent();
            FewValues.DataContext = Histogram(
                "NETCAP_ANALYSIS_RESULT_PSBS", NetworkTraceMeasure.Packets, NetworkTraceGrouping.Source,
                ("192.168.1.20", 582), ("192.168.1.30", 14), ("192.168.1.1", 12));
            HistogramModel received = Histogram(
                "NETCAP_ANALYSIS_RESULT_PRBS", NetworkTraceMeasure.Packets, NetworkTraceGrouping.Source,
                ("203.0.113.31", 254), ("198.51.100.10", 89), ("203.0.113.24", 38), ("192.168.1.1", 37),
                ("203.0.113.36", 22), ("192.0.2.7", 21), ("198.51.100.95", 19), ("192.0.2.122", 16), ("192.0.2.127", 9),
                ("198.51.100.4", 3));
            ManyValues.DataContext = received;
            AllValues.DataContext = received;
            Averages.DataContext = Histogram(
                "NETCAP_ANALYSIS_RESULT_PLBS", NetworkTraceMeasure.AverageBytes, NetworkTraceGrouping.Source,
                ("203.0.113.31", 839), ("203.0.113.36", 769), ("192.0.2.7", 487), ("203.0.113.24", 397),
                ("198.51.100.10", 313), ("192.168.1.1", 217), ("192.0.2.122", 191), ("198.51.100.95", 94));
            NoValues.DataContext = Histogram(
                "NETCAP_ANALYSIS_RESULT_PSBS", NetworkTraceMeasure.Packets, NetworkTraceGrouping.Source);
            Protocols.DataContext = Histogram(
                "NETCAP_ANALYSIS_RESULT_PCBP", NetworkTraceMeasure.Packets, NetworkTraceGrouping.Protocol,
                ("6 (TCP)", 684), ("17 (UDP)", 558), ("0 (HOPOPT)", 14), ("108", 12));
            BytesSent.DataContext = Histogram(
                "NETCAP_ANALYSIS_RESULT_BSBS", NetworkTraceMeasure.Bytes, NetworkTraceGrouping.Source,
                ("192.168.1.20", 133287), ("192.168.1.30", 5088), ("192.168.1.1", 1644));
        }

        // A list of the network capture analysis, as the analyzer makes it.
        private static HistogramModel Histogram(
            string topic, NetworkTraceMeasure measure, NetworkTraceGrouping groupedBy, params (string Name, int Value)[] values) =>
            new NetworkTraceAnalyzerResultsModel
            {
                Topic = Localization.GetLocalizedString(topic),
                Measure = measure,
                GroupedBy = groupedBy,
                Values = values
                    .Select(value => new NetworkTraceAnalyzerResultsDataModel { Name = value.Name, Value = value.Value })
                    .ToList(),
            }.Histogram;
    }
}
