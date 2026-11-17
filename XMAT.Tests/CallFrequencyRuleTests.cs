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

        // Text that neither parse reads, such as a malformed URL kept as written for an unknown scheme, is matched as it is,
        // with or without a host, and nothing throws; the path once threw for it, which failed the whole analysis. This
        // supersedes the frozen exception type that a hostless malformed URL once gave.
        [Theory]
        [InlineData("https://[", "")]
        [InlineData("https://[", "example.test")]
        [InlineData("http:x", "example.test")]
        public void Run_TextThatNoParseReads_IsMatchedAsItIs(
            string uri,
            string host)
        {
            ServiceCallItem[] calls =
                CreateCalls(
                    uri,
                    host);

            RuleResult matching = CreateRule(uri).Run(
                new RulesEngine(false),
                calls,
                new ServiceCallStats(calls));
            RuleResult other = CreateRule("/p").Run(
                new RulesEngine(false),
                calls,
                new ServiceCallStats(calls));

            Assert.Equal(
                calls,
                Assert.Single(matching.Violations).m_violatingCalls.ToArray());
            Assert.Empty(other.Violations);
            Assert.All(
                calls,
                call => Assert.Equal((uri, host), (call.Uri, call.Host)));
        }

        // A row with a host whose scheme makes no URL keeps its path as the call's URI, which limits match like a hostless
        // row's path; such a URI once threw, as a relative URI with a host still failed.
        [Fact]
        public void Run_MatchesThePathKeptForARowWithAHost()
        {
            ServiceCallItem[] calls =
                CreateCalls(
                    "/p?q=1#f",
                    "example.test");

            RuleResult result = CreateRule("/p").Run(
                new RulesEngine(false),
                calls,
                new ServiceCallStats(calls));

            Assert.Equal(
                calls,
                Assert.Single(result.Violations).m_violatingCalls.ToArray());
        }

        // The path is the frozen one wherever that gave one: every URI of a hostless row, and every URI with a scheme, which
        // every URI is that analysis makes for a row with a host and a scheme that makes a URL.
        [Theory]
        [InlineData("https://example.test/p?q=1#f", "example.test")]
        [InlineData("http://example.test:8080/a%20b/c", "example.test")]
        [InlineData("ftp://files.example.test/f", "files.example.test")]
        [InlineData("get://example.test/v1/items", "example.test")]
        [InlineData("file:///C:/x", "example.test")]
        [InlineData("https://example.test/p", "")]
        [InlineData("/p?q=1#f", "")]
        [InlineData("/a%20b", "")]
        [InlineData("relative/p", "")]
        [InlineData("*", "")]
        [InlineData("", "")]
        [InlineData("//server/x", "")]
        [InlineData("\\\\server\\x", "")]
        [InlineData("C:\\x", "")]
        public void GetPath_IsTheFrozenPath_WhereThatGaveOne(
            string uri,
            string host)
        {
            ServiceCallItem call =
                CreateCall(
                    1,
                    uri,
                    host,
                    TimeSpan.Zero);

            Assert.Equal(
                FrozenGetPath(call),
                CallFrequencyRule.GetPath(call));
        }

        // Where the frozen path threw, the path is the text as it is when neither parse reads it, and a path without a
        // scheme is parsed against the base whatever the host.
        [Theory]
        [InlineData("https://[", "", "https://[", true)]
        [InlineData("https://[", "example.test", "https://[", true)]
        [InlineData("http:x", "example.test", "http:x", true)]
        [InlineData("https:/x", "", "https:/x", true)]
        [InlineData("a:b", "", "a:b", true)]
        [InlineData("/p?q=1#f", "example.test", "/p", true)]
        [InlineData("*", "example.test", "/*", true)]
        [InlineData("\\\\server\\x", "example.test", "//server/x", false)]
        [InlineData("//server/x", "example.test", "/x", false)]
        public void GetPath_NeverThrows(
            string uri,
            string host,
            string expected,
            bool frozenThrew)
        {
            ServiceCallItem call =
                CreateCall(
                    1,
                    uri,
                    host,
                    TimeSpan.Zero);

            Assert.Equal(
                expected,
                CallFrequencyRule.GetPath(call));
            Assert.Equal(
                frozenThrew,
                Record.Exception(() => FrozenGetPath(call)) is UriFormatException);
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
