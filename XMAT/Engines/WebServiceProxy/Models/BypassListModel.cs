// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;

namespace XMAT.WebServiceCapture.Models
{
    /// <summary>
    /// Maintains a list of host patterns whose traffic should be tunneled through
    /// the proxy without TLS interception. Patterns may contain the '*' wildcard
    /// to match any sequence of characters (e.g. "*.auth.xboxlive.com").
    /// Matching hosts will have their CONNECT tunnels relayed as opaque byte streams.
    /// </summary>
    public class BypassListModel
    {
        public ObservableCollection<string> BypassedUrls { get; } = new ObservableCollection<string>();

        private readonly object _lockObj = new object();
        private readonly Dictionary<string, Regex> _regexCache = new Dictionary<string, Regex>(StringComparer.OrdinalIgnoreCase);

        public bool Add(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                return false;

            pattern = pattern.Trim();

            lock (_lockObj)
            {
                if (BypassedUrls.Any(existing => string.Equals(existing, pattern, StringComparison.OrdinalIgnoreCase)))
                    return false;

                BypassedUrls.Add(pattern);
                return true;
            }
        }

        public void Remove(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                return;

            pattern = pattern.Trim();

            lock (_lockObj)
            {
                for (int i = BypassedUrls.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(BypassedUrls[i], pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        BypassedUrls.RemoveAt(i);
                    }
                }
                _regexCache.Remove(pattern);
            }
        }

        public void Clear()
        {
            lock (_lockObj)
            {
                BypassedUrls.Clear();
                _regexCache.Clear();
            }
        }

        /// <summary>
        /// Returns true if the given host matches any pattern in the bypass list.
        /// </summary>
        public bool IsBypassed(string host)
        {
            if (string.IsNullOrEmpty(host))
                return false;

            string[] patterns;
            lock (_lockObj)
            {
                patterns = new string[BypassedUrls.Count];
                for (int i = 0; i < BypassedUrls.Count; i++)
                    patterns[i] = BypassedUrls[i];
            }

            return patterns
                .Select(GetRegex)
                .Any(regex => regex.IsMatch(host));
        }

        private Regex GetRegex(string pattern)
        {
            lock (_lockObj)
            {
                if (_regexCache.TryGetValue(pattern, out var cached))
                    return cached;

                string escaped = Regex.Escape(pattern).Replace("\\*", ".*");
                var regex = new Regex("^" + escaped + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                _regexCache[pattern] = regex;
                return regex;
            }
        }
    }
}
