// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XMAT.Operations.Processes;

namespace XMAT.Operations.Captures
{
    internal sealed class FilterCaptureSessionsOperation : IOperation<FilterCaptureSessionsRequest,
        FilterCaptureSessionsResult>
    {
        public Task<OperationResult<FilterCaptureSessionsResult>> ExecuteAsync(
            FilterCaptureSessionsRequest request,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            _ = context;
            return Task.FromResult(Select(request, cancellationToken));
        }

        internal static OperationResult<FilterCaptureSessionsResult> Select(
            FilterCaptureSessionsRequest request,
            CancellationToken cancellationToken)
        {
            if (request?.Sessions == null)
            {
                return OperationResult<FilterCaptureSessionsResult>.Failure(
                    OperationOutcome.InvalidInput,
                    new OperationDiagnostic(
                        "process-filter.sessions-missing",
                        OperationDiagnosticSeverity.Error,
                        "Capture sessions are required."));
            }
            ProcessFilter filter;
            try
            {
                filter = ProcessFilter.Compile(request.Filter);
            }
            catch (ArgumentException)
            {
                return OperationResult<FilterCaptureSessionsResult>.Failure(
                    OperationOutcome.InvalidInput,
                    new OperationDiagnostic(
                        "process-filter.invalid-selector",
                        OperationDiagnosticSeverity.Error,
                        "A process selector is invalid."));
            }
            return SelectCompiled(request.Sessions, filter, cancellationToken);
        }

        internal static OperationResult<FilterCaptureSessionsResult> SelectCompiled(
            IReadOnlyList<CaptureSessionRef> sessions,
            ProcessFilter filter,
            CancellationToken cancellationToken)
        {
            var included = new List<CaptureRowKey>(sessions.Count);
            var deviceTotals = new Dictionary<string, MutableCounts>(StringComparer.Ordinal);
            var total = new MutableCounts();
            foreach (CaptureSessionRef session in sessions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (session == null || string.IsNullOrEmpty(session.DeviceId))
                {
                    return OperationResult<FilterCaptureSessionsResult>.Failure(
                        OperationOutcome.InvalidInput,
                        new OperationDiagnostic(
                            "process-filter.session-invalid",
                            OperationDiagnosticSeverity.Error,
                            "A capture session is invalid."));
                }
                if (!deviceTotals.TryGetValue(session.DeviceId, out MutableCounts device))
                {
                    device = new MutableCounts();
                    deviceTotals.Add(session.DeviceId, device);
                }
                bool unknown = ProcessFilter.IsUnknown(session.Process);
                bool matches = filter.Matches(session.Process);
                total.Add(matches, unknown);
                device.Add(matches, unknown);
                if (matches)
                {
                    included.Add(session.Key);
                }
            }
            var devices = deviceTotals.ToDictionary(
                item => item.Key,
                item => item.Value.ToRecord(),
                StringComparer.Ordinal);
            var scope = new ProcessFilterScope(filter.Specification, total.ToRecord(), devices);
            return OperationResult<FilterCaptureSessionsResult>.Success(
                new FilterCaptureSessionsResult(included.AsReadOnly(), scope));
        }

        private sealed class MutableCounts
        {
            private long _total;
            private long _included;
            private long _unknown;

            internal void Add(bool included, bool unknown)
            {
                _total++;
                if (included)
                {
                    _included++;
                }
                if (unknown)
                {
                    _unknown++;
                }
            }

            internal ProcessFilterCounts ToRecord() => new(_total, _included, _total - _included, _unknown);
        }
    }
}
