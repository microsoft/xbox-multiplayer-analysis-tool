// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using CaptureAnalysisEngine;

namespace XMAT.Tests
{
    public class AnalysisWorkBudgetTests
    {
        [Fact]
        public void UnsortedRepeatedCallScans_FlushEveryChargedUnitIncludingRemainders()
        {
            const ulong Start = 638_000_000_000_000_000;
            const string Endpoint = "events";
            var calls =
                new List<ServiceCallItem>
                {
                    Call(1, Start),
                    Call(
                        2,
                        Start +
                        (1500UL *
                        (ulong)TimeSpan
                            .TicksPerMillisecond)),
                    Call(
                        3,
                        Start +
                        (500UL *
                        (ulong)TimeSpan
                            .TicksPerMillisecond)),
                };
            var budget =
                new AnalysisWorkBudget(
                    long.MaxValue);
            var rule =
                new RepeatedCallsRule
                {
                    Endpoint = Endpoint,
                    Name = "RepeatedCalls_Rule",
                };

            RuleResult result = rule.Run(
                new RulesEngine(
                    false,
                    budget),
                calls,
                new ServiceCallStats(calls));

            Assert.Equal(23, budget.Used);
            Assert.Equal(
                new uint[] { 1, 2, 3 },
                Assert.Single(
                    result.Violations)
                    .m_violatingCalls
                    .Select(call => call.Id));

            static ServiceCallItem Call(
                uint id,
                ulong time) =>
                new(id)
                {
                    Host = "events",
                    Uri = "https://events/inGameEvents",
                    ReqBodyHash = 7,
                    ReqBody = "7",
                    ReqTimeUTC = time,
                };
        }

        [Fact]
        public void FallbackRegexTimeoutBecomesWorkLimit()
        {
            var budget =
                new AnalysisWorkBudget(
                    fallbackMatcher:
                        static (_, _) =>
                            throw new RegexMatchTimeoutException());
            var matcher =
                new InputRegex("(a)\\1");

            AnalysisWorkLimitException exception =
                Assert.Throws<AnalysisWorkLimitException>(
                    () => matcher.IsMatch(
                        "aa",
                        budget,
                        CancellationToken.None));

            Assert.DoesNotContain(
                "(a)",
                exception.Message,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "aa",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Throws<AnalysisWorkLimitException>(
                () => budget.Charge());
            Assert.Equal(0, budget.Used);
        }

        [Fact]
        public void CancellationPrecedesFallbackRegexTimeout()
        {
            using var cancellation =
                new CancellationTokenSource();
            var budget =
                new AnalysisWorkBudget(
                    fallbackMatcher:
                        (_, _) =>
                        {
                            cancellation.Cancel();
                            throw new RegexMatchTimeoutException();
                        });
            var matcher =
                new InputRegex("(a)\\1");

            Assert.Throws<OperationCanceledException>(
                () => matcher.IsMatch(
                    "aa",
                    budget,
                    cancellation.Token));

            budget.Charge();
            Assert.Equal(0, budget.FallbackMatchElapsed);
        }

        [Fact]
        public void DistinctFallbackAttemptsAcrossMatchersShareElapsedAllowance()
        {
            long timestamp = 0;
            var budget =
                new AnalysisWorkBudget(
                    fallbackMatchAllowance:
                        TimeSpan.FromMilliseconds(100),
                    fallbackTimestamp:
                        () => timestamp,
                    fallbackTimestampFrequency: 1_000,
                    fallbackMatcher:
                        (_, input) =>
                        {
                            timestamp++;
                            return input == "match";
                        });
            InputRegex[] matchers =
            {
                new("(a)\\1"),
                new("(b)\\1"),
            };

            Assert.True(
                matchers[0].IsMatch(
                    "match",
                    budget,
                    CancellationToken.None));
            for (var index = 0;
                index < 99;
                index++)
            {
                Assert.False(
                    matchers[index % matchers.Length].IsMatch(
                        $"input-{index}",
                        budget,
                        CancellationToken.None));
            }
            Assert.Throws<AnalysisWorkLimitException>(
                () => matchers[1].IsMatch(
                    "input-99",
                    budget,
                    CancellationToken.None));
            Assert.Equal(
                101,
                budget.FallbackMatchElapsed);
            Assert.Equal(0, budget.Used);
        }
    }
}
