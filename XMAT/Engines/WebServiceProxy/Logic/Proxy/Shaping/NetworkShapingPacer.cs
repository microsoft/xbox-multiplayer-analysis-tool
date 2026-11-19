// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.WebServiceCapture.Proxy
{
    internal sealed class NetworkShapingPacer(NetworkShapingController controller, bool upload)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private long _version = -1;
        private double _next;

        internal async ValueTask<int> PaceAsync(int maximumBytes, CancellationToken cancellationToken)
        {
            if (Rate(controller.Current) == 0)
            {
                return maximumBytes;
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                while (true)
                {
                    NetworkShapingController.Snapshot snapshot = controller.ReadSnapshot();
                    int rate = Rate(snapshot.Profile);
                    if (rate == 0)
                    {
                        return maximumBytes;
                    }

                    double now = Timestamp();
                    if (_version != snapshot.Version)
                    {
                        _version = snapshot.Version;
                        _next = now;
                    }

                    int count = Math.Min(maximumBytes, Math.Max(1, rate * 1000 / 8 / 50));
                    // Retain at most 50 ms of scheduling credit, including Windows timer overshoot.
                    double due = Math.Max(now - 0.05, _next) + count * 8.0 / (rate * 1000.0);
                    await WaitAsync(due, snapshot, cancellationToken).ConfigureAwait(false);
                    if (controller.ReadSnapshot().Version != snapshot.Version)
                    {
                        continue;
                    }

                    _next = due;
                    return count;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        private int Rate(NetworkShapingProfile profile) =>
            profile.Mode != NetworkShapingMode.Shape ? 0 : upload ? profile.UpKbps : profile.DownKbps;

        internal static double Timestamp() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        internal static async Task WaitAsync(
            double due,
            NetworkShapingController.Snapshot snapshot,
            CancellationToken cancellationToken)
        {
            double seconds = due - Timestamp();
            if (seconds <= 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task delay = Task.Delay(TimeSpan.FromSeconds(seconds), delayCancellation.Token);
            await Task.WhenAny(delay, snapshot.Changed.Task).ConfigureAwait(false);
            await delayCancellation.CancelAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
