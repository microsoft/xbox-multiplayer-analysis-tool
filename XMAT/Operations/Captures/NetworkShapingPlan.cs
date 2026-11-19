// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Operations.Captures
{
    internal sealed record ScheduledShapingChange(int AfterSeconds, NetworkShapingProfile Profile);

    internal sealed record NetworkShapingPlan(
        int Version,
        NetworkShapingProfile Initial,
        IReadOnlyList<ScheduledShapingChange> Schedule = null,
        int BodyTimeoutSeconds = 120)
    {
        internal static JsonSerializerOptions JsonOptions { get; } = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 16,
            Converters = { new JsonStringEnumConverter<NetworkShapingMode>(JsonNamingPolicy.CamelCase) },
        };

        internal void Validate()
        {
            if (Version != 1 || Initial == null || BodyTimeoutSeconds is < 1 or > 1800 ||
                Schedule?.Count > 1024)
            {
                throw new ArgumentException("Invalid shaping plan.");
            }
            Initial.Validate();
            int previous = 0;
            foreach (ScheduledShapingChange change in Schedule ?? Array.Empty<ScheduledShapingChange>())
            {
                if (change?.Profile == null || change.AfterSeconds <= previous || change.AfterSeconds > 86400)
                {
                    throw new ArgumentException("Scheduled changes must have strictly increasing positive times within 24 hours.");
                }
                change.Profile.Validate();
                previous = change.AfterSeconds;
            }
        }

        internal static NetworkShapingPlan Parse(string json)
        {
            if (json == null || json.Length > 256 * 1024)
            {
                throw new ArgumentException("The shaping profile exceeds its size limit.");
            }
            NetworkShapingPlan plan = JsonSerializer.Deserialize<NetworkShapingPlan>(json, JsonOptions);
            if (plan == null)
            {
                throw new ArgumentException("A shaping plan is required.");
            }
            plan.Validate();
            return plan;
        }
    }
}
