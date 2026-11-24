// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace XMAT.NetworkTraceCaptureAnalysis.Models
{
    internal class NetworkTraceAnalyzerResultsDataModel
    {
        public string Name { get; set; }
        public int Value { get; set; }

        // The count as the new look shows it, such as "1,284".
        public string ValueText => ColumnSummaryModel.Number(Value);

        // What screen readers read for a check in the new look: "UDP Packets over MTU (1384): 0".
        public string AccessibleName => Localization.GetLocalizedString("NETCAP_ANALYSIS_CHECK_NAME", Name, ValueText);
    }

    // What a list's values measure.
    internal enum NetworkTraceMeasure
    {
        Packets,
        Bytes,
        // Bytes per packet, on average.
        AverageBytes,
    }

    // What a list's values are counted by.
    internal enum NetworkTraceGrouping
    {
        Protocol,
        Source,
    }

    internal class NetworkTraceAnalyzerResultsModel
    {
        private HistogramModel _histogram;

        public string Topic { get; set; }
        public IEnumerable<NetworkTraceAnalyzerResultsDataModel> Values { get; set; }
        public NetworkTraceMeasure Measure { get; set; }
        public NetworkTraceGrouping GroupedBy { get; set; }

        // The list as the new look shows it: largest first, with the units of its measure.
        public HistogramModel Histogram => _histogram ??= HistogramModel.Create(
            Topic,
            (Values ?? Enumerable.Empty<NetworkTraceAnalyzerResultsDataModel>())
                .Select(value => new HistogramValue(value.Name, value.Value)),
            Units(Measure, GroupedBy));

        internal static HistogramUnits Units(NetworkTraceMeasure measure, NetworkTraceGrouping groupedBy)
        {
            (string one, string many, string moreOne, string moreMany) = groupedBy == NetworkTraceGrouping.Protocol
                ? ("NETCAP_ANALYSIS_PROTOCOL", "NETCAP_ANALYSIS_PROTOCOLS", "NETCAP_ANALYSIS_MORE_PROTOCOL", "NETCAP_ANALYSIS_MORE_PROTOCOLS")
                : ("NETCAP_ANALYSIS_SOURCE", "NETCAP_ANALYSIS_SOURCES", "NETCAP_ANALYSIS_MORE_SOURCE", "NETCAP_ANALYSIS_MORE_SOURCES");
            (string amountOne, string amountMany) = measure switch
            {
                NetworkTraceMeasure.Bytes => ("NETCAP_ANALYSIS_BYTE", "NETCAP_ANALYSIS_BYTES"),
                NetworkTraceMeasure.AverageBytes => ("NETCAP_ANALYSIS_AVERAGE_BYTE", "NETCAP_ANALYSIS_AVERAGE_BYTES"),
                _ => ("COLUMN_SUMMARY_PACKET", "COLUMN_SUMMARY_PACKETS"),
            };
            return new HistogramUnits(
                amount => ColumnSummaryModel.Counted(amount, amountOne, amountMany),
                count => ColumnSummaryModel.Counted(count, one, many))
            {
                Caption = measure == NetworkTraceMeasure.AverageBytes
                    ? Localization.GetLocalizedString("NETCAP_ANALYSIS_AVERAGE_CAPTION")
                    : null,
                MoreValuesText = count => ColumnSummaryModel.Counted(count, moreOne, moreMany),
            };
        }
    }

    internal class NetworkTraceAnalyzerModel : INotifyPropertyChanged
    {
        internal NetworkTraceAnalyzerModel()
        {
            NumericStats = new();
            NumericStatsLists = new();
        }

        private int _totalPacketsScanned;
        public int TotalPacketsScanned
        {
            get => _totalPacketsScanned;
            set
            {
                _totalPacketsScanned = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TotalPacketsScanned)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ScannedText)));
            }
        }

        // "1,280 packets scanned", beside the new look's checks.
        public string ScannedText => ColumnSummaryModel.Counted(
            TotalPacketsScanned, "NETCAP_ANALYSIS_PACKET_SCANNED", "NETCAP_ANALYSIS_PACKETS_SCANNED");

        public ObservableCollection<NetworkTraceAnalyzerResultsModel> NumericStatsLists { get; }
        public ObservableCollection<NetworkTraceAnalyzerResultsDataModel> NumericStats { get; }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
