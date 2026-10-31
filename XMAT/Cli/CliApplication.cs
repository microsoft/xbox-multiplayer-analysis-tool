// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XMAT.Operations;

namespace XMAT.Cli
{
    // Standard output receives exactly one JSON document; standard error receives data-free text for people.
    internal sealed class CliConsole
    {
        internal CliConsole(Stream output, TextWriter error)
        {
            Output = output;
            Error = error;
        }

        internal Stream Output { get; }

        internal TextWriter Error { get; }
    }

    // Parses the command line, runs the command and writes its report. Help, --version, doctor and usage errors never
    // write files or touch legacy statics; only analyze reserves a staging folder and isolates them (CliAnalyzeCommand).
    internal static class CliApplication
    {
        private static readonly byte[] NewLine = { (byte)'\r', (byte)'\n' };

        internal static async Task<int> RunAsync(IReadOnlyList<string> args, CliConsole console)
        {
            string runId = Guid.NewGuid().ToString("N");
            CliParseResult parsed = null;
            CliSignals signals = null;
            CliReport report;
            try
            {
                parsed = CliCommandLine.Parse(args);
                if (!parsed.Succeeded)
                {
                    report = CliReport.ForParseError(runId, parsed);
                }
                else if (parsed.Request.Command == CliCommands.Analyze)
                {
                    // Not disposed: a late signal handler may still cancel it while the process exits.
                    var cancellation = new CancellationTokenSource();
                    signals = CliSignals.Register(cancellation);
                    report = await CliAnalyzeCommand.RunAsync(parsed.Request, runId, cancellation.Token).ConfigureAwait(false);
                }
                else if (parsed.Request.Command == CliCommands.Doctor)
                {
                    report = CliDoctorCommand.Run(parsed.Request, runId);
                }
                else if (parsed.Request.Command == CliCommands.Help)
                {
                    report = CliHelp.Create(runId, parsed.Request.Topic);
                }
                else if (parsed.Request.Command == CliCommands.Version)
                {
                    report = CliReport.ForVersion(runId);
                }
                else
                {
                    throw new InvalidOperationException("The parser returned a command that xmat does not run.");
                }
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                report = CliReport.ForInternalError(runId, parsed, ex);
            }

            try
            {
                Write(console, report);
            }
            finally
            {
                signals?.Dispose();
            }

            return report.ExitCode;
        }

        // Writes the text for people to standard error, then the JSON document to standard output. The document is produced
        // before anything is written (Serialize); writing is best effort, and the exit code reports the result regardless.
        internal static void Write(CliConsole console, CliReport report)
        {
            byte[] document = Serialize(report);
            try
            {
                foreach (string line in report.HumanLines)
                {
                    console.Error.WriteLine(line);
                }

                foreach (OperationDiagnostic warning in report.Warnings)
                {
                    console.Error.WriteLine($"xmat: warning {warning.Code}: {warning.Message}");
                }

                foreach (OperationDiagnostic error in report.Errors)
                {
                    console.Error.WriteLine($"xmat: error {error.Code}: {error.Message}");
                }

                if (report.ExitCode == CliExitCodes.Usage)
                {
                    console.Error.WriteLine("xmat: run xmat help for usage.");
                }

                console.Error.Flush();
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                // Standard error is gone (for example a closed pipe) or the report is damaged; the JSON document and the exit
                // code still count.
            }

            try
            {
                console.Output.Write(document, 0, document.Length);
                console.Output.Write(NewLine, 0, NewLine.Length);
                console.Output.Flush();
            }
            catch (IOException)
            {
                // Standard output is gone; the exit code still reports the result.
            }
        }

        // The JSON document of the report. When it cannot be produced, the fixed fallback document replaces it with the
        // error cli.internal.output. The run keeps its exit code: 7 stays 7, and a run that published --out keeps 0 or 10
        // and lists its artifacts. Only a run that published nothing and would have succeeded (help, --version, doctor),
        // whose document was its result, becomes an internal error (exit code 70).
        private static byte[] Serialize(CliReport report)
        {
            try
            {
                return CliJson.Serialize(report);
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                if (report.Artifacts.Count == 0 &&
                    (report.ExitCode == CliExitCodes.Success || report.ExitCode == CliExitCodes.FindingsFound))
                {
                    report.SetExitCode(CliExitCodes.Internal);
                }

                report.Errors.Add(CliJson.OutputFailure);

                // The exception type helps a bug report; its message can hold paths or captured data, so it is never shown.
                report.HumanLines.Add($"xmat: internal error ({ex.GetType().FullName}).");
                return CliJson.SerializeFallback(report);
            }
        }
    }
}
