// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using CaptureAnalysisEngine;

namespace XMAT.Tests
{
    public class CallFrequencyRuleTests
    {
        private static readonly Uri RelativeUriBase =
            new("https://xmat.invalid/");

        static CallFrequencyRuleTests()
        {
            Localization.LoadLanguage("en-us");
        }

        [Theory]
        [InlineData(
            "https://example.test/p?q=1#f",
            "example.test",
            "/p")]
        [InlineData(
            "/p?q=1#f",
            "",
            "/p")]
        [InlineData(
            "/a%20b",
            "",
            "/a%20b")]
        public void Run_MatchesSubpathsWithoutChangingAbsoluteOrHostlessUris(
            string uri,
            string host,
            string subpath)
        {
            ServiceCallItem[] calls =
                CreateCalls(
                    uri,
                    host);
            CallFrequencyRule rule =
                CreateRule(subpath);

            RuleResult result = rule.Run(
                new RulesEngine(false),
                calls,
                new ServiceCallStats(calls));

            RuleViolation violation =
                Assert.Single(result.Violations);
            Assert.Equal(
                ViolationLevel.Warning,
                violation.m_level);
            Assert.Equal(
                calls,
                violation.m_violatingCalls.ToArray());
            Assert.All(
                calls,
                call =>
                {
                    Assert.Equal(uri, call.Uri);
                    Assert.Equal(host, call.Host);
                });
        }

        [Fact]
        public void Run_MultipleLimitsMatchFrozenApplicabilityAndViolationOrder()
        {
            ServiceCallItem[] calls =
            {
                CreateCall(
                    1,
                    "https://example.test/shared/a",
                    "example.test",
                    TimeSpan.Zero),
                CreateCall(
                    2,
                    "https://example.test/first/a",
                    "example.test",
                    TimeSpan.FromSeconds(1)),
                CreateCall(
                    3,
                    "https://example.test/second/a",
                    "example.test",
                    TimeSpan.FromSeconds(2)),
                CreateCall(
                    4,
                    "https://example.test/unmatched",
                    "example.test",
                    TimeSpan.FromSeconds(3)),
            };
            CallFrequencyRule rule =
                CreateEmptyRule();
            rule.m_rateLimits.Add(
                CreateLimits(
                    "first",
                    "/shared/*",
                    "/first/*",
                    "/missing/*"));
            rule.m_rateLimits.Add(
                CreateLimits(
                    "second",
                    "/shared/*",
                    "/second/*",
                    "/missing/*"));

            List<ServiceCallItem>[] expectedApplicable =
                FrozenApplicableCalls(
                    rule,
                    calls);
            List<ServiceCallItem>[] actualApplicable =
                rule.ClassifyApplicableCalls(calls);

            Assert.Equal(
                expectedApplicable.Select(
                    calls => JoinCallIds(calls)),
                actualApplicable.Select(
                    calls => JoinCallIds(calls)));

            var expected =
                FrozenOutcome(
                    rule,
                    expectedApplicable);
            var stats =
                new ServiceCallStats(calls);
            RuleResult actual = rule.Run(
                new RulesEngine(false),
                calls,
                stats);

            Assert.Equal(
                expected.Violations,
                Snapshot(actual));
            Assert.Equal(
                expected.Sustained,
                actual.Results[
                    CallFrequencyRule
                        .TimesSustainedExceededDataKey]);
            Assert.Equal(
                expected.Burst,
                actual.Results[
                    CallFrequencyRule
                        .TimesBurstExceededDataKey]);
            Assert.Equal(
                stats.m_numCalls,
                actual.Results[
                    CallFrequencyRule
                        .TotalCallsDataKey]);
        }

        [Fact]
        public void Run_RelativeUriWithAHostStillFails()
        {
            ServiceCallItem[] calls =
                CreateCalls(
                    "/p",
                    "example.test");
            CallFrequencyRule rule =
                CreateRule("/p");

            Assert.Throws<UriFormatException>(
                () => rule.Run(
                    new RulesEngine(false),
                    calls,
                    new ServiceCallStats(calls)));
        }

        [Fact]
        public void Run_MalformedUriWithoutSubpathsDoesNotParseThePath()
        {
            ServiceCallItem[] calls =
                CreateCalls(
                    "https://[",
                    string.Empty);
            CallFrequencyRule rule =
                CreateEmptyRule();
            rule.m_rateLimits.Add(
                CreateLimits("empty"));

            RuleResult result = rule.Run(
                new RulesEngine(false),
                calls,
                new ServiceCallStats(calls));

            Assert.Empty(result.Violations);
        }

        [Fact]
        public void Run_HostlessMalformedAbsoluteUriThrowsFrozenExceptionType()
        {
            ServiceCallItem[] calls =
                CreateCalls(
                    "https://[",
                    string.Empty);
            CallFrequencyRule rule =
                CreateRule("/p");

            Exception expected =
                Record.Exception(
                    () => FrozenApplicableCalls(
                        rule,
                        calls));
            Exception actual =
                Record.Exception(
                    () => rule.Run(
                        new RulesEngine(false),
                        calls,
                        new ServiceCallStats(calls)));

            Assert.IsType<UriFormatException>(
                expected);
            Assert.NotNull(actual);
            Assert.Equal(
                expected.GetType(),
                actual.GetType());
        }

        private static CallFrequencyRule CreateRule(
            string subpath)
        {
            CallFrequencyRule rule =
                CreateEmptyRule();
            rule.m_rateLimits.Add(
                CreateLimits(
                    string.Empty,
                    subpath));
            return rule;
        }

        private static CallFrequencyRule CreateEmptyRule() =>
            new()
            {
                Name = "CallFrequency",
                Endpoint = "*",
            };

        private static CallFrequencyRule.RateLimits CreateLimits(
            string description,
            params string[] subpaths)
        {
            var limits =
                new CallFrequencyRule.RateLimits
                {
                    m_description = description,
                    m_burstTimePeriodSeconds = 15,
                    m_burstCallLimit = 1,
                    m_sustainedTimePeriodSeconds = 300,
                    m_sustainedCallLimit = 100,
                };
            limits.m_applicableSubpaths.AddRange(
                subpaths);
            return limits;
        }

        private static List<ServiceCallItem>[] FrozenApplicableCalls(
            CallFrequencyRule rule,
            IEnumerable<ServiceCallItem> items)
        {
            var pathCache =
                new Dictionary<ServiceCallItem, string>(
                    ReferenceEqualityComparer.Instance);
            return rule.m_rateLimits
                .Select(limits =>
                    items.Where(call =>
                    {
                        foreach (string subpath in
                            limits.m_applicableSubpaths)
                        {
                            if (!pathCache.TryGetValue(
                                    call,
                                    out string path))
                            {
                                path = FrozenGetPath(call);
                                pathCache.Add(
                                    call,
                                    path);
                            }
                            if (WildcardMatcher.IsMatch(
                                    subpath,
                                    path))
                            {
                                return true;
                            }
                        }
                        return false;
                    }).ToList())
                .ToArray();
        }

        private static (
            List<ViolationSnapshot> Violations,
            uint Sustained,
            uint Burst) FrozenOutcome(
                CallFrequencyRule rule,
                IReadOnlyList<List<ServiceCallItem>>
                    applicableCalls)
        {
            var violations =
                new List<ViolationSnapshot>();
            uint sustainedCount = 0;
            uint burstCount = 0;
            for (var index = 0;
                index < rule.m_rateLimits.Count;
                index++)
            {
                CallFrequencyRule.RateLimits limits =
                    rule.m_rateLimits[index];
                var sustained =
                    Utils.GetExcessCallsForTimeWindow(
                        applicableCalls[index],
                        limits
                            .m_sustainedTimePeriodSeconds *
                            1000,
                        limits.m_sustainedCallLimit);
                var burst =
                    Utils.GetExcessCallsForTimeWindow(
                        applicableCalls[index],
                        limits
                            .m_burstTimePeriodSeconds *
                            1000,
                        limits.m_burstCallLimit);
                foreach (List<ServiceCallItem> calls in
                    sustained)
                {
                    bool severe =
                        calls.Count >=
                        limits.m_sustainedCallLimit *
                        10;
                    string summary = severe
                        ? Localization.GetLocalizedString(
                            "LTA_FREQ_CALLS_VIOLATION_EXCEEDED",
                            limits.m_description,
                            limits.m_sustainedCallLimit *
                                10,
                            calls.Count,
                            limits
                                .m_sustainedTimePeriodSeconds)
                        : Localization.GetLocalizedString(
                            "LTA_FREQ_CALLS_VIOLATION_SUSTAINED",
                            limits.m_description,
                            limits.m_sustainedCallLimit,
                            calls.Count,
                            limits
                                .m_sustainedTimePeriodSeconds);
                    violations.Add(
                        ViolationSnapshot.Create(
                            severe
                                ? ViolationLevel.Error
                                : ViolationLevel.Warning,
                            rule.Endpoint,
                            summary,
                            calls));
                    sustainedCount++;
                }
                foreach (List<ServiceCallItem> calls in
                    burst)
                {
                    violations.Add(
                        ViolationSnapshot.Create(
                            ViolationLevel.Warning,
                            rule.Endpoint,
                            Localization
                                .GetLocalizedString(
                                    "LTA_FREQ_CALLS_VIOLATION_BURST",
                                    limits.m_description,
                                    limits
                                        .m_burstCallLimit,
                                    calls.Count,
                                    limits
                                        .m_burstTimePeriodSeconds),
                            calls));
                    burstCount++;
                }
            }
            return (
                violations,
                sustainedCount,
                burstCount);
        }

        private static List<ViolationSnapshot> Snapshot(
            RuleResult result) =>
            result.Violations
                .Select(violation =>
                    ViolationSnapshot.Create(
                        violation.m_level,
                        violation.m_endpoint,
                        violation.m_summary,
                        violation.m_violatingCalls))
                .ToList();

        private static string FrozenGetPath(
            ServiceCallItem call)
        {
            if (string.IsNullOrEmpty(call.Host) &&
                !HasScheme(call.Uri) &&
                Uri.TryCreate(
                    call.Uri,
                    UriKind.Relative,
                    out Uri relative))
            {
                return new Uri(
                    RelativeUriBase,
                    relative).AbsolutePath;
            }
            return new Uri(call.Uri).AbsolutePath;
        }

        private static bool HasScheme(
            string value)
        {
            int separator =
                value?.IndexOf(':') ?? -1;
            return separator > 0 &&
                Uri.CheckSchemeName(
                    value.Substring(
                        0,
                        separator));
        }

        private sealed record ViolationSnapshot(
            ViolationLevel Level,
            string Endpoint,
            string Summary,
            string CallIds)
        {
            internal static ViolationSnapshot Create(
                ViolationLevel level,
                string endpoint,
                string summary,
                IEnumerable<ServiceCallItem> calls) =>
                new(
                    level,
                    endpoint,
                    summary,
                    JoinCallIds(calls));
        }

        private static string JoinCallIds(
            IEnumerable<ServiceCallItem> calls) =>
            string.Join(
                ",",
                calls.Select(call => call.Id));

        private static ServiceCallItem[] CreateCalls(
            string uri,
            string host) =>
            new[]
            {
                CreateCall(
                    1,
                    uri,
                    host,
                    TimeSpan.Zero),
                CreateCall(
                    2,
                    uri,
                    host,
                    TimeSpan.FromSeconds(1)),
            };

        private static ServiceCallItem CreateCall(
            uint id,
            string uri,
            string host,
            TimeSpan offset) =>
            new(id)
            {
                Host = host,
                Uri = uri,
                ReqTimeUTC = (ulong)offset.Ticks,
            };
    }
}
