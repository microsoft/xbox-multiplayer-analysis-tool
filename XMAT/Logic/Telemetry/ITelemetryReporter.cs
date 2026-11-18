// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;

namespace XMAT.Telemetry
{
    internal interface ITelemetryReporter : IDisposable
    {
        void Send(TelemetryEvent item, Guid sessionId);

        // Stop submitting new XMAT events. Already queued SDK events may still be uploaded.
        void Revoke();
    }
}
