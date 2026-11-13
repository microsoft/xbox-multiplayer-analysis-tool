// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using CaptureAnalysisEngine;
using XMAT.Operations.Captures;
using XMAT.Operations.Processes;

namespace XMAT.Operations.Analysis
{
    internal sealed record AnalysisLimits(
        long MaxInputBytes,
        long MaxEntryBytes,
        long MaxTotalRetainedBytes,
        int MaxRecords)
    {
        internal const long BytesPerMib = 1024 * 1024;
        internal const int DefaultInputMib = 512;
        internal const int DefaultDataMib = 256;
        internal const int DefaultRecordMib = 128;
        internal const int DefaultRecords = 100_000;

        // 512 MiB source, 128 MiB per archive entry or database record,
        // 256 MiB aggregate retained data, and 100,000 format-specific records
        // (SAZ requests, or CaptureDevices and WebProxyConnections rows combined).
        // A capture row holds its bodies as base64 (4 bytes for every 3), so the
        // record default admits a row with a 32 MiB request body (the proxy's
        // limit) and a response body at the largest capture limit, 40 MiB
        // (ResponseStreamingOptions.MaximumCaptureLimitMegabytes): about 96 MiB.
        internal static AnalysisLimits Default { get; } = FromMib(
            DefaultInputMib,
            DefaultDataMib,
            DefaultRecordMib,
            DefaultRecords);

        internal long? MaxArchiveEntries { get; init; }

        internal long? MaxCentralDirectoryBytes { get; init; }

        // Limits in whole MiB, as xmat analyze takes them; the conversion to bytes is checked.
        internal static AnalysisLimits FromMib(
            int inputMib,
            int dataMib,
            int recordMib,
            int records) =>
            new(
                checked(inputMib * BytesPerMib),
                checked(recordMib * BytesPerMib),
                checked(dataMib * BytesPerMib),
                records);
    }

    // ProcessFilter selects sessions by client process; null or empty analyzes every session.
    internal sealed record AnalysisOptions(
        string DeviceId = null,
        AnalysisLimits Limits = null,
        bool ProduceLegacyReports = false,
        ProcessFilterSpec ProcessFilter = null);

    internal sealed record AnalyzeCaptureRequest(
        AnalysisInput Input,
        AnalysisOptions Options);

    internal sealed record FindingSummary(
        string Rule,
        string Service,
        string Level,
        int Count);

    internal sealed record ConsoleSummary(
        string Id,
        int Calls,
        IReadOnlyList<FindingSummary> Findings);

    internal sealed record DeviceSummary(
        string Id,
        IReadOnlyList<ConsoleSummary> Consoles);

    internal sealed record AnalysisSummary(
        string InputKind,
        string InputSha256,
        IReadOnlyList<DeviceSummary> Devices,
        int TotalCalls,
        int TotalFindings);

    internal sealed record AnalysisResult(AnalysisSummary Summary)
    {
        internal LegacyAnalysisReports Legacy { get; init; }

        // The client-process scope of the selected devices, counted before service conversion, and the keys of the
        // sessions that were analyzed. A run with no analyzable data still returns them.
        internal ProcessFilterScope ProcessScope { get; init; }

        internal IReadOnlyList<CaptureRowKey> IncludedKeys { get; init; }
    }

    internal sealed class LegacyAnalysisReports
    {
        internal LegacyAnalysisReports(
            IReadOnlyDictionary<string, IReadOnlyList<ReportDocument>> perConsoleReports)
        {
            PerConsoleReports = perConsoleReports ??
                throw new ArgumentNullException(nameof(perConsoleReports));
        }

        internal IReadOnlyDictionary<string, IReadOnlyList<ReportDocument>> PerConsoleReports { get; }
    }
}
