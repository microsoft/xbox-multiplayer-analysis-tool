// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CaptureAnalysisEngine;

namespace XMAT.Tests
{
    public class ServiceCallDataTests
    {
        [Fact]
        public void CreateFromServiceCallItems_GroupsByConsoleIP()
        {
            var baseTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();
            var items = new List<ServiceCallItem>
            {
                new ServiceCallItem(0)
                {
                    ConsoleIP = "10.0.0.1",
                    Host = "api.host.com",
                    ReqTimeUTC = baseTime,
                    ElapsedCallTimeMs = 50
                },
                new ServiceCallItem(1)
                {
                    ConsoleIP = "10.0.0.2",
                    Host = "api.host.com",
                    ReqTimeUTC = baseTime + (ulong)TimeSpan.TicksPerMillisecond * 100,
                    ElapsedCallTimeMs = 60
                },
                new ServiceCallItem(2)
                {
                    ConsoleIP = "10.0.0.1",
                    Host = "api.host.com",
                    ReqTimeUTC = baseTime + (ulong)TimeSpan.TicksPerMillisecond * 200,
                    ElapsedCallTimeMs = 70
                }
            };

            var data = new ServiceCallData(false);
            data.CreateFromServiceCallItems(items);

            Assert.Equal(2, data.m_perConsoleData.Count);
            Assert.True(data.m_perConsoleData.ContainsKey("10.0.0.1"));
            Assert.True(data.m_perConsoleData.ContainsKey("10.0.0.2"));
        }

        [Fact]
        public void CreateFromServiceCallItems_GroupsByHost()
        {
            var baseTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();
            var items = new List<ServiceCallItem>
            {
                new ServiceCallItem(0)
                {
                    ConsoleIP = "10.0.0.1",
                    Host = "api1.host.com",
                    ReqTimeUTC = baseTime,
                    ElapsedCallTimeMs = 50
                },
                new ServiceCallItem(1)
                {
                    ConsoleIP = "10.0.0.1",
                    Host = "api2.host.com",
                    ReqTimeUTC = baseTime + (ulong)TimeSpan.TicksPerMillisecond * 100,
                    ElapsedCallTimeMs = 60
                }
            };

            var data = new ServiceCallData(false);
            data.CreateFromServiceCallItems(items);

            var consoleData = data.m_perConsoleData["10.0.0.1"];
            Assert.Equal(2, consoleData.m_servicesHistory.Count);
            Assert.True(consoleData.m_servicesHistory.ContainsKey("api1.host.com"));
            Assert.True(consoleData.m_servicesHistory.ContainsKey("api2.host.com"));
        }

        [Fact]
        public void CreateFromServiceCallItems_SkipsEmptyConsoleIP()
        {
            var items = new List<ServiceCallItem>
            {
                new ServiceCallItem(0)
                {
                    ConsoleIP = "",
                    Host = "api.host.com",
                    ReqTimeUTC = (ulong)DateTime.UtcNow.ToFileTimeUtc(),
                    ElapsedCallTimeMs = 50
                }
            };

            var data = new ServiceCallData(false);
            data.CreateFromServiceCallItems(items);

            Assert.Empty(data.m_perConsoleData);
        }

        [Fact]
        public void CreateFromServiceCallItems_ComputesStats()
        {
            var baseTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();
            var items = new List<ServiceCallItem>
            {
                new ServiceCallItem(0)
                {
                    ConsoleIP = "10.0.0.1",
                    Host = "api.host.com",
                    ReqTimeUTC = baseTime,
                    ElapsedCallTimeMs = 100
                },
                new ServiceCallItem(1)
                {
                    ConsoleIP = "10.0.0.1",
                    Host = "api.host.com",
                    ReqTimeUTC = baseTime + (ulong)TimeSpan.TicksPerMillisecond * 200,
                    ElapsedCallTimeMs = 300
                }
            };

            var data = new ServiceCallData(false);
            data.CreateFromServiceCallItems(items);

            var stats = data.m_perConsoleData["10.0.0.1"].m_servicesStats["api.host.com"];
            Assert.Equal(2ul, stats.m_numCalls);
        }

        [Fact]
        public void CreateFromServiceCallItems_OrdersByReqTimeUTC()
        {
            var baseTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();
            var items = new List<ServiceCallItem>
            {
                new ServiceCallItem(0)
                {
                    ConsoleIP = "10.0.0.1",
                    Host = "api.host.com",
                    ReqTimeUTC = baseTime + (ulong)TimeSpan.TicksPerMillisecond * 200,
                    ElapsedCallTimeMs = 50
                },
                new ServiceCallItem(1)
                {
                    ConsoleIP = "10.0.0.1",
                    Host = "api.host.com",
                    ReqTimeUTC = baseTime,
                    ElapsedCallTimeMs = 50
                }
            };

            var data = new ServiceCallData(false);
            data.CreateFromServiceCallItems(items);

            var history = data.m_perConsoleData["10.0.0.1"].m_servicesHistory["api.host.com"];
            Assert.True(history.First.Value.ReqTimeUTC <= history.Last.Value.ReqTimeUTC);
        }
    }
}
