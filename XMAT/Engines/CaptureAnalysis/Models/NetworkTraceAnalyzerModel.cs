// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using XMAT.SharedInterfaces;

namespace XMAT.NetworkTraceCaptureAnalysis.Models
{
    internal class NetworkTraceAnalyzerResultsDataModel
    {
        public string Name { get; set; }
        public long Value { get; set; }
    }

    internal class NetworkTraceAnalyzerResultsModel
    {
        public string Topic { get; set; }
        public IEnumerable<NetworkTraceAnalyzerResultsDataModel> Values { get; set; }
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
            }
        }

        public ObservableCollection<NetworkTraceAnalyzerResultsModel> NumericStatsLists { get; }
        public ObservableCollection<NetworkTraceAnalyzerResultsDataModel> NumericStats { get; }
        internal NetworkTraceStatisticsResult CanonicalAnalysis { get; set; }
        internal CaptureDataResult InvestigationData { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
