// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Operations.Captures
{
    internal sealed record NetworkShapingTrace(
        int BodyTimeoutSeconds,
        IReadOnlyList<NetworkShapingChange> Changes);

    internal sealed record NetworkShapingMetadata(int Version, IReadOnlyList<NetworkShapingTrace> Traces)
    {
        internal const string DeviceField = "NetworkShaping";
        internal const string ArchiveEntry = "xmat/network-shaping.json";
        internal const int MaximumBytes = 4 * 1024 * 1024;
        internal const int MaximumTraces = 64;
        internal const int MaximumChanges = 16384;

        internal static NetworkShapingMetadata From(NetworkShapingController controller, int bodyTimeoutSeconds)
        {
            IReadOnlyList<NetworkShapingChange> history = controller.History;
            return TimeSpan.FromSeconds(bodyTimeoutSeconds) != ProxyRequestReadPolicy.Default.BodyTotalTimeout ||
                history.Any(change => change.Profile.Mode != NetworkShapingMode.Off)
                ? new(1, new[] { new NetworkShapingTrace(bodyTimeoutSeconds, history) })
                : null;
        }

        internal static NetworkShapingMetadata Combine(IEnumerable<NetworkShapingMetadata> items)
        {
            NetworkShapingTrace[] traces = items.Where(item => item != null).SelectMany(item => item.Traces).ToArray();
            return traces.Length == 0 ? null : new(1, traces);
        }

        // Each device's history is within the limits, but a combination of several can exceed one; Serialize refuses it.
        internal static bool FitsLimits(NetworkShapingMetadata metadata) =>
            metadata == null ||
            metadata.Traces.Count <= MaximumTraces &&
            metadata.Traces.Sum(trace => trace.Changes.Count) <= MaximumChanges &&
            Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(metadata, NetworkShapingPlan.JsonOptions)) <= MaximumBytes;

        internal static string Serialize(NetworkShapingMetadata metadata)
        {
            if (metadata == null)
            {
                return null;
            }
            Validate(metadata);
            string json = JsonSerializer.Serialize(metadata, NetworkShapingPlan.JsonOptions);
            if (Encoding.UTF8.GetByteCount(json) > MaximumBytes)
            {
                throw new InvalidDataException("Shaping metadata exceeds its size limit.");
            }
            return json;
        }

        internal static NetworkShapingMetadata Parse(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }
            if (Encoding.UTF8.GetByteCount(json) > MaximumBytes)
            {
                throw new InvalidDataException("Shaping metadata exceeds its size limit.");
            }
            try
            {
                // Replay reads metadata before configuring storage; construct only these known data types.
                using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
                JsonElement root = document.RootElement;
                CheckProperties(root, "version", "traces");
                JsonElement traceElements = root.GetProperty("traces");
                if (traceElements.GetArrayLength() > MaximumTraces)
                {
                    throw new JsonException();
                }
                var traces = new List<NetworkShapingTrace>();
                int totalChanges = 0;
                foreach (JsonElement trace in traceElements.EnumerateArray())
                {
                    CheckProperties(trace, "bodyTimeoutSeconds", "changes");
                    JsonElement changes = trace.GetProperty("changes");
                    if ((totalChanges += changes.GetArrayLength()) > MaximumChanges)
                    {
                        throw new JsonException();
                    }
                    traces.Add(new NetworkShapingTrace(
                        trace.GetProperty("bodyTimeoutSeconds").GetInt32(),
                        changes.EnumerateArray().Select(ReadChange).ToArray()));
                }
                var metadata = new NetworkShapingMetadata(root.GetProperty("version").GetInt32(), traces);
                Validate(metadata);
                return metadata;
            }
            catch (Exception ex) when (
                ex is JsonException or ArgumentException or InvalidOperationException or FormatException or KeyNotFoundException)
            {
                throw new InvalidDataException("Shaping metadata is invalid.", ex);
            }
        }

        private static NetworkShapingChange ReadChange(JsonElement change)
        {
            CheckProperties(change, "sequence", "timestamp", "profile");
            JsonElement profile = change.GetProperty("profile");
            CheckProperties(profile, "mode", "downKbps", "upKbps", "latencyMs", "jitterMs");
            NetworkShapingMode mode = NetworkShapingMode.Off;
            if (profile.TryGetProperty("mode", out JsonElement value))
            {
                if (value.ValueKind == JsonValueKind.Number)
                {
                    mode = (NetworkShapingMode)value.GetInt32();
                }
                else if (value.ValueKind != JsonValueKind.String ||
                    !Enum.TryParse(value.GetString(), ignoreCase: true, out mode))
                {
                    throw new JsonException();
                }
            }
            return new NetworkShapingChange(
                change.TryGetProperty("sequence", out JsonElement sequence) ? sequence.GetInt64() : 0,
                change.TryGetProperty("timestamp", out JsonElement timestamp) ? timestamp.GetDateTimeOffset() : default,
                new NetworkShapingProfile(
                    mode,
                    ReadNumber(profile, "downKbps"),
                    ReadNumber(profile, "upKbps"),
                    ReadNumber(profile, "latencyMs"),
                    ReadNumber(profile, "jitterMs")));
        }

        private static int ReadNumber(JsonElement parent, string name) =>
            parent.TryGetProperty(name, out JsonElement value) ? value.GetInt32() : 0;

        private static void CheckProperties(JsonElement element, params string[] names)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Contains(property.Name, StringComparer.Ordinal))
                {
                    throw new JsonException();
                }
            }
        }

        internal static NetworkShapingMetadata ReadArchive(ZipArchive archive)
        {
            ZipArchiveEntry[] entries = archive.Entries.Where(entry => entry.FullName == ArchiveEntry).ToArray();
            if (entries.Length == 0)
            {
                return null;
            }
            if (entries.Length != 1 || entries[0].Length > MaximumBytes)
            {
                throw new InvalidDataException("Shaping metadata is duplicated or exceeds its size limit.");
            }
            using Stream stream = entries[0].Open();
            using var memory = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = stream.Read(buffer)) != 0)
            {
                if (memory.Length + count > MaximumBytes)
                {
                    throw new InvalidDataException("Shaping metadata exceeds its size limit.");
                }
                memory.Write(buffer, 0, count);
            }
            return Parse(Encoding.UTF8.GetString(memory.GetBuffer(), 0, (int)memory.Length));
        }

        private static void Validate(NetworkShapingMetadata metadata)
        {
            if (metadata?.Version != 1 || metadata.Traces == null || metadata.Traces.Count > MaximumTraces)
            {
                throw new ArgumentException("Unsupported shaping metadata.");
            }
            int total = 0;
            foreach (NetworkShapingTrace trace in metadata.Traces)
            {
                if (trace?.Changes == null || trace.BodyTimeoutSeconds is < 1 or > 1800 ||
                    trace.Changes.Count == 0 || (total += trace.Changes.Count) > MaximumChanges)
                {
                    throw new ArgumentException("Invalid shaping history.");
                }
                long previous = -1;
                foreach (NetworkShapingChange change in trace.Changes)
                {
                    if (change?.Profile == null || change.Sequence <= previous)
                    {
                        throw new ArgumentException("Invalid shaping change sequence.");
                    }
                    change.Profile.Validate();
                    previous = change.Sequence;
                }
            }
        }
    }
}
