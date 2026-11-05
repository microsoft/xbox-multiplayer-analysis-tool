// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using XMAT.Operations;
using XMAT.Operations.Export;
using XMAT.Operations.Processes;

namespace XMAT.Cli
{
    internal static class CliCommands
    {
        internal const string Analyze = "analyze";
        internal const string Doctor = "doctor";
        internal const string Export = "export";
        internal const string Help = "help";
        internal const string Version = "version";

        // The commands xmat.exe runs. Each one needs a Supported capability in the registry (XMAT.Tests/Cli checks it).
        internal static IReadOnlyList<string> Implemented { get; } = new[] { Analyze, Doctor, Export };
    }

    internal sealed record CliRequest(
        string Command,
        string Input = null,
        string Output = null,
        string DeviceId = null,
        bool FailOnFindings = false,
        string Target = null,
        string Topic = null,
        CliLimits Limits = null,
        ProcessFilterSpec ProcessFilter = null,
        CaptureExportFormat? Format = null,
        CaptureSelectionSpec Selection = null);

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
        internal const string ProcessOption = "--process";
        internal const string ExcludeProcessOption = "--exclude-process";
        internal const string ForOption = "--for";
        internal const string FormatOption = "--format";
        internal const string HostOption = "--host";
        internal const string ExcludeHostOption = "--exclude-host";
        internal const string HostModeOption = "--host-mode";
        internal const string StatusOption = "--status";
        internal const string MethodOption = "--method";
        internal const string MatchNoneSwitch = "--match-none";
        internal const string VersionSwitch = "--version";
        internal const string FindingsValue = "findings";

        // Unknown commands, options and doctor targets get fixed messages.
        internal const string UnknownCommandMessage = "Unknown command. Run xmat help for the commands.";
        internal const string UnknownOptionMessage = "Unknown option. Run xmat help for the options.";
        internal const string UnknownTargetMessage = "Unknown doctor target. Run xmat help doctor for the targets.";

        // A selector can be a path or another value that must not be repeated, so the message never names it.
        internal const string InvalidProcessSelectorMessage =
            "--process and --exclude-process take an image name such as game*.exe (* and ? are wildcards, .exe is " +
            "optional) or a process id, each at most 260 characters and at most 64 of each option.";

        private const string DeviceMessage = "--device takes a device id such as d1 from a previous summary.";

        private static readonly HashSet<string> AnalyzeOptions = new(StringComparer.Ordinal)
        {
            OutOption,
            DeviceOption,
            FailOnOption,
            CliLimits.InputOption,
            CliLimits.DataOption,
            CliLimits.RecordOption,
            CliLimits.RecordsOption,
            ProcessOption,
            ExcludeProcessOption,
        };

        // The only options that may be given more than once; each value adds a selector.
        private static readonly HashSet<string> RepeatableAnalyzeOptions = new(StringComparer.Ordinal)
        {
            ProcessOption,
            ExcludeProcessOption,
        };

        private static readonly HashSet<string> DoctorOptions = new(StringComparer.Ordinal) { ForOption };

        private static readonly HashSet<string> ExportOptions = new(StringComparer.Ordinal)
        {
            OutOption,
            FormatOption,
            DeviceOption,
            ProcessOption,
            ExcludeProcessOption,
            HostOption,
            ExcludeHostOption,
            HostModeOption,
            StatusOption,
            MethodOption,
            MatchNoneSwitch,
            CliLimits.InputOption,
            CliLimits.DataOption,
            CliLimits.RecordOption,
            CliLimits.RecordsOption,
        };

        private static readonly HashSet<string> RepeatableExportOptions = new(StringComparer.Ordinal)
        {
            ProcessOption,
            ExcludeProcessOption,
            HostOption,
            ExcludeHostOption,
            StatusOption,
            MethodOption,
        };

        // The values the GUI can show as an empty choice (a session without a status, method or host) may be given
        // empty, as --status= or --status "".
        private static readonly HashSet<string> EmptyLiteralOptions = new(StringComparer.Ordinal)
        {
            HostOption,
            ExcludeHostOption,
            StatusOption,
            MethodOption,
        };

        private static readonly HashSet<string> ExportSwitches = new(StringComparer.Ordinal) { MatchNoneSwitch };

        // The --format values, in CaptureExportFormat order.
        private static readonly string[] FormatNames = { "xmatcap", "saz", "sanitized-saz" };

        // The options that analyze accepts; its command line in the capability registry names exactly these.
        internal static IReadOnlySet<string> AnalyzeOptionNames => AnalyzeOptions;

        // The options that export accepts; the registry's export command lines name exactly these.
        internal static IReadOnlySet<string> ExportOptionNames => ExportOptions;

        internal static string FormatName(CaptureExportFormat format) => FormatNames[(int)format];

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

            if (first == CliCommands.Export)
            {
                return ParseExport(args);
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
            CliParseResult error = arguments.Read(args, command, AnalyzeOptions, RepeatableAnalyzeOptions);
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
                return CliParseResult.UsageError(command, "cli.usage.invalid-value", DeviceMessage);
            }

            bool failOnFindings = arguments.Options.TryGetValue(FailOnOption, out string failOn);
            if (failOnFindings && !string.Equals(failOn, FindingsValue, StringComparison.Ordinal))
            {
                return CliParseResult.UsageError(command, "cli.usage.invalid-value", "--fail-on takes the value findings.");
            }

            CliLimits limits = ReadLimits(arguments, command, out CliParseResult invalidLimit);
            if (invalidLimit != null)
            {
                return invalidLimit;
            }

            ProcessFilterSpec processFilter = null;
            if (arguments.RepeatedOptions.Count != 0)
            {
                OperationResult<ProcessFilterSpec> selectors = ProcessFilter.Parse(
                    arguments.ValuesOf(ProcessOption),
                    arguments.ValuesOf(ExcludeProcessOption));
                if (!selectors.Succeeded || selectors.Value == null)
                {
                    return CliParseResult.UsageError(command, "cli.usage.invalid-value", InvalidProcessSelectorMessage);
                }

                processFilter = selectors.Value;
            }

            return CliParseResult.Success(new CliRequest(
                command,
                Input: arguments.Positionals[0],
                Output: output,
                DeviceId: deviceId,
                FailOnFindings: failOnFindings,
                Limits: limits,
                ProcessFilter: processFilter));
        }

        private static CliParseResult ParseExport(IReadOnlyList<string> args)
        {
            const string command = CliCommands.Export;
            var arguments = new CliArguments();
            CliParseResult error = arguments.Read(
                args, command, ExportOptions, RepeatableExportOptions, ExportSwitches, EmptyLiteralOptions);
            if (error != null)
            {
                return error;
            }

            if (arguments.HelpRequested)
            {
                return HelpFor(command);
            }

            if (arguments.Positionals.Count != 1)
            {
                return CliParseResult.UsageError(
                    command,
                    arguments.Positionals.Count == 0 ? "cli.usage.missing-argument" : "cli.usage.unexpected-argument",
                    "export takes a single INPUT capture file.");
            }

            if (!arguments.Options.TryGetValue(OutOption, out string output))
            {
                return CliParseResult.UsageError(command, "cli.usage.missing-option", "export needs --out FILE.");
            }

            if (arguments.Options.TryGetValue(DeviceOption, out string deviceId) && !IsDeviceId(deviceId))
            {
                return CliParseResult.UsageError(command, "cli.usage.invalid-value", DeviceMessage);
            }

            CaptureExportFormat? format = ExportFormat(output, arguments.Options.GetValueOrDefault(FormatOption));
            if (format == null)
            {
                return CliParseResult.UsageError(
                    command,
                    "cli.usage.invalid-value",
                    "--out must end in .xmatcap or .saz, and --format (xmatcap, saz or sanitized-saz, which needs .saz) " +
                        "must agree with it.");
            }

            string mode = arguments.Options.GetValueOrDefault(HostModeOption, "glob");
            if (mode is not ("glob" or "exact"))
            {
                return CliParseResult.UsageError(command, "cli.usage.invalid-value", "--host-mode takes glob or exact.");
            }

            CliLimits limits = ReadLimits(arguments, command, out CliParseResult invalidLimit);
            if (invalidLimit != null)
            {
                return invalidLimit;
            }

            OperationResult<ProcessFilterSpec> process = ProcessFilter.Parse(
                arguments.ValuesOf(ProcessOption),
                arguments.ValuesOf(ExcludeProcessOption));
            if (!process.Succeeded || process.Value == null)
            {
                return CliParseResult.UsageError(command, "cli.usage.invalid-value", InvalidProcessSelectorMessage);
            }

            OperationResult<CaptureSelectionSpec> selection = CaptureSelectionValidator.Validate(new CaptureSelectionSpec(
                process.Value,
                new HostSelection(
                    mode == "exact" ? HostMatchMode.Exact : HostMatchMode.Glob,
                    Given(arguments, HostOption),
                    arguments.ValuesOf(ExcludeHostOption).ToImmutableArray()),
                Given(arguments, StatusOption),
                Given(arguments, MethodOption),
                null,
                arguments.Options.ContainsKey(MatchNoneSwitch)));
            if (!selection.Succeeded || selection.Value == null)
            {
                return CliParseResult.UsageError(
                    command,
                    "cli.usage.invalid-value",
                    "--host and --exclude-host take host names or host/path patterns (at most 64 of each, 260 characters " +
                        "each), or with --host-mode exact any host value that the capture shows, the empty one included; " +
                        "--status and --method take any of the values that the capture shows.");
            }

            return CliParseResult.Success(new CliRequest(
                command,
                Input: arguments.Positionals[0],
                Output: output,
                DeviceId: deviceId,
                Limits: limits,
                Format: format,
                Selection: selection.Value));
        }

        // An ordinary format follows --out's extension; a sanitized archive is only made when asked for. A format that
        // does not agree with the extension is null, so a file never gets a misleading name.
        private static CaptureExportFormat? ExportFormat(string output, string format)
        {
            format ??= CaptureExportFormats.HasExpectedExtension(CaptureExportFormat.Xmatcap, output) ? "xmatcap" : "saz";
            int index = Array.IndexOf(FormatNames, format);
            return index >= 0 && CaptureExportFormats.HasExpectedExtension((CaptureExportFormat)index, output)
                ? (CaptureExportFormat)index
                : null;
        }

        // A repeatable option that was given selects its values; one that was not leaves its filter off (null).
        private static ImmutableArray<string>? Given(CliArguments arguments, string option) =>
            arguments.RepeatedOptions.TryGetValue(option, out List<string> values) ? values.ToImmutableArray() : null;

        private static CliLimits ReadLimits(CliArguments arguments, string command, out CliParseResult error)
        {
            CliLimits limits = CliLimits.Read(arguments.Options, out CliLimitOption invalid);
            error = limits == null
                ? CliParseResult.UsageError(command, "cli.usage.invalid-value", invalid.RangeMessage)
                : limits.MaxRecordMib > limits.MaxDataMib
                    ? CliParseResult.UsageError(command, "cli.usage.invalid-value", CliLimits.RecordOverDataMessage)
                    : null;
            return limits;
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

    // The options (--name VALUE or --name=VALUE, each at most once unless the command makes it repeatable), positional
    // arguments and help switches that follow a command. "--" ends the options, so an INPUT that starts with '-' can still
    // be named.
    internal sealed class CliArguments
    {
        internal Dictionary<string, string> Options { get; } = new(StringComparer.Ordinal);

        // The values of the repeatable options that were given, in command-line order.
        internal Dictionary<string, List<string>> RepeatedOptions { get; } = new(StringComparer.Ordinal);

        internal List<string> Positionals { get; } = new();

        internal bool HelpRequested { get; private set; }

        internal IReadOnlyList<string> ValuesOf(string option) =>
            RepeatedOptions.TryGetValue(option, out List<string> values) ? values : Array.Empty<string>();

        // Returns null when the arguments are valid, otherwise the usage error. A switch takes no value; an option in
        // emptyValues may be given an empty value.
        internal CliParseResult Read(
            IReadOnlyList<string> args,
            string command,
            IReadOnlySet<string> allowedOptions,
            IReadOnlySet<string> repeatableOptions = null,
            IReadOnlySet<string> switches = null,
            IReadOnlySet<string> emptyValues = null)
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

                if (switches != null && switches.Contains(name))
                {
                    if (value != null)
                    {
                        return CliParseResult.UsageError(command, "cli.usage.invalid-value", $"The {name} switch takes no value.");
                    }

                    if (!Options.TryAdd(name, string.Empty))
                    {
                        return CliParseResult.UsageError(command, "cli.usage.duplicate-option", $"The {name} option was given more than once.");
                    }

                    continue;
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

                if (value.Length == 0 && (emptyValues == null || !emptyValues.Contains(name)))
                {
                    return CliParseResult.UsageError(command, "cli.usage.missing-value", $"The {name} option needs a value.");
                }

                if (repeatableOptions != null && repeatableOptions.Contains(name))
                {
                    if (!RepeatedOptions.TryGetValue(name, out List<string> values))
                    {
                        values = new List<string>();
                        RepeatedOptions.Add(name, values);
                    }

                    values.Add(value);
                    continue;
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
