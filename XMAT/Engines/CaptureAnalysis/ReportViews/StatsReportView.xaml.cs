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
        private readonly TelemetryNavigationTracker _navigation = new(XmatTelemetryService.Current);
        public StatsReportView()
        {
            InitializeComponent();
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
