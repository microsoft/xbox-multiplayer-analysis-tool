// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using CaptureAnalysisEngine;

namespace XMAT.Tests
{
    public class SmallBatchDetectionRuleIsolationTests
    {
        static SmallBatchDetectionRuleIsolationTests()
        {
            Localization.LoadLanguage("en-us");
        }

        [Fact]
        public void CloneAndRepeatedRunsDoNotShareMutableCounts()
        {
            var source = new SmallBatchDetectionRule
            {
                Name = "SmallBatch",
                Endpoint = "example.test",
                m_minBatchXUIDsPerBatchCall = 2,
            };
            source.m_MatchPatterns.Add(
                "/batch",
                "xuids");
            var first =
                (SmallBatchDetectionRule)source.Clone();
            var second =
                (SmallBatchDetectionRule)source.Clone();
            ServiceCallItem[] calls =
            {
                new(1)
                {
                    Uri = "/batch",
                    ReqBody =
                        "{\"xuids\":[\"1\"]}",
                },
            };

            RuleResult firstRun = first.Run(
                new RulesEngine(false),
                calls,
                new ServiceCallStats(calls));
            RuleResult firstAgain = first.Run(
                new RulesEngine(false),
                calls,
                new ServiceCallStats(calls));
            RuleResult secondRun = second.Run(
                new RulesEngine(false),
                Array.Empty<ServiceCallItem>(),
                new ServiceCallStats(
                    Array.Empty<ServiceCallItem>()));

            foreach ((string key, object value) in
                firstRun.Results)
            {
                Assert.Equal(
                    value,
                    firstAgain.Results[key]);
            }
            Assert.Single(
                first.m_patternInstancesFound);
            Assert.Single(firstRun.Violations);
            Assert.Single(firstAgain.Violations);
            Assert.Equal(
                Tuple.Create(0, 0),
                Assert.Single(
                    second.m_patternInstancesFound));
            Assert.Empty(secondRun.Violations);
        }

        [Fact]
        public void CustomBacktrackingPatternIsNotDiscardedForShortInput()
        {
            var rule = new SmallBatchDetectionRule
            {
                Name = "CustomPattern",
                Endpoint = "example.test",
                m_minBatchXUIDsPerBatchCall = 2,
            };
            rule.m_MatchPatterns.Add(
                "(/batch)\\1",
                "xuids");
            ServiceCallItem[] calls =
            {
                new(1)
                {
                    Uri = "/batch/batch",
                    ReqBody =
                        "{\"xuids\":[\"1\"]}",
                },
            };

            RuleResult result = rule.Run(
                new RulesEngine(false),
                calls,
                new ServiceCallStats(calls));

            Assert.Single(result.Violations);
        }
    }
}
