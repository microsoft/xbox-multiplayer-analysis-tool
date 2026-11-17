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
                NetworkShapingMetadata metadata =
                    JsonSerializer.Deserialize<NetworkShapingMetadata>(json, NetworkShapingPlan.JsonOptions);
                Validate(metadata);
                return metadata;
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                throw new InvalidDataException("Shaping metadata is invalid.", ex);
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
            if (metadata?.Version != 1 || metadata.Traces == null || metadata.Traces.Count > 64)
            {
                throw new ArgumentException("Unsupported shaping metadata.");
            }
            int total = 0;
            foreach (NetworkShapingTrace trace in metadata.Traces)
            {
                if (trace?.Changes == null || trace.BodyTimeoutSeconds is < 1 or > 1800 ||
                    trace.Changes.Count == 0 || (total += trace.Changes.Count) > 16384)
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
