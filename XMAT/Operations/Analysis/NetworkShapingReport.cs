// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Net;
using System.Text;
using XMAT.Operations.Captures;

namespace XMAT.Operations.Analysis
{
    internal static class NetworkShapingReport
    {
        internal static void WriteMetadata(string directory, NetworkShapingMetadata metadata)
        {
            if (metadata == null)
            {
                return;
            }
            File.WriteAllText(
                Path.Combine(directory, "network-shaping.json"),
                NetworkShapingMetadata.Serialize(metadata),
                new UTF8Encoding(false));
        }

        internal static void WriteBanner(string directory, string text, string linkText)
        {
            string path = Path.Combine(directory, "index.html");
            if (!File.Exists(path))
            {
                return;
            }
            string banner =
                "<aside id=\"network-shaping-banner\" role=\"note\">" +
                WebUtility.HtmlEncode(text) +
                " <a href=\"network-shaping.json\">" + WebUtility.HtmlEncode(linkText) + "</a>.</aside>";
            string html = File.ReadAllText(path);
            const string container = "<div class=\"atg-container\">";
            int position = html.IndexOf(container, StringComparison.Ordinal);
            html = position >= 0
                ? html.Insert(position + container.Length, banner)
                : html.Replace("<body>", "<body>" + banner, StringComparison.OrdinalIgnoreCase);
            File.WriteAllText(path, html, new UTF8Encoding(false));
        }
    }
}
