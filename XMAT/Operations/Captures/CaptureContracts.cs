// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using XMAT.Operations.Processes;

namespace XMAT.Operations.Captures
{
    public readonly record struct CaptureRowKey(Guid SourceId, long Generation, long RowId, long Incarnation);

    internal sealed record CaptureSessionRef(CaptureRowKey Key, string DeviceId, ClientProcessInfo Process);

    internal sealed record ProcessFilterCounts(long Total, long Included, long Hidden, long Unknown);

    internal sealed record ProcessFilterScope(
        ProcessFilterSpec Filter,
        ProcessFilterCounts Counts,
        IReadOnlyDictionary<string, ProcessFilterCounts> Devices);

    internal sealed record FilterCaptureSessionsRequest(
        IReadOnlyList<CaptureSessionRef> Sessions,
        ProcessFilterSpec Filter);

    internal sealed record FilterCaptureSessionsResult(
        IReadOnlyList<CaptureRowKey> IncludedKeys,
        ProcessFilterScope Scope);
}
