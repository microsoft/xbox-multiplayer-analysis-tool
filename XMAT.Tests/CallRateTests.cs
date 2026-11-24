// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CaptureAnalysisEngine;
using XMAT.XboxLiveCaptureAnalysis.ReportModels.PerEndpointReport;

namespace XMAT.Tests
{
    // The call rate charts: each endpoint's calls in its call frequency windows, measured the way the analyzer counts
    // them, with the analyzer's own counts and levels on the badges.
    public class CallRateTests
    {
        private const string Profile = "profile.xboxlive.com";
        private const string StatsWrite = "statswrite.xboxlive.com";
        private const string Presence = "userpresence.xboxlive.com";
        private static readonly DateTime SampleStart = new(2026, 11, 13, 3, 42, 6, 337, DateTimeKind.Utc);

        // The request times of analysis-sample.saz (built by build-analysis-sample.ps1), in milliseconds from its first
        // call: profiles looked up one at a time, stats written every 1.6 to 2.4 s, and presence written and read.
        private static readonly int[] ProfileTimes =
        {
            0, 850, 30600, 31720, 32900, 33970, 35280, 36370, 37590, 38740, 40000, 41100, 42200, 43400, 58600, 59800,
            61100, 62300, 117200, 128650, 140100, 154550, 165000, 175400, 192300, 193440, 194650, 195730, 197000,
            198120, 199310, 200550, 201640, 202800,
        };

        private static readonly int[] StatsWriteTimes =
        {
            8400, 10538, 12279, 14314, 16380, 18071, 19698, 21530, 23434, 25431, 27715, 29705, 31904, 33602, 35760,
            37807, 40084, 42205, 44466, 46291, 48559, 50900, 52710, 54483, 56460, 58578, 60634, 62938, 65058, 67113,
            69075, 71151, 73034, 74678, 76823, 78587, 80618, 82562, 84895, 87219, 89521, 91148, 93222, 95219, 97285,
            98966, 100989, 102710, 104707, 106522, 108457, 110530, 112273, 114317, 116447, 118203, 120429, 122720,
            124351, 126153, 128195, 130257, 132189, 133950, 136031, 138335, 140544, 142225, 143952, 146056, 147898,
            150234, 152489, 154661, 156515, 158203, 159947, 162136, 163990, 165722, 167395, 169204, 170824, 172699,
            174513, 176155, 177756, 180069, 182083, 184367, 186050, 188035, 189901, 191837, 194011, 196309, 198382,
            200113, 202241, 204521, 206502, 208337, 210621, 212552,
        };

        private static readonly (int Time, string Path)[] PresenceCalls =
        {
            (1200, "/users/xuid(1000000000000101)/devices/current/titles/current"),
            (13400, "/users/xuid(1000000000000101)/groups/People"),
            (23600, "/users/xuid(1000000000000101)/devices/current/titles/current"),
            (29840, "/users/batch"),
            (30420, "/users/batch"),
            (51100, "/users/xuid(1000000000000101)/devices/current/titles/current"),
            (119000, "/users/batch"),
            (189400, "/users/xuid(1000000000000101)/devices/current/titles/current"),
            (215600, "/users/xuid(1000000000000101)/devices/current/titles/current"),
        };

        static CallRateTests()
        {
            Localization.LoadLanguage("en-us");
        }

        // The sample's profile lookups: 12 in 15 s while players join (against 10) and 34 in 5 min (against 30), both
        // warnings, where the analyzer found them.
        [Fact]
        public void SampleProfileLookups_GoOverBothLimitsWhereTheAnalyzerFoundThem()
        {
            PerEndpointReportDocument document = SampleReport();
            CallRateReport report = CallRateReport.From(document);
            CallRateRow row = Assert.Single(report.Rows, row => row.Endpoint == Profile);
            RuleReportData rule = CallFrequency(document, Profile);

            Assert.Equal((216, 1), (report.Intervals, report.Step));
            Assert.Equal(34, row.Calls);
            Assert.Equal(12, row.Series.Peak);
            Assert.Equal(new[] { 10, 11, 12, 12, 11, 10 }, row.Series.Counts.Skip(42).Take(6));
            Assert.Equal(new[] { new CallRateStretch(43, 46) }, row.Series.OverLimit);
            Assert.Equal("1", rule.ResultData[CallFrequencyRule.TimesBurstExceededDataKey]);
            Assert.Equal((1, ViolationLevel.Warning), (row.OverLimitCount, row.Level));
            Assert.Equal(("1 warning", "10 / 15 s"), (row.Badge, row.LimitLabel));
            Assert.Equal(
                "profile.xboxlive.com went over its burst limit once; the most was 12 calls in 15 seconds, " +
                "against a limit of 10.",
                row.Sentence);
            Assert.Equal("0:44 \u00b7 12 calls in 15 s \u00b7 limit 10", row.Tip(44));

            report.Window = CallRateWindowKind.Sustained;

            Assert.Equal(34, row.Series.Peak);
            Assert.Equal(new[] { new CallRateStretch(200, 216) }, row.Series.OverLimit);
            Assert.Equal("1", rule.ResultData[CallFrequencyRule.TimesSustainedExceededDataKey]);
            Assert.Equal((1, ViolationLevel.Warning), (row.OverLimitCount, row.Level));
            Assert.Equal("30 / 5 min", row.LimitLabel);
        }

        // The sample's 104 stats writes: never more than 10 in 15 s, so no burst finding, but more than ten times the
        // sustained limit of 10 in 5 min, which the analyzer reports as its one error.
        [Fact]
        public void SampleStatsWrites_StayUnderTheBurstLimitAndAreAnErrorOverTheSustainedOne()
        {
            PerEndpointReportDocument document = SampleReport();
            CallRateReport report = CallRateReport.From(document);
            CallRateRow row = report.Rows[0];
            RuleReportData rule = CallFrequency(document, StatsWrite);

            Assert.Equal((StatsWrite, 104), (row.Endpoint, row.Calls));
            Assert.Equal(9, row.Series.Peak);
            Assert.Empty(row.Series.OverLimit);
            Assert.Equal("0", rule.ResultData[CallFrequencyRule.TimesBurstExceededDataKey]);
            Assert.Equal((0, (ViolationLevel?)null, "None"), (row.OverLimitCount, row.Level, row.Badge));
            Assert.Equal(
                "statswrite.xboxlive.com stayed under its burst limit; the most was 9 calls in 15 seconds, " +
                "against a limit of 10.",
                row.Sentence);

            report.Window = CallRateWindowKind.Sustained;

            Assert.Equal(104, row.Series.Peak);
            Assert.Equal(11, row.Series.Counts[28]);
            Assert.Equal(new[] { new CallRateStretch(28, 216) }, row.Series.OverLimit);
            Assert.Equal(1, rule.Errors);
            Assert.Equal((1, ViolationLevel.Error), (row.OverLimitCount, row.Level));
            Assert.Equal(("1 error", "10 / 5 min"), (row.Badge, row.LimitLabel));
            Assert.Equal(
                "statswrite.xboxlive.com went over its sustained limit once, an error; the most was 104 calls in " +
                "5 minutes, against a limit of 10.",
                row.Sentence);
        }

        // A badge names the analyzer's count and level in words, so color is never the only cue.
        [Theory]
        [InlineData(0, ViolationLevel.Warning, "None")]
        [InlineData(1, ViolationLevel.Warning, "1 warning")]
        [InlineData(3, ViolationLevel.Warning, "3 warnings")]
        [InlineData(1, ViolationLevel.Error, "1 error")]
        [InlineData(2, ViolationLevel.Error, "2 errors")]
        public void Badge_NamesTheAnalyzersCountAndLevel(int violations, ViolationLevel level, string expected)
        {
            ServiceCallItem call = Call(0, Profile, "/p", "GET", 0);
            var limits = new CallRateLimits(
                null, new[] { call }, new CallRateWindow(15, 10, violations, level), new CallRateWindow(300, 30, 0, level));
            var row = new CallRateRow(Profile, null, limits, call.ReqTimeUTC, 1, 1);

            row.Show(CallRateWindowKind.Burst);

            Assert.Equal(expected, row.Badge);
            Assert.Equal(violations > 0, row.IsOverLimit);
        }

        // A window holds the calls up to its length apart in whole milliseconds, as the analyzer's does, and a sample
        // keeps the most the window held during its second, so a burst between two samples still shows.
        [Theory]
        [InlineData(new[] { 0, 1000 }, 1u, new[] { 1, 2, 1 })]
        [InlineData(new[] { 0, 1001 }, 1u, new[] { 1, 1, 1 })]
        [InlineData(new[] { 200, 900, 1150 }, 2u, new[] { 0, 2, 3, 0 })]
        public void Measure_CountsCallsAsTheAnalyzerDoes(int[] milliseconds, uint limit, int[] expected)
        {
            ServiceCallItem[] calls = milliseconds
                .Select((time, index) => Call((uint)index, Profile, "/p", "GET", time))
                .ToArray();
            ulong start = (ulong)SampleStart.ToFileTimeUtc();

            CallRateSeries series = CallRateSeries.Measure(
                calls.Select(call => call.ReqTimeUTC).ToArray(), start, expected.Length - 1, 1, 1, limit);

            Assert.Equal(expected, series.Counts);
            Assert.Equal(
                Utils.GetExcessCallsForTimeWindow(calls, 1000, limit).Count > 0,
                series.OverLimit.Count > 0);
        }

        // The switch moves every row to the window it picks, named with the period the rows share.
        [Fact]
        public void Switch_MovesEveryRowToTheWindowItPicks()
        {
            CallRateReport report = CallRateReport.From(SampleReport());
            var changed = new List<string>();
            report.PropertyChanged += (sender, e) => changed.Add(e.PropertyName);
            CallRateRow row = report.Rows[1];
            var rowChanged = 0;
            row.PropertyChanged += (sender, e) => rowChanged++;

            Assert.Equal(("Burst (15 s)", "Sustained (5 min)"), (report.BurstLabel, report.SustainedLabel));
            Assert.True(report.IsBurst);
            Assert.All(report.Rows, item => Assert.Equal(CallRateWindowKind.Burst, item.Window));

            report.IsBurst = false;
            Assert.True(report.IsBurst);
            report.IsSustained = true;

            Assert.Equal(CallRateWindowKind.Sustained, report.Window);
            Assert.False(report.IsBurst);
            Assert.Equal(new[] { nameof(report.Window), nameof(report.IsBurst), nameof(report.IsSustained) }, changed);
            Assert.All(report.Rows, item =>
            {
                Assert.Equal(CallRateWindowKind.Sustained, item.Window);
                Assert.Equal(300u, item.Series.WindowSeconds);
                Assert.EndsWith(" / 5 min", item.LimitLabel);
                Assert.Contains("its sustained limit", item.Sentence);
            });
            Assert.Equal(1, rowChanged);

            report.IsBurst = true;
            Assert.Equal("10 / 15 s", row.LimitLabel);
            Assert.Equal(15u, row.Series.WindowSeconds);
        }

        // Endpoints are ordered busiest first, and an endpoint whose calls fall under more than one set of limits gets
        // a row for each, named by the set's own description; a set that counted none of its calls has no row.
        [Fact]
        public void Rows_FollowEachSetOfLimitsThatCountedCalls()
        {
            PerEndpointReportDocument document = SampleReport(
                Call(900, "sessiondirectory.xboxlive.com", "/handles", "POST", 27000));
            CallRateReport report = CallRateReport.From(document);

            Assert.Equal(
                new[]
                {
                    (StatsWrite, null, 104),
                    (Profile, null, 34),
                    (Presence, "write presence", 5),
                    (Presence, "read presence", 4),
                    ("sessiondirectory.xboxlive.com", (string)null, 1),
                },
                report.Rows.Select(row => (row.Endpoint, row.Detail, row.Calls)));
            Assert.Equal(2, CallFrequency(document, "sessiondirectory.xboxlive.com").CallRates.Limits.Count);
            CallRateRow read = report.Rows[3];
            Assert.Equal("3 / 15 s", read.LimitLabel);
            Assert.StartsWith(
                "userpresence.xboxlive.com (read presence) stayed under its burst limit; the most was 2 calls",
                read.Sentence);
            Assert.DoesNotContain("CallRates", document.ToJson(), StringComparison.Ordinal);

            report.Window = CallRateWindowKind.Sustained;

            Assert.Equal("100 / 5 min", read.LimitLabel);
            Assert.Equal("30 / 5 min", report.Rows[2].LimitLabel);
        }

        // The sample's call frequency findings, from the shipped rules and URI map: profile, statswrite and
        // userpresence calls at their recorded times, and any OTHER calls.
        internal static PerEndpointReportDocument SampleReport(params ServiceCallItem[] other)
        {
            uint id = 0;
            var items = new List<ServiceCallItem>();
            items.AddRange(ProfileTimes.Select(time => Call(id++, Profile, "/users/batch/profile/settings", "POST", time)));
            items.AddRange(StatsWriteTimes.Select(time =>
                Call(id++, StatsWrite, "/stats/users/1000000000000101/scids/00000000-0000-0000-0000-0000534d504c/", "POST", time)));
            items.AddRange(PresenceCalls.Select(call => Call(id++, Presence, call.Path, "POST", call.Time)));
            items.AddRange(other);

            var data = new ServiceCallData(false);
            data.CreateFromServiceCallItems(items);
            var analyzer = new TraceAnalyzer();
            analyzer.LoadURIMap(Path.Combine(AppContext.BaseDirectory, "Data", "XboxLiveTraceAnalyzer.APIMap.csv"));
            using (var rules = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(CallFrequencyRules()))))
            {
                analyzer.LoadRules(rules);
            }

            analyzer.AddReports(new Report[] { new PerEndpointReport() });
            analyzer.RunUriConverterOnData(data);
            Dictionary<string, IEnumerable<ReportDocument>> reports = analyzer.Run(new RulesEngine(false), data, null);
            return Assert.IsType<PerEndpointReportDocument>(Assert.Single(Assert.Single(reports).Value));
        }

        internal static RuleReportData CallFrequency(PerEndpointReportDocument document, string endpoint) =>
            document.EndpointReportData
                .Single(data => data.UriService == endpoint)
                .RuleReportData.Single(rule => rule.Name == CallFrequencyRule.DisplayName);

        // The shipped rules file with only its call frequency rules.
        private static string CallFrequencyRules()
        {
            JsonNode rules = JsonNode.Parse(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "XboxLiveTraceAnalyzer.Rules.json")));
            var callFrequency = new JsonArray(rules["Rules"].AsArray()
                .Where(rule => (string)rule["Type"] == nameof(CallFrequencyRule))
                .Select(rule => rule.DeepClone())
                .ToArray());
            return new JsonObject { ["Version"] = rules["Version"]?.DeepClone(), ["Rules"] = callFrequency }
                .ToJsonString(new JsonSerializerOptions());
        }

        internal static ServiceCallItem Call(uint id, string host, string path, string method, int milliseconds) => new(id)
        {
            ConsoleIP = "192.0.2.10",
            Host = host,
            PhysicalHost = host,
            EndpointKind = EndpointKind.Captured,
            Uri = $"https://{host}{path}",
            Method = method,
            ReqTimeUTC = (ulong)SampleStart.AddMilliseconds(milliseconds).ToFileTimeUtc(),
            ElapsedCallTimeMs = 100,
            ReqBody = string.Empty,
        };
    }
}
