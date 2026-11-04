// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using XMAT;

namespace CaptureAnalysisEngine
{
    /// <summary>
    /// Where an analysis endpoint's records came from. Unknown is the default so records
    /// without recoverable provenance are never treated as captured traffic.
    /// </summary>
    public enum EndpointKind
    {
        Unknown = 0,
        Captured = 1,
        SyntheticTelemetry = 2
    }

    /// <summary>
    /// Collision-free identity of an analysis endpoint bucket. A captured host named
    /// "inGameEvents" and the events synthesized from telemetry share a logical name but
    /// are different endpoints. Display text uses <see cref="LogicalName"/> only.
    /// </summary>
    public readonly record struct EndpointKey(EndpointKind Kind, string LogicalName) : IComparable<EndpointKey>
    {
        // Same order as the former name-keyed buckets, with the kind as a tie-breaker. The final
        // ordinal tie-breaker returns zero only for equal keys, so the order and the comparison
        // operators stay consistent with record equality.
        public int CompareTo(EndpointKey other)
        {
            int result = string.Compare(LogicalName, other.LogicalName, StringComparison.CurrentCulture);
            if (result == 0)
            {
                result = Kind.CompareTo(other.Kind);
            }
            return result != 0 ? result : string.Compare(LogicalName, other.LogicalName, StringComparison.Ordinal);
        }

        public static bool operator <(EndpointKey left, EndpointKey right) => left.CompareTo(right) < 0;

        public static bool operator <=(EndpointKey left, EndpointKey right) => left.CompareTo(right) <= 0;

        public static bool operator >(EndpointKey left, EndpointKey right) => left.CompareTo(right) > 0;

        public static bool operator >=(EndpointKey left, EndpointKey right) => left.CompareTo(right) >= 0;

        /// <summary>
        /// Stable, culture-invariant identity text that report data and its consumers group
        /// and select by. Display text is never used as identity.
        /// </summary>
        public string Id => $"{Kind}:{LogicalName}";

        public override string ToString() => LogicalName ?? string.Empty;
    }

    /// <summary>
    /// Display labels for the endpoints of one report: the logical name, qualified by the
    /// localized endpoint kind only when another endpoint in the same report shares that name.
    /// Labels are display text only; consumers group by <see cref="EndpointKey.Id"/>.
    /// </summary>
    internal static class EndpointLabels
    {
        internal static Dictionary<EndpointKey, string> For(IEnumerable<EndpointKey> endpoints)
        {
            EndpointKey[] keys = endpoints.Distinct().ToArray();
            HashSet<string> sharedNames = keys
                .GroupBy(key => key.LogicalName ?? string.Empty, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToHashSet(StringComparer.Ordinal);
            return keys.ToDictionary(
                key => key,
                key => sharedNames.Contains(key.LogicalName ?? string.Empty)
                    ? Localization.GetLocalizedString("LTA_ENDPOINT_KIND_LABEL", key.LogicalName, KindText(key.Kind))
                    : key.LogicalName);
        }

        private static string KindText(EndpointKind kind) => Localization.GetLocalizedString(kind switch
        {
            EndpointKind.Captured => "LTA_ENDPOINT_KIND_CAPTURED",
            EndpointKind.SyntheticTelemetry => "LTA_ENDPOINT_KIND_SYNTHETIC",
            _ => "LTA_ENDPOINT_KIND_UNKNOWN"
        });
    }
}
