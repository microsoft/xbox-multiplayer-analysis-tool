// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;

namespace XMAT.XboxLiveCaptureAnalysis.ReportModels.PerEndpointReport
{
    // Samples First to Last, all over the limit.
    internal readonly record struct CallRateStretch(int First, int Last);

    // A limit's calls in its window, measured every Step seconds from the capture's start. Sample k is the number of
    // calls in the window that ends k * Step seconds after the start, or more if the window held more when a call
    // arrived since the sample before. A call stays in the window for the window's length in whole milliseconds, as
    // the analyzer counts it, so every group of calls that the analyzer finds over the limit shows as a sample over it.
    internal sealed class CallRateSeries
    {
        // Samples are a second apart unless the capture is longer than this many seconds.
        internal const int MaxSamples = 86_400;

        private CallRateSeries(int[] counts, int step, uint windowSeconds, uint limit)
        {
            Counts = counts;
            Step = step;
            WindowSeconds = windowSeconds;
            Limit = limit;
            Peak = counts.Max();

            var stretches = new List<CallRateStretch>();
            int first = -1;
            for (int sample = 0; sample < counts.Length; sample++)
            {
                if (counts[sample] > limit)
                {
                    first = first < 0 ? sample : first;
                }
                else if (first >= 0)
                {
                    stretches.Add(new CallRateStretch(first, sample - 1));
                    first = -1;
                }
            }

            if (first >= 0)
            {
                stretches.Add(new CallRateStretch(first, counts.Length - 1));
            }

            OverLimit = stretches;
        }

        public IReadOnlyList<int> Counts { get; }
        public int Step { get; }
        public uint WindowSeconds { get; }
        public uint Limit { get; }
        public int Peak { get; }
        public IReadOnlyList<CallRateStretch> OverLimit { get; }

        public bool IsOverLimit(int sample) => Counts[sample] > Limit;

        // Seconds between samples for a capture SECONDS long.
        internal static int StepFor(long seconds) => (int)Math.Max(1, (seconds + MaxSamples - 1) / MaxSamples);

        // Samples 0 to INTERVALS of the calls at TIMES (request times in FILETIME ticks, in order), STEP seconds apart
        // from START.
        internal static CallRateSeries Measure(
            IReadOnlyList<ulong> times,
            ulong start,
            int intervals,
            int step,
            uint windowSeconds,
            uint limit)
        {
            long stepTicks = step * TimeSpan.TicksPerSecond;
            // A call at time c is in the window ending at x while (x - c) in whole milliseconds is at most its length.
            long reach = ((long)windowSeconds * 1000 + 1) * TimeSpan.TicksPerMillisecond;
            var counts = new int[intervals + 1];
            int oldest = 0;
            int next = 0;

            int InWindow(long end)
            {
                while (oldest < next && (long)times[oldest] <= end - reach)
                {
                    oldest++;
                }

                return next - oldest;
            }

            for (int sample = 0; sample <= intervals; sample++)
            {
                long end = (long)start + sample * stepTicks;
                int most = 0;
                while (next < times.Count && (long)times[next] <= end)
                {
                    long arrival = (long)times[next];
                    while (next < times.Count && (long)times[next] == arrival)
                    {
                        next++;
                    }

                    most = Math.Max(most, InWindow(arrival));
                }

                counts[sample] = Math.Max(most, InWindow(end));
            }

            return new CallRateSeries(counts, step, windowSeconds, limit);
        }
    }
}
