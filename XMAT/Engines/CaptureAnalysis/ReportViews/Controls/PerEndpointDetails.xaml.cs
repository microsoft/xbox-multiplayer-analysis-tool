// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Windows.Controls;
using XMAT.XboxLiveCaptureAnalysis.ReportModels.PerEndpointReport;

namespace XMAT.XboxLiveCaptureAnalysis.ReportViews.Controls
{
    /// <summary>
    /// Interaction logic for PerEndpointDetails.xaml
    /// </summary>
    public partial class PerEndpointDetails : UserControl
    {
        public PerEndpointDetails()
        {
            InitializeComponent();
        }

        public void MakeRuleVisible(string ruleName)
        {
            ContentPresenter ruleElement = GetContentForRule(ruleName);
            ruleElement?.BringIntoView();
        }

        private ContentPresenter GetContentForRule(string ruleName)
        {
            int matchingEndpointIndex = 0;
            foreach (EndpointValidationRuleDetails ruleDetails in EndpointRuleStack.Items)
            {
                if (ruleDetails.IssueCount.RuleName == ruleName)
                {
                    return EndpointRuleStack.ItemContainerGenerator.ContainerFromIndex(matchingEndpointIndex)
                        as ContentPresenter;
                }
                matchingEndpointIndex++;
            }
            return null;
        }
    }
}
