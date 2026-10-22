// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

namespace XMAT.Telemetry
{
    internal sealed class TelemetryNavigationTracker(XmatTelemetryService telemetry)
    {
        private ulong? _navigationId;
        private TelemetryOperation _operation;

        internal void Start(ulong navigationId, bool visible)
        {
            if (_navigationId == navigationId)
            {
                return;
            }
            Cancel();
            _navigationId = navigationId;
            if (visible)
            {
                _operation = telemetry.Begin(TelemetryOperationKind.StatisticsRender, TelemetryFeature.StatisticsReport);
            }
        }

        internal void Complete(ulong navigationId, bool succeeded)
        {
            if (_navigationId != navigationId)
            {
                return;
            }
            _operation?.Complete(succeeded ? TelemetryOutcome.Succeeded : TelemetryOutcome.Failed,
                succeeded ? TelemetryError.None : TelemetryError.InvalidData);
            _operation = null;
            _navigationId = null;
        }

        internal void Cancel()
        {
            _operation?.Complete(TelemetryOutcome.Cancelled);
            _operation = null;
            _navigationId = null;
        }
    }
}
