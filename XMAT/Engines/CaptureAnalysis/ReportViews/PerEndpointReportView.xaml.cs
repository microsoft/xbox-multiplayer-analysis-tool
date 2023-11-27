// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CaptureAnalysisEngine;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using XMAT.XboxLiveCaptureAnalysis.ReportModels.PerEndpointReport;
using XMAT.XboxLiveCaptureAnalysis.ReportViews.Controls;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

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

        public void MakeEndpointVisible(string endpointName)
        {
            ContentPresenter endPointElement = GetContentForEndpoint(endpointName);
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
            string endpointName = (e as ExecutedRoutedEventArgs).Parameter as string;
            MakeEndpointVisible(endpointName);
        }

        private ContentPresenter GetContentForEndpoint(string endpointName)
        {
            int matchingEndpointIndex = 0;
            foreach (EndpointValidationDetails endpoint in EndpointStack.Items)
            {
                if (endpoint.EndpointName == endpointName)
                {
                    return EndpointStack.ItemContainerGenerator.ContainerFromIndex(matchingEndpointIndex)
                        as ContentPresenter;
                }
                matchingEndpointIndex++;
            }
            return null;
        }

        private void GoToEndpointBfr_Executed(object sender, RoutedEventArgs e)
        {
            string endpointName = (e as ExecutedRoutedEventArgs).Parameter as string;
            ContentPresenter endPointElement = GetContentForEndpoint(endpointName);
            var detailsView = VisualTreeHelper.GetChild(endPointElement, 0) as PerEndpointDetails;
            detailsView.MakeRuleVisible(BatchFrequencyRule.DisplayName);
        }

        private void GoToEndpointBdr_Executed(object sender, RoutedEventArgs e)
        {
            string endpointName = (e as ExecutedRoutedEventArgs).Parameter as string;
            ContentPresenter endPointElement = GetContentForEndpoint(endpointName);
            var detailsView = VisualTreeHelper.GetChild(endPointElement, 0) as PerEndpointDetails;
            detailsView.MakeRuleVisible(BurstDetectionRule.DisplayName);
        }

        private void GoToEndpointCfr_Executed(object sender, RoutedEventArgs e)
        {
            string endpointName = (e as ExecutedRoutedEventArgs).Parameter as string;
            ContentPresenter endPointElement = GetContentForEndpoint(endpointName);
            var detailsView = VisualTreeHelper.GetChild(endPointElement, 0) as PerEndpointDetails;
            detailsView.MakeRuleVisible(CallFrequencyRule.DisplayName);
        }

        private void GoToEndpointPdr_Executed(object sender, RoutedEventArgs e)
        {
            string endpointName = (e as ExecutedRoutedEventArgs).Parameter as string;
            ContentPresenter endPointElement = GetContentForEndpoint(endpointName);
            var detailsView = VisualTreeHelper.GetChild(endPointElement, 0) as PerEndpointDetails;
            detailsView.MakeRuleVisible(PollingDetectionRule.DisplayName);
        }

        private void GoToEndpointRcr_Executed(object sender, RoutedEventArgs e)
        {
            string endpointName = (e as ExecutedRoutedEventArgs).Parameter as string;
            ContentPresenter endPointElement = GetContentForEndpoint(endpointName);
            var detailsView = VisualTreeHelper.GetChild(endPointElement, 0) as PerEndpointDetails;
            detailsView.MakeRuleVisible(RepeatedCallsRule.DisplayName);
        }

        private void GoToEndpointSbdr_Executed(object sender, RoutedEventArgs e)
        {
            string endpointName = (e as ExecutedRoutedEventArgs).Parameter as string;
            ContentPresenter endPointElement = GetContentForEndpoint(endpointName);
            var detailsView = VisualTreeHelper.GetChild(endPointElement, 0) as PerEndpointDetails;
            detailsView.MakeRuleVisible(SmallBatchDetectionRule.DisplayName);
        }

        private void GoToEndpointTcr_Executed(object sender, RoutedEventArgs e)
        {
            string endpointName = (e as ExecutedRoutedEventArgs).Parameter as string;
            ContentPresenter endPointElement = GetContentForEndpoint(endpointName);
            var detailsView = VisualTreeHelper.GetChild(endPointElement, 0) as PerEndpointDetails;
            detailsView.MakeRuleVisible(ThrottledCallsRule.DisplayName);
        }
    }
}
