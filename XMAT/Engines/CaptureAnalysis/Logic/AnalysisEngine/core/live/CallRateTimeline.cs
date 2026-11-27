// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;

namespace CaptureAnalysisEngine
{
    // What one endpoint's call frequency rule counted, for the call rate charts in the analysis results: when the
    // endpoint's calls began and ended and, for each set of limits, the calls it applies to and what the analyzer found.
    // It travels with the rule's result and report data but is never written to the report files.
    internal sealed class CallRateTimeline
    {
        internal CallRateTimeline(ulong firstCall, ulong lastCall, int calls)
        {
            FirstCall = firstCall;
            LastCall = lastCall;
            Calls = calls;
        }

        // Request times (ServiceCallItem.ReqTimeUTC, FILETIME ticks) of the endpoint's first and last call.
        internal ulong FirstCall { get; }
        internal ulong LastCall { get; }

        // Every call to the endpoint, whether or not a set of limits applies to it.
        internal int Calls { get; }

        internal List<CallRateLimits> Limits { get; } = new();

        internal static CallRateTimeline For(IEnumerable<ServiceCallItem> items)
        {
            ulong first = ulong.MaxValue;
            ulong last = ulong.MinValue;
            int calls = 0;
            foreach (ServiceCallItem item in items)
            {
                first = Math.Min(first, item.ReqTimeUTC);
                last = Math.Max(last, item.ReqTimeUTC);
                calls++;
            }

            return calls == 0 ? new CallRateTimeline(0, 0, 0) : new CallRateTimeline(first, last, calls);
        }
    }

    // One set of an endpoint's call frequency limits.
    internal sealed class CallRateLimits
    {
        internal CallRateLimits(
            string description,
            IEnumerable<ServiceCallItem> applicableCalls,
            CallRateWindow burst,
            CallRateWindow sustained)
        {
            Description = string.IsNullOrEmpty(description) ? null : description;
            // The calls that the analyzer counts against the limits: shoulder taps never count.
            CallTimes = applicableCalls
                .Where(call => !call.IsShoulderTap)
                .Select(call => call.ReqTimeUTC)
                .Order()
                .ToArray();
            Burst = burst;
            Sustained = sustained;
        }

        // The description the rules give these limits, or null when they give none.
        internal string Description { get; }

        // Request times of the calls the limits count, in order.
        internal IReadOnlyList<ulong> CallTimes { get; }

        internal CallRateWindow Burst { get; }
        internal CallRateWindow Sustained { get; }
    }

    // A window of a set of limits: no more than Limit calls in PeriodSeconds. Violations is how many times the analyzer
    // found the calls over it, and Level the highest level it gave them (Warning when there are none).
    internal sealed record CallRateWindow(uint PeriodSeconds, uint Limit, int Violations, ViolationLevel Level);
}
