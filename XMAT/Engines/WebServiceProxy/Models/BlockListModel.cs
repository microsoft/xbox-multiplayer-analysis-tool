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
    /// Maintains a list of URL patterns whose traffic should be blocked by the proxy.
    /// Patterns may contain the '*' wildcard to match any sequence of characters
    /// (e.g. "*.example.com" or "192.168.1.*"). Patterns are matched against the
    /// request's host and optionally the full "host/path" string.
    /// </summary>
    public class BlockListModel
    {
        public ObservableCollection<string> BlockedUrls { get; } = new ObservableCollection<string>();

        private readonly object _lockObj = new object();
        private readonly Dictionary<string, Regex> _regexCache = new Dictionary<string, Regex>(StringComparer.OrdinalIgnoreCase);

        public bool Add(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                return false;

            pattern = pattern.Trim();

            lock (_lockObj)
            {
                if (BlockedUrls.Any(existing => string.Equals(existing, pattern, StringComparison.OrdinalIgnoreCase)))
                    return false;

                BlockedUrls.Add(pattern);
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
                for (int i = BlockedUrls.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(BlockedUrls[i], pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        BlockedUrls.RemoveAt(i);
                    }
                }
                _regexCache.Remove(pattern);
            }
        }

        public void Clear()
        {
            lock (_lockObj)
            {
                BlockedUrls.Clear();
                _regexCache.Clear();
            }
        }

        /// <summary>
        /// Returns true if the given host (and optional path) matches any pattern in the block list.
        /// </summary>
        public bool IsBlocked(string host, string path = null)
        {
            if (string.IsNullOrEmpty(host))
                return false;

            string[] candidates;
            if (!string.IsNullOrEmpty(path))
            {
                string normalizedPath = path.StartsWith('/') ? path : "/" + path;
                candidates = new[] { host, host + normalizedPath };
            }
            else
            {
                candidates = new[] { host };
            }

            string[] patterns;
            lock (_lockObj)
            {
                patterns = new string[BlockedUrls.Count];
                for (int i = 0; i < BlockedUrls.Count; i++)
                    patterns[i] = BlockedUrls[i];
            }

            foreach (var pattern in patterns)
            {
                Regex regex = GetRegex(pattern);
                foreach (var candidate in candidates)
                {
                    if (regex.IsMatch(candidate))
                        return true;
                }
            }

            return false;
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
