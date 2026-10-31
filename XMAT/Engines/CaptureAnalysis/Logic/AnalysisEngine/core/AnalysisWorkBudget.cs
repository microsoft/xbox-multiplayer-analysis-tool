// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;

namespace CaptureAnalysisEngine
{
    internal sealed class AnalysisWorkLimitException : Exception
    {
        internal AnalysisWorkLimitException()
            : base("Analysis exceeded its safe work limit.")
        {
        }
    }

    internal sealed class AnalysisWorkBudget
    {
        // Work units count pair construction, indexed comparisons/linking, sequence memberships and retained output.
        // Live items are 16-byte pair-equivalents, so auxiliary arrays and surviving output share the same ceiling.
        internal const long DefaultLimit =
            16_000_000_000;
        internal const int DefaultMaxLiveItems =
            24_000_000;
        internal const int DefaultMaxRetainedPollingPairs =
            DefaultMaxLiveItems;
        internal static readonly TimeSpan
            DefaultFallbackMatchAllowance =
                TimeSpan.FromSeconds(5);

        private const int PairBytes = 16;
        private const int PairWorkUnits = 2;
        private const int IntBytes = 4;
        private const int ReferenceBytes = 8;
        private const int ArrayHeaderBytes = 24;
        private const int SequenceDescriptorBytes = 24;
        private const int OutputContainerBytes = 96;
        private const int KeyIndexBytesPerCall = 96;

        private readonly long _limit;
        private readonly int _maxLiveItems;
        private readonly long _maxLiveBytes;
        private readonly long _fallbackMatchAllowance;
        private readonly Func<long> _fallbackTimestamp;
        private readonly Func<Regex, string, bool> _fallbackMatcher;
        private readonly object _liveLock =
            new();
        private readonly object _fallbackMatchLock =
            new();
        private readonly object _pollingWorkspaceLock =
            new();
        private long _used;
        private long _liveBytes;
        private long _peakLiveBytes;
        private long _livePairs;
        private long _peakLivePairs;
        private long _fallbackMatchElapsed;
        private int _exceeded;
        private bool _pollingWorkspaceInUse;

        internal AnalysisWorkBudget(
            long limit = DefaultLimit,
            int maxLiveItems = DefaultMaxLiveItems,
            TimeSpan? fallbackMatchAllowance = null,
            Func<long> fallbackTimestamp = null,
            long fallbackTimestampFrequency = 0,
            Func<Regex, string, bool> fallbackMatcher = null)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                limit);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                maxLiveItems);
            _limit = limit;
            _maxLiveItems = maxLiveItems;
            _maxLiveBytes = checked(
                (long)maxLiveItems *
                PairBytes);
            TimeSpan allowance =
                fallbackMatchAllowance ??
                DefaultFallbackMatchAllowance;
            if (allowance <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(fallbackMatchAllowance));
            }
            if (fallbackTimestamp == null)
            {
                _fallbackTimestamp =
                    Stopwatch.GetTimestamp;
                fallbackTimestampFrequency =
                    Stopwatch.Frequency;
            }
            else
            {
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                    fallbackTimestampFrequency);
                _fallbackTimestamp =
                    fallbackTimestamp;
            }
            _fallbackMatchAllowance = checked(
                (long)Math.Ceiling(
                    allowance.Ticks *
                    (double)fallbackTimestampFrequency /
                    TimeSpan.TicksPerSecond));
            _fallbackMatcher =
                fallbackMatcher ??
                MatchWithRegex;
        }

        internal long Used =>
            Interlocked.Read(ref _used);

        internal long LiveRetainedItems
        {
            get
            {
                lock (_liveLock)
                {
                    return BytesToItems(
                        _liveBytes);
                }
            }
        }

        internal long PeakLiveItems
        {
            get
            {
                lock (_liveLock)
                {
                    return BytesToItems(
                        _peakLiveBytes);
                }
            }
        }

        internal long PeakLivePairs
        {
            get
            {
                lock (_liveLock)
                {
                    return _peakLivePairs;
                }
            }
        }

        internal long FallbackMatchElapsed
        {
            get
            {
                lock (_fallbackMatchLock)
                {
                    return _fallbackMatchElapsed;
                }
            }
        }

        internal void Charge(long units = 1)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                units);
            ThrowIfExceeded();
            long used = Interlocked.Add(
                ref _used,
                units);
            if (used > _limit)
            {
                Reject();
            }
        }

        internal void ThrowIfLimitExceeded()
        {
            ThrowIfExceeded();
        }

        internal bool MatchFallback(
            Regex regex,
            string input,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(regex);
            ArgumentNullException.ThrowIfNull(input);
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfExceeded();
            long started = _fallbackTimestamp();
            try
            {
                bool result =
                    _fallbackMatcher(
                        regex,
                        input);
                cancellationToken.ThrowIfCancellationRequested();
                ChargeFallbackElapsed(
                    started,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }
            catch (RegexMatchTimeoutException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ChargeFallbackElapsed(
                    started,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                Reject();
                return false;
            }
        }

        internal void ChargeRetainedOutput(
            int items,
            int containers = 0)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(
                items);
            ArgumentOutOfRangeException.ThrowIfNegative(
                containers);
            long units = checked(
                (long)items +
                containers);
            if (units > 0)
            {
                Charge(units);
            }
            long bytes = checked(
                ((long)items *
                ReferenceBytes) +
                ((long)containers *
                OutputContainerBytes));
            ReserveLive(
                bytes,
                0);
        }

        internal WorkCounter CreateCounter() =>
            new(this);

        internal PollingKeyWorkspace EnterPollingKey(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfExceeded();
            lock (_pollingWorkspaceLock)
            {
                while (_pollingWorkspaceInUse)
                {
                    cancellationToken
                        .ThrowIfCancellationRequested();
                    _ = Monitor.Wait(
                        _pollingWorkspaceLock,
                        50);
                    ThrowIfExceeded();
                }
                _pollingWorkspaceInUse = true;
            }
            return new PollingKeyWorkspace(this);
        }

        private void ChargeFallbackElapsed(
            long started,
            CancellationToken cancellationToken)
        {
            long finished =
                _fallbackTimestamp();
            long elapsed = finished >= started
                ? finished - started
                : 0;
            bool exceeded;
            lock (_fallbackMatchLock)
            {
                _fallbackMatchElapsed = checked(
                    _fallbackMatchElapsed +
                    elapsed);
                exceeded =
                    _fallbackMatchElapsed >
                    _fallbackMatchAllowance;
            }
            if (exceeded)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Reject();
            }
        }

        private static bool MatchWithRegex(
            Regex regex,
            string input) =>
            regex.IsMatch(input);

        private void ReserveLive(
            long bytes,
            long pairs)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(
                bytes);
            ArgumentOutOfRangeException.ThrowIfNegative(
                pairs);
            if (bytes == 0 && pairs == 0)
            {
                return;
            }
            lock (_liveLock)
            {
                long liveBytes = checked(
                    _liveBytes + bytes);
                long livePairs = checked(
                    _livePairs + pairs);
                if (liveBytes > _maxLiveBytes ||
                    livePairs > _maxLiveItems)
                {
                    Reject();
                }
                _liveBytes = liveBytes;
                _livePairs = livePairs;
                _peakLiveBytes = Math.Max(
                    _peakLiveBytes,
                    liveBytes);
                _peakLivePairs = Math.Max(
                    _peakLivePairs,
                    livePairs);
            }
        }

        private void ReleaseLive(
            long bytes,
            long pairs)
        {
            lock (_liveLock)
            {
                _liveBytes -= bytes;
                _livePairs -= pairs;
            }
        }

        private void ReleasePollingKey()
        {
            lock (_pollingWorkspaceLock)
            {
                _pollingWorkspaceInUse = false;
                Monitor.Pulse(
                    _pollingWorkspaceLock);
            }
        }

        private void ThrowIfExceeded()
        {
            if (Volatile.Read(ref _exceeded) != 0)
            {
                throw new AnalysisWorkLimitException();
            }
        }

        private void Reject()
        {
            Volatile.Write(ref _exceeded, 1);
            throw new AnalysisWorkLimitException();
        }

        private static long BytesToItems(
            long bytes) =>
            checked(
                (bytes + PairBytes - 1) /
                PairBytes);

        internal sealed class WorkCounter
        {
            private const int FlushThreshold = 4096;
            private readonly AnalysisWorkBudget _budget;
            private long _pending;

            internal WorkCounter(
                AnalysisWorkBudget budget)
            {
                _budget = budget;
            }

            internal void Charge(long units = 1)
            {
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                    units);
                _pending = checked(
                    _pending + units);
                if (_pending >= FlushThreshold)
                {
                    Flush();
                }
            }

            internal void Flush()
            {
                if (_pending == 0)
                {
                    return;
                }
                long pending = _pending;
                _pending = 0;
                _budget.Charge(pending);
            }

            internal void FlushPreservingFailure()
            {
                try
                {
                    Flush();
                }
                catch (AnalysisWorkLimitException)
                {
                    // The charge and exceeded state were recorded; preserve the exception already in flight.
                }
            }
        }

        internal sealed class PollingKeyWorkspace :
            IDisposable
        {
            private AnalysisWorkBudget _budget;
            private long _retainedBytes;
            private long _retainedPairs;
            private int _pairCount;

            internal PollingKeyWorkspace(
                AnalysisWorkBudget budget)
            {
                _budget = budget;
            }

            internal void ReserveKeyIndex(int calls)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(
                    calls);
                ReserveBytes(
                    checked(
                        ((long)calls *
                        KeyIndexBytesPerCall) +
                        (3L * ArrayHeaderBytes)));
            }

            internal int ReservePairs(long pairs)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(
                    pairs);
                EnsurePairCount(pairs);
                AnalysisWorkBudget budget =
                    Budget();
                int pairCount =
                    checked((int)pairs);
                Reserve(
                    checked(
                        (pairs *
                        PairBytes) +
                        ArrayHeaderBytes),
                    pairs);
                if (pairs > 0)
                {
                    budget.Charge(
                        checked(
                            pairs *
                            PairWorkUnits));
                }
                _pairCount = pairCount;
                return pairCount;
            }

            internal void EnsurePairCount(long pairs)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(
                    pairs);
                AnalysisWorkBudget budget =
                    Budget();
                if (pairs > budget._maxLiveItems)
                {
                    budget.Reject();
                }
            }

            internal void ReserveLegacyIndexEntries(
                int entries)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(
                    entries);
                ReserveBytes(
                    checked(
                        ((long)entries *
                        IntBytes) +
                        ArrayHeaderBytes));
            }

            internal void ReserveSequenceDescriptors(
                int descriptors)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(
                    descriptors);
                ReserveBytes(
                    checked(
                        ((long)descriptors *
                        SequenceDescriptorBytes) +
                        ArrayHeaderBytes));
            }

            internal void ReserveSequenceMemberships(
                long memberships)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(
                    memberships);
                RequireBoundedByPairs(
                    memberships);
                if (memberships > 0)
                {
                    Budget().Charge(memberships);
                }
                ReserveBytes(
                    checked(
                        (memberships *
                        IntBytes) +
                        ArrayHeaderBytes));
            }

            internal void ReserveSubsetIndex(
                int calls,
                int sequences)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(
                    calls);
                ArgumentOutOfRangeException.ThrowIfNegative(
                    sequences);
                long bytes = checked(
                    ((long)calls *
                    (IntBytes +
                    ReferenceBytes +
                    ArrayHeaderBytes)) +
                    sequences +
                    (4L * ArrayHeaderBytes));
                ReserveBytes(bytes);
            }

            internal void ReserveSubsetIndexEntries(
                long entries)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(
                    entries);
                RequireBoundedByPairs(entries);
                if (entries > 0)
                {
                    Budget().Charge(entries);
                }
                ReserveBytes(
                    checked(
                        (entries *
                        IntBytes) +
                        ArrayHeaderBytes));
            }

            internal void ReserveSubsetFlags(
                int sequences)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(
                    sequences);
                ReserveBytes(
                    checked(
                        (long)sequences +
                        ArrayHeaderBytes));
            }

            private void RequireBoundedByPairs(
                long items)
            {
                long maximum =
                    checked(
                        ((long)Math.Max(
                            1,
                            _pairCount) *
                        2) +
                        1);
                if (items > maximum)
                {
                    Budget().Reject();
                }
            }

            private AnalysisWorkBudget Budget()
            {
                AnalysisWorkBudget budget =
                    _budget;
                ObjectDisposedException.ThrowIf(
                    budget == null,
                    this);
                return budget;
            }

            private void ReserveBytes(long bytes) =>
                Reserve(bytes, 0);

            private void Reserve(
                long bytes,
                long pairs)
            {
                AnalysisWorkBudget budget =
                    Budget();
                budget.ReserveLive(
                    bytes,
                    pairs);
                _retainedBytes = checked(
                    _retainedBytes + bytes);
                _retainedPairs = checked(
                    _retainedPairs + pairs);
            }

            public void Dispose()
            {
                AnalysisWorkBudget budget =
                    Interlocked.Exchange(
                        ref _budget,
                        null);
                if (budget == null)
                {
                    return;
                }
                budget.ReleaseLive(
                    _retainedBytes,
                    _retainedPairs);
                budget.ReleasePollingKey();
            }
        }
    }
}
