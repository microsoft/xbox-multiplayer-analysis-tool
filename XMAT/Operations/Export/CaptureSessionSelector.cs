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

        internal static OperationResult<CaptureSelectionSpec> Validate(CaptureSelectionSpec selection) =>
            Validate(selection, out _);

        internal static OperationResult<CaptureSelectionSpec> Validate(
            CaptureSelectionSpec selection,
            out ProcessFilter compiled)
        {
            compiled = null;
            if (selection == null)
            {
                return Failure("export.selection.missing");
            }

            ProcessFilter process;
            try
            {
                process = ProcessFilter.Compile(selection.Process);
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
                !TryNormalizeHosts(host.Exclude, host.Mode, out ImmutableArray<string>? exclude, out hostCode))
            {
                return Failure(hostCode);
            }
            if (!TryPreserveExact(selection.Statuses, "status", out ImmutableArray<string>? statuses, out string code) ||
                !TryPreserveExact(selection.Methods, "method", out ImmutableArray<string>? methods, out code))
            {
                return Failure(code);
            }

            var canonical = new CaptureSelectionSpec(
                process.Specification,
                new HostSelection(host.Mode, include, exclude ?? ImmutableArray<string>.Empty),
                statuses,
                methods,
                selection.Keys,
                selection.MatchNone);
            compiled = process;
            return OperationResult<CaptureSelectionSpec>.Success(canonical);
        }

        private static bool TryNormalizeHosts(
            ImmutableArray<string>? values,
            HostMatchMode mode,
            out ImmutableArray<string>? normalized,
            out string code)
        {
            if (mode == HostMatchMode.Exact)
            {
                return TryPreserveExact(values, "host", out normalized, out code);
            }
            if (!values.HasValue)
            {
                normalized = null;
                code = null;
                return true;
            }
            if (!TryNormalizeGlob(values.Value, out ImmutableArray<string> concrete, out code))
            {
                normalized = null;
                return false;
            }
            normalized = concrete;
            return true;
        }

        private static bool TryNormalizeGlob(
            ImmutableArray<string> values,
            out ImmutableArray<string> normalized,
            out string code)
        {
            code = "export.selection.invalid-host";
            if (values.IsDefault)
            {
                normalized = ImmutableArray<string>.Empty;
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

        private static bool TryPreserveExact(
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
            ImmutableArray<string> source = values.Value.IsDefault
                ? ImmutableArray<string>.Empty
                : values.Value;
            if (source.Any(value => value == null))
            {
                normalized = null;
                code = $"export.selection.invalid-{field}";
                return false;
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
            bool isLive,
            CancellationToken cancellationToken = default)
        {
            if (sessions == null)
            {
                return Failure(
                    "export.selection.sessions-missing",
                    "Capture sessions are required.");
            }
            OperationResult<CaptureSelectionSpec> validated =
                CaptureSelectionValidator.Validate(selection, out ProcessFilter process);
            if (!validated.Succeeded)
            {
                return new OperationResult<CaptureSessionSelection>(
                    validated.Outcome,
                    null,
                    validated.Diagnostics);
            }
            return SelectValidated(sessions, validated.Value, process, deviceId, isLive, cancellationToken);
        }

        internal static OperationResult<CaptureSessionSelection> SelectValidated(
            IReadOnlyList<CaptureExportSession> sessions,
            CaptureSelectionSpec canonical,
            ProcessFilter process,
            string deviceId,
            bool isLive,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sessions.Any(session =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return session == null;
                }))
            {
                return Failure(
                    "export.selection.session-invalid",
                    "A capture session is invalid.");
            }
            List<CaptureExportSession> candidates = sessions
                .Where(session =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return string.IsNullOrEmpty(deviceId) ||
                        string.Equals(session.DeviceId, deviceId, StringComparison.Ordinal);
                })
                .ToList();

            var processSessions = candidates
                .Select(session =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return new CaptureSessionRef(session.Key, session.DeviceId, session.Process);
                })
                .ToList();
            OperationResult<FilterCaptureSessionsResult> processResult =
                FilterCaptureSessionsOperation.SelectCompiled(
                    processSessions,
                    process,
                    cancellationToken);
            if (!processResult.Succeeded)
            {
                return new OperationResult<CaptureSessionSelection>(
                    processResult.Outcome,
                    null,
                    processResult.Diagnostics);
            }

            HashSet<CaptureRowKey> processKeys = KeySet(
                processResult.Value.IncludedKeys,
                cancellationToken);
            HostMatcher host = HostMatcher.Compile(canonical.Host, cancellationToken);
            HashSet<string> statuses = ExactSet(canonical.Statuses, cancellationToken);
            HashSet<string> methods = ExactSet(canonical.Methods, cancellationToken);
            var keys = new List<CaptureRowKey>();
            var overall = new MutableCounts();
            var devices = new Dictionary<string, MutableCounts>(StringComparer.Ordinal);
            foreach (CaptureExportSession session in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
            cancellationToken.ThrowIfCancellationRequested();

            return OperationResult<CaptureSessionSelection>.Success(new CaptureSessionSelection(
                keys.AsReadOnly(),
                overall.ToRecord(),
                devices.ToDictionary(item => item.Key, item => item.Value.ToRecord(), StringComparer.Ordinal),
                processResult.Value.Scope));
        }

        private static HashSet<CaptureRowKey> KeySet(
            IEnumerable<CaptureRowKey> values,
            CancellationToken cancellationToken)
        {
            var result = new HashSet<CaptureRowKey>();
            foreach (CaptureRowKey value in values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(value);
            }
            return result;
        }

        private static HashSet<string> ExactSet(
            ImmutableArray<string>? values,
            CancellationToken cancellationToken)
        {
            if (!values.HasValue)
            {
                return null;
            }
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (string value in values.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(value);
            }
            return result;
        }

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

            private HostMatcher(
                HostSelection selection,
                CancellationToken cancellationToken)
            {
                _mode = selection.Mode;
                if (_mode == HostMatchMode.Glob)
                {
                    _includePatterns = Compile(selection.Include, cancellationToken);
                    _excludePatterns = Compile(selection.Exclude, cancellationToken);
                }
                else
                {
                    _includeExact = ExactSet(selection.Include, cancellationToken);
                    _excludeExact = ExactSet(selection.Exclude, cancellationToken);
                }
            }

            internal static HostMatcher Compile(
                HostSelection selection,
                CancellationToken cancellationToken) =>
                new(selection, cancellationToken);

            internal bool Matches(string host, string path)
            {
                if (_mode == HostMatchMode.Exact)
                {
                    string value = host ?? string.Empty;
                    bool exactIncluded = _includeExact == null || _includeExact.Contains(value);
                    return exactIncluded && !_excludeExact.Contains(value);
                }

                var candidates = HostListEntry.MatchCandidates(host, path);
                bool globIncluded = _includePatterns == null ||
                    MatchesAny(_includePatterns, candidates);
                return globIncluded && !MatchesAny(_excludePatterns, candidates);
            }

            private static Regex[] Compile(
                ImmutableArray<string>? patterns,
                CancellationToken cancellationToken)
            {
                if (!patterns.HasValue)
                {
                    return null;
                }
                var result = new Regex[patterns.Value.Length];
                for (var index = 0; index < result.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result[index] = HostListEntry.CreatePattern(patterns.Value[index]);
                }
                return result;
            }

            private static bool MatchesAny(
                Regex[] patterns,
                HostMatchCandidates candidates) =>
                patterns != null && patterns.Any(pattern => HostListEntry.Matches(pattern, candidates));
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
