// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
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

        IReadOnlyList<RawExportRow> ReadChunk(
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

    // Only expected input, sanitizer, and write failures use this type. Cancellation and unexpected internal failures
    // retain their native classification.
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

    internal sealed class ExportCaptureSessionsOperation :
        IOperation<ExportCaptureRequest, ExportCaptureResult>
    {
        private readonly CaptureInputReader _inputReader = new();

        internal ICaptureExportPublisher Publisher { get; set; } = new CaptureExportPublisher();

        internal Func<
            CaptureExportFormat,
            string,
            ICaptureExportSnapshot,
            long,
            ICaptureExportWriter> WriterFactory { get; set; } =
                static (format, path, snapshot, expectedRows) =>
                    format switch
                    {
                        CaptureExportFormat.Xmatcap => new XmatcapExportWriter(
                            path,
                            snapshot.CaptureFields,
                            snapshot.DeviceFields,
                            snapshot.DeviceDescriptors,
                            includeOnlyCapturedDevices: true),
                        CaptureExportFormat.Saz => FiddlerSazHandler.CreateExportWriter(
                            path,
                            sanitized: false,
                            expectedRows: expectedRows),
                        CaptureExportFormat.SanitizedSaz => FiddlerSazHandler.CreateExportWriter(
                            path,
                            sanitized: true,
                            expectedRows: expectedRows),
                        _ => throw new ArgumentOutOfRangeException(nameof(format)),
                    };

        internal const string SelectPhase = "export.select";
        internal const string ReadPhase = "export.read";
        internal const string WritePhase = "export.write";
        internal const string PublishPhase = "export.publish";

        public async Task<OperationResult<ExportCaptureResult>> ExecuteAsync(
            ExportCaptureRequest request,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            context ??= OperationContext.None;
            var diagnostics = new List<OperationDiagnostic>();
            ExportCaptureResult result = EmptyResult(request);
            ICaptureExportSnapshot snapshot = request?.Snapshot;
            var ownsSnapshot = false;
            CaptureSessionSelection selected = null;
            var unavailable = 0L;
            var unavailableByDevice = new Dictionary<string, long>(StringComparer.Ordinal);
            Dictionary<CaptureRowKey, CaptureExportSession> sessionByKey = null;
            long exported = 0;
            ExportWriteSummary writeSummary = null;
            try
            {
                if (request == null ||
                    request.Selection == null ||
                    request.Limits == null ||
                    string.IsNullOrWhiteSpace(request.OutputPath) ||
                    !Enum.IsDefined(request.Format) ||
                    (string.IsNullOrWhiteSpace(request.InputPath) == (request.Snapshot == null)))
                {
                    return Failure(
                        OperationOutcome.InvalidInput,
                        result,
                        diagnostics,
                        "export.request.invalid",
                        "The export request is invalid.");
                }
                CaptureInputReader.ValidateLimits(request.Limits);
                OperationResult<CaptureSelectionSpec> validated =
                    CaptureSelectionValidator.Validate(request.Selection);
                if (!validated.Succeeded)
                {
                    return new OperationResult<ExportCaptureResult>(
                        validated.Outcome,
                        result,
                        validated.Diagnostics);
                }
                result = result with { Selection = validated.Value };
                cancellationToken.ThrowIfCancellationRequested();
                if (snapshot == null)
                {
                    LoadedInput loaded = _inputReader.Read(
                        AnalysisInput.FromFile(request.InputPath),
                        request.Limits,
                        diagnostics,
                        cancellationToken);
                    try
                    {
                        snapshot = CaptureExportSnapshot.FromLoadedInput(
                            loaded,
                            request.Limits,
                            request.DeviceId);
                        ownsSnapshot = true;
                    }
                    catch
                    {
                        loaded.Dispose();
                        throw;
                    }
                }
                result = result with
                {
                    InputKind = snapshot.InputKind,
                    InputSha256 = snapshot.InputSha256,
                };
                context.Progress.Report(new OperationProgress(SelectPhase, 0));
                OperationResult<CaptureSessionSelection> selection = CaptureSessionSelector.Select(
                    snapshot.Sessions,
                    validated.Value,
                    request.DeviceId,
                    snapshot.IsLive);
                if (!selection.Succeeded)
                {
                    return new OperationResult<ExportCaptureResult>(
                        selection.Outcome,
                        result,
                        selection.Diagnostics);
                }
                selected = selection.Value;
                result = result with
                {
                    Counts = selected.Counts,
                    Devices = selected.Devices,
                    ProcessScope = selected.ProcessScope,
                };
                context.Progress.Report(new OperationProgress(SelectPhase, 1));
                if (selected.Counts.Eligible == 0)
                {
                    return Failure(
                        OperationOutcome.NoAnalyzableData,
                        result,
                        diagnostics,
                        "export.no-eligible-rows",
                        "The selection contains no eligible capture sessions.");
                }

                sessionByKey = snapshot.Sessions.ToDictionary(session => session.Key);
                context.Progress.Report(new OperationProgress(ReadPhase, 0));
                context.Progress.Report(new OperationProgress(WritePhase, 0));
                context.Progress.Report(new OperationProgress(PublishPhase, 0));
                PublishedCapture published = await Publisher.PublishAsync(
                    request.OutputPath,
                    request.ReplaceExisting,
                    request.AllowUnprotectedOutput,
                    snapshot.ValidateDestination,
                    (stagedPath, token) =>
                    {
                        using ICaptureExportWriter writer = WriterFactory(
                            request.Format,
                            stagedPath,
                            snapshot,
                            selected.Counts.Eligible);
                        for (var offset = 0; offset < selected.Keys.Count; offset += 64)
                        {
                            token.ThrowIfCancellationRequested();
                            CaptureRowKey[] requested = selected.Keys.Skip(offset).Take(64).ToArray();
                            IReadOnlyList<RawExportRow> rows;
                            try
                            {
                                rows = snapshot.ReadChunk(requested, token);
                            }
                            catch (InvalidOperationException exception)
                            {
                                throw new CaptureSnapshotChangedException(exception);
                            }
                            var requestedSet = requested.ToHashSet();
                            if (rows == null ||
                                rows.Any(row =>
                                    row == null ||
                                    !requestedSet.Contains(row.Key) ||
                                    row.RowId != row.Key.RowId ||
                                    row.PayloadVersion != sessionByKey[row.Key].PayloadVersion) ||
                                rows.Select(row => row.Key).Distinct().Count() != rows.Count)
                            {
                                throw new CaptureSnapshotChangedException();
                            }
                            var returned = rows.Select(row => row.Key).ToHashSet();
                            foreach (CaptureRowKey missing in requested.Where(key => !returned.Contains(key)))
                            {
                                unavailable++;
                                string device = sessionByKey[missing].DeviceId;
                                unavailableByDevice[device] =
                                    unavailableByDevice.TryGetValue(device, out long count) ? count + 1 : 1;
                            }
                            context.Progress.Report(new OperationProgress(
                                ReadPhase,
                                Math.Min(1d, (offset + requested.Length) / (double)selected.Keys.Count)));
                            foreach (RawExportRow row in rows)
                            {
                                writer.Write(row, token);
                                exported++;
                                context.Progress.Report(new OperationProgress(
                                    WritePhase,
                                    Math.Min(1d, exported / (double)selected.Counts.Eligible)));
                            }
                        }
                        if (exported == 0)
                        {
                            throw new NoExportRowsException();
                        }
                        writeSummary = writer.Complete(token);
                        if (writeSummary == null || writeSummary.RowsWritten != exported)
                        {
                            throw new InvalidOperationException("The export writer reported an inconsistent row count.");
                        }
                        return Task.CompletedTask;
                    },
                    cancellationToken).ConfigureAwait(false);
                CaptureExportCounts completedCounts = CompletedCounts(selected.Counts, exported, unavailable);
                IReadOnlyDictionary<string, CaptureExportCounts> completedDevices =
                    CompletedDevices(selected.Devices, sessionByKey, selected.Keys, unavailableByDevice);
                AddUnavailableDiagnostic(diagnostics, unavailable);
                result = result with
                {
                    Counts = completedCounts,
                    Devices = completedDevices,
                    Sanitization = writeSummary.Sanitization,
                    OutputBytes = published.Bytes,
                    OutputSha256 = published.Sha256,
                    Published = true,
                    OutputProtected = published.Protected,
                };
                context.Progress.Report(new OperationProgress(PublishPhase, 1));
                return OperationResult<ExportCaptureResult>.Success(result, diagnostics);
            }
            catch (NoExportRowsException)
            {
                result = FailedResult(result, selected, unavailable, unavailableByDevice);
                AddUnavailableDiagnostic(diagnostics, unavailable);
                return Failure(
                    OperationOutcome.NoAnalyzableData,
                    result,
                    diagnostics,
                    "export.no-eligible-rows",
                    "No selected capture rows remained available.");
            }
            catch (OperationCanceledException)
            {
                result = FailedResult(result, selected, unavailable, unavailableByDevice);
                return Failure(
                    OperationOutcome.Cancelled,
                    result,
                    diagnostics,
                    "export.cancelled",
                    "Export was cancelled.",
                    OperationDiagnosticSeverity.Info);
            }
            catch (CaptureInputException exception)
            {
                return Failure(
                    OperationOutcome.InvalidInput,
                    result,
                    diagnostics,
                    exception.Code,
                    exception.DiagnosticMessage);
            }
            catch (CaptureExportException exception)
            {
                result = FailedResult(result, selected, unavailable, unavailableByDevice);
                return Failure(
                    exception.Outcome,
                    result,
                    diagnostics,
                    exception.Code,
                    exception.Message);
            }
            catch (CaptureExportDestinationException exception)
            {
                result = FailedResult(result, selected, unavailable, unavailableByDevice);
                return Failure(
                    OperationOutcome.Conflict,
                    result,
                    diagnostics,
                    exception.Code,
                    exception.Message);
            }
            catch (CaptureSaveException exception)
            {
                result = FailedResult(result, selected, unavailable, unavailableByDevice);
                return Failure(
                    OperationOutcome.Conflict,
                    result,
                    diagnostics,
                    "export.output.open-capture",
                    exception.Message);
            }
            catch (CaptureSnapshotChangedException)
            {
                result = FailedResult(result, selected, unavailable, unavailableByDevice);
                return Failure(
                    OperationOutcome.Failed,
                    result,
                    diagnostics,
                    "export.snapshot.changed",
                    "A selected capture row changed during export.");
            }
            catch (CaptureExportCleanupException exception)
            {
                AddUnavailableDiagnostic(diagnostics, unavailable);
                if (exception.Publication != null && selected != null)
                {
                    result = result with
                    {
                        Counts = CompletedCounts(selected.Counts, exported, unavailable),
                        Devices = CompletedDevices(
                            selected.Devices,
                            sessionByKey,
                            selected.Keys,
                            unavailableByDevice),
                        Sanitization = writeSummary?.Sanitization,
                        OutputBytes = exception.Publication.Bytes,
                        OutputSha256 = exception.Publication.Sha256,
                        Published = true,
                        OutputProtected = exception.Publication.Protected,
                    };
                }
                else
                {
                    result = FailedResult(result, selected, unavailable, unavailableByDevice);
                }
                return Failure(
                    OperationOutcome.CleanupFailed,
                    result,
                    diagnostics,
                    "export.cleanup.failed",
                    exception.Message);
            }
            catch (IOException)
            {
                result = FailedResult(result, selected, unavailable, unavailableByDevice);
                return Failure(
                    OperationOutcome.Failed,
                    result,
                    diagnostics,
                    CaptureExportErrorCodes.WriteFailed,
                    "The export could not be written.");
            }
            catch (UnauthorizedAccessException)
            {
                result = FailedResult(result, selected, unavailable, unavailableByDevice);
                return Failure(
                    OperationOutcome.Failed,
                    result,
                    diagnostics,
                    CaptureExportErrorCodes.WriteFailed,
                    "The export could not be written.");
            }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                result = FailedResult(result, selected, unavailable, unavailableByDevice);
                return Failure(
                    OperationOutcome.InternalError,
                    result,
                    diagnostics,
                    "export.internal",
                    "Export could not be completed because of an internal failure.");
            }
            finally
            {
                if (ownsSnapshot)
                {
                    snapshot?.Dispose();
                }
            }
        }

        private static ExportCaptureResult EmptyResult(ExportCaptureRequest request) =>
            new(
                request?.Format ?? CaptureExportFormat.Xmatcap,
                request?.Selection,
                null,
                null,
                null,
                null,
                request?.Snapshot?.InputKind,
                request?.Snapshot?.InputSha256,
                null,
                null,
                Published: false,
                OutputProtected: null);

        private static CaptureExportCounts CompletedCounts(
            CaptureExportCounts counts,
            long exported,
            long unavailable) =>
            counts with { Exported = exported, Unavailable = unavailable };

        private static IReadOnlyDictionary<string, CaptureExportCounts> CompletedDevices(
            IReadOnlyDictionary<string, CaptureExportCounts> devices,
            IReadOnlyDictionary<CaptureRowKey, CaptureExportSession> sessions,
            IReadOnlyList<CaptureRowKey> keys,
            IReadOnlyDictionary<string, long> unavailable)
        {
            var eligibleByDevice = keys.GroupBy(
                    key => sessions[key].DeviceId,
                    StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => (long)group.Count(), StringComparer.Ordinal);
            return devices.ToDictionary(
                item => item.Key,
                item =>
                {
                    long missing = unavailable.TryGetValue(item.Key, out long value) ? value : 0;
                    long eligible = eligibleByDevice.TryGetValue(item.Key, out long count) ? count : 0;
                    return item.Value with { Exported = eligible - missing, Unavailable = missing };
                },
                StringComparer.Ordinal);
        }

        private static ExportCaptureResult FailedResult(
            ExportCaptureResult result,
            CaptureSessionSelection selected,
            long unavailable,
            IReadOnlyDictionary<string, long> unavailableByDevice)
        {
            if (selected == null)
            {
                return result with
                {
                    OutputBytes = null,
                    OutputSha256 = null,
                    Published = false,
                    OutputProtected = null,
                };
            }
            IReadOnlyDictionary<string, CaptureExportCounts> devices = selected.Devices.ToDictionary(
                item => item.Key,
                item => item.Value with
                {
                    Exported = 0,
                    Unavailable = unavailableByDevice.TryGetValue(item.Key, out long count) ? count : 0,
                },
                StringComparer.Ordinal);
            return result with
            {
                Counts = selected.Counts with { Exported = 0, Unavailable = unavailable },
                Devices = devices,
                OutputBytes = null,
                OutputSha256 = null,
                Published = false,
                OutputProtected = null,
            };
        }

        private static void AddUnavailableDiagnostic(
            ICollection<OperationDiagnostic> diagnostics,
            long unavailable)
        {
            if (unavailable != 0 &&
                !diagnostics.Any(diagnostic => diagnostic.Code == "export.rows.unavailable"))
            {
                diagnostics.Add(new OperationDiagnostic(
                    "export.rows.unavailable",
                    OperationDiagnosticSeverity.Warning,
                    "One or more selected capture rows became unavailable."));
            }
        }

        private static OperationResult<ExportCaptureResult> Failure(
            OperationOutcome outcome,
            ExportCaptureResult result,
            ICollection<OperationDiagnostic> diagnostics,
            string code,
            string message,
            OperationDiagnosticSeverity severity = OperationDiagnosticSeverity.Error)
        {
            var all = diagnostics.ToList();
            all.Add(new OperationDiagnostic(code, severity, message));
            return new OperationResult<ExportCaptureResult>(outcome, result, all);
        }

        private sealed class CaptureSnapshotChangedException : Exception
        {
            internal CaptureSnapshotChangedException(Exception innerException = null)
                : base("The capture snapshot changed.", innerException)
            {
            }
        }

        private sealed class NoExportRowsException : Exception
        {
        }
    }
}
