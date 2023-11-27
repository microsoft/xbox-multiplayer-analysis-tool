// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using XMAT.XboxLiveCaptureAnalysis.Models;

namespace XMAT.XboxLiveCaptureAnalysis
{
    internal class ProcessStepToFontWeightConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
            {
                return string.Empty;
            }

            if (value is not AnalysisResultsModel.ProcessStep processStep)
            {
                throw new ArgumentException("ProcessStepToFontWeightConverter received a value that wasn't a AnalysisResultsModel.ProcessStep");
            }

            var step = int.Parse(parameter.ToString());

            return (int)processStep == step ? FontWeights.Bold : FontWeights.Normal;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException("ProcessStepToFontWeightConverter.ConvertBack() not implemented");
        }
    }
}
