// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XMAT.Operations;
using XMAT.Operations.Captures;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Cli
{
    internal static class CliCaptureCommand
    {
        private const int MaximumProfileBytes = 256 * 1024;

        private static readonly HashSet<string> Options = new(StringComparer.Ordinal)
        {
            "--port", "--duration-seconds", "--out", "--listen", "--root-pfx",
            "--shape", "--down-kbps", "--up-kbps", "--latency-ms", "--jitter-ms",
            "--body-timeout-seconds", "--shape-profile",
        };

        private static readonly string[] Presets = { "off", "min", "avg", "exc", "broken", "disconnect" };
        private static readonly string[] CustomShapingOptions = { "--down-kbps", "--up-kbps", "--latency-ms", "--jitter-ms" };

        // Each usage error names the option at fault but never repeats its value.
        internal static CliParseResult Parse(IReadOnlyList<string> args)
        {
            const string command = CliCommands.Capture;
            var arguments = new CliArguments();
            CliParseResult error = arguments.Read(args, command, Options);
            if (error != null)
            {
                return error;
            }
            if (arguments.HelpRequested)
            {
                return CliParseResult.Success(new CliRequest(CliCommands.Help, Topic: command));
            }
            if (arguments.Positionals.Count != 0)
            {
                return CliParseResult.UsageError(command, "cli.usage.unexpected-argument", "capture takes no INPUT; use its options.");
            }
            foreach ((string required, string message) in new[]
            {
                ("--port", "capture needs --port N (1-65535)."),
                ("--duration-seconds", "capture needs --duration-seconds N (1-86400)."),
                ("--out", "capture needs --out FILE.xmatcap."),
            })
            {
                if (!arguments.Options.ContainsKey(required))
                {
                    return CliParseResult.UsageError(command, "cli.usage.missing-option", message);
                }
            }
            string output = arguments.Options["--out"];
            if (!string.Equals(Path.GetExtension(output), ".xmatcap", StringComparison.OrdinalIgnoreCase))
            {
                return Invalid("--out must name a .xmatcap file.");
            }
            if (!TryNumber(arguments, "--port", 1, 65535, 0, out int port))
            {
                return Invalid("--port takes a number from 1 to 65535.");
            }
            if (!TryNumber(arguments, "--duration-seconds", 1, 86400, 0, out int duration))
            {
                return Invalid("--duration-seconds takes a number from 1 to 86400.");
            }
            IPAddress address = IPAddress.Loopback;
            if (arguments.Options.TryGetValue("--listen", out string listen) && !IPAddress.TryParse(listen, out address))
            {
                return Invalid("--listen takes an IP address, such as 127.0.0.1 or 0.0.0.0.");
            }
            arguments.Options.TryGetValue("--root-pfx", out string root);
            arguments.Options.TryGetValue("--shape-profile", out string file);
            if (file != null &&
                (arguments.Options.ContainsKey("--shape") || arguments.Options.ContainsKey("--body-timeout-seconds") ||
                 CustomShapingOptions.Any(arguments.Options.ContainsKey)))
            {
                return Invalid("--shape-profile cannot be combined with --shape, --down-kbps, --up-kbps, " +
                    "--latency-ms, --jitter-ms or --body-timeout-seconds.");
            }

            NetworkShapingPlan plan = null;
            if (file == null)
            {
                error = ReadShapingPlan(arguments, out plan);
                if (error != null)
                {
                    return error;
                }
            }
            return CliParseResult.Success(new CliRequest(
                command,
                Output: output,
                Capture: new(port, duration, output, plan, root, address),
                ShapingProfilePath: file));
        }

        private static CliParseResult ReadShapingPlan(CliArguments arguments, out NetworkShapingPlan plan)
        {
            plan = null;
            bool custom = CustomShapingOptions.Any(arguments.Options.ContainsKey);
            NetworkShapingProfile profile = new(custom ? NetworkShapingMode.Shape : NetworkShapingMode.Off);
            if (arguments.Options.TryGetValue("--shape", out string preset))
            {
                if (!Presets.Contains(preset, StringComparer.Ordinal))
                {
                    return Invalid("--shape takes off, min, avg, exc, broken or disconnect.");
                }
                profile = NetworkShapingProfile.Preset(preset);
            }
            if (custom && profile.Mode != NetworkShapingMode.Shape)
            {
                return Invalid("--down-kbps, --up-kbps, --latency-ms and --jitter-ms need --shape min, avg or exc, or no --shape.");
            }
            if (!TryNumber(arguments, "--down-kbps", 0, 1_000_000, profile.DownKbps, out int down) ||
                !TryNumber(arguments, "--up-kbps", 0, 1_000_000, profile.UpKbps, out int up))
            {
                return Invalid("--down-kbps and --up-kbps take a number from 0 (unlimited) to 1000000.");
            }
            if (!TryNumber(arguments, "--latency-ms", 0, 10_000, profile.LatencyMs, out int latency))
            {
                return Invalid("--latency-ms takes a number from 0 to 10000.");
            }
            if (!TryNumber(arguments, "--jitter-ms", 0, latency, profile.JitterMs, out int jitter))
            {
                return Invalid("--jitter-ms takes a number from 0 to the added round-trip latency.");
            }
            if (!TryNumber(arguments, "--body-timeout-seconds", 1, 1800, 120, out int bodyTimeout))
            {
                return Invalid("--body-timeout-seconds takes a number from 1 to 1800.");
            }
            plan = new NetworkShapingPlan(
                1, profile with { DownKbps = down, UpKbps = up, LatencyMs = latency, JitterMs = jitter },
                BodyTimeoutSeconds: bodyTimeout);
            plan.Validate();
            return null;
        }

        private static bool TryNumber(CliArguments arguments, string name, int minimum, int maximum, int fallback, out int value)
        {
            value = fallback;
            return !arguments.Options.TryGetValue(name, out string text) ||
                (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) &&
                 value >= minimum && value <= maximum);
        }

        private static CliParseResult Invalid(string message) =>
            CliParseResult.UsageError(CliCommands.Capture, "cli.usage.invalid-value", message);

        // Mirrors analyze's --out checks, so output mistakes fail before a listener or profile is opened.
        private static CliFailure CheckOutput(string output)
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(output);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or SecurityException)
            {
                return new CliFailure(CliExitCodes.Usage, "cli.usage.invalid-value", "--out is not a valid file path.");
            }
            if (Path.Exists(fullPath))
            {
                return new CliFailure(
                    CliExitCodes.Conflict, "cli.output.exists", "The --out file already exists; xmat never overwrites output.");
            }
            string parent = Path.GetDirectoryName(fullPath);
            return string.IsNullOrEmpty(parent) || !Directory.Exists(parent)
                ? new CliFailure(
                    CliExitCodes.Usage, "cli.output.parent-missing", "The folder that should contain the --out file does not exist.")
                : null;
        }

        private static async Task<(CliFailure Failure, NetworkShapingPlan Plan)> ReadProfileAsync(
            string path, CancellationToken cancellationToken)
        {
            byte[] bytes;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > MaximumProfileBytes)
                {
                    return (Profile("The --shape-profile file exceeds 256 KiB."), null);
                }
                bytes = new byte[(int)stream.Length];
                await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException)
            {
                return (new CliFailure(
                    CliExitCodes.InvalidInput, "capture.profile.unreadable", "The --shape-profile file could not be read."), null);
            }
            try
            {
                return (null, NetworkShapingPlan.Parse(Encoding.UTF8.GetString(bytes)));
            }
            catch (JsonException)
            {
                return (Profile(
                    "The --shape-profile file is not valid shaping profile JSON; check its syntax and property names."), null);
            }
            catch (ArgumentException)
            {
                return (Profile(
                    "The --shape-profile values are invalid; check the version, value ranges and schedule order."), null);
            }

            static CliFailure Profile(string message) =>
                new(CliExitCodes.InvalidInput, "capture.profile.invalid", message);
        }

        internal static async Task<CliReport> RunAsync(
            CliRequest request, string runId, CliConsole console, CancellationToken cancellationToken)
        {
            var report = new CliReport(CliCommands.Capture, runId);
            CaptureProxyRequest capture = request.Capture;
            CliFailure failure = CheckOutput(capture.OutputPath);
            if (failure == null && request.ShapingProfilePath != null)
            {
                NetworkShapingPlan plan;
                try
                {
                    (failure, plan) = await ReadProfileAsync(
                        request.ShapingProfilePath, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    report.Cancel();
                    return report;
                }
                capture = capture with { Shaping = plan };
            }
            if (failure == null && capture.Shaping.Schedule?.Any(change => change.AfterSeconds >= capture.DurationSeconds) == true)
            {
                failure = new CliFailure(
                    CliExitCodes.InvalidInput, "capture.profile.invalid",
                    "Scheduled shaping changes in --shape-profile must occur before --duration-seconds ends.");
            }
            if (failure != null)
            {
                report.Fail(failure.ExitCode, failure.Code, failure.Message);
                return report;
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
                        // A closed progress stream must not interrupt capture or its cleanup.
                        return;
                    }
                }
            }
        }
    }
}
