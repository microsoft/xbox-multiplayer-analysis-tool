// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace XMAT.AgentBridge
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            if (args.Length != 6 ||
                args[0] != "--instance" || !Guid.TryParseExact(args[1], "D", out var instance) ||
                args[2] != "--pid" || !int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ||
                args[4] != "--started" || !long.TryParse(args[5], NumberStyles.None, CultureInfo.InvariantCulture, out var started) ||
                instance == Guid.Empty || pid <= 0 || started <= 0)
            {
                await Console.Error.WriteLineAsync("XMAT_AGENT_INVALID_ARGUMENTS");
                return 2;
            }

            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.StartTime.ToUniversalTime().Ticks != started)
                {
                    await Console.Error.WriteLineAsync("XMAT_AGENT_INSTANCE_MISMATCH");
                    return 3;
                }

                await using var pipe = new NamedPipeClientStream(
                    ".", $"XMAT.Agent.{instance:N}", PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await pipe.ConnectAsync(timeout.Token);
                if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid) ||
                    serverPid != (uint)pid || process.HasExited ||
                    process.StartTime.ToUniversalTime().Ticks != started)
                {
                    await Console.Error.WriteLineAsync("XMAT_AGENT_INSTANCE_MISMATCH");
                    return 3;
                }

                using var forwarding = new CancellationTokenSource();
                using var input = Console.OpenStandardInput();
                using var output = Console.OpenStandardOutput();
                var toApp = input.CopyToAsync(pipe, forwarding.Token);
                var toClient = pipe.CopyToAsync(output, forwarding.Token);
                try
                {
                    await await Task.WhenAny(toApp, toClient);
                    await output.FlushAsync();
                }
                finally
                {
                    await forwarding.CancelAsync();
                    pipe.Close();
                }
                return 0;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                Win32Exception or ArgumentException or InvalidOperationException or OperationCanceledException)
            {
                await Console.Error.WriteLineAsync("XMAT_AGENT_UNAVAILABLE_OR_WRONG_ELEVATION");
                return 4;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
    }
}
