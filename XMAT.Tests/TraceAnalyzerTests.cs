// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using CaptureAnalysisEngine;

namespace XMAT.Tests
{
    public class TraceAnalyzerTests
    {
        [Fact]
        public void Run_WritesEveryJsonReportForFirstEligibleConsoleOnly()
        {
            string outputDirectory =
                AnalysisReportTestPaths.CreateOwnedDirectory("xtr");
            try
            {
                var analyzer = new TraceAnalyzer();
                analyzer.AddRule(new ConsoleCountRule
                {
                    Name = "ConsoleCount",
                    Endpoint = "*"
                });
                analyzer.AddReports(new Report[]
                {
                    new FirstJsonReport(),
                    new SecondJsonReport(),
                    new ThirdJsonReport()
                });
                ServiceCallData data = CreateMultiConsoleData();
                var rulesEngine = new RulesEngine(false);

                Dictionary<string, IEnumerable<ReportDocument>> reports =
                    analyzer.Run(
                        rulesEngine,
                        data,
                        outputDirectory);

                Assert.Equal(
                    new[] { "first-console", "second-console" },
                    reports.Keys);
                Assert.All(
                    reports["first-console"],
                    report => Assert.Equal(1, ReportCallCount(report.ToJson())));
                Assert.Equal(
                    1,
                    ReportCallCount(File.ReadAllText(Path.Join(
                        outputDirectory,
                        "FirstJsonReport.json"))));
                Assert.Equal(
                    1,
                    ReportCallCount(File.ReadAllText(Path.Join(
                        outputDirectory,
                        "SecondJsonReport.json"))));
                Assert.Equal(
                    1,
                    ReportCallCount(File.ReadAllText(Path.Join(
                        outputDirectory,
                        "ThirdJsonReport.json"))));
            }
            finally
            {
                AnalysisReportTestPaths.DeleteOwnedDirectory(
                    outputDirectory);
            }
        }

        private static ServiceCallData CreateMultiConsoleData()
        {
            var data = new ServiceCallData(false)
            {
                m_endpointToService =
                    new Dictionary<string, Tuple<string, string, string>>()
            };
            data.m_perConsoleData.Add(
                "empty-console",
                new ServiceCallData.PerConsoleData());
            data.m_perConsoleData.Add(
                "first-console",
                CreateConsoleData(1));
            data.m_perConsoleData.Add(
                "second-console",
                CreateConsoleData(2));
            return data;
        }

        private static ServiceCallData.PerConsoleData CreateConsoleData(
            int callCount)
        {
            const string endpoint = "example.xboxlive.com";
            var endpointKey = new EndpointKey(
                EndpointKind.Captured,
                endpoint);
            var calls = new LinkedList<ServiceCallItem>();
            for (int index = 0; index < callCount; index++)
            {
                calls.AddLast(new ServiceCallItem((uint)(index + 1))
                {
                    Host = endpoint,
                    Uri = $"https://{endpoint}/{index}",
                    ConsoleIP = $"10.0.0.{callCount}",
                    ReqTimeUTC = (ulong)DateTime.UtcNow
                        .AddMilliseconds(index)
                        .ToFileTimeUtc(),
                    ElapsedCallTimeMs = 10,
                    HttpStatusCode = 200
                });
            }

            var console = new ServiceCallData.PerConsoleData();
            console.m_servicesHistory.Add(endpointKey, calls);
            console.m_servicesStats.Add(
                endpointKey,
                new ServiceCallStats(calls));
            return console;
        }

        private static int ReportCallCount(string json)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement
                .GetProperty("CallCount")
                .GetInt32();
        }

        private sealed class ConsoleCountRule : BaseRule<ConsoleCountRule>
        {
            public override void DeserializeJson(JsonElement json)
            {
            }

            public override RuleResult Run(
                RulesEngine engine,
                IEnumerable<ServiceCallItem> items,
                ServiceCallStats stats)
            {
                RuleResult result = InitializeResult("ConsoleCount", "");
                result.Results.Add("CallCount", items.Count());
                return result;
            }
        }

        private sealed class ConsoleCountReportDocument : ReportDocument
        {
            internal int CallCount { get; set; }

            public override string ToJson()
            {
                return JsonSerializer.Serialize(new
                {
                    CallCount
                });
            }
        }

        private abstract class ConsoleCountReport : Report
        {
            public override ReportDocument RunReport(
                IEnumerable<RuleResult> result,
                Dictionary<string, Tuple<string, string, string>> endpoints)
            {
                RuleResult countResult = Assert.Single(result);
                return new ConsoleCountReportDocument
                {
                    CallCount = (int)countResult.Results["CallCount"]
                };
            }
        }

        private sealed class FirstJsonReport : ConsoleCountReport
        {
        }

        private sealed class SecondJsonReport : ConsoleCountReport
        {
        }

        private sealed class ThirdJsonReport : ConsoleCountReport
        {
        }
    }
}
