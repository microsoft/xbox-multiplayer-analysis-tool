// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.SharedInterfaces
{
    public sealed record CaptureDescriptor(
        Guid Id,
        string DeviceName,
        DeviceType DeviceType,
        CaptureType CaptureType,
        bool IsReadOnly,
        bool IsRunning,
        long Revision,
        long CaptureEpoch,
        int? ProxyPort,
        int ActiveScripts,
        bool NameIsTruncated = false,
        bool RecoveryRequired = false,
        CaptureTraceRecoveryPhase TraceRecoveryPhase = CaptureTraceRecoveryPhase.None,
        bool TraceMayBeRunning = false,
        long? PacketCount = null,
        string LocalTraceSession = null);

    public interface ICaptureCatalog
    {
        Task<IReadOnlyList<CaptureDescriptor>> GetCapturesAsync(CancellationToken cancellationToken = default);
    }
}
