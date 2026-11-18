// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;

namespace XMAT.Telemetry
{
    internal interface ITelemetryReporter : IDisposable
    {
        void Send(TelemetryEvent item, Guid sessionId);

        // Stop without flushing; discard queued data and prevent replay in future sessions.
        void Revoke();
    }
}
