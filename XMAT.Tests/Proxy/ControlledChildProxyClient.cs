// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;

namespace XMAT.Tests.Proxy
{
    internal sealed class ControlledChildProxyClient : IAsyncDisposable
    {
        private readonly Process _process;

        private ControlledChildProxyClient(Process process)
        {
            _process = process;
        }

        internal int ProcessId => _process.Id;

        internal static ControlledChildProxyClient Start(int proxyPort, string url)
        {
            var start =
                new ProcessStartInfo(
                    "curl.exe")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                };
            foreach (string argument in
                new[]
                {
                    "--silent",
                    "--show-error",
                    "--output",
                    "NUL",
                    "--max-time",
                    "20",
                    "--noproxy",
                    string.Empty,
                    "--proxy",
                    $"http://127.0.0.1:{proxyPort}",
                    url,
                })
            {
                start.ArgumentList.Add(argument);
            }
            return new ControlledChildProxyClient(
                Process.Start(start) ?? throw new InvalidOperationException("The child proxy client did not start."));
        }

        internal async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            await _process.WaitForExitAsync(cancellationToken);
            Assert.Equal(0, _process.ExitCode);
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            finally
            {
                _process.Dispose();
            }
            return ValueTask.CompletedTask;
        }
    }
}
