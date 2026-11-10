// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

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
        private readonly HostPatternList _patterns = new();

        public ObservableCollection<string> BypassedUrls => _patterns.Entries;

        public bool Add(string pattern) => _patterns.Add(pattern);

        /// <summary>
        /// Returns true when the list holds exactly this entry (after normalization),
        /// regardless of any other pattern that might also match it.
        /// </summary>
        public bool Contains(string pattern) => _patterns.Contains(pattern);

        public bool Remove(string pattern) => _patterns.Remove(pattern);

        public void Clear() => _patterns.Clear();

        /// <summary>
        /// Returns true if the given host matches any pattern in the bypass list.
        /// </summary>
        public bool IsBypassed(string host)
        {
            if (string.IsNullOrEmpty(host))
                return false;

            return _patterns.Matches(HostListEntry.LiteralHostCandidates(host));
        }

        /// <summary>
        /// Returns the entries that match the host, such as a covering wildcard.
        /// </summary>
        internal IReadOnlyList<string> MatchingPatterns(string host)
        {
            if (string.IsNullOrEmpty(host))
                return Array.Empty<string>();

            return _patterns.MatchingPatterns(HostListEntry.LiteralHostCandidates(host));
        }
    }
}
