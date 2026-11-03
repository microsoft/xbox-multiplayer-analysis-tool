// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.SharedInterfaces
{
    public interface ICaptureInvestigation
    {
        Task<IReadOnlyList<CaptureAnalyzerDescriptor>> GetAnalyzersAsync(CancellationToken cancellationToken = default);
        Task<IReadOnlyList<CaptureWebSocketAvailability>> GetWebSocketsAsync(
            Guid sourceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CaptureWebSocketAvailability>>(Array.Empty<CaptureWebSocketAvailability>());
        Task<IReadOnlyList<CaptureCompletedReportDescriptor>> GetCompletedReportsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CaptureCompletedReportDescriptor>>(Array.Empty<CaptureCompletedReportDescriptor>());
        Task<CaptureDataResult> OpenCompletedReportAsync(Guid reportId, CancellationToken cancellationToken = default) =>
            Task.FromException<CaptureDataResult>(new CaptureDataException(CaptureDataError.UnsupportedData));
        Task<CaptureDataResult> SearchAsync(
            Guid sourceId, CaptureDataKind kind, CaptureDataFilter filter, QueryExecutionOptions options,
            CancellationToken cancellationToken = default);
        Task<CaptureDataResult> AnalyzeAsync(
            Guid sourceId, CaptureDataKind kind, CaptureDataFilter filter, QueryExecutionOptions options,
            CancellationToken cancellationToken = default);
    }
}
