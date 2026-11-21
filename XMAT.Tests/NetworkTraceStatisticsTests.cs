// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.NetworkTrace;
using XMAT.NetworkTrace.Models;
using XMAT.NetworkTraceCaptureAnalysis;

namespace XMAT.Tests
{
    public class NetworkTraceStatisticsTests
    {
        [Fact]
        public void ExplicitPacketsUseActualBytesAndTimestampOrderForMatchingPayloadPairs()
        {
            using var database = CaptureInvestigationTestData.NetworkDatabase();
            var second = CaptureInvestigationTestData.AddPacket(database, CaptureInvestigationTestData.Start.AddMilliseconds(10));
            var first = CaptureInvestigationTestData.AddPacket(database, CaptureInvestigationTestData.Start);
            CaptureInvestigationTestData.AddPacket(database, CaptureInvestigationTestData.Start.AddDays(1), marker: 1);
            var packets = database.TableByName(NetworkTraceCaptureMethod.TableName).Dataset.DataRecords
                .Select(record => new NetworkTracePacketDataModel(record)).ToArray();
            Assert.NotSame(packets[0].Payload, packets[1].Payload);
            Assert.Equal(packets[0].Payload, packets[1].Payload);
            Assert.Equal(second, packets[0].RecordRowId);
            Assert.Equal(first, packets[1].RecordRowId);
            var result = NetworkTraceStatistics.Analyze(packets, new NetworkTraceStatisticsOptions(30, 2, 1000));
            Assert.Equal(1, result.Statistics.Single(statistic => statistic.Id == "packet.matching-payload-pairs").Value);
            Assert.Equal(0, result.Statistics.Single(statistic => statistic.Id == "packet.send-second-over-threshold").Value);
            Assert.Equal(3, result.Statistics.Single(statistic => statistic.Id == "packet.udp-over-mtu").Value);
            Assert.Equal(120, result.Statistics.Single(statistic => statistic.Id == "packet.sent.bytes").Value);
            Assert.Equal(3, result.TotalPackets);
        }

        [Fact]
        public void ShortPacketsRemainUnknownInsteadOfBeingReportedAsAnIpv4Protocol()
        {
            using var database = CaptureInvestigationTestData.NetworkDatabase();
            CaptureInvestigationTestData.AddPacket(database, CaptureInvestigationTestData.Start, shortPacket: true);
            var packet = new NetworkTracePacketDataModel(database.TableByName(NetworkTraceCaptureMethod.TableName).Dataset.DataRecords.Single());
            var result = NetworkTraceStatistics.Analyze([packet], new NetworkTraceStatisticsOptions(1500, 20, 1000));
            Assert.Equal("-1", result.Statistics.Single(statistic => statistic.Id == "packet.protocol").Group);
            Assert.Null(NetworkTraceStatistics.Source(packet));
            Assert.Null(NetworkTraceStatistics.Destination(packet));
        }

        [Fact]
        public void CancellationNeverPublishesPartialPacketStatistics()
        {
            using var database = CaptureInvestigationTestData.NetworkDatabase();
            CaptureInvestigationTestData.AddPacket(database, CaptureInvestigationTestData.Start);
            var packet = new NetworkTracePacketDataModel(database.TableByName(NetworkTraceCaptureMethod.TableName).Dataset.DataRecords.Single());
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => NetworkTraceStatistics.Analyze(
                [packet], new NetworkTraceStatisticsOptions(1500, 20, 1000), cancellationToken: cancelled.Token));
        }
    }
}
