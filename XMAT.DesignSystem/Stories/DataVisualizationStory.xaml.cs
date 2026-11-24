// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Controls;
using CaptureAnalysisEngine;
using XMAT.NetworkTraceCaptureAnalysis.Models;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using XMAT.XboxLiveCaptureAnalysis.ReportModels.PerEndpointReport;

namespace XMAT.DesignSystem
{
    // The Data visualization page: XMAT's charts, from the real controls, with plausible captures and analyses.
    public partial class DataVisualizationStory : Story
    {
        // The sample capture starts at 14:30:05 and runs 45 seconds. Its times have no kind, so they read the same in
        // every time zone.
        private static readonly DateTime CaptureStart = new(2026, 11, 19, 14, 30, 5);

        // Requests in each second of the sample capture: a burst as players sign in, quieter traffic, a second burst as
        // they join a match, and a rise toward the end.
        private static readonly int[] RequestsPerSecond =
        {
            12, 33, 52, 58, 44, 34, 27, 23, 21, 20, 22, 19, 18, 21, 23, 20, 19, 25, 37, 49, 52, 41, 31, 26, 23,
            21, 19, 20, 22, 21, 19, 18, 20, 23, 22, 20, 19, 21, 24, 26, 29, 31, 34, 36, 38,
        };

        // The sample capture's hosts, each repeated by how busy it is.
        private static readonly string[] HostShares = new (string Host, int Share)[]
            {
                ("sessiondirectory.xboxlive.com", 9), ("userpresence.xboxlive.com", 6), ("profile.xboxlive.com", 5),
                ("social.xboxlive.com", 4), ("achievements.xboxlive.com", 3), ("titlehub.xboxlive.com", 3),
                ("statswrite.xboxlive.com", 2), ("privacy.xboxlive.com", 1), ("inventory.xboxlive.com", 1),
                ("leaderboards.xboxlive.com", 1),
            }
            .SelectMany(host => Enumerable.Repeat(host.Host, host.Share))
            .ToArray();

        // The sample analysis's first call, and when its calls were made, in milliseconds from it: profiles looked up
        // one at a time and in a rush as players join, stats written every 1.6 to 2.4 s, and presence written and read.
        private static readonly DateTime AnalysisStart = new(2026, 11, 13, 3, 42, 6, 337, DateTimeKind.Utc);

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

        private static readonly int[] PresenceWriteTimes = { 1200, 23600, 51100, 189400, 215600 };

        private static readonly int[] PresenceReadTimes = { 13400, 29840, 30420, 119000 };

        public DataVisualizationStory()
        {
            InitializeComponent();

            List<ProxyConnectionModel> capture = SampleCapture();
            // While the capture runs, it's 300 ms into the second after its last.
            DateTime now = CaptureStart.AddSeconds(RequestsPerSecond.Length).AddMilliseconds(300);
            CapturingOverview.ItemsSource = capture;
            CapturingOverview.IsCapturing = true;
            CapturingOverview.Clock = () => now;
            NarrowerOverview.ItemsSource = capture;
            NarrowestOverview.ItemsSource = capture;

            Timeline.ItemsSource = new ObservableCollection<ProxyConnectionModel>(SampleRequests());

            WholeVolume.Content = Volume(capture, live: false, now);
            LiveVolume.Content = Volume(capture, live: true, now);

            StatusSummary.Model = ColumnSummaryModel.Create(
                "Status", StatusColumn(), capture, capture.First(row => row.Status == "429"));
            HostSummary.Model = ColumnSummaryModel.Create("Host", HostColumn(), capture, null);

            FewValues.DataContext = Histogram(
                "NETCAP_ANALYSIS_RESULT_PSBS", NetworkTraceMeasure.Packets, NetworkTraceGrouping.Source,
                ("192.168.1.20", 582), ("192.168.1.30", 14), ("192.168.1.1", 12));
            HistogramModel received = Histogram(
                "NETCAP_ANALYSIS_RESULT_PRBS", NetworkTraceMeasure.Packets, NetworkTraceGrouping.Source,
                ("203.0.113.31", 254), ("198.51.100.10", 89), ("203.0.113.24", 38), ("192.168.1.1", 37),
                ("203.0.113.36", 22), ("192.0.2.7", 21), ("198.51.100.95", 19), ("192.0.2.122", 16), ("192.0.2.127", 9),
                ("198.51.100.4", 3));
            ManyValues.DataContext = received;
            AllValues.DataContext = received;
            Averages.DataContext = Histogram(
                "NETCAP_ANALYSIS_RESULT_PLBS", NetworkTraceMeasure.AverageBytes, NetworkTraceGrouping.Source,
                ("203.0.113.31", 839), ("203.0.113.36", 769), ("192.0.2.7", 487), ("203.0.113.24", 397),
                ("198.51.100.10", 313), ("192.168.1.1", 217), ("192.0.2.122", 191), ("198.51.100.95", 94));
            NoValues.DataContext = Histogram(
                "NETCAP_ANALYSIS_RESULT_PSBS", NetworkTraceMeasure.Packets, NetworkTraceGrouping.Source);
            Protocols.DataContext = Histogram(
                "NETCAP_ANALYSIS_RESULT_PCBP", NetworkTraceMeasure.Packets, NetworkTraceGrouping.Protocol,
                ("6 (TCP)", 684), ("17 (UDP)", 558), ("0 (HOPOPT)", 14), ("108", 12));
            BytesSent.DataContext = Histogram(
                "NETCAP_ANALYSIS_RESULT_BSBS", NetworkTraceMeasure.Bytes, NetworkTraceGrouping.Source,
                ("192.168.1.20", 133287), ("192.168.1.30", 5088), ("192.168.1.1", 1644));

            BurstCallRate.DataContext = CallRates(CallRateWindowKind.Burst);
            SustainedCallRate.DataContext = CallRates(CallRateWindowKind.Sustained);
        }

        // The sample capture's requests, as the traffic table lists them: mostly 2xx, with redirects, throttled
        // requests and a few server errors spread through it.
        private static List<ProxyConnectionModel> SampleCapture()
        {
            var requests = new List<ProxyConnectionModel>();
            for (int second = 0; second < RequestsPerSecond.Length; second++)
            {
                int count = RequestsPerSecond[second];
                for (int index = 0; index < count; index++)
                {
                    int number = requests.Count;
                    string status = (number % 89) == 41 ? "503"
                        : (number % 21) == 7 ? "429"
                        : (number % 13) == 5 ? "304"
                        : (number % 9) == 4 ? "201"
                        : "200";
                    string host = HostShares[(number * 13) % HostShares.Length];
                    requests.Add(Request(
                        number + 1,
                        CaptureStart.AddSeconds(second).AddMilliseconds(index * 1000 / count),
                        40 + ((number * 37) % 260),
                        status == "201" ? "POST" : "GET",
                        host,
                        "/users/xuid(2533274812345678)",
                        status));
                }
            }

            return requests;
        }

        // A player joining a match, request by request: its first requests, as the waterfall shows them.
        private static IEnumerable<ProxyConnectionModel> SampleRequests()
        {
            const string Session = "/serviceconfigs/7492baca-c1b4-440d-a391-b7ef364a8d40/sessiontemplates";
            const string Player = "/users/xuid(2533274812345678)";
            var requests = new (int Start, int Duration, string Method, string Host, string Path, string Status)[]
            {
                (0, 180, "POST", "profile.xboxlive.com", "/users/batch/profile/settings", "200"),
                (40, 120, "POST", "userpresence.xboxlive.com", Player + "/devices/current/titles/current", "200"),
                (110, 460, "PUT", "sessiondirectory.xboxlive.com", Session + "/lobby/sessions/8a0c", "201"),
                (150, 90, "GET", "achievements.xboxlive.com", Player + "/achievements", "304"),
                (230, 340, "GET", "social.xboxlive.com", Player + "/people", "200"),
                (320, 150, "POST", "profile.xboxlive.com", "/users/batch/profile/settings", "429"),
                (400, 620, "GET", "titlehub.xboxlive.com", Player + "/titles/titlehistory/decoration/image", "200"),
                (590, 210, "POST", "sessiondirectory.xboxlive.com", "/handles", "201"),
                (700, 480, "GET", "sessiondirectory.xboxlive.com", Session + "/match/sessions/3f61", "503"),
                (830, 130, "POST", "statswrite.xboxlive.com", "/stats/users/2533274812345678/scids/00000000", "200"),
                (960, 300, "GET", "inventory.xboxlive.com", "/users/me/inventory", "200"),
                (1180, 350, "GET", "social.xboxlive.com", "/users/me/summary", string.Empty),
            };
            return requests.Select((request, index) => Request(
                index + 1, CaptureStart.AddMilliseconds(request.Start), request.Duration, request.Method, request.Host,
                request.Path, request.Status));
        }

        // A request as the capture recorded it; one without a status has no response yet.
        private static ProxyConnectionModel Request(
            int number, DateTime time, int milliseconds, string method, string host, string path, string status) =>
            new(number, status, time, TimeSpan.FromMilliseconds(milliseconds), "https", host, method, path,
                $"{method} {path} HTTP/1.1\r\nHost: {host}\r\n", Array.Empty<byte>(),
                status.Length == 0 ? string.Empty : $"HTTP/1.1 {status}\r\n", Array.Empty<byte>(), "192.168.1.20");

        // The timeline's Volume chart of REQUESTS, in the timeline's well: the whole capture, or while LIVE, the minute
        // before NOW.
        private static Border Volume(IEnumerable<ProxyConnectionModel> requests, bool live, DateTime now)
        {
            var series = new CaptureVolumeSeries();
            foreach (ProxyConnectionModel request in requests)
            {
                series.Add(request.RequestTime);
            }

            var chart = new CaptureVolumeChart();
            chart.SetResourceReference(CaptureVolumeChart.LineBrushProperty, "AccentBrush");
            chart.SetResourceReference(CaptureVolumeChart.GridLineBrushProperty, "BorderSubtleBrush");
            chart.SetResourceReference(CaptureVolumeChart.BaselineBrushProperty, "BorderSubtleBrush");
            chart.SetResourceReference(CaptureVolumeChart.LabelBrushProperty, "TextSecondaryBrush");
            chart.SetResourceReference(CaptureVolumeChart.AxisFontSizeProperty, "TimelineAxisFontSize");
            chart.SetResourceReference(CaptureVolumeChart.ReadoutFontSizeProperty, "BaseFontSize");
            // As the timeline does, the chart is shown again at each new size.
            chart.SizeChanged += (_, _) => chart.Show(series, live, now);
            var well = new Border { Width = 790, Height = 160, Margin = default, Child = chart };
            well.SetResourceReference(StyleProperty, "TimelineWellStyle");
            return well;
        }

        // The traffic table's Status column, as its column summary reads it.
        private static ColumnSummaryColumn StatusColumn() =>
            new(row => ((ProxyConnectionModel)row).Status)
            {
                EmptyValueText = Localization.GetLocalizedString("COLUMN_SUMMARY_NO_STATUS"),
                RowCountText = RequestCount,
                BarOf = ColumnSummaryBars.ForHttpStatus,
                Select = _ => { },
                Hint = Localization.GetLocalizedString("COLUMN_SUMMARY_HINT_VALUE"),
            };

        // The traffic table's Host column, as its column summary reads it.
        private static ColumnSummaryColumn HostColumn() =>
            new(row => ((ProxyConnectionModel)row).Host)
            {
                RowCountText = RequestCount,
                ValueCountText = count => ColumnSummaryModel.Counted(count, "COLUMN_SUMMARY_HOST", "COLUMN_SUMMARY_HOSTS"),
                MoreValuesText = count =>
                    ColumnSummaryModel.Counted(count, "COLUMN_SUMMARY_MORE_HOST", "COLUMN_SUMMARY_MORE_HOSTS"),
                Select = _ => { },
                Hint = Localization.GetLocalizedString("COLUMN_SUMMARY_HINT_HOST"),
            };

        private static string RequestCount(int count) =>
            ColumnSummaryModel.Counted(count, "COLUMN_SUMMARY_REQUEST", "COLUMN_SUMMARY_REQUESTS");

        // A list of the network capture analysis, as the analyzer makes it.
        private static HistogramModel Histogram(
            string topic, NetworkTraceMeasure measure, NetworkTraceGrouping groupedBy, params (string Name, int Value)[] values) =>
            new NetworkTraceAnalyzerResultsModel
            {
                Topic = Localization.GetLocalizedString(topic),
                Measure = measure,
                GroupedBy = groupedBy,
                Values = values
                    .Select(value => new NetworkTraceAnalyzerResultsDataModel { Name = value.Name, Value = value.Value })
                    .ToList(),
            }.Histogram;

        // The sample analysis's call frequency section in WINDOW, with the limits of XMAT's shipped rules and what the
        // analyzer finds in these calls: profile lookups over both limits, stats writes over the sustained one.
        private static CallRateReport CallRates(CallRateWindowKind window)
        {
            var document = new PerEndpointReportDocument();
            document.EndpointReportData.Add(Endpoint(
                "profile.xboxlive.com",
                (null, ProfileTimes, new CallRateWindow(15, 10, 1, ViolationLevel.Warning),
                    new CallRateWindow(300, 30, 1, ViolationLevel.Warning))));
            document.EndpointReportData.Add(Endpoint(
                "statswrite.xboxlive.com",
                (null, StatsWriteTimes, new CallRateWindow(15, 10, 0, ViolationLevel.Warning),
                    new CallRateWindow(300, 10, 1, ViolationLevel.Error))));
            document.EndpointReportData.Add(Endpoint(
                "userpresence.xboxlive.com",
                ("write presence", PresenceWriteTimes, new CallRateWindow(15, 3, 0, ViolationLevel.Warning),
                    new CallRateWindow(300, 30, 0, ViolationLevel.Warning)),
                ("read presence", PresenceReadTimes, new CallRateWindow(15, 3, 0, ViolationLevel.Warning),
                    new CallRateWindow(300, 100, 0, ViolationLevel.Warning))));
            CallRateReport report = CallRateReport.From(document);
            report.Window = window;
            return report;
        }

        // An endpoint of the Per Endpoint report, with what its call frequency rule counted for each set of LIMITS.
        private static EndpointReportData Endpoint(
            string host, params (string Description, int[] Times, CallRateWindow Burst, CallRateWindow Sustained)[] limits)
        {
            List<ServiceCallItem>[] calls = limits
                .Select(set => set.Times
                    .Select((time, index) => new ServiceCallItem((uint)index)
                    {
                        ReqTimeUTC = (ulong)AnalysisStart.AddMilliseconds(time).ToFileTimeUtc(),
                    })
                    .ToList())
                .ToArray();
            CallRateTimeline timeline = CallRateTimeline.For(calls.SelectMany(set => set));
            for (int index = 0; index < limits.Length; index++)
            {
                timeline.Limits.Add(new CallRateLimits(
                    limits[index].Description, calls[index], limits[index].Burst, limits[index].Sustained));
            }

            var endpoint = new EndpointReportData { UriService = host, Kind = EndpointKind.Captured };
            endpoint.RuleReportData.Add(new RuleReportData { Name = CallFrequencyRule.DisplayName, CallRates = timeline });
            return endpoint;
        }
    }
}
