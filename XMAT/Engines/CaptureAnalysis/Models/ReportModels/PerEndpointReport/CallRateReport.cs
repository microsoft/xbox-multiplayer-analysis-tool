// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using CaptureAnalysisEngine;

namespace XMAT.XboxLiveCaptureAnalysis.ReportModels.PerEndpointReport
{
    internal enum CallRateWindowKind
    {
        Burst,
        Sustained,
    }

    // The Per Endpoint report's call frequency section: a row for each set of call frequency limits that counted an
    // endpoint's calls, charted on one time axis, in the window that the switch picks.
    internal sealed class CallRateReport : INotifyPropertyChanged
    {
        private CallRateWindowKind _window;

        private CallRateReport(IReadOnlyList<CallRateRow> rows, int intervals, int step)
        {
            Rows = rows;
            Intervals = intervals;
            Step = step;
            BurstLabel = SwitchLabel("CALL_RATE_BURST", rows.Select(row => row.Limits.Burst.PeriodSeconds));
            SustainedLabel = SwitchLabel("CALL_RATE_SUSTAINED", rows.Select(row => row.Limits.Sustained.PeriodSeconds));
            foreach (CallRateRow row in rows)
            {
                row.Show(_window);
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public IReadOnlyList<CallRateRow> Rows { get; }

        public bool HasRows => Rows.Count > 0;

        // The shared time axis: samples 0 to Intervals, Step seconds apart from the capture's first call.
        public int Intervals { get; }
        public int Step { get; }
        public long Seconds => (long)Intervals * Step;

        // The switch's choices, with the period when every row shares it, such as "Burst (15 s)".
        public string BurstLabel { get; }
        public string SustainedLabel { get; }

        public CallRateWindowKind Window
        {
            get => _window;
            set
            {
                if (_window == value)
                {
                    return;
                }

                _window = value;
                foreach (CallRateRow row in Rows)
                {
                    row.Show(value);
                }

                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Window)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBurst)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSustained)));
            }
        }

        // For the switch's two radio buttons; clearing one leaves the window to the other.
        public bool IsBurst
        {
            get => Window == CallRateWindowKind.Burst;
            set
            {
                if (value)
                {
                    Window = CallRateWindowKind.Burst;
                }
            }
        }

        public bool IsSustained
        {
            get => Window == CallRateWindowKind.Sustained;
            set
            {
                if (value)
                {
                    Window = CallRateWindowKind.Sustained;
                }
            }
        }

        // Rows for the endpoints whose call frequency rule kept its counted calls, busiest endpoint first; the time
        // axis runs from the first of their calls to the last.
        internal static CallRateReport From(PerEndpointReportDocument document)
        {
            var endpoints = document.EndpointReportData
                .Select(endpoint => (
                    Endpoint: endpoint,
                    Timeline: endpoint.RuleReportData
                        .FirstOrDefault(rule => rule.Name == CallFrequencyRule.DisplayName)?.CallRates))
                .Where(item => item.Timeline is { Calls: > 0, Limits.Count: > 0 })
                .ToList();
            if (endpoints.Count == 0)
            {
                return new CallRateReport(Array.Empty<CallRateRow>(), 1, 1);
            }

            ulong start = endpoints.Min(item => item.Timeline.FirstCall);
            ulong end = endpoints.Max(item => item.Timeline.LastCall);
            long seconds = Math.Max(1, (long)((end - start + (ulong)TimeSpan.TicksPerSecond - 1) / (ulong)TimeSpan.TicksPerSecond));
            int step = CallRateSeries.StepFor(seconds);
            int intervals = (int)((seconds + step - 1) / step);
            Dictionary<EndpointKey, string> labels =
                EndpointLabels.For(document.EndpointReportData.Select(data => data.Key));

            var rows = new List<CallRateRow>();
            foreach (var (endpoint, timeline) in endpoints
                .OrderByDescending(item => item.Timeline.Calls)
                .ThenBy(item => labels[item.Endpoint.Key], StringComparer.Ordinal))
            {
                // A set of limits that counted none of the endpoint's calls has nothing to show, unless no set did.
                List<CallRateLimits> counted = timeline.Limits.Where(limits => limits.CallTimes.Count > 0).ToList();
                if (counted.Count == 0)
                {
                    counted.Add(timeline.Limits[0]);
                }

                foreach (CallRateLimits limits in counted)
                {
                    rows.Add(new CallRateRow(
                        labels[endpoint.Key],
                        counted.Count > 1 ? limits.Description : null,
                        limits,
                        start,
                        intervals,
                        step));
                }
            }

            return new CallRateReport(rows, intervals, step);
        }

        // m:ss, or h:mm:ss from an hour.
        internal static string FormatTime(long seconds) =>
            seconds >= 3600
                ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}"
                : $"{seconds / 60}:{seconds % 60:00}";

        // A window's length, such as "15 s" or "5 min".
        internal static string Period(uint seconds) =>
            seconds >= 60 && seconds % 60 == 0
                ? Localization.GetLocalizedString("CALL_RATE_PERIOD_MINUTES", seconds / 60)
                : Localization.GetLocalizedString("CALL_RATE_PERIOD_SECONDS", seconds);

        // A window's length in words, such as "15 seconds" or "5 minutes".
        internal static string PeriodInWords(uint seconds)
        {
            bool minutes = seconds >= 60 && seconds % 60 == 0;
            uint count = minutes ? seconds / 60 : seconds;
            string key = (minutes, count == 1) switch
            {
                (true, true) => "CALL_RATE_PERIOD_MINUTE_WORDS",
                (true, false) => "CALL_RATE_PERIOD_MINUTES_WORDS",
                (false, true) => "CALL_RATE_PERIOD_SECOND_WORDS",
                _ => "CALL_RATE_PERIOD_SECONDS_WORDS",
            };
            return Localization.GetLocalizedString(key, count);
        }

        internal static string CallCount(int calls) =>
            Localization.GetLocalizedString(calls == 1 ? "CALL_RATE_CALL" : "CALL_RATE_CALLS", calls);

        private static string SwitchLabel(string key, IEnumerable<uint> periods)
        {
            string name = Localization.GetLocalizedString(key);
            uint[] distinct = periods.Distinct().Take(2).ToArray();
            return distinct.Length == 1
                ? Localization.GetLocalizedString("CALL_RATE_WINDOW_PERIOD", name, Period(distinct[0]))
                : name;
        }
    }

    // One set of an endpoint's call frequency limits in the window shown: its chart, the analyzer's verdict on it, and
    // a sentence that says both for screen readers.
    internal sealed class CallRateRow : INotifyPropertyChanged
    {
        private readonly ulong _start;
        private readonly int _intervals;
        private readonly int _step;
        private CallRateSeries _burst;
        private CallRateSeries _sustained;

        internal CallRateRow(string endpoint, string detail, CallRateLimits limits, ulong start, int intervals, int step)
        {
            Endpoint = endpoint;
            Detail = detail;
            Limits = limits;
            _start = start;
            _intervals = intervals;
            _step = step;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        internal CallRateLimits Limits { get; }

        public string Endpoint { get; }

        // The limits' description when the endpoint has more than one row, such as "write presence"; otherwise null.
        public string Detail { get; }

        // The calls the limits count.
        public int Calls => Limits.CallTimes.Count;

        public CallRateWindowKind Window { get; private set; }

        public CallRateSeries Series { get; private set; }

        // The limit and its window, such as "10 / 15 s".
        public string LimitLabel { get; private set; }

        // How many times the analyzer found the calls over the limit, and the highest level it gave them; null when
        // it never did.
        public int OverLimitCount { get; private set; }
        public ViolationLevel? Level { get; private set; }
        public bool IsOverLimit => Level != null;

        // That count with its level, such as "1 warning", "3 warnings" or "1 error", so color is never the only cue;
        // "None" when there is none.
        public string Badge { get; private set; }

        public string Sentence { get; private set; }

        public override string ToString() => Sentence;

        internal void Show(CallRateWindowKind window)
        {
            CallRateWindow limits = window == CallRateWindowKind.Burst ? Limits.Burst : Limits.Sustained;
            Window = window;
            Series = window == CallRateWindowKind.Burst
                ? _burst ??= Measure(Limits.Burst)
                : _sustained ??= Measure(Limits.Sustained);
            LimitLabel = Localization.GetLocalizedString(
                "CALL_RATE_LIMIT", limits.Limit, CallRateReport.Period(limits.PeriodSeconds));
            OverLimitCount = limits.Violations;
            Level = limits.Violations > 0 ? limits.Level : null;
            Badge = Level switch
            {
                ViolationLevel.Error => Localization.GetLocalizedString(
                    limits.Violations == 1 ? "LTA_ISSUE_COUNT_ERROR" : "LTA_ISSUE_COUNT_ERRORS", limits.Violations),
                ViolationLevel.Warning => Localization.GetLocalizedString(
                    limits.Violations == 1 ? "LTA_ISSUE_COUNT_WARNING" : "LTA_ISSUE_COUNT_WARNINGS", limits.Violations),
                _ => Localization.GetLocalizedString("CALL_RATE_NONE"),
            };
            Sentence = Describe(window, limits);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }

        // The hover readout for a sample, such as "2:10 · 12 calls in 15 s · limit 10".
        public string Tip(int sample) =>
            Localization.GetLocalizedString(
                "CALL_RATE_TIP",
                CallRateReport.FormatTime((long)sample * _step),
                CallRateReport.CallCount(Series.Counts[sample]),
                CallRateReport.Period(Series.WindowSeconds),
                Series.Limit);

        private CallRateSeries Measure(CallRateWindow window) =>
            CallRateSeries.Measure(Limits.CallTimes, _start, _intervals, _step, window.PeriodSeconds, window.Limit);

        private string Describe(CallRateWindowKind window, CallRateWindow limits)
        {
            bool error = Level == ViolationLevel.Error;
            string key = limits.Violations switch
            {
                0 => "CALL_RATE_SENTENCE_UNDER",
                1 => error ? "CALL_RATE_SENTENCE_ERROR_ONCE" : "CALL_RATE_SENTENCE_ONCE",
                _ => error ? "CALL_RATE_SENTENCE_ERROR_TIMES" : "CALL_RATE_SENTENCE_TIMES",
            };
            return Localization.GetLocalizedString(
                key,
                Detail == null ? Endpoint : Localization.GetLocalizedString("CALL_RATE_ENDPOINT_DETAIL", Endpoint, Detail),
                Localization.GetLocalizedString(
                    window == CallRateWindowKind.Burst ? "CALL_RATE_BURST_NAME" : "CALL_RATE_SUSTAINED_NAME"),
                CallRateReport.CallCount(Series.Peak),
                CallRateReport.PeriodInWords(limits.PeriodSeconds),
                limits.Limit,
                limits.Violations);
        }
    }
}
