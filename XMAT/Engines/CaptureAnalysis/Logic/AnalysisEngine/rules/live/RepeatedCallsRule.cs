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
    public class RepeatedCallsRule : BaseRule<RepeatedCallsRule>
    {
        public static String TotalCallsDataKey { get { return Localization.GetLocalizedString("LTA_REPEATED_CALLS_DATA_TOTAL"); } }
        public static String DuplicatesDataKey { get { return Localization.GetLocalizedString("LTA_REPEATED_CALLS_DATA_DUPS"); } }
        public static String PercentageDataKey { get { return Localization.GetLocalizedString("LTA_REPEATED_CALLS_DATA_PERCENT"); } }

        public static String DisplayName { get { return Localization.GetLocalizedString("LTA_REPEATED_CALLS_TITLE"); } }
        public static String Description { get { return Localization.GetLocalizedString("LTA_REPEATED_CALLS_DESC"); } }

        public UInt32 m_minAllowedRepeatIntervalMs = 2000;

        public Int32 m_totalCallsChecked = 0;
        public Int32 m_numberOfRepeats = 0;

        public RepeatedCallsRule() : base()
        {
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
            var eligible =
                new List<ServiceCallItem>();
            var timeIndex =
                new Dictionary<
                    (string Uri, RequestBodyIdentity Body),
                    List<ServiceCallItem>>();
            var unorderedKeys =
                new HashSet<
                    (string Uri, RequestBodyIdentity Body)>();
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
                eligible.Add(item);
                var key =
                    (item.Uri,
                     bodyIdentities.Resolve(item));
                if (!timeIndex.TryGetValue(
                        key,
                        out List<ServiceCallItem> calls))
                {
                    calls = new List<ServiceCallItem>();
                    timeIndex.Add(key, calls);
                }
                else if (calls[^1].ReqTimeUTC >
                    item.ReqTimeUTC)
                {
                    _ = unorderedKeys.Add(key);
                }
                calls.Add(item);
            }

            var repeatedMembership =
                new HashSet<ServiceCallItem>();
            var description =
                new StringBuilder();
            var repeatedCount = 0;
            m_totalCallsChecked =
                allItems.Count;
            foreach (ServiceCallItem item in eligible)
            {
                cancellationToken.ThrowIfCancellationRequested();
                budget.Charge();
                if (repeatedMembership.Contains(item))
                {
                    continue;
                }

                var key =
                    (item.Uri,
                     bodyIdentities.Resolve(item));
                List<ServiceCallItem> calls =
                    timeIndex[key];
                int start = 0;
                int end = 0;
                int matchingCalls;
                if (unorderedKeys.Contains(key))
                {
                    matchingCalls =
                        CountWindowMatches(
                            calls,
                            item.ReqTimeUTC,
                            m_minAllowedRepeatIntervalMs,
                            budget,
                            cancellationToken);
                }
                else
                {
                    start = FirstAfter(
                        calls,
                        item.ReqTimeUTC,
                        budget,
                        cancellationToken);
                    end = FirstOutsideWindow(
                        calls,
                        start,
                        item.ReqTimeUTC,
                        m_minAllowedRepeatIntervalMs,
                        budget,
                        cancellationToken);
                    matchingCalls =
                        end - start;
                }
                if (matchingCalls == 0)
                {
                    continue;
                }
                budget.ChargeRetainedOutput(
                    matchingCalls + 1,
                    1);
                var repeatedCalls =
                    new List<ServiceCallItem>(
                        matchingCalls + 1);
                repeatedCalls.Add(item);
                if (unorderedKeys.Contains(key))
                {
                    AddWindowMatches(
                        calls,
                        item.ReqTimeUTC,
                        m_minAllowedRepeatIntervalMs,
                        repeatedCalls,
                        budget,
                        cancellationToken);
                }
                else
                {
                    for (var index = start;
                        index < end;
                        index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        repeatedCalls.Add(calls[index]);
                    }
                }

                if (repeatedCalls.Count > 1)
                {
                    description.Clear();
                    description.AppendFormat(
                        Localization.GetLocalizedString(
                            "LTA_REPEATED_CALLS_VIOLATION",
                            repeatedCalls.Count));
                    budget.ChargeRetainedOutput(
                        repeatedCalls.Count,
                        1);
                    result.AddViolation(
                        ViolationLevel.Warning,
                        description.ToString(),
                        repeatedCalls);
                    repeatedCount = checked(
                        repeatedCount +
                        repeatedCalls.Count);
                    foreach (ServiceCallItem repeated in
                        repeatedCalls)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        budget.Charge();
                        _ = repeatedMembership.Add(
                            repeated);
                    }
                }
            }

            m_numberOfRepeats =
                repeatedCount;
            result.Results.Add(
                TotalCallsDataKey,
                m_totalCallsChecked);
            result.Results.Add(
                DuplicatesDataKey,
                m_numberOfRepeats);
            result.Results.Add(
                PercentageDataKey,
                ((double)m_numberOfRepeats) /
                m_totalCallsChecked);
            return result;
        }

        private static int CountWindowMatches(
            List<ServiceCallItem> calls,
            ulong time,
            uint milliseconds,
            AnalysisWorkBudget budget,
            CancellationToken cancellationToken)
        {
            ulong windowTicks =
                milliseconds *
                (ulong)TimeSpan.TicksPerMillisecond;
            var count = 0;
            AnalysisWorkBudget.WorkCounter counter =
                budget.CreateCounter();
            try
            {
                foreach (ServiceCallItem call in calls)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    counter.Charge();
                    if (call.ReqTimeUTC > time &&
                        call.ReqTimeUTC - time <
                        windowTicks)
                    {
                        count++;
                    }
                }
            }
            catch
            {
                counter.FlushPreservingFailure();
                throw;
            }
            counter.Flush();
            return count;
        }

        private static void AddWindowMatches(
            List<ServiceCallItem> calls,
            ulong time,
            uint milliseconds,
            List<ServiceCallItem> destination,
            AnalysisWorkBudget budget,
            CancellationToken cancellationToken)
        {
            ulong windowTicks =
                milliseconds *
                (ulong)TimeSpan.TicksPerMillisecond;
            AnalysisWorkBudget.WorkCounter counter =
                budget.CreateCounter();
            try
            {
                foreach (ServiceCallItem call in calls)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    counter.Charge();
                    if (call.ReqTimeUTC > time &&
                        call.ReqTimeUTC - time <
                        windowTicks)
                    {
                        destination.Add(call);
                    }
                }
            }
            catch
            {
                counter.FlushPreservingFailure();
                throw;
            }
            counter.Flush();
        }

        private static int FirstAfter(
            List<ServiceCallItem> calls,
            ulong time,
            AnalysisWorkBudget budget,
            CancellationToken cancellationToken)
        {
            var low = 0;
            int high = calls.Count;
            var comparisons = 0;
            while (low < high)
            {
                cancellationToken.ThrowIfCancellationRequested();
                comparisons++;
                int middle =
                    low + (high - low) / 2;
                if (calls[middle].ReqTimeUTC <=
                    time)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            budget.Charge(
                Math.Max(1, comparisons));
            return low;
        }

        private static int FirstOutsideWindow(
            List<ServiceCallItem> calls,
            int start,
            ulong time,
            uint milliseconds,
            AnalysisWorkBudget budget,
            CancellationToken cancellationToken)
        {
            ulong windowTicks =
                milliseconds *
                (ulong)TimeSpan.TicksPerMillisecond;
            int low = start;
            int high = calls.Count;
            var comparisons = 0;
            while (low < high)
            {
                cancellationToken.ThrowIfCancellationRequested();
                comparisons++;
                int middle =
                    low + (high - low) / 2;
                ulong delta =
                    calls[middle].ReqTimeUTC -
                    time;
                if (delta < windowTicks)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            budget.Charge(
                Math.Max(1, comparisons));
            return low;
        }

        public override void DeserializeJson(JsonElement json)
        {
            Utils.SafeAssign(json, @"MinAllowedRepeatIntervalMs", ref m_minAllowedRepeatIntervalMs);
        }
    }
}
