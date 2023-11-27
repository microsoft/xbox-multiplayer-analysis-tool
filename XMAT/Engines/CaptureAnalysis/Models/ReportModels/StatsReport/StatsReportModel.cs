// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CaptureAnalysisEngine;
using XMAT.SharedInterfaces;
using XMAT.XboxLiveCaptureAnalysis.ReportModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Resources;

namespace XMAT.XboxLiveCaptureAnalysis.ReportModels.StatsReport
{
    internal class StatsReportModel : ReportViewModel
    {
        private const string TemplateToken = "${StatsReportJson}";

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

            foreach (string resourceName in HtmlResourcesToCopy)
            {
                CopyResourceToAnalysisDirectory(resourceName, analysisStorageDirectory);
            }

            // populate the newly copied "stats.js" file's token with
            // the actual JSON from the report

            string statsJsonText = reportDocument.ToJson();

            string templatefilepath = Path.Combine(analysisStorageDirectory, @"stats.js");
            string templateText = File.ReadAllText(templatefilepath);
            string replacedText = templateText.Replace(TemplateToken, statsJsonText);
            File.WriteAllText(templatefilepath, replacedText);
        }

        private string[] HtmlResourcesToCopy = new string[]
        {
            "Resources/html/xlta/index.html",
            "Resources/html/xlta/bootstrap.css",
            "Resources/html/xlta/site.css",
            "Resources/html/xlta/report.css",
            "Resources/html/xlta/jquery.js",
            "Resources/html/xlta/bootstrap.min.js",
            "Resources/html/xlta/report_view.js",
            "Resources/html/xlta/stats.js",
        };
    }
}
