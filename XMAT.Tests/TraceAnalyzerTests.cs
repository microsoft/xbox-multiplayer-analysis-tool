// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text;
using CaptureAnalysisEngine;

namespace XMAT.Tests
{
    public class TraceAnalyzerTests
    {
        private static readonly Encoding Utf8NoBom =
            new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true);
        private const string SerializationFailure =
            "report serialization failed";
        private const string CleanupFailure =
            "staging cleanup failed";

        [Fact]
        public void Run_WritesEveryJsonReportForFirstEligibleConsoleOnly()
        {
            string outputDirectory =
                AnalysisReportTestPaths.CreateOwnedDirectory("xtr");
            try
            {
                File.WriteAllText(
                    Path.Join(
                        outputDirectory,
                        "FirstJsonReport.json"),
                    "pre-existing");
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
                    report =>
                    {
                        Assert.Equal(
                            1,
                            ReportCallCount(
                                report.ToJson()));
                        Assert.True(
                            Assert.IsType<
                                ConsoleCountReportDocument>(
                                    report)
                                .WriteJsonCalled);
                    });
                byte[] expected =
                    Utf8NoBom.GetBytes(
                        reports["first-console"]
                            .First()
                            .ToJson());
                foreach (string fileName in
                    new[]
                    {
                        "FirstJsonReport.json",
                        "SecondJsonReport.json",
                        "ThirdJsonReport.json",
                    })
                {
                    string path =
                        Path.Join(
                            outputDirectory,
                            fileName);
                    Assert.Equal(
                        expected,
                        File.ReadAllBytes(path));
                    Assert.Equal(
                        1,
                        ReportCallCount(
                            File.ReadAllText(path)));
                }
                Assert.Empty(
                    Directory.GetFiles(
                        outputDirectory,
                        "*.tmp"));
            }
            finally
            {
                AnalysisReportTestPaths.DeleteOwnedDirectory(
                    outputDirectory);
            }
        }

        [Fact]
        public void BuiltInDocuments_StreamTheSameUtf8BytesAsToJson()
        {
            Localization.LoadLanguage("en-us");

            foreach (ReportDocument document in
                BuiltInDocuments())
            {
                byte[] expected =
                    Utf8NoBom.GetBytes(
                        document.ToJson());
                using var output =
                    new MemoryStream();

                document.WriteJson(output);

                Assert.Equal(
                    expected,
                    output.ToArray());
            }
        }

        [Fact]
        public void BuiltInStreamingOverrides_DoNotCallVirtualToJson()
        {
            var call =
                new ThrowingCallReportDocument();
            var stats =
                new ThrowingStatsReportDocument();
            var endpoint =
                new ThrowingPerEndpointReportDocument();
            var warning =
                new ThrowingCertWarningReportDocument();
            var documents =
                new (
                    ReportDocument Document,
                    Func<string> Serialize)[]
                {
                    (
                        call,
                        call.SerializeWithoutVirtualToJson),
                    (
                        stats,
                        stats.SerializeWithoutVirtualToJson),
                    (
                        endpoint,
                        endpoint.SerializeWithoutVirtualToJson),
                    (
                        warning,
                        warning.SerializeWithoutVirtualToJson),
                };

            foreach (var testCase in documents)
            {
                byte[] expected =
                    Utf8NoBom.GetBytes(
                        testCase.Serialize());
                Assert.Throws<
                    InvalidOperationException>(
                        () =>
                            testCase.Document
                                .ToJson());
                using var output =
                    new MemoryStream();

                testCase.Document.WriteJson(
                    output);

                Assert.Equal(
                    expected,
                    output.ToArray());
            }
        }

        [Fact]
        public void RunReports_SerializationFailurePreservesExistingReport()
        {
            string outputDirectory =
                AnalysisReportTestPaths
                    .CreateOwnedDirectory(
                        "xrf");
            try
            {
                string reportPath =
                    Path.Join(
                        outputDirectory,
                        "ThrowingJsonReport.json");
                byte[] original =
                    Utf8NoBom.GetBytes(
                        "pre-existing é雪");
                File.WriteAllBytes(
                    reportPath,
                    original);
                var analyzer =
                    CreateThrowingReportAnalyzer();

                AggregateException exception =
                    Assert.Throws<AggregateException>(
                        () => analyzer.RunReports(
                                Array.Empty<RuleResult>(),
                                EmptyReportData(),
                                outputDirectory)
                            .ToList());

                AssertSerializationFailure(
                    exception);
                Assert.Equal(
                    original,
                    File.ReadAllBytes(reportPath));
                Assert.Empty(
                    ReportStagingFiles(
                        outputDirectory));
            }
            finally
            {
                AnalysisReportTestPaths
                    .DeleteOwnedDirectory(
                        outputDirectory);
            }
        }

        [Fact]
        public void RunReports_CleanupFailureDoesNotMaskSerializationFailure()
        {
            string outputDirectory =
                AnalysisReportTestPaths
                    .CreateOwnedDirectory(
                        "xrc");
            try
            {
                var analyzer =
                    CreateThrowingReportAnalyzer();
                analyzer.StagingFileDelete =
                    _ => throw new IOException(
                        CleanupFailure);

                AggregateException exception =
                    Assert.Throws<AggregateException>(
                        () => analyzer.RunReports(
                                Array.Empty<RuleResult>(),
                                EmptyReportData(),
                                outputDirectory)
                            .ToList());

                AssertSerializationFailure(
                    exception);
                Assert.DoesNotContain(
                    exception
                        .Flatten()
                        .InnerExceptions,
                    inner =>
                        inner.Message ==
                        CleanupFailure);
                string stagingFile =
                    Assert.Single(
                        ReportStagingFiles(
                            outputDirectory));
                File.Delete(stagingFile);
            }
            finally
            {
                AnalysisReportTestPaths
                    .DeleteOwnedDirectory(
                        outputDirectory);
            }
        }

        [Fact]
        public void RunReports_CancellationPreservesExistingReportAndCleansStaging()
        {
            string outputDirectory =
                AnalysisReportTestPaths
                    .CreateOwnedDirectory(
                        "xrn");
            using var cancellation =
                new CancellationTokenSource();
            try
            {
                string reportPath =
                    Path.Join(
                        outputDirectory,
                        "CancellingJsonReport.json");
                byte[] original =
                    Utf8NoBom.GetBytes(
                        "pre-existing cancellation");
                File.WriteAllBytes(
                    reportPath,
                    original);
                var analyzer =
                    new TraceAnalyzer();
                analyzer.AddReports(
                    new Report[]
                    {
                        new CancellingJsonReport(
                            cancellation.Cancel),
                    });

                OperationCanceledException exception =
                    Assert.ThrowsAny<
                        OperationCanceledException>(
                            () => analyzer.RunReports(
                                    Array.Empty<
                                        RuleResult>(),
                                    EmptyReportData(),
                                    outputDirectory,
                                    cancellation.Token)
                                .ToList());

                Assert.Equal(
                    cancellation.Token,
                    exception.CancellationToken);
                Assert.Equal(
                    original,
                    File.ReadAllBytes(
                        reportPath));
                Assert.Empty(
                    ReportStagingFiles(
                        outputDirectory,
                        "CancellingJsonReport"));
            }
            finally
            {
                AnalysisReportTestPaths
                    .DeleteOwnedDirectory(
                        outputDirectory);
            }
        }

        private static IEnumerable<ReportDocument>
            BuiltInDocuments()
        {
            const string Text =
                "é雪 \"quoted\" \\\\ line\n\t\u0001";

            var callReport =
                new CallReportDocument
                {
                    StartTime = 1,
                    EndTime = 2,
                };
            var endpoint =
                new CallReportDocument.Endpoint
                {
                    Uri = Text,
                    EndpointFunctionCall = Text,
                    Kind = EndpointKind.Captured,
                    EndpointId = Text,
                    Label = Text,
                };
            endpoint.Calls.Add(
                new CallReportDocument.Endpoint.Call
                {
                    Id = 1,
                    ReqTime = 1.5,
                    Uri = Text,
                    EndpointFunctionCall = Text,
                    ReqBody =
                        "body \"quoted\"\n\t\u0001 é雪",
                });
            callReport.CallEndpoints.Add(endpoint);

            var statsReport =
                new StatsReportDocument();
            statsReport.StatsResults.Add(
                new StatsReportDocument.Host
                {
                    Uri = Text,
                    EndpointFunctionCall = Text,
                    CallCount = 3,
                    AvgTimeBetweenCallsInMs = 4,
                    Kind = EndpointKind.SyntheticTelemetry,
                    EndpointId = Text,
                    Label = Text,
                });

            var endpointReport =
                new PerEndpointReportDocument();
            endpointReport.Metadata.Result = Text;
            var endpointData =
                new EndpointReportData
                {
                    UriService = Text,
                    EndpointFunctionCall = Text,
                    Kind = EndpointKind.Captured,
                    ActionableHost = Text,
                };
            endpointData.Metadata.Result = Text;
            var ruleData =
                new RuleReportData
                {
                    Name = Text,
                    Description = Text,
                };
            ruleData.Metadata.Result = Text;
            ruleData.ResultData.Add(
                Text,
                Text);
            var violation =
                new RuleViolationData
                {
                    Level = Text,
                    Summary = Text,
                };
            violation.CallData.Add(
                new ViolationCallData
                {
                    Id = 5,
                    UriMethod = Text,
                    EndpointFunctionCall = Text,
                });
            ruleData.Violations.Add(violation);
            endpointData.RuleReportData.Add(ruleData);
            endpointReport.EndpointReportData.Add(
                endpointData);

            var warningReport =
                new CertWarningReportDocument();
            warningReport.WarningResults.Add(
                new CertWarningReportDocument.CertWarning
                {
                    XRName = Text,
                    Requirement = Text,
                    Remark = Text,
                    Intent = Text,
                });

            return new ReportDocument[]
            {
                callReport,
                statsReport,
                endpointReport,
                warningReport,
            };
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

        private static TraceAnalyzer
            CreateThrowingReportAnalyzer()
        {
            var analyzer =
                new TraceAnalyzer();
            analyzer.AddReports(
                new Report[]
                {
                    new ThrowingJsonReport(),
                });
            return analyzer;
        }

        private static ServiceCallData EmptyReportData() =>
            new(false)
            {
                m_endpointToService =
                    new Dictionary<
                        string,
                        Tuple<string, string, string>>(),
            };

        private static string[] ReportStagingFiles(
            string outputDirectory,
            string reportName =
                "ThrowingJsonReport") =>
            Directory.GetFiles(
                outputDirectory,
                reportName +
                    ".json.*.tmp");

        private static void AssertSerializationFailure(
            AggregateException exception)
        {
            Assert.Contains(
                exception
                    .Flatten()
                    .InnerExceptions,
                inner =>
                    inner is
                        InvalidOperationException &&
                    inner.Message ==
                        SerializationFailure);
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

            internal bool WriteJsonCalled { get; private set; }

            public override string ToJson()
            {
                return JsonSerializer.Serialize(new
                {
                    CallCount
                });
            }

            public override void WriteJson(
                Stream output)
            {
                WriteJsonCalled = true;
                base.WriteJson(output);
            }
        }

        private sealed class ThrowingCallReportDocument :
            CallReportDocument
        {
            internal string
                SerializeWithoutVirtualToJson() =>
                base.ToJson();

            public override string ToJson() =>
                throw new InvalidOperationException(
                    SerializationFailure);
        }

        private sealed class ThrowingStatsReportDocument :
            StatsReportDocument
        {
            internal string
                SerializeWithoutVirtualToJson() =>
                base.ToJson();

            public override string ToJson() =>
                throw new InvalidOperationException(
                    SerializationFailure);
        }

        private sealed class
            ThrowingPerEndpointReportDocument :
            PerEndpointReportDocument
        {
            internal string
                SerializeWithoutVirtualToJson() =>
                base.ToJson();

            public override string ToJson() =>
                throw new InvalidOperationException(
                    SerializationFailure);
        }

        private sealed class
            ThrowingCertWarningReportDocument :
            CertWarningReportDocument
        {
            internal string
                SerializeWithoutVirtualToJson() =>
                base.ToJson();

            public override string ToJson() =>
                throw new InvalidOperationException(
                    SerializationFailure);
        }

        private sealed class ThrowingJsonReportDocument :
            ReportDocument
        {
            public override string ToJson() =>
                throw new InvalidOperationException(
                    SerializationFailure);
        }

        private sealed class ThrowingJsonReport :
            Report
        {
            public override ReportDocument RunReport(
                IEnumerable<RuleResult> results,
                Dictionary<
                    string,
                    Tuple<string, string, string>>
                    endpointsMap) =>
                new ThrowingJsonReportDocument();
        }

        private sealed class
            CancellingJsonReportDocument :
            ReportDocument
        {
            private readonly Action _cancel;

            internal CancellingJsonReportDocument(
                Action cancel)
            {
                _cancel = cancel;
            }

            public override string ToJson()
            {
                _cancel();
                return "{\"cancelled\":true}";
            }
        }

        private sealed class CancellingJsonReport :
            Report
        {
            private readonly Action _cancel;

            internal CancellingJsonReport(
                Action cancel)
            {
                _cancel = cancel;
            }

            public override ReportDocument RunReport(
                IEnumerable<RuleResult> results,
                Dictionary<
                    string,
                    Tuple<string, string, string>>
                    endpointsMap) =>
                new CancellingJsonReportDocument(
                    _cancel);
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
