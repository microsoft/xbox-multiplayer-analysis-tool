// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows.Controls;
using XMAT.Telemetry;
using XMAT.XboxLiveCaptureAnalysis.ReportModels.PerEndpointReport;
using XMAT.XboxLiveCaptureAnalysis.ReportModels.StatsReport;

namespace XMAT.XboxLiveCaptureAnalysis
{
    /// <summary>
    /// Interaction logic for AnalysisReportsView.xaml
    /// </summary>
    public partial class AnalysisReportsView : UserControl
    {
        public AnalysisReportsView()
        {
            InitializeComponent();
            Loaded += (_, _) => RecordReport();
        }

        private void ReportTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source == ReportTabs)
            {
                RecordReport();
            }
        }

        private void RecordReport()
        {
            if (IsLoaded && IsVisible)
            {
                XmatTelemetryService.Current.Use(ReportTabs.SelectedItem switch
                {
                    PerEndpointReportModel => TelemetryFeature.PerEndpointReport,
                    StatsReportModel => TelemetryFeature.StatisticsReport,
                    _ => TelemetryFeature.None
                });
            }
        }
    }
}
