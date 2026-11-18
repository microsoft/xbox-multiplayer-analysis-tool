// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;

namespace XMAT.Operations.Processes
{
    public sealed record ProcessFilterSpec(ImmutableArray<string> Include, ImmutableArray<string> Exclude)
    {
        public static ProcessFilterSpec Empty { get; } =
            new(ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);
    }

    internal sealed class ProcessFilter
    {
        private const int MaximumSelectors = 64;
        private readonly ImmutableArray<Selector> _include;
        private readonly ImmutableArray<Selector> _exclude;

        private ProcessFilter(
            ProcessFilterSpec specification,
            ImmutableArray<Selector> include,
            ImmutableArray<Selector> exclude)
        {
            Specification = specification;
            _include = include;
            _exclude = exclude;
        }

        internal ProcessFilterSpec Specification { get; }

        internal static bool IsEmpty(ProcessFilterSpec spec) =>
            spec == null || (spec.Include.IsDefaultOrEmpty && spec.Exclude.IsDefaultOrEmpty);

        internal static OperationResult<ProcessFilterSpec> Parse(
            IEnumerable<string> include,
            IEnumerable<string> exclude)
        {
            if (!TryCanonicalize(include, out ImmutableArray<string> includeValues) ||
                !TryCanonicalize(exclude, out ImmutableArray<string> excludeValues))
            {
                return OperationResult<ProcessFilterSpec>.Failure(
                    OperationOutcome.InvalidInput,
                    new OperationDiagnostic(
                        "process-filter.invalid-selector",
                        OperationDiagnosticSeverity.Error,
                        "A process selector is invalid."));
            }
            return OperationResult<ProcessFilterSpec>.Success(new ProcessFilterSpec(includeValues, excludeValues));
        }

        internal static ProcessFilter Compile(ProcessFilterSpec spec)
        {
            ProcessFilterSpec source = spec ?? ProcessFilterSpec.Empty;
            OperationResult<ProcessFilterSpec> parsed = Parse(
                source.Include.IsDefault ? null : source.Include,
                source.Exclude.IsDefault ? null : source.Exclude);
            if (!parsed.Succeeded)
            {
                throw new ArgumentException("The process filter contains an invalid selector.", nameof(spec));
            }
            ProcessFilterSpec normalized = parsed.Value;
            if (!source.Include.IsDefault &&
                !source.Exclude.IsDefault &&
                source.Include.SequenceEqual(normalized.Include, StringComparer.Ordinal) &&
                source.Exclude.SequenceEqual(normalized.Exclude, StringComparer.Ordinal))
            {
                normalized = source;
            }
            return new ProcessFilter(
                normalized,
                normalized.Include.Select(Selector.Parse).ToImmutableArray(),
                normalized.Exclude.Select(Selector.Parse).ToImmutableArray());
        }

        internal bool Matches(ClientProcessInfo info)
        {
            if (_include.IsEmpty && _exclude.IsEmpty)
            {
                return true;
            }
            if (IsUnknown(info))
            {
                return _include.IsEmpty;
            }
            bool included = _include.IsEmpty || _include.Any(selector => selector.Matches(info));
            return included && !_exclude.Any(selector => selector.Matches(info));
        }

        internal static bool IsUnknown(ClientProcessInfo info) =>
            info == null ||
            (info.Status != ClientProcessStatus.Local && info.Status != ClientProcessStatus.Imported) ||
            (string.IsNullOrEmpty(info.Name) && info.ProcessId <= 0);

        private static bool TryCanonicalize(IEnumerable<string> values, out ImmutableArray<string> result)
        {
            var builder = ImmutableArray.CreateBuilder<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var count = 0;
            if (values != null)
            {
                foreach (string value in values)
                {
                    count++;
                    if (count > MaximumSelectors || !TryCanonicalize(value, out string canonical))
                    {
                        result = default;
                        return false;
                    }
                    if (seen.Add(canonical))
                    {
                        builder.Add(canonical);
                    }
                }
            }
            result = builder.ToImmutable();
            return true;
        }

        private static bool TryCanonicalize(string value, out string canonical)
        {
            canonical = value?.Trim();
            if (string.IsNullOrEmpty(canonical) ||
                !ClientProcessRecordCodec.TryNormalizeName(canonical, out string normalized) ||
                !string.Equals(canonical, normalized, StringComparison.Ordinal))
            {
                return false;
            }
            if (canonical.Length > 1 && canonical[0] is '+' or '-' && canonical.Skip(1).All(char.IsAsciiDigit))
            {
                return false;
            }
            if (canonical.All(char.IsAsciiDigit))
            {
                if (!int.TryParse(canonical, NumberStyles.None, CultureInfo.InvariantCulture, out int processId) ||
                    processId <= 0)
                {
                    return false;
                }
                canonical = processId.ToString(CultureInfo.InvariantCulture);
            }
            return true;
        }

        private readonly record struct Selector(string Pattern, int ProcessId, bool Numeric)
        {
            internal static Selector Parse(string value)
            {
                if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int processId))
                {
                    return new Selector(null, processId, true);
                }
                return new Selector(WithoutExe(value), 0, false);
            }

            internal bool Matches(ClientProcessInfo info)
            {
                if (Numeric)
                {
                    return info.ProcessId == ProcessId;
                }
                return !string.IsNullOrEmpty(info.Name) && WildcardMatches(Pattern, WithoutExe(info.Name));
            }

            private static string WithoutExe(string value) => value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? value[..^4]
                : value;

            private static bool WildcardMatches(string pattern, string value)
            {
                var patternIndex = 0;
                var valueIndex = 0;
                var starIndex = -1;
                var retryValueIndex = -1;
                while (valueIndex < value.Length)
                {
                    if (patternIndex < pattern.Length &&
                        (pattern[patternIndex] == '?' || char.ToUpperInvariant(pattern[patternIndex]) ==
                            char.ToUpperInvariant(value[valueIndex])))
                    {
                        patternIndex++;
                        valueIndex++;
                    }
                    else if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
                    {
                        starIndex = patternIndex++;
                        retryValueIndex = valueIndex;
                    }
                    else if (starIndex >= 0)
                    {
                        patternIndex = starIndex + 1;
                        valueIndex = ++retryValueIndex;
                    }
                    else
                    {
                        return false;
                    }
                }
                while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
                {
                    patternIndex++;
                }
                return patternIndex == pattern.Length;
            }
        }
    }
}
