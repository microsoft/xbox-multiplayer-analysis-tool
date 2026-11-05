// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using XMAT.Operations;
using XMAT.Operations.Analysis;
using XMAT.Operations.Replay;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Cli
{
    internal static class CliCommands
    {
        internal const string Analyze = "analyze";
        internal const string Compose = "compose";
        internal const string Doctor = "doctor";
        internal const string Help = "help";
        internal const string Replay = "replay";
        internal const string Version = "version";

        // The commands xmat.exe runs. Each one needs a Supported capability in the registry (XMAT.Tests/Cli checks it).
        internal static IReadOnlyList<string> Implemented { get; } = new[] { Analyze, Compose, Doctor, Replay };
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
        CliReplay Replay = null);

    // What xmat replay and xmat compose send and how, as their command lines give it. Headers and RemoveHeaders keep
    // the order they were given in; BodyFile is read when the run starts. Selection is the --request text (replay only)
    // and Sequence the --sequence text (compose only), both already validated.
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
        bool VerifyDiff = false)
    {
        internal static CliReplay Default { get; } = new();
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
        internal const string ForOption = "--for";
        internal const string VersionSwitch = "--version";
        internal const string FindingsValue = "findings";

        internal const string RequestOption = "--request";
        internal const string UrlOption = "--url";
        internal const string OriginOption = "--origin";
        internal const string MethodOption = "--method";
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
        };

        private static readonly HashSet<string> ComposeCommandOptions = new(
            SendOptions.Concat(new[] { UrlOption, SequenceOption }), StringComparer.Ordinal);

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

        private static readonly HashSet<string> RepeatableOptions = new(StringComparer.Ordinal) { HeaderOption, RemoveHeaderOption };

        // compose has no captured origin to change, and replay never follows a redirect to another origin, so only replay
        // takes --allow-origin-change. Both take --allow-connect-to, the permission that --connect-to needs. Only replay has
        // captured responses to verify against (--verify, --verify-diff).
        private static readonly HashSet<string> ComposeSwitches = new(StringComparer.Ordinal)
        {
            AllowUnsafeMethodsSwitch,
            AllowConnectToSwitch,
        };

        private static readonly HashSet<string> ReplaySwitches = new(
            ComposeSwitches.Concat(new[] { AllowOriginChangeSwitch, UnconditionalSwitch, VerifySwitch, VerifyDiffSwitch }),
            StringComparer.Ordinal);

        // The options that analyze accepts; its command line in the capability registry names exactly these.
        internal static IReadOnlySet<string> AnalyzeOptionNames => AnalyzeOptions;

        // The options and switches that replay and compose accept; their registry command lines name exactly these.
        internal static IReadOnlySet<string> ReplayOptionNames { get; } = new HashSet<string>(ReplayCommandOptions.Concat(ReplaySwitches), StringComparer.Ordinal);

        internal static IReadOnlySet<string> ComposeOptionNames { get; } = new HashSet<string>(ComposeCommandOptions.Concat(ComposeSwitches), StringComparer.Ordinal);

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

            if (first == CliCommands.Replay)
            {
                return ParseReplay(args);
            }

            if (first == CliCommands.Compose)
            {
                return ParseCompose(args);
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

        private static CliParseResult ParseReplay(IReadOnlyList<string> args)
        {
            const string command = CliCommands.Replay;
            var arguments = new CliArguments();
            CliParseResult error = arguments.Read(args, command, ReplayCommandOptions, RepeatableOptions, ReplaySwitches);
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

            var removeHeaders = new List<string>();
            foreach (string name in arguments.RepeatedValues(RemoveHeaderOption))
            {
                if (!RequestReplayDraft.IsValidHeader(name, string.Empty))
                {
                    return CliParseResult.UsageError(command, "cli.usage.invalid-value", "--remove-header takes a header name.");
                }

                removeHeaders.Add(name);
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
                    Unconditional = arguments.Switches.Contains(UnconditionalSwitch),

                    // --verify-diff verifies too, and also writes how each verified response differs.
                    Verify = arguments.Switches.Contains(VerifySwitch) || arguments.Switches.Contains(VerifyDiffSwitch),
                    VerifyDiff = arguments.Switches.Contains(VerifyDiffSwitch),
                }));
        }

        private static CliParseResult ParseCompose(IReadOnlyList<string> args)
        {
            const string command = CliCommands.Compose;
            var arguments = new CliArguments();
            CliParseResult error = arguments.Read(args, command, ComposeCommandOptions, RepeatableOptions, ComposeSwitches);
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

            if (!arguments.Options.ContainsKey(UrlOption))
            {
                return CliParseResult.UsageError(command, "cli.usage.missing-option", "compose needs --url URL.");
            }

            if (!arguments.Options.TryGetValue(OutOption, out string output))
            {
                return CliParseResult.UsageError(command, "cli.usage.missing-option", "compose needs --out DIR.");
            }

            error = ReadSendOptions(command, arguments, out CliReplay replay);
            return error ?? CliParseResult.Success(new CliRequest(command, Output: output, Replay: replay));
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

                if (!ReplaySequence.IsPathAndQueryTemplate(url))
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
            foreach (string text in arguments.RepeatedValues(HeaderOption))
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
                AllowUnsafeMethods: arguments.Switches.Contains(AllowUnsafeMethodsSwitch),
                AllowOriginChange: arguments.Switches.Contains(AllowOriginChangeSwitch),
                Sequence: sequenceText,
                HttpVersion: httpVersion,
                ConnectTo: connectTo,
                AllowConnectTo: arguments.Switches.Contains(AllowConnectToSwitch));
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

    // The options (--name VALUE or --name=VALUE, each at most once unless it is repeatable), switches (--name, at most
    // once, without a value), positional arguments and help switches that follow a command. "--" ends the options, so an
    // INPUT that starts with '-' can still be named.
    internal sealed class CliArguments
    {
        private readonly Dictionary<string, List<string>> _repeated = new(StringComparer.Ordinal);

        internal Dictionary<string, string> Options { get; } = new(StringComparer.Ordinal);

        internal HashSet<string> Switches { get; } = new(StringComparer.Ordinal);

        internal List<string> Positionals { get; } = new();

        internal bool HelpRequested { get; private set; }

        // The values of a repeatable option, in the order given.
        internal IReadOnlyList<string> RepeatedValues(string name) =>
            _repeated.TryGetValue(name, out List<string> values) ? values : Array.Empty<string>();

        // Returns null when the arguments are valid, otherwise the usage error. allowedOptions lists every option that
        // takes a value, repeatable ones included.
        internal CliParseResult Read(
            IReadOnlyList<string> args,
            string command,
            IReadOnlySet<string> allowedOptions,
            IReadOnlySet<string> repeatableOptions = null,
            IReadOnlySet<string> allowedSwitches = null)
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

                if (allowedSwitches != null && allowedSwitches.Contains(name))
                {
                    if (value != null)
                    {
                        return CliParseResult.UsageError(command, "cli.usage.invalid-value", $"The {name} switch takes no value.");
                    }

                    if (!Switches.Add(name))
                    {
                        return CliParseResult.UsageError(command, "cli.usage.duplicate-option", $"The {name} option was given more than once.");
                    }

                    continue;
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

                if (repeatableOptions != null && repeatableOptions.Contains(name))
                {
                    if (!_repeated.TryGetValue(name, out List<string> values))
                    {
                        values = new List<string>();
                        _repeated.Add(name, values);
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
