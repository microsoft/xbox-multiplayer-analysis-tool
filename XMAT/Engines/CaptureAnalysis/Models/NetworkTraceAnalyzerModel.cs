// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace XMAT.NetworkTraceCaptureAnalysis.Models
{
    internal class NetworkTraceAnalyzerResultsDataModel
    {
        public string Name { get; set; }
        public int Value { get; set; }
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

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
