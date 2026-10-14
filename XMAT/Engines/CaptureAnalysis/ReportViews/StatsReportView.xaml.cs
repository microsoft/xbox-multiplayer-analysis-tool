// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows.Controls;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using XMAT.Telemetry;

namespace XMAT.XboxLiveCaptureAnalysis
{
    /// <summary>
    /// Interaction logic for StatsReportView.xaml
    /// </summary>
    public partial class StatsReportView : UserControl
    {
        private TelemetryOperation _navigation;
        public StatsReportView()
        {
            InitializeComponent();
        }

        private void Navigation_Starting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            _navigation?.Complete(TelemetryOutcome.Cancelled);
            _navigation = null;
            if (IsVisible)
            {
                _navigation = XmatTelemetryService.Current.Begin(TelemetryOperationKind.StatisticsRender, TelemetryFeature.StatisticsReport);
            }
        }

        private void Navigation_Completed(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            _navigation?.Complete(e.IsSuccess ? TelemetryOutcome.Succeeded : TelemetryOutcome.Failed,
                e.IsSuccess ? TelemetryError.None : TelemetryError.InvalidData);
            _navigation = null;
        }

        private void View_Unloaded(object sender, RoutedEventArgs e)
        {
            _navigation?.Complete(TelemetryOutcome.Cancelled);
            _navigation = null;
        }
    }
}
