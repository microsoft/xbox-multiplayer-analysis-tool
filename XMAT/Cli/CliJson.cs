// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using XMAT.Operations;
using XMAT.Operations.Analysis;
using XMAT.Operations.Capabilities;

namespace XMAT.Cli
{
    // Writes xmat's JSON field by field, so only allow-listed values reach the output: rule names, levels, counts,
    // service labels, ordinal ids, the input kind and SHA-256, the effective limits, codes and fixed messages. Never
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
                foreach (CliArtifact artifact in report.Artifacts)
                {
                    if (artifact != null)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("kind", FixedOrNull(artifact.Kind));
                        writer.WriteString("path", FixedOrNull(artifact.Path));
                        writer.WriteEndObject();
                    }
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
            if (analysis.Limits != null)
            {
                writer.WriteStartObject("limits");
                writer.WriteNumber("maxInputMib", analysis.Limits.MaxInputMib);
                writer.WriteNumber("maxDataMib", analysis.Limits.MaxDataMib);
                writer.WriteNumber("maxRecordMib", analysis.Limits.MaxRecordMib);
                writer.WriteNumber("maxRecords", analysis.Limits.MaxRecords);
                writer.WriteEndObject();
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

            foreach (char character in value)
            {
                if (character < ' ' || character > '~')
                {
                    return null;
                }
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
