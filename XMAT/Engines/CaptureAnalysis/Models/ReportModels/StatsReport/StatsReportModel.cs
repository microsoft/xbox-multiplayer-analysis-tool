// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using CaptureAnalysisEngine;
using XMAT.SharedInterfaces;

namespace XMAT.XboxLiveCaptureAnalysis.ReportModels.StatsReport
{
    internal class StatsReportModel : ReportViewModel
    {
        public override string ReportName { get { return Localization.GetLocalizedString("LTA_REPORT_STATS_TITLE"); } }

        public string ReportUrl { get; }

        public StatsReportModel(
            ICaptureAppModel captureAppModel,
            string sourceCaptureName,
            string analysisStorageDirectory,
            StatsReportDocument reportDocument) :
            base(captureAppModel, sourceCaptureName, reportDocument)
        {
            ReportUrl = $"file:///{analysisStorageDirectory}/index.html";

            AnalysisReportArtifactWriter.WriteStats(analysisStorageDirectory, reportDocument.ToJson());
        }
    }
}
