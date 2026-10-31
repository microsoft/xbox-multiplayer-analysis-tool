// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using XMAT.Operations.Analysis;

namespace XMAT.Cli
{
    // xmat doctor --for analyze checks what analyze needs and changes nothing: it only reads files next to xmat.exe
    // (through AppContext.BaseDirectory). Like help and usage errors it never touches PublicUtilities or other statics
    // that create folders or open logs.
    internal static class CliDoctorCommand
    {
        internal static CliReport Run(CliRequest request, string runId)
        {
            var report = new CliReport(CliCommands.Doctor, runId);
            var doctor = new CliDoctorReport(request.Target)
            {
                Capabilities = CliCatalog.CapabilitiesOf(request.Target),
                Framework = RuntimeInformation.FrameworkDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            };
            report.Doctor = doctor;
            doctor.Checks.AddRange(CliPrerequisites.Check());
            if (doctor.Checks.TrueForAll(check => check.Passed))
            {
                report.SetExitCode(CliExitCodes.Success);
            }
            else
            {
                report.Fail(CliExitCodes.Prerequisite, "cli.prerequisite.missing", "A file that xmat needs is missing or invalid; see the failed checks.");
            }

            return report;
        }
    }

    // What analyze needs from its installation, checked without writing anything or touching GUI statics: valid
    // analysis data (the shared AnalysisAssets check that the GUI's loader also applies) and valid English strings.
    internal static class CliPrerequisites
    {
        internal const string LanguageCheck = "localization.en-us";
        internal const string InvalidDataCode = "cli.prerequisite.invalid";

        internal static IReadOnlyList<CliCheck> Check()
        {
            string root = AppContext.BaseDirectory;
            var checks = AnalysisAssets.Check(root)
                .Select(asset => new CliCheck(asset.Id, asset.Passed, asset.Passed ? null : asset.Code ?? InvalidDataCode))
                .ToList();
            checks.Add(CheckLanguage(root));
            checks.Add(new CliCheck("runtime.64-bit", Environment.Is64BitProcess, Environment.Is64BitProcess ? null : "runtime.not-64-bit"));
            return checks;
        }

        // Langs\en-us.json must be what the GUI's Localization expects: a JSON object whose values are all strings. It is
        // parsed here directly, so the check never loads Localization.
        internal static CliCheck CheckLanguage(string root)
        {
            string path = Path.Combine(root, "Langs", "en-us.json");
            if (!File.Exists(path))
            {
                return new CliCheck(LanguageCheck, false, "localization.missing");
            }

            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                return new CliCheck(LanguageCheck, false, "localization.unreadable");
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(text);
                JsonElement strings = document.RootElement;
                bool valid = strings.ValueKind == JsonValueKind.Object &&
                    strings.EnumerateObject().Any() &&
                    strings.EnumerateObject().All(property => property.Value.ValueKind == JsonValueKind.String);
                return valid
                    ? new CliCheck(LanguageCheck, true)
                    : new CliCheck(LanguageCheck, false, "localization.invalid");
            }
            catch (JsonException)
            {
                return new CliCheck(LanguageCheck, false, "localization.invalid");
            }
        }
    }
}
