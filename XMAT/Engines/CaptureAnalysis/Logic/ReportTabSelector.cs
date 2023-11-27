// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using XMAT.XboxLiveCaptureAnalysis.ReportModels;
using XMAT.XboxLiveCaptureAnalysis.ReportModels.PerEndpointReport;
using XMAT.XboxLiveCaptureAnalysis.ReportModels.StatsReport;

namespace XMAT.XboxLiveCaptureAnalysis
{
    class ReportTabSelector : DataTemplateSelector
    {
        public DataTemplate PerEndpointReportTemplate { get; set; }
        public DataTemplate StatsReportTemplate { get; set; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            InitializeDataTemplateProviders();

            var context = item as ReportViewModel;
            DataTemplate dataTemplate;

            if (!DataTemplateProviders.TryGetValue(context.GetType(), out dataTemplate))
            {
                throw new NotImplementedException($"Report type not supported: {context.GetType().Name}");
            }

            return dataTemplate;
        }

        private void InitializeDataTemplateProviders()
        {
            if (DataTemplateProviders.Count == 0)
            {
                DataTemplateProviders.Add(
                    typeof(PerEndpointReportModel),
                    PerEndpointReportTemplate);
                DataTemplateProviders.Add(
                    typeof(StatsReportModel),
                    StatsReportTemplate);

                // add additional view data templates for reports here...
            }
        }

        private readonly Dictionary<Type, DataTemplate> DataTemplateProviders = new();
    }
}
