// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using CaptureAnalysisEngine;

namespace XMAT.Tests.Analysis.Rules
{
    // The product PollingDetectionRule and RepeatedCallsRule must report exactly what their frozen oracles
    // (RuleOracles.cs) report: the same violations in the same order, with the same levels, descriptions and calls, and
    // the same result data, whether the calls are in request time order (a host's calls) or listed out of it (in-game
    // events), and whatever their ids. The boundary tests pin the behavior that any optimization of the rules has to
    // keep.
    public class RepeatedCallsAndPollingDifferentialTests
    {
        private const long Second = TimeSpan.TicksPerSecond;
        private const long Millisecond = TimeSpan.TicksPerMillisecond;
        private const long Hour = TimeSpan.TicksPerHour;
        private const string ConsoleAddress = "10.0.0.2";
        private const string InGameEvents = "inGameEvents";
        private const string TelemetryHost = "data-vef.xboxlive.com";

        private static readonly CallKey A = new($"https://{RuleRuns.Endpoint}/users/xuid(1)/devices/current", 11);
        private static readonly CallKey B = new($"https://{RuleRuns.Endpoint}/users/xuid(2)/devices/current", 11);
        private static readonly CallKey C = new($"https://{RuleRuns.Endpoint}/users/xuid(3)/devices/current", 13);
        // Same URI as A with another body, so a separate repeated call.
        private static readonly CallKey AOtherBody = new(A.Uri, 12);

        private static readonly Dictionary<string, Func<List<ServiceCallItem>>> StructuredCases = new()
        {
            ["empty"] = () => new Capture().Build(),
            ["single-call"] = () => new Capture().Add(A, 0).Build(),
            ["two-identical"] = () => new Capture().Every(A, 0, Second, 2).Build(),
            ["three-identical"] = () => new Capture().Every(A, 0, Second, 3).Build(),
            ["five-identical-below-the-minimum-sequence"] = () => new Capture().Every(A, 0, Second, 5).Build(),
            ["six-identical-the-minimum-sequence"] = () => new Capture().Every(A, 0, Second, 6).Build(),
            ["identical-every-100-ms"] = () => new Capture().Every(A, 0, 100 * Millisecond, 120).Build(),
            ["polling-every-30-s"] = () => new Capture().Every(A, 0, 30 * Second, 40).Build(),
            ["jitter-within-1-percent"] = () => new Capture().Jittered(A, 0, 10 * Second, 40, 0.009, seed: 7).Build(),
            ["jitter-beyond-1-percent"] = () => new Capture().Jittered(A, 0, 10 * Second, 40, 0.03, seed: 11).Build(),
            ["intervals-around-1-percent"] = () => new Capture()
                .Intervals(A, 0, 10, 10, 10.1, 10, 9.9, 10, 10.099, 10, 9.901, 10, 10.11, 10, 9.89, 10, 10, 10, 10)
                .Build(),
            ["interleaved-keys"] = () => new Capture()
                .Every(A, 0, 2 * Second, 60)
                .Every(B, 300 * Millisecond, 3 * Second, 40)
                .Every(AOtherBody, 700 * Millisecond, 5 * Second, 24)
                .Every(C, 1100 * Millisecond, 7 * Second, 18)
                .Build(),
            ["ties-in-time"] = () => new Capture()
                .Add(A, 0).Add(A, 0).Add(A, 0)
                .Every(A, Second, Second, 8)
                .Add(A, 3 * Second).Add(A, 3 * Second)
                .Add(B, 3 * Second)
                .Every(B, 0, Second, 6)
                .Build(),
            ["first-call-exclusion"] = () => new Capture()
                .Add(A, 0)
                .Every(A, 10 * Second, Second, 5)
                .Every(B, 0, Second, 6)
                .Build(),
            ["one-hour-window-edges"] = OneHourWindowEdges,
            ["cadences-near-one-hour"] = () => new Capture()
                .Every(A, 0, 50 * TimeSpan.TicksPerMinute, 8)
                .Every(B, 0, Hour - Second, 8)
                .Every(C, 0, Hour + TimeSpan.TicksPerMinute, 8)
                .Build(),
            ["repeated-window-edges"] = RepeatedWindowEdges,
            ["subsets-and-supersets"] = () => new Capture()
                .Every(A, 0, 2 * Second, 21)
                .Every(A, Second, 2 * Second, 5)
                .Every(B, 0, 7 * Second, 18)
                .Every(B, 3 * Second, 11 * Second, 12)
                .Build(),
            ["equal-length-sequences"] = EqualLengthSequences,
            ["shoulder-taps"] = () =>
            {
                var capture = new Capture();
                for (var index = 0; index < 30; index++)
                {
                    capture.Add(A, index * Second, shoulderTap: index % 3 == 2);
                }

                return capture.Add(B, 0, shoulderTap: true).Add(B, 0, shoulderTap: true)
                    .Every(B, 100 * Millisecond, 100 * Millisecond, 10)
                    .Build();
            },
            ["many-distinct-keys"] = () =>
            {
                var capture = new Capture();
                for (var key = 0; key < 30; key++)
                {
                    var distinct = new CallKey($"https://{RuleRuns.Endpoint}/titles/{key}", (ulong)(key % 3));
                    capture.Every(distinct, key * 37 * Millisecond, 1500 * Millisecond, (key % 3) + 1);
                }

                return capture.Build();
            },
            // Capture order (the ids) differs from request time order, as when a capture's frames are out of order.
            ["capture-order-differs-from-time-order"] = () => new Capture()
                .Every(A, 10 * Second, Second, 6)
                .Every(A, 0, Second, 6)
                .Every(B, 4 * Second, 2 * Second, 6)
                .Every(B, 0, 500 * Millisecond, 6)
                .Build(idsInInsertionOrder: true),
            ["dense-burst-every-50-ms"] = () => new Capture()
                .Every(A, 0, 50 * Millisecond, 150)
                .Every(B, 25 * Millisecond, 3 * Second, 20)
                .Build(),
        };

        // Captures whose request numbers (the ids) repeat. Polling looks up the calls that can follow a call by the id
        // of its first call and ends a subset check at a larger id, so calls that share an id meet there.
        private static readonly Dictionary<string, Func<List<ServiceCallItem>>> DuplicateIdCases = new()
        {
            ["each-request-number-twice"] = () => Renumbered(
                new Capture().Every(A, 0, Second, 24).Every(B, 500 * Millisecond, 2 * Second, 12).Build(),
                index => (uint)((index / 2) + 1)),
            ["one-request-number"] = () => Renumbered(
                new Capture().Every(A, 0, 10 * Second, 30).Every(A, 3 * Second, 10 * Second, 30).Build(),
                _ => 7u),
            // Request numbers that start again, as in two captures put together.
            ["request-numbers-start-again"] = () => Renumbered(
                new Capture().Every(A, 0, 5 * Second, 20).Every(A, 200 * Second, 5 * Second, 20).Build(),
                index => (uint)((index % 20) + 1)),
        };

        // Calls listed out of request time order, as the engine passes in-game events (the inGameEvents endpoint) to a
        // rules file's "*" rules. The rules must report what the oracles report for the list as given, in which the
        // first listed call of a repeated call is the one that polling leaves out. Ids follow the list unless a case
        // gives others.
        private static readonly Dictionary<string, Func<List<ServiceCallItem>>> ListedCases = new()
        {
            ["cadence-with-late-and-early-calls"] = CadenceWithLateAndEarlyCalls,
            ["later-batch-listed-first"] = () => new Capture()
                .Every(A, 100 * Second, 10 * Second, 10)
                .Every(A, 0, 10 * Second, 10)
                .Build(idsInInsertionOrder: true),
            ["cadences-listed-backwards"] = () => new Capture()
                .Every(A, 11 * Second, -Second, 12)
                .Every(B, 5 * Second, -500 * Millisecond, 10)
                .Build(idsInInsertionOrder: true),
            // Calls at one time listed apart, among a 1 s cadence and calls less than 5 s apart either way.
            ["ties-listed-apart"] = () => new Capture()
                .At(A, 5, 1, 5, 2, 5, 3, 4, 5, 6, 7, 8, 5, 9, 10)
                .At(B, 3, 3.5, 3, 9, 4, 3.5, 120, 7, 1, 3)
                .Build(idsInInsertionOrder: true),
            ["keys-interleaved-out-of-order"] = () => new Capture()
                .At(A, 30, 0, 10, 20, 40, 50, 60, 70)
                .At(B, 2, 0, 4, 6, 8, 10, 12, 14, 16)
                .At(A, 80, 90, 100, 15)
                .At(AOtherBody, 7, 1, 2, 3, 4, 5, 6)
                .Build(idsInInsertionOrder: true),
            ["ids-in-time-order"] = () => IdsInTimeOrder(CadenceWithLateAndEarlyCalls()),
            ["ids-falling-along-the-list"] = () =>
                Renumbered(CadenceWithLateAndEarlyCalls(), index => (uint)(100 - index)),
            ["ids-shuffled"] = () => ShuffledIds(CadenceWithLateAndEarlyCalls(), new Random(5)),
            ["ids-repeated"] = () => Renumbered(CadenceWithLateAndEarlyCalls(), index => (uint)((index % 4) + 1)),
        };

        public static TheoryData<string> StructuredCaseNames()
        {
            var names = new TheoryData<string>();
            foreach (var name in StructuredCases.Keys)
            {
                names.Add(name);
            }

            return names;
        }

        public static TheoryData<int> RandomSeeds()
        {
            var seeds = new TheoryData<int>();
            for (var seed = 1; seed <= 48; seed++)
            {
                seeds.Add(seed);
            }

            return seeds;
        }

        public static TheoryData<string> DuplicateIdCaseNames() => Names(DuplicateIdCases);

        public static TheoryData<int> DuplicateIdSeeds() => Seeds(16);

        public static TheoryData<string> ListedCaseNames() => Names(ListedCases);

        public static TheoryData<int> ListedSeeds() => Seeds(32);

        [Theory]
        [MemberData(nameof(StructuredCaseNames))]
        public void StructuredCase_ProductRulesReportWhatTheOraclesReport(string name)
        {
            AssertProductMatchesOracles(StructuredCases[name]());
        }

        [Theory]
        [MemberData(nameof(RandomSeeds))]
        public void RandomCorpus_ProductRulesReportWhatTheOraclesReport(int seed)
        {
            AssertProductMatchesOracles(RandomCorpus(seed));
        }

        [Theory]
        [MemberData(nameof(DuplicateIdCaseNames))]
        public void DuplicateIdCase_ProductRulesReportWhatTheOraclesReport(string name)
        {
            AssertProductMatchesOracles(DuplicateIdCases[name]());
        }

        [Theory]
        [MemberData(nameof(DuplicateIdSeeds))]
        public void DuplicateIdCorpus_ProductRulesReportWhatTheOraclesReport(int seed)
        {
            AssertProductMatchesOracles(DuplicateIdCorpus(seed));
        }

        [Theory]
        [MemberData(nameof(ListedCaseNames))]
        public void ListedCase_ProductRulesReportWhatTheOraclesReportForTheListAsGiven(string name)
        {
            AssertProductMatchesOraclesAsListed(ListedCases[name]());
        }

        [Theory]
        [MemberData(nameof(ListedSeeds))]
        public void ListedCorpus_ProductRulesReportWhatTheOraclesReportForTheListAsGiven(int seed)
        {
            AssertProductMatchesOraclesAsListed(ListedCorpus(seed));
        }

        // Polling leaves out the first call of every repeated call (same URI and body) and needs five more. The calls
        // at 0 s are the first A and the first B (ids 1 and 2).
        [Fact]
        public void Polling_LeavesOutTheFirstCall_AndNeedsFiveCalls()
        {
            var six = RuleRuns.EngineInput(StructuredCases["six-identical-the-minimum-sequence"]());
            Assert.Equal(new[] { "2,3,4,5,6" }, Ids(RuleRuns.ProductPolling(six)));

            var five = RuleRuns.EngineInput(StructuredCases["five-identical-below-the-minimum-sequence"]());
            Assert.Empty(RuleRuns.ProductPolling(five).Violations);

            var stray = RuleRuns.EngineInput(StructuredCases["first-call-exclusion"]());
            Assert.Equal(new[] { "8,9,10,11,12", "3,4,5,6,7" }, Ids(RuleRuns.ProductPolling(stray)));
        }

        // Two calls are compared when they are at most 3,600,000 ms apart after truncation to whole milliseconds: A
        // (one hour apart), B (one hour and one tick) and AOtherBody (one millisecond less) poll, C (one hour and one
        // millisecond) does not.
        [Fact]
        public void Polling_ComparesCallsUpToOneHourApart()
        {
            RuleResult result = RuleRuns.ProductPolling(RuleRuns.EngineInput(OneHourWindowEdges()));

            Assert.Equal(
                new[] { A, B, AOtherBody },
                result.Violations.Select(sequence => KeyOf(sequence.m_violatingCalls[0])));
            Assert.All(result.Violations, sequence => Assert.Equal(6, sequence.m_violatingCalls.Count));
        }

        // The longest sequences come first and equal lengths keep the order in which they were found. A shorter
        // sequence inside a longer one is dropped (here the 10 s sequences 2,6,...,22 and 3,7,...,23), except when it
        // ends with the longer one's last call: the subset check stops one call early, so 4,8,...,24 and 5,9,...,25 are
        // reported.
        [Fact]
        public void Polling_OrdersSequencesByLength_AndKeepsSubsetsThatEndWithTheirSuperset()
        {
            RuleResult result = RuleRuns.ProductPolling(RuleRuns.EngineInput(EqualLengthSequences()));

            Assert.Equal(
                new[]
                {
                    "2,4,6,8,10,12,14,16,18,20,22,24",
                    "3,5,7,9,11,13,15,17,19,21,23,25",
                    "4,8,12,16,20,24",
                    "5,9,13,17,21,25",
                },
                Ids(result));
        }

        // A window holds the later calls less than 5,000 ms (whole milliseconds) after its first call: A (4,999 ms
        // apart) and C (one tick less than 5,000 ms) repeat, B (5,000 ms) does not. Calls at the same time are not
        // repeats of each other, so the two AOtherBody calls at 0 ms (ids 4 and 5) each open a window with the call one
        // tick later (id 6), which the duplicates data then counts twice: 12 duplicates among 11 distinct calls.
        [Fact]
        public void RepeatedCalls_UsesAStrictWindow_AndCountsACallOncePerWindow()
        {
            RuleResult result = RuleRuns.ProductRepeated(RuleRuns.EngineInput(RepeatedWindowEdges()));

            Assert.Equal(new[] { "1,7", "3,8", "4,6", "5,6", "10,13", "11,14" }, Ids(result));
            Assert.Equal(15, (int)result.Results[RepeatedCallsRuleOracle.TotalCallsDataKey]);
            Assert.Equal(12, (int)result.Results[RepeatedCallsRuleOracle.DuplicatesDataKey]);
        }

        // Identical calls 100 ms apart. Polling reports, for every cadence of 1 to (calls - 2) / 4 intervals, the
        // sequence that ends with the last call (the cadence's other sequences are inside the 100 ms one). The
        // repeated-calls rule reports one window per 50 calls: a window holds the calls less than 5,000 ms after its
        // first one. From 402 calls on, cadences of 100 intervals or more are within 1% of their neighbors and polling
        // reports fewer sequences than (calls - 2) / 4.
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(51)]
        [InlineData(52)]
        [InlineData(173)]
        [InlineData(240)]
        public void IdenticalCalls_ReportTheSequencesAndWindowsThatTheRulesHaveAlwaysReported(int count)
        {
            var calls = RuleRuns.EngineInput(new Capture().Every(A, 0, 100 * Millisecond, count).Build());

            Assert.Equal(count >= 2 ? (count - 2) / 4 : 0, RuleRuns.ProductPolling(calls).ViolationCount);
            Assert.Equal(count >= 2 ? ((count - 2) / 50) + 1 : 0, RuleRuns.ProductRepeated(calls).ViolationCount);
        }

        // A negative SameDeltaThresholdPercent leaves no tolerance around a gap other than zero, so polling links only
        // calls at the same time. With MinSequenceSize 3 that reports 6,7,9 (6,7 claims 7,8 and 7,9 and links the last)
        // and 6,8,9 among the four A calls at 1 s, and 3,4,5 among the B calls at 0 s after the first one.
        [Theory]
        [InlineData("-.5")]
        [InlineData("-2")]
        public void Polling_WithANegativeThreshold_LinksOnlyCallsAtTheSameTime(string threshold)
        {
            string properties = $"{{\"SameDeltaThresholdPercent\": \"{threshold}\", \"MinSequenceSize\": \"3\"}}";
            var input = RuleRuns.EngineInput(new Capture()
                .Add(A, 0)
                .Add(A, Second).Add(A, Second).Add(A, Second).Add(A, Second)
                .Every(A, 2 * Second, Second, 4)
                .Add(B, 0).Add(B, 0).Add(B, 0).Add(B, 0)
                .Every(B, Second, Second, 6)
                .Build());

            RuleResult oracle = RuleRuns.OraclePolling(input, properties);

            Assert.Equal(new[] { "6,7,9", "6,8,9", "3,4,5" }, Ids(oracle));
            AssertSameFindings(oracle, RuleRuns.ProductPolling(input, properties));
        }

        // The engine runs a rule on an endpoint's calls as they are listed, and it lists in-game events (the
        // inGameEvents endpoint) by telemetry call and event line, not by time. The shipped rules' "*.*" never matches
        // that endpoint but a rules file's "*" does: polling and repeated calls then run on calls out of time order,
        // with ties, and must report what the oracles report for the list as given.
        [Fact]
        public void WildcardRules_OnInGameEvents_ReportWhatTheOraclesReportForTheListAsGiven()
        {
            var data = new ServiceCallData(false);
            data.CreateFromServiceCallItems(new[]
            {
                TelemetryCall(1, 600, 0, 10, 20, 30, 40, 50, 25, 25, 33, 2, 61, 70, 80, 90),
                TelemetryCall(2, 120, 3, 3, 9, 4, 3.5, 180, 7, 1, 3),
            });
            ServiceCallData.PerConsoleData console = data.m_perConsoleData[ConsoleAddress];
            LinkedList<ServiceCallItem> events =
                console.m_servicesHistory[new EndpointKey(EndpointKind.SyntheticTelemetry, InGameEvents)];
            // The events take their ids from a counter that tests running in parallel share; number them in list order,
            // as an analysis on its own does.
            uint id = 0;
            foreach (ServiceCallItem call in events)
            {
                call.Id = ++id;
            }

            var engine = new RulesEngine(false);
            engine.AddRule(RuleRuns.Configured(new PollingDetectionRule(), "*", RuleRuns.ShippedPollingProperties));
            engine.AddRule(RuleRuns.Configured(new RepeatedCallsRule(), "*", RuleRuns.ShippedRepeatedProperties));

            engine.RunRulesOnData(ConsoleAddress, console);

            Assert.Equal(25, events.Count);
            Assert.Contains(events.Zip(events.Skip(1)), pair => pair.First.ReqTimeUTC > pair.Second.ReqTimeUTC);
            List<RuleResult> results = engine.GetResults(ConsoleAddress);
            RuleResult polling = RuleRuns.OraclePolling(events, RuleRuns.ShippedPollingProperties, InGameEvents);
            RuleResult repeated = RuleRuns.OracleRepeated(events, RuleRuns.ShippedRepeatedProperties, InGameEvents);
            Assert.NotEmpty(polling.Violations);
            Assert.NotEmpty(repeated.Violations);
            AssertSameFindings(polling, Assert.Single(results, result => result.RuleName == RuleRuns.PollingRuleName));
            AssertSameFindings(
                repeated,
                Assert.Single(results, result => result.RuleName == RuleRuns.RepeatedRuleName));
        }

        private static void AssertProductMatchesOracles(List<ServiceCallItem> calls) =>
            AssertProductMatchesOraclesOn(RuleRuns.EngineInput(calls));

        // Runs the rules on the calls as listed, without the engine's ordering by request time.
        private static void AssertProductMatchesOraclesAsListed(List<ServiceCallItem> calls) =>
            AssertProductMatchesOraclesOn(new LinkedList<ServiceCallItem>(calls));

        private static void AssertProductMatchesOraclesOn(LinkedList<ServiceCallItem> input)
        {
            foreach (var properties in new[] { RuleRuns.ShippedPollingProperties, RuleRuns.OtherPollingProperties })
            {
                AssertSameFindings(
                    RuleRuns.OraclePolling(input, properties),
                    RuleRuns.ProductPolling(input, properties));
            }

            foreach (var properties in new[] { RuleRuns.ShippedRepeatedProperties, RuleRuns.DefaultRepeatedProperties })
            {
                AssertSameFindings(
                    RuleRuns.OracleRepeated(input, properties),
                    RuleRuns.ProductRepeated(input, properties));
            }
        }

        private static void AssertSameFindings(RuleResult expected, RuleResult actual)
        {
            Assert.Equal(Describe(expected), Describe(actual));
            for (var index = 0; index < expected.Violations.Count; index++)
            {
                Assert.True(
                    expected.Violations[index].m_violatingCalls.SequenceEqual(
                        actual.Violations[index].m_violatingCalls,
                        ReferenceEqualityComparer.Instance),
                    $"Violation {index} lists other call objects than the oracle's.");
            }
        }

        private static List<string> Describe(RuleResult result)
        {
            var lines = new List<string>
            {
                $"rule: {result.RuleName} | endpoint: {result.Endpoint} | description: {result.RuleDescription}",
            };
            lines.AddRange(result.Results.Select(data => $"data: {data.Key} = {Format(data.Value)}"));
            lines.AddRange(result.Violations.Select((violation, index) =>
                $"violation {index}: {violation.m_level} | {violation.m_endpoint} | {violation.m_summary} | calls " +
                string.Join(",", violation.m_violatingCalls.Select(call => call.Id))));
            return lines;
        }

        private static string Format(object value) => value switch
        {
            null => "null",
            double number => "Double " + number.ToString("R", CultureInfo.InvariantCulture),
            _ => value.GetType().Name + " " + Convert.ToString(value, CultureInfo.InvariantCulture),
        };

        // The ids of each violation's calls, one string per violation.
        private static IEnumerable<string> Ids(RuleResult result) =>
            result.Violations.Select(violation => string.Join(",", violation.m_violatingCalls.Select(call => call.Id)));

        private static CallKey KeyOf(ServiceCallItem call) => new(call.Uri, call.ReqBodyHash);

        private static List<ServiceCallItem> OneHourWindowEdges() => new Capture()
            .Every(A, 0, Hour, 7)
            .Every(B, 0, Hour + 1, 7)
            .Every(C, 0, Hour + Millisecond, 7)
            .Every(AOtherBody, 0, Hour - Millisecond, 7)
            .Build();

        private static List<ServiceCallItem> RepeatedWindowEdges() => new Capture()
            .Every(A, 0, 4999 * Millisecond, 4)
            .Every(B, 0, 5000 * Millisecond, 4)
            .Every(C, 0, (5000 * Millisecond) - 1, 4)
            .Add(AOtherBody, 0).Add(AOtherBody, 0).Add(AOtherBody, 1)
            .Build();

        // Two 5 s cadences on one key, offset by 1 s. The first call is left out, so both sequences hold 12 calls.
        private static List<ServiceCallItem> EqualLengthSequences() => new Capture()
            .Every(A, 0, 5 * Second, 13)
            .Every(A, Second, 5 * Second, 12)
            .Build();

        // A few keys that share URIs or bodies, each with a cadence (jittered around the 1% tolerance), bursts, random
        // times or repeated timestamps; some calls are shoulder taps and some corpora list calls out of time order.
        private static List<ServiceCallItem> RandomCorpus(int seed)
        {
            long[] periods =
            {
                100 * Millisecond, Second, 5 * Second, 30 * Second, 10 * TimeSpan.TicksPerMinute,
                Hour - Second, Hour, Hour + Millisecond, Hour + TimeSpan.TicksPerMinute,
            };
            double[] jitters = { 0, 0.002, 0.0099, 0.0101, 0.03 };
            long[] gaps = { 2 * Second, 30 * Second, 10 * TimeSpan.TicksPerMinute, 2 * Hour };
            long[] tieSpacings = { 0, 1, Millisecond, Second };

            var random = new Random(seed);
            var capture = new Capture();
            var keys = random.Next(1, 5);
            for (var key = 0; key < keys; key++)
            {
                var callKey = new CallKey(
                    $"https://{RuleRuns.Endpoint}/random/{random.Next(0, 3)}",
                    (ulong)random.Next(0, 2));
                var time = random.Next(0, 60_000) * Millisecond;
                switch (random.Next(0, 4))
                {
                    case 0:
                        var period = periods[random.Next(periods.Length)];
                        var jitter = jitters[random.Next(jitters.Length)];
                        var count = random.Next(0, 40);
                        for (var call = 0; call < count; call++)
                        {
                            capture.Add(callKey, time, Tap(random));
                            time += (long)Math.Round(period * (1 + (((random.NextDouble() * 2) - 1) * jitter)));
                        }

                        break;
                    case 1:
                        var bursts = random.Next(1, 4);
                        for (var burst = 0; burst < bursts; burst++)
                        {
                            var size = random.Next(2, 7);
                            for (var call = 0; call < size; call++)
                            {
                                capture.Add(callKey, time + (random.Next(0, 6000) * Millisecond), Tap(random));
                            }

                            time += gaps[random.Next(gaps.Length)];
                        }

                        break;
                    case 2:
                        var scattered = random.Next(0, 30);
                        for (var call = 0; call < scattered; call++)
                        {
                            capture.Add(
                                callKey,
                                (random.Next(0, 7_200_000) * Millisecond) + random.Next(0, (int)Millisecond),
                                Tap(random));
                        }

                        break;
                    default:
                        var timestamps = random.Next(2, 8);
                        for (var timestamp = 0; timestamp < timestamps; timestamp++)
                        {
                            var copies = random.Next(1, 4);
                            for (var copy = 0; copy < copies; copy++)
                            {
                                capture.Add(callKey, time, Tap(random));
                            }

                            time += tieSpacings[random.Next(tieSpacings.Length)];
                        }

                        break;
                }
            }

            return capture.Build(idsInInsertionOrder: random.Next(0, 4) == 0);
        }

        private static bool Tap(Random random) => random.Next(0, 20) == 0;

        // A 10 s cadence of A whose first listed call is the latest, with earlier calls listed among it, then B calls
        // less than 5 s apart.
        private static List<ServiceCallItem> CadenceWithLateAndEarlyCalls() => new Capture()
            .At(A, 600, 0, 10, 20, 30, 40, 50, 25, 25, 33, 2, 61, 70, 80, 90)
            .At(B, 3, 3.5, 3, 9, 4)
            .Build(idsInInsertionOrder: true);

        // A random corpus listed in a random order, as in-game events can be. The ids then follow the list, keep the
        // capture's order (by request time or as added), are shuffled or repeat.
        private static List<ServiceCallItem> ListedCorpus(int seed)
        {
            var random = new Random(1000 + seed);
            List<ServiceCallItem> calls = RandomCorpus(seed);
            Shuffle(calls, random);
            return (seed % 4) switch
            {
                0 => Renumbered(calls, index => (uint)(index + 1)),
                1 => calls,
                2 => ShuffledIds(calls, random),
                _ => RepeatedIds(calls, random),
            };
        }

        // A random corpus whose ids repeat, as when a capture repeats request numbers.
        private static List<ServiceCallItem> DuplicateIdCorpus(int seed) =>
            RepeatedIds(RandomCorpus(seed), new Random(2000 + seed));

        // Gives each call a new id, chosen by its position in the list.
        private static List<ServiceCallItem> Renumbered(List<ServiceCallItem> calls, Func<int, uint> id)
        {
            for (var index = 0; index < calls.Count; index++)
            {
                calls[index].Id = id(index);
            }

            return calls;
        }

        // Ids that follow request time (ties keep the list order) while the list does not.
        private static List<ServiceCallItem> IdsInTimeOrder(List<ServiceCallItem> calls)
        {
            var byTime = Enumerable.Range(0, calls.Count).OrderBy(index => calls[index].ReqTimeUTC).ToList();
            for (var rank = 0; rank < byTime.Count; rank++)
            {
                calls[byTime[rank]].Id = (uint)(rank + 1);
            }

            return calls;
        }

        // The ids 1 to n in a random order.
        private static List<ServiceCallItem> ShuffledIds(List<ServiceCallItem> calls, Random random)
        {
            var ids = Enumerable.Range(1, calls.Count).Select(id => (uint)id).ToList();
            Shuffle(ids, random);
            return Renumbered(calls, index => ids[index]);
        }

        // Ids from 1 to a third of the number of calls (at least 2), so that most of them repeat.
        private static List<ServiceCallItem> RepeatedIds(List<ServiceCallItem> calls, Random random)
        {
            var largest = Math.Max(2, calls.Count / 3);
            return Renumbered(calls, _ => (uint)random.Next(1, largest + 1));
        }

        private static void Shuffle<T>(IList<T> items, Random random)
        {
            for (var index = items.Count - 1; index > 0; index--)
            {
                var other = random.Next(index + 1);
                (items[index], items[other]) = (items[other], items[index]);
            }
        }

        private static TheoryData<string> Names(Dictionary<string, Func<List<ServiceCallItem>>> cases)
        {
            var names = new TheoryData<string>();
            foreach (var name in cases.Keys)
            {
                names.Add(name);
            }

            return names;
        }

        private static TheoryData<int> Seeds(int count)
        {
            var seeds = new TheoryData<int>();
            for (var seed = 1; seed <= count; seed++)
            {
                seeds.Add(seed);
            }

            return seeds;
        }

        // A call to the Xbox telemetry endpoint whose body holds a CS1 event line for each time (in seconds). The
        // conversion clears each event's display body but preserves its source request-body identity.
        private static ServiceCallItem TelemetryCall(ulong body, params double[] seconds)
        {
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            IEnumerable<string> lines = seconds.Select(second =>
                $"0|ABCD_12345678.Ping|{start.AddTicks((long)Math.Round(second * Second)):O}|3|4|5|6|7|8|9|10|11");
            return new ServiceCallItem
            {
                Host = TelemetryHost,
                Uri = $"https://{TelemetryHost}/data/events",
                ConsoleIP = ConsoleAddress,
                ReqBodyHash = body,
                ReqBody = string.Join("\r\n", lines),
                ReqTimeUTC = (ulong)start.AddHours(body).ToFileTimeUtc(),
            };
        }

        private readonly record struct CallKey(string Uri, ulong Body);

        // Calls of one endpoint. Ids follow request time, as in a capture, unless idsInInsertionOrder keeps the order
        // in which the calls were added.
        private sealed class Capture
        {
            private const long Start = 638_000_000_000_000_000;
            private readonly List<(CallKey Key, long Ticks, bool ShoulderTap)> _calls = new();

            internal Capture Add(CallKey key, long ticks, bool shoulderTap = false)
            {
                _calls.Add((key, ticks, shoulderTap));
                return this;
            }

            internal Capture Every(CallKey key, long first, long interval, int count)
            {
                for (var index = 0; index < count; index++)
                {
                    Add(key, first + (index * interval));
                }

                return this;
            }

            internal Capture Jittered(CallKey key, long first, long interval, int count, double jitter, int seed)
            {
                var random = new Random(seed);
                var time = first;
                for (var index = 0; index < count; index++)
                {
                    Add(key, time);
                    time += (long)Math.Round(interval * (1 + (((random.NextDouble() * 2) - 1) * jitter)));
                }

                return this;
            }

            // A call at first, then one call after each interval (in seconds).
            internal Capture Intervals(CallKey key, long first, params double[] seconds)
            {
                var time = first;
                Add(key, time);
                foreach (var interval in seconds)
                {
                    time += (long)Math.Round(interval * Second);
                    Add(key, time);
                }

                return this;
            }

            // One call at each time (in seconds), in the order given.
            internal Capture At(CallKey key, params double[] seconds)
            {
                foreach (var second in seconds)
                {
                    Add(key, (long)Math.Round(second * Second));
                }

                return this;
            }

            internal List<ServiceCallItem> Build(bool idsInInsertionOrder = false)
            {
                var ordered = idsInInsertionOrder ? _calls : _calls.OrderBy(call => call.Ticks).ToList();
                return ordered
                    .Select((call, index) => new ServiceCallItem((uint)(index + 1))
                    {
                        Host = RuleRuns.Endpoint,
                        Uri = call.Key.Uri,
                        ReqBodyHash = call.Key.Body,
                        ReqBody =
                            call.Key.Body.ToString(
                                CultureInfo.InvariantCulture),
                        ReqTimeUTC = (ulong)(Start + call.Ticks),
                        IsShoulderTap = call.ShoulderTap,
                    })
                    .ToList();
            }
        }
    }

    // Runs the product rules and their frozen oracles on one endpoint's calls, by default with the properties that
    // XboxLiveTraceAnalyzer.Rules.json ships. EngineInput orders the calls by request time (ties keep their order), as
    // the engine does for a host; the engine passes in-game events as they are listed. Rule names, descriptions and
    // data keys are localized, so the language is loaded once before any rule runs: tests that run in parallel then
    // never see the strings change between an oracle and a product run.
    internal static class RuleRuns
    {
        internal const string Endpoint = "userpresence.xboxlive.com";
        internal const string ShippedPollingProperties = "{\"SameDeltaThresholdPercent\": \".01\"}";
        internal const string ShippedRepeatedProperties = "{\"MinAllowedRepeatIntervalMs\": \"5000\"}";

        // A rules file can set every polling property; these differ from the defaults in each of them.
        internal const string OtherPollingProperties =
            "{\"SameDeltaThresholdPercent\": \".05\", \"MaxFrequencyMs\": \"10000\", \"MinSequenceSize\": \"3\"}";

        // The repeated-calls rule's own default window (2,000 ms), for rules files that set none.
        internal const string DefaultRepeatedProperties = "{}";

        static RuleRuns()
        {
            Localization.LoadLanguage("en-us");
        }

        // The rule names that the results, and xmat's findings, carry.
        internal static string PollingRuleName => PollingDetectionRuleOracle.DisplayName;

        internal static string RepeatedRuleName => RepeatedCallsRuleOracle.DisplayName;

        internal static LinkedList<ServiceCallItem> EngineInput(IEnumerable<ServiceCallItem> calls) =>
            new(calls.OrderBy(call => call.ReqTimeUTC));

        internal static RuleResult ProductPolling(
            LinkedList<ServiceCallItem> calls,
            string properties = ShippedPollingProperties)
        {
            var rule = new PollingDetectionRule { Name = "PollingDetection_Rule", Endpoint = Endpoint };
            Configure(rule.DeserializeJson, properties);
            return rule.Run(new RulesEngine(false), calls, new ServiceCallStats(calls));
        }

        internal static RuleResult OraclePolling(
            LinkedList<ServiceCallItem> calls,
            string properties = ShippedPollingProperties,
            string endpoint = Endpoint)
        {
            var oracle = new PollingDetectionRuleOracle { Endpoint = endpoint };
            Configure(oracle.DeserializeJson, properties);
            return oracle.Run(calls);
        }

        internal static RuleResult ProductRepeated(
            LinkedList<ServiceCallItem> calls,
            string properties = ShippedRepeatedProperties)
        {
            var rule = new RepeatedCallsRule { Name = "RepeatedCalls_Rule", Endpoint = Endpoint };
            Configure(rule.DeserializeJson, properties);
            return rule.Run(new RulesEngine(false), calls, new ServiceCallStats(calls));
        }

        internal static RuleResult OracleRepeated(
            LinkedList<ServiceCallItem> calls,
            string properties = ShippedRepeatedProperties,
            string endpoint = Endpoint)
        {
            var oracle = new RepeatedCallsRuleOracle { Endpoint = endpoint };
            Configure(oracle.DeserializeJson, properties);
            return oracle.Run(calls);
        }

        // A product rule as a rules file defines it, for the engine to run on the endpoints that match its endpoint.
        internal static TRule Configured<TRule>(TRule rule, string endpoint, string properties)
            where TRule : Rule
        {
            rule.Name = rule.RuleID + "_Rule";
            rule.Endpoint = endpoint;
            Configure(rule.DeserializeJson, properties);
            return rule;
        }

        // The rules parse their properties with the current culture; the shipped ".01" needs a '.' decimal separator.
        private static void Configure(Action<JsonElement> deserialize, string properties)
        {
            using var document = JsonDocument.Parse(properties);
            var culture = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            try
            {
                deserialize(document.RootElement);
            }
            finally
            {
                CultureInfo.CurrentCulture = culture;
            }
        }
    }
}
