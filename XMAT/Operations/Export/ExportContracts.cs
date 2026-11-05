// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using XMAT.DataAbstractionLayer;
using XMAT.Operations.Analysis;
using XMAT.Operations.Captures;
using XMAT.Operations.Processes;
using XMAT.WebServiceCapture;

namespace XMAT.Operations.Export
{
    internal enum HostMatchMode
    {
        Glob,
        Exact,
    }

    internal enum CaptureExportFormat
    {
        Xmatcap,
        Saz,
        SanitizedSaz,
    }

    internal static class CaptureExportFormats
    {
        internal static bool HasExpectedExtension(CaptureExportFormat format, string path)
        {
            string extension = System.IO.Path.GetExtension(path);
            return format == CaptureExportFormat.Xmatcap
                ? string.Equals(extension, ".xmatcap", StringComparison.OrdinalIgnoreCase)
                : string.Equals(extension, ".saz", StringComparison.OrdinalIgnoreCase);
        }
    }

    // Include null disables host inclusion. A present default or empty array is enabled and matches nothing. Exclude
    // default is normalized to empty. Exact mode accepts "" as the GUI's missing-host value; glob mode rejects it.
    internal sealed record HostSelection(
        HostMatchMode Mode,
        ImmutableArray<string>? Include,
        ImmutableArray<string> Exclude);

    // Statuses and Methods use the same null/default/empty distinction as Host.Include; exact arrays may contain "".
    // Null Keys disables key selection, while an empty set and MatchNone both select no rows.
    internal sealed record CaptureSelectionSpec(
        ProcessFilterSpec Process,
        HostSelection Host,
        ImmutableArray<string>? Statuses,
        ImmutableArray<string>? Methods,
        ImmutableHashSet<CaptureRowKey> Keys,
        bool MatchNone);

    internal sealed record ExportCaptureRequest(
        string InputPath,
        ICaptureExportSnapshot Snapshot,
        string DeviceId,
        CaptureSelectionSpec Selection,
        CaptureExportFormat Format,
        string OutputPath,
        bool ReplaceExisting,
        AnalysisLimits Limits,
        bool AllowUnprotectedOutput = false);

    // Key is the exact ProxyConnectionModel.RowKey for live, loaded, and imported tabs. Host, Path, Status, and Method
    // are frozen stored/display values: NULL becomes "", with no other normalization.
    internal sealed record CaptureExportSession(
        CaptureRowKey Key,
        string DeviceId,
        string Host,
        string Path,
        string Status,
        string Method,
        ClientProcessInfo Process,
        bool HasRecordedResponseHeaders,
        bool HasRecordedResponseTimestamp,
        long PayloadVersion)
    {
        internal bool IsComplete => HasRecordedResponseHeaders && HasRecordedResponseTimestamp;
        internal bool NoRecordedResponse => !HasRecordedResponseHeaders;
    }

    // NoRecordedResponse is a subset of SkippedIncomplete. Published=false requires Exported=0.
    internal sealed record CaptureExportCounts(
        long Total,
        long Matched,
        long Selected,
        long Eligible,
        long Exported,
        long Unavailable,
        long SkippedIncomplete,
        long NoRecordedResponse);

    internal interface ICaptureExportSnapshot : IDisposable
    {
        IReadOnlyList<SqLtSchemaColumn> CaptureFields { get; }
        IReadOnlyList<SqLtSchemaColumn> DeviceFields { get; }
        IReadOnlyDictionary<string, RawExportRow> DeviceDescriptors { get; }
        IReadOnlyList<CaptureExportSession> Sessions { get; }
        bool IsLive { get; }

        // File inputs use "xmatcap" or "saz"; GUI snapshots use "records". GUI snapshots use AnalysisLimits.Default.
        string InputKind { get; }

        // Identity and hash may be null for snapshots without an applicable source file. Destination validation still
        // rejects every applicable source path or identity.
        SqLtFileIdentity? InputIdentity { get; }
        string InputSha256 { get; }

        CaptureExportChunk ReadChunk(
            IReadOnlyList<CaptureRowKey> keys,
            CancellationToken cancellationToken);

        void ValidateDestination(string path);
    }

    // Values enumerates the exact CaptureFields names in schema ordinal order. Values are raw SQLite storage values
    // (string, long, double, byte[], or null): bodies remain stored base64 TEXT and timestamps remain stored strings.
    // Missing columns have no key. The four process values are the frozen ClientProcessRecordCodec overlay.
    internal sealed record RawExportRow(
        CaptureRowKey Key,
        long RowId,
        long PayloadVersion,
        IReadOnlyDictionary<string, object> Values);

    internal sealed record CaptureExportChunk(
        IReadOnlyList<CaptureRowKey> ConsumedKeys,
        IReadOnlyList<RawExportRow> Rows);

    internal interface ICaptureExportWriter : IDisposable
    {
        void Write(RawExportRow row, CancellationToken cancellationToken);
        ExportWriteSummary Complete(CancellationToken cancellationToken);
    }

    // RowsWritten measures staging activity, not published output.
    internal sealed record ExportWriteSummary(
        long RowsWritten,
        SanitizedExportSummary Sanitization);

    internal interface ICaptureExportPublisher
    {
        Task<PublishedCapture> PublishAsync(
            string destination,
            bool replaceExisting,
            bool allowUnprotected,
            Action<string> validateDestination,
            Func<string, CancellationToken, Task> writeStaged,
            CancellationToken cancellationToken);
    }

    internal sealed record PublishedCapture(
        long Bytes,
        string Sha256,
        bool ReplacedExisting,
        bool Protected);

    // Counts, Devices, ProcessScope, and Sanitization remain null until selection or writing evaluates them. OutputBytes
    // OutputSha256 and OutputProtected remain null until meaningful publication data exists. A non-published result has
    // Exported=0.
    internal sealed record ExportCaptureResult(
        CaptureExportFormat Format,
        CaptureSelectionSpec Selection,
        CaptureExportCounts Counts,
        IReadOnlyDictionary<string, CaptureExportCounts> Devices,
        ProcessFilterScope ProcessScope,
        SanitizedExportSummary Sanitization,
        string InputKind,
        string InputSha256,
        long? OutputBytes,
        string OutputSha256,
        bool Published,
        bool? OutputProtected);

    internal static class CaptureExportErrorCodes
    {
        internal const string InvalidHttp = "export.input.invalid-http";
        internal const string InvalidBody = "export.input.invalid-body";
        internal const string InputLimit = "export.input.limit";
        internal const string OutputUnprotected = "export.output.unprotected";
        internal const string FormatMismatch = "export.output.format-mismatch";
        internal const string SanitizerFailed = "export.sanitize.failed";
        internal const string WriteFailed = "export.write.failed";
    }

    // Only expected input, destination-protection, sanitizer, and write failures use this type. Cancellation and
    // unexpected internal failures retain their native classification.
    internal sealed class CaptureExportException : Exception
    {
        internal CaptureExportException(
            string code,
            OperationOutcome outcome,
            Exception innerException = null)
            : base(FixedMessage(code, outcome), innerException)
        {
            Code = code;
            Outcome = outcome;
        }

        internal string Code { get; }
        internal OperationOutcome Outcome { get; }

        private static string FixedMessage(string code, OperationOutcome outcome)
        {
            (OperationOutcome Expected, string Text) definition = code switch
            {
                CaptureExportErrorCodes.InvalidHttp =>
                    (OperationOutcome.InvalidInput, "The capture contains invalid HTTP data."),
                CaptureExportErrorCodes.InvalidBody =>
                    (OperationOutcome.InvalidInput, "The capture contains invalid body data."),
                CaptureExportErrorCodes.InputLimit =>
                    (OperationOutcome.InvalidInput, "The capture exceeds an export input limit."),
                CaptureExportErrorCodes.OutputUnprotected =>
                    (OperationOutcome.Failed, "The export destination cannot protect the output."),
                CaptureExportErrorCodes.FormatMismatch =>
                    (OperationOutcome.InvalidInput, "The export format does not match the output file name."),
                CaptureExportErrorCodes.SanitizerFailed =>
                    (OperationOutcome.Failed, "The sanitized export could not be produced."),
                CaptureExportErrorCodes.WriteFailed =>
                    (OperationOutcome.Failed, "The export could not be written."),
                _ => throw new ArgumentOutOfRangeException(nameof(code), "The export error code is not recognized."),
            };
            if (outcome != definition.Expected)
            {
                throw new ArgumentException(
                    "The export error code and outcome do not match.",
                    nameof(outcome));
            }
            return definition.Text;
        }
    }

    internal sealed record CaptureSessionSelection(
        IReadOnlyList<CaptureRowKey> Keys,
        CaptureExportCounts Counts,
        IReadOnlyDictionary<string, CaptureExportCounts> Devices,
        ProcessFilterScope ProcessScope);
}
