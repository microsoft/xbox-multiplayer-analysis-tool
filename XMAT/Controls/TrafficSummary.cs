// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using XMAT.WebServiceCapture.Models;

namespace XMAT
{
    // A response's status class, as the capture timeline colors it: none covers requests without a status yet and any
    // status that isn't 2xx to 5xx.
    public enum TrafficStatusClass
    {
        Success,
        Redirect,
        ClientError,
        ServerError,
        None,
    }

    // What the traffic overview shows for a set of rows: how many there are, how many fall in each status class, the
    // three busiest hosts, and how many requests arrived in each second of the capture.
    internal sealed class TrafficSummary
    {
        internal const int ClassCount = 5;
        internal const int BusiestHostCount = 3;

        private TrafficSummary(
            int total, int[] classCounts, IReadOnlyList<(string Host, int Count)> busiestHosts, double[] perSecond,
            int? lastSecondCount)
        {
            Total = total;
            ClassCounts = classCounts;
            BusiestHosts = busiestHosts;
            RequestsPerSecond = perSecond;
            LastSecondCount = lastSecondCount;
        }

        internal static TrafficSummary Empty { get; } =
            new(0, new int[ClassCount], Array.Empty<(string, int)>(), Array.Empty<double>(), null);

        internal int Total { get; }

        // Counts by TrafficStatusClass.
        internal IReadOnlyList<int> ClassCounts { get; }

        // At most three hosts, busiest first; hosts with the same count in ordinal order.
        internal IReadOnlyList<(string Host, int Count)> BusiestHosts { get; }

        // Requests per second from the first request's second to the last second: the latest completed second while
        // capturing, the last request's second otherwise. Longer captures average neighboring seconds so that there are at
        // most maxPoints values, the newest last.
        internal IReadOnlyList<double> RequestsPerSecond { get; }

        // While capturing, the requests in the last completed second; null otherwise.
        internal int? LastSecondCount { get; }

        internal static TrafficStatusClass ClassOf(string status)
        {
            int hundreds = int.TryParse(status, out int code) ? code / 100 : 0;
            return hundreds switch
            {
                2 => TrafficStatusClass.Success,
                3 => TrafficStatusClass.Redirect,
                4 => TrafficStatusClass.ClientError,
                >= 5 => TrafficStatusClass.ServerError,
                _ => TrafficStatusClass.None,
            };
        }

        // Summarizes ROWS, the ProxyConnectionModel rows a table shows. NOW is the local time, like the rows' request times.
        internal static TrafficSummary Compute(IEnumerable rows, bool capturing, DateTime now, int maxPoints)
        {
            if (rows == null)
            {
                return Empty;
            }

            var classCounts = new int[ClassCount];
            var hosts = new Dictionary<string, int>(StringComparer.Ordinal);
            var seconds = new Dictionary<long, int>();
            long first = long.MaxValue;
            long last = long.MinValue;
            int total = 0;
            foreach (object item in rows)
            {
                if (item is not ProxyConnectionModel row)
                {
                    continue;
                }

                total++;
                classCounts[(int)ClassOf(row.Status)]++;
                if (!string.IsNullOrEmpty(row.Host))
                {
                    hosts[row.Host] = hosts.GetValueOrDefault(row.Host) + 1;
                }

                if (row.RequestTime != DateTime.MinValue)
                {
                    long second = row.RequestTime.Ticks / TimeSpan.TicksPerSecond;
                    seconds[second] = seconds.GetValueOrDefault(second) + 1;
                    first = Math.Min(first, second);
                    last = Math.Max(last, second);
                }
            }

            int? lastSecondCount = null;
            if (capturing)
            {
                last = now.Ticks / TimeSpan.TicksPerSecond - 1;
                lastSecondCount = seconds.GetValueOrDefault(last);
            }

            var busiest = hosts
                .OrderByDescending(entry => entry.Value)
                .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                .Take(BusiestHostCount)
                .Select(entry => (entry.Key, entry.Value))
                .ToArray();
            return new TrafficSummary(
                total, classCounts, busiest, PerSecond(seconds, first, last, maxPoints), lastSecondCount);
        }

        // Shortens HOST to its first label, as "sessiondirectory" for sessiondirectory.xboxlive.com, unless it is an
        // address or another of the SHOWN hosts starts with the same label.
        internal static string ShortHostName(string host, IEnumerable<string> shown)
        {
            int dot = host?.IndexOf('.') ?? -1;
            if (dot <= 0 || IPAddress.TryParse(host.Trim('[', ']'), out _))
            {
                return host;
            }

            string label = host.Substring(0, dot + 1);
            return shown.Any(other => !string.Equals(other, host, StringComparison.Ordinal) &&
                other.StartsWith(label, StringComparison.OrdinalIgnoreCase))
                ? host
                : label.TrimEnd('.');
        }

        private static double[] PerSecond(Dictionary<long, int> seconds, long first, long last, int maxPoints)
        {
            if (first == long.MaxValue || last < first || maxPoints < 1)
            {
                return Array.Empty<double>();
            }

            long span = last - first + 1;
            long group = (span + maxPoints - 1) / maxPoints;
            int points = (int)((span + group - 1) / group);
            var values = new double[points];
            foreach ((long second, int count) in seconds)
            {
                if (second >= first && second <= last)
                {
                    values[points - 1 - (int)((last - second) / group)] += count;
                }
            }

            // The groups end at the last second, so only the oldest one can cover fewer seconds.
            for (int index = 0; index < points; index++)
            {
                long covered = index == 0 ? span - (points - 1) * group : group;
                values[index] /= covered;
            }

            return values;
        }
    }
}
