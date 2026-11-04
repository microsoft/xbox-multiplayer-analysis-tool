// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using XMAT.Telemetry;
using XMAT.XboxLiveCaptureAnalysis.ReportModels.StatsReport;

namespace XMAT.XboxLiveCaptureAnalysis
{
    /// <summary>
    /// Interaction logic for StatsReportView.xaml
    /// </summary>
    public partial class StatsReportView : UserControl
    {
        private readonly TelemetryNavigationTracker _navigation = new(XmatTelemetryService.Current);
        public StatsReportView()
        {
            InitializeComponent();
            DataContextChanged += StatsReportView_DataContextChanged;
        }

        private void StatsReportView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // Report tab switches detach this view and clear its inherited context;
            // WebView2 throws if Source is reset to null, so keep the current report.
            if (e.NewValue is StatsReportModel model &&
                Uri.TryCreate(model.ReportUrl, UriKind.Absolute, out var reportUri))
            {
                webView.Source = reportUri;
            }
        }

        private void Navigation_Starting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            _navigation.Start(e.NavigationId, IsVisible);
        }

        private void Navigation_Completed(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            _navigation.Complete(e.NavigationId, e.IsSuccess);
        }

        private void View_Unloaded(object sender, RoutedEventArgs e)
        {
            _navigation.Cancel();
        }
    }
}
