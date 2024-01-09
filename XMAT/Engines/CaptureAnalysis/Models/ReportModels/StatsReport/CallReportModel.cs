// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CaptureAnalysisEngine;
using XMAT.SharedInterfaces;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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

            // populate the newly copied "calls.js" file's token with
            // the actual JSON from the report

            string statsJsonText = reportDocument.ToJson();

            string templatefilepath = Path.Combine(analysisStorageDirectory, @"calls.js");
            string templateText = File.ReadAllText(templatefilepath);
            string replacedText = templateText.Replace(TemplateToken, statsJsonText);
            File.WriteAllText(templatefilepath, replacedText);
        }
    }
}
