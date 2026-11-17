// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XMAT.Operations;
using XMAT.Operations.Captures;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Cli
{
    internal static class CliCaptureCommand
    {
        private static readonly HashSet<string> Options = new(StringComparer.Ordinal)
        {
            "--port", "--duration-seconds", "--out", "--listen", "--root-pfx",
            "--shape", "--down-kbps", "--up-kbps", "--latency-ms", "--jitter-ms",
            "--body-timeout-seconds", "--shape-profile",
        };

        internal static CliParseResult Parse(IReadOnlyList<string> args)
        {
            var arguments = new CliArguments();
            CliParseResult error = arguments.Read(args, CliCommands.Capture, Options);
            if (error != null)
            {
                return error;
            }
            if (arguments.HelpRequested)
            {
                return CliParseResult.Success(new CliRequest(CliCommands.Help, Topic: CliCommands.Capture));
            }
            try
            {
                if (arguments.Positionals.Count != 0 ||
                    !arguments.Options.TryGetValue("--out", out string output) ||
                    !arguments.Options.ContainsKey("--port") ||
                    !arguments.Options.ContainsKey("--duration-seconds") ||
                    !string.Equals(Path.GetExtension(output), ".xmatcap", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("Capture requires a port, duration and native output path.");
                }
                int Number(string name, int fallback) => arguments.Options.TryGetValue(name, out string text)
                    ? int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture) : fallback;
                int port = Number("--port", 0);
                int duration = Number("--duration-seconds", 0);
                if (port is < 1 or > 65535 || duration is < 1 or > 86400)
                {
                    throw new ArgumentException("Capture port or duration is out of range.");
                }
                arguments.Options.TryGetValue("--shape-profile", out string file);
                string[] shapingOptions =
                    { "--shape", "--down-kbps", "--up-kbps", "--latency-ms", "--jitter-ms", "--body-timeout-seconds" };
                if (file != null && shapingOptions.Any(arguments.Options.ContainsKey))
                {
                    throw new ArgumentException("Profile files and shaping flags cannot be combined.");
                }
                arguments.Options.TryGetValue("--shape", out string preset);
                bool custom = shapingOptions.Skip(1).Take(4).Any(arguments.Options.ContainsKey);
                NetworkShapingProfile profile = preset == null
                    ? new(custom ? NetworkShapingMode.Shape : NetworkShapingMode.Off)
                    : NetworkShapingProfile.Preset(preset);
                if (custom && profile.Mode != NetworkShapingMode.Shape)
                {
                    throw new ArgumentException("Bandwidth and latency options require shaping mode.");
                }
                profile = profile with
                {
                    DownKbps = Number("--down-kbps", profile.DownKbps),
                    UpKbps = Number("--up-kbps", profile.UpKbps),
                    LatencyMs = Number("--latency-ms", profile.LatencyMs),
                    JitterMs = Number("--jitter-ms", profile.JitterMs),
                };
                var plan = new NetworkShapingPlan(1, profile, BodyTimeoutSeconds: Number("--body-timeout-seconds", 120));
                plan.Validate();
                IPAddress address = IPAddress.Loopback;
                if (arguments.Options.TryGetValue("--listen", out string listen) &&
                    !IPAddress.TryParse(listen, out address))
                {
                    throw new ArgumentException("An IP listening address is required.");
                }
                arguments.Options.TryGetValue("--root-pfx", out string root);
                return CliParseResult.Success(new CliRequest(
                    CliCommands.Capture,
                    Output: output,
                    Capture: new(port, duration, output, plan, root, address),
                    ShapingProfilePath: file));
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
            {
                return CliParseResult.UsageError(
                    CliCommands.Capture, "cli.capture.options",
                    "capture needs --port (1-65535), --duration-seconds (1-86400), and --out FILE.xmatcap. " +
                    "Shaping values must be valid; --shape-profile cannot be combined with shaping flags.");
            }
        }

        internal static async Task<CliReport> RunAsync(
            CliRequest request, string runId, CliConsole console, CancellationToken cancellationToken)
        {
            var report = new CliReport(CliCommands.Capture, runId);
            CaptureProxyRequest capture = request.Capture;
            if (request.ShapingProfilePath != null)
            {
                try
                {
                    using var stream = new FileStream(request.ShapingProfilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (stream.Length > 256 * 1024)
                    {
                        throw new InvalidDataException();
                    }
                    var bytes = new byte[(int)stream.Length];
                    await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                    capture = capture with { Shaping = NetworkShapingPlan.Parse(Encoding.UTF8.GetString(bytes)) };
                }
                catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex) && ex is not OperationCanceledException)
                {
                    report.Fail(CliExitCodes.InvalidInput, "capture.profile.invalid", "The shaping profile could not be read or is invalid.");
                    return report;
                }
            }
            OperationResult<CaptureProxyResult> result = await new CaptureProxyOperation().ExecuteAsync(
                capture,
                new OperationContext(progress: new ReadyProgress(console.Error)),
                cancellationToken).ConfigureAwait(false);
            report.Capture = result.Value;
            report.ApplyOutcome(result, "capture.failed", "Capture did not complete.");
            report.CleanupState = result.Outcome == OperationOutcome.CleanupFailed
                ? CliCleanupStates.Failed : CliCleanupStates.Completed;
            if (result.Succeeded)
            {
                report.HumanLines.Add($"xmat: captured {result.Value.Records} session(s) to the requested file.");
            }
            return report;
        }

        private sealed class ReadyProgress(TextWriter error) : IProgress<OperationProgress>
        {
            public void Report(OperationProgress progress)
            {
                if (progress.Phase == "capture.listening")
                {
                    try
                    {
                        error.WriteLine("xmat: explicit proxy listening; capture ends at the requested duration.");
                        error.Flush();
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                    {
                    }
                }
            }
        }
    }
}
