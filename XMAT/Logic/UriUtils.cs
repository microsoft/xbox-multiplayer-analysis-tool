// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;

namespace XMAT
{
    public static class UriUtils
    {
        // The schemes that a capture's row can make a URL from: http and https, which XMAT captures, and ftp, which Fiddler
        // also records. Any other scheme is unknown, as is an empty one, and so is the method that earlier imports stored as
        // the scheme of a request line without one.
        public static bool IsUrlScheme(string scheme) =>
            scheme is "http" or "https" or "ftp";

        public static string GetAbsoluteUri(
            string uriScheme,
            string uriHost,
            string uriPort,
            string uriPath)
        {
            // if the client specified an absolute URI in the request line, use it directly
            // otherwise we assume it's relative and try to create a proper Uri out of it
            if (!Uri.TryCreate(uriPath, UriKind.Absolute, out Uri uri))
            {
                string path = uriPath;
                string query = string.Empty;

                // split out the query string or the fragment, if it exists
                if (uriPath.Contains('?'))
                {
                    var pathSplit = path.Split('?');
                    path = pathSplit[0];
                    if (pathSplit.Length > 1)
                        query = "?" + pathSplit[1];
                }
                else if (uriPath.Contains('#'))
                {
                    var pathSplit = path.Split('#');
                    path = pathSplit[0];
                    if (pathSplit.Length > 1)
                        query = "#" + pathSplit[1];
                }

                // if the port is -1, the default port is used by UriBuilder

                var ub = new UriBuilder(
                    uriScheme,
                    uriHost,
                    string.IsNullOrEmpty(uriPort) ? -1 : int.Parse(uriPort),
                    path,
                    query);
                uri = ub.Uri;
            }
            return uri.ToString();
        }
    }
}
