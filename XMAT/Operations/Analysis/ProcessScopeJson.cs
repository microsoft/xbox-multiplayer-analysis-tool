// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using XMAT.Operations.Captures;
using XMAT.Operations.Processes;

namespace XMAT.Operations.Analysis
{
    // The process scope of an analysis as xmat's JSON (processFilter) and a filtered GUI analysis (scope.json) write it:
    // the validated selectors, never a name or path observed in the capture, and the counts of the selection once it
    // completed. Devices use the analysis's ordinal ids, in device order.
    internal static class ProcessScopeJson
    {
        internal const string ScopeFileName = "scope.json";

        private static readonly JsonWriterOptions Options = new() { Indented = true };

        internal static void Write(Utf8JsonWriter writer, ProcessFilterSpec filter, ProcessFilterScope scope)
        {
            ArgumentNullException.ThrowIfNull(writer);
            filter ??= ProcessFilterSpec.Empty;
            bool applied = scope?.Counts != null;
            writer.WriteStartObject();
            writer.WriteBoolean("applied", applied);
            WriteSelectors(writer, "include", filter.Include);
            WriteSelectors(writer, "exclude", filter.Exclude);
            if (applied)
            {
                writer.WriteStartObject("counts");
                WriteCounts(writer, scope.Counts);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("counts");
            }

            writer.WriteStartArray("devices");
            if (applied && scope.Devices != null)
            {
                foreach (KeyValuePair<string, ProcessFilterCounts> device in scope.Devices
                    .OrderBy(device => DeviceOrdinal(device.Key))
                    .ThenBy(device => device.Key, StringComparer.Ordinal))
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", device.Key);
                    WriteCounts(writer, device.Value);
                    writer.WriteEndObject();
                }
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        internal static byte[] Serialize(ProcessFilterSpec filter, ProcessFilterScope scope)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, Options))
            {
                Write(writer, filter, scope);
            }

            return stream.ToArray();
        }

        private static void WriteSelectors(Utf8JsonWriter writer, string name, ImmutableArray<string> selectors)
        {
            writer.WriteStartArray(name);
            if (!selectors.IsDefault)
            {
                foreach (string selector in selectors)
                {
                    writer.WriteStringValue(selector);
                }
            }

            writer.WriteEndArray();
        }

        private static void WriteCounts(Utf8JsonWriter writer, ProcessFilterCounts counts)
        {
            writer.WriteNumber("total", counts.Total);
            writer.WriteNumber("included", counts.Included);
            writer.WriteNumber("hidden", counts.Hidden);
            writer.WriteNumber("unknown", counts.Unknown);
        }

        // d1, d2, ..., d10 in numeric order.
        private static long DeviceOrdinal(string id) =>
            id != null && id.Length > 1 && id[0] == 'd' &&
            long.TryParse(id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out long ordinal)
                ? ordinal
                : long.MaxValue;
    }
}
