// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CaptureAnalysisEngine;
using XMAT.SharedInterfaces;

namespace XMAT.XboxLiveCaptureAnalysis.ReportModels.PerEndpointReport
{
    internal class PerEndpointReportModel : ReportViewModel
    {
        public override string ReportName { get { return Localization.GetLocalizedString("LTA_ENDPOINT_REPORT_TITLE"); } }
        public EndpointValidationIssuesSummary Summary { get; }
        public EndpointValidationIssuesDetails Details { get; }

        public PerEndpointReportModel(
            ICaptureAppModel captureAppModel,
            string sourceCaptureName,
            PerEndpointReportDocument reportDocument) :
            base(captureAppModel, sourceCaptureName, reportDocument)
        {
            Summary = new EndpointValidationIssuesSummary(
                captureAppModel,
                sourceCaptureName,
                reportDocument);
            Details = new EndpointValidationIssuesDetails(
                captureAppModel,
                sourceCaptureName,
                reportDocument);
        }
    }
}
