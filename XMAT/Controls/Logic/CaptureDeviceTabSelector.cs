// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XMAT.Models;
using XMAT.SharedInterfaces;
using System;
using System.Windows;
using System.Windows.Controls;

namespace XMAT
{
    class CaptureDeviceTabSelector : DataTemplateSelector
    {
        public DataTemplate WebProxyTemplate { get; set; }
        public DataTemplate NetworkTraceTemplate { get; set; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            var context = item as CaptureDeviceContextModel;
            switch(context.CaptureType)
            {
                case CaptureType.WebProxy:
                    return WebProxyTemplate;
                case CaptureType.NetworkTrace:
                    return NetworkTraceTemplate;
                default:
                    throw new NotImplementedException($"Capture type not supported: {context.CaptureType}");
            }
        }
    }
}
