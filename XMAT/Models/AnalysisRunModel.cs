// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using XMAT.Operations.Processes;
using XMAT.SharedInterfaces;

namespace XMAT.Models
{
    public class AnalysisRunModel : ICaptureAnalysisRun, INotifyPropertyChanged
    {
        private ProcessFilterSpec _processFilter = ProcessFilterSpec.Empty;

        public Int64 Id { get; }
        public string Header { get; set; }
        public UserControl Content { get; set; }
        public Visibility CloseVisibility { get; set; }
        public ICaptureAnalyzer SourceAnalyzer { get; }
        public object AnalysisData { get; set; }

        // Set once before the run starts; null means no process filter.
        public ProcessFilterSpec ProcessFilter
        {
            get => _processFilter;
            internal set => _processFilter = value ?? ProcessFilterSpec.Empty;
        }

        public AnalysisRunModel(ICaptureAnalyzer analyzer, Int64 id)
        {
            Id = id;
            SourceAnalyzer = analyzer;
        }

        public bool IsProcessing
        {
            get
            {
                return CloseVisibility == Visibility.Hidden;
            }
            set
            {
                CloseVisibility = value ? Visibility.Hidden : Visibility.Visible;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CloseVisibility)));
            }
        }

        public void Release()
        {
            // TODO: is this still needed?
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
