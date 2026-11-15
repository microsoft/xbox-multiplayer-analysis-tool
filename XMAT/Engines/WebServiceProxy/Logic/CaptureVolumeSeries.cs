// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;

namespace XMAT.WebServiceCapture
{
    // Requests per second for the timeline's Volume view. The timeline layout adds and removes each request it shows,
    // so the counts stay current without rescanning the capture. A second is a whole second of RequestTime.
    internal sealed class CaptureVolumeSeries
    {
        // While a capture runs, the chart shows this many completed seconds, one point each.
        internal const int LiveSeconds = 60;

        // Otherwise the whole capture fits the width: a point per second while points are at least this far apart,
        // or else the shortest step that keeps them so.
        internal const double MinimumPointSpacing = 3;

        // The lowest top value, so that a quiet capture doesn't look like a spike.
        internal const double MinimumTop = 5;

        private const long SecondsPerDay = 86400;

        // Steps of a stopped capture, and intervals between time labels, in seconds; longer spans use whole days.
        private static readonly long[] Steps =
            { 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200, SecondsPerDay };

        // Top values up to 100, so that the line uses most of the height; above 100, these times a power of ten.
        private static readonly double[] Tops = { MinimumTop, 10, 20, 30, 40, 50, 60, 80, 100 };
        private static readonly double[] TopFactors = { 1, 1.5, 2, 3, 4, 5, 6, 8, 10 };

        private readonly Dictionary<long, int> _counts = new();
        private long _first;
        private long _last;
        private int _peak;
        private bool _boundsStale;
        private bool _peakStale;

        internal int Total { get; private set; }

        // The first and last seconds with a request, when Total is above zero.
        internal long FirstSecond
        {
            get
            {
                RefreshBounds();
                return _first;
            }
        }

        internal long LastSecond
        {
            get
            {
                RefreshBounds();
                return _last;
            }
        }

        // The most requests in one second.
        internal int Peak
        {
            get
            {
                if (_peakStale)
                {
                    _peakStale = false;
                    _peak = 0;
                    foreach (var count in _counts.Values)
                    {
                        _peak = Math.Max(_peak, count);
                    }
                }
                return _peak;
            }
        }

        // Points of the chart: point I counts the requests from second First + I * Step for Step seconds, the last one
        // only up to End.
        internal readonly record struct PlotSpan(long First, long End, long Step)
        {
            internal static PlotSpan Empty { get; } = new(0, 0, 1);

            internal int Count => (int)((End - First + Step - 1) / Step);

            internal long StartOf(int index) => First + (index * Step);

            internal long EndOf(int index) => Math.Min(StartOf(index) + Step, End);

            // The average per second over the seconds that point INDEX covers.
            internal double Average(int index, int total) => (double)total / (EndOf(index) - StartOf(index));
        }

        internal static long SecondOf(DateTime time) => time.Ticks / TimeSpan.TicksPerSecond;

        internal static DateTime TimeOf(long second) => new(second * TimeSpan.TicksPerSecond);

        internal int CountAt(long second) => _counts.TryGetValue(second, out var count) ? count : 0;

        internal void Add(DateTime time)
        {
            var second = SecondOf(time);
            var count = CountAt(second) + 1;
            _counts[second] = count;
            if (Total++ == 0)
            {
                _first = second;
                _last = second;
                _boundsStale = false;
            }
            else if (!_boundsStale)
            {
                _first = Math.Min(_first, second);
                _last = Math.Max(_last, second);
            }
            if (!_peakStale)
            {
                _peak = Math.Max(_peak, count);
            }
        }

        internal void Remove(DateTime time)
        {
            var second = SecondOf(time);
            if (!_counts.TryGetValue(second, out var count))
            {
                return;
            }

            Total--;
            _peakStale |= count == _peak;
            if (count > 1)
            {
                _counts[second] = count - 1;
            }
            else
            {
                _counts.Remove(second);
                _boundsStale |= second == _first || second == _last;
            }
        }

        internal void Clear()
        {
            _counts.Clear();
            Total = 0;
            _peak = 0;
            _peakStale = false;
            _boundsStale = false;
        }

        // The last LiveSeconds seconds completed before NOW, oldest first: the newest is the second before NOW's.
        internal PlotSpan GetLive(DateTime now, List<int> totals)
        {
            totals.Clear();
            var end = SecondOf(now);
            for (var second = end - LiveSeconds; second < end; second++)
            {
                totals.Add(CountAt(second));
            }
            return new PlotSpan(end - LiveSeconds, end, 1);
        }

        // The whole capture across WIDTH pixels, from its first second to its last, in the shortest step that keeps
        // points MinimumPointSpacing apart. TOTALS gets each point's request count.
        internal PlotSpan GetWhole(double width, List<int> totals)
        {
            totals.Clear();
            if (Total == 0)
            {
                return PlotSpan.Empty;
            }

            var first = FirstSecond;
            var end = LastSecond + 1;
            var span = new PlotSpan(first, end, ChooseStep(end - first, width));
            for (var index = span.Count; index > 0; index--)
            {
                totals.Add(0);
            }
            foreach (var (second, count) in _counts)
            {
                totals[(int)((second - first) / span.Step)] += count;
            }
            return span;
        }

        internal static long ChooseStep(long seconds, double width)
        {
            var gaps = Math.Max(Math.Floor(width / MinimumPointSpacing), 0);
            foreach (var step in Steps)
            {
                if (((seconds + step - 1) / step) - 1 <= gaps)
                {
                    return step;
                }
            }
            return (long)Math.Ceiling(seconds / (gaps + 1) / SecondsPerDay) * SecondsPerDay;
        }

        // The chart's top value for the highest value it plots, PEAK: the first of 5, 10, 20, 30, 40, 50, 60, 80, 100,
        // 150, 200, 300, 400, 500, 600, 800, 1000, 1500 and so on at or above it.
        internal static double NiceTop(double peak)
        {
            foreach (var top in Tops)
            {
                if (!(peak > top))
                {
                    return top;
                }
            }

            var magnitude = Math.Pow(10, Math.Floor(Math.Log10(peak)));
            foreach (var factor in TopFactors)
            {
                if (factor * magnitude >= peak)
                {
                    return factor * magnitude;
                }
            }
            return 10 * magnitude;
        }

        // The interval between time labels: the shortest that puts them SPACING pixels apart at PIXELSPERSECOND.
        internal static long ChooseLabelInterval(double pixelsPerSecond, double spacing)
        {
            foreach (var interval in Steps)
            {
                if (interval * pixelsPerSecond >= spacing)
                {
                    return interval;
                }
            }
            return pixelsPerSecond > 0
                ? (long)Math.Ceiling(spacing / pixelsPerSecond / SecondsPerDay) * SecondsPerDay
                : SecondsPerDay;
        }

        // Elapsed capture time as m:ss, or h:mm:ss from an hour.
        internal static string FormatElapsed(long seconds) =>
            seconds >= 3600
                ? string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600}:{(seconds / 60) % 60:00}:{seconds % 60:00}")
                : string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");

        private void RefreshBounds()
        {
            if (!_boundsStale)
            {
                return;
            }

            _boundsStale = false;
            _first = long.MaxValue;
            _last = long.MinValue;
            foreach (var second in _counts.Keys)
            {
                _first = Math.Min(_first, second);
                _last = Math.Max(_last, second);
            }
        }
    }
}
