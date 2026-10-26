// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections;
using System.Reflection;
using System.Resources;
using System.Text.Json;
using System.Text.RegularExpressions;
using CaptureAnalysisEngine;

namespace XMAT.Tests
{
    // The production Call, Stats and PerEndpoint reports keep a captured "inGameEvents" host
    // and the events synthesized from both telemetry formats under that name as separate
    // endpoint identities: in their documents, their serialized JSON and the shipped report
    // page's identity and display selectors.
    public class AnalysisReportIdentityTests
    {
        private const string InGameEvents = "inGameEvents";
        private const string OtherHost = "api.example.test";
        private static readonly EndpointKey Genuine = new(EndpointKind.Captured, InGameEvents);
        private static readonly EndpointKey Synthetic = new(EndpointKind.SyntheticTelemetry, InGameEvents);
        private static readonly EndpointKey Other = new(EndpointKind.Captured, OtherHost);

        [Fact]
        public void StatsReport_RecordsEachInGameEventsIdentityWithItsOwnCalls()
        {
            JsonElement[] stats = ProductionRun.Execute().Stats;

            JsonElement[] inGameEvents = WithUri(stats, InGameEvents);
            Assert.Equal(2, inGameEvents.Length);
            // Two genuine calls, two events from each telemetry format, and one other call.
            Assert.Equal(2ul, ForKind(inGameEvents, EndpointKind.Captured).GetProperty("Call Count").GetUInt64());
            Assert.Equal(4ul, ForKind(inGameEvents, EndpointKind.SyntheticTelemetry).GetProperty("Call Count").GetUInt64());
            Assert.Equal(1ul, ForKind(WithUri(stats, OtherHost), EndpointKind.Captured).GetProperty("Call Count").GetUInt64());
        }

        [Fact]
        public void CallReport_SeriesHoldOnlyTheCallsOfTheirIdentity()
        {
            ProductionRun run = ProductionRun.Execute();
            JsonElement[] inGameEvents = WithUri(run.CallEndpoints, InGameEvents);
            Assert.Equal(2, inGameEvents.Length);
            JsonElement genuine = ForKind(inGameEvents, EndpointKind.Captured);
            JsonElement synthetic = ForKind(inGameEvents, EndpointKind.SyntheticTelemetry);

            // Synthesized events are new items with their own IDs, so each series is compared
            // with the calls the analysis grouped under that identity.
            Assert.Equal(new uint[] { 0, 1 }, run.CallIds(Genuine));
            Assert.Equal(4, run.CallIds(Synthetic).Length);
            Assert.Empty(run.CallIds(Genuine).Intersect(run.CallIds(Synthetic)));
            Assert.Equal(run.CallIds(Genuine), CallIds(genuine));
            Assert.Equal(run.CallIds(Synthetic), CallIds(synthetic));
            Assert.Equal(new[] { InGameEvents }, CallHosts(genuine));
            Assert.Equal(new[] { "data-vef.xboxlive.com", "v10.events.data.microsoft.com" }, CallHosts(synthetic));
            Assert.Equal(
                run.CallIds(Other),
                CallIds(ForKind(WithUri(run.CallEndpoints, OtherHost), EndpointKind.Captured)));
        }

        // The shipped report_view.js keys count-graph categories and timeline series by its
        // identity selector and shows its display selection in ticks, legends and table rows.
        // The real producer JSON is checked against those declared selectors; the script is
        // inspected but not executed.
        [Fact]
        public void ReportConsumer_GroupsByStableIdentityAndDisplaysQualifiedUris()
        {
            ReportViewSelectors consumer = ReportViewSelectors.Load();
            ProductionRun run = ProductionRun.Execute();

            Assert.Matches(@"function endpointId\(endpoint\)\s*\{\s*return endpoint\[ENDPOINT_ID\];", consumer.Script);
            Assert.Matches(@"push\(\[\s*stat\[""Call Count""\]\s*,\s*endpointId\(stat\)\s*\]\)", consumer.Script);
            Assert.Matches(@"\bid:\s*endpointId\(endpoint\)", consumer.Script);
            Assert.Matches(@"if \(api === ""Uri"" && endpoint\[ENDPOINT_LABEL\]\)", consumer.Script);
            Assert.DoesNotMatch(@"\b(stat|endpoint)\[API\]", consumer.Script);

            foreach (JsonElement[] records in new[] { run.Stats, run.CallEndpoints })
            {
                AssertConsumerIdentity(records, consumer);
                AssertConsumerDisplay(records, consumer);
            }
            Assert.Equal(
                run.Stats.Select(consumer.IdOf).OrderBy(id => id, StringComparer.Ordinal),
                run.CallEndpoints.Select(consumer.IdOf).OrderBy(id => id, StringComparer.Ordinal));
            Assert.Contains(Genuine.Id, run.Stats.Select(consumer.IdOf));
            Assert.Contains(Synthetic.Id, run.Stats.Select(consumer.IdOf));
        }

        [Fact]
        public void PerEndpointReport_KeepsTheSameIdentitiesInTheProductionPipeline()
        {
            var document = Assert.IsType<PerEndpointReportDocument>(
                ProductionRun.Execute().Reports[typeof(PerEndpointReportDocument)]);

            Assert.Equal("2", TotalCalls(document, EndpointKind.Captured));
            Assert.Equal("4", TotalCalls(document, EndpointKind.SyntheticTelemetry));
            JsonElement[] endpoints = Json(document).GetProperty("EndpointReportData").EnumerateArray()
                .Where(endpoint => endpoint.GetProperty("UriService").GetString() == InGameEvents)
                .ToArray();
            Assert.Equal(2, endpoints.Length);
            ForKind(endpoints, EndpointKind.Captured);
            ForKind(endpoints, EndpointKind.SyntheticTelemetry);
        }

        // Results recorded without an endpoint identity stay Unknown; nothing is inferred
        // from their display name.
        [Fact]
        public void ResultsWithoutEndpointIdentity_StayUnknownInCallAndStatsJson()
        {
            Localization.LoadLanguage("en-us");
            var calls = new List<ServiceCallItem> { AnalysisHostIdentityTests.Captured(0, OtherHost) };
            var statsResult = new RuleResult(StatsRecorderRule.DisplayName, OtherHost, string.Empty);
            statsResult.Results.Add(StatsRecorderRule.StatsDataKey, new ServiceCallStats(calls));
            var callsResult = new RuleResult(CallRecorderRule.DisplayName, OtherHost, string.Empty);
            callsResult.Results.Add(CallRecorderRule.CallsDataKey, calls.AsEnumerable());
            var endpoints = new Dictionary<string, Tuple<string, string, string>>();

            JsonElement stat = Assert.Single(
                Json(new StatsReport().RunReport(new[] { statsResult }, endpoints)).EnumerateArray());
            JsonElement endpoint = Assert.Single(
                Json(new CallReport().RunReport(new[] { callsResult }, endpoints)).GetProperty("Call List").EnumerateArray());

            Assert.Equal(OtherHost, stat.GetProperty("Uri").GetString());
            Assert.Equal(EndpointKind.Unknown, KindOf(stat));
            Assert.Equal(OtherHost, endpoint.GetProperty("Uri").GetString());
            Assert.Equal(EndpointKind.Unknown, KindOf(endpoint));
            ReportViewSelectors consumer = ReportViewSelectors.Load();
            string unknown = new EndpointKey(EndpointKind.Unknown, OtherHost).Id;
            Assert.Equal(unknown, consumer.IdOf(stat));
            Assert.Equal(unknown, consumer.IdOf(endpoint));
            Assert.Equal(OtherHost, consumer.DisplayOf(stat));
        }

        // Requires each record's consumer identity to be unique and derived from the record's
        // own kind and logical name rather than from any display text.
        private static void AssertConsumerIdentity(JsonElement[] records, ReportViewSelectors consumer)
        {
            string[] ids = records.Select(consumer.IdOf).ToArray();
            Assert.True(
                ids.Distinct(StringComparer.Ordinal).Count() == ids.Length,
                $"Report consumer identities collide: {string.Join(" | ", ids)}");
            foreach (JsonElement record in records)
            {
                Assert.Equal(
                    new EndpointKey(KindOf(record), record.GetProperty("Uri").GetString()).Id,
                    consumer.IdOf(record));
            }
        }

        // Displayed text keeps the human-readable Uri, and only a Uri shared by several
        // identities is qualified by each identity's localized kind.
        private static void AssertConsumerDisplay(JsonElement[] records, ReportViewSelectors consumer)
        {
            string[] uris = records.Select(record => record.GetProperty("Uri").GetString()).ToArray();
            string[] texts = records.Select(consumer.DisplayOf).ToArray();
            Assert.True(
                texts.Distinct(StringComparer.Ordinal).Count() == texts.Length,
                $"Displayed endpoint text collides: {string.Join(" | ", texts)}");
            for (int index = 0; index < records.Length; index++)
            {
                Assert.DoesNotContain("NO_IMPL_", texts[index]);
                if (uris.Count(uri => uri == uris[index]) == 1)
                {
                    Assert.Equal(uris[index], texts[index]);
                }
                else
                {
                    Assert.Contains(uris[index], texts[index]);
                    Assert.Contains(KindText(KindOf(records[index])), texts[index]);
                }
            }
        }

        private static string KindText(EndpointKind kind) => Localization.GetLocalizedString(kind switch
        {
            EndpointKind.Captured => "LTA_ENDPOINT_KIND_CAPTURED",
            EndpointKind.SyntheticTelemetry => "LTA_ENDPOINT_KIND_SYNTHETIC",
            _ => "LTA_ENDPOINT_KIND_UNKNOWN"
        });

        private static string ProductionData(string fileName)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Data", fileName);
            Assert.True(File.Exists(path), $"Production analysis data is missing: {path}");
            return path;
        }

        private static JsonElement Json(ReportDocument document) =>
            JsonSerializer.Deserialize<JsonElement>(document.ToJson());

        private static JsonElement[] WithUri(IEnumerable<JsonElement> items, string uri) =>
            items.Where(item => item.GetProperty("Uri").GetString() == uri).ToArray();

        private static JsonElement ForKind(IEnumerable<JsonElement> items, EndpointKind kind) =>
            Assert.Single(items, item => KindOf(item) == kind);

        private static EndpointKind KindOf(JsonElement item)
        {
            Assert.True(
                item.TryGetProperty("Kind", out JsonElement kind),
                $"The report record does not carry its endpoint identity: {item}");
            return kind.ValueKind == JsonValueKind.String
                ? Enum.Parse<EndpointKind>(kind.GetString())
                : (EndpointKind)kind.GetInt32();
        }

        private static uint[] CallIds(JsonElement endpoint) =>
            endpoint.GetProperty("Calls").EnumerateArray()
                .Select(call => call.GetProperty("Id").GetUInt32())
                .OrderBy(id => id)
                .ToArray();

        // Hosts of the recorded call URIs, as captured (System.Uri would lowercase them).
        private static string[] CallHosts(JsonElement endpoint) =>
            endpoint.GetProperty("Calls").EnumerateArray()
                .Select(call => call.GetProperty("Uri").GetString())
                .Select(uri => uri[(uri.IndexOf("://", StringComparison.Ordinal) + 3)..].Split('/')[0])
                .Distinct(StringComparer.Ordinal)
                .OrderBy(host => host, StringComparer.Ordinal)
                .ToArray();

        private static string TotalCalls(PerEndpointReportDocument document, EndpointKind kind)
        {
            EndpointReportData endpoint = Assert.Single(
                document.EndpointReportData,
                candidate => candidate.UriService == InGameEvents && candidate.Kind == kind);
            RuleReportData rule = Assert.Single(
                endpoint.RuleReportData,
                candidate => candidate.Name == CallFrequencyRule.DisplayName);
            return rule.ResultData[CallFrequencyRule.TotalCallsDataKey];
        }

        // The reports, rules and API map that XboxLiveCaptureAnalyzer.Initialize configures, run
        // in memory (no report files) on a genuine "inGameEvents" host, both telemetry formats
        // and one other host from the same console.
        private sealed class ProductionRun
        {
            private readonly Dictionary<EndpointKey, LinkedList<ServiceCallItem>> _history;

            private ProductionRun(
                Dictionary<Type, ReportDocument> reports,
                Dictionary<EndpointKey, LinkedList<ServiceCallItem>> history)
            {
                Reports = reports;
                _history = history;
            }

            public Dictionary<Type, ReportDocument> Reports { get; }

            public JsonElement[] Stats => Json(Reports[typeof(StatsReportDocument)]).EnumerateArray().ToArray();

            public JsonElement[] CallEndpoints =>
                Json(Reports[typeof(CallReportDocument)]).GetProperty("Call List").EnumerateArray().ToArray();

            // The IDs of the calls the analysis grouped under one endpoint identity.
            public uint[] CallIds(EndpointKey endpoint) =>
                _history[endpoint].Select(call => call.Id).OrderBy(id => id).ToArray();

            public static ProductionRun Execute()
            {
                Localization.LoadLanguage("en-us");
                var data = new ServiceCallData(false);
                data.CreateFromServiceCallItems(new List<ServiceCallItem>
                {
                    AnalysisHostIdentityTests.Captured(0, InGameEvents),
                    AnalysisHostIdentityTests.Captured(1, InGameEvents),
                    AnalysisHostIdentityTests.Cs1Telemetry(2),
                    AnalysisHostIdentityTests.Cs2Telemetry(3),
                    AnalysisHostIdentityTests.Captured(4, OtherHost)
                });
                var analyzer = new TraceAnalyzer();
                analyzer.LoadURIMap(ProductionData("XboxLiveTraceAnalyzer.APIMap.csv"));
                analyzer.LoadRules(ProductionData("XboxLiveTraceAnalyzer.Rules.json"));
                analyzer.AddReports(new Report[] { new PerEndpointReport(), new CallReport(), new StatsReport() });
                analyzer.RunUriConverterOnData(data);

                Dictionary<string, IEnumerable<ReportDocument>> reports = analyzer.Run(new RulesEngine(false), data, null);
                Assert.Equal(new[] { AnalysisHostIdentityTests.ConsoleAddress }, reports.Keys);
                return new ProductionRun(
                    reports[AnalysisHostIdentityTests.ConsoleAddress].ToDictionary(report => report.GetType()),
                    data.m_perConsoleData[AnalysisHostIdentityTests.ConsoleAddress].m_servicesHistory);
            }
        }

        // The selectors that the shipped report_view.js declares, and its display rule: the
        // collision label for the Uri selector, otherwise the selected field or the Uri.
        private sealed class ReportViewSelectors
        {
            private ReportViewSelectors(string script)
            {
                Script = script;
                Display = Declared(script, "API");
                Id = Declared(script, "ENDPOINT_ID");
                Label = Declared(script, "ENDPOINT_LABEL");
            }

            public string Script { get; }
            public string Display { get; }
            public string Id { get; }
            public string Label { get; }

            public string IdOf(JsonElement record)
            {
                Assert.True(
                    record.TryGetProperty(Id, out JsonElement id) && !string.IsNullOrEmpty(id.GetString()),
                    $"The report record has no '{Id}' identity for the report page: {record}");
                return id.GetString();
            }

            public string DisplayOf(JsonElement record)
            {
                if (Display == "Uri" && Text(record, Label) is string label)
                {
                    return label;
                }
                return Text(record, Display) ?? record.GetProperty("Uri").GetString();
            }

            public static ReportViewSelectors Load()
            {
                Assembly product = typeof(StatsReportDocument).Assembly;
                using Stream resources = product.GetManifestResourceStream(product.GetName().Name + ".g.resources");
                Assert.NotNull(resources);
                using var set = new ResourceSet(resources);
                foreach (DictionaryEntry entry in set)
                {
                    if (entry.Key is string name &&
                        name.EndsWith("html/xlta/report_view.js", StringComparison.OrdinalIgnoreCase))
                    {
                        using var reader = new StreamReader((Stream)entry.Value);
                        return new ReportViewSelectors(reader.ReadToEnd());
                    }
                }
                Assert.Fail("report_view.js is not packaged with the product.");
                return null;
            }

            private static string Text(JsonElement record, string field) =>
                record.TryGetProperty(field, out JsonElement value) && value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrEmpty(value.GetString())
                    ? value.GetString()
                    : null;

            private static string Declared(string script, string name)
            {
                Match declaration = Regex.Match(
                    script,
                    "^\\s*" + name + "\\s*=\\s*\"(?<field>[^\"]+)\"\\s*;",
                    RegexOptions.Multiline);
                Assert.True(declaration.Success, $"report_view.js does not declare {name}.");
                return declaration.Groups["field"].Value;
            }
        }
    }
}
