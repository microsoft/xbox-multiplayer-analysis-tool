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
    /// Host literals are normalized by <see cref="HostListEntry"/>; standalone
    /// host:port entries are rejected.
    /// </summary>
    public class BypassListModel
    {
        public ObservableCollection<string> BypassedUrls { get; } = new ObservableCollection<string>();

        private readonly object _lockObj = new object();
        private readonly Dictionary<string, Regex> _regexCache = new Dictionary<string, Regex>(StringComparer.OrdinalIgnoreCase);

        public bool Add(string pattern)
        {
            if (!HostListEntry.TryNormalize(pattern, out string entry, out _))
                return false;

            lock (_lockObj)
            {
                if (BypassedUrls.Any(existing => string.Equals(existing, entry, StringComparison.OrdinalIgnoreCase)))
                    return false;

                BypassedUrls.Add(entry);
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
                return BypassedUrls.Any(existing => string.Equals(existing, entry, StringComparison.OrdinalIgnoreCase));
            }
        }

        public bool Remove(string pattern)
        {
            if (!HostListEntry.TryNormalize(pattern, out string entry, out _))
                return false;

            bool removed = false;
            lock (_lockObj)
            {
                for (int i = BypassedUrls.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(BypassedUrls[i], entry, StringComparison.OrdinalIgnoreCase))
                    {
                        BypassedUrls.RemoveAt(i);
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

            string candidate = HostListEntry.MatchCandidate(host);
            return Snapshot()
                .Select(GetRegex)
                .Any(regex => regex.IsMatch(candidate));
        }

        /// <summary>
        /// Returns the entries that match the host, such as a covering wildcard.
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
                return BypassedUrls.ToArray();
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
