// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using XMAT;

namespace CaptureAnalysisEngine
{
    [Rule]
    public class PollingDetectionRule : BaseRule<PollingDetectionRule>
    {
        public static string PollingSequencesFoundDataKey { get { return Localization.GetLocalizedString("LTA_POLL_CALLS_SEQUENCES"); } }

        public static String DisplayName { get { return Localization.GetLocalizedString("LTA_POLL_CALLS_TITLE"); } }
        public static String Description { get { return Localization.GetLocalizedString("LTA_POLL_CALLS_DESC"); } }

        private struct CallDelta
        {
            internal int FirstIndex;
            internal int SecondIndex;
            internal int PreviousIndex;
            internal int NextIndex;
        }

        private readonly struct DeltaRange
        {
            internal DeltaRange(
                int start,
                int end)
            {
                Start = start;
                End = end;
            }

            internal int Start { get; }

            internal int End { get; }
        }

        private sealed class DeltaGraph
        {
            internal DeltaGraph(
                CallDelta[] deltas,
                Dictionary<uint, DeltaRange> ranges,
                bool hasOrderedIndex)
            {
                Deltas = deltas;
                Ranges = ranges;
                HasOrderedIndex = hasOrderedIndex;
            }

            internal CallDelta[] Deltas { get; }

            internal Dictionary<uint, DeltaRange> Ranges { get; }

            internal bool HasOrderedIndex { get; }
        }

        private struct SequenceDescriptor
        {
            internal int RootDeltaIndex;
            internal int Offset;
            internal int Length;
            internal int DiscoveryOrder;
            internal double AverageDelta;
        }

        private sealed class SequenceWorkspace
        {
            internal SequenceWorkspace(
                SequenceDescriptor[] sequences,
                int sequenceCount,
                int[] calls)
            {
                Sequences = sequences;
                SequenceCount = sequenceCount;
                Calls = calls;
            }

            internal SequenceDescriptor[] Sequences { get; }

            internal int SequenceCount { get; set; }

            internal int[] Calls { get; }
        }

        private sealed class Sequence
        {
            internal Sequence(
                List<ServiceCallItem> calls,
                double averageDelta)
            {
                Calls = calls;
                AverageDelta = averageDelta;
            }

            internal List<ServiceCallItem> Calls { get; }

            internal double AverageDelta { get; }

            internal int Length =>
                Calls.Count;
        }

        private sealed class SequenceDescriptorComparer :
            IComparer<SequenceDescriptor>
        {
            internal static SequenceDescriptorComparer Instance { get; } =
                new();

            public int Compare(
                SequenceDescriptor left,
                SequenceDescriptor right)
            {
                int byLength =
                    left.Length.CompareTo(
                        right.Length);
                return byLength != 0
                    ? byLength
                    : left.DiscoveryOrder.CompareTo(
                        right.DiscoveryOrder);
            }
        }

        public override RuleResult Run(
            RulesEngine engine,
            IEnumerable<ServiceCallItem> items,
            ServiceCallStats stats)
        {
            RuleResult result =
                InitializeResult(
                    DisplayName,
                    Description);
            List<ServiceCallItem> allItems =
                items.ToList();
            if (allItems.Count == 0)
            {
                return result;
            }

            AnalysisWorkBudget budget =
                engine.WorkBudget;
            CancellationToken cancellationToken =
                engine.CancellationToken;
            var groups =
                new Dictionary<
                    (string Uri, RequestBodyIdentity Body),
                    List<ServiceCallItem>>();
            var orderedGroups =
                new List<List<ServiceCallItem>>();
            var bodyIdentities =
                new RequestBodyIdentityResolver();
            foreach (ServiceCallItem item in allItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.IsShoulderTap)
                {
                    continue;
                }
                budget.Charge();
                var key =
                    (item.Uri,
                     bodyIdentities.Resolve(item));
                if (!groups.TryGetValue(
                        key,
                        out List<ServiceCallItem> group))
                {
                    group = new List<ServiceCallItem>();
                    groups.Add(key, group);
                    orderedGroups.Add(group);
                }
                group.Add(item);
            }

            var sequences = new List<Sequence>();
            foreach (List<ServiceCallItem> group in
                orderedGroups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (group.Count <= 2)
                {
                    continue;
                }

                var repeats =
                    group.GetRange(
                        1,
                        group.Count - 1);
                using AnalysisWorkBudget.PollingKeyWorkspace workspace =
                    budget.EnterPollingKey(
                        cancellationToken);
                sequences.AddRange(
                    GenerateSequences(
                        repeats,
                        workspace,
                        budget,
                        cancellationToken));
            }

            var description =
                new StringBuilder();
            foreach (Sequence sequence in
                sequences.OrderByDescending(
                    sequence => sequence.Length))
            {
                cancellationToken.ThrowIfCancellationRequested();
                budget.ChargeRetainedOutput(
                    sequence.Length,
                    1);
                description.Clear();
                description.Append(
                    Localization.GetLocalizedString(
                        "LTA_POLL_CALLS_VIOLATION",
                        sequence.Length,
                        TimeSpan.FromTicks(
                                (long)sequence.AverageDelta)
                            .ToString(
                                @"mm\:ss\.fff")));
                result.AddViolation(
                    ViolationLevel.Warning,
                    description.ToString(),
                    sequence.Calls);
            }

            result.Results.Add(
                PollingSequencesFoundDataKey,
                result.ViolationCount);
            return result;
        }

        private List<Sequence> GenerateSequences(
            List<ServiceCallItem> calls,
            AnalysisWorkBudget.PollingKeyWorkspace retained,
            AnalysisWorkBudget budget,
            CancellationToken cancellationToken)
        {
            AnalysisWorkBudget.WorkCounter counter =
                budget.CreateCounter();
            DeltaGraph graph =
                BuildDeltas(
                    calls,
                    retained,
                    counter,
                    cancellationToken);
            if (graph.Deltas.Length == 0)
            {
                counter.Flush();
                return new List<Sequence>();
            }

            LinkDeltas(
                graph,
                calls,
                m_sameDeltaThresholdPercentage,
                counter,
                cancellationToken);
            SequenceWorkspace sequenceWorkspace =
                BuildSequences(
                    graph,
                    calls,
                    retained,
                    counter,
                    cancellationToken);
            sequenceWorkspace.SequenceCount =
                RemoveSubsets(
                    sequenceWorkspace,
                    calls,
                    retained,
                    counter,
                    cancellationToken);
            List<Sequence> result =
                MaterializeSequences(
                    sequenceWorkspace,
                    calls,
                    budget,
                    counter,
                    cancellationToken);
            counter.Flush();
            return result;
        }

        private DeltaGraph BuildDeltas(
            List<ServiceCallItem> calls,
            AnalysisWorkBudget.PollingKeyWorkspace retained,
            AnalysisWorkBudget.WorkCounter counter,
            CancellationToken cancellationToken)
        {
            retained.ReserveKeyIndex(
                calls.Count);
            var pairCounts =
                new int[calls.Count];
            long totalPairs = 0;
            bool timesOrdered =
                TimesAreOrdered(calls);
            if (!float.IsNaN(m_maxFrequencyMs) &&
                m_maxFrequencyMs >= 0)
            {
                if (timesOrdered)
                {
                    var windowEnd = 1;
                    for (var first = 0;
                        first < calls.Count;
                        first++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (windowEnd <= first)
                        {
                            windowEnd = first + 1;
                        }
                        while (windowEnd < calls.Count &&
                            IsWithinMaxFrequency(
                                calls[first],
                                calls[windowEnd]))
                        {
                            if ((windowEnd & 1023) == 0)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                            }
                            counter.Charge();
                            windowEnd++;
                        }
                        int count =
                            windowEnd - first - 1;
                        pairCounts[first] = count;
                        totalPairs = checked(
                            totalPairs + count);
                    }
                }
                else
                {
                    for (var first = 0;
                        first < calls.Count;
                        first++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        for (int second = first + 1;
                            second < calls.Count;
                            second++)
                        {
                            if ((second & 1023) == 0)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                            }
                            counter.Charge();
                            if (!IsWithinMaxFrequency(
                                    calls[first],
                                    calls[second]))
                            {
                                continue;
                            }
                            pairCounts[first]++;
                            totalPairs = checked(
                                totalPairs + 1);
                            retained.EnsurePairCount(
                                totalPairs);
                        }
                    }
                }
            }

            counter.Flush();
            int pairCount =
                retained.ReservePairs(
                    totalPairs);
            var deltas =
                new CallDelta[pairCount];
            var seenIds =
                new HashSet<uint>();
            var hasUniqueIds = true;
            foreach (ServiceCallItem call in calls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                counter.Charge();
                if (!seenIds.Add(call.Id))
                {
                    hasUniqueIds = false;
                }
            }
            bool hasOrderedIndex =
                timesOrdered &&
                hasUniqueIds;

            var ranges =
                new Dictionary<uint, DeltaRange>();
            var offset = 0;
            for (var first = 0;
                first < calls.Count;
                first++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int start = offset;
                if (timesOrdered)
                {
                    int end =
                        first + 1 + pairCounts[first];
                    for (int second = first + 1;
                        second < end;
                        second++)
                    {
                        AddDelta(
                            deltas,
                            ref offset,
                            first,
                            second,
                            cancellationToken);
                    }
                }
                else
                {
                    for (int second = first + 1;
                        second < calls.Count;
                        second++)
                    {
                        if ((second & 1023) == 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                        if (!IsWithinMaxFrequency(
                                calls[first],
                                calls[second]))
                        {
                            continue;
                        }
                        AddDelta(
                            deltas,
                            ref offset,
                            first,
                            second,
                            cancellationToken);
                    }
                }
                if (hasOrderedIndex &&
                    offset > start)
                {
                    ranges.Add(
                        calls[first].Id,
                        new DeltaRange(
                            start,
                            offset));
                }
            }
            if (!hasOrderedIndex)
            {
                retained.ReserveLegacyIndexEntries(
                    pairCount);
            }

            counter.Flush();
            return new DeltaGraph(
                deltas,
                ranges,
                hasOrderedIndex);
        }

        private static void AddDelta(
            CallDelta[] deltas,
            ref int offset,
            int first,
            int second,
            CancellationToken cancellationToken)
        {
            if ((offset & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            deltas[offset++] =
                new CallDelta
                {
                    FirstIndex = first,
                    SecondIndex = second,
                    PreviousIndex = -1,
                    NextIndex = -1,
                };
        }

        private static bool TimesAreOrdered(
            List<ServiceCallItem> calls)
        {
            for (var index = 1;
                index < calls.Count;
                index++)
            {
                if (calls[index - 1].ReqTimeUTC >
                    calls[index].ReqTimeUTC)
                {
                    return false;
                }
            }
            return true;
        }

        private bool IsWithinMaxFrequency(
            ServiceCallItem first,
            ServiceCallItem second)
        {
            ulong delta =
                second.ReqTimeUTC -
                first.ReqTimeUTC;
            float deltaAsMilliseconds =
                delta /
                (ulong)TimeSpan.TicksPerMillisecond;
            return deltaAsMilliseconds <=
                m_maxFrequencyMs;
        }

        private static void LinkDeltas(
            DeltaGraph graph,
            List<ServiceCallItem> calls,
            double tolerancePercentage,
            AnalysisWorkBudget.WorkCounter counter,
            CancellationToken cancellationToken)
        {
            double lowerPercent =
                1 - tolerancePercentage;
            double upperPercent =
                1 + tolerancePercentage;

            if (!graph.HasOrderedIndex)
            {
                LinkDeltasWithLegacyScan(
                    graph,
                    calls,
                    lowerPercent,
                    upperPercent,
                    counter,
                    cancellationToken);
                return;
            }

            // The successor set contains exactly the still-unclaimed potentials in their original order. Removing
            // each match while enumerating the tolerance interval therefore claims the same deltas and leaves the
            // source linked to the same last match as the original nested scan.
            for (var sourceIndex = 0;
                sourceIndex < graph.Deltas.Length;
                sourceIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                counter.Charge();
                ref CallDelta source =
                    ref graph.Deltas[sourceIndex];
                if (!graph.Ranges.TryGetValue(
                        calls[source.SecondIndex].Id,
                        out DeltaRange range))
                {
                    continue;
                }

                ulong sourceDelta =
                    DeltaTicks(
                        source,
                        calls);
                double lower =
                    sourceDelta *
                    lowerPercent;
                double upper =
                    sourceDelta *
                    upperPercent;
                int start =
                    LowerBound(
                        graph.Deltas,
                        range,
                        calls,
                        lower,
                        counter,
                        cancellationToken);
                int end =
                    UpperBound(
                        graph.Deltas,
                        range,
                        calls,
                        upper,
                        counter,
                        cancellationToken);
                int potentialIndex =
                    FindNextUnclaimed(
                        graph.Deltas,
                        start,
                        range.End);
                while (potentialIndex < end)
                {
                    if ((potentialIndex & 1023) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    counter.Charge();
                    source.NextIndex =
                        potentialIndex;
                    int next =
                        FindNextUnclaimed(
                            graph.Deltas,
                            potentialIndex + 1,
                            range.End);
                    graph.Deltas[potentialIndex]
                        .PreviousIndex =
                        next;
                    potentialIndex = next;
                }
            }
            counter.Flush();
        }

        // Duplicate ids or unsorted timestamps make the ordered range index invalid. This metered scan keeps C7's
        // original delta order, unclaimed check and last-match assignment exactly.
        private static void LinkDeltasWithLegacyScan(
            DeltaGraph graph,
            List<ServiceCallItem> calls,
            double lowerPercent,
            double upperPercent,
            AnalysisWorkBudget.WorkCounter counter,
            CancellationToken cancellationToken)
        {
            var grouped =
                new Dictionary<uint, List<int>>();
            for (var index = 0;
                index < graph.Deltas.Length;
                index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                uint id =
                    calls[
                        graph.Deltas[index]
                            .FirstIndex].Id;
                if (!grouped.TryGetValue(
                        id,
                        out List<int> group))
                {
                    group = new List<int>();
                    grouped.Add(id, group);
                }
                group.Add(index);
            }

            for (var sourceIndex = 0;
                sourceIndex < graph.Deltas.Length;
                sourceIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ref CallDelta source =
                    ref graph.Deltas[sourceIndex];
                if (!grouped.TryGetValue(
                        calls[source.SecondIndex].Id,
                        out List<int> potentials))
                {
                    continue;
                }
                ulong sourceDelta =
                    DeltaTicks(
                        source,
                        calls);
                double lower =
                    sourceDelta *
                    lowerPercent;
                double upper =
                    sourceDelta *
                    upperPercent;
                foreach (int potentialIndex in
                    potentials)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    counter.Charge();
                    ref CallDelta potential =
                        ref graph.Deltas[potentialIndex];
                    if (potential.PreviousIndex < 0)
                    {
                        ulong potentialDelta =
                            DeltaTicks(
                                potential,
                                calls);
                        if (potentialDelta >= lower &&
                            potentialDelta <= upper)
                        {
                            source.NextIndex =
                                potentialIndex;
                            potential.PreviousIndex =
                                sourceIndex;
                        }
                    }
                }
            }
            counter.Flush();
        }

        private SequenceWorkspace BuildSequences(
            DeltaGraph graph,
            List<ServiceCallItem> calls,
            AnalysisWorkBudget.PollingKeyWorkspace retained,
            AnalysisWorkBudget.WorkCounter counter,
            CancellationToken cancellationToken)
        {
            var rootCount = 0;
            for (var index = 0;
                index < graph.Deltas.Length;
                index++)
            {
                if ((index & 1023) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                counter.Charge();
                if (graph.Deltas[index]
                    .PreviousIndex < 0)
                {
                    rootCount++;
                }
            }

            retained.ReserveSequenceDescriptors(
                rootCount);
            var sequences =
                new SequenceDescriptor[rootCount];
            var sequenceCount = 0;
            long totalCalls = 0;
            for (var root = 0;
                root < graph.Deltas.Length;
                root++)
            {
                if (graph.Deltas[root]
                    .PreviousIndex >= 0)
                {
                    continue;
                }
                cancellationToken.ThrowIfCancellationRequested();
                int deltaCount = 0;
                double totalDelta = 0;
                for (int current = root;
                    current >= 0;
                    current = graph.Deltas[current]
                        .NextIndex)
                {
                    if ((deltaCount & 1023) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    counter.Charge();
                    totalDelta +=
                        DeltaTicks(
                            graph.Deltas[current],
                            calls);
                    deltaCount++;
                }
                int length =
                    deltaCount + 1;
                if (length <
                    m_minSequenceSize)
                {
                    continue;
                }
                totalCalls = checked(
                    totalCalls + length);
                sequences[sequenceCount++] =
                    new SequenceDescriptor
                    {
                        RootDeltaIndex = root,
                        Length = length,
                        DiscoveryOrder = root,
                        AverageDelta =
                            totalDelta /
                            deltaCount,
                    };
            }

            retained.ReserveSequenceMemberships(
                totalCalls);
            var sequenceCalls =
                new int[checked((int)totalCalls)];
            var offset = 0;
            for (var sequenceIndex = 0;
                sequenceIndex < sequenceCount;
                sequenceIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SequenceDescriptor descriptor =
                    sequences[sequenceIndex];
                descriptor.Offset = offset;
                int current =
                    descriptor.RootDeltaIndex;
                while (current >= 0)
                {
                    if ((offset & 1023) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    counter.Charge();
                    CallDelta delta =
                        graph.Deltas[current];
                    sequenceCalls[offset++] =
                        delta.FirstIndex;
                    if (delta.NextIndex < 0)
                    {
                        sequenceCalls[offset++] =
                            delta.SecondIndex;
                    }
                    current = delta.NextIndex;
                }
                sequences[sequenceIndex] =
                    descriptor;
            }

            Array.Sort(
                sequences,
                0,
                sequenceCount,
                SequenceDescriptorComparer.Instance);
            counter.Flush();
            return new SequenceWorkspace(
                sequences,
                sequenceCount,
                sequenceCalls);
        }

        private static int RemoveSubsets(
            SequenceWorkspace workspace,
            List<ServiceCallItem> calls,
            AnalysisWorkBudget.PollingKeyWorkspace retained,
            AnalysisWorkBudget.WorkCounter counter,
            CancellationToken cancellationToken)
        {
            if (workspace.SequenceCount <= 1)
            {
                return workspace.SequenceCount;
            }

            var idsIncrease = true;
            for (var index = 1;
                index < calls.Count;
                index++)
            {
                if (calls[index - 1].Id >=
                    calls[index].Id)
                {
                    idsIncrease = false;
                    break;
                }
            }

            int count = idsIncrease
                ? RemoveSubsetsWithIndex(
                    workspace,
                    calls.Count,
                    retained,
                    counter,
                    cancellationToken)
                : RemoveSubsetsLegacy(
                    workspace,
                    calls,
                    retained,
                    counter,
                    cancellationToken);
            counter.Flush();
            return count;
        }

        private static int RemoveSubsetsWithIndex(
            SequenceWorkspace workspace,
            int callCount,
            AnalysisWorkBudget.PollingKeyWorkspace retained,
            AnalysisWorkBudget.WorkCounter counter,
            CancellationToken cancellationToken)
        {
            retained.ReserveSubsetIndex(
                callCount,
                workspace.SequenceCount);
            var postingCounts =
                new int[callCount];
            long totalPostings = 0;
            // With strictly increasing call ids, the legacy Contains result is exactly subsequence membership in the
            // longer sequence without its final call. The inverted index selects only possible supersets; the
            // two-pointer check below remains exact and preserves the legacy final-call exclusion.
            for (var sequenceIndex = 0;
                sequenceIndex < workspace.SequenceCount;
                sequenceIndex++)
            {
                SequenceDescriptor sequence =
                    workspace.Sequences[sequenceIndex];
                int end =
                    sequence.Offset +
                    sequence.Length -
                    1;
                for (int call = sequence.Offset;
                    call < end;
                    call++)
                {
                    int callIndex =
                        workspace.Calls[call];
                    postingCounts[callIndex]++;
                    totalPostings++;
                }
            }

            retained.ReserveSubsetIndexEntries(
                totalPostings);
            var postings =
                new int[callCount][];
            for (var call = 0;
                call < callCount;
                call++)
            {
                postings[call] =
                    postingCounts[call] == 0
                        ? Array.Empty<int>()
                        : new int[
                            postingCounts[call]];
                postingCounts[call] = 0;
            }
            for (var sequenceIndex = 0;
                sequenceIndex < workspace.SequenceCount;
                sequenceIndex++)
            {
                SequenceDescriptor sequence =
                    workspace.Sequences[sequenceIndex];
                int end =
                    sequence.Offset +
                    sequence.Length -
                    1;
                for (int call = sequence.Offset;
                    call < end;
                    call++)
                {
                    int callIndex =
                        workspace.Calls[call];
                    postings[callIndex][
                        postingCounts[callIndex]++] =
                        sequenceIndex;
                }
            }

            var contained =
                new bool[workspace.SequenceCount];
            for (var sequenceIndex = 0;
                sequenceIndex < workspace.SequenceCount;
                sequenceIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SequenceDescriptor sequence =
                    workspace.Sequences[sequenceIndex];
                int anchor = -1;
                int smallestPosting = int.MaxValue;
                int sequenceEnd =
                    sequence.Offset +
                    sequence.Length;
                for (int call = sequence.Offset;
                    call < sequenceEnd;
                    call++)
                {
                    counter.Charge();
                    int callIndex =
                        workspace.Calls[call];
                    int postingLength =
                        postings[callIndex].Length;
                    if (postingLength <
                        smallestPosting)
                    {
                        anchor = callIndex;
                        smallestPosting =
                            postingLength;
                        if (smallestPosting == 0)
                        {
                            break;
                        }
                    }
                }
                if (smallestPosting == 0)
                {
                    continue;
                }

                int[] candidates =
                    postings[anchor];
                for (int candidateIndex =
                        candidates.Length - 1;
                    candidateIndex >= 0;
                    candidateIndex--)
                {
                    if ((candidateIndex & 1023) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    int longerIndex =
                        candidates[candidateIndex];
                    SequenceDescriptor longer =
                        workspace.Sequences[longerIndex];
                    if (longer.Length <=
                        sequence.Length)
                    {
                        break;
                    }
                    counter.Charge();
                    if (ContainsMonotonic(
                            longer,
                            sequence,
                            workspace.Calls,
                            counter,
                            cancellationToken))
                    {
                        contained[sequenceIndex] =
                            true;
                        break;
                    }
                }
            }

            return CompactUncontained(
                workspace.Sequences,
                workspace.SequenceCount,
                contained);
        }

        private static bool ContainsMonotonic(
            SequenceDescriptor longer,
            SequenceDescriptor shorter,
            int[] calls,
            AnalysisWorkBudget.WorkCounter counter,
            CancellationToken cancellationToken)
        {
            int longerIndex =
                longer.Offset;
            int longerEnd =
                longer.Offset +
                longer.Length -
                1;
            int shorterIndex =
                shorter.Offset;
            int shorterEnd =
                shorter.Offset +
                shorter.Length;
            long comparisons = 0;
            while (shorterIndex < shorterEnd)
            {
                if ((comparisons & 1023) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                int wanted =
                    calls[shorterIndex];
                while (longerIndex < longerEnd &&
                    calls[longerIndex] < wanted)
                {
                    comparisons++;
                    longerIndex++;
                }
                comparisons++;
                if (longerIndex == longerEnd ||
                    calls[longerIndex] != wanted)
                {
                    counter.Charge(
                        Math.Max(1, comparisons));
                    return false;
                }
                longerIndex++;
                shorterIndex++;
            }
            counter.Charge(
                Math.Max(1, comparisons));
            return true;
        }

        private static int RemoveSubsetsLegacy(
            SequenceWorkspace workspace,
            List<ServiceCallItem> calls,
            AnalysisWorkBudget.PollingKeyWorkspace retained,
            AnalysisWorkBudget.WorkCounter counter,
            CancellationToken cancellationToken)
        {
            retained.ReserveSubsetFlags(
                workspace.SequenceCount);
            var contained =
                new bool[workspace.SequenceCount];
            for (var first = 0;
                first < workspace.SequenceCount;
                first++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int second =
                        workspace.SequenceCount - 1;
                    second > first;
                    second--)
                {
                    counter.Charge();
                    if (workspace.Sequences[second]
                        .Length ==
                        workspace.Sequences[first]
                            .Length)
                    {
                        break;
                    }
                    if (ContainsLegacy(
                            workspace.Sequences[second],
                            workspace.Sequences[first],
                            workspace.Calls,
                            calls,
                            counter,
                            cancellationToken))
                    {
                        contained[first] = true;
                        break;
                    }
                }
            }

            return CompactUncontained(
                workspace.Sequences,
                workspace.SequenceCount,
                contained);
        }

        private static bool ContainsLegacy(
            SequenceDescriptor longer,
            SequenceDescriptor shorter,
            int[] sequenceCalls,
            List<ServiceCallItem> calls,
            AnalysisWorkBudget.WorkCounter counter,
            CancellationToken cancellationToken)
        {
            if (shorter.Length >=
                longer.Length)
            {
                return false;
            }

            for (int mine = 0, other = 0;
                mine <
                    longer.Length -
                    shorter.Length +
                    other;
                mine++)
            {
                if ((mine & 1023) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                counter.Charge();
                int mineIndex =
                    sequenceCalls[
                        longer.Offset +
                        mine];
                int otherIndex =
                    sequenceCalls[
                        shorter.Offset +
                        other];
                if (ReferenceEquals(
                        calls[mineIndex],
                        calls[otherIndex]))
                {
                    if (++other ==
                        shorter.Length)
                    {
                        return true;
                    }
                }
                else if (calls[mineIndex].Id >
                    calls[otherIndex].Id)
                {
                    return false;
                }
            }
            return false;
        }

        private static int CompactUncontained(
            SequenceDescriptor[] sequences,
            int sequenceCount,
            bool[] contained)
        {
            var resultCount = 0;
            for (var index = 0;
                index < sequenceCount;
                index++)
            {
                if (!contained[index])
                {
                    sequences[resultCount++] =
                        sequences[index];
                }
            }
            return resultCount;
        }

        private static List<Sequence> MaterializeSequences(
            SequenceWorkspace workspace,
            List<ServiceCallItem> calls,
            AnalysisWorkBudget budget,
            AnalysisWorkBudget.WorkCounter counter,
            CancellationToken cancellationToken)
        {
            var result =
                new List<Sequence>(
                    workspace.SequenceCount);
            for (var sequenceIndex = 0;
                sequenceIndex < workspace.SequenceCount;
                sequenceIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SequenceDescriptor descriptor =
                    workspace.Sequences[sequenceIndex];
                budget.ChargeRetainedOutput(
                    descriptor.Length,
                    1);
                var sequenceCalls =
                    new List<ServiceCallItem>(
                        descriptor.Length);
                int end =
                    descriptor.Offset +
                    descriptor.Length;
                for (int call = descriptor.Offset;
                    call < end;
                    call++)
                {
                    counter.Charge();
                    sequenceCalls.Add(
                        calls[
                            workspace.Calls[call]]);
                }
                result.Add(
                    new Sequence(
                        sequenceCalls,
                        descriptor.AverageDelta));
            }
            return result;
        }

        private static int LowerBound(
            CallDelta[] deltas,
            DeltaRange range,
            List<ServiceCallItem> calls,
            double value,
            AnalysisWorkBudget.WorkCounter counter,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int low = range.Start;
            int high = range.End;
            var comparisons = 0;
            while (low < high)
            {
                comparisons++;
                int middle =
                    low + (high - low) / 2;
                if (DeltaTicks(
                        deltas[middle],
                        calls) <
                    value)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            counter.Charge(
                Math.Max(1, comparisons));
            return low;
        }

        private static int UpperBound(
            CallDelta[] deltas,
            DeltaRange range,
            List<ServiceCallItem> calls,
            double value,
            AnalysisWorkBudget.WorkCounter counter,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int low = range.Start;
            int high = range.End;
            var comparisons = 0;
            while (low < high)
            {
                comparisons++;
                int middle =
                    low + (high - low) / 2;
                if (DeltaTicks(
                        deltas[middle],
                        calls) <=
                    value)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            counter.Charge(
                Math.Max(1, comparisons));
            return low;
        }

        private static int FindNextUnclaimed(
            CallDelta[] deltas,
            int index,
            int end)
        {
            if (index >= end)
            {
                return end;
            }
            int root = index;
            while (root < end &&
                deltas[root].PreviousIndex >= 0)
            {
                root = deltas[root]
                    .PreviousIndex;
            }
            while (index < end &&
                deltas[index].PreviousIndex >= 0)
            {
                int following =
                    deltas[index]
                        .PreviousIndex;
                deltas[index]
                    .PreviousIndex =
                    root;
                index = following;
            }
            return root;
        }

        private static ulong DeltaTicks(
            CallDelta delta,
            List<ServiceCallItem> calls) =>
            calls[delta.SecondIndex].ReqTimeUTC -
            calls[delta.FirstIndex].ReqTimeUTC;

        public override void DeserializeJson(JsonElement json)
        {
            Utils.SafeAssign(json, @"SameDeltaThresholdPercent", ref m_sameDeltaThresholdPercentage);
            Utils.SafeAssign(json, @"MaxFrequencyMs", ref m_maxFrequencyMs);
            Utils.SafeAssign(json, @"MinSequenceSize", ref m_minSequenceSize);
        }

        private static class Constants
        {
            internal const float PollingDetectionSameDeltaThresholdMs = .01f;
            internal const float PollingDetectionMaxFrequencyMs = 60 * 1000 * 60;
            internal const int PollingDetectionMinSequenceSize = 5;
        }

        private float m_sameDeltaThresholdPercentage = Constants.PollingDetectionSameDeltaThresholdMs;
        private float m_maxFrequencyMs = Constants.PollingDetectionMaxFrequencyMs;
        private int m_minSequenceSize = Constants.PollingDetectionMinSequenceSize;
    }
}
