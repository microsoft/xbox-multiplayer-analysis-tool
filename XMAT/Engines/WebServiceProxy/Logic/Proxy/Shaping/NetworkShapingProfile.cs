// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;

namespace XMAT.WebServiceCapture.Proxy
{
    public enum NetworkShapingMode
    {
        Off,
        Shape,
        Broken,
        Disconnect,
    }

    public sealed record NetworkShapingProfile(
        NetworkShapingMode Mode = NetworkShapingMode.Off,
        int DownKbps = 0,
        int UpKbps = 0,
        int LatencyMs = 0,
        int JitterMs = 0)
    {
        public static NetworkShapingProfile Off { get; } = new();

        public static NetworkShapingProfile Preset(string name) => name switch
        {
            "off" => Off,
            "min" => new(NetworkShapingMode.Shape, 192, 192, 75),
            "avg" => new(NetworkShapingMode.Shape, 1000, 700, 50),
            "exc" => new(NetworkShapingMode.Shape, 35000, 10000, 30),
            "broken" => new(NetworkShapingMode.Broken),
            "disconnect" => new(NetworkShapingMode.Disconnect),
            _ => throw new ArgumentException("Unknown shaping preset.", nameof(name)),
        };

        public void Validate()
        {
            if (!Enum.IsDefined(Mode) ||
                DownKbps is < 0 or > 1_000_000 ||
                UpKbps is < 0 or > 1_000_000 ||
                LatencyMs is < 0 or > 10_000 ||
                JitterMs < 0 || JitterMs > LatencyMs)
            {
                throw new ArgumentException(
                    "Shaping rates must be 0 (unlimited) to 1000000 kbps, RTT 0 to 10000 ms, and RTT jitter 0 to the RTT.");
            }
        }
    }
}
