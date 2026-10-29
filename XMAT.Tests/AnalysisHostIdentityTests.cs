// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text;
using CaptureAnalysisEngine;
using XMAT.Models;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.XboxLiveCaptureAnalysis;
using XMAT.XboxLiveCaptureAnalysis.ReportModels.PerEndpointReport;

namespace XMAT.Tests
{
    // Analysis keeps captured endpoints, synthesized telemetry and physical-host provenance
    // distinct, so host actions are offered only for groups with one real host.
    public class AnalysisHostIdentityTests
    {
        internal const string ConsoleAddress = "10.0.0.1";
        private const string Cs1Host = "data-vef.xboxlive.com";
        private const string Cs2Host = "v10.events.data.microsoft.com";
        private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly EndpointKey GenuineInGameEvents = new(EndpointKind.Captured, "inGameEvents");
        private static readonly EndpointKey SyntheticInGameEvents = new(EndpointKind.SyntheticTelemetry, "inGameEvents");

        [Theory]
        [InlineData("CS1")]
        [InlineData("CS2")]
        [InlineData("Both")]
        public void GenuineInGameEventsHost_StaysSeparateFromSynthesizedTelemetry(string format)
        {
            var items = new List<ServiceCallItem> { Captured(0, "inGameEvents"), Captured(1, "inGameEvents") };
            var telemetryHosts = new List<string>();
            if (format != "CS2")
            {
                items.Add(Cs1Telemetry(2));
                telemetryHosts.Add(Cs1Host);
            }
            if (format != "CS1")
            {
                items.Add(Cs2Telemetry(3));
                telemetryHosts.Add(Cs2Host);
            }

            var data = new ServiceCallData(false);
            data.CreateFromServiceCallItems(items);
            ServiceCallData.PerConsoleData console = data.m_perConsoleData[ConsoleAddress];

            Assert.NotEqual(GenuineInGameEvents, SyntheticInGameEvents);
            var genuine = console.m_servicesHistory[GenuineInGameEvents];
            var synthetic = console.m_servicesHistory[SyntheticInGameEvents];
            Assert.Equal(2, genuine.Count);
            Assert.All(genuine, call => Assert.Equal(EndpointKind.Captured, call.EndpointKind));
            Assert.Equal(2 * telemetryHosts.Count, synthetic.Count);
            Assert.All(synthetic, call =>
            {
                Assert.Equal(EndpointKind.SyntheticTelemetry, call.EndpointKind);
                Assert.Contains(call.PhysicalHost, telemetryHosts);
            });
            Assert.Equal(2ul, console.m_servicesStats[GenuineInGameEvents].m_numCalls);
            Assert.Equal((ulong)(2 * telemetryHosts.Count), console.m_servicesStats[SyntheticInGameEvents].m_numCalls);
            Assert.DoesNotContain(console.m_servicesHistory.Keys, key => telemetryHosts.Contains(key.LogicalName));

            Assert.Equal("inGameEvents", ServiceCallData.GetActionableHost(GenuineInGameEvents, genuine));
            Assert.Null(ServiceCallData.GetActionableHost(SyntheticInGameEvents, synthetic));
        }

        [Fact]
        public void ActionableHost_RequiresEveryCapturedContributorToShareOneValidHost()
        {
            var key = new EndpointKey(EndpointKind.Captured, "api.example.com");

            // Provenance equality ignores DNS case but keeps each spelling's final dot.
            Assert.Equal("api.example.com", ServiceCallData.GetActionableHost(key, new[]
            {
                Captured(0, "api.example.com", "API.Example.com"),
                Captured(1, "api.example.com:8443", "api.example.com")
            }), ignoreCase: true);
            Assert.Equal("Api.Example.com.", ServiceCallData.GetActionableHost(key, new[]
            {
                Captured(0, "api.example.com", "Api.Example.com.")
            }));
            Assert.Null(ServiceCallData.GetActionableHost(key, new[]
            {
                Captured(0, "api.example.com", "api.example.com."),
                Captured(1, "api.example.com", "api.example.com")
            }));
            Assert.Equal("::1", ServiceCallData.GetActionableHost(key, new[]
            {
                Captured(0, "api.example.com", "::1"),
                Captured(1, "api.example.com", "0:0::1")
            }));
            Assert.Null(ServiceCallData.GetActionableHost(key, new[]
            {
                Captured(0, "api.example.com", "api.example.com"),
                Captured(1, "api.example.com", "other.example.com")
            }));
            Assert.Null(ServiceCallData.GetActionableHost(key, new[]
            {
                Captured(0, "api.example.com", "api.example.com"),
                Legacy(1, "api.example.com")
            }));
            Assert.Null(ServiceCallData.GetActionableHost(key, new[] { Captured(0, "api.example.com", "[::1") }));
            Assert.Null(ServiceCallData.GetActionableHost(key, Array.Empty<ServiceCallItem>()));
            Assert.Null(ServiceCallData.GetActionableHost(
                new EndpointKey(EndpointKind.Unknown, "api.example.com"), new[] { Captured(0, "api.example.com") }));
            Assert.Null(ServiceCallData.GetActionableHost(
                new EndpointKey(EndpointKind.SyntheticTelemetry, "api.example.com"), new[] { Captured(0, "api.example.com") }));
        }

        [Fact]
        public void ItemCopies_CarryKindAndPhysicalHost()
        {
            ServiceCallItem copy = Captured(0, "api.example.com:8443", "api.example.com").Copy();

            Assert.Equal(EndpointKind.Captured, copy.EndpointKind);
            Assert.Equal("api.example.com", copy.PhysicalHost);
            Assert.Equal(EndpointKind.Unknown, new ServiceCallItem().EndpointKind);
            Assert.Null(new ServiceCallItem().PhysicalHost);
        }

        [Theory]
        [InlineData("api.example.com", "other.example:8443", "api.example.com")]
        [InlineData("", "Example.COM:8443", "Example.COM")]
        [InlineData("Api.Example.Test.", "other.example", "Api.Example.Test.")]
        [InlineData("2001:DB8::1", "other.example", "2001:db8::1")]
        [InlineData("", "[2001:db8::1]:443", "2001:db8::1")]
        [InlineData("[", "api.example.com", "api.example.com")]
        [InlineData("", null, null)]
        [InlineData("", "[::1", null)]
        public void CapturedRecords_PreferStructuredHostProvenanceOverTheHostHeader(
            string structuredHost, string hostHeader, string expected)
        {
            ServiceCallItem item = XboxLiveCaptureAnalyzer.ProxyConnectionModelToServiceCallItem(
                new CaptureRecord(structuredHost, hostHeader));

            Assert.NotNull(item);
            Assert.Equal(EndpointKind.Captured, item.EndpointKind);
            Assert.Equal(expected, item.PhysicalHost);
        }

        // A host captured in a legacy spelling is analyzed like any other, but it is not a host
        // identity, so its endpoint never offers host actions.
        [Fact]
        public void LegacyCapturedHostSpelling_IsNeverActionable()
        {
            ServiceCallItem item = XboxLiveCaptureAnalyzer.ProxyConnectionModelToServiceCallItem(
                new CaptureRecord("Reg~Name.Example.Test", "Reg~Name.Example.Test:443"));

            Assert.NotNull(item);
            Assert.Equal(EndpointKind.Captured, item.EndpointKind);
            Assert.Equal("Reg~Name.Example.Test", item.PhysicalHost);
            Assert.Null(ServiceCallData.GetActionableHost(new EndpointKey(EndpointKind.Captured, item.Host), new[] { item }));
        }

        [Fact]
        public void PerEndpointReport_ProjectsDistinctKeysAndActionabilityIntoSummaryAndDetails()
        {
            PerEndpointReportDocument document = CreateInGameEventsReport();

            var rows = document.EndpointReportData.Where(row => row.UriService == "inGameEvents").ToList();
            Assert.Equal(2, rows.Count);
            EndpointReportData genuine = Assert.Single(rows, row => row.Kind == EndpointKind.Captured);
            EndpointReportData synthetic = Assert.Single(rows, row => row.Kind == EndpointKind.SyntheticTelemetry);
            Assert.Equal(GenuineInGameEvents, genuine.Key);
            Assert.Equal(SyntheticInGameEvents, synthetic.Key);
            Assert.Equal("inGameEvents", genuine.ActionableHost);
            Assert.Null(synthetic.ActionableHost);

            var app = new CaptureAppModel();
            var summary = new EndpointValidationIssuesSummary(app, "source", document);
            var details = new EndpointValidationIssuesDetails(app, "source", document);
            Assert.Equal(
                new[] { GenuineInGameEvents, SyntheticInGameEvents },
                summary.Endpoints.Select(endpoint => endpoint.Key).OrderBy(key => key.Kind));
            Assert.Equal(
                new[] { GenuineInGameEvents, SyntheticInGameEvents },
                details.Endpoints.Select(endpoint => endpoint.Key).OrderBy(key => key.Kind));
            Assert.Equal("inGameEvents", summary.Endpoints.Single(e => e.Key == GenuineInGameEvents).ActionableHost);
            Assert.Null(summary.Endpoints.Single(e => e.Key == SyntheticInGameEvents).ActionableHost);
            Assert.Equal("inGameEvents", details.Endpoints.Single(e => e.Key == GenuineInGameEvents).ActionableHost);
            Assert.Null(details.Endpoints.Single(e => e.Key == SyntheticInGameEvents).ActionableHost);
        }

        // A per-endpoint report with a genuine captured "inGameEvents" host and telemetry
        // synthesized from both formats under the same logical name, plus any other captured
        // hosts whose names no other endpoint shares.
        internal static PerEndpointReportDocument CreateInGameEventsReport(params string[] otherHosts)
        {
            Localization.LoadLanguage("en-us");
            var items = new List<ServiceCallItem>
            {
                Captured(0, "inGameEvents"),
                Captured(1, "inGameEvents"),
                Cs1Telemetry(2),
                Cs2Telemetry(3)
            };
            var map = new StringBuilder("\"inGameEvents\",\"events_service\",\"EventsService\",\"XblEvents\"\n");
            foreach (string host in otherHosts)
            {
                items.Add(Captured((uint)items.Count, host));
                map.Append($"\"{host}\",\"other_service\",\"OtherService\",\"XblOther\"\n");
            }
            var data = new ServiceCallData(false);
            data.CreateFromServiceCallItems(items);
            var analyzer = new TraceAnalyzer();
            using (var mapReader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(map.ToString()))))
            {
                analyzer.LoadURIMap(mapReader);
            }
            analyzer.AddRule(new TestRule { Name = "Identity", Endpoint = "*" });
            analyzer.AddReports(new Report[] { new PerEndpointReport() });
            analyzer.RunUriConverterOnData(data);

            var reports = analyzer.Run(new RulesEngine(false), data, null);
            return Assert.IsType<PerEndpointReportDocument>(Assert.Single(reports[ConsoleAddress]));
        }

        // Captured provenance keeps the host's spelling, as the capture converter records it.
        internal static ServiceCallItem Captured(uint id, string host, string physicalHost = null) => new(id)
        {
            ConsoleIP = ConsoleAddress,
            Host = host,
            PhysicalHost = physicalHost ?? host,
            EndpointKind = EndpointKind.Captured,
            Uri = $"https://{host}/path/{id}",
            Method = "GET",
            ReqTimeUTC = (ulong)Start.AddSeconds(id).ToFileTimeUtc(),
            ElapsedCallTimeMs = 10,
            ReqBody = string.Empty
        };

        private static ServiceCallItem Legacy(uint id, string host)
        {
            ServiceCallItem item = Captured(id, host);
            item.PhysicalHost = null;
            return item;
        }

        internal static ServiceCallItem Cs1Telemetry(uint id)
        {
            ServiceCallItem item = Captured(id, Cs1Host);
            item.Method = "POST";
            item.ReqBody = string.Join(
                Environment.NewLine,
                Cs1Event("PlayerSessionStart", 10),
                Cs1Event("PlayerSessionEnd", 11));
            return item;
        }

        private static string Cs1Event(string name, int second) => string.Join("|", new[]
        {
            "A", $"ABCD_12345678.{name}", Start.AddSeconds(second).ToString("o"),
            "3", "4", "5", "6", "7", "8", "9", "10", "11"
        });

        internal static ServiceCallItem Cs2Telemetry(uint id)
        {
            ServiceCallItem item = Captured(id, Cs2Host);
            item.Method = "POST";
            item.ReqBody = string.Join(
                "\n",
                Cs2Event("PlayerSessionStart", 20),
                Cs2Event("PlayerSessionEnd", 21));
            return item;
        }

        private static string Cs2Event(string name, int second) =>
            $"{{\"name\":\"Microsoft.XboxLive.T12345678.{name}\",\"time\":\"{Start.AddSeconds(second):o}\",\"data\":{{\"baseData\":{{}}}}}}";

        // A captured proxy row as stored in the capture table.
        private sealed class CaptureRecord(string structuredHost, string hostHeader) : IDataRecord
        {
            public int RowId => 1;

            public long Int(string fieldName) => 7;

            public string Str(string fieldName) => fieldName switch
            {
                WebServiceCaptureMethod.FieldKey_RequestTimestamp => Start.ToString("o"),
                WebServiceCaptureMethod.FieldKey_ResponseTimestamp => Start.AddMilliseconds(25).ToString("o"),
                WebServiceCaptureMethod.FieldKey_RequestScheme => "https",
                WebServiceCaptureMethod.FieldKey_RequestHost => structuredHost,
                WebServiceCaptureMethod.FieldKey_RequestPort => "443",
                WebServiceCaptureMethod.FieldKey_RequestMethod => "GET",
                WebServiceCaptureMethod.FieldKey_RequestPath => "https://api.example.com/users/me",
                WebServiceCaptureMethod.FieldKey_RequestStatus => "200",
                WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders =>
                    "GET https://api.example.com/users/me HTTP/1.1\r\n" +
                    (hostHeader == null ? string.Empty : $"Host: {hostHeader}\r\n") +
                    "x-xbl-api-build-version: adk\r\n",
                WebServiceCaptureMethod.FieldKey_ClientIP => ConsoleAddress,
                _ => string.Empty
            };
        }
    }
}
