// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
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
            Reports = new();
            ArtifactManifest = Array.Empty<AnalysisArtifact>();
        }

        private ProcessStep _activeProcessStep;
        public ProcessStep ActiveProcessStep
        {
            get => _activeProcessStep;
            set
            {
                if (value == ProcessStep.Complete &&
                    _activeProcessStep != ProcessStep.Complete)
                {
                    FinalizeExportMetadata();
                }
                _activeProcessStep = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveProcessStep)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveProcessStepDesc)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsProcessing)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsComplete)));
            }
        }

        public string ActiveProcessStepDesc { get => ActiveProcessStep.ToString(); }
        public bool IsProcessing { get => ActiveProcessStep != ProcessStep.NotStarted && ActiveProcessStep != ProcessStep.Complete; }
        public bool IsComplete { get => ActiveProcessStep == ProcessStep.Complete; }

        public string OutputDirectory { get; internal set; }
        public string SourceCaptureName { get; internal set; }
        public DateTime CompletedAt { get; internal set; }
        public IReadOnlyList<AnalysisArtifact> ArtifactManifest { get; internal set; }

        public ObservableCollection<ReportViewModel> Reports { get; }

        public event PropertyChangedEventHandler PropertyChanged;

        private void FinalizeExportMetadata()
        {
            CompletedAt = DateTime.Now;
            try
            {
                if (string.IsNullOrEmpty(OutputDirectory) ||
                    !Directory.Exists(OutputDirectory))
                {
                    ArtifactManifest = Array.Empty<AnalysisArtifact>();
                    return;
                }

                // Completion runs on the dispatcher; content hashing is
                // deferred to the background export so large calls.js files
                // do not block the UI.
                AnalysisArtifact[] artifacts = Directory.EnumerateFiles(
                        OutputDirectory,
                        "*",
                        SearchOption.TopDirectoryOnly)
                    .Select(path => new AnalysisArtifact(
                        Path.GetFileName(path),
                        new FileInfo(path).Length))
                    .OrderBy(
                        artifact => artifact.RelativePath,
                        StringComparer.Ordinal)
                    .ToArray();
                ArtifactManifest = Array.AsReadOnly(artifacts);
            }
            catch (Exception exception) when (
                !ExceptionUtilities.IsFatal(exception))
            {
                ArtifactManifest = Array.Empty<AnalysisArtifact>();
                try
                {
                    PublicUtilities.AppLog(
                        LogLevel.ERROR,
                        $"Analysis export metadata could not be finalized: " +
                        $"{exception}");
                }
                catch (Exception logException) when (
                    !ExceptionUtilities.IsFatal(logException))
                {
                }
            }
        }
    }
}
