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
    internal sealed class HostPatternList
    {
        private readonly object _lockObj = new();
        private readonly Dictionary<string, Regex> _regexCache = new(StringComparer.OrdinalIgnoreCase);

        internal ObservableCollection<string> Entries { get; } = new();

        internal bool Add(string pattern)
        {
            if (!HostListEntry.TryNormalize(pattern, out string entry, out _))
                return false;

            lock (_lockObj)
            {
                if (Entries.Any(existing => string.Equals(existing, entry, StringComparison.OrdinalIgnoreCase)))
                    return false;

                Entries.Add(entry);
                return true;
            }
        }

        internal bool Contains(string pattern)
        {
            if (!HostListEntry.TryNormalize(pattern, out string entry, out _))
                return false;

            lock (_lockObj)
            {
                return Entries.Any(existing => string.Equals(existing, entry, StringComparison.OrdinalIgnoreCase));
            }
        }

        internal bool Remove(string pattern)
        {
            if (!HostListEntry.TryNormalize(pattern, out string entry, out _))
                return false;

            var removed = false;
            lock (_lockObj)
            {
                for (int index = Entries.Count - 1; index >= 0; index--)
                {
                    if (string.Equals(Entries[index], entry, StringComparison.OrdinalIgnoreCase))
                    {
                        Entries.RemoveAt(index);
                        removed = true;
                    }
                }
                _regexCache.Remove(entry);
            }
            return removed;
        }

        internal void Clear()
        {
            lock (_lockObj)
            {
                Entries.Clear();
                _regexCache.Clear();
            }
        }

        internal bool Matches((string Host, string HostAndPath) candidates) =>
            Snapshot().Select(GetRegex)
                .Any(regex => HostListEntry.Matches(regex, candidates));

        internal IReadOnlyList<string> MatchingPatterns((string Host, string HostAndPath) candidates) =>
            Snapshot().Where(pattern => HostListEntry.Matches(GetRegex(pattern), candidates))
                .ToList();

        private string[] Snapshot()
        {
            lock (_lockObj)
            {
                return Entries.ToArray();
            }
        }

        private Regex GetRegex(string pattern)
        {
            lock (_lockObj)
            {
                if (_regexCache.TryGetValue(pattern, out Regex cached))
                    return cached;

                Regex regex = HostListEntry.CreatePattern(pattern);
                _regexCache[pattern] = regex;
                return regex;
            }
        }
    }
}
