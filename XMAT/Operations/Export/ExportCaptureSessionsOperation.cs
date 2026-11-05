// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XMAT.DataAbstractionLayer;
using XMAT.Operations.Analysis;
using XMAT.Operations.Captures;
using XMAT.WebServiceCapture;

namespace XMAT.Operations.Export
{
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
                            snapshot.DeviceDescriptors),
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
                if (!FormatMatches(request.Format, request.OutputPath))
                {
                    throw new CaptureExportException(
                        CaptureExportErrorCodes.FormatMismatch,
                        OperationOutcome.InvalidInput);
                }
                PrecheckDestination(request);
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
                            loaded);
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
                OperationResult<CaptureSessionSelection> selection = CaptureSessionSelector.SelectValidated(
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
                        context.Progress.Report(new OperationProgress(PublishPhase, 0));
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
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                result = FailedResult(result, selected, unavailable, unavailableByDevice);
                var failure = ClassifyFailure(exception);
                return Failure(
                    failure.Outcome,
                    result,
                    diagnostics,
                    failure.Code,
                    failure.Message,
                    failure.Outcome == OperationOutcome.Cancelled
                        ? OperationDiagnosticSeverity.Info
                        : OperationDiagnosticSeverity.Error);
            }
            finally
            {
                if (ownsSnapshot)
                {
                    snapshot?.Dispose();
                }
            }
        }

        private static (
            OperationOutcome Outcome,
            string Code,
            string Message) ClassifyFailure(Exception exception)
        {
            if (exception is CaptureInputException input)
            {
                bool limit = input.Code is "analysis.input.too-large" or "analysis.input.limit";
                return (
                    OperationOutcome.InvalidInput,
                    limit ? CaptureExportErrorCodes.InputLimit : input.Code,
                    limit ? "The capture exceeds an export input limit." : input.DiagnosticMessage);
            }
            return exception switch
            {
                OperationCanceledException => (
                    OperationOutcome.Cancelled,
                    "export.cancelled",
                    "Export was cancelled."),
                CaptureExportException export => (
                    export.Outcome,
                    export.Code,
                    export.Message),
                CaptureExportDestinationException destination => (
                    OperationOutcome.Conflict,
                    destination.Code,
                    destination.Message),
                CaptureSaveException save => (
                    OperationOutcome.Conflict,
                    "export.output.open-capture",
                    save.Message),
                CaptureSnapshotChangedException => (
                    OperationOutcome.Failed,
                    "export.snapshot.changed",
                    "A selected capture row changed during export."),
                IOException or UnauthorizedAccessException => (
                    OperationOutcome.Failed,
                    CaptureExportErrorCodes.WriteFailed,
                    "The export could not be written."),
                _ => (
                    OperationOutcome.InternalError,
                    "export.internal",
                    "Export could not be completed because of an internal failure."),
            };
        }

        private static bool FormatMatches(CaptureExportFormat format, string path)
        {
            string extension = Path.GetExtension(path);
            return format == CaptureExportFormat.Xmatcap
                ? string.Equals(extension, ".xmatcap", StringComparison.OrdinalIgnoreCase)
                : string.Equals(extension, ".saz", StringComparison.OrdinalIgnoreCase);
        }

        private static void PrecheckDestination(ExportCaptureRequest request)
        {
            string output = Path.GetFullPath(request.OutputPath);
            if (!string.IsNullOrWhiteSpace(request.InputPath) &&
                string.Equals(
                    output,
                    Path.GetFullPath(request.InputPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new CaptureExportDestinationException("export.output.is-source");
            }
            if (!request.ReplaceExisting && File.Exists(output))
            {
                throw new CaptureExportDestinationException("export.output.exists");
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
