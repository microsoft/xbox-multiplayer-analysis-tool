// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;

namespace XMAT.WebServiceCapture.Models
{
    public enum WebProxyStopPhase
    {
        StoppingPublicListener,
        StoppingInterceptedListener,
        WaitingForCallbacks,
        DrainingUiUpdates,
        DisablingSystemProxy,
        Complete
    }

    public sealed class WebProxyStopProgress
    {
        public WebProxyStopPhase Phase { get; }
        public TimeSpan Elapsed { get; }
        public int ActiveCallbacks { get; }
        public int PendingUiUpdates { get; }

        public WebProxyStopProgress(
            WebProxyStopPhase phase,
            TimeSpan elapsed,
            int activeCallbacks = 0,
            int pendingUiUpdates = 0)
        {
            Phase = phase;
            Elapsed = elapsed;
            ActiveCallbacks = activeCallbacks;
            PendingUiUpdates = pendingUiUpdates;
        }
    }
}
