// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CaptureAnalysisEngine;
using XMAT.Models;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using XMAT.XboxLiveCaptureAnalysis.ReportModels;
using XMAT.XboxLiveCaptureAnalysis.ReportModels.PerEndpointReport;
using XMAT.XboxLiveCaptureAnalysis.ReportViews.Controls;

namespace XMAT.XboxLiveCaptureAnalysis
{
    /// <summary>
    /// Interaction logic for PerEndpointReportView.xaml
    /// </summary>
    public partial class PerEndpointReportView : UserControl
    {
        public PerEndpointReportView()
        {
            InitializeComponent();

            SummaryLabel.Text = Localization.GetLocalizedString("LTA_ENDPOINT_REPORT_SUMMARY_LABEL");
            DetailsLabel.Text = Localization.GetLocalizedString("LTA_ENDPOINT_REPORT_DETAILS_LABEL");

            EndpointSummary.Columns[0].Header = Localization.GetLocalizedString("LTA_SUMMARY_HEADER_ENDPOINT_NAME");
            EndpointSummary.Columns[1].Header = Localization.GetLocalizedString("LTA_SUMMARY_HEADER_BATCH_FREQ");
            EndpointSummary.Columns[2].Header = Localization.GetLocalizedString("LTA_SUMMARY_HEADER_BURST_DETECT");
            EndpointSummary.Columns[3].Header = Localization.GetLocalizedString("LTA_SUMMARY_HEADER_CALL_FREQ");
            EndpointSummary.Columns[4].Header = Localization.GetLocalizedString("LTA_SUMMARY_HEADER_POLL_DETECT");
            EndpointSummary.Columns[5].Header = Localization.GetLocalizedString("LTA_SUMMARY_HEADER_REPEAT_CALLS");
            EndpointSummary.Columns[6].Header = Localization.GetLocalizedString("LTA_SUMMARY_HEADER_SMALL_BATCH");
            EndpointSummary.Columns[7].Header = Localization.GetLocalizedString("LTA_SUMMARY_HEADER_THROTTLED_CALL");
        }

        public void MakeEndpointVisible(EndpointKey endpoint)
        {
            ContentPresenter endPointElement = GetContentForEndpoint(endpoint);
            endPointElement?.BringIntoView();
        }

        // Tells the user why a request cannot be shown, with a message box unless tests replace it.
        internal Action<string> ShowNavigationWarning { get; set; }

        // Goes to a request in the tab that the report analyzed, found by reference (AnalysisSource), never to a tab that
        // merely has its name: another tab, live or loaded, can have the same name and the same request numbers. While that
        // tab is closed, or no longer shows the rows that were analyzed, nothing is selected and the user is told why.
        private void GoToEndpoint_Id(object sender, RoutedEventArgs e)
        {
            if (DataContext is not PerEndpointReportModel reportModel ||
                e is not ExecutedRoutedEventArgs { Parameter: not null } args ||
                !long.TryParse(
                    args.Parameter.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out long requestNumber))
            {
                return;
            }

            e.Handled = true;
            AnalysisSource source = reportModel.Source;
            ICaptureAppModel app = reportModel.CaptureAppModel;
            if (source == null ||
                app == null ||
                !app.CaptureDeviceContexts.Any(context => ReferenceEquals(context.CaptureController, source.Controller)))
            {
                WarnNavigation(Localization.GetLocalizedString(
                    "LTA_GOTO_REQUEST_SOURCE_CLOSED", requestNumber, reportModel.SourceCaptureName));
                return;
            }

            ProxyConnectionModel request = source.FindRequest(requestNumber);
            if (request == null)
            {
                WarnNavigation(Localization.GetLocalizedString(
                    "LTA_GOTO_REQUEST_SOURCE_CHANGED", requestNumber, reportModel.SourceCaptureName));
                return;
            }

            app.SelectCaptureDeviceContext(source.Controller);
            if (!ReferenceEquals(source.Controller.SelectedConnectionModel, request))
            {
                source.Controller.SelectedConnectionModel = request;
            }
        }

        private void WarnNavigation(string message)
        {
            if (ShowNavigationWarning != null)
            {
                ShowNavigationWarning(message);
                return;
            }

            Window owner = Window.GetWindow(this);
            string title = Localization.GetLocalizedString("WEBCAP_ANALYZER_TITLE");
            if (owner == null)
            {
                MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void GoToEndpoint_Executed(object sender, RoutedEventArgs e)
        {
            MakeEndpointVisible(EndpointParameter(e));
        }

        // Summary rows and detail headers act on their own typed endpoint and its physical
        // host provenance, never on display text or the selected capture row.
        private void HostListMenuOpening(object sender, ContextMenuEventArgs e)
        {
            FrameworkElement owner = HostListContextMenu.FindOwner(e.OriginalSource);
            string host;
            switch (owner?.DataContext)
            {
                case EndpointValidationCounts summary:
                    host = summary.ActionableHost;
                    break;
                case EndpointValidationDetails details:
                    host = details.ActionableHost;
                    break;
                default:
                    return;
            }

            HostListPolicy policy = ((DataContext as PerEndpointReportModel)?.CaptureAppModel as CaptureAppModel)?.HostListPolicy;
            if (policy == null)
            {
                e.Handled = true;
                return;
            }
            HostListContextMenu.Populate(owner.ContextMenu, policy, host, "HOST_LIST_NO_PHYSICAL_HOST", owner);
        }

        private static EndpointKey EndpointParameter(RoutedEventArgs e) =>
            e is ExecutedRoutedEventArgs { Parameter: EndpointKey endpoint } ? endpoint : default;

        // Navigation uses the typed key: a captured endpoint and synthesized telemetry can
        // share a display name.
        internal ContentPresenter GetContentForEndpoint(EndpointKey endpointKey)
        {
            int matchingEndpointIndex = 0;
            foreach (EndpointValidationDetails endpoint in EndpointStack.Items)
            {
                if (endpoint.Key == endpointKey)
                {
                    return EndpointStack.ItemContainerGenerator.ContainerFromIndex(matchingEndpointIndex)
                        as ContentPresenter;
                }
                matchingEndpointIndex++;
            }
            return null;
        }

        private void ShowRule(RoutedEventArgs e, string ruleName)
        {
            ContentPresenter endPointElement = GetContentForEndpoint(EndpointParameter(e));
            if (endPointElement != null && VisualTreeHelper.GetChild(endPointElement, 0) is PerEndpointDetails detailsView)
            {
                detailsView.MakeRuleVisible(ruleName);
            }
        }

        private void GoToEndpointBfr_Executed(object sender, RoutedEventArgs e)
        {
            ShowRule(e, BatchFrequencyRule.DisplayName);
        }

        private void GoToEndpointBdr_Executed(object sender, RoutedEventArgs e)
        {
            ShowRule(e, BurstDetectionRule.DisplayName);
        }

        private void GoToEndpointCfr_Executed(object sender, RoutedEventArgs e)
        {
            ShowRule(e, CallFrequencyRule.DisplayName);
        }

        private void GoToEndpointPdr_Executed(object sender, RoutedEventArgs e)
        {
            ShowRule(e, PollingDetectionRule.DisplayName);
        }

        private void GoToEndpointRcr_Executed(object sender, RoutedEventArgs e)
        {
            ShowRule(e, RepeatedCallsRule.DisplayName);
        }

        private void GoToEndpointSbdr_Executed(object sender, RoutedEventArgs e)
        {
            ShowRule(e, SmallBatchDetectionRule.DisplayName);
        }

        private void GoToEndpointTcr_Executed(object sender, RoutedEventArgs e)
        {
            ShowRule(e, ThrottledCallsRule.DisplayName);
        }
    }
}
