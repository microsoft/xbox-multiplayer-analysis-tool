// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CaptureAnalysisEngine;
using XMAT.SharedInterfaces;
using System;
using System.IO;
using System.Windows;
using System.Windows.Resources;

namespace XMAT.XboxLiveCaptureAnalysis.ReportModels
{
    internal abstract class ReportViewModel
    {
        public abstract string ReportName { get; }

        public virtual bool Show { get { return true; } }

        public ICaptureAppModel CaptureAppModel { get; }
        public string SourceCaptureName { get; }
        public ReportDocument ReportDocument { get; }

        protected ReportViewModel(
            ICaptureAppModel captureAppModel,
            string sourceCaptureName,
            ReportDocument reportDocument)
        {
            CaptureAppModel = captureAppModel;
            SourceCaptureName = sourceCaptureName;
            ReportDocument = reportDocument;
        }

        protected void CopyResourceToAnalysisDirectory(
            string resourceName,
            string destinationFolder)
        {
            var filename = Path.GetFileName(resourceName);
            var filepath = Path.Combine(destinationFolder, filename);
            var embeddedResourceUri = string.Format("pack://application:,,,/{0}", resourceName);
            Uri uri = new Uri(embeddedResourceUri, UriKind.Absolute);
            StreamResourceInfo info = Application.GetResourceStream(uri);
            using(var resourceStream = info.Stream)
            {
                using (var fileStream = File.Create(filepath))
                {
                    resourceStream.Seek(0, SeekOrigin.Begin);
                    resourceStream.CopyTo(fileStream);
                }
            }
        }
    }
}
