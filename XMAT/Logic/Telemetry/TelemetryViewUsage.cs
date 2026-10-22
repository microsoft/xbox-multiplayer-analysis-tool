// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;

namespace XMAT.Telemetry
{
    internal sealed class TelemetryViewUsage(Action<TelemetryFeature> use)
    {
        private TelemetryFeature _visibleFeature;

        internal void Update(bool visible, bool hasData, TelemetryFeature feature)
        {
            var current = visible && hasData ? feature : TelemetryFeature.None;
            if (current != TelemetryFeature.None && current != _visibleFeature)
            {
                use(current);
            }
            _visibleFeature = current;
        }
    }
}
