// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Globalization;
using System.Text;
using CaptureAnalysisEngine;

namespace XMAT.Tests
{
    public class RequestBodyIdentityTests
    {
        static RequestBodyIdentityTests()
        {
            Localization.LoadLanguage("en-us");
        }

        [Fact]
        public void ForcedHashCollisionStillUsesExactContent()
        {
            var identities =
                new RequestBodyIdentityInterner(
                    new ConstantHashOrdinalComparer());

            RequestBodyIdentity first =
                identities.GetOrAdd("first");
            RequestBodyIdentity second =
                identities.GetOrAdd("other");

            Assert.NotSame(first, second);
            Assert.Same(
                first,
                identities.GetOrAdd("first"));
            Assert.Equal(1, first.Value);
            Assert.Equal(2, second.Value);
            Assert.Equal(2, identities.IdentityCount);
            Assert.Equal(
                2,
                identities.RepresentativeCount);
        }

        [Fact]
        public void HashCollisionDoesNotMergeRepeatedCalls()
        {
            var identities =
                new RequestBodyIdentityInterner(
                    new ConstantHashOrdinalComparer());
            var first = new ServiceCallItem(1)
            {
                Uri = "https://example.test/path",
                ReqBody = "first",
                ReqBodyHash = 1,
                ReqTimeUTC = 0,
            };
            var second = new ServiceCallItem(2)
            {
                Uri = first.Uri,
                ReqBody = "other",
                ReqBodyHash = 1,
                ReqTimeUTC =
                    (ulong)TimeSpan.TicksPerSecond,
            };
            first.BodyIdentity =
                identities.GetOrAdd(
                    first.ReqBody);
            second.BodyIdentity =
                identities.GetOrAdd(
                    second.ReqBody);
            var rule = new RepeatedCallsRule
            {
                Name = "RepeatedCalls",
                Endpoint = "example.test",
                m_minAllowedRepeatIntervalMs = 5_000,
            };
            ServiceCallItem[] calls =
            {
                first,
                second,
            };

            RuleResult result = rule.Run(
                new RulesEngine(false),
                calls,
                new ServiceCallStats(calls));

            Assert.Empty(result.Violations);
        }

        [Fact]
        public void HashCollisionDoesNotMergePollingCalls()
        {
            var identities =
                new RequestBodyIdentityInterner(
                    new ConstantHashOrdinalComparer());
            RequestBodyIdentity first =
                identities.GetOrAdd("first");
            RequestBodyIdentity second =
                identities.GetOrAdd("other");
            ServiceCallItem[] calls =
                Enumerable.Range(
                        0,
                        6)
                    .Select(index =>
                        new ServiceCallItem(
                            (uint)(index + 1))
                        {
                            Uri =
                                "https://example.test/path",
                            ReqBody =
                                index % 2 == 0
                                    ? "first"
                                    : "other",
                            ReqBodyHash = 1,
                            BodyIdentity =
                                index % 2 == 0
                                    ? first
                                    : second,
                            ReqTimeUTC =
                                (ulong)index *
                                (ulong)TimeSpan
                                    .TicksPerSecond,
                        })
                    .ToArray();
            var rule = new PollingDetectionRule
            {
                Name = "Polling",
                Endpoint = "example.test",
            };

            RuleResult result = rule.Run(
                new RulesEngine(false),
                calls,
                new ServiceCallStats(calls));

            Assert.Empty(result.Violations);
        }

        [Fact]
        public void AsciiEquivalentBytesShareDecodedTextIdentity()
        {
            var identities =
                new RequestBodyIdentityInterner();
            string first =
                Encoding.ASCII.GetString(
                    new byte[] { 0x80 });
            string second =
                Encoding.ASCII.GetString(
                    new byte[] { 0x81 });

            Assert.Equal(first, second);
            Assert.Same(
                identities.GetOrAdd(first),
                identities.GetOrAdd(second));
        }

        [Fact]
        public void IdentityAssignmentDoesNotDependOnStringHashSeed()
        {
            string[] bodies =
            {
                "beta",
                "alpha",
                "beta",
                "gamma",
                "alpha",
            };

            Assert.Equal(
                Assign(
                    bodies,
                    new SaltedOrdinalComparer(
                        0x13579BDF)),
                Assign(
                    bodies,
                    new SaltedOrdinalComparer(
                        unchecked(
                            (int)0xA5A5A5A5))));
            Assert.Equal(
                new long[] { 1, 2, 1, 3, 2 },
                Assign(
                    bodies,
                    StringComparer.Ordinal));
        }

        [Fact]
        [Trait("Category", "Scaling")]
        public void ProductionInterner_OneHundredThousandDistinctBodiesCompletesWithinBound()
        {
            if (string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable(
                        "XMAT_SCALING_BENCHMARKS")))
            {
                return;
            }

            const int Count = 100_000;
            var identities =
                new RequestBodyIdentityInterner();
            RequestBodyIdentity last = null;
            Stopwatch elapsed =
                Stopwatch.StartNew();
            for (var index = 0;
                index < Count;
                index++)
            {
                last = identities.GetOrAdd(
                    "body-" +
                    index.ToString(
                        "D6",
                        CultureInfo.InvariantCulture));
            }
            elapsed.Stop();

            Assert.Equal(
                Count,
                identities.IdentityCount);
            Assert.Equal(
                Count,
                identities.RepresentativeCount);
            Assert.Equal(
                Count,
                last.Value);
            Assert.True(
                elapsed.Elapsed <
                    TimeSpan.FromSeconds(30),
                $"Interning {Count:N0} bodies took {elapsed.Elapsed}.");
        }

        [Fact]
        public void MissingBodyNeverUsesOneDefaultIdentity()
        {
            var resolver =
                new RequestBodyIdentityResolver();
            var first = new ServiceCallItem(1);
            var second = new ServiceCallItem(2);

            RequestBodyIdentity firstIdentity =
                resolver.Resolve(first);

            Assert.Same(
                firstIdentity,
                resolver.Resolve(first));
            Assert.NotSame(
                firstIdentity,
                resolver.Resolve(second));
        }

        [Fact]
        public void TelemetryCopyKeepsOriginalBodyIdentityAfterClearingBody()
        {
            string[] fields =
            {
                "0",
                "ABCD_12345678.CustomEvent",
                "2026-01-01T00:00:00Z",
                "3", "4", "5", "6", "7", "8", "9", "10", "11",
            };
            var original = new ServiceCallItem(1)
            {
                ConsoleIP = "10.0.0.1",
                Host = "data-vef.xboxlive.com",
                EndpointKind = EndpointKind.Captured,
                ReqBody = string.Join(
                    "|",
                    fields),
            };
            var data = new ServiceCallData(false);

            data.CreateFromServiceCallItems(
                new[] { original });

            ServiceCallItem converted = Assert.Single(
                data.m_perConsoleData["10.0.0.1"]
                    .m_servicesHistory[
                        new EndpointKey(
                            EndpointKind.SyntheticTelemetry,
                            "inGameEvents")]);
            Assert.Equal(
                string.Empty,
                converted.ReqBody);
            Assert.NotNull(
                original.BodyIdentity);
            Assert.Same(
                original.BodyIdentity,
                converted.BodyIdentity);
        }

        [Fact]
        public void ClearedTelemetryBodiesDoNotMergeDifferentOrigins()
        {
            ServiceCallItem Source(
                uint id,
                string time) =>
                new(id)
                {
                    ConsoleIP = "10.0.0.1",
                    Host =
                        "data-vef.xboxlive.com",
                    EndpointKind =
                        EndpointKind.Captured,
                    ReqTimeUTC = id,
                    ReqBody =
                        "0|ABCD_12345678.CustomEvent|" +
                        time +
                        "|3|4|5|6|7|8|9|10|11",
                };
            ServiceCallItem first =
                Source(
                    1,
                    "2026-01-01T00:00:00Z");
            ServiceCallItem second =
                Source(
                    2,
                    "2026-01-01T00:00:01Z");
            var data = new ServiceCallData(false);

            data.CreateFromServiceCallItems(
                new[] { first, second });

            ServiceCallItem[] converted =
                data.m_perConsoleData["10.0.0.1"]
                    .m_servicesHistory[
                        new EndpointKey(
                            EndpointKind.SyntheticTelemetry,
                            "inGameEvents")]
                    .ToArray();
            Assert.All(
                converted,
                item =>
                    Assert.Equal(
                        string.Empty,
                        item.ReqBody));
            Assert.Same(
                first.BodyIdentity,
                converted[0].BodyIdentity);
            Assert.Same(
                second.BodyIdentity,
                converted[1].BodyIdentity);
            Assert.NotSame(
                converted[0].BodyIdentity,
                converted[1].BodyIdentity);
        }

        private static long[] Assign(
            IEnumerable<string> bodies,
            IEqualityComparer<string> comparer)
        {
            var identities =
                new RequestBodyIdentityInterner(
                    comparer);
            return bodies
                .Select(body =>
                    identities
                        .GetOrAdd(body)
                        .Value)
                .ToArray();
        }

        private sealed class ConstantHashOrdinalComparer :
            IEqualityComparer<string>
        {
            public bool Equals(
                string left,
                string right) =>
                string.Equals(
                    left,
                    right,
                    StringComparison.Ordinal);

            public int GetHashCode(
                string value) =>
                1;
        }

        private sealed class SaltedOrdinalComparer :
            IEqualityComparer<string>
        {
            private readonly int _salt;

            internal SaltedOrdinalComparer(
                int salt)
            {
                _salt = salt;
            }

            public bool Equals(
                string left,
                string right) =>
                string.Equals(
                    left,
                    right,
                    StringComparison.Ordinal);

            public int GetHashCode(
                string value) =>
                StringComparer.Ordinal
                    .GetHashCode(value) ^
                _salt;
        }
    }
}
