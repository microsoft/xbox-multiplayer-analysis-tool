// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using XMAT.SharedInterfaces;

namespace XMAT.AgentAccess
{
    internal sealed record AgentCaptureDataFilter
    {
        public string DeviceName { get; init; }
        public string Host { get; init; }
        public string Method { get; init; }
        public int? StatusCode { get; init; }
        public DateTimeOffset? AfterUtc { get; init; }
        public DateTimeOffset? BeforeUtc { get; init; }
        public double? MinimumDurationMilliseconds { get; init; }
        public int? Protocol { get; init; }
        public string SourceAddress { get; init; }
        public string DestinationAddress { get; init; }
        public Guid? CaptureId { get; init; }
        public long? CaptureEpoch { get; init; }
        public int? ConnectionId { get; init; }
        public bool? FromHost { get; init; }
        public string MessageType { get; init; }

        internal CaptureDataFilter Resolve(AgentDataPolicy policy) => new(
            policy.Resolve("device", DeviceName), policy.Resolve("address", Host), Method, StatusCode,
            AfterUtc, BeforeUtc, MinimumDurationMilliseconds, Protocol,
            policy.Resolve("address", SourceAddress), policy.Resolve("address", DestinationAddress),
            CaptureId, CaptureEpoch, ConnectionId, FromHost, MessageType);
    }
}
