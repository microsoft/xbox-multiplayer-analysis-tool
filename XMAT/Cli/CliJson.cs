// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using XMAT.Operations;
using XMAT.Operations.Analysis;
using XMAT.Operations.Capabilities;
using XMAT.Operations.Export;

namespace XMAT.Cli
{
    // Writes xmat's JSON field by field, so only allow-listed values reach the output: rule names, levels, counts,
    // service labels, ordinal ids, the input kind and SHA-256, the effective limits, the validated process selectors of
    // the command line, an export's format and its published file's size and SHA-256, codes and fixed messages. Never
    // AnalysisResult.Legacy, captured content or paths.
    internal static class CliJson
    {
        // summary.json in a published --out folder.
        internal const string SummarySchema = "xmat-analysis/1";

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

                if (report.Export != null)
                {
                    WriteExport(writer, report.Export);
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

        private static void WriteAnalysis(Utf8JsonWriter writer, CliAnalysisReport analysis)
        {
            writer.WriteStartObject("input");
            writer.WriteString("kind", analysis.InputKind);
            writer.WriteString("sha256", analysis.InputSha256);
            writer.WriteEndObject();
            WriteLimits(writer, analysis.Limits);

            if (analysis.ProcessFilter != null)
            {
                writer.WritePropertyName("processFilter");
                ProcessScopeJson.Write(writer, analysis.ProcessFilter, analysis.ProcessScope);
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

        // The export block: counts instead of the host, status and method values, the validated process selectors, and the
        // publication. Null counts and output fields mean that the run ended before they were known.
        private static void WriteExport(Utf8JsonWriter writer, CliExportReport export)
        {
            ExportCaptureResult result = export.Result;
            CaptureSelectionSpec selection = export.Selection;
            writer.WriteStartObject("export");
            writer.WriteString("format", export.Format is { } format ? CliCommandLine.FormatName(format) : null);
            writer.WriteStartObject("input");
            writer.WriteString("kind", result?.InputKind);
            writer.WriteString("sha256", result?.InputSha256);
            writer.WriteEndObject();
            WriteLimits(writer, export.Limits);
            writer.WriteStartObject("selection");
            writer.WriteString("device", export.DeviceId);
            writer.WriteStartObject("host");
            writer.WriteString("mode", selection?.Host.Mode == HostMatchMode.Exact ? "exact" : "glob");
            WriteCount(writer, "include", selection?.Host.Include);
            WriteCount(writer, "exclude", selection?.Host.Exclude ?? ImmutableArray<string>.Empty);
            writer.WriteEndObject();
            WriteCount(writer, "statuses", selection?.Statuses);
            WriteCount(writer, "methods", selection?.Methods);
            writer.WriteBoolean("matchNone", selection?.MatchNone ?? false);
            writer.WriteEndObject();
            writer.WritePropertyName("processFilter");
            ProcessScopeJson.Write(writer, selection?.Process, result?.ProcessScope);
            if (result?.Counts == null)
            {
                writer.WriteNull("counts");
            }
            else
            {
                writer.WriteStartObject("counts");
                WriteCounts(writer, result.Counts);
                writer.WriteEndObject();
            }

            writer.WriteStartArray("devices");
            foreach (KeyValuePair<string, CaptureExportCounts> device in
                (result?.Devices ?? ImmutableDictionary<string, CaptureExportCounts>.Empty)
                    .OrderBy(entry => entry.Key.Length)
                    .ThenBy(entry => entry.Key, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("id", device.Key);
                WriteCounts(writer, device.Value);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            if (export.Format == CaptureExportFormat.SanitizedSaz && result?.Sanitization is { } sanitization)
            {
                writer.WriteStartObject("sanitization");
                writer.WriteNumber("redactedValues", sanitization.RedactedValues);
                writer.WriteNumber("omittedBodies", sanitization.OmittedBodies);
                writer.WriteNumber("omittedHeaders", sanitization.OmittedHeaders);
                writer.WriteEndObject();
            }

            writer.WriteStartObject("output");
            writer.WriteBoolean("published", result?.Published ?? false);
            if (result?.OutputBytes is long bytes)
            {
                writer.WriteNumber("bytes", bytes);
            }
            else
            {
                writer.WriteNull("bytes");
            }

            writer.WriteString("sha256", result?.OutputSha256);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        private static void WriteCount(Utf8JsonWriter writer, string name, ImmutableArray<string>? values)
        {
            if (values is { IsDefault: false } given)
            {
                writer.WriteNumber(name, given.Length);
            }
            else
            {
                writer.WriteNull(name);
            }
        }

        private static void WriteCounts(Utf8JsonWriter writer, CaptureExportCounts counts)
        {
            writer.WriteNumber("total", counts.Total);
            writer.WriteNumber("matched", counts.Matched);
            writer.WriteNumber("selected", counts.Selected);
            writer.WriteNumber("eligible", counts.Eligible);
            writer.WriteNumber("exported", counts.Exported);
            writer.WriteNumber("unavailable", counts.Unavailable);
            writer.WriteNumber("skippedIncomplete", counts.SkippedIncomplete);
            writer.WriteNumber("noRecordedResponse", counts.NoRecordedResponse);
        }

        private static void WriteLimits(Utf8JsonWriter writer, CliLimits limits)
        {
            if (limits != null)
            {
                writer.WriteStartObject("limits");
                writer.WriteNumber("maxInputMib", limits.MaxInputMib);
                writer.WriteNumber("maxDataMib", limits.MaxDataMib);
                writer.WriteNumber("maxRecordMib", limits.MaxRecordMib);
                writer.WriteNumber("maxRecords", limits.MaxRecords);
                writer.WriteEndObject();
            }
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
