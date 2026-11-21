// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.IO;
using System.Resources;
using System.Text;
using CaptureAnalysisEngine;
using XMAT.XboxLiveCaptureAnalysis.ReportModels;

namespace XMAT
{
    internal static class AnalysisReportArtifactWriter
    {
        internal static readonly string[] SharedResources =
            ["bootstrap.css", "site.css", "report.css", "jquery.js", "bootstrap.min.js", "report_view.js"];

        internal static byte[] Resource(string name)
        {
            var manager = new ResourceManager("XboxMultiplayerAnalysisTool.g", typeof(PublicUtilities).Assembly);
            try
            {
                using var source = manager.GetStream($"resources/html/xlta/{name}".ToLowerInvariant(), CultureInfo.InvariantCulture)
                    ?? throw new InvalidOperationException("The report resource is unavailable.");
                using var memory = new MemoryStream();
                source.CopyTo(memory);
                return memory.ToArray();
            }
            finally { manager.ReleaseAllResources(); }
        }

        internal static byte[] Calls(string json) =>
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Resource("calls.js")).Replace("${CallsReportJson}", json, StringComparison.Ordinal));

        internal static byte[] Stats(string json) =>
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Resource("stats.js")).Replace("${StatsReportJson}", json, StringComparison.Ordinal));

        internal static byte[] Index(string prefix) =>
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Resource("index.html"))
                .Replace("\"calls.js\"", $"\"{prefix}calls.js\"", StringComparison.Ordinal)
                .Replace("\"stats.js\"", $"\"{prefix}stats.js\"", StringComparison.Ordinal));

        internal static void WriteCalls(string directory, string json) =>
            File.WriteAllBytes(Path.Combine(directory, "calls.js"), Calls(json));

        internal static void WriteCalls(string directory, ReportDocument document)
        {
            var path = Path.Combine(directory, "calls.js");
            File.WriteAllBytes(path, Resource("calls.js"));
            ReportViewModel.WriteJsonTemplate(path, "${CallsReportJson}", document);
        }

        internal static void WriteStats(string directory, ReportDocument document)
        {
            foreach (var resource in SharedResources)
                File.WriteAllBytes(Path.Combine(directory, resource), Resource(resource));
            File.WriteAllBytes(Path.Combine(directory, "index.html"), Index(string.Empty));
            var path = Path.Combine(directory, "stats.js");
            File.WriteAllBytes(path, Resource("stats.js"));
            ReportViewModel.WriteJsonTemplate(path, "${StatsReportJson}", document);
        }

        internal static void WriteStats(string directory, string json)
        {
            foreach (var resource in SharedResources)
                File.WriteAllBytes(Path.Combine(directory, resource), Resource(resource));
            File.WriteAllBytes(Path.Combine(directory, "index.html"), Index(string.Empty));
            File.WriteAllBytes(Path.Combine(directory, "stats.js"), Stats(json));
        }
    }
}
