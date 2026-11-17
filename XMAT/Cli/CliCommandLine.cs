// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using XMAT.Operations;
using XMAT.Operations.Analysis;
using XMAT.Operations.Export;
using XMAT.Operations.Processes;
using XMAT.Operations.Replay;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Cli
{
    internal static class CliCommands
    {
        internal const string Analyze = "analyze";
        internal const string Compose = "compose";
        internal const string Curl = "curl";
        internal const string Doctor = "doctor";
        internal const string Export = "export";
        internal const string Help = "help";
        internal const string Replay = "replay";
        internal const string SaveRequest = "save-request";
        internal const string Version = "version";

        // The commands xmat.exe runs. Each one needs a Supported capability in the registry (XMAT.Tests/Cli checks it).
        internal static IReadOnlyList<string> Implemented { get; } = new[] { Analyze, Compose, Curl, Doctor, Export, Replay, SaveRequest };
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
        CliReplay Replay = null,
        ProcessFilterSpec ProcessFilter = null,
        CaptureExportFormat? Format = null,
        CaptureSelectionSpec Selection = null);

    // What xmat replay, compose and save-request send or save, as their command lines give it. Headers and RemoveHeaders
    // keep the order they were given in; BodyFile and RequestFile are read when the run starts. Selection is the --request
    // text (replay and save-request) and Sequence the --sequence text (compose only), both already validated. RequestFile
    // is a request file to start from (compose and save-request); ClearHeaders and EmptyBody complete what such a file left
    // out; HttpVersionSet says whether --http-version was given, which then wins over a file's version. IncludeSensitive
    // saves captured content (save-request only). Form is the multipart form of --form, --form-string and --form-type, in
    // command-line order, or null; its files are read when the run starts (CliRequestBody). SaveSaz also saves the sends as
    // a Fiddler archive, replays.saz (replay and compose).
    internal sealed record CliReplay(
        string Selection = null,
        int Repeat = 1,
        int Parallel = 1,
        int TimeoutSeconds = 30,
        int MaxResponseMib = 16,
        int FollowRedirects = 0,
        string Url = null,
        string Origin = null,
        string Method = null,
        IReadOnlyList<KeyValuePair<string, string>> Headers = null,
        IReadOnlyList<string> RemoveHeaders = null,
        string BodyFile = null,
        bool Unconditional = false,
        bool Verify = false,
        bool AllowUnsafeMethods = false,
        bool AllowOriginChange = false,
        string Sequence = null,
        ReplayHttpVersion HttpVersion = ReplayHttpVersion.Auto,
        ReplayConnectTo ConnectTo = null,
        bool AllowConnectTo = false,
        bool VerifyDiff = false,
        string RequestFile = null,
        bool ClearHeaders = false,
        bool EmptyBody = false,
        bool HttpVersionSet = false,
        bool IncludeSensitive = false,
        IReadOnlyList<CliFormPart> Form = null,
        bool SaveSaz = false)
    {
        internal static CliReplay Default { get; } = new();
    }

    // A part of a multipart form, as --form, --form-string and --form-type give it: a text part has its Value, a file part
    // the Path that --form NAME=@PATH named, read when the run starts; MediaType is null unless --form-type gave one.
    internal sealed record CliFormPart(string Name, string Value, string Path, string MediaType)
    {
        internal bool IsFile => Path != null;

        // The name that a file part is sent with: the file's own name, never its folder.
        internal string FileName => IsFile ? System.IO.Path.GetFileName(Path) : null;
    }

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

        internal const string RequestOption = "--request";
        internal const string UrlOption = "--url";
        internal const string OriginOption = "--origin";
        internal const string HeaderOption = "--header";
        internal const string RemoveHeaderOption = "--remove-header";
        internal const string BodyFileOption = "--body-file";
        internal const string SequenceOption = "--sequence";
        internal const string HttpVersionOption = "--http-version";
        internal const string ConnectToOption = "--connect-to";
        internal const string UnconditionalSwitch = "--unconditional";
        internal const string VerifySwitch = "--verify";
        internal const string VerifyDiffSwitch = "--verify-diff";
        internal const string AllowUnsafeMethodsSwitch = "--allow-unsafe-methods";
        internal const string AllowOriginChangeSwitch = "--allow-origin-change";
        internal const string AllowConnectToSwitch = "--allow-connect-to";
        internal const string RequestFileOption = "--request-file";
        internal const string ClearHeadersSwitch = "--clear-headers";
        internal const string EmptyBodySwitch = "--empty-body";
        internal const string IncludeSensitiveSwitch = "--include-sensitive";
        internal const string FormOption = "--form";
        internal const string FormStringOption = "--form-string";
        internal const string FormTypeOption = "--form-type";
        internal const string SaveSazSwitch = "--save-saz";

        internal const string HttpVersionMessage = "--http-version takes auto, 1.1 or 2.";

        internal const string ConnectToMessage =
            "--connect-to takes an IP address and a port, as IPv4:PORT or [IPv6]:PORT, with no host name; the port is " +
                "1 to 65535.";

        // The whole-number options of replay and compose, with their defaults and maximums (ReplayOptions' limits).
        internal static readonly CliLimitOption RepeatOption = new("--repeat", string.Empty, 1, ReplayOptions.MaxRepeat);
        internal static readonly CliLimitOption ParallelOption = new("--parallel", string.Empty, 1, ReplayOptions.MaxParallelism);
        internal static readonly CliLimitOption TimeoutOption = new("--timeout", " of seconds", 30, 600);
        internal static readonly CliLimitOption MaxResponseOption = new("--max-response-mib", " of MiB", 16, 512);
        internal static readonly CliLimitOption FollowRedirectsOption =
            new("--follow-redirects", string.Empty, 0, ReplayOptions.MaxRedirectLimit);

        internal const string FollowRedirectsRangeMessage = "--follow-redirects takes a whole number from 0 to 10.";

        internal const string SequenceRangeMessage =
            "--sequence takes START-END: whole numbers of 1 to 15 digits, START no greater than END, " +
            "at most 10,000 numbers.";

        internal const string SequenceTemplateMessage =
            "--sequence needs a # in the path or query of --url, and none in its scheme, host or port.";

        internal const string SequenceNeededMessage =
            "--url cannot have a fragment; to send one request per number, put # in its path or query and " +
            "add --sequence.";

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

        // Options with a value that compose and replay share; --header and --remove-header may repeat.
        private static readonly string[] SendOptions =
        {
            OutOption,
            MethodOption,
            HeaderOption,
            BodyFileOption,
            RepeatOption.Name,
            ParallelOption.Name,
            TimeoutOption.Name,
            MaxResponseOption.Name,
            FollowRedirectsOption.Name,
            HttpVersionOption,
            ConnectToOption,
            FormOption,
            FormStringOption,
            FormTypeOption,
        };

        private static readonly HashSet<string> ComposeCommandOptions = new(
            SendOptions.Concat(new[] { UrlOption, SequenceOption, RequestFileOption, RemoveHeaderOption }), StringComparer.Ordinal);

        private static readonly HashSet<string> ReplayCommandOptions = new(
            SendOptions.Concat(new[]
            {
                RequestOption,
                DeviceOption,
                UrlOption,
                OriginOption,
                RemoveHeaderOption,
                CliLimits.InputOption,
                CliLimits.DataOption,
                CliLimits.RecordOption,
                CliLimits.RecordsOption,
            }),
            StringComparer.Ordinal);

        private static readonly HashSet<string> RepeatableOptions = new(StringComparer.Ordinal)
        {
            HeaderOption, RemoveHeaderOption, FormOption, FormStringOption, FormTypeOption,
        };

        // compose has no captured origin to change, and replay never follows a redirect to another origin, so only replay
        // takes --allow-origin-change, and compose only with --request-file, whose URL is the origin. Both take
        // --allow-connect-to, the permission that --connect-to needs, and --save-saz, which also saves the sends as a Fiddler
        // archive. Only replay has captured responses to verify against (--verify, --verify-diff).
        private static readonly HashSet<string> ComposeSwitches = new(StringComparer.Ordinal)
        {
            AllowUnsafeMethodsSwitch,
            AllowConnectToSwitch,
            SaveSazSwitch,
        };

        // What compose takes only with --request-file: the completions of what a file left out, and consent for changing
        // the origin of the file's URL.
        private static readonly string[] RequestFileOnly =
        {
            RemoveHeaderOption, ClearHeadersSwitch, EmptyBodySwitch, AllowOriginChangeSwitch,
        };

        private static readonly HashSet<string> ComposeCommandSwitches = new(
            ComposeSwitches.Concat(new[] { AllowOriginChangeSwitch, ClearHeadersSwitch, EmptyBodySwitch }),
            StringComparer.Ordinal);

        private static readonly HashSet<string> ReplaySwitches = new(
            ComposeSwitches.Concat(new[] { AllowOriginChangeSwitch, UnconditionalSwitch, VerifySwitch, VerifyDiffSwitch }),
            StringComparer.Ordinal);

        // save-request never sends: it takes edits, completions, --http-version and the content and consent switches, and
        // no other transport option.
        private static readonly HashSet<string> SaveRequestOptions = new(StringComparer.Ordinal)
        {
            OutOption,
            RequestOption,
            DeviceOption,
            RequestFileOption,
            UrlOption,
            MethodOption,
            HeaderOption,
            RemoveHeaderOption,
            BodyFileOption,
            HttpVersionOption,
            FormOption,
            FormStringOption,
            FormTypeOption,
            CliLimits.InputOption,
            CliLimits.DataOption,
            CliLimits.RecordOption,
            CliLimits.RecordsOption,
        };

        private static readonly HashSet<string> SaveRequestSwitches = new(StringComparer.Ordinal)
        {
            UnconditionalSwitch, ClearHeadersSwitch, EmptyBodySwitch, IncludeSensitiveSwitch, AllowOriginChangeSwitch,
        };

        // curl takes what save-request takes. It reads the options that only a send uses so that it can refuse them with
        // one message: a script is made with curl's default transport, never one that leaves a choice out.
        private static readonly HashSet<string> CurlSendOnlyOptions = new(StringComparer.Ordinal)
        {
            RepeatOption.Name,
            ParallelOption.Name,
            TimeoutOption.Name,
            MaxResponseOption.Name,
            FollowRedirectsOption.Name,
            ConnectToOption,
            SequenceOption,
            OriginOption,
        };

        private static readonly HashSet<string> CurlSendOnlySwitches = new(StringComparer.Ordinal)
        {
            AllowConnectToSwitch, AllowUnsafeMethodsSwitch, VerifySwitch, VerifyDiffSwitch,
        };

        private static readonly HashSet<string> CurlReadOptions = new(SaveRequestOptions.Concat(CurlSendOnlyOptions), StringComparer.Ordinal);

        private static readonly HashSet<string> CurlReadSwitches = new(SaveRequestSwitches.Concat(CurlSendOnlySwitches), StringComparer.Ordinal);

        private static readonly HashSet<string> CurlReadNames = new(CurlReadOptions.Concat(CurlReadSwitches), StringComparer.Ordinal);

        internal const string CurlSendOnlyMessage =
            "xmat curl exports each request with curl's default transport and sends nothing: --repeat, --parallel, " +
            "--timeout, --max-response-mib, --follow-redirects, --connect-to, --allow-connect-to, --sequence, " +
            "--allow-unsafe-methods, --verify, --verify-diff and --origin do not apply.";

        internal const string CurlHttpVersionMessage =
            "xmat curl takes --http-version auto or 1.1: curl's HTTP/2-only mode is not known to send the same request.";

        // The options that analyze accepts; its command line in the capability registry names exactly these.
        internal static IReadOnlySet<string> AnalyzeOptionNames => AnalyzeOptions;

        // The options that export accepts; the registry's export command lines name exactly these.
        internal static IReadOnlySet<string> ExportOptionNames => ExportOptions;

        // The options and switches that replay, compose and save-request accept; their registry command lines name exactly
        // these.
        internal static IReadOnlySet<string> ReplayOptionNames { get; } = new HashSet<string>(ReplayCommandOptions.Concat(ReplaySwitches), StringComparer.Ordinal);

        internal static IReadOnlySet<string> ComposeOptionNames { get; } = new HashSet<string>(ComposeCommandOptions.Concat(ComposeCommandSwitches), StringComparer.Ordinal);

        internal static IReadOnlySet<string> SaveRequestOptionNames { get; } = new HashSet<string>(SaveRequestOptions.Concat(SaveRequestSwitches), StringComparer.Ordinal);

        // curl accepts exactly what save-request accepts; the options that only a send uses are refused (CurlSendOnlyMessage).
        internal static IReadOnlySet<string> CurlOptionNames => SaveRequestOptionNames;

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

            if (first == CliCommands.Replay)
            {
                return ParseReplay(args);
            }

            if (first == CliCommands.Compose)
            {
                return ParseCompose(args);
            }

            if (first == CliCommands.SaveRequest)
            {
                return ParseExport(args, CliCommands.SaveRequest);
            }

            if (first == CliCommands.Curl)
            {
                return ParseExport(args, CliCommands.Curl);
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

            error = ReadTarget(
                arguments, command, "analyze needs the INPUT capture file.", "analyze takes a single INPUT capture file.",
                "analyze needs --out DIR.", out string output, out string deviceId);
            if (error != null)
            {
                return error;
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
                processFilter = ReadProcessSelectors(arguments, command, out error);
                if (error != null)
                {
                    return error;
                }
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

            error = ReadTarget(
                arguments, command, "export takes a single INPUT capture file.", "export takes a single INPUT capture file.",
                "export needs --out FILE.", out string output, out string deviceId);
            if (error != null)
            {
                return error;
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

            ProcessFilterSpec process = ReadProcessSelectors(arguments, command, out error);
            if (error != null)
            {
                return error;
            }

            OperationResult<CaptureSelectionSpec> selection = CaptureSelectionValidator.Validate(new CaptureSelectionSpec(
                process,
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

        // The checks that analyze and export share, in this order: a single INPUT, --out, then a valid --device. The
        // messages are the command's own; its other checks follow.
        private static CliParseResult ReadTarget(
            CliArguments arguments, string command, string noInputMessage, string extraInputMessage, string noOutputMessage,
            out string output, out string deviceId)
        {
            deviceId = null;
            if (arguments.Positionals.Count != 1)
            {
                output = null;
                return arguments.Positionals.Count == 0
                    ? CliParseResult.UsageError(command, "cli.usage.missing-argument", noInputMessage)
                    : CliParseResult.UsageError(command, "cli.usage.unexpected-argument", extraInputMessage);
            }

            if (!arguments.Options.TryGetValue(OutOption, out output))
            {
                return CliParseResult.UsageError(command, "cli.usage.missing-option", noOutputMessage);
            }

            return arguments.Options.TryGetValue(DeviceOption, out deviceId) && !IsDeviceId(deviceId)
                ? CliParseResult.UsageError(command, "cli.usage.invalid-value", DeviceMessage)
                : null;
        }

        // The --process and --exclude-process selectors; null and the usage error when one is invalid.
        private static ProcessFilterSpec ReadProcessSelectors(
            CliArguments arguments, string command, out CliParseResult error)
        {
            OperationResult<ProcessFilterSpec> selectors = ProcessFilter.Parse(
                arguments.ValuesOf(ProcessOption),
                arguments.ValuesOf(ExcludeProcessOption));
            error = !selectors.Succeeded || selectors.Value == null
                ? CliParseResult.UsageError(command, "cli.usage.invalid-value", InvalidProcessSelectorMessage)
                : null;
            return error == null ? selectors.Value : null;
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

        private static CliParseResult ParseReplay(IReadOnlyList<string> args)
        {
            const string command = CliCommands.Replay;
            var arguments = new CliArguments();
            CliParseResult error = arguments.Read(args, command, ReplayOptionNames, RepeatableOptions, ReplaySwitches);
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
                return CliParseResult.UsageError(command, "cli.usage.missing-argument", "replay needs the INPUT capture file.");
            }

            if (arguments.Positionals.Count > 1)
            {
                return CliParseResult.UsageError(command, "cli.usage.unexpected-argument", "replay takes a single INPUT capture file.");
            }

            if (!arguments.Options.TryGetValue(OutOption, out string output))
            {
                return CliParseResult.UsageError(command, "cli.usage.missing-option", "replay needs --out DIR.");
            }

            if (!arguments.Options.TryGetValue(RequestOption, out string selection))
            {
                return CliParseResult.UsageError(
                    command, "cli.usage.missing-option", "replay needs --request with all, or request numbers and ranges.");
            }

            if (!ReplaySelection.TryParse(selection, out _))
            {
                return CliParseResult.UsageError(
                    command,
                    "cli.usage.invalid-value",
                    "--request takes all, or request numbers and ranges such as 1,5,9-12: at most 10,000 numbers in all.");
            }

            if (arguments.Options.TryGetValue(DeviceOption, out string deviceId) && !IsDeviceId(deviceId))
            {
                return CliParseResult.UsageError(
                    command, "cli.usage.invalid-value", "--device takes a device id such as d1 from a previous summary.");
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

            if (arguments.Options.ContainsKey(UrlOption) && arguments.Options.ContainsKey(OriginOption))
            {
                return CliParseResult.UsageError(
                    command, "cli.usage.conflicting-options", "--url and --origin cannot be used together.");
            }

            error = ReadSendOptions(command, arguments, out CliReplay replay);
            if (error != null)
            {
                return error;
            }

            if (arguments.Options.TryGetValue(OriginOption, out string origin) && !ReplayOperation.TryParseOrigin(origin, out _))
            {
                return CliParseResult.UsageError(
                    command,
                    "cli.usage.invalid-value",
                    "--origin takes the scheme, host and port of an http or https URL, such as https://example.test:8443, " +
                        "without a path, query or credentials.");
            }

            error = ReadRemoveHeaders(command, arguments, out List<string> removeHeaders);
            if (error != null)
            {
                return error;
            }

            error = ReadForm(command, arguments, replay.Headers, removeHeaders, out List<CliFormPart> form);
            if (error != null)
            {
                return error;
            }

            return CliParseResult.Success(new CliRequest(
                command,
                Input: arguments.Positionals[0],
                Output: output,
                DeviceId: deviceId,
                Limits: limits,
                Replay: replay with
                {
                    Selection = selection,
                    Origin = origin,
                    RemoveHeaders = removeHeaders,
                    Unconditional = arguments.Options.ContainsKey(UnconditionalSwitch),
                    Form = form,

                    // --verify-diff verifies too, and also writes how each verified response differs.
                    Verify = arguments.Options.ContainsKey(VerifySwitch) || arguments.Options.ContainsKey(VerifyDiffSwitch),
                    VerifyDiff = arguments.Options.ContainsKey(VerifyDiffSwitch),
                }));
        }

        private static CliParseResult ParseCompose(IReadOnlyList<string> args)
        {
            const string command = CliCommands.Compose;
            var arguments = new CliArguments();
            CliParseResult error = arguments.Read(args, command, ComposeOptionNames, RepeatableOptions, ComposeCommandSwitches);
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
                return CliParseResult.UsageError(
                    command, "cli.usage.unexpected-argument", "compose takes no arguments besides its options.");
            }

            // --url is the request to compose, or with --request-file an edit of the file's request.
            bool fromFile = arguments.Options.TryGetValue(RequestFileOption, out string requestFile);
            if (!fromFile && !arguments.Options.ContainsKey(UrlOption))
            {
                return CliParseResult.UsageError(
                    command, "cli.usage.missing-option", "compose needs --url URL or --request-file FILE.");
            }

            if (!fromFile && RequestFileOnly.Any(name => Has(arguments, name)))
            {
                return CliParseResult.UsageError(
                    command,
                    "cli.usage.missing-option",
                    "--remove-header, --clear-headers, --empty-body and --allow-origin-change need --request-file.");
            }

            error = CheckBodyChoice(command, arguments);
            if (error != null)
            {
                return error;
            }

            if (!arguments.Options.TryGetValue(OutOption, out string output))
            {
                return CliParseResult.UsageError(command, "cli.usage.missing-option", "compose needs --out DIR.");
            }

            error = ReadSendOptions(command, arguments, out CliReplay replay);
            if (error != null)
            {
                return error;
            }

            error = ReadRemoveHeaders(command, arguments, out List<string> removeHeaders);
            if (error != null)
            {
                return error;
            }

            error = ReadForm(command, arguments, replay.Headers, removeHeaders, out List<CliFormPart> form);
            return error ?? CliParseResult.Success(new CliRequest(
                command,
                Output: output,
                Replay: replay with
                {
                    RequestFile = requestFile,
                    RemoveHeaders = removeHeaders,
                    ClearHeaders = arguments.Options.ContainsKey(ClearHeadersSwitch),
                    EmptyBody = arguments.Options.ContainsKey(EmptyBodySwitch),
                    Form = form,
                }));
        }

        // xmat save-request and xmat curl: one source (an INPUT capture file with --request, a request file, or --url for
        // a new request), the edits and completions that compose takes, and whether to export captured content. Nothing is
        // sent, so they take no transport option but --http-version: curl refuses the options that only a send uses, and
        // HTTP/2 only. curl always exports captured content, so its missing consent is a policy refusal (exit code 3,
        // CliCurlCommand), not a usage error.
        private static CliParseResult ParseExport(IReadOnlyList<string> args, string command)
        {
            bool curl = command == CliCommands.Curl;
            var arguments = new CliArguments();
            CliParseResult error = arguments.Read(
                args,
                command,
                curl ? CurlReadNames : SaveRequestOptionNames,
                RepeatableOptions,
                curl ? CurlReadSwitches : SaveRequestSwitches);
            if (error != null)
            {
                return error;
            }

            if (arguments.HelpRequested)
            {
                return HelpFor(command);
            }

            if (curl && (CurlSendOnlyOptions.Any(name => Has(arguments, name)) || CurlSendOnlySwitches.Any(name => Has(arguments, name))))
            {
                return CliParseResult.UsageError(command, "cli.usage.unsupported-option", CurlSendOnlyMessage);
            }

            if (arguments.Positionals.Count > 1)
            {
                return CliParseResult.UsageError(
                    command, "cli.usage.unexpected-argument", command + " takes at most one INPUT capture file.");
            }

            string input = arguments.Positionals.Count == 1 ? arguments.Positionals[0] : null;
            bool fromFile = arguments.Options.TryGetValue(RequestFileOption, out string requestFile);
            arguments.Options.TryGetValue(RequestOption, out string selection);
            bool captureOptions = selection != null || arguments.Options.ContainsKey(DeviceOption) ||
                arguments.Options.ContainsKey(CliLimits.InputOption) || arguments.Options.ContainsKey(CliLimits.DataOption) ||
                arguments.Options.ContainsKey(CliLimits.RecordOption) || arguments.Options.ContainsKey(CliLimits.RecordsOption);
            if (input != null && fromFile)
            {
                return CliParseResult.UsageError(
                    command,
                    "cli.usage.conflicting-options",
                    command + " takes one source: an INPUT capture file, --request-file, or --url for a new request.");
            }

            if (input != null && selection == null)
            {
                return CliParseResult.UsageError(
                    command, "cli.usage.missing-option", command + " needs --request with an INPUT capture file.");
            }

            if (input == null && captureOptions)
            {
                return CliParseResult.UsageError(
                    command,
                    "cli.usage.missing-argument",
                    "--request, --device and the input limits need an INPUT capture file.");
            }

            if (input == null && !fromFile && !arguments.Options.ContainsKey(UrlOption))
            {
                return CliParseResult.UsageError(
                    command,
                    "cli.usage.missing-argument",
                    command + " needs an INPUT capture file with --request, --request-file FILE, or --url URL.");
            }

            if (!fromFile && (Has(arguments, ClearHeadersSwitch) || Has(arguments, EmptyBodySwitch)))
            {
                return CliParseResult.UsageError(
                    command, "cli.usage.missing-option", "--clear-headers and --empty-body need --request-file.");
            }

            if (!curl && Has(arguments, AllowOriginChangeSwitch) && !Has(arguments, IncludeSensitiveSwitch))
            {
                return CliParseResult.UsageError(
                    command,
                    "cli.usage.missing-option",
                    "--allow-origin-change applies only with --include-sensitive: a template keeps no URL.");
            }

            error = CheckBodyChoice(command, arguments);
            if (error != null)
            {
                return error;
            }

            if (!arguments.Options.TryGetValue(OutOption, out string output))
            {
                return CliParseResult.UsageError(command, "cli.usage.missing-option", command + " needs --out DIR.");
            }

            if (selection != null && !ReplaySelection.TryParse(selection, out _))
            {
                return CliParseResult.UsageError(
                    command,
                    "cli.usage.invalid-value",
                    "--request takes all, or request numbers and ranges such as 1,5,9-12: at most 10,000 numbers in all.");
            }

            if (arguments.Options.TryGetValue(DeviceOption, out string deviceId) && !IsDeviceId(deviceId))
            {
                return CliParseResult.UsageError(
                    command, "cli.usage.invalid-value", "--device takes a device id such as d1 from a previous summary.");
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

            // A saved URL may be a sequence template, which compose --sequence expands later.
            if (arguments.Options.TryGetValue(UrlOption, out string url) && !RequestReplayDraft.IsValidRequestFileUrl(url))
            {
                return CliParseResult.UsageError(
                    command,
                    "cli.usage.invalid-value",
                    "--url takes an absolute http or https URL without credentials or a fragment; a # only in its path or " +
                        "query.");
            }

            if (arguments.Options.TryGetValue(MethodOption, out string method) && !RequestReplayDraft.IsValidMethod(method))
            {
                return CliParseResult.UsageError(
                    command, "cli.usage.invalid-value", "--method takes an HTTP method such as GET or POST, other than CONNECT.");
            }

            var headers = new List<KeyValuePair<string, string>>();
            foreach (string text in arguments.ValuesOf(HeaderOption))
            {
                if (!TryParseStoredHeader(text, out KeyValuePair<string, string> header))
                {
                    return CliParseResult.UsageError(
                        command,
                        "cli.usage.invalid-value",
                        "--header takes NAME: VALUE, with a valid header name and a value without control characters " +
                            "other than tabs.");
                }

                headers.Add(header);
            }

            ReplayHttpVersion httpVersion = ReplayHttpVersion.Auto;
            if (arguments.Options.TryGetValue(HttpVersionOption, out string versionText) &&
                !ReplayTransportOptions.TryParseVersion(versionText, out httpVersion))
            {
                return CliParseResult.UsageError(command, "cli.usage.invalid-value", HttpVersionMessage);
            }

            if (curl && httpVersion == ReplayHttpVersion.Http2)
            {
                return CliParseResult.UsageError(command, "cli.usage.invalid-value", CurlHttpVersionMessage);
            }

            error = ReadRemoveHeaders(command, arguments, out List<string> removeHeaders);
            if (error != null)
            {
                return error;
            }

            error = ReadForm(command, arguments, headers, removeHeaders, out List<CliFormPart> form);
            if (error != null)
            {
                return error;
            }

            arguments.Options.TryGetValue(BodyFileOption, out string bodyFile);
            return CliParseResult.Success(new CliRequest(
                command,
                Input: input,
                Output: output,
                DeviceId: deviceId,
                Limits: input != null ? limits : null,
                Replay: new CliReplay(
                    Selection: selection,
                    Url: url,
                    Method: method,
                    Headers: headers,
                    RemoveHeaders: removeHeaders,
                    BodyFile: bodyFile,
                    Unconditional: Has(arguments, UnconditionalSwitch),
                    AllowOriginChange: Has(arguments, AllowOriginChangeSwitch),
                    HttpVersion: httpVersion,
                    RequestFile: requestFile,
                    ClearHeaders: Has(arguments, ClearHeadersSwitch),
                    EmptyBody: Has(arguments, EmptyBodySwitch),
                    HttpVersionSet: arguments.Options.ContainsKey(HttpVersionOption),
                    IncludeSensitive: Has(arguments, IncludeSensitiveSwitch),
                    Form: form)));
        }

        // Whether the command line gave an option, a repeated option or a switch.
        private static bool Has(CliArguments arguments, string name) =>
            arguments.Options.ContainsKey(name) || arguments.ValuesOf(name).Any();

        // --body-file and --empty-body each give the body; only one can.
        private static CliParseResult CheckBodyChoice(string command, CliArguments arguments) =>
            Has(arguments, BodyFileOption) && Has(arguments, EmptyBodySwitch)
                ? CliParseResult.UsageError(
                    command, "cli.usage.conflicting-options", "--body-file and --empty-body cannot be used together.")
                : null;

        internal const string FormSyntaxMessage =
            "--form takes NAME=VALUE or NAME=@FILE, and --form-string takes NAME=VALUE; NAME is 1 to 256 printable ASCII " +
                "characters.";

        internal const string FormFileMessage =
            "--form NAME=@FILE needs a FILE whose own name is 1 to 255 characters of well-formed Unicode without control " +
                "characters.";

        internal const string FormTypeMessage =
            "--form-type takes N=TYPE: a part number from 1 to the number of --form and --form-string parts, given once, " +
                "and a media type such as text/plain.";

        // The multipart form of --form, --form-string and --form-type, in command-line order; null when there is none. Only
        // the syntax, names, file names and types are checked here, and nothing is read: the files and the size are the
        // run's (CliRequestBody). A form makes the body and its Content-Type, so it takes no other body or Content-Type edit.
        private static CliParseResult ReadForm(
            string command,
            CliArguments arguments,
            IReadOnlyList<KeyValuePair<string, string>> headers,
            IReadOnlyList<string> removeHeaders,
            out List<CliFormPart> form)
        {
            form = null;
            var parts = new List<CliFormPart>();
            foreach (KeyValuePair<string, string> option in arguments.OrderedValues(FormOption, FormStringOption))
            {
                int separator = option.Value.IndexOf('=');
                string name = separator < 0 ? null : option.Value[..separator];
                if (!MultipartForm.IsValidName(name))
                {
                    return CliParseResult.UsageError(command, "cli.usage.invalid-value", FormSyntaxMessage);
                }

                string value = option.Value[(separator + 1)..];
                if (option.Key == FormOption && value.StartsWith('@'))
                {
                    string path = value[1..];
                    if (path.Length == 0 || !MultipartForm.IsValidFileName(System.IO.Path.GetFileName(path)))
                    {
                        return CliParseResult.UsageError(command, "cli.usage.invalid-value", FormFileMessage);
                    }

                    parts.Add(new CliFormPart(name, null, path, null));
                }
                else
                {
                    parts.Add(new CliFormPart(name, value, null, null));
                }
            }

            IReadOnlyList<string> types = arguments.ValuesOf(FormTypeOption);
            if (parts.Count == 0)
            {
                return types.Count == 0
                    ? null
                    : CliParseResult.UsageError(
                        command, "cli.usage.missing-option", "--form-type needs --form or --form-string parts to apply to.");
            }

            if (parts.Count > MultipartForm.MaxParts)
            {
                return CliParseResult.UsageError(
                    command, "cli.usage.invalid-value", "A form can have at most 128 parts, --form and --form-string together.");
            }

            foreach (string text in types)
            {
                int separator = text.IndexOf('=');
                if (separator <= 0 ||
                    !int.TryParse(text.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out int number) ||
                    number < 1 || number > parts.Count || parts[number - 1].MediaType != null ||
                    !MultipartForm.IsValidMediaType(text[(separator + 1)..]))
                {
                    return CliParseResult.UsageError(command, "cli.usage.invalid-value", FormTypeMessage);
                }

                parts[number - 1] = parts[number - 1] with { MediaType = text[(separator + 1)..] };
            }

            if (Has(arguments, BodyFileOption) || Has(arguments, EmptyBodySwitch))
            {
                return CliParseResult.UsageError(
                    command,
                    "cli.usage.conflicting-options",
                    "--form and --form-string make the body, so they cannot be used with --body-file or --empty-body.");
            }

            if ((headers ?? Array.Empty<KeyValuePair<string, string>>()).Any(header => IsContentType(header.Key)) ||
                (removeHeaders ?? Array.Empty<string>()).Any(IsContentType))
            {
                return CliParseResult.UsageError(
                    command,
                    "cli.usage.conflicting-options",
                    "A form makes its own Content-Type, so --header Content-Type and --remove-header Content-Type cannot be " +
                        "used with --form or --form-string.");
            }

            form = parts;
            return null;
        }

        private static bool IsContentType(string name) => string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase);

        private static CliParseResult ReadRemoveHeaders(string command, CliArguments arguments, out List<string> removeHeaders)
        {
            removeHeaders = new List<string>();
            foreach (string name in arguments.ValuesOf(RemoveHeaderOption))
            {
                if (!RequestReplayDraft.IsValidHeader(name, string.Empty))
                {
                    return CliParseResult.UsageError(command, "cli.usage.invalid-value", "--remove-header takes a header name.");
                }

                removeHeaders.Add(name);
            }

            return null;
        }

        // NAME: VALUE for a request file, which holds any valid header line: unlike a header to send, its value may be
        // Unicode, and it may be one that sending sets itself, such as Host.
        private static bool TryParseStoredHeader(string text, out KeyValuePair<string, string> header)
        {
            header = default;
            int separator = text.IndexOf(':');
            if (separator <= 0)
            {
                return false;
            }

            string name = text[..separator];
            string value = text[(separator + 1)..].Trim(' ', '\t');
            if (!RequestReplayDraft.IsValidHeader(name, value))
            {
                return false;
            }

            header = new(name, value);
            return true;
        }

        // The options that compose and replay share. Messages name the option and what it takes, never the value.
        private static CliParseResult ReadSendOptions(string command, CliArguments arguments, out CliReplay replay)
        {
            replay = null;
            CliLimitOption[] numbers = { RepeatOption, ParallelOption, TimeoutOption, MaxResponseOption, FollowRedirectsOption };
            var values = new int[numbers.Length];
            for (int index = 0; index < numbers.Length; index++)
            {
                // Only --follow-redirects can be 0, its default: follow no redirect.
                bool zeroAllowed = numbers[index] == FollowRedirectsOption;
                if (!arguments.Options.TryGetValue(numbers[index].Name, out string text))
                {
                    values[index] = numbers[index].Default;
                }
                else if (zeroAllowed && text == "0")
                {
                    values[index] = 0;
                }
                else if (!CliLimits.TryParse(text, numbers[index].Maximum, out values[index]))
                {
                    return CliParseResult.UsageError(
                        command,
                        "cli.usage.invalid-value",
                        zeroAllowed ? FollowRedirectsRangeMessage : numbers[index].RangeMessage);
                }
            }

            // Every send in flight may hold a whole response: their total is bounded (ReplayOptions.MaxInFlightResponseBytes).
            if ((long)values[1] * values[3] * AnalysisLimits.BytesPerMib > ReplayOptions.MaxInFlightResponseBytes)
            {
                return CliParseResult.UsageError(
                    command, "cli.usage.invalid-value", "--parallel times --max-response-mib must not exceed 1,024 MiB.");
            }

            // With --sequence, --url is a template: its first number must give a valid URL, and the operation checks every
            // number before anything is sent.
            arguments.Options.TryGetValue(UrlOption, out string url);
            ReplaySequence sequence = null;
            if (arguments.Options.TryGetValue(SequenceOption, out string sequenceText))
            {
                if (!ReplaySequence.TryParse(sequenceText, out sequence))
                {
                    return CliParseResult.UsageError(command, "cli.usage.invalid-value", SequenceRangeMessage);
                }

                if (url != null && !ReplaySequence.IsPathAndQueryTemplate(url))
                {
                    return CliParseResult.UsageError(command, "cli.usage.invalid-value", SequenceTemplateMessage);
                }
            }

            if (url != null && !IsHttpUrl(sequence == null ? url : sequence.Expand(url, sequence.Start)))
            {
                return CliParseResult.UsageError(
                    command,
                    "cli.usage.invalid-value",
                    command == CliCommands.Compose && url.Contains(ReplaySequence.Placeholder)
                        ? SequenceNeededMessage
                        : "--url takes an absolute http or https URL without credentials or a fragment.");
            }

            if (arguments.Options.TryGetValue(MethodOption, out string method) && !RequestReplayDraft.IsValidMethod(method))
            {
                return CliParseResult.UsageError(
                    command, "cli.usage.invalid-value", "--method takes an HTTP method such as GET or POST, other than CONNECT.");
            }

            var headers = new List<KeyValuePair<string, string>>();
            foreach (string text in arguments.ValuesOf(HeaderOption))
            {
                if (!TryParseHeader(text, out KeyValuePair<string, string> header))
                {
                    return CliParseResult.UsageError(
                        command,
                        "cli.usage.invalid-value",
                        "--header takes NAME: VALUE, with a valid header name and a value of printable ASCII or " +
                            "tabs; it cannot set Host, Content-Length or connection headers.");
                }

                headers.Add(header);
            }

            ReplayHttpVersion httpVersion = ReplayHttpVersion.Auto;
            if (arguments.Options.TryGetValue(HttpVersionOption, out string versionText) &&
                !ReplayTransportOptions.TryParseVersion(versionText, out httpVersion))
            {
                return CliParseResult.UsageError(command, "cli.usage.invalid-value", HttpVersionMessage);
            }

            ReplayConnectTo connectTo = null;
            if (arguments.Options.TryGetValue(ConnectToOption, out string connectToText) &&
                !ReplayConnectTo.TryParse(connectToText, out connectTo))
            {
                return CliParseResult.UsageError(command, "cli.usage.invalid-value", ConnectToMessage);
            }

            arguments.Options.TryGetValue(BodyFileOption, out string bodyFile);
            replay = new CliReplay(
                Repeat: values[0],
                Parallel: values[1],
                TimeoutSeconds: values[2],
                MaxResponseMib: values[3],
                FollowRedirects: values[4],
                Url: url,
                Method: method,
                Headers: headers,
                RemoveHeaders: Array.Empty<string>(),
                BodyFile: bodyFile,
                AllowUnsafeMethods: arguments.Options.ContainsKey(AllowUnsafeMethodsSwitch),
                AllowOriginChange: arguments.Options.ContainsKey(AllowOriginChangeSwitch),
                Sequence: sequenceText,
                HttpVersion: httpVersion,
                ConnectTo: connectTo,
                AllowConnectTo: arguments.Options.ContainsKey(AllowConnectToSwitch),
                HttpVersionSet: arguments.Options.ContainsKey(HttpVersionOption),
                SaveSaz: arguments.Options.ContainsKey(SaveSazSwitch));
            return null;
        }

        private static bool IsHttpUrl(string text) =>
            Uri.TryCreate(text, UriKind.Absolute, out Uri uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
            string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment);

        // NAME: VALUE. Host and Content-Length follow from the URL and the body, and connection headers belong to the
        // transport, so none of them can be set.
        private static bool TryParseHeader(string text, out KeyValuePair<string, string> header)
        {
            header = default;
            int separator = text.IndexOf(':');
            if (separator <= 0)
            {
                return false;
            }

            string name = text[..separator];
            string value = text[(separator + 1)..].Trim(' ', '\t');
            if (!RequestReplayDraft.IsValidHeader(name, value) ||
                !RequestReplayDraft.IsSendableHeaderValue(value) ||
                name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                ProxyHeaderUtilities.IsHopByHopHeader(name))
            {
                return false;
            }

            header = new(name, value);
            return true;
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
        // Every repeatable option's value, in command-line order across names.
        private readonly List<KeyValuePair<string, string>> _ordered = new();

        internal Dictionary<string, string> Options { get; } = new(StringComparer.Ordinal);

        // The values of the repeatable options that were given, in command-line order.
        internal Dictionary<string, List<string>> RepeatedOptions { get; } = new(StringComparer.Ordinal);

        internal List<string> Positionals { get; } = new();

        internal bool HelpRequested { get; private set; }

        internal IReadOnlyList<string> ValuesOf(string option) =>
            RepeatedOptions.TryGetValue(option, out List<string> values) ? values : Array.Empty<string>();

        // The values of these repeatable options, with their names, in the order given across all of them.
        internal IEnumerable<KeyValuePair<string, string>> OrderedValues(params string[] names) =>
            _ordered.Where(pair => Array.IndexOf(names, pair.Key) >= 0);

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
                    _ordered.Add(new(name, value));
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
