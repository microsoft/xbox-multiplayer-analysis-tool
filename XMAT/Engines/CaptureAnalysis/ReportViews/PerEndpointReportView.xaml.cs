// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CaptureAnalysisEngine;
using XMAT.Models;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
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

        private void GoToEndpoint_Id(object sender, RoutedEventArgs e)
        {
            PerEndpointReportModel reportModel = DataContext as PerEndpointReportModel;
            // select the device by source name
            ICaptureDeviceContext context = reportModel.CaptureAppModel.SelectCaptureDeviceContext(reportModel.SourceCaptureName);
            if (context != null)
            {
                // select the capture by ID
                int requestId = int.Parse((e as ExecutedRoutedEventArgs).Parameter.ToString());
                WebServiceDeviceCaptureController controller =
                    context.CaptureController as WebServiceDeviceCaptureController;
                ProxyConnectionModel model = controller.ProxyConnections.ById(requestId);
                if (model != null && model != controller.SelectedConnectionModel)
                {
                    controller.SelectedConnectionModel = model;
                }
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
