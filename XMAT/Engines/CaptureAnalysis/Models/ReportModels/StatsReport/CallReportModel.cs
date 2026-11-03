// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using CaptureAnalysisEngine;
using XMAT.SharedInterfaces;

namespace XMAT.XboxLiveCaptureAnalysis.ReportModels.StatsReport
{
    internal class CallReportModel : ReportViewModel
    {
        public override string ReportName { get { return Localization.GetLocalizedString("LTA_REPORT_CALLS_TITLE"); } }

        public override bool Show { get { return false; } }

        public CallReportModel(
            ICaptureAppModel captureAppModel,
            string sourceCaptureName,
            string analysisStorageDirectory,
            CallReportDocument reportDocument) :
            base(captureAppModel, sourceCaptureName, reportDocument)
        {
            AnalysisReportArtifactWriter.WriteCalls(analysisStorageDirectory, reportDocument.ToJson());
        }
    }
}
