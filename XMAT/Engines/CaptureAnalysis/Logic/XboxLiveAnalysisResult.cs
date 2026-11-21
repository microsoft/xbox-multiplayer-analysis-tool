// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using CaptureAnalysisEngine;
using XMAT.Operations.Captures;

namespace XMAT.XboxLiveCaptureAnalysis
{
    internal sealed record XboxLiveAnalysisResult(
        TraceAnalysisResult Analysis,
        IReadOnlyDictionary<string, long> RecordByBreadcrumb,
        int ConvertedRecords,
        int SkippedRecords)
    {
        internal ProcessFilterScope ProcessScope { get; init; }
        internal IReadOnlyList<CaptureRowKey> IncludedKeys { get; init; }
    }
}
