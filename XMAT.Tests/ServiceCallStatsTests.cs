// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using CaptureAnalysisEngine;

namespace XMAT.Tests
{
    public class ServiceCallStatsTests
    {
        private static List<ServiceCallItem> CreateCallItems(int count, ulong baseTimeUtc, ulong intervalMs, ulong elapsedMs = 50)
        {
            var items = new List<ServiceCallItem>();
            for (uint i = 0; i < count; i++)
            {
                items.Add(new ServiceCallItem(i)
                {
                    ReqTimeUTC = baseTimeUtc + (ulong)(i * (long)intervalMs * TimeSpan.TicksPerMillisecond),
                    ElapsedCallTimeMs = elapsedMs,
                    HttpStatusCode = 200
                });
            }
            return items;
        }

        [Fact]
        public void GatherStats_CountsCalls()
        {
            var baseTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();
            var items = CreateCallItems(10, baseTime, 100);

            var stats = new ServiceCallStats(items);

            Assert.Equal(10ul, stats.m_numCalls);
        }

        [Fact]
        public void GatherStats_CalculatesAvgElapsedCallTime()
        {
            var baseTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();
            var items = CreateCallItems(5, baseTime, 100, elapsedMs: 200);

            var stats = new ServiceCallStats(items);

            Assert.Equal(200ul, stats.m_avgElapsedCallTimeMs);
        }

        [Fact]
        public void GatherStats_TracksMaxElapsedCallTime()
        {
            var baseTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();
            var items = new List<ServiceCallItem>
            {
                new ServiceCallItem(0) { ReqTimeUTC = baseTime, ElapsedCallTimeMs = 100 },
                new ServiceCallItem(1) { ReqTimeUTC = baseTime + (ulong)TimeSpan.TicksPerMillisecond * 100, ElapsedCallTimeMs = 500 },
                new ServiceCallItem(2) { ReqTimeUTC = baseTime + (ulong)TimeSpan.TicksPerMillisecond * 200, ElapsedCallTimeMs = 200 }
            };

            var stats = new ServiceCallStats(items);

            Assert.Equal(500ul, stats.m_maxElapsedCallTimeMs);
        }

        [Fact]
        public void GatherStats_IgnoresShoulderTaps()
        {
            var baseTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();
            var items = new List<ServiceCallItem>
            {
                new ServiceCallItem(0) { ReqTimeUTC = baseTime, ElapsedCallTimeMs = 100 },
                new ServiceCallItem(1) { ReqTimeUTC = baseTime + 1000, ElapsedCallTimeMs = 100, IsShoulderTap = true },
                new ServiceCallItem(2) { ReqTimeUTC = baseTime + 2000, ElapsedCallTimeMs = 100 }
            };

            var stats = new ServiceCallStats(items);

            Assert.Equal(2ul, stats.m_numCalls);
        }

        [Fact]
        public void GatherStats_TracksRequestBodyHashCounts()
        {
            var baseTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();
            var items = new List<ServiceCallItem>
            {
                new ServiceCallItem(0) { ReqTimeUTC = baseTime, ReqBodyHash = 111, ElapsedCallTimeMs = 50 },
                new ServiceCallItem(1) { ReqTimeUTC = baseTime + (ulong)TimeSpan.TicksPerMillisecond * 100, ReqBodyHash = 111, ElapsedCallTimeMs = 50 },
                new ServiceCallItem(2) { ReqTimeUTC = baseTime + (ulong)TimeSpan.TicksPerMillisecond * 200, ReqBodyHash = 222, ElapsedCallTimeMs = 50 }
            };

            var stats = new ServiceCallStats(items);

            Assert.Equal(2, stats.m_reqBodyHashCountMap.Count);
            Assert.Equal(2u, stats.m_reqBodyHashCountMap[111]);
            Assert.Equal(1u, stats.m_reqBodyHashCountMap[222]);
        }

        [Fact]
        public void GatherStats_HandlesSingleItem()
        {
            var baseTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();
            var items = new List<ServiceCallItem>
            {
                new ServiceCallItem(0) { ReqTimeUTC = baseTime, ElapsedCallTimeMs = 150 }
            };

            var stats = new ServiceCallStats(items);

            Assert.Equal(1ul, stats.m_numCalls);
            Assert.Equal(150ul, stats.m_avgElapsedCallTimeMs);
            Assert.Equal(150ul, stats.m_maxElapsedCallTimeMs);
        }

        [Fact]
        public void Stringify_ProducesNonEmptyString()
        {
            var baseTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();
            var items = CreateCallItems(3, baseTime, 100);
            var stats = new ServiceCallStats(items);

            string result = stats.Stringify();
            Assert.NotEmpty(result);
            Assert.Contains(",", result);
        }
    }
}
