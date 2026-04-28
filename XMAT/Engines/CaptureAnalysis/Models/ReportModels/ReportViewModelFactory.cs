// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using CaptureAnalysisEngine;
using XMAT.SharedInterfaces;
using XMAT.XboxLiveCaptureAnalysis.ReportModels.PerEndpointReport;
using XMAT.XboxLiveCaptureAnalysis.ReportModels.StatsReport;

namespace XMAT.XboxLiveCaptureAnalysis.ReportModels
{
#pragma warning disable IDE0065 // Using directives must be placed outside of a namespace declaration
    using ModelCreatorFunc = Func<
        // in as parameters
        ICaptureAppModel,
        string /*sourceCaptureName*/,
        string /*analysisStorageDirectory*/,
        ReportDocument,
        // out as return value
        ReportViewModel>;

    internal static class ReportViewModelFactory
    {
        public static ReportViewModel CreateForDocument(
            ICaptureAppModel captureAppModel,
            string sourceCaptureName,
            string analysisStorageDirectory,
            ReportDocument reportDocument)
        {
            if (ViewModelCreators.TryGetValue(
                reportDocument.GetType(),
                out ModelCreatorFunc creator))
            {
                return creator(
                    captureAppModel,
                    sourceCaptureName,
                    analysisStorageDirectory,
                    reportDocument);
            }
            return null;
        }

        private readonly static Dictionary<Type, ModelCreatorFunc>
            ViewModelCreators = new()
        {
            // PerEndpointReport
            {
                typeof(PerEndpointReportDocument),
                    (ICaptureAppModel captureAppModel,
                        string sourceCaptureName,
                        string analysisStorageDirectory,
                        ReportDocument reportDocument) =>
                {
                    return new PerEndpointReportModel(
                        captureAppModel,
                        sourceCaptureName,
                        reportDocument as PerEndpointReportDocument);
                }
            },

            // CallReport
            {
                typeof(CallReportDocument),
                    (ICaptureAppModel captureAppModel,
                        string sourceCaptureName,
                        string analysisStorageDirectory,
                        ReportDocument reportDocument) =>
                {
                    return new CallReportModel(
                        captureAppModel,
                        sourceCaptureName,
                        analysisStorageDirectory,
                        reportDocument as CallReportDocument);
                }
            },

            // StatsReport
            {
                typeof(StatsReportDocument),
                    (ICaptureAppModel captureAppModel,
                        string sourceCaptureName,
                        string analysisStorageDirectory,
                        ReportDocument reportDocument) =>
                {
                    return new StatsReportModel(
                        captureAppModel,
                        sourceCaptureName,
                        analysisStorageDirectory,
                        reportDocument as StatsReportDocument);
                }
            }
        };
    }
}
