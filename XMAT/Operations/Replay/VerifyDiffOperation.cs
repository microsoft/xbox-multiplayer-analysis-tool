// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XMAT.Operations.Inspection;

namespace XMAT.Operations.Replay
{
    // Explains one verification (VerifyDiff): it compares the captured response and the replay's with ReplayVerifier, so the
    // verdict is verify's own, then checks where each decoded side ends and shows how the compared bytes differ, as text,
    // JSON values or bytes, within VerifyDiffLimits. The preview's decoder only proves where content ends: what the detail
    // shows comes only from the bytes that the verdict compared. At most MaxConcurrentComparisons run at a time, on the
    // thread pool, the comparison included, so parallel sends never hold more decoded pairs than that. It keeps nothing,
    // logs nothing and does no I/O.
    internal sealed class VerifyDiffOperation : IOperation<VerifyDiffRequest, VerifyDiff>
    {
        internal const int MaxConcurrentComparisons = 2;

        internal const string CancelledCode = "verify.diff.cancelled";

        private static readonly SemaphoreSlim Workers = new(MaxConcurrentComparisons, MaxConcurrentComparisons);

        public async Task<OperationResult<VerifyDiff>> ExecuteAsync(
            VerifyDiffRequest request, OperationContext context, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            try
            {
                return OperationResult<VerifyDiff>.Success(await CompareAsync(request, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return OperationResult<VerifyDiff>.Failure(
                    OperationOutcome.Cancelled,
                    new OperationDiagnostic(CancelledCode, OperationDiagnosticSeverity.Info, "The comparison was cancelled."));
            }
        }

        // The verdict and its detail, once a worker is free; throws OperationCanceledException when cancelled.
        internal static async Task<VerifyDiff> CompareAsync(VerifyDiffRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            await Workers.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Task.Run: the work leaves the caller's thread even when the wait completed synchronously on it.
                return await Task.Run(() => Compare(request, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Workers.Release();
            }
        }

        // The verdict and its detail, on the calling thread and without the gate. A failure of the comparison propagates as
        // ReplayVerifier.Compare's does; a failure while the detail is built leaves it Unavailable (verify.diff.internal)
        // with the verdict unchanged. Fatal exceptions and cancellation propagate.
        internal static VerifyDiff Compare(VerifyDiffRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            ReplayComparison comparison = ReplayVerifier.CompareSides(
                request.Original, request.Actual, request.MaxDecodedBytes, cancellationToken);
            return Explain(comparison, (request.Limits ?? VerifyDiffLimits.Default).Clamped(), cancellationToken);
        }

        // The detail of a comparison, measured as diff.json. hasher replaces the line and member-name hash, for tests.
        internal static VerifyDiff Explain(
            ReplayComparison comparison,
            VerifyDiffLimits limits,
            CancellationToken cancellationToken,
            VerifyDiffHasher hasher = null)
        {
            ArgumentNullException.ThrowIfNull(comparison);
            ArgumentNullException.ThrowIfNull(limits);
            VerifyDiff detail;
            try
            {
                detail = new Builder(comparison, limits, hasher, cancellationToken).Build();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                detail = Unavailable(comparison, VerifyDiffLimitation.Internal);
            }

            return Measure(detail, limits.MaxSerializedBytes);
        }

        // The detail with its size as diff.json. A detail that does not fit maxBytes, or that cannot be written, keeps
        // everything but its body (Unavailable); that metadata always fits the least maxBytes (MinSerializedBytes), and it is
        // measured once, never retried. Measuring keeps none of the bytes.
        private static VerifyDiff Measure(VerifyDiff detail, int maxBytes)
        {
            int size;
            string reason = VerifyDiffLimitation.SerializedLimit;
            try
            {
                size = VerifyDiffJson.Measure(detail, maxBytes);
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex) && ex is not OperationCanceledException)
            {
                size = -1;
                reason = VerifyDiffLimitation.Internal;
            }

            if (size >= 0)
            {
                return detail with { SerializedBytes = size };
            }

            VerifyDiff fallback = detail with
            {
                State = VerifyDiffState.Unavailable,
                Limitations = With(detail.Limitations, reason),
                Body = null,
            };
            int metadata = VerifyDiffJson.Measure(fallback, maxBytes);
            if (metadata < 0)
            {
                throw new InvalidOperationException("A verification detail without its body is larger than its limit.");
            }

            return fallback with { SerializedBytes = metadata };
        }

        // A detail without body differences: the sides as compared, a Decoded side's end not checked.
        private static VerifyDiff Unavailable(ReplayComparison comparison, string reason)
        {
            var limitations = new List<string> { reason };
            VerifyDiffSide Side(VerifyBody body, string unproven)
            {
                if (body == null)
                {
                    return null;
                }

                bool decoded = body.Basis == VerifyBodyBasis.Decoded;
                if (decoded)
                {
                    limitations.Add(unproven);
                }

                return new VerifyDiffSide(
                    body.Basis,
                    body.Framing,
                    decoded ? VerifyDecodeEnd.Unproven : VerifyDecodeEnd.NotApplicable,
                    body.Compared.Length,
                    0,
                    null);
            }

            VerifyDiffSide original = Side(comparison.Original, VerifyDiffLimitation.OriginalEndUnproven);
            VerifyDiffSide actual = Side(comparison.Actual, VerifyDiffLimitation.ActualEndUnproven);
            return new VerifyDiff(
                VerifyDiffState.Unavailable,
                limitations.AsReadOnly(),
                comparison.Verification,
                comparison.OriginalStatus,
                comparison.ActualStatus,
                original,
                actual,
                null,
                0);
        }

        private static IReadOnlyList<string> With(IReadOnlyList<string> codes, string code) =>
            codes.Contains(code) ? codes : codes.Append(code).ToList().AsReadOnly();

        private sealed class Builder
        {
            private readonly ReplayComparison _comparison;
            private readonly VerifyDiffBudget _budget;
            private readonly VerifyDiffHasher _hasher;

            internal Builder(
                ReplayComparison comparison, VerifyDiffLimits limits, VerifyDiffHasher hasher, CancellationToken cancellationToken)
            {
                _comparison = comparison;
                _budget = new VerifyDiffBudget(limits, cancellationToken);
                _hasher = hasher;
            }

            internal VerifyDiff Build()
            {
                VerifyBody original = _comparison.Original;
                VerifyBody actual = _comparison.Actual;
                VerifyDecodeEnd originalEnd = End(original, VerifyDiffLimitation.OriginalIncomplete, VerifyDiffLimitation.OriginalEndUnproven, BodyFraming.Advisory);
                VerifyDecodeEnd actualEnd = End(actual, VerifyDiffLimitation.ActualIncomplete, VerifyDiffLimitation.ActualEndUnproven, BodyFraming.Decoded);
                var sides = new Examined();
                VerifyBodyDiff body =
                    _comparison.Verification.Differences.Contains(ReplayVerifier.BodyDifference, StringComparer.Ordinal)
                        ? Body(Whole(originalEnd) && Whole(actualEnd), sides)
                        : new VerifyBodyDiff(true, null, null, null, null);
                IReadOnlyList<string> limitations = _budget.Limitations.ToList().AsReadOnly();
                return new VerifyDiff(
                    limitations.Count == 0 ? VerifyDiffState.Complete : VerifyDiffState.Limited,
                    limitations,
                    _comparison.Verification,
                    _comparison.OriginalStatus,
                    _comparison.ActualStatus,
                    new VerifyDiffSide(
                        original.Basis, original.Framing, originalEnd, original.Compared.Length, sides.Original, sides.OriginalCharset),
                    new VerifyDiffSide(
                        actual.Basis, actual.Framing, actualEnd, actual.Compared.Length, sides.Actual, sides.ActualCharset),
                    body,
                    0);
            }

            private static bool Whole(VerifyDecodeEnd end) => end is VerifyDecodeEnd.NotApplicable or VerifyDecodeEnd.Complete;

            private static bool IsContent(VerifyBodyBasis basis) => basis is VerifyBodyBasis.Plain or VerifyBodyBasis.Decoded;

            // Where a Decoded side's content ends, as the body preview's decoder checks its stored body: Complete only when
            // that decoder ends it there and decoded as many bytes as verify compared.
            private VerifyDecodeEnd End(VerifyBody side, string incomplete, string unproven, BodyFraming framing)
            {
                if (side.Basis != VerifyBodyBasis.Decoded)
                {
                    return VerifyDecodeEnd.NotApplicable;
                }

                BodyDecodeInspection inspection = BodyPreviewOperation.InspectDecode(
                    new BodyPreviewRequest(
                        side.Stored,
                        side.ContentType,
                        side.ContentEncoding,
                        side.TransferEncoding,
                        framing,
                        Parts: BodyPreviewParts.None),
                    _budget.Work);
                if (inspection.Notices.Contains(BodyPreviewNotice.WorkLimit, StringComparer.Ordinal))
                {
                    _budget.Limit(VerifyDiffLimitation.WorkLimit);
                }

                if (inspection.End == BodyDecodeEnd.Complete && inspection.DecodedBytes == side.Compared.Length)
                {
                    return VerifyDecodeEnd.Complete;
                }

                if (inspection.End == BodyDecodeEnd.Incomplete)
                {
                    _budget.Limit(incomplete);
                    return VerifyDecodeEnd.Incomplete;
                }

                _budget.Limit(unproven);
                return VerifyDecodeEnd.Unproven;
            }

            // The body differences: text when both sides' bases are content and their examined bytes read as text, bytes
            // otherwise; and JSON values when a side looks like JSON. Each side's ExaminedBytes is how far into its bytes
            // the text or the byte view actually read.
            private VerifyBodyDiff Body(bool endsKnown, Examined sides)
            {
                VerifyBody original = _comparison.Original;
                VerifyBody actual = _comparison.Actual;
                int most = _budget.Limits.MaxExaminedBytes;
                ReadOnlyMemory<byte> originalBytes = original.Compared[..Math.Min(original.Compared.Length, most)];
                ReadOnlyMemory<byte> actualBytes = actual.Compared[..Math.Min(actual.Compared.Length, most)];
                bool originalAll = originalBytes.Length == original.Compared.Length;
                bool actualAll = actualBytes.Length == actual.Compared.Length;
                if (!originalAll || !actualAll)
                {
                    _budget.Limit(VerifyDiffLimitation.ExaminedLimit);
                }

                bool looksLikeJson = LooksLikeJson(original, originalBytes.Span) || LooksLikeJson(actual, actualBytes.Span);
                VerifyDiffTextSide originalText = null;
                VerifyDiffTextSide actualText = null;
                if (IsContent(original.Basis) && IsContent(actual.Basis))
                {
                    originalText = VerifyDiffTextSide.TryRead(
                        original, originalBytes.Span, originalAll, _budget, _hasher, out long originalRead);
                    sides.Original = originalRead;
                    if (originalText != null)
                    {
                        actualText = VerifyDiffTextSide.TryRead(
                            actual, actualBytes.Span, actualAll, _budget, _hasher, out long actualRead);
                        sides.Actual = actualRead;
                    }
                }

                if (originalText == null || actualText == null)
                {
                    VerifyByteDiff bytes = VerifyDiffBytes.Diff(
                        originalBytes.Span,
                        original.Compared.Length,
                        actualBytes.Span,
                        actual.Compared.Length,
                        _budget,
                        out long originalScanned,
                        out long actualScanned);
                    sides.Original = Math.Max(sides.Original, originalScanned);
                    sides.Actual = Math.Max(sides.Actual, actualScanned);
                    return new VerifyBodyDiff(false, null, bytes, null, looksLikeJson ? VerifyJsonUnavailable.NotWhole : null);
                }

                sides.OriginalCharset = originalText.Charset;
                sides.ActualCharset = actualText.Charset;
                VerifyTextDiff text = VerifyDiffLines.Diff(originalText, actualText, _budget);
                if (!looksLikeJson)
                {
                    return new VerifyBodyDiff(false, text, null, null, null);
                }

                VerifyJsonDiff json = null;
                string unavailable;
                if (!originalAll || !actualAll || !endsKnown)
                {
                    unavailable = VerifyJsonUnavailable.NotWhole;
                }
                else if (!IsJsonCharset(originalText.Charset) || !IsJsonCharset(actualText.Charset))
                {
                    unavailable = VerifyJsonUnavailable.Charset;
                }
                else
                {
                    json = VerifyDiffJsonValues.Compare(
                        originalBytes[originalText.Preamble..],
                        actualBytes[actualText.Preamble..],
                        originalText.Preamble != actualText.Preamble,
                        _budget,
                        _hasher,
                        out unavailable);
                }

                return new VerifyBodyDiff(false, text, null, json, unavailable);
            }

            // The JSON views read only UTF-8 and US-ASCII (BodyTextFormat.JsonCharset).
            private static bool IsJsonCharset(string charset) => charset is "utf-8" or "us-ascii";

            // Whether a side's media type says JSON, or its examined bytes start like a JSON object or array.
            private static bool LooksLikeJson(VerifyBody side, ReadOnlySpan<byte> examined)
            {
                BodyTextFormat format = BodyTextRules.Read(side.ContentType, BodyPreviewLimits.Default.MaxHeaderChars, examined);
                return format.JsonType || BodyPreviewJson.LooksLikeJson(examined[format.Preamble..]);
            }

            // How far into each side's bytes the body differences read, and the charset each was read as text with.
            private sealed class Examined
            {
                internal long Original { get; set; }

                internal long Actual { get; set; }

                internal string OriginalCharset { get; set; }

                internal string ActualCharset { get; set; }
            }
        }
    }
}
