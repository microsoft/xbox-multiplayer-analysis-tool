// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using XMAT.DataAbstractionLayer;

namespace XMAT.Operations.Capture
{
    internal enum SazExportKind
    {
        Plain,
        Sanitized,
    }

    internal readonly record struct SazExportKey(
        long RowId,
        long? RequestNumber,
        long StoredBytes);

    internal sealed record SazExportBudgets(
        int MaxRows,
        long MaxTotalStoredBytes,
        long MaxArchiveBytes,
        int MaxHeadReadBytes,
        int MaxHeadBytes,
        int MaxHeadFieldLines,
        long MaxBodyBytes,
        int MaxSchemeBytes,
        int MaxTimeBytes,
        int MaxClientAddressBytes)
    {
        internal static SazExportBudgets Default { get; } = new(
            250_000,
            4_294_967_296,
            4_294_967_296,
            1_048_576,
            1_048_576,
            16_384,
            67_108_864,
            16,
            64,
            16_384);
    }

    internal sealed record SazExportPage(
        IReadOnlyList<SazExportKey> Keys,
        long? LastScannedRowId);

    internal sealed class StoredTextAcceptance
    {
        private StoredTextAcceptance(CaptureTextSource source)
        {
            Source = source;
        }

        internal static StoredTextAcceptance For(
            CaptureTextSource boundSource)
        {
            ArgumentNullException.ThrowIfNull(boundSource);
            if (boundSource.Sha256 == null)
            {
                throw new ArgumentException(
                    "The capture text source must be bound.",
                    nameof(boundSource));
            }

            return new StoredTextAcceptance(boundSource);
        }

        internal CaptureTextSource Source { get; }
    }

    internal sealed record SazExportPlanRequest(
        SazExportSource Source,
        SazExportKind Kind,
        StoredTextAcceptance Acceptance,
        SazExportBudgets Budgets);

    internal sealed class SazExportPlan
    {
        private int _used;

        internal SazExportPlan(
            SazExportPlanRequest request,
            IReadOnlyList<SazExportKey> keys,
            IReadOnlyList<long> sessionIds,
            long storedBytes)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(keys);
            ArgumentNullException.ThrowIfNull(sessionIds);
            if (keys.Count != sessionIds.Count)
            {
                throw new ArgumentException(
                    "Every planned row needs one session id.",
                    nameof(sessionIds));
            }

            Request = request;
            Keys = keys;
            SessionIds = sessionIds;
            Rows = keys.Count;
            StoredBytes = storedBytes;
        }

        internal int Rows { get; }

        internal long StoredBytes { get; }

        internal SazExportPlanRequest Request { get; }

        internal IReadOnlyList<SazExportKey> Keys { get; }

        internal IReadOnlyList<long> SessionIds { get; }

        internal bool TryUse() =>
            Interlocked.Exchange(ref _used, 1) == 0;
    }

    internal abstract record SazExportDestination
    {
        internal sealed record ToStream(Stream Stream) :
            SazExportDestination;

        internal sealed record ToTarget(
            CaptureArchiveTarget Target,
            CaptureArchiveReplaceDecision ReplaceDecision = null) :
            SazExportDestination;
    }

    internal readonly record struct SazExportProgress(
        int Processed,
        int Total);

    internal sealed record SazExportRequest(
        SazExportPlan Plan,
        SazExportDestination Destination,
        IProgress<SazExportProgress> Rows);

    internal sealed record SazExportSummary(
        int RowsPlanned,
        int Sessions,
        int Entries,
        int RowsWithoutRequest,
        int RowsOversized,
        int RowsUnreadable,
        int BodiesNotKept,
        int BodiesUndecodable,
        int RenumberedSessions,
        long ReplacedHeadCharacters,
        long DroppedHeaderLines,
        long ReformattedHeaderLines,
        int AbsoluteTargets,
        int DiscardedClientAddresses,
        int AdjustedTimes,
        long ArchiveBytes,
        SanitizedExportSummary Sanitized);

    internal sealed record SazExportResult(
        SazExportPublication Publication,
        SazExportUncertainty Uncertainty,
        string Code,
        SazExportSummary Summary,
        IReadOnlyList<SazExportResidue> Residues);

    internal sealed class SazExportException : Exception
    {
        internal SazExportException(string code, string message)
            : base(message)
        {
            ArgumentException.ThrowIfNullOrEmpty(code);
            Code = code;
        }

        internal string Code { get; }
    }
}
