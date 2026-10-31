// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using XMAT.Operations;

namespace XMAT.Cli
{
    internal static class CliCommands
    {
        internal const string Analyze = "analyze";
        internal const string Doctor = "doctor";
        internal const string Help = "help";
        internal const string Version = "version";

        // The commands xmat.exe runs. Each one needs a Supported capability in the registry (XMAT.Tests/Cli checks it).
        internal static IReadOnlyList<string> Implemented { get; } = new[] { Analyze, Doctor };
    }

    internal sealed record CliRequest(
        string Command,
        string Input = null,
        string Output = null,
        string DeviceId = null,
        bool FailOnFindings = false,
        string Target = null,
        string Topic = null,
        CliLimits Limits = null);

    // A parsed request, or a data-free usage error (exit code 2).
    internal sealed record CliParseResult(CliRequest Request, string Command, OperationDiagnostic Error)
    {
        internal bool Succeeded => Error == null;

        internal static CliParseResult Success(CliRequest request) => new(request, request.Command, null);

        internal static CliParseResult UsageError(string command, string code, string message) =>
            new(null, command, new OperationDiagnostic(code, OperationDiagnosticSeverity.Error, message));
    }

    // Parses the command line without side effects: it reads only the arguments and the capability registry. Messages
    // name only commands, options, values and targets from the catalog and never repeat what the user typed: values can
    // be paths, and an unknown word can be a secret.
    internal static class CliCommandLine
    {
        internal const string OutOption = "--out";
        internal const string DeviceOption = "--device";
        internal const string FailOnOption = "--fail-on";
        internal const string ForOption = "--for";
        internal const string VersionSwitch = "--version";
        internal const string FindingsValue = "findings";

        // Unknown commands, options and doctor targets get fixed messages.
        internal const string UnknownCommandMessage = "Unknown command. Run xmat help for the commands.";
        internal const string UnknownOptionMessage = "Unknown option. Run xmat help for the options.";
        internal const string UnknownTargetMessage = "Unknown doctor target. Run xmat help doctor for the targets.";

        private static readonly HashSet<string> AnalyzeOptions = new(StringComparer.Ordinal)
        {
            OutOption,
            DeviceOption,
            FailOnOption,
            CliLimits.InputOption,
            CliLimits.DataOption,
            CliLimits.RecordOption,
            CliLimits.RecordsOption,
        };

        private static readonly HashSet<string> DoctorOptions = new(StringComparer.Ordinal) { ForOption };

        // The options that analyze accepts; its command line in the capability registry names exactly these.
        internal static IReadOnlySet<string> AnalyzeOptionNames => AnalyzeOptions;

        internal static CliParseResult Parse(IReadOnlyList<string> args)
        {
            if (args == null || args.Count == 0)
            {
                return CliParseResult.UsageError(null, "cli.usage.no-command", "No command was given.");
            }

            string first = args[0] ?? string.Empty;
            if (first == CliCommands.Help || IsHelpSwitch(first))
            {
                return ParseHelp(args);
            }

            if (first == VersionSwitch)
            {
                return args.Count == 1
                    ? CliParseResult.Success(new CliRequest(CliCommands.Version))
                    : CliParseResult.UsageError(CliCommands.Version, "cli.usage.unexpected-argument", "--version takes no arguments.");
            }

            if (first == CliCommands.Analyze)
            {
                return ParseAnalyze(args);
            }

            if (first == CliCommands.Doctor)
            {
                return ParseDoctor(args);
            }

            if (first.StartsWith('-'))
            {
                return CliParseResult.UsageError(null, "cli.usage.unknown-option", UnknownOptionMessage);
            }

            return CliParseResult.UsageError(null, "cli.usage.unknown-command", UnknownCommandMessage);
        }

        internal static bool IsHelpSwitch(string token) => token is "--help" or "-h" or "-?" or "/?";

        private static CliParseResult ParseHelp(IReadOnlyList<string> args)
        {
            if (args.Count == 1)
            {
                return CliParseResult.Success(new CliRequest(CliCommands.Help));
            }

            if (args.Count > 2)
            {
                return CliParseResult.UsageError(CliCommands.Help, "cli.usage.unexpected-argument", "help takes at most one command name.");
            }

            string topic = args[1] ?? string.Empty;
            return CliCatalog.IsImplemented(topic)
                ? HelpFor(topic)
                : CliParseResult.UsageError(CliCommands.Help, "cli.usage.unknown-command", UnknownCommandMessage);
        }

        private static CliParseResult HelpFor(string command) =>
            CliParseResult.Success(new CliRequest(CliCommands.Help, Topic: command));

        private static CliParseResult ParseAnalyze(IReadOnlyList<string> args)
        {
            const string command = CliCommands.Analyze;
            var arguments = new CliArguments();
            CliParseResult error = arguments.Read(args, command, AnalyzeOptions);
            if (error != null)
            {
                return error;
            }

            if (arguments.HelpRequested)
            {
                return HelpFor(command);
            }

            if (arguments.Positionals.Count == 0)
            {
                return CliParseResult.UsageError(command, "cli.usage.missing-argument", "analyze needs the INPUT capture file.");
            }

            if (arguments.Positionals.Count > 1)
            {
                return CliParseResult.UsageError(command, "cli.usage.unexpected-argument", "analyze takes a single INPUT capture file.");
            }

            if (!arguments.Options.TryGetValue(OutOption, out string output))
            {
                return CliParseResult.UsageError(command, "cli.usage.missing-option", "analyze needs --out DIR.");
            }

            if (arguments.Options.TryGetValue(DeviceOption, out string deviceId) && !IsDeviceId(deviceId))
            {
                return CliParseResult.UsageError(
                    command, "cli.usage.invalid-value", "--device takes a device id such as d1 from a previous summary.");
            }

            bool failOnFindings = arguments.Options.TryGetValue(FailOnOption, out string failOn);
            if (failOnFindings && !string.Equals(failOn, FindingsValue, StringComparison.Ordinal))
            {
                return CliParseResult.UsageError(command, "cli.usage.invalid-value", "--fail-on takes the value findings.");
            }

            CliLimits limits = CliLimits.Read(arguments.Options, out CliLimitOption invalidLimit);
            if (limits == null)
            {
                return CliParseResult.UsageError(command, "cli.usage.invalid-value", invalidLimit.RangeMessage);
            }

            if (limits.MaxRecordMib > limits.MaxDataMib)
            {
                return CliParseResult.UsageError(command, "cli.usage.invalid-value", CliLimits.RecordOverDataMessage);
            }

            return CliParseResult.Success(new CliRequest(
                command,
                Input: arguments.Positionals[0],
                Output: output,
                DeviceId: deviceId,
                FailOnFindings: failOnFindings,
                Limits: limits));
        }

        private static CliParseResult ParseDoctor(IReadOnlyList<string> args)
        {
            const string command = CliCommands.Doctor;
            var arguments = new CliArguments();
            CliParseResult error = arguments.Read(args, command, DoctorOptions);
            if (error != null)
            {
                return error;
            }

            if (arguments.HelpRequested)
            {
                return HelpFor(command);
            }

            if (arguments.Positionals.Count != 0)
            {
                return CliParseResult.UsageError(command, "cli.usage.unexpected-argument", "doctor takes no arguments besides --for analyze.");
            }

            if (!arguments.Options.TryGetValue(ForOption, out string target))
            {
                return CliParseResult.UsageError(command, "cli.usage.missing-option", "doctor needs --for analyze.");
            }

            return CliCatalog.IsDoctorTarget(target)
                ? CliParseResult.Success(new CliRequest(command, Target: target))
                : CliParseResult.UsageError(
                    command,
                    "cli.usage.unknown-target",
                    UnknownTargetMessage);
        }

        // Ordinal device ids from a previous summary: d1, d2, ...
        private static bool IsDeviceId(string value)
        {
            if (value.Length < 2 || value.Length > 10 || value[0] != 'd' || value[1] == '0')
            {
                return false;
            }

            for (int index = 1; index < value.Length; index++)
            {
                if (!char.IsAsciiDigit(value[index]))
                {
                    return false;
                }
            }

            return true;
        }
    }

    // The options (--name VALUE or --name=VALUE, each at most once), positional arguments and help switches that follow
    // a command. "--" ends the options, so an INPUT that starts with '-' can still be named.
    internal sealed class CliArguments
    {
        internal Dictionary<string, string> Options { get; } = new(StringComparer.Ordinal);

        internal List<string> Positionals { get; } = new();

        internal bool HelpRequested { get; private set; }

        // Returns null when the arguments are valid, otherwise the usage error.
        internal CliParseResult Read(IReadOnlyList<string> args, string command, IReadOnlySet<string> allowedOptions)
        {
            bool onlyPositionals = false;
            for (int index = 1; index < args.Count; index++)
            {
                string token = args[index] ?? string.Empty;
                if (onlyPositionals)
                {
                    Positionals.Add(token);
                    continue;
                }

                if (token == "--")
                {
                    onlyPositionals = true;
                    continue;
                }

                if (CliCommandLine.IsHelpSwitch(token))
                {
                    HelpRequested = true;
                    continue;
                }

                if (token.Length < 2 || !token.StartsWith('-'))
                {
                    Positionals.Add(token);
                    continue;
                }

                string name = token;
                string value = null;
                int separator = token.IndexOf('=');
                if (separator > 0)
                {
                    name = token.Substring(0, separator);
                    value = token.Substring(separator + 1);
                }

                if (!allowedOptions.Contains(name))
                {
                    return CliParseResult.UsageError(
                        command, "cli.usage.unknown-option", $"{command} does not accept this option. Run xmat help {command}.");
                }

                if (value == null)
                {
                    if (index + 1 >= args.Count || IsOptionOrSwitch(args[index + 1]))
                    {
                        return CliParseResult.UsageError(command, "cli.usage.missing-value", $"The {name} option needs a value.");
                    }

                    index++;
                    value = args[index] ?? string.Empty;
                }

                if (value.Length == 0)
                {
                    return CliParseResult.UsageError(command, "cli.usage.missing-value", $"The {name} option needs a value.");
                }

                if (!Options.TryAdd(name, value))
                {
                    return CliParseResult.UsageError(command, "cli.usage.duplicate-option", $"The {name} option was given more than once.");
                }
            }

            return null;
        }

        private static bool IsOptionOrSwitch(string token) =>
            token == null || token.StartsWith("--", StringComparison.Ordinal) || CliCommandLine.IsHelpSwitch(token);
    }
}
