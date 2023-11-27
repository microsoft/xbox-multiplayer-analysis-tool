// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using XMAT.XboxLiveCaptureAnalysis.ReportModels.RuleDataModels;

namespace XMAT.XboxLiveCaptureAnalysis
{
    internal class RuleContentSelector : DataTemplateSelector
    {
        public DataTemplate BatchFrequencyRuleTemplate { get; set; }
        public DataTemplate BurstDetectionRuleTemplate { get; set; }
        public DataTemplate CallFrequencyRuleTemplate { get; set; }
        public DataTemplate PollingDetectionRuleTemplate { get; set; }
        public DataTemplate RepeatedCallsRuleTemplate { get; set; }
        public DataTemplate SmallBatchDetectionRuleTemplate { get; set; }
        public DataTemplate ThrottledCallRuleTemplate { get; set; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            InitializeDataTemplateProviders();

            if (item == null)
            {
                return null;
            }

            DataTemplate dataTemplate;

            if (!DataTemplateProviders.TryGetValue(item.GetType(), out dataTemplate))
            {
                throw new NotImplementedException($"Rule type not supported: {item.GetType().Name}");
            }

            return dataTemplate;
        }

        private void InitializeDataTemplateProviders()
        {
            if (DataTemplateProviders.Count == 0)
            {
                DataTemplateProviders.Add(
                    typeof(BatchFrequencyRuleDataModel),
                    BatchFrequencyRuleTemplate);
                DataTemplateProviders.Add(
                    typeof(BurstDetectionRuleDataModel),
                    BurstDetectionRuleTemplate);
                DataTemplateProviders.Add(
                    typeof(CallFrequencyRuleDataModel),
                    CallFrequencyRuleTemplate);
                DataTemplateProviders.Add(
                    typeof(PollingDetectionRuleDataModel),
                    PollingDetectionRuleTemplate);
                DataTemplateProviders.Add(
                    typeof(RepeatedCallsRuleDataModel),
                    RepeatedCallsRuleTemplate);
                DataTemplateProviders.Add(
                    typeof(SmallBatchDetectionRuleDataModel),
                    SmallBatchDetectionRuleTemplate);
                DataTemplateProviders.Add(
                    typeof(ThrottledCallRuleDataModel),
                    ThrottledCallRuleTemplate);

                // add additional view data templates for rules here...
            }
        }

        private readonly Dictionary<Type, DataTemplate> DataTemplateProviders = new();
    }
}
