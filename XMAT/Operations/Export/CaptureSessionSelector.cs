// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using XMAT.Operations.Captures;
using XMAT.Operations.Processes;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Operations.Export
{
    internal static class CaptureSelectionValidator
    {
        private const int MaximumValues = 64;
        private const int MaximumValueLength = 260;

        internal static OperationResult<CaptureSelectionSpec> Validate(CaptureSelectionSpec selection)
        {
            if (selection == null)
            {
                return Failure("export.selection.missing");
            }

            ProcessFilterSpec process;
            try
            {
                process = ProcessFilter.Compile(selection.Process).Specification;
            }
            catch (ArgumentException)
            {
                return Failure("export.selection.invalid-process");
            }

            HostSelection host = selection.Host ??
                new HostSelection(HostMatchMode.Glob, null, ImmutableArray<string>.Empty);
            if (host.Mode is not (HostMatchMode.Glob or HostMatchMode.Exact))
            {
                return Failure("export.selection.invalid-host");
            }
            if (!TryNormalizeHosts(host.Include, host.Mode, out ImmutableArray<string>? include, out string hostCode) ||
                !TryNormalizeHosts(host.Exclude, host.Mode, out ImmutableArray<string> exclude, out hostCode))
            {
                return Failure(hostCode);
            }
            if (!TryNormalizeExact(selection.Statuses, "status", out ImmutableArray<string>? statuses, out string code) ||
                !TryNormalizeExact(selection.Methods, "method", out ImmutableArray<string>? methods, out code))
            {
                return Failure(code);
            }

            return OperationResult<CaptureSelectionSpec>.Success(new CaptureSelectionSpec(
                process,
                new HostSelection(host.Mode, include, exclude),
                statuses,
                methods,
                selection.Keys,
                selection.MatchNone));
        }

        private static bool TryNormalizeHosts(
            ImmutableArray<string>? values,
            HostMatchMode mode,
            out ImmutableArray<string>? normalized,
            out string code)
        {
            if (!values.HasValue)
            {
                normalized = null;
                code = null;
                return true;
            }
            if (!TryNormalizeHosts(values.Value, mode, out ImmutableArray<string> concrete, out code))
            {
                normalized = null;
                return false;
            }
            normalized = concrete;
            return true;
        }

        private static bool TryNormalizeHosts(
            ImmutableArray<string> values,
            HostMatchMode mode,
            out ImmutableArray<string> normalized,
            out string code)
        {
            code = "export.selection.invalid-host";
            if (values.IsDefault)
            {
                normalized = ImmutableArray<string>.Empty;
                return true;
            }
            if (mode == HostMatchMode.Exact)
            {
                if (values.Any(value => value == null))
                {
                    normalized = default;
                    return false;
                }
                normalized = values;
                return true;
            }
            if (values.Length > MaximumValues)
            {
                normalized = default;
                code = "export.selection.limit";
                return false;
            }

            var builder = ImmutableArray.CreateBuilder<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string value in values)
            {
                if (value == null ||
                    value.Length > MaximumValueLength ||
                    value.Any(char.IsControl))
                {
                    normalized = default;
                    return false;
                }
                string canonical = value;
                if (value.Length == 0 || !HostListEntry.TryNormalizeValue(value, out canonical, out _))
                {
                    normalized = default;
                    return false;
                }
                if (canonical.Length > MaximumValueLength)
                {
                    normalized = default;
                    return false;
                }
                if (seen.Add(canonical))
                {
                    builder.Add(canonical);
                }
            }
            normalized = builder.ToImmutable();
            return true;
        }

        private static bool TryNormalizeExact(
            ImmutableArray<string>? values,
            string field,
            out ImmutableArray<string>? normalized,
            out string code)
        {
            if (!values.HasValue)
            {
                normalized = null;
                code = null;
                return true;
            }
            ImmutableArray<string> source = values.Value;
            if (source.IsDefault)
            {
                normalized = ImmutableArray<string>.Empty;
                code = null;
                return true;
            }

            foreach (string value in source)
            {
                if (value == null)
                {
                    normalized = null;
                    code = $"export.selection.invalid-{field}";
                    return false;
                }
            }
            normalized = source;
            code = null;
            return true;
        }

        private static OperationResult<CaptureSelectionSpec> Failure(string code) =>
            OperationResult<CaptureSelectionSpec>.Failure(
                OperationOutcome.InvalidInput,
                new OperationDiagnostic(
                    code,
                    OperationDiagnosticSeverity.Error,
                    "The capture selection is invalid."));
    }

    internal static class CaptureSessionSelector
    {
        internal static OperationResult<CaptureSessionSelection> Select(
            IReadOnlyList<CaptureExportSession> sessions,
            CaptureSelectionSpec selection,
            string deviceId,
            bool isLive)
        {
            if (sessions == null)
            {
                return Failure(
                    "export.selection.sessions-missing",
                    "Capture sessions are required.");
            }
            OperationResult<CaptureSelectionSpec> validated = CaptureSelectionValidator.Validate(selection);
            if (!validated.Succeeded)
            {
                return new OperationResult<CaptureSessionSelection>(
                    validated.Outcome,
                    null,
                    validated.Diagnostics);
            }
            CaptureSelectionSpec canonical = validated.Value;
            if (sessions.Any(session => session == null))
            {
                return Failure(
                    "export.selection.session-invalid",
                    "A capture session is invalid.");
            }
            List<CaptureExportSession> candidates = sessions
                .Where(session =>
                    string.IsNullOrEmpty(deviceId) ||
                    string.Equals(session.DeviceId, deviceId, StringComparison.Ordinal))
                .ToList();

            var processSessions = candidates
                .Select(session => new CaptureSessionRef(session.Key, session.DeviceId, session.Process))
                .ToList();
            OperationResult<FilterCaptureSessionsResult> processResult = new FilterCaptureSessionsOperation()
                .ExecuteAsync(
                    new FilterCaptureSessionsRequest(processSessions, canonical.Process),
                    OperationContext.None,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (!processResult.Succeeded)
            {
                return new OperationResult<CaptureSessionSelection>(
                    processResult.Outcome,
                    null,
                    processResult.Diagnostics);
            }

            var processKeys = processResult.Value.IncludedKeys.ToHashSet();
            HostMatcher host = HostMatcher.Compile(canonical.Host);
            HashSet<string> statuses = ExactSet(canonical.Statuses);
            HashSet<string> methods = ExactSet(canonical.Methods);
            var keys = new List<CaptureRowKey>();
            var overall = new MutableCounts();
            var devices = new Dictionary<string, MutableCounts>(StringComparer.Ordinal);
            foreach (CaptureExportSession session in candidates)
            {
                if (!devices.TryGetValue(session.DeviceId, out MutableCounts device))
                {
                    device = new MutableCounts();
                    devices.Add(session.DeviceId, device);
                }
                overall.Total++;
                device.Total++;
                bool matches = !canonical.MatchNone &&
                    processKeys.Contains(session.Key) &&
                    host.Matches(session.Host, session.Path) &&
                    MatchesExact(statuses, canonical.Statuses, session.Status) &&
                    MatchesExact(methods, canonical.Methods, session.Method);
                if (!matches)
                {
                    continue;
                }
                overall.Matched++;
                device.Matched++;
                if (canonical.Keys != null && !canonical.Keys.Contains(session.Key))
                {
                    continue;
                }
                overall.Selected++;
                device.Selected++;
                if (isLive && !session.IsComplete)
                {
                    overall.SkippedIncomplete++;
                    device.SkippedIncomplete++;
                    if (session.NoRecordedResponse)
                    {
                        overall.NoRecordedResponse++;
                        device.NoRecordedResponse++;
                    }
                    continue;
                }
                overall.Eligible++;
                device.Eligible++;
                keys.Add(session.Key);
            }

            return OperationResult<CaptureSessionSelection>.Success(new CaptureSessionSelection(
                keys.AsReadOnly(),
                overall.ToRecord(),
                devices.ToDictionary(item => item.Key, item => item.Value.ToRecord(), StringComparer.Ordinal),
                processResult.Value.Scope));
        }

        private static HashSet<string> ExactSet(ImmutableArray<string>? values) =>
            values.HasValue ? values.Value.ToHashSet(StringComparer.Ordinal) : null;

        private static bool MatchesExact(HashSet<string> values, ImmutableArray<string>? source, string candidate) =>
            !source.HasValue || values.Contains(candidate ?? string.Empty);

        private static OperationResult<CaptureSessionSelection> Failure(string code, string message) =>
            OperationResult<CaptureSessionSelection>.Failure(
                OperationOutcome.InvalidInput,
                new OperationDiagnostic(code, OperationDiagnosticSeverity.Error, message));

        private sealed class HostMatcher
        {
            private readonly HostMatchMode _mode;
            private readonly HashSet<string> _includeExact;
            private readonly HashSet<string> _excludeExact;
            private readonly Regex[] _includePatterns;
            private readonly Regex[] _excludePatterns;

            private HostMatcher(HostSelection selection)
            {
                _mode = selection.Mode;
                if (_mode == HostMatchMode.Glob)
                {
                    _includePatterns = Compile(selection.Include);
                    _excludePatterns = selection.Exclude.Select(HostListEntry.CreatePattern).ToArray();
                }
                else
                {
                    _includeExact = selection.Include.HasValue
                        ? selection.Include.Value.ToHashSet(StringComparer.Ordinal)
                        : null;
                    _excludeExact = selection.Exclude.ToHashSet(StringComparer.Ordinal);
                }
            }

            internal static HostMatcher Compile(HostSelection selection) => new(selection);

            internal bool Matches(string host, string path)
            {
                if (_mode == HostMatchMode.Exact)
                {
                    string value = host ?? string.Empty;
                    bool exactIncluded = _includeExact == null || _includeExact.Contains(value);
                    return exactIncluded && !_excludeExact.Contains(value);
                }

                bool globIncluded = _includePatterns == null ||
                    MatchesAny(_includePatterns, host, path);
                return globIncluded && !MatchesAny(_excludePatterns, host, path);
            }

            private static Regex[] Compile(ImmutableArray<string>? patterns) =>
                patterns.HasValue ? patterns.Value.Select(HostListEntry.CreatePattern).ToArray() : null;

            private static bool MatchesAny(Regex[] patterns, string host, string path) =>
                patterns != null && patterns.Any(pattern => HostListEntry.Matches(pattern, host, path));
        }

        private sealed class MutableCounts
        {
            internal long Total;
            internal long Matched;
            internal long Selected;
            internal long Eligible;
            internal long SkippedIncomplete;
            internal long NoRecordedResponse;

            internal CaptureExportCounts ToRecord() =>
                new(Total, Matched, Selected, Eligible, 0, 0, SkippedIncomplete, NoRecordedResponse);
        }
    }
}
