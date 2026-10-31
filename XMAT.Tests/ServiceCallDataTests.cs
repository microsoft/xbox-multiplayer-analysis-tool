// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

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
            Assert.Contains(consoleData.m_servicesHistory.Keys, key => key.LogicalName == "api1.host.com");
            Assert.Contains(consoleData.m_servicesHistory.Keys, key => key.LogicalName == "api2.host.com");
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

            var stats = Assert.Single(
                data.m_perConsoleData["10.0.0.1"].m_servicesStats,
                entry => entry.Key.LogicalName == "api.host.com").Value;
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

            var history = Assert.Single(
                data.m_perConsoleData["10.0.0.1"].m_servicesHistory,
                entry => entry.Key.LogicalName == "api.host.com").Value;
            Assert.True(history.First.Value.ReqTimeUTC <= history.Last.Value.ReqTimeUTC);
        }

        [Fact]
        public void Cs1LargeTelemetryLinePreservesFieldsAndDelimiters()
        {
            const int FieldCount = 10_000;
            var fields = new string[FieldCount];
            fields[0] = "0";
            fields[1] =
                "ABCD_12345678.CustomEvent";
            fields[2] =
                "2026-01-01T00:00:00Z";
            for (var index = 3;
                index < fields.Length;
                index++)
            {
                fields[index] =
                    index % 3 == 0
                        ? string.Empty
                        : $"value-{index}";
            }
            var call = new ServiceCallItem(1)
            {
                ConsoleIP = "10.0.0.1",
                Host = "data-vef.xboxlive.com",
                EndpointKind = EndpointKind.Captured,
                ReqBody = string.Join("|", fields),
            };
            var data = new ServiceCallData(false);

            data.CreateFromServiceCallItems(
                new[] { call });

            ServiceCallItem converted = Assert.Single(
                data.m_perConsoleData["10.0.0.1"]
                    .m_servicesHistory[
                        new EndpointKey(
                            EndpointKind.SyntheticTelemetry,
                            "inGameEvents")]);
            Assert.Equal(
                string.Join(
                    "|",
                    fields,
                    1,
                    fields.Length - 1) +
                    "|",
                converted.Dimensions);
        }
    }
}
