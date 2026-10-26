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
    /// request's host and optionally the full "host/path" string. Host literals are
    /// normalized by <see cref="HostListEntry"/>; standalone host:port entries are rejected.
    /// </summary>
    public class BlockListModel
    {
        public ObservableCollection<string> BlockedUrls { get; } = new ObservableCollection<string>();

        private readonly object _lockObj = new object();
        private readonly Dictionary<string, Regex> _regexCache = new Dictionary<string, Regex>(StringComparer.OrdinalIgnoreCase);

        public bool Add(string pattern)
        {
            if (!HostListEntry.TryNormalize(pattern, out string entry, out _))
                return false;

            lock (_lockObj)
            {
                if (BlockedUrls.Any(existing => string.Equals(existing, entry, StringComparison.OrdinalIgnoreCase)))
                    return false;

                BlockedUrls.Add(entry);
                return true;
            }
        }

        /// <summary>
        /// Returns true when the list holds exactly this entry (after normalization),
        /// regardless of any other pattern that might also match it.
        /// </summary>
        public bool Contains(string pattern)
        {
            if (!HostListEntry.TryNormalize(pattern, out string entry, out _))
                return false;

            lock (_lockObj)
            {
                return BlockedUrls.Any(existing => string.Equals(existing, entry, StringComparison.OrdinalIgnoreCase));
            }
        }

        public bool Remove(string pattern)
        {
            if (!HostListEntry.TryNormalize(pattern, out string entry, out _))
                return false;

            bool removed = false;
            lock (_lockObj)
            {
                for (int i = BlockedUrls.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(BlockedUrls[i], entry, StringComparison.OrdinalIgnoreCase))
                    {
                        BlockedUrls.RemoveAt(i);
                        removed = true;
                    }
                }
                _regexCache.Remove(entry);
            }
            return removed;
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

            host = HostListEntry.MatchCandidate(host);
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

            return Snapshot()
                .Select(GetRegex)
                .Any(regex => candidates.Any(candidate => regex.IsMatch(candidate)));
        }

        /// <summary>
        /// Returns the entries that match the host by itself, such as a covering wildcard.
        /// Path patterns only match particular requests, so they are not included.
        /// </summary>
        internal IReadOnlyList<string> MatchingPatterns(string host)
        {
            if (string.IsNullOrEmpty(host))
                return Array.Empty<string>();

            string candidate = HostListEntry.MatchCandidate(host);
            return Snapshot().Where(pattern => GetRegex(pattern).IsMatch(candidate)).ToList();
        }

        private string[] Snapshot()
        {
            lock (_lockObj)
            {
                return BlockedUrls.ToArray();
            }
        }

        private Regex GetRegex(string pattern)
        {
            lock (_lockObj)
            {
                if (_regexCache.TryGetValue(pattern, out var cached))
                    return cached;

                var regex = HostListEntry.CreatePattern(pattern);
                _regexCache[pattern] = regex;
                return regex;
            }
        }
    }
}
