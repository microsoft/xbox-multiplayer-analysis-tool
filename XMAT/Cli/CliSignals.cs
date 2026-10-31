// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace XMAT.Cli
{
    // Ctrl+C or Ctrl+Break requests cancellation; CliStagedRun decides what it means for the run. A second Ctrl+C
    // ends the process at once. Closing the console window, logging off or shutting down cancels too and waits briefly
    // for the cleanup, because Windows ends the process when the handler returns. This class only cancels; it never
    // sets exit codes.
    internal sealed class CliSignals : IDisposable
    {
        private static readonly TimeSpan CloseGracePeriod = TimeSpan.FromSeconds(4);
        private static readonly PosixSignal[] Signals = { PosixSignal.SIGINT, PosixSignal.SIGQUIT, PosixSignal.SIGHUP, PosixSignal.SIGTERM };

        private readonly CancellationTokenSource _cancellation;
        private readonly List<PosixSignalRegistration> _registrations = new();

        // Not disposed: a handler that is still running may wait on it.
        private readonly ManualResetEventSlim _finished = new(false);
        private int _signalled;

        private CliSignals(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        internal static CliSignals Register(CancellationTokenSource cancellation)
        {
            var signals = new CliSignals(cancellation);
            foreach (PosixSignal signal in Signals)
            {
                try
                {
                    signals._registrations.Add(PosixSignalRegistration.Create(signal, signals.OnSignal));
                }
                catch (PlatformNotSupportedException)
                {
                    // Not every signal maps to a console event; the others still work.
                }
            }

            return signals;
        }

        public void Dispose()
        {
            _finished.Set();
            foreach (PosixSignalRegistration registration in _registrations)
            {
                registration.Dispose();
            }
        }

        private void OnSignal(PosixSignalContext context)
        {
            if (Interlocked.Exchange(ref _signalled, 1) != 0)
            {
                return;
            }

            context.Cancel = true;
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The run has already finished.
            }

            if (context.Signal == PosixSignal.SIGHUP || context.Signal == PosixSignal.SIGTERM)
            {
                _finished.Wait(CloseGracePeriod);
            }
        }
    }
}
