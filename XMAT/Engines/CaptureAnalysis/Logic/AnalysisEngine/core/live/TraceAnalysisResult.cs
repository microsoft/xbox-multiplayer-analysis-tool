// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;

namespace CaptureAnalysisEngine
{
    public sealed record ConsoleAnalysisResult(
        string ConsoleId,
        IReadOnlyList<RuleResult> Rules,
        IReadOnlyList<ReportDocument> Reports);

    public sealed record TraceAnalysisResult(IReadOnlyList<ConsoleAnalysisResult> Consoles);
}
