// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

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
        private readonly HostPatternList _patterns = new();

        public ObservableCollection<string> BlockedUrls => _patterns.Entries;

        public bool Add(string pattern) => _patterns.Add(pattern);

        /// <summary>
        /// Returns true when the list holds exactly this entry (after normalization),
        /// regardless of any other pattern that might also match it.
        /// </summary>
        public bool Contains(string pattern) => _patterns.Contains(pattern);

        public bool Remove(string pattern) => _patterns.Remove(pattern);

        public void Clear() => _patterns.Clear();

        /// <summary>
        /// Returns true if the given host (and optional path) matches any pattern in the block list.
        /// </summary>
        public bool IsBlocked(string host, string path = null)
        {
            if (string.IsNullOrEmpty(host))
                return false;

            return _patterns.Matches(HostListEntry.MatchCandidates(host, path));
        }

        /// <summary>
        /// Returns the entries that match the host by itself, such as a covering wildcard.
        /// Path patterns only match particular requests, so they are not included.
        /// </summary>
        internal IReadOnlyList<string> MatchingPatterns(string host)
        {
            if (string.IsNullOrEmpty(host))
                return Array.Empty<string>();

            return _patterns.MatchingPatterns(HostListEntry.MatchCandidates(host));
        }
    }
}
