// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Specialized;
using System.Linq;
using System.Text.RegularExpressions;

namespace XMAT.WebServiceCapture.Models
{
    internal sealed class HostPatternList
    {
        private readonly object _gate;
        private readonly System.Collections.Generic.Dictionary<string, Regex> _regexCache =
            new(StringComparer.OrdinalIgnoreCase);

        internal HostPatternList() : this(new object())
        {
        }

        internal HostPatternList(object gate)
        {
            _gate = gate ?? throw new ArgumentNullException(nameof(gate));
            Entries = new HostListCollection(_gate);
            Entries.CollectionChanged += HandleCollectionChanged;
        }

        internal HostListCollection Entries { get; }

        internal bool Add(string pattern)
        {
            if (!HostListEntry.TryNormalize(pattern, out string entry, out _))
                return false;

            lock (_gate)
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

            lock (_gate)
            {
                return Entries.Any(existing => string.Equals(existing, entry, StringComparison.OrdinalIgnoreCase));
            }
        }

        internal bool Remove(string pattern)
        {
            if (!HostListEntry.TryNormalize(pattern, out string entry, out _))
                return false;

            lock (_gate)
            {
                string existing = Entries.FirstOrDefault(value =>
                    string.Equals(value, entry, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                    return false;
                Entries.Remove(existing);
                return true;
            }
        }

        internal void Clear() => Entries.Clear();

        internal bool Matches(HostMatchCandidates candidates) =>
            Snapshot().Select(GetRegex)
                .Any(regex => HostListEntry.Matches(regex, candidates));

        internal System.Collections.Generic.IReadOnlyList<string> MatchingPatterns(HostMatchCandidates candidates) =>
            Snapshot().Where(pattern => HostListEntry.Matches(GetRegex(pattern), candidates))
                .ToList();

        private string[] Snapshot()
        {
            lock (_gate)
            {
                return Entries.ToArray();
            }
        }

        private Regex GetRegex(string pattern)
        {
            lock (_gate)
            {
                if (_regexCache.TryGetValue(pattern, out Regex cached))
                    return cached;

                Regex regex = HostListEntry.CreatePattern(pattern);
                _regexCache[pattern] = regex;
                return regex;
            }
        }

        private void HandleCollectionChanged(object sender, NotifyCollectionChangedEventArgs args)
        {
            if (args.Action is NotifyCollectionChangedAction.Remove or
                NotifyCollectionChangedAction.Replace or
                NotifyCollectionChangedAction.Reset)
            {
                lock (_gate)
                    _regexCache.Clear();
            }
        }
    }
}
