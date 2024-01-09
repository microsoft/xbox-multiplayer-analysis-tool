// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.ObjectModel;
using System.ComponentModel;
using XMAT.XboxLiveCaptureAnalysis.ReportModels;

namespace XMAT.XboxLiveCaptureAnalysis.Models
{
    internal class AnalysisResultsModel : INotifyPropertyChanged
    {
        internal enum ProcessStep : int
        {
            NotStarted = 0,
            ConvertingProxyConnectionsToServiceCallItems = 1,
            CreatingServiceDataFromCallItems = 2,
            RunningUrlConverterOnData = 3,
            RunningValidationRules = 4,
            DumpingServiceCallData = 5,
            DumpingServiceCallItems = 6,
            DumpingRuleResults = 7,
            Complete = 8
        }

        internal AnalysisResultsModel()
        {
            ActiveProcessStep = ProcessStep.NotStarted;
            Reports = new ();
        }

        private ProcessStep _activeProcessStep;
        public ProcessStep ActiveProcessStep
        {
            get => _activeProcessStep;
            set
            {
                _activeProcessStep = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveProcessStep)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveProcessStepDesc)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsProcessing)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsComplete)));
            }
        }

        public string ActiveProcessStepDesc { get => ActiveProcessStep.ToString(); }
        public bool IsProcessing { get => ActiveProcessStep != ProcessStep.NotStarted && ActiveProcessStep != ProcessStep.Complete; }
        public bool IsComplete {  get => ActiveProcessStep == ProcessStep.Complete; }

        public ObservableCollection<ReportViewModel> Reports { get; }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
