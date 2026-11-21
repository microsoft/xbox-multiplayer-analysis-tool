// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Linq;

namespace CaptureAnalysisEngine
{
    public sealed record ConsoleAnalysisResult(
        string ConsoleId,
        IReadOnlyList<RuleResult> Rules,
        IReadOnlyList<ReportDocument> Reports);

    public sealed record TraceAnalysisResult(IReadOnlyList<ConsoleAnalysisResult> Consoles)
    {
        internal static TraceAnalysisResult FromConsoleResults(IReadOnlyDictionary<string, TraceConsoleAnalysis> results) =>
            new(results.Select(console => new ConsoleAnalysisResult(console.Key, console.Value.RuleResults,
                console.Value.Reports)).ToArray());
    }
}
