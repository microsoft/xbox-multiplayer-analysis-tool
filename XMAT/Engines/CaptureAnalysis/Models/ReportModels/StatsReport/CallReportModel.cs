// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.IO;
using CaptureAnalysisEngine;
using XMAT.SharedInterfaces;

namespace XMAT.XboxLiveCaptureAnalysis.ReportModels.StatsReport
{
    internal class CallReportModel : ReportViewModel
    {
        private const string TemplateToken = "${CallsReportJson}";

        public override string ReportName { get { return Localization.GetLocalizedString("LTA_REPORT_CALLS_TITLE"); } }

        public override bool Show { get { return false; } }

        public CallReportModel(
            ICaptureAppModel captureAppModel,
            string sourceCaptureName,
            string analysisStorageDirectory,
            CallReportDocument reportDocument) :
            base(captureAppModel, sourceCaptureName, reportDocument)
        {
            CopyResourceToAnalysisDirectory(
                "Resources/html/xlta/calls.js",
                analysisStorageDirectory);

            string templatefilepath = Path.Combine(analysisStorageDirectory, @"calls.js");
            WriteJsonTemplate(
                templatefilepath,
                TemplateToken,
                reportDocument);
        }
    }
}
