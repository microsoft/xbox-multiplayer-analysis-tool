// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using XMAT.NetworkTrace.Models;

namespace XMAT.NetworkTraceCaptureAnalysis
{
    internal sealed record NetworkTraceStatisticsOptions(int MaximumMtu, int PacketsPerSecondThreshold, int DuplicateWindowMilliseconds);
    internal sealed record NetworkTraceStatistic(string Id, string Group, long Value);
    internal sealed record NetworkTraceStatisticsResult(
        int TotalPackets, NetworkTraceStatisticsOptions Options, IReadOnlyList<NetworkTraceStatistic> Statistics);

    internal static class NetworkTraceStatistics
    {
        internal static NetworkTraceStatisticsResult Analyze(
            IReadOnlyList<NetworkTracePacketDataModel> packets,
            NetworkTraceStatisticsOptions options,
            Action<string> log = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(packets);
            ArgumentNullException.ThrowIfNull(options);
            var statistics = new List<NetworkTraceStatistic>();
            foreach (var group in packets.GroupBy(Protocol).OrderBy(group => group.Key))
            {
                cancellationToken.ThrowIfCancellationRequested();
                statistics.Add(new("packet.protocol", group.Key.ToString(CultureInfo.InvariantCulture), group.LongCount()));
            }
            foreach (var group in packets.GroupBy(Source).OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sent = group.Where(packet => (packet.Flags & NetworkPacketFlags.Send) != 0).ToArray();
                var received = group.Where(packet => (packet.Flags & NetworkPacketFlags.Receive) != 0).ToArray();
                if (sent.Length != 0)
                {
                    statistics.Add(new("packet.sent.count", group.Key, sent.LongLength));
                    statistics.Add(new("packet.sent.bytes", group.Key, sent.Sum(packet => (long)packet.Payload.Length)));
                }
                if (received.Length != 0)
                {
                    statistics.Add(new("packet.received.count", group.Key, received.LongLength));
                    statistics.Add(new("packet.received.bytes", group.Key, received.Sum(packet => (long)packet.Payload.Length)));
                }
                statistics.Add(new("packet.source.average-bytes", group.Key, (long)group.Average(packet => packet.Payload.Length)));
            }
            statistics.Add(new("packet.udp-over-mtu", null,
                packets.LongCount(packet => Protocol(packet) == (int)NetworkProtocol.UDP && packet.Payload.Length > options.MaximumMtu)));
            long overThreshold = 0;
            foreach (var bucket in packets.Where(packet => (packet.Flags & NetworkPacketFlags.Send) != 0)
                .GroupBy(packet => (Second: packet.Timestamp.ToUniversalTime().Ticks / TimeSpan.TicksPerSecond, Source: Source(packet))))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = bucket.LongCount();
                if (count > options.PacketsPerSecondThreshold)
                {
                    overThreshold++;
                    log?.Invoke($"{bucket.Key.Source} at second {bucket.Key.Second} sent {count} packets; threshold {options.PacketsPerSecondThreshold}");
                }
            }
            statistics.Add(new("packet.send-second-over-threshold", null, overThreshold));
            var ordered = packets.OrderBy(packet => packet.Timestamp.ToUniversalTime()).ToArray();
            long duplicates = 0;
            for (var index = 0; index < ordered.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var start = ordered[index];
                for (var next = index + 1; next < ordered.Length; next++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((ordered[next].Timestamp.ToUniversalTime() - start.Timestamp.ToUniversalTime()).TotalMilliseconds >
                        options.DuplicateWindowMilliseconds)
                        break;
                    if (start.Payload.AsSpan().SequenceEqual(ordered[next].Payload))
                    {
                        duplicates++;
                        log?.Invoke($"Matching packet payloads from {Source(ordered[next])} at {ordered[next].Timestamp:O}");
                    }
                }
            }
            statistics.Add(new("packet.matching-payload-pairs", null, duplicates));
            cancellationToken.ThrowIfCancellationRequested();
            return new(packets.Count, options, statistics.AsReadOnly());
        }

        internal static int Protocol(NetworkTracePacketDataModel packet) =>
            packet.RawIpHeader.Version == 4 ? packet.Protocol : (int)NetworkProtocol.Unknown;

        internal static string Source(NetworkTracePacketDataModel packet) =>
            packet.RawIpHeader.Version == 4 ? packet.SourceIpv4Address : null;

        internal static string Destination(NetworkTracePacketDataModel packet) =>
            packet.RawIpHeader.Version == 4 ? packet.DestinationIpv4Address : null;
    }
}
