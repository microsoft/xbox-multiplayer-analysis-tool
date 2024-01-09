// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using XMAT.Models;

namespace XMAT
{
    class CaptureAnalysisTabSelector : DataTemplateSelector
    {
        public DataTemplate XboxLiveCaptureAnalyzerTemplate { get; set; }
        public DataTemplate NetworkTraceCaptureAnalyzerTemplate { get; set; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            InitializeDataTemplateProviders();

            var context = item as AnalysisRunModel;
            DataTemplate dataTemplate;

            if (!DataTemplateProviders.TryGetValue(context.SourceAnalyzer.GetType(), out dataTemplate))
            {
                throw new NotImplementedException($"Analysis type not supported: {context.SourceAnalyzer.GetType().Name}");
            }

            return dataTemplate;
        }

        private void InitializeDataTemplateProviders()
        {
            if (DataTemplateProviders.Count == 0)
            {
                DataTemplateProviders.Add(
                    typeof(XMAT.XboxLiveCaptureAnalysis.XboxLiveCaptureAnalyzer),
                    XboxLiveCaptureAnalyzerTemplate);

                DataTemplateProviders.Add(
                    typeof(XMAT.NetworkTraceCaptureAnalysis.NetworkTraceCaptureAnalyzer),
                    NetworkTraceCaptureAnalyzerTemplate);

                // add additional view data templates for analyzers here...
            }
        }

        private readonly Dictionary<Type, DataTemplate> DataTemplateProviders = new();
    }
}
