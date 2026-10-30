// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Windows.Data;
using XMAT.Models;
using XMAT.SharedInterfaces;
using XMAT.XboxLiveCaptureAnalysis;
using XMAT.XboxLiveCaptureAnalysis.Models;

namespace XMAT
{
    internal static class AnalysisReportExportCommand
    {
        internal static bool CanExecute(
            IEnumerable<ICaptureAnalysisRun> analysisRuns)
        {
            return SnapshotSelected(analysisRuns) != null;
        }

        internal static AnalysisExportSource SnapshotSelected(
            IEnumerable<ICaptureAnalysisRun> analysisRuns)
        {
            if (analysisRuns == null)
            {
                return null;
            }

            var view = CollectionViewSource.GetDefaultView(analysisRuns);
            if (view?.CurrentItem is not AnalysisRunModel run ||
                run.SourceAnalyzer is not XboxLiveCaptureAnalyzer ||
                run.AnalysisData is not AnalysisResultsModel results ||
                !results.IsComplete ||
                string.IsNullOrEmpty(results.OutputDirectory) ||
                results.CompletedAt == default ||
                results.ArtifactManifest == null ||
                results.ArtifactManifest.Count == 0)
            {
                return null;
            }

            return new AnalysisExportSource(
                results.OutputDirectory,
                results.SourceCaptureName,
                results.CompletedAt,
                results.ArtifactManifest);
        }
    }
}
