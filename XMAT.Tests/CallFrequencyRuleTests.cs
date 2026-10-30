// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using CaptureAnalysisEngine;

namespace XMAT.Tests
{
    public class CallFrequencyRuleTests
    {
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
        public void Run_HostlessMalformedAbsoluteUriStillFails()
        {
            ServiceCallItem[] calls =
                CreateCalls(
                    "https://[",
                    string.Empty);
            CallFrequencyRule rule =
                CreateRule("/p");

            Assert.Throws<UriFormatException>(
                () => rule.Run(
                    new RulesEngine(false),
                    calls,
                    new ServiceCallStats(calls)));
        }

        private static CallFrequencyRule CreateRule(
            string subpath)
        {
            var rule = new CallFrequencyRule
            {
                Name = "CallFrequency",
                Endpoint = "*",
            };
            var limits =
                new CallFrequencyRule.RateLimits
                {
                    m_burstTimePeriodSeconds = 15,
                    m_burstCallLimit = 1,
                    m_sustainedTimePeriodSeconds = 300,
                    m_sustainedCallLimit = 100,
                };
            limits.m_applicableSubpaths.Add(
                subpath);
            rule.m_rateLimits.Add(limits);
            return rule;
        }

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
