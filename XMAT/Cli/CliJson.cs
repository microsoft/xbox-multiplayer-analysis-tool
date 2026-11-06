// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using XMAT.Operations;
using XMAT.Operations.Analysis;
using XMAT.Operations.Capabilities;
using XMAT.Operations.Replay;

namespace XMAT.Cli
{
    // Writes xmat's JSON field by field, so only allow-listed values reach the output: rule names, levels, counts,
    // service labels, ordinal ids, request numbers, outcomes and status codes, the input kind and SHA-256, the effective
    // limits and options, codes and fixed messages. Never AnalysisResult.Legacy, captured content or paths other than
    // artifact paths.
    internal static class CliJson
    {
        // summary.json in a published --out folder.
        internal const string SummarySchema = "xmat-analysis/1";

        // summary.json of a published replay or compose run.
        internal const string ReplaySummarySchema = "xmat-replay/1";

        // summary.json of a published save-request run.
        internal const string SaveRequestSummarySchema = "xmat-save-request/1";

        // summary.json of a published curl run.
        internal const string CurlSummarySchema = "xmat-curl/1";

        // The only error of the fallback document (SerializeFallback).
        internal static readonly OperationDiagnostic OutputFailure = new(
            "cli.internal.output",
            OperationDiagnosticSeverity.Error,
            "xmat could not produce its full report; this report keeps only the exit code, the published artifacts and the " +
                "cleanup state.");

        private static readonly JsonWriterOptions Options = new() { Indented = true };

        // The one JSON document that xmat writes to standard output.
        internal static byte[] Serialize(CliReport report)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, Options))
            {
                writer.WriteStartObject();
                writer.WriteString("schema", CliReport.Schema);
                writer.WriteString("command", report.Command);
                writer.WriteString("runId", report.RunId);
                writer.WriteString("status", report.Status);
                writer.WriteNumber("exitCode", report.ExitCode);
                if (report.Version != null)
                {
                    writer.WriteString("version", report.Version);
                }

                if (report.Analysis != null)
                {
                    WriteAnalysis(writer, report.Analysis);
                }

                if (report.Replay != null)
                {
                    WriteReplay(writer, report.Replay, includeFiles: report.Artifacts.Count != 0);
                }

                if (report.SaveRequest != null)
                {
                    WriteSaveRequest(writer, report.SaveRequest, includeFiles: report.Artifacts.Count != 0);
                }

                if (report.Curl != null)
                {
                    WriteCurl(writer, report.Curl, includeFiles: report.Artifacts.Count != 0);
                }

                if (report.Doctor != null)
                {
                    WriteDoctor(writer, report.Doctor);
                }

                if (report.Commands != null)
                {
                    WriteCommands(writer, report.Commands);
                }

                writer.WriteStartArray("artifacts");
                foreach (CliArtifact artifact in report.Artifacts)
                {
                    writer.WriteStartObject();
                    writer.WriteString("kind", artifact.Kind);
                    writer.WriteString("path", artifact.Path);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteStartObject("cleanup");
                writer.WriteString("state", report.CleanupState);
                writer.WriteEndObject();
                WriteDiagnostics(writer, "warnings", report.Warnings);
                WriteDiagnostics(writer, "errors", report.Errors);
                writer.WriteEndObject();
            }

            return stream.ToArray();
        }

        // Replaces the document when Serialize fails: only values that xmat fixed itself (the schema, the command name, the
        // run id, the status and exit code, the published artifacts and the cleanup state) and the error OutputFailure. A
        // value that is not printable ASCII is written as null, so this document cannot fail the same way.
        internal static byte[] SerializeFallback(CliReport report)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, Options))
            {
                writer.WriteStartObject();
                writer.WriteString("schema", CliReport.Schema);
                writer.WriteString("command", FixedOrNull(report.Command));
                writer.WriteString("runId", FixedOrNull(report.RunId));
                writer.WriteString("status", report.Status);
                writer.WriteNumber("exitCode", report.ExitCode);
                writer.WriteStartArray("artifacts");
                foreach (CliArtifact artifact in
                    report.Artifacts.Where(
                        static artifact =>
                            artifact != null))
                {
                    writer.WriteStartObject();
                    writer.WriteString("kind", FixedOrNull(artifact.Kind));
                    writer.WriteString("path", FixedOrNull(artifact.Path));
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteStartObject("cleanup");
                writer.WriteString("state", FixedOrNull(report.CleanupState));
                writer.WriteEndObject();
                writer.WriteStartArray("warnings");
                writer.WriteEndArray();
                WriteDiagnostics(writer, "errors", new[] { OutputFailure });
                writer.WriteEndObject();
            }

            return stream.ToArray();
        }

        // summary.json: the analysis results and warnings of one run, without its status (a published folder always
        // succeeded).
        internal static byte[] SerializeSummary(CliReport report)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, Options))
            {
                writer.WriteStartObject();
                writer.WriteString("schema", SummarySchema);
                writer.WriteString("command", report.Command);
                writer.WriteString("runId", report.RunId);
                writer.WriteString("version", CliReport.ToolVersion);
                WriteAnalysis(writer, report.Analysis ?? new CliAnalysisReport());
                WriteDiagnostics(writer, "warnings", report.Warnings);
                writer.WriteEndObject();
            }

            return stream.ToArray();
        }

        // summary.json of replay and compose: the results of every send and the run's warnings, without its status (the
        // results are published whatever the responses were).
        internal static byte[] SerializeReplaySummary(CliReport report)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, Options))
            {
                writer.WriteStartObject();
                writer.WriteString("schema", ReplaySummarySchema);
                writer.WriteString("command", report.Command);
                writer.WriteString("runId", report.RunId);
                writer.WriteString("version", CliReport.ToolVersion);
                WriteReplay(writer, report.Replay ?? new CliReplayReport(null, null), includeFiles: true);
                WriteDiagnostics(writer, "warnings", report.Warnings);
                writer.WriteEndObject();
            }

            return stream.ToArray();
        }

        private static void WriteLimits(Utf8JsonWriter writer, CliLimits limits)
        {
            writer.WriteStartObject("limits");
            writer.WriteNumber("maxInputMib", limits.MaxInputMib);
            writer.WriteNumber("maxDataMib", limits.MaxDataMib);
            writer.WriteNumber("maxRecordMib", limits.MaxRecordMib);
            writer.WriteNumber("maxRecords", limits.MaxRecords);
            writer.WriteEndObject();
        }

        // summary.json of save-request: what each saved request file holds, without its status (a published folder always
        // succeeded).
        internal static byte[] SerializeSaveRequestSummary(CliReport report)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, Options))
            {
                writer.WriteStartObject();
                writer.WriteString("schema", SaveRequestSummarySchema);
                writer.WriteString("command", report.Command);
                writer.WriteString("runId", report.RunId);
                writer.WriteString("version", CliReport.ToolVersion);
                WriteSaveRequest(writer, report.SaveRequest ?? new CliSaveRequestReport(null, null), includeFiles: true);
                WriteDiagnostics(writer, "warnings", report.Warnings);
                writer.WriteEndObject();
            }

            return stream.ToArray();
        }

        // Only kinds, hashes, numbers, ordinal ids, profiles, omitted kinds and counts: never a URL, method, header name or
        // value, or body, which are in the request files.
        private static void WriteSaveRequest(Utf8JsonWriter writer, CliSaveRequestReport save, bool includeFiles)
        {
            if (save.InputKind != null)
            {
                writer.WriteStartObject("input");
                writer.WriteString("kind", save.InputKind);
                writer.WriteString("sha256", save.InputSha256);
                writer.WriteEndObject();
            }

            if (save.Limits != null)
            {
                WriteLimits(writer, save.Limits);
            }

            CliReplay options = save.Options;
            writer.WriteStartObject("options");
            writer.WriteBoolean("includeSensitive", options.IncludeSensitive);
            writer.WriteBoolean("allowOriginChange", options.AllowOriginChange);
            writer.WriteBoolean("unconditional", options.Unconditional);
            writer.WriteString(
                "httpVersion", options.HttpVersionSet ? ReplayTransportOptions.VersionName(options.HttpVersion) : null);
            writer.WriteEndObject();

            writer.WriteStartArray("requests");
            foreach (CliSavedRequest saved in save.Requests)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", saved.Id);
                writer.WriteString("device", saved.DeviceId);
                if (saved.RequestNumber is long request)
                {
                    writer.WriteNumber("request", request);
                }
                else
                {
                    writer.WriteNull("request");
                }

                writer.WriteString("profile", saved.Profile);
                WriteStrings(writer, "omitted", saved.Omitted);
                writer.WriteStartObject("recognized");
                writer.WriteNumber("headers", saved.Recognized.HeaderNames.Count);
                writer.WriteNumber("queryParameters", saved.Recognized.QueryParameterNames.Count);
                writer.WriteNumber("bodyBytesNotInspected", saved.Recognized.BodyBytesNotInspected);
                writer.WriteEndObject();
                writer.WriteString("file", includeFiles ? CliSaveRequestCommand.FileOf(saved.Id) : null);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        // summary.json of curl: what each script asks for, without its status (a published folder always succeeded).
        internal static byte[] SerializeCurlSummary(CliReport report)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, Options))
            {
                writer.WriteStartObject();
                writer.WriteString("schema", CurlSummarySchema);
                writer.WriteString("command", report.Command);
                writer.WriteString("runId", report.RunId);
                writer.WriteString("version", CliReport.ToolVersion);
                WriteCurl(writer, report.Curl ?? new CliCurlReport(null, null), includeFiles: true);
                WriteDiagnostics(writer, "warnings", report.Warnings);
                writer.WriteEndObject();
            }

            return stream.ToArray();
        }

        // Only kinds, hashes, numbers, ordinal ids, versions and counts: never a URL, method, header name or value, or body,
        // which are in the scripts and body files.
        private static void WriteCurl(Utf8JsonWriter writer, CliCurlReport curl, bool includeFiles)
        {
            if (curl.InputKind != null)
            {
                writer.WriteStartObject("input");
                writer.WriteString("kind", curl.InputKind);
                writer.WriteString("sha256", curl.InputSha256);
                writer.WriteEndObject();
            }

            if (curl.Limits != null)
            {
                WriteLimits(writer, curl.Limits);
            }

            CliReplay options = curl.Options;
            writer.WriteStartObject("options");
            writer.WriteBoolean("includeSensitive", options.IncludeSensitive);
            writer.WriteBoolean("allowOriginChange", options.AllowOriginChange);
            writer.WriteBoolean("unconditional", options.Unconditional);
            writer.WriteString(
                "httpVersion", options.HttpVersionSet ? ReplayTransportOptions.VersionName(options.HttpVersion) : null);
            writer.WriteEndObject();

            writer.WriteStartArray("scripts");
            foreach (CliCurlScript script in curl.Scripts)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", script.Id);
                writer.WriteString("device", script.DeviceId);
                if (script.RequestNumber is long request)
                {
                    writer.WriteNumber("request", request);
                }
                else
                {
                    writer.WriteNull("request");
                }

                writer.WriteNumber("bodyBytes", script.BodyBytes);
                writer.WriteString("httpVersion", ReplayTransportOptions.VersionName(script.HttpVersion));
                writer.WriteBoolean("unicodeCurl", script.UnicodeCurl);
                writer.WriteStartObject("recognized");
                writer.WriteNumber("headers", script.Recognized.HeaderNames.Count);
                writer.WriteNumber("queryParameters", script.Recognized.QueryParameterNames.Count);
                writer.WriteEndObject();
                writer.WriteString("script", includeFiles ? CliCurlCommand.ScriptOf(script.Id) : null);
                writer.WriteString("body", includeFiles && script.BodyBytes > 0 ? CliCurlCommand.BodyOf(script.Id) : null);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        // Only numbers, ordinal ids, outcome names, codes and the artifact paths of each send: never the URL, method,
        // headers or bodies, which are in the request and response files.
        private static void WriteReplay(Utf8JsonWriter writer, CliReplayReport replay, bool includeFiles)
        {
            // A capture file's kind and hash with its limits, or a request file's kind and hash (compose --request-file).
            if (replay.Limits != null || replay.InputKind == CliReplayCommand.RequestFileInputKind)
            {
                writer.WriteStartObject("input");
                writer.WriteString("kind", replay.InputKind);
                writer.WriteString("sha256", replay.InputSha256);
                writer.WriteEndObject();
            }

            if (replay.Limits != null)
            {
                WriteLimits(writer, replay.Limits);
            }

            CliReplay options = replay.Options;
            writer.WriteStartObject("options");
            writer.WriteNumber("repeat", options.Repeat);
            writer.WriteNumber("parallel", options.Parallel);
            writer.WriteNumber("timeoutSeconds", options.TimeoutSeconds);
            writer.WriteNumber("maxResponseMib", options.MaxResponseMib);
            writer.WriteNumber("followRedirects", options.FollowRedirects);
            writer.WriteBoolean("unconditional", options.Unconditional);
            writer.WriteBoolean("verify", options.Verify);
            writer.WriteBoolean("verifyDiff", options.VerifyDiff);
            writer.WriteBoolean("allowUnsafeMethods", options.AllowUnsafeMethods);
            writer.WriteBoolean("allowOriginChange", options.AllowOriginChange);
            writer.WriteString("httpVersion", ReplayTransportOptions.VersionName(options.HttpVersion));

            // Whether --connect-to was given, never its address: the output names no endpoint.
            writer.WriteBoolean("connectTo", options.ConnectTo != null);
            writer.WriteBoolean("allowConnectTo", options.AllowConnectTo);
            writer.WriteBoolean("saveSaz", options.SaveSaz);
            writer.WriteEndObject();

            writer.WriteStartArray("replays");
            foreach (ReplayItemResult item in replay.Items)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", item.Id);
                writer.WriteString("device", item.DeviceId);
                if (item.RequestNumber is long request)
                {
                    writer.WriteNumber("request", request);
                }
                else
                {
                    writer.WriteNull("request");
                }

                if (item.Sequence is long sequence)
                {
                    writer.WriteNumber("sequence", sequence);
                }
                else
                {
                    writer.WriteNull("sequence");
                }

                writer.WriteNumber("iteration", item.Iteration);
                writer.WriteString("outcome", OutcomeName(item.Outcome));
                if (item.StatusCode != 0)
                {
                    writer.WriteNumber("status", item.StatusCode);
                }
                else
                {
                    writer.WriteNull("status");
                }

                writer.WriteNumber("durationMs", (long)item.Duration.TotalMilliseconds);
                writer.WriteNumber("responseBytes", item.ResponseBytes);
                writer.WriteNumber("redirects", item.Redirects);
                writer.WriteString("transport", item.Transport);
                writer.WriteString("httpVersion", item.HttpVersion);
                writer.WriteString("responseVersion", item.ResponseVersion);
                writer.WriteString("code", item.Code);
                if (item.Verification == null)
                {
                    writer.WriteNull("verify");
                }
                else
                {
                    writer.WriteStartObject("verify");
                    writer.WriteString("result", item.Verification.Matches ? "match" : "mismatch");
                    WriteStrings(writer, "differences", item.Verification.Differences);

                    // The state of the send's diff.json, never its content, which can hold captured data; null without
                    // --verify-diff.
                    if (options.VerifyDiff && replay.Details.TryGetValue(item.Id, out VerifyDiffState state))
                    {
                        writer.WriteStartObject("detail");
                        writer.WriteString("state", JsonNamingPolicy.CamelCase.ConvertName(state.ToString()));
                        writer.WriteString(
                            "file",
                            includeFiles ? CliReplayCommand.FolderOf(item.Id) + "/" + CliReplayCommand.DiffFile : null);
                        writer.WriteEndObject();
                    }
                    else
                    {
                        writer.WriteNull("detail");
                    }

                    writer.WriteEndObject();
                }

                if (includeFiles)
                {
                    string folder = CliReplayCommand.FolderOf(item.Id);
                    writer.WriteStartObject("files");
                    writer.WriteString("request", folder + "/" + CliReplayCommand.RequestFile);
                    writer.WriteString(
                        "response",
                        item.Outcome is ReplayOutcome.Completed or ReplayOutcome.TooLarge
                            ? folder + "/" + CliReplayCommand.ResponseFile
                            : null);
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            int completed = replay.Items.Count(item => item.Outcome == ReplayOutcome.Completed);
            writer.WriteStartObject("totals");
            writer.WriteNumber("replays", replay.Items.Count);
            writer.WriteNumber("completed", completed);
            writer.WriteNumber("failed", replay.Items.Count - completed);
            writer.WriteNumber("matched", replay.Items.Count(item => item.Verification?.Matches == true));
            writer.WriteNumber("mismatched", replay.Items.Count(item => item.Verification is { Matches: false }));
            writer.WriteEndObject();
        }

        private static string OutcomeName(ReplayOutcome outcome) => outcome switch
        {
            ReplayOutcome.Completed => "completed",
            ReplayOutcome.TimedOut => "timedOut",
            ReplayOutcome.TooLarge => "tooLarge",
            _ => "failed",
        };

        private static void WriteAnalysis(Utf8JsonWriter writer, CliAnalysisReport analysis)
        {
            writer.WriteStartObject("input");
            writer.WriteString("kind", analysis.InputKind);
            writer.WriteString("sha256", analysis.InputSha256);
            writer.WriteEndObject();
            if (analysis.Limits != null)
            {
                WriteLimits(writer, analysis.Limits);
            }

            writer.WriteStartArray("devices");
            foreach (DeviceSummary device in analysis.Devices)
            {
                writer.WriteStartObject();
                writer.WriteString("id", device.Id);
                writer.WriteStartArray("consoles");
                foreach (ConsoleSummary console in device.Consoles)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", console.Id);
                    writer.WriteNumber("calls", console.Calls);
                    writer.WriteStartArray("findings");
                    foreach (FindingSummary finding in console.Findings)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("rule", finding.Rule);
                        writer.WriteString("service", finding.Service);
                        writer.WriteString("level", finding.Level);
                        writer.WriteNumber("count", finding.Count);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartObject("totals");
            writer.WriteNumber("calls", analysis.TotalCalls);
            writer.WriteNumber("findings", analysis.TotalFindings);
            writer.WriteEndObject();
        }

        private static void WriteDoctor(Utf8JsonWriter writer, CliDoctorReport doctor)
        {
            writer.WriteString("target", doctor.Target);
            writer.WriteStartArray("checks");
            foreach (CliCheck check in doctor.Checks)
            {
                writer.WriteStartObject();
                writer.WriteString("id", check.Id);
                writer.WriteString("status", check.Passed ? "pass" : "fail");
                if (!check.Passed && check.Code != null)
                {
                    writer.WriteString("code", check.Code);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartObject("environment");
            writer.WriteString("framework", doctor.Framework);
            writer.WriteString("architecture", doctor.Architecture);
            writer.WriteEndObject();
            writer.WriteStartArray("capabilities");
            foreach (Capability capability in doctor.Capabilities)
            {
                writer.WriteStartObject();
                writer.WriteString("id", capability.Id);
                writer.WriteString("status", StatusName(capability.Status));
                writer.WriteString("slice", capability.Slice);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        private static void WriteCommands(Utf8JsonWriter writer, IReadOnlyList<CliCommandHelp> commands)
        {
            writer.WriteStartArray("commands");
            foreach (CliCommandHelp command in commands)
            {
                writer.WriteStartObject();
                writer.WriteString("name", command.Name);
                writer.WriteString("status", command.Status);
                WriteStrings(writer, "usage", command.Usage);
                WriteStrings(writer, "capabilities", command.Capabilities);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        private static void WriteStrings(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
        {
            writer.WriteStartArray(name);
            foreach (string value in values)
            {
                writer.WriteStringValue(value);
            }

            writer.WriteEndArray();
        }

        private static void WriteDiagnostics(Utf8JsonWriter writer, string name, IReadOnlyList<OperationDiagnostic> diagnostics)
        {
            writer.WriteStartArray(name);
            foreach (OperationDiagnostic diagnostic in diagnostics)
            {
                writer.WriteStartObject();
                writer.WriteString("code", diagnostic.Code);
                writer.WriteString("message", diagnostic.Message);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        private static string FixedOrNull(string value)
        {
            if (value == null)
            {
                return null;
            }

            if (value.Any(static character =>
                    character < ' ' ||
                    character > '~'))
            {
                return null;
            }

            return value;
        }

        private static string StatusName(CapabilityStatus status) => status switch
        {
            CapabilityStatus.Supported => "supported",
            CapabilityStatus.Planned => "planned",
            _ => "guiOnly",
        };
    }
}
