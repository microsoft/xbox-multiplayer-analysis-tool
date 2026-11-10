// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XMAT.DataAbstractionLayer;

namespace XMAT.Operations.Capture
{
    internal static class SazExportCodes
    {
        internal const string SazRequired =
            "capture-file.export.saz-required";
        internal const string UnsupportedLocation =
            "capture-file.export.unsupported-location";
        internal const string NoPrivateStaging =
            "capture-file.export.no-private-staging";
        internal const string AmbiguousExport =
            "capture.text.ambiguous-export";
        internal const string AmbiguousStoredText =
            "capture.text.ambiguous";
        internal const string SourceUnavailable =
            "capture.text.source-unavailable";
        internal const string SourceUnsupported =
            "capture-file.export.source-unsupported";
        internal const string TooManyRows =
            "capture-file.export.too-many-rows";
        internal const string TooLarge =
            "capture-file.export.too-large";
        internal const string InvalidStartLine =
            "capture-file.export.invalid-start-line";
        internal const string SanitizerInvalidHttp =
            "capture-file.sanitize.invalid-http";
        internal const string HeadUnencodable =
            SazWriteException.HeadUnencodableCode;
        internal const string Exists =
            "capture-file.export.exists";
        internal const string Changed =
            "capture-file.export.changed";
        internal const string NotAFile =
            "capture-file.export.not-a-file";
        internal const string ReadOnly =
            "capture-file.export.read-only";
        internal const string InUse =
            "capture-file.export.in-use";
        internal const string AccessDenied =
            "capture-file.export.access-denied";
        internal const string LocationUnverified =
            "capture-file.export.location-unverified";
        internal const string Uncertain =
            "capture-file.export.uncertain";
        internal const string CleanupFailed =
            "capture-file.export.cleanup-failed";
        internal const string Cancelled =
            "capture-file.export.cancelled";
        internal const string WriteFailed =
            "capture-file.export.write-failed";
        internal const string PlanUsed =
            "capture-file.export.plan-used";
        internal const string AnalyzeInputExceeded =
            "capture-file.export.analyze-input-exceeded";
        internal const string PlanningPhase =
            "capture-file.export.planning";
        internal const string WritingPhase =
            "capture-file.export.writing";
        internal const string PublishingPhase =
            "capture-file.export.publishing";
    }

    internal sealed record SazExportTestHooks(
        CaptureArchiveKind? AdmitAsKind = null,
        bool? AdmitAsRemote = null,
        bool RefuseWorkFolder = false);

    internal sealed class SazExportOperation :
        IOperation<SazExportRequest, SazExportResult>
    {
        private const string CancelledMessage =
            "The archive export was cancelled before publication.";
        private const string SourceUnsupportedMessage =
            "The capture source does not support archive export.";
        private const string SourceUnavailableMessage =
            "The capture text source is no longer available for a source-bound decision.";
        private const string AmbiguousMessage =
            "The unmarked capture requires an explicit stored-text decision.";
        private const string TooManyRowsMessage =
            "The archive export has more rows than its limit.";
        private const string TooLargeMessage =
            "The archive export exceeds its stored-data limit.";
        private const string WriteFailedMessage =
            "The archive could not be written.";
        private const string PlanUsedMessage =
            "The archive export plan has already been used.";

        private readonly SazExportTestHooks _hooks;

        internal SazExportOperation()
            : this(new SazExportTestHooks())
        {
        }

        internal SazExportOperation(SazExportTestHooks hooks)
        {
            ArgumentNullException.ThrowIfNull(hooks);
            _hooks = hooks;
        }

        internal const int MaxRows = 250_000;

        internal const long AnalyzeInputCeilingBytes =
            2_147_483_648;

        private const int PlanPageRows = 256;

        internal async Task<OperationResult<SazExportPlan>> PrepareAsync(
            SazExportPlanRequest request,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            context ??= OperationContext.None;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (request?.Source == null ||
                    request.Budgets == null)
                {
                    return PrepareFailure(
                        OperationOutcome.InternalError,
                        SazExportCodes.SourceUnsupported,
                        SourceUnsupportedMessage);
                }

                CaptureTextSource textSource =
                    request.Source.TextSource;
                if (textSource.Semantics ==
                    CaptureTextSemantics.AmbiguousUnmarked)
                {
                    CaptureTextSource bound;
                    try
                    {
                        bound = await textSource.BindAsync(
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (CaptureTextSourceException)
                    {
                        return PrepareFailure(
                            OperationOutcome.InvalidInput,
                            SazExportCodes.SourceUnavailable,
                            SourceUnavailableMessage);
                    }

                    if (request.Acceptance == null ||
                        !request.Acceptance.Source.Matches(bound))
                    {
                        return PrepareFailure(
                            OperationOutcome.InvalidInput,
                            SazExportCodes.AmbiguousExport,
                            AmbiguousMessage);
                    }
                }

                if (request.Source.IsUnsupported)
                {
                    return PrepareFailure(
                        OperationOutcome.InternalError,
                        SazExportCodes.SourceUnsupported,
                        SourceUnsupportedMessage);
                }

                ValidateBudgets(request.Budgets);
                long? watermark =
                    request.Source.GetWatermark(
                        cancellationToken);
                var keys = new List<SazExportKey>();
                long storedBytes = 0;
                long? cursor = null;
                while (watermark.HasValue)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SazExportPage page =
                        request.Source.ReadPageForKind(
                            cursor,
                            watermark.Value,
                            PlanPageRows,
                            request.Budgets,
                            request.Kind,
                            cancellationToken);
                    foreach (SazExportKey key in
                        page?.Keys ??
                        Array.Empty<SazExportKey>())
                    {
                        if (keys.Count >=
                            request.Budgets.MaxRows)
                        {
                            return PrepareFailure(
                                OperationOutcome.InvalidInput,
                                SazExportCodes.TooManyRows,
                                TooManyRowsMessage);
                        }

                        if (key.StoredBytes < 0 ||
                            storedBytes >
                                request.Budgets
                                    .MaxTotalStoredBytes -
                                key.StoredBytes)
                        {
                            return PrepareFailure(
                                OperationOutcome.InvalidInput,
                                SazExportCodes.TooLarge,
                                TooLargeMessage);
                        }

                        storedBytes += key.StoredBytes;
                        keys.Add(key);
                    }

                    context.Progress.Report(
                        new OperationProgress(
                            SazExportCodes.PlanningPhase,
                            request.Budgets.MaxRows == 0
                                ? null
                                : Math.Min(
                                    1,
                                    (double)keys.Count /
                                    request.Budgets.MaxRows)));
                    long? next =
                        page?.LastScannedRowId;
                    if (!next.HasValue ||
                        next == cursor ||
                        next.Value >= watermark.Value)
                    {
                        break;
                    }

                    cursor = next;
                }

                IReadOnlyList<long> sessionIds =
                    AssignSessionIds(keys);
                context.Progress.Report(
                    new OperationProgress(
                        SazExportCodes.PlanningPhase,
                        1));
                return OperationResult<SazExportPlan>.Success(
                    new SazExportPlan(
                        request,
                        keys,
                        sessionIds,
                        storedBytes));
            }
            catch (OperationCanceledException)
            {
                return PrepareFailure(
                    OperationOutcome.Cancelled,
                    SazExportCodes.Cancelled,
                    CancelledMessage);
            }
            catch (OverflowException)
            {
                return PrepareFailure(
                    OperationOutcome.InvalidInput,
                    SazExportCodes.TooLarge,
                    TooLargeMessage);
            }
        }

        internal Task<CaptureArchiveTarget> AdmitTargetAsync(
            string path,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            context ??= OperationContext.None;
            cancellationToken.ThrowIfCancellationRequested();
            if (_hooks.RefuseWorkFolder)
            {
                throw new SazExportException(
                    SazExportCodes.NoPrivateStaging,
                    "The private archive staging folder is unavailable.");
            }

            return Task.FromResult(
                CaptureArchivePublisher.Admit(
                    path,
                    context.WorkingDirectory,
                    _hooks,
                    cancellationToken));
        }

        public Task<OperationResult<SazExportResult>> ExecuteAsync(
            SazExportRequest request,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            context ??= OperationContext.None;
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromResult(
                    FailureResult(
                        OperationOutcome.Cancelled,
                        request?.Plan,
                        SazExportCodes.Cancelled,
                        CancelledMessage));
            }

            if (request?.Plan == null ||
                request.Destination == null)
            {
                return Task.FromResult(
                    FailureResult(
                        OperationOutcome.InternalError,
                        request?.Plan,
                        SazExportCodes.WriteFailed,
                        WriteFailedMessage));
            }

            if (!request.Plan.TryUse())
            {
                return Task.FromResult(
                    FailureResult(
                        OperationOutcome.InternalError,
                        request.Plan,
                        SazExportCodes.PlanUsed,
                        PlanUsedMessage));
            }

            var summary =
                new SummaryBuilder(request.Plan);
            int processed = 0;
            var progressClock =
                Stopwatch.StartNew();
            CaptureArchivePublication publication = null;
            SazSanitizer sanitizer =
                request.Plan.Request.Kind ==
                    SazExportKind.Sanitized
                    ? new SazSanitizer(
                        cancellationToken)
                    : null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Stream destination;
                if (request.Destination is
                    SazExportDestination.ToStream stream)
                {
                    destination = stream.Stream;
                }
                else if (request.Destination is
                    SazExportDestination.ToTarget target)
                {
                    publication =
                        CaptureArchivePublisher
                            .BeginPublication(
                                target.Target,
                                target.ReplaceDecision,
                                cancellationToken);
                    destination =
                        publication.Stream;
                }
                else
                {
                    return Task.FromResult(
                        FailureResult(
                            OperationOutcome.InternalError,
                            request.Plan,
                            SazExportCodes.WriteFailed,
                            WriteFailedMessage));
                }

                context.Progress.Report(
                    new OperationProgress(
                        SazExportCodes.WritingPhase));
                using var counting = new ArchiveCountingStream(
                    destination,
                    request.Plan.Request.Budgets
                        .MaxArchiveBytes);
                using var writer =
                    new SazArchiveWriter(counting);
                SazExportBudgets readBudgets =
                    ReadBudgets(request.Plan);
                long storedBytes = 0;
                for (int index = 0;
                    index < request.Plan.Keys.Count;
                    index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SazExportKey key =
                        request.Plan.Keys[index];
                    StoredRow row =
                        request.Plan.Request.Source.ReadRowForKind(
                            key,
                            readBudgets,
                            request.Plan.Request.Kind,
                            cancellationToken);
                    if (row.BytesRead < 0 ||
                        storedBytes >
                            request.Plan.Request.Budgets
                                .MaxTotalStoredBytes -
                            row.BytesRead)
                    {
                        throw new SazExportLimitException();
                    }

                    storedBytes += row.BytesRead;
                    CaptureSazProjection projected =
                        CaptureSaz.ToSession(
                            row,
                            request.Plan.SessionIds[index],
                            request.Plan.Request.Kind,
                            sanitizer,
                            request.Plan.Request.Budgets);
                    if (projected.Session != null)
                    {
                        writer.Add(
                            projected.Session,
                            cancellationToken);
                        if (sanitizer != null)
                        {
                            sanitizer.Summary.Sessions++;
                        }
                    }

                    summary.Add(
                        key,
                        request.Plan.SessionIds[index],
                        projected);
                    processed++;
                    if (progressClock.ElapsedMilliseconds >=
                        100)
                    {
                        request.Rows?.Report(
                            new(
                                processed,
                                request.Plan.Rows));
                        progressClock.Restart();
                    }
                }

                writer.Complete(cancellationToken);
                request.Rows?.Report(
                    new(
                        processed,
                        request.Plan.Rows));
                SazExportSummary complete =
                    summary.Build(
                        counting.ArchiveLength,
                        sanitizer?.Summary);
                if (publication != null)
                {
                    publication.PrepareCommit();
                    cancellationToken.ThrowIfCancellationRequested();
                    context.Progress.Report(
                        new OperationProgress(
                            SazExportCodes.PublishingPhase));
                    return Task.FromResult(
                        PublicationResult(
                            publication.Commit(),
                            complete));
                }

                destination.Flush();
                return Task.FromResult(
                    new OperationResult<SazExportResult>(
                        OperationOutcome.Succeeded,
                        new SazExportResult(
                            SazExportPublication.Written,
                            SazExportUncertainty.None,
                            null,
                            complete,
                            Array.Empty<SazExportResidue>()),
                        Array.Empty<OperationDiagnostic>()));
            }
            catch (OperationCanceledException)
            {
                request.Rows?.Report(
                    new(
                        processed,
                        request.Plan.Rows));
                return Task.FromResult(
                    FailureResult(
                        OperationOutcome.Cancelled,
                        request.Plan,
                        SazExportCodes.Cancelled,
                        CancelledMessage,
                        summary));
            }
            catch (SazExportLimitException)
            {
                return Task.FromResult(
                    FailureResult(
                        OperationOutcome.InvalidInput,
                        request.Plan,
                        SazExportCodes.TooLarge,
                        TooLargeMessage,
                        summary));
            }
            catch (CaptureSazException exception)
            {
                return Task.FromResult(
                    FailureResult(
                        OperationOutcome.InvalidInput,
                        request.Plan,
                        exception.Code,
                        "An HTTP start line is invalid.",
                        summary));
            }
            catch (SazSanitizeException exception)
            {
                return Task.FromResult(
                    FailureResult(
                        OperationOutcome.InvalidInput,
                        request.Plan,
                        exception.Code,
                        "An HTTP message cannot be sanitized.",
                        summary));
            }
            catch (SazWriteException exception)
            {
                return Task.FromResult(
                    FailureResult(
                        OperationOutcome.InternalError,
                        request.Plan,
                        exception.Code,
                        WriteFailedMessage,
                        summary));
            }
            catch (IOException)
            {
                return Task.FromResult(
                    FailureResult(
                        OperationOutcome.Failed,
                        request.Plan,
                        SazExportCodes.WriteFailed,
                        WriteFailedMessage,
                        summary));
            }
            catch (Exception exception) when (
                !ExceptionUtilities.IsFatal(exception))
            {
                return Task.FromResult(
                    FailureResult(
                        OperationOutcome.InternalError,
                        request.Plan,
                        SazExportCodes.WriteFailed,
                        WriteFailedMessage,
                        summary));
            }
            finally
            {
                publication?.Dispose();
            }
        }

        private static OperationResult<SazExportResult> PublicationResult(
            CaptureArchivePublicationResult publication,
            SazExportSummary summary)
        {
            ArgumentNullException.ThrowIfNull(publication);
            var diagnostics =
                new List<OperationDiagnostic>();
            OperationOutcome outcome;
            if (publication.Residues?.Count > 0)
            {
                outcome = OperationOutcome.CleanupFailed;
                diagnostics.Add(
                    Diagnostic(
                        SazExportCodes.CleanupFailed,
                        "One or more archive publication files could not be removed."));
            }
            else
            {
                outcome = publication.Publication switch
                {
                    SazExportPublication.Created or
                    SazExportPublication.Replaced =>
                        OperationOutcome.Succeeded,
                    SazExportPublication.NotPublished
                        when publication.Code is
                            SazExportCodes.Exists or
                            SazExportCodes.Changed or
                            SazExportCodes.InUse or
                            SazExportCodes.AccessDenied or
                            SazExportCodes.LocationUnverified =>
                        OperationOutcome.Conflict,
                    _ => OperationOutcome.Failed,
                };
            }

            if (publication.Code != null)
            {
                diagnostics.Insert(
                    0,
                    Diagnostic(
                        publication.Code,
                        "The archive was not published."));
            }

            return new(
                outcome,
                new SazExportResult(
                    publication.Publication,
                    publication.Uncertainty,
                    publication.Code,
                    summary,
                    publication.Residues ??
                        Array.Empty<SazExportResidue>()),
                diagnostics);
        }

        private static OperationResult<SazExportResult> FailureResult(
            OperationOutcome outcome,
            SazExportPlan plan,
            string code,
            string message,
            SummaryBuilder builder = null)
        {
            var value = new SazExportResult(
                SazExportPublication.NotPublished,
                SazExportUncertainty.None,
                code,
                builder?.Build(
                    0,
                    plan?.Request.Kind ==
                        SazExportKind.Sanitized
                        ? new SanitizedExportSummary()
                        : null) ??
                    EmptySummary(plan),
                Array.Empty<SazExportResidue>());
            return new(
                outcome,
                value,
                new[] { Diagnostic(code, message) });
        }

        private static SazExportSummary EmptySummary(
            SazExportPlan plan) =>
            new(
                plan?.Rows ?? 0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                plan?.Request.Kind == SazExportKind.Sanitized
                    ? new SanitizedExportSummary()
                    : null);

        private static OperationDiagnostic Diagnostic(
            string code,
            string message) =>
            new(
                code,
                OperationDiagnosticSeverity.Error,
                message);

        private static OperationResult<SazExportPlan> PrepareFailure(
            OperationOutcome outcome,
            string code,
            string message) =>
            OperationResult<SazExportPlan>.Failure(
                outcome,
                Diagnostic(code, message));

        private static void ValidateBudgets(
            SazExportBudgets budgets)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                budgets.MaxRows);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                budgets.MaxTotalStoredBytes);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                budgets.MaxArchiveBytes);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                budgets.MaxHeadReadBytes);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                budgets.MaxHeadBytes);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                budgets.MaxHeadFieldLines);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                budgets.MaxBodyBytes);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                budgets.MaxSchemeBytes);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                budgets.MaxTimeBytes);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                budgets.MaxClientAddressBytes);
        }

        private static IReadOnlyList<long> AssignSessionIds(
            IReadOnlyList<SazExportKey> keys)
        {
            var kept = new bool[keys.Count];
            var used = new HashSet<long>();
            long largest = 0;
            for (int index = 0;
                index < keys.Count;
                index++)
            {
                long? candidate =
                    keys[index].RequestNumber;
                if (candidate is >= 1 and <= int.MaxValue &&
                    used.Add(candidate.Value))
                {
                    kept[index] = true;
                    largest = Math.Max(
                        largest,
                        candidate.Value);
                }
            }

            var ids = new long[keys.Count];
            long next = largest;
            for (int index = 0;
                index < keys.Count;
                index++)
            {
                if (kept[index])
                {
                    ids[index] =
                        keys[index].RequestNumber.Value;
                    continue;
                }

                if (next == int.MaxValue)
                {
                    for (int reset = 0;
                        reset < ids.Length;
                        reset++)
                    {
                        ids[reset] = reset + 1L;
                    }

                    return ids;
                }

                ids[index] = ++next;
            }

            return ids;
        }

        private static SazExportBudgets ReadBudgets(
                    SazExportPlan plan)
                {
                    SazExportBudgets budgets =
                        plan.Request.Budgets;
                    return plan.Request.Kind ==
                        SazExportKind.Sanitized
                        ? budgets with
                        {
                            MaxBodyBytes = Math.Min(
                                budgets.MaxBodyBytes,
                                SazSanitizer.MaximumBodyBytes),
                        }
                        : budgets;
                }

                private sealed class SummaryBuilder
                {
                    private readonly SazExportPlan _plan;
                    private int _sessions;
                    private int _entries;
                    private int _rowsWithoutRequest;
                    private int _rowsOversized;
                    private int _rowsUnreadable;
                    private int _bodiesNotKept;
                    private int _bodiesUndecodable;
                    private int _renumberedSessions;
                    private long _replacedHeadCharacters;
                    private long _droppedHeaderLines;
                    private long _reformattedHeaderLines;
                    private int _absoluteTargets;
                    private int _discardedClientAddresses;
                    private int _adjustedTimes;

                    internal SummaryBuilder(
                        SazExportPlan plan)
                    {
                        _plan = plan;
                    }

                    internal void Add(
                        SazExportKey key,
                        long sessionId,
                        CaptureSazProjection projection)
                    {
                        _bodiesNotKept = checked(
                            _bodiesNotKept +
                            projection.BodiesNotKept);
                        _bodiesUndecodable = checked(
                            _bodiesUndecodable +
                            projection.BodiesUndecodable);
                        _replacedHeadCharacters = checked(
                            _replacedHeadCharacters +
                            projection.ReplacedHeadCharacters);
                        _droppedHeaderLines = checked(
                            _droppedHeaderLines +
                            projection.DroppedHeaderLines);
                        _reformattedHeaderLines = checked(
                            _reformattedHeaderLines +
                            projection.ReformattedHeaderLines);
                        _absoluteTargets = checked(
                            _absoluteTargets +
                            projection.AbsoluteTargets);
                        _discardedClientAddresses = checked(
                            _discardedClientAddresses +
                            projection.DiscardedClientAddresses);
                        _adjustedTimes = checked(
                            _adjustedTimes +
                            projection.AdjustedTimes);
                        switch (projection.Disposition)
                        {
                            case CaptureSazDisposition.NoRequest:
                                _rowsWithoutRequest++;
                                break;

                            case CaptureSazDisposition.Oversized:
                                _rowsOversized++;
                                break;

                            case CaptureSazDisposition.Unreadable:
                                _rowsUnreadable++;
                                break;

                            case CaptureSazDisposition.Session:
                                _sessions++;
                                _entries = checked(
                                    _entries +
                                    (projection.Session.Response == null
                                        ? 2
                                        : 3));
                                if (key.RequestNumber != sessionId)
                                {
                                    _renumberedSessions++;
                                }
                                break;
                        }
                    }

                    internal SazExportSummary Build(
                        long archiveBytes,
                        SanitizedExportSummary sanitized) =>
                        new(
                            _plan.Rows,
                            _sessions,
                            _entries,
                            _rowsWithoutRequest,
                            _rowsOversized,
                            _rowsUnreadable,
                            _bodiesNotKept,
                            _bodiesUndecodable,
                            _renumberedSessions,
                            _replacedHeadCharacters,
                            _droppedHeaderLines,
                            _reformattedHeaderLines,
                            _absoluteTargets,
                            _discardedClientAddresses,
                            _adjustedTimes,
                            archiveBytes,
                            _plan.Request.Kind ==
                                SazExportKind.Sanitized
                                ? sanitized ??
                                    new SanitizedExportSummary()
                                : null);
                }

                private sealed class SazExportLimitException :
                    Exception
                {
                }

                private sealed class ArchiveCountingStream :
                    Stream
                {
                    private readonly Stream _inner;
                    private readonly long _maximumLength;
                    private long _position;
                    private long _length;

                    internal ArchiveCountingStream(
                        Stream inner,
                        long maximumLength)
                    {
                        ArgumentNullException.ThrowIfNull(inner);
                        _inner = inner;
                        _maximumLength = maximumLength;
                        _position = inner.CanSeek
                            ? inner.Position
                            : 0;
                        _length = inner.CanSeek
                            ? inner.Length
                            : _position;
                    }

                    internal long ArchiveLength => _length;

                    public override bool CanRead =>
                        _inner.CanRead;

                    public override bool CanSeek =>
                        _inner.CanSeek;

                    public override bool CanWrite =>
                        _inner.CanWrite;

                    public override long Length => _length;

                    public override long Position
                    {
                        get => _inner.CanSeek
                            ? _inner.Position
                            : _position;
                        set
                        {
                            if (!_inner.CanSeek)
                            {
                                throw new NotSupportedException();
                            }

                            _inner.Position = value;
                            _position = value;
                        }
                    }

                    public override void Flush() =>
                        _inner.Flush();

                    public override int Read(
                        byte[] buffer,
                        int offset,
                        int count)
                    {
                        int read = _inner.Read(
                            buffer,
                            offset,
                            count);
                        _position = checked(
                            _position + read);
                        return read;
                    }

                    public override long Seek(
                        long offset,
                        SeekOrigin origin)
                    {
                        long position =
                            _inner.Seek(offset, origin);
                        _position = position;
                        return position;
                    }

                    public override void SetLength(long value)
                    {
                        if (value > _maximumLength)
                        {
                            throw new SazExportLimitException();
                        }

                        _inner.SetLength(value);
                        _length = value;
                        if (_position > value)
                        {
                            _position = value;
                        }
                    }

                    public override void Write(
                        byte[] buffer,
                        int offset,
                        int count)
                    {
                        long end = EndOfWrite(count);
                        _inner.Write(
                            buffer,
                            offset,
                            count);
                        Written(end);
                    }

                    public override void Write(
                        ReadOnlySpan<byte> buffer)
                    {
                        long end =
                            EndOfWrite(buffer.Length);
                        _inner.Write(buffer);
                        Written(end);
                    }

                    private long EndOfWrite(int count)
                    {
                        long end = checked(
                            Position + count);
                        if (end > _maximumLength)
                        {
                            throw new SazExportLimitException();
                        }

                        return end;
                    }

                    private void Written(long end)
                    {
                        _position = end;
                        _length = Math.Max(
                            _length,
                            end);
                    }
                }

    }
}
