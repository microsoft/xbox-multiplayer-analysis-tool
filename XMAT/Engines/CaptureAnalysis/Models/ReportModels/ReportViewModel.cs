// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Resources;
using CaptureAnalysisEngine;
using XMAT.SharedInterfaces;

namespace XMAT.XboxLiveCaptureAnalysis.ReportModels
{
    internal abstract class ReportViewModel
    {
        private static readonly Encoding Utf8NoBom =
            new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true);

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
            var embeddedResourceUri = string.Format("pack://application:,,,/XboxMultiplayerAnalysisTool;component/{0}", resourceName);
            Uri uri = new Uri(embeddedResourceUri, UriKind.Absolute);
            StreamResourceInfo info = Application.GetResourceStream(uri);
            using (var resourceStream = info.Stream)
            {
                using (var fileStream = File.Create(filepath))
                {
                    resourceStream.Seek(0, SeekOrigin.Begin);
                    resourceStream.CopyTo(fileStream);
                }
            }
        }

        internal static void WriteJsonTemplate(
            string templatePath,
            string token,
            ReportDocument reportDocument)
        {
            ArgumentException.ThrowIfNullOrEmpty(
                templatePath);
            ArgumentException.ThrowIfNullOrEmpty(
                token);
            ArgumentNullException.ThrowIfNull(
                reportDocument);

            string templateText =
                File.ReadAllText(templatePath);
            string jsonBufferPath =
                templatePath +
                "." +
                Guid.NewGuid().ToString("N") +
                ".json.tmp";
            using var jsonStream =
                new FileStream(
                    jsonBufferPath,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.DeleteOnClose);
            reportDocument.WriteJson(
                jsonStream);
            jsonStream.Flush();

            using var output =
                new FileStream(
                    templatePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.Read);
            using var writer =
                new StreamWriter(
                    output,
                    Utf8NoBom,
                    bufferSize: 4096,
                    leaveOpen: true);
            int start = 0;
            while (true)
            {
                int tokenIndex =
                    templateText.IndexOf(
                        token,
                        start,
                        StringComparison.Ordinal);
                if (tokenIndex < 0)
                {
                    writer.Write(
                        templateText.AsSpan(start));
                    break;
                }

                writer.Write(
                    templateText.AsSpan(
                        start,
                        tokenIndex - start));
                writer.Flush();
                jsonStream.Position = 0;
                jsonStream.CopyTo(output);
                start =
                    tokenIndex +
                    token.Length;
            }
        }
    }
}
