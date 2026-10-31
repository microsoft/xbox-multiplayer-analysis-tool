// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CaptureAnalysisEngine;

namespace XMAT.Tests.Analysis.Rules
{
    public class RuleComplexityDifferentialTests
    {
        private const string Endpoint =
            "userstats.xboxlive.com";
        private const string ScalingVariable =
            "XMAT_SCALING_BENCHMARKS";
        private const ulong Millisecond =
            (ulong)TimeSpan.TicksPerMillisecond;
        private const ulong Start =
            638_000_000_000_000_000;

        static RuleComplexityDifferentialTests()
        {
            Localization.LoadLanguage("en-us");
        }

        public static TheoryData<int> RandomSeeds()
        {
            var seeds =
                new TheoryData<int>();
            for (var seed = 1;
                seed <= 16;
                seed++)
            {
                seeds.Add(seed);
            }
            return seeds;
        }

        [Fact]
        public void ThrottledCalls_StructuredCasesMatchFrozenImplementation()
        {
            var cases =
                new[]
                {
                    Array.Empty<ServiceCallItem>(),
                    new[] { Call(1, 0) },
                    new[] { Call(1, 0, 429) },
                    Enumerable.Range(0, 6)
                        .Select(index =>
                            Call(
                                (uint)(index + 1),
                                (ulong)index * Millisecond,
                                429))
                        .ToArray(),
                    new[]
                    {
                        Call(1, 0, 429),
                        Call(2, 1 * Millisecond),
                        Call(3, 2 * Millisecond, 429),
                        Call(4, 3 * Millisecond),
                        Call(5, 4 * Millisecond, 429),
                    },
                    new[]
                    {
                        Call(1, 0),
                        Call(2, 1 * Millisecond),
                        Call(
                            3,
                            2 * Millisecond,
                            429,
                            requestBody:
                                "{\"maxRequests\":2,\"periodInSeconds\":3}",
                            responseBody: "{}"),
                    },
                    new[]
                    {
                        Call(
                            1,
                            0,
                            429,
                            requestBody: "{not-json",
                            responseBody: "{}"),
                        Call(2, Millisecond),
                    },
                };

            foreach (ServiceCallItem[] calls in cases)
            {
                AssertSame(
                    ThrottledCallsOracle.Run(
                        new LinkedList<ServiceCallItem>(
                            calls),
                        Endpoint),
                    RunThrottled(calls));
            }
        }

        [Theory]
        [MemberData(nameof(RandomSeeds))]
        public void ThrottledCalls_RandomCorpusMatchesFrozenImplementation(
            int seed)
        {
            ServiceCallItem[] calls =
                RandomThrottledCalls(seed);

            AssertSame(
                ThrottledCallsOracle.Run(
                    new LinkedList<ServiceCallItem>(
                        calls),
                    Endpoint),
                RunThrottled(calls));
        }

        [Fact]
        public void BatchFrequency_StructuredCasesMatchFrozenImplementation()
        {
            var cases =
                new[]
                {
                    Array.Empty<ServiceCallItem>(),
                    new[]
                    {
                        Call(
                            1,
                            0,
                            uri: "/batch",
                            requestBody: "{\"xuids\":[\"1\"]}"),
                    },
                    new[]
                    {
                        Call(
                            1,
                            0,
                            uri: "/batch",
                            requestBody: "{\"xuids\":[\"1\",\"2\"]}"),
                        Call(
                            2,
                            500 * Millisecond,
                            uri: "/batch",
                            requestBody: "{\"xuids\":[\"3\"]}"),
                        Call(
                            3,
                            4_000 * Millisecond,
                            uri: "/batch",
                            requestBody: "{\"xuids\":[\"4\",\"5\",\"6\"]}"),
                        Call(
                            4,
                            4_500 * Millisecond,
                            uri: "/batch",
                            requestBody: "{\"xuids\":[\"7\"]}"),
                    },
                    new[]
                    {
                        Call(
                            1,
                            3_000 * Millisecond,
                            uri: "/batch",
                            requestBody: "{\"xuids\":[\"1\"]}"),
                        Call(
                            2,
                            1_000 * Millisecond,
                            uri: "/batch",
                            requestBody: "{\"xuids\":[\"2\"]}"),
                        Call(
                            3,
                            2_000 * Millisecond,
                            uri: "/other",
                            requestBody: "{\"users\":[\"3\",\"4\"]}"),
                        Call(
                            4,
                            2_100 * Millisecond,
                            uri: "/other",
                            requestBody: "{malformed"),
                    },
                };

            foreach (ServiceCallItem[] calls in cases)
            {
                AssertSame(
                    BatchFrequencyOracle.Run(
                        new LinkedList<ServiceCallItem>(
                            calls),
                        Endpoint,
                        BatchPatterns(),
                        2_000),
                    RunBatch(calls));
            }
        }

        [Theory]
        [MemberData(nameof(RandomSeeds))]
        public void BatchFrequency_RandomCorpusMatchesFrozenImplementation(
            int seed)
        {
            ServiceCallItem[] calls =
                RandomBatchCalls(seed);

            AssertSame(
                BatchFrequencyOracle.Run(
                    new LinkedList<ServiceCallItem>(
                        calls),
                    Endpoint,
                    BatchPatterns(),
                    2_000),
                RunBatch(calls));
        }

        [Fact]
        public void PrintCallIdRange_MatchesFrozenImplementation()
        {
            var cases =
                new (uint[] Ids, uint Minimum)[]
                {
                    (Array.Empty<uint>(), 3),
                    (new uint[] { 1 }, 3),
                    (new uint[] { 1, 2, 3, 4 }, 3),
                    (new uint[] { 1, 2, 4, 5, 9 }, 3),
                    (new uint[] { 0, 1, 2, uint.MaxValue, 0 }, 2),
                };
            foreach ((uint[] ids, uint minimumRange) in
                cases)
            {
                ServiceCallItem[] calls = ids
                    .Select((id, index) =>
                        Call(
                            id,
                            (ulong)index *
                            Millisecond))
                    .ToArray();
                var expected =
                    new StringBuilder();
                var actual =
                    new StringBuilder();

                PrintCallIdRangeOracle(
                    expected,
                    new LinkedList<ServiceCallItem>(
                        calls),
                    minimumRange);
                Utils.PrintCallIdRange(
                    actual,
                    new LinkedList<ServiceCallItem>(
                        calls),
                    minimumRange);

                Assert.Equal(
                    expected.ToString(),
                    actual.ToString());
            }
        }

        [Fact]
        [Trait("Category", "Scaling")]
        public void ThrottledCalls_OneHundredThousandItemsCompletesWithinBound()
        {
            if (string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable(
                        ScalingVariable)))
            {
                return;
            }

            var calls =
                new LinkedList<ServiceCallItem>(
                    Enumerable.Range(0, 100_000)
                        .Select(index =>
                            Call(
                                (uint)(index + 1),
                                (ulong)index *
                                Millisecond,
                                index is 0 or 50_000 or 99_999
                                    ? 429U
                                    : 200U)));
            var rule =
                new ThrottledCallsRule
                {
                    Endpoint = Endpoint,
                    Name = "ThrottledCalls_Rule",
                };
            var stopwatch =
                Stopwatch.StartNew();

            RuleResult result = rule.Run(
                new RulesEngine(false),
                calls,
                new ServiceCallStats(calls));

            stopwatch.Stop();
            Assert.Equal(
                100_000,
                (int)result.Results[
                    ThrottledCallsRule
                        .TotalCallsDataKey]);
            Assert.True(
                stopwatch.Elapsed <
                    TimeSpan.FromSeconds(10),
                $"ThrottledCallsRule took {stopwatch.Elapsed}.");
        }

        [Fact]
        [Trait("Category", "Scaling")]
        public void BatchFrequency_OneHundredThousandItemsCompletesWithinBound()
        {
            if (string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable(
                        ScalingVariable)))
            {
                return;
            }

            ServiceCallItem[] calls =
                Enumerable.Range(0, 100_000)
                    .Select(index =>
                        Call(
                            (uint)(index + 1),
                            (ulong)index *
                            Millisecond,
                            uri: "/batch",
                            requestBody:
                                "{\"xuids\":[\"1\"]}"))
                    .ToArray();
            var rule =
                new BatchFrequencyRule
                {
                    Endpoint = Endpoint,
                    Name = "BatchFrequency_Rule",
                    m_BatchSetDetectionWindowsMs = 2_000,
                    m_MatchPatterns =
                        new Dictionary<string, string>(
                            StringComparer.Ordinal)
                        {
                            ["/batch"] = "xuids",
                        },
                };
            var stopwatch =
                Stopwatch.StartNew();

            RuleResult result = rule.Run(
                new RulesEngine(false),
                calls,
                new ServiceCallStats(calls));

            stopwatch.Stop();
            Assert.Equal(
                100_000U,
                (uint)result.Results[
                    BatchFrequencyRule
                        .TotalBatchCallsDataKey]);
            Assert.True(
                stopwatch.Elapsed <
                    TimeSpan.FromSeconds(15),
                $"BatchFrequencyRule took {stopwatch.Elapsed}.");
        }

        private static RuleResult RunThrottled(
            IEnumerable<ServiceCallItem> calls)
        {
            var input =
                new LinkedList<ServiceCallItem>(
                    calls);
            var rule =
                new ThrottledCallsRule
                {
                    Endpoint = Endpoint,
                    Name = "ThrottledCalls_Rule",
                };
            return rule.Run(
                new RulesEngine(false),
                input,
                new ServiceCallStats(input));
        }

        private static RuleResult RunBatch(
            IEnumerable<ServiceCallItem> calls)
        {
            var input =
                new LinkedList<ServiceCallItem>(
                    calls);
            var rule =
                new BatchFrequencyRule
                {
                    Endpoint = Endpoint,
                    Name = "BatchFrequency_Rule",
                    m_BatchSetDetectionWindowsMs = 2_000,
                    m_MatchPatterns = BatchPatterns(),
                };
            return rule.Run(
                new RulesEngine(false),
                input,
                new ServiceCallStats(input));
        }

        private static Dictionary<string, string>
            BatchPatterns() =>
            new(StringComparer.Ordinal)
            {
                ["/batch"] = "xuids",
                ["/other"] = "users",
            };

        private static ServiceCallItem[] RandomThrottledCalls(
            int seed)
        {
            var random = new Random(seed);
            int count = random.Next(0, 60);
            var calls =
                new ServiceCallItem[count];
            for (var index = 0;
                index < count;
                index++)
            {
                uint status =
                    random.Next(0, 5) == 0
                        ? 429U
                        : (uint)random.Next(200, 500);
                int body = random.Next(0, 4);
                calls[index] = Call(
                    (uint)(index + 1),
                    (ulong)random.Next(
                        0,
                        10_000) *
                    Millisecond,
                    status,
                    requestBody: body switch
                    {
                        0 => "{\"maxRequests\":2,\"periodInSeconds\":3}",
                        1 => "{malformed",
                        _ => string.Empty,
                    },
                    responseBody:
                        random.Next(0, 2) == 0
                            ? string.Empty
                            : "{}");
            }
            return calls;
        }

        private static ServiceCallItem[] RandomBatchCalls(
            int seed)
        {
            var random = new Random(seed);
            int count = random.Next(0, 50);
            var calls =
                new List<ServiceCallItem>();
            for (var index = 0;
                index < count;
                index++)
            {
                string uri = random.Next(0, 3) switch
                {
                    0 => "/batch",
                    1 => "/other",
                    _ => "/single",
                };
                string property =
                    uri == "/other"
                        ? "users"
                        : "xuids";
                string body = random.Next(0, 5) switch
                {
                    0 => "{malformed",
                    1 => "{\"" + property + "\":{}}",
                    _ => "{\"" + property + "\":[" +
                        string.Join(
                            ",",
                            Enumerable.Range(
                                    0,
                                    random.Next(0, 6))
                                .Select(value =>
                                    "\"" + value + "\"")) +
                        "]}",
                };
                calls.Add(
                    Call(
                        (uint)(index + 1),
                        (ulong)random.Next(
                            0,
                            20_000) *
                        Millisecond,
                        uri: uri,
                        requestBody: body));
                if (random.Next(0, 20) == 0)
                {
                    calls.Add(calls[^1]);
                }
            }
            return calls.ToArray();
        }

        private static ServiceCallItem Call(
            uint id,
            ulong offset,
            uint status = 200,
            string uri = "/single",
            string requestBody = "",
            string responseBody = "") =>
            new(id)
            {
                Host = Endpoint,
                Uri = "https://" + Endpoint + uri,
                ReqBody = requestBody,
                RspBody = responseBody,
                HttpStatusCode = status,
                ReqTimeUTC = Start + offset,
            };

        private static void AssertSame(
            RuleResult expected,
            RuleResult actual)
        {
            Assert.Equal(expected.RuleName, actual.RuleName);
            Assert.Equal(expected.Endpoint, actual.Endpoint);
            Assert.Equal(expected.RuleDescription, actual.RuleDescription);
            Assert.Equal(expected.Results.Count, actual.Results.Count);
            foreach ((string key, object value) in
                expected.Results)
            {
                Assert.True(actual.Results.TryGetValue(key, out object actualValue));
                Assert.Equal(value, actualValue);
            }
            Assert.Equal(expected.Violations.Count, actual.Violations.Count);
            for (var index = 0;
                index < expected.Violations.Count;
                index++)
            {
                RuleViolation expectedViolation = expected.Violations[index];
                RuleViolation actualViolation = actual.Violations[index];
                Assert.Equal(expectedViolation.m_level, actualViolation.m_level);
                Assert.Equal(expectedViolation.m_endpoint, actualViolation.m_endpoint);
                Assert.Equal(expectedViolation.m_summary, actualViolation.m_summary);
                Assert.True(
                    expectedViolation.m_violatingCalls.SequenceEqual(
                        actualViolation.m_violatingCalls,
                        ReferenceEqualityComparer.Instance),
                    $"Violation {index} contains different call references.");
            }
        }

        // Frozen from 1f4781c, including its repeated enumeration.
        private static void PrintCallIdRangeOracle(
            StringBuilder output,
            IEnumerable<ServiceCallItem> calls,
            uint minRangeSize)
        {
            if (calls.Count() == 0)
            {
                return;
            }
            var range = new List<uint>();
            uint previous = calls.First().Id - 1;
            for (var index = 0;
                index < calls.Count();
                index++)
            {
                uint local = calls.ElementAt(index).Id;
                if (local == previous + 1)
                {
                    range.Add(local);
                }
                else
                {
                    AppendRange(output, range, minRangeSize);
                    range.Clear();
                    range.Add(local);
                }
                previous = local;
            }
            AppendRange(output, range, minRangeSize);
        }

        private static void AppendRange(
            StringBuilder output,
            List<uint> range,
            uint minimum)
        {
            if (range.Count >= minimum)
            {
                output.AppendFormat("{0}-{1}, ", range.First(), range.Last());
            }
            else
            {
                foreach (uint id in range)
                {
                    output.AppendFormat("{0}, ", id);
                }
            }
        }

        // Frozen from 1f4781c. These copies must not follow later product optimizations.
        private static class ThrottledCallsOracle
        {
            internal static RuleResult Run(
                IEnumerable<ServiceCallItem> items,
                string endpoint)
            {
                var result = new RuleResult(
                    ThrottledCallsRule.DisplayName,
                    endpoint,
                    ThrottledCallsRule.Description);
                int totalCalls = items.Count();
                int throttledCalls =
                    items.Where(call => call.HttpStatusCode == 429).Count();

                for (var index = 0;
                    index < totalCalls;)
                {
                    ServiceCallItem call = items.ElementAt(index);
                    if (call.HttpStatusCode != 429)
                    {
                        index++;
                        continue;
                    }

                    var set = new List<ServiceCallItem> { items.ElementAt(index) };
                    ServiceCallItem throttled = set.First();
                    string guideline = string.Empty;
                    try
                    {
                        if (!string.IsNullOrEmpty(throttled.RspBody))
                        {
                            JsonDocument document = JsonDocument.Parse(throttled.ReqBody);
                            JsonElement root = document.RootElement;
                            if (root.TryGetProperty(
                                    "maxRequests",
                                    out JsonElement maximum) &&
                                root.TryGetProperty(
                                    "periodInSeconds",
                                    out JsonElement period))
                            {
                                guideline = Localization.GetLocalizedString(
                                    "LTA_THROTTLE_CALLS_GUIDELINE",
                                    maximum,
                                    period);
                            }
                        }
                    }
                    catch (Exception)
                    {
                    }

                    while (++index < throttledCalls)
                    {
                        _ = items.ElementAt(index);
                        if (call.HttpStatusCode != 429)
                        {
                            index++;
                            break;
                        }
                        set.Add(call);
                    }

                    if (set.Count == 1)
                    {
                        result.AddViolation(
                            ViolationLevel.Warning,
                            Localization.GetLocalizedString(
                                "LTA_THROTTLE_CALLS_VIOLATION_SINGLE") +
                            guideline,
                            throttled);
                    }
                    else
                    {
                        result.AddViolation(
                            ViolationLevel.Warning,
                            Localization.GetLocalizedString(
                                "LTA_THROTTLE_CALLS_VIOLATION_MULTI") +
                            guideline,
                            set);
                    }
                    set.Clear();
                }

                result.Results.Add(ThrottledCallsRule.TotalCallsDataKey, totalCalls);
                result.Results.Add(ThrottledCallsRule.ThrottledCallsDataKey, throttledCalls);
                result.Results.Add(
                    ThrottledCallsRule.PercentageDataKey,
                    ((double)throttledCalls) / totalCalls);
                return result;
            }
        }

        private static class BatchFrequencyOracle
        {
            internal static RuleResult Run(
                IEnumerable<ServiceCallItem> items,
                string endpoint,
                Dictionary<string, string> patterns,
                uint window)
            {
                var result = new RuleResult(
                    BatchFrequencyRule.DisplayName,
                    endpoint,
                    BatchFrequencyRule.Description);
                uint totalBatchCalls = 0;
                foreach ((string pattern, string selector) in
                    patterns)
                {
                    var matches = new Dictionary<ServiceCallItem, int>();
                    foreach (ServiceCallItem item in items)
                    {
                        Match match = Regex.Match(item.Uri, pattern);
                        if (match.Success)
                        {
                            try
                            {
                                JsonDocument document = JsonDocument.Parse(item.ReqBody);
                                JsonElement? element = document.SelectElement(selector);
                                if (element.HasValue &&
                                    element.Value.ValueKind ==
                                        JsonValueKind.Array)
                                {
                                    int count = element.Value.EnumerateArray().Count();
                                    matches.Add(item, count);
                                }
                            }
                            catch (Exception)
                            {
                            }
                        }
                    }

                    totalBatchCalls = (uint)matches.Count;
                    if (matches.Count >= 2)
                    {
                        var start = 0;
                        var callsWithinWindow = new List<ServiceCallItem>();
                        var totalUsers = 0;
                        totalUsers += matches.Values.ElementAt(start);
                        callsWithinWindow.Add(matches.Keys.ElementAt(start));
                        for (var end = 1;
                            end < matches.Count();
                            end++)
                        {
                            ulong elapsed = (ulong)Math.Abs(
                                (float)(matches.Keys.ElementAt(end).ReqTimeUTC -
                                    matches.Keys.ElementAt(start).ReqTimeUTC) /
                                TimeSpan.TicksPerMillisecond);
                            if (elapsed <= window)
                            {
                                callsWithinWindow.Add(matches.Keys.ElementAt(end));
                                totalUsers += matches.Values.ElementAt(end);
                            }
                            else
                            {
                                if (callsWithinWindow.Count >= 2)
                                {
                                    result.AddViolation(
                                        ViolationLevel.Warning,
                                        Localization.GetLocalizedString(
                                            "LTA_BATCH_CALLS_VIOLATION",
                                            callsWithinWindow.Count,
                                            window,
                                            totalUsers),
                                        callsWithinWindow);
                                }
                                start = end;
                                totalUsers = 0;
                                callsWithinWindow.Clear();
                            }
                        }
                        if (callsWithinWindow.Count >= 2)
                        {
                            result.AddViolation(
                                ViolationLevel.Warning,
                                Localization.GetLocalizedString(
                                    "LTA_BATCH_CALLS_VIOLATION",
                                    callsWithinWindow.Count,
                                    window,
                                    totalUsers),
                                callsWithinWindow);
                        }
                    }
                }

                result.Results.Add(BatchFrequencyRule.TotalBatchCallsDataKey, totalBatchCalls);
                result.Results.Add(BatchFrequencyRule.AllowedTimeBetweenCallsDataKey, window);
                result.Results.Add(BatchFrequencyRule.TimesExceededDataKey, result.ViolationCount);
                result.Results.Add(
                    BatchFrequencyRule.PotentialReducedCallCountDataKey,
                    totalBatchCalls - result.ViolationCount);
                return result;
            }
        }
    }
}
