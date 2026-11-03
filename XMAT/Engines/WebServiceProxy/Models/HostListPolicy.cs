// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text.RegularExpressions;
using XMAT.SharedInterfaces;

namespace XMAT.WebServiceCapture.Models
{
    /// <summary>
    /// The one validator and normalizer behind the Block and Bypass lists and their editors,
    /// used for adding, exact membership, removal and matching. DNS hosts keep their trimmed
    /// spelling and compare without regard to case, but a final dot is significant. IP
    /// literals have one canonical spelling, so "[::1]", "::1" and "0:0::1" are the same entry.
    /// Text containing '*' or '/' stays an opaque glob pattern in which colons and brackets are
    /// literal; it is matched against bracket-free IPv6 text, so IPv6 patterns omit brackets.
    /// </summary>
    internal static class HostListEntry
    {
        private static readonly char[] PatternCharacters = { '*', '/' };

        internal static bool TryNormalize(string text, out string entry, out string error)
        {
            entry = null;
            error = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string value = text.Trim();
            if (value.IndexOfAny(PatternCharacters) >= 0)
            {
                entry = value;
                return true;
            }
            if (HostAuthority.TryNormalizeHost(value, out string host))
            {
                entry = host;
                return true;
            }
            if (IsPortAttempt(value))
            {
                // Entries apply to every port; silently dropping the port would broaden the rule.
                error = Localization.GetLocalizedString("HOST_LIST_PORT_NOT_ALLOWED", value);
                return false;
            }

            // Any other text keeps the existing literal pattern behavior.
            entry = value;
            return true;
        }

        // A bracketed token followed by ':' is a port attempt whatever it contains, and a host
        // followed by ':' is one whatever follows it, so "[example.com]:443", "example.com:" and
        // "[::1]:notaport" are rejected like "example.com:443". A complete bare IPv6 literal
        // never gets here: it is a valid host whose last group is not a port.
        private static bool IsPortAttempt(string value)
        {
            int colon;
            if (value[0] == '[')
            {
                colon = value.IndexOf(']') + 1;
                return colon > 1 && colon < value.Length && value[colon] == ':';
            }

            colon = value.IndexOf(':');
            return colon > 0 && HostAuthority.TryNormalizeHost(value[..colon], out _);
        }

        // The host form compared with patterns: the host identity if there is one, otherwise the
        // host of an authority as sent (trimmed, without its port), otherwise the trimmed text.
        // DNS spelling and a final dot are kept so that dotted and dotless names match different
        // patterns, and IPv6 is canonical text without brackets.
        internal static string MatchCandidate(string host)
        {
            if (HostAuthority.TryNormalizeHost(host, out string identity) ||
                HostAuthority.TryParse(host, out identity, out _))
            {
                return identity;
            }
            return host?.Trim();
        }

        internal static Regex CreatePattern(string entry) =>
            new("^" + Regex.Escape(entry).Replace("\\*", ".*") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// The application's session-only Block and Bypass lists, shared by every capture and
    /// analysis view of one application instance. Nothing here is persisted.
    /// </summary>
    public sealed class HostListPolicy
    {
        private readonly object _gate = new();
        private readonly Dictionary<(HostPolicyList List, string Pattern), Entry> _entries = new(new EntryKeyComparer());
        private long _revision = 1;
        private long _epoch;

        public BlockListModel BlockList { get; }
        public BypassListModel BypassList { get; }

        public HostListPolicy()
        {
            BlockList = new BlockListModel(_gate);
            BypassList = new BypassListModel(_gate);
            BlockList.Entries.Committing = (args, actor) => Committed(HostPolicyList.Block, args, actor);
            BypassList.Entries.Committing = (args, actor) => Committed(HostPolicyList.Bypass, args, actor);
        }

        internal HostPolicySnapshot Inspect(Guid? actor)
        {
            lock (_gate)
            {
                return new(_revision, BlockList.BlockedUrls.Select(pattern => Describe(Find(HostPolicyList.Block, pattern), actor))
                    .Concat(BypassList.BypassedUrls.Select(pattern => Describe(Find(HostPolicyList.Bypass, pattern), actor))).ToArray());
            }
        }

        internal HostPolicyPlan Prepare(HostPolicyRequest request, Guid? actor)
        {
            lock (_gate)
            {
                if (request == null || !Enum.IsDefined(request.List) || !Enum.IsDefined(request.Action))
                    throw new AdministrationException(AdministrationFailure.InvalidRequest);
                if (request.ExpectedRevision != _revision)
                    throw new AdministrationException(AdministrationFailure.SourceChanged);
                if (request.Action == HostPolicyAction.Clear)
                {
                    if (request.Pattern != null || request.EntryId.HasValue || request.ExpectedEntryEpoch.HasValue)
                        throw new AdministrationException(AdministrationFailure.InvalidRequest);
                    var count = request.List == HostPolicyList.Block ? BlockList.BlockedUrls.Count : BypassList.BypassedUrls.Count;
                    return new(request, null, count > 0, count);
                }
                if (request.Pattern?.Length > 1024 || request.Pattern?.Any(char.IsControl) == true ||
                    !HostListEntry.TryNormalize(request.Pattern, out var pattern, out _))
                    throw new AdministrationException(AdministrationFailure.InvalidRequest);
                var entry = Find(request.List, pattern);
                if (request.Action != HostPolicyAction.Add &&
                    (entry == null || request.EntryId != entry.Id || request.ExpectedEntryEpoch != entry.Epoch))
                    throw new AdministrationException(AdministrationFailure.SourceChanged);
                if (request.Action == HostPolicyAction.Add &&
                    (request.EntryId.HasValue || request.ExpectedEntryEpoch.HasValue))
                    throw new AdministrationException(AdministrationFailure.InvalidRequest);
                var changes = request.Action == HostPolicyAction.Add ? entry == null :
                    request.Action == HostPolicyAction.Remove || entry.Owner != actor;
                return new(request with { Pattern = pattern }, entry == null ? null : Describe(entry, actor), changes, changes ? 1 : 0);
            }
        }

        internal HostPolicyChange Execute(HostPolicyPlan plan, Guid? actor)
        {
            lock (_gate)
            {
                var current = Prepare(plan?.Request, actor);
                if (current != plan)
                    throw new AdministrationException(AdministrationFailure.SourceChanged);
                if (!current.ChangesState)
                    return new(_revision, false, current.Entry);
                var collection = current.Request.List == HostPolicyList.Block ? BlockList.Entries : BypassList.Entries;
                if (current.Request.Action == HostPolicyAction.Clear)
                    return collection.ClearOwned(actor);
                if (current.Request.Action == HostPolicyAction.Add)
                    return collection.AddOwned(current.Request.Pattern, actor);
                var entry = Find(current.Request.List, current.Request.Pattern);
                if (current.Request.Action == HostPolicyAction.TakeOver)
                {
                    if (actor.HasValue)
                        throw new AdministrationException(AdministrationFailure.InvalidRequest);
                    var adopted = entry with { Owner = null, Epoch = ++_epoch };
                    _entries[Key(entry.List, entry.Pattern)] = adopted;
                    return new(++_revision, true, Describe(adopted, actor), AffectedEntries: 1);
                }
                var index = collection.IndexOf(entry.Pattern);
                return collection.RemoveOwned(index, actor);
            }
        }

        private HostPolicyChange Committed(HostPolicyList list, NotifyCollectionChangedEventArgs args, Guid? actor)
        {
            Entry changed = null;
            var affected = args.Action == NotifyCollectionChangedAction.Move ? 0 : 1;
            if (args.Action == NotifyCollectionChangedAction.Reset)
            {
                affected = _entries.Keys.Count(key => key.List == list);
                foreach (var key in _entries.Keys.Where(key => key.List == list).ToArray())
                    _entries.Remove(key);
            }
            else if (args.Action != NotifyCollectionChangedAction.Move)
            {
                if (args.OldItems is { Count: > 0 })
                {
                    var pattern = (string)args.OldItems[0];
                    changed = Find(list, pattern);
                    _entries.Remove(Key(list, pattern));
                }
                if (args.NewItems is { Count: > 0 })
                {
                    var pattern = (string)args.NewItems[0];
                    changed = new Entry(changed?.Id ?? Guid.NewGuid(), list, pattern, ++_epoch, actor);
                    _entries[Key(list, pattern)] = changed;
                }
            }
            return new(++_revision, true, changed == null ? null : Describe(changed, actor), AffectedEntries: affected);
        }

        private Entry Find(HostPolicyList list, string pattern) => _entries.GetValueOrDefault(Key(list, pattern));
        private static (HostPolicyList List, string Pattern) Key(HostPolicyList list, string pattern) =>
            (list, pattern);

        private static HostPolicyEntryDescriptor Describe(Entry entry, Guid? actor) =>
            new(entry.Id, entry.List, entry.Pattern, entry.Epoch,
                !entry.Owner.HasValue ? HostPolicyOwnership.Desktop :
                entry.Owner == actor ? HostPolicyOwnership.ThisSession : HostPolicyOwnership.AnotherSession);

        private sealed record Entry(Guid Id, HostPolicyList List, string Pattern, long Epoch, Guid? Owner);

        private sealed class EntryKeyComparer : IEqualityComparer<(HostPolicyList List, string Pattern)>
        {
            public bool Equals((HostPolicyList List, string Pattern) left, (HostPolicyList List, string Pattern) right) =>
                left.List == right.List && StringComparer.OrdinalIgnoreCase.Equals(left.Pattern, right.Pattern);

            public int GetHashCode((HostPolicyList List, string Pattern) value) =>
                HashCode.Combine(value.List, StringComparer.OrdinalIgnoreCase.GetHashCode(value.Pattern));
        }
    }
}
