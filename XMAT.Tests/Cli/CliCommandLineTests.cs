// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using XMAT.Cli;
using XMAT.DataAbstractionLayer;
using XMAT.Operations;
using XMAT.Operations.CaptureFiles;
using XMAT.Operations.Capabilities;
using XMAT.Operations.Replay;

namespace XMAT.Tests.Cli
{
    // The parser, the registry-driven command catalog, help and the JSON documents, in process. Commands that configure
    // HeadlessStorage run in child-process tests because this test host already used PublicUtilities.
    public class CliCommandLineTests
    {
        [Theory]
        [InlineData("", "cli.usage.no-command")]
        [InlineData("frobnicate", "cli.usage.unknown-command")]
        [InlineData("--bogus", "cli.usage.unknown-option")]
        [InlineData("--version extra", "cli.usage.unexpected-argument")]
        [InlineData("help frobnicate", "cli.usage.unknown-command")]
        [InlineData("analyze", "cli.usage.missing-argument")]
        [InlineData("analyze a.saz b.saz --out results", "cli.usage.unexpected-argument")]
        [InlineData("analyze a.saz", "cli.usage.missing-option")]
        [InlineData("analyze a.saz --out", "cli.usage.missing-value")]
        [InlineData("analyze a.saz --out=", "cli.usage.missing-value")]
        [InlineData("analyze a.saz --out --device d1", "cli.usage.missing-value")]
        [InlineData("analyze a.saz --out results --out other", "cli.usage.duplicate-option")]
        [InlineData("analyze a.saz --out results -o", "cli.usage.unknown-option")]
        [InlineData("analyze a.saz --out results --fail-on warnings", "cli.usage.invalid-value")]
        [InlineData("analyze a.saz --out results --device 1", "cli.usage.invalid-value")]
        [InlineData("analyze a.saz --out results --device d0", "cli.usage.invalid-value")]
        [InlineData("doctor", "cli.usage.missing-option")]
        [InlineData("doctor analyze", "cli.usage.unexpected-argument")]
        [InlineData("doctor --for frobnicate", "cli.usage.unknown-target")]
        [InlineData("mark-capture-text", "cli.usage.missing-argument")]
        [InlineData("mark-capture-text a.xmatcap b.xmatcap --out results --interpret as-stored", "cli.usage.unexpected-argument")]
        [InlineData("mark-capture-text a.xmatcap --interpret as-stored", "cli.usage.missing-option")]
        [InlineData("mark-capture-text a.xmatcap --out results", "cli.usage.missing-option")]
        [InlineData("mark-capture-text a.xmatcap --out results --interpret repair", "cli.usage.invalid-value")]
        [InlineData("mark-capture-text a.xmatcap --out results --interpret as-stored --max-input-mib 0", "cli.usage.invalid-value")]
        [InlineData("mark-capture-text a.xmatcap --out results --interpret as-stored --max-input-mib 2049", "cli.usage.invalid-value")]
        [InlineData("mark-capture-text a.xmatcap --out results --interpret as-stored --accept-stored-text", "cli.usage.unknown-option")]
        [InlineData("--proxy-guard 42", "cli.usage.unknown-option")]
        [InlineData("analyze a.saz --out results --telemetry-sample", "cli.usage.unknown-option")]
        // Commands, options, values and doctor targets planned for later releases are usage errors until they ship.
        [InlineData("convert a.xmatcap --to saz --out results", "cli.usage.unknown-command")]
        [InlineData("help capture", "cli.usage.unknown-command")]
        [InlineData("analyze a.saz --out results --ruleset lta", "cli.usage.unknown-option")]
        [InlineData("analyze a.saz --out results --fail-on warning", "cli.usage.invalid-value")]
        [InlineData("doctor --for capture-xbox", "cli.usage.unknown-target")]
        [InlineData("replay", "cli.usage.missing-argument")]
        [InlineData("replay a.saz b.saz --request 1 --out results", "cli.usage.unexpected-argument")]
        [InlineData("replay a.saz --out results", "cli.usage.missing-option")]
        [InlineData("replay a.saz --request 1", "cli.usage.missing-option")]
        [InlineData("replay a.saz --request 0 --out results", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1-10001 --out results", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --request 2 --out results", "cli.usage.duplicate-option")]
        [InlineData("replay a.saz --request 1 --out results --device 1", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --repeat 0", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --repeat 1001", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --parallel 17", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --timeout 601", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --max-response-mib 513", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --follow-redirects 00", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --header X-Name:caf\u00e9", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --follow-redirects 11", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --max-records 0", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --url http://a.test/ --origin http://b.test", "cli.usage.conflicting-options")]
        [InlineData("replay a.saz --request 1 --out results --url ftp://a.test/", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --url http://user@a.test/", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --url /relative", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --origin http://a.test/path", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --method CONNECT", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --method GE(T", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --header NoSeparator", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --header Host:a.test", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --header content-length:1", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --header Transfer-Encoding:chunked", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --header Bad(Name):x", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --remove-header Bad:Name", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --verify=yes", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --verify --verify", "cli.usage.duplicate-option")]
        [InlineData("replay a.saz --request 1 --out results --verify-diff=yes", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --verify-diff --verify-diff", "cli.usage.duplicate-option")]
        [InlineData("compose --url http://a.test/ --out results --verify-diff", "cli.usage.unknown-option")]
        [InlineData("replay a.saz --request 1 --out results --fail-on findings", "cli.usage.unknown-option")]
        [InlineData("compose --out results", "cli.usage.missing-option")]
        [InlineData("compose --url http://a.test/", "cli.usage.missing-option")]
        [InlineData("compose extra --url http://a.test/ --out results", "cli.usage.unexpected-argument")]
        [InlineData("compose --url http://a.test/ --out results --verify", "cli.usage.unknown-option")]
        [InlineData("compose --url http://a.test/ --out results --unconditional", "cli.usage.unknown-option")]
        [InlineData("compose --url http://a.test/ --out results --request 1", "cli.usage.unknown-option")]
        [InlineData("compose --url http://a.test/ --out results --remove-header X", "cli.usage.missing-option")]
        [InlineData("compose --url http://a.test/ --out results --origin http://b.test", "cli.usage.unknown-option")]
        [InlineData("compose --url http://a.test/ --out results --allow-origin-change", "cli.usage.missing-option")]
        [InlineData("compose --url http://a.test/ --out results --clear-headers", "cli.usage.missing-option")]
        [InlineData("compose --url http://a.test/ --out results --empty-body", "cli.usage.missing-option")]
        [InlineData("compose --request-file r.json --out results --body-file b --empty-body", "cli.usage.conflicting-options")]
        [InlineData("compose --request-file r.json --out results --include-sensitive", "cli.usage.unknown-option")]
        [InlineData("save-request --out results", "cli.usage.missing-argument")]
        [InlineData("save-request a.saz --out results", "cli.usage.missing-option")]
        [InlineData("save-request a.saz --request 1 --request-file r.json --out results", "cli.usage.conflicting-options")]
        [InlineData("save-request --request 1 --url http://a.test/ --out results", "cli.usage.missing-argument")]
        [InlineData("save-request --url http://a.test/ --out results --clear-headers", "cli.usage.missing-option")]
        [InlineData("save-request --url http://a.test/ --out results --allow-origin-change", "cli.usage.missing-option")]
        [InlineData("save-request --url http://a.test/ --out results --timeout 5", "cli.usage.unknown-option")]
        [InlineData("save-request --url http://a.test/ --out results --verify", "cli.usage.unknown-option")]
        [InlineData("save-request --url http://a.test:#/ --out results", "cli.usage.invalid-value")]
        [InlineData("save-request --url http://a.test/", "cli.usage.missing-option")]
        [InlineData("save-request a.saz b.saz --request 1 --out results", "cli.usage.unexpected-argument")]
        [InlineData("replay a.saz --request 1 --out results --parallel 3 --max-response-mib 512", "cli.usage.invalid-value")]
        [InlineData("compose --url http://a.test/ --out results --parallel 16 --max-response-mib 65", "cli.usage.invalid-value")]
        [InlineData("compose --url http://a.test/item/ --out results --sequence 1-3", "cli.usage.invalid-value")]
        [InlineData("compose --url http://a#.test/ --out results --sequence 1-3", "cli.usage.invalid-value")]
        [InlineData("compose --url http://a.test:#/ --out results --sequence 1-3", "cli.usage.invalid-value")]
        [InlineData("compose --url http://a.test/# --out results --sequence 3-1", "cli.usage.invalid-value")]
        [InlineData("compose --url http://a.test/# --out results --sequence 1-10001", "cli.usage.invalid-value")]
        [InlineData("compose --url http://a.test/# --out results --sequence 1234567890123456-1234567890123457", "cli.usage.invalid-value")]
        [InlineData("compose --url http://a.test/# --out results", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --sequence 1-2", "cli.usage.unknown-option")]
        // --accept-stored-text is replay's, save-request's and curl's alone (Q's parity gates); a switch takes no value.
        [InlineData("compose --url http://a.test/ --out results --accept-stored-text", "cli.usage.unknown-option")]
        [InlineData("compose --request-file r.json --out results --accept-stored-text", "cli.usage.unknown-option")]
        [InlineData("analyze a.saz --out results --accept-stored-text", "cli.usage.unknown-option")]
        [InlineData("doctor --for analyze --accept-stored-text", "cli.usage.unknown-option")]
        [InlineData("replay a.saz --request 1 --out results --accept-stored-text=yes", "cli.usage.invalid-value")]
        [InlineData("replay a.saz --request 1 --out results --accept-stored-text --accept-stored-text", "cli.usage.duplicate-option")]
        public void Parse_RejectsInvalidCommandLinesWithExitCode2(string commandLine, string code)
        {
            CliParseResult result = CliCommandLine.Parse(Split(commandLine));
            CliReport report = CliReport.ForParseError("run", result);

            Assert.False(result.Succeeded);
            Assert.Null(result.Request);
            Assert.Equal(code, result.Error.Code);
            Assert.Equal(OperationDiagnosticSeverity.Error, result.Error.Severity);
            Assert.Equal(CliExitCodes.Usage, report.ExitCode);
            Assert.Equal("failed", report.Status);
        }

        // replay, save-request and curl read --accept-stored-text from any of their inputs: only an unmarked capture INPUT
        // needs it, which the operation decides, not the parser.
        [Theory]
        [InlineData("replay a.saz --request 1 --out results --accept-stored-text")]
        [InlineData("replay a.saz --request 1 --url http://a.test/ --out results --accept-stored-text")]
        [InlineData("save-request a.saz --request 1 --out results --accept-stored-text")]
        [InlineData("save-request --url http://a.test/ --out results --accept-stored-text")]
        [InlineData("curl a.saz --request 1 --out results --accept-stored-text")]
        [InlineData("curl --request-file r.json --out results --accept-stored-text")]
        public void Parse_ReadsAcceptStoredText_ForReplaySaveRequestAndCurl(string commandLine)
        {
            CliParseResult accepted = CliCommandLine.Parse(Split(commandLine));
            CliParseResult without = CliCommandLine.Parse(Split(commandLine.Replace(" --accept-stored-text", string.Empty)));

            Assert.True(accepted.Succeeded);
            Assert.True(without.Succeeded);
            Assert.True(accepted.Request.Replay.AcceptStoredText);
            Assert.False(without.Request.Replay.AcceptStoredText);
        }

        [Fact]
        public void Parse_ReadsAnalyzeOptionsInEitherForm()
        {
            CliParseResult spaced = CliCommandLine.Parse(
                new[] { "analyze", "a.saz", "--out", "results", "--device", "d2", "--fail-on", "findings" });
            CliParseResult joined = CliCommandLine.Parse(
                new[] { "analyze", "--out=results", "--device=d2", "--fail-on=findings", "--", "-a.saz" });

            Assert.Equal(
                new CliRequest(
                    CliCommands.Analyze, Input: "a.saz", Output: "results", DeviceId: "d2", FailOnFindings: true,
                    Limits: CliLimits.Default),
                spaced.Request);
            Assert.Equal(
                new CliRequest(
                    CliCommands.Analyze, Input: "-a.saz", Output: "results", DeviceId: "d2", FailOnFindings: true,
                    Limits: CliLimits.Default),
                joined.Request);
        }

        [Theory]
        [InlineData("as-stored", (int)CaptureTextInterpretation.AsStored)]
        [InlineData("legacy-one-layer", (int)CaptureTextInterpretation.LegacyOneLayer)]
        public void Parse_ReadsMarkCaptureTextOptions(
            string value,
            int expected)
        {
            CliParseResult result =
                CliCommandLine.Parse(
                    new[]
                    {
                        "mark-capture-text",
                        "capture.xmatcap",
                        "--out",
                        "results",
                        "--interpret",
                        value,
                        "--max-input-mib",
                        "2048",
                    });

            Assert.Equal(
                new CliRequest(
                    CliCommands.MarkCaptureText,
                    Input: "capture.xmatcap",
                    Output: "results",
                    Interpretation:
                        (CaptureTextInterpretation)expected,
                    MaxInputMib: 2048),
                result.Request);
        }

        [Fact]
        public void MarkCaptureTextParseErrorHasNoCommandReportObject()
        {
            CliParseResult parsed =
                CliCommandLine.Parse(
                    new[]
                    {
                        "mark-capture-text",
                        "capture.xmatcap",
                    });
            CliReport report =
                CliReport.ForParseError(
                    "run",
                    parsed);
            using JsonDocument document =
                JsonDocument.Parse(
                    CliJson.Serialize(
                        report));

            Assert.False(
                document.RootElement.TryGetProperty(
                    "markCaptureText",
                    out _));
        }

        [Theory]
        [InlineData("help", null)]
        [InlineData("--help", null)]
        [InlineData("-h", null)]
        [InlineData("/?", null)]
        [InlineData("help analyze", "analyze")]
        [InlineData("--help doctor", "doctor")]
        [InlineData("analyze --help", "analyze")]
        [InlineData("doctor -?", "doctor")]
        [InlineData("help replay", "replay")]
        [InlineData("compose --help", "compose")]
        [InlineData("help mark-capture-text", "mark-capture-text")]
        [InlineData("mark-capture-text --help", "mark-capture-text")]
        public void Parse_RecognizesHelp(string commandLine, string topic)
        {
            CliParseResult result = CliCommandLine.Parse(Split(commandLine));

            Assert.True(result.Succeeded);
            Assert.Equal(new CliRequest(CliCommands.Help, Topic: topic), result.Request);
        }

        [Fact]
        public void Parse_RecognizesVersionAndDoctor()
        {
            Assert.Equal(new CliRequest(CliCommands.Version), CliCommandLine.Parse(new[] { "--version" }).Request);
            Assert.Equal(
                new CliRequest(CliCommands.Doctor, Target: "analyze"),
                CliCommandLine.Parse(new[] { "doctor", "--for", "analyze" }).Request);
        }

        [Fact]
        public void Parse_ReadsReplayOptions()
        {
            CliRequest request = CliCommandLine.Parse(new[]
            {
                "replay", "a.saz", "--request=1,5-7", "--out", "results", "--device", "d2", "--repeat", "3",
                "--parallel=4", "--timeout", "45", "--max-response-mib", "64", "--follow-redirects", "2", "--origin",
                "http://127.0.0.1:8080", "--method", "PUT", "--header", "Accept: application/json", "--header=X-Two:2",
                "--remove-header", "Cookie", "--remove-header", "X-Trace", "--body-file", "body.bin", "--unconditional",
                "--verify", "--allow-unsafe-methods", "--allow-origin-change", "--max-records", "50",
            }).Request;

            Assert.Equal(
                (CliCommands.Replay, "a.saz", "results", "d2", 50),
                (request.Command, request.Input, request.Output, request.DeviceId, request.Limits.MaxRecords));
            CliReplay replay = request.Replay;
            Assert.Equal(
                ("1,5-7", 3, 4, 45, 64, 2),
                (replay.Selection, replay.Repeat, replay.Parallel, replay.TimeoutSeconds, replay.MaxResponseMib, replay.FollowRedirects));
            Assert.Equal(
                ((string)null, "http://127.0.0.1:8080", "PUT", "body.bin"),
                (replay.Url, replay.Origin, replay.Method, replay.BodyFile));
            Assert.Equal(
                new[] { new KeyValuePair<string, string>("Accept", "application/json"), new KeyValuePair<string, string>("X-Two", "2") },
                replay.Headers);
            Assert.Equal(new[] { "Cookie", "X-Trace" }, replay.RemoveHeaders);
            Assert.True(replay.Unconditional && replay.Verify && replay.AllowUnsafeMethods && replay.AllowOriginChange);
            Assert.False(replay.VerifyDiff);
        }

        // --verify-diff verifies too: the verdicts and exit codes are --verify's, with a detail besides.
        [Theory]
        [InlineData("--verify", true, false)]
        [InlineData("--verify-diff", true, true)]
        [InlineData("--verify --verify-diff", true, true)]
        public void Parse_VerifyDiffAlsoVerifies(string switches, bool verify, bool verifyDiff)
        {
            CliReplay replay = CliCommandLine.Parse(Split("replay a.saz --request all --out r " + switches)).Request.Replay;

            Assert.Equal((verify, verifyDiff), (replay.Verify, replay.VerifyDiff));
        }

        // compose and replay take --proxy, kept in its canonical form, and --allow-proxy, the permission that it needs; a
        // proxy URL that cannot be used is a usage error with one fixed message.
        [Theory]
        [InlineData("compose --url https://a.test/ --out r")]
        [InlineData("replay a.saz --request 1 --out r")]
        public void Parse_ReadsTheProxyAndItsPermission(string commandLine)
        {
            CliReplay allowed = CliCommandLine.Parse(Split(commandLine + " --proxy http://Proxy.Test:3128 --allow-proxy")).Request.Replay;
            CliReplay notAllowed = CliCommandLine.Parse(Split(commandLine + " --proxy http://127.0.0.1")).Request.Replay;
            CliReplay none = CliCommandLine.Parse(Split(commandLine)).Request.Replay;
            CliParseResult invalid = CliCommandLine.Parse(Split(commandLine + " --proxy https://a.test"));

            Assert.Equal(("http://proxy.test:3128", true), (allowed.Proxy.ToString(), allowed.AllowProxy));
            Assert.Equal(("http://127.0.0.1:80", false), (notAllowed.Proxy.ToString(), notAllowed.AllowProxy));
            Assert.Equal(((ReplayProxy)null, false), (none.Proxy, none.AllowProxy));
            Assert.Equal(("cli.usage.invalid-value", CliCommandLine.ProxyMessage), (invalid.Error.Code, invalid.Error.Message));
        }

        // compose and replay take --client-cert, a thumbprint kept in upper case, and --allow-client-cert, the one origin
        // that may receive the certificate, kept in canonical form; each is read even without the other. A value that cannot
        // be used is a usage error with one fixed message per option.
        [Theory]
        [InlineData("compose --url https://a.test/ --out r")]
        [InlineData("replay a.saz --request 1 --out r")]
        public void Parse_ReadsTheClientCertificateAndTheOriginThatMayReceiveIt(string commandLine)
        {
            const string Thumbprint = "0123456789abcdef0123456789abcdef01234567";
            CliReplay both = CliCommandLine.Parse(Split(
                commandLine + " --client-cert " + Thumbprint + " --allow-client-cert https://A.Test:8443/")).Request.Replay;
            CliReplay certificateOnly = CliCommandLine.Parse(Split(commandLine + " --client-cert " + Thumbprint)).Request.Replay;
            CliReplay consentOnly = CliCommandLine.Parse(Split(commandLine + " --allow-client-cert https://a.test")).Request.Replay;
            CliReplay none = CliCommandLine.Parse(Split(commandLine)).Request.Replay;
            CliParseResult badThumbprint = CliCommandLine.Parse(Split(commandLine + " --client-cert 0123"));
            CliParseResult badOrigin = CliCommandLine.Parse(Split(commandLine + " --allow-client-cert http://a.test"));

            Assert.Equal(
                (Thumbprint.ToUpperInvariant(), "https://a.test:8443"),
                (both.ClientCertificate.Thumbprint, both.AllowClientCertificate.ToString()));
            Assert.Equal((Thumbprint.ToUpperInvariant(), (ReplayCredentialOrigin)null), (certificateOnly.ClientCertificate.Thumbprint, certificateOnly.AllowClientCertificate));
            Assert.Equal(((ReplayClientCertificate)null, "https://a.test:443"), (consentOnly.ClientCertificate, consentOnly.AllowClientCertificate.ToString()));
            Assert.Equal(((ReplayClientCertificate)null, (ReplayCredentialOrigin)null), (none.ClientCertificate, none.AllowClientCertificate));
            Assert.Equal(("cli.usage.invalid-value", CliCommandLine.ClientCertMessage), (badThumbprint.Error.Code, badThumbprint.Error.Message));
            Assert.Equal(("cli.usage.invalid-value", CliCommandLine.AllowClientCertMessage), (badOrigin.Error.Code, badOrigin.Error.Message));
        }

        // compose and replay take --windows-auth, and --allow-windows-auth, the one origin whose challenges it may answer,
        // kept in canonical form; each is read even without the other, and an origin that cannot be used is a usage error
        // with one fixed message.
        [Theory]
        [InlineData("compose --url https://a.test/ --out r")]
        [InlineData("replay a.saz --request 1 --out r")]
        public void Parse_ReadsWindowsAuthAndTheOriginItMayAnswer(string commandLine)
        {
            CliReplay both = CliCommandLine.Parse(Split(commandLine + " --windows-auth --allow-windows-auth https://A.Test:8443/")).Request.Replay;
            CliReplay windowsOnly = CliCommandLine.Parse(Split(commandLine + " --windows-auth")).Request.Replay;
            CliReplay consentOnly = CliCommandLine.Parse(Split(commandLine + " --allow-windows-auth https://a.test")).Request.Replay;
            CliReplay none = CliCommandLine.Parse(Split(commandLine)).Request.Replay;

            Assert.Equal((true, "https://a.test:8443"), (both.WindowsAuth, both.AllowWindowsAuth.ToString()));
            Assert.Equal((true, (ReplayCredentialOrigin)null), (windowsOnly.WindowsAuth, windowsOnly.AllowWindowsAuth));
            Assert.Equal((false, "https://a.test:443"), (consentOnly.WindowsAuth, consentOnly.AllowWindowsAuth.ToString()));
            Assert.Equal((false, (ReplayCredentialOrigin)null), (none.WindowsAuth, none.AllowWindowsAuth));
            foreach (string origin in new[] { "http://a.test", "https://a.test/x", "https://[fe80::1%253]", "https://127\u30021", "a.test" })
            {
                CliParseResult bad = CliCommandLine.Parse(new List<string>(Split(commandLine)) { "--windows-auth", "--allow-windows-auth", origin }.ToArray());
                Assert.Equal(("cli.usage.invalid-value", CliCommandLine.AllowWindowsAuthMessage), (bad.Error.Code, bad.Error.Message));
            }
        }

        [Fact]
        public void Parse_ReadsComposeOptionsAndTheDefaults()
        {
            CliRequest minimal = CliCommandLine.Parse(new[] { "compose", "--url", "https://a.test/x?y=1", "--out", "r" }).Request;
            CliRequest replay = CliCommandLine.Parse(new[] { "replay", "a.saz", "--request", "all", "--out", "r" }).Request;

            Assert.Equal((CliCommands.Compose, null, "r"), (minimal.Command, minimal.Input, minimal.Output));
            CliReplay compose = minimal.Replay;
            Assert.Equal(("https://a.test/x?y=1", null, null), (compose.Url, compose.Method, compose.Selection));
            Assert.Equal((1, 1, 30, 16, 0), (compose.Repeat, compose.Parallel, compose.TimeoutSeconds, compose.MaxResponseMib, compose.FollowRedirects));
            Assert.Empty(compose.Headers);
            Assert.False(compose.AllowUnsafeMethods || compose.AllowOriginChange || compose.Verify || compose.Unconditional);
            Assert.Null(minimal.Limits);
            Assert.Equal(CliLimits.Default, replay.Limits);
            Assert.Equal("all", replay.Replay.Selection);
            Assert.Equal(
                0,
                CliCommandLine.Parse(new[] { "compose", "--url", "https://a.test/", "--out", "r", "--follow-redirects", "0" })
                    .Request.Replay.FollowRedirects);
            Assert.True(CliCommandLine.Parse(
                new[] { "replay", "a.saz", "--request", "1", "--out", "r", "--parallel", "2", "--max-response-mib", "512" }).Succeeded);
            Assert.True(CliCommandLine.Parse(
                new[] { "compose", "--url", "https://a.test/", "--out", "r", "--parallel", "16", "--max-response-mib", "64" }).Succeeded);
        }

        // compose starts from --url or a request file; save-request from a capture, a request file or --url. Only a file's
        // request takes the completions, and a template's URL may stay a sequence template.
        [Fact]
        public void Parse_ReadsRequestFileAndSaveRequestOptions()
        {
            CliRequest compose = CliCommandLine.Parse(Split(
                "compose --request-file r.json --out o --clear-headers --empty-body --remove-header X --allow-origin-change " +
                "--sequence 1-3")).Request;
            CliRequest save = CliCommandLine.Parse(Split(
                "save-request a.saz --request 1-2 --out o --include-sensitive --allow-origin-change --http-version 1.1")).Request;
            CliRequest fromUrl = CliCommandLine.Parse(Split("save-request --url https://a.test/#?p=# --out o")).Request;

            // A request file holds any valid header line, so save-request takes Unicode values and headers such as Host.
            CliRequest stored = CliCommandLine.Parse(new[]
            {
                "save-request", "--url", "https://a.test/", "--out", "o", "--header", "Host: h.test", "--header", "X-N: caf\u00e9",
            }).Request;
            Assert.Equal(
                new KeyValuePair<string, string>[] { new("Host", "h.test"), new("X-N", "caf\u00e9") }, stored.Replay.Headers);

            Assert.Equal(
                ("r.json", true, true, true, false),
                (compose.Replay.RequestFile, compose.Replay.ClearHeaders, compose.Replay.EmptyBody,
                    compose.Replay.AllowOriginChange, compose.Replay.HttpVersionSet));
            Assert.Equal(new[] { "X" }, compose.Replay.RemoveHeaders);
            Assert.Equal(
                ("a.saz", "1-2", true, true, true),
                (save.Input, save.Replay.Selection, save.Replay.IncludeSensitive, save.Replay.AllowOriginChange,
                    save.Replay.HttpVersionSet));
            Assert.Equal(CliLimits.Default, save.Limits);
            Assert.Equal(("https://a.test/#?p=#", null, false), (fromUrl.Replay.Url, fromUrl.Limits, fromUrl.Replay.IncludeSensitive));
        }

        // curl takes what save-request takes. The options that only a send uses and HTTP/2 only are usage errors, each with
        // one fixed message. Consent is left to the run, which refuses without it before reading anything (exit code 3),
        // so --allow-origin-change without --include-sensitive parses.
        [Fact]
        public void Parse_Curl_TakesWhatSaveRequestTakes_AndRefusesWhatOnlyASendUses()
        {
            CliRequest curl = CliCommandLine.Parse(Split(
                "curl a.saz --request 1-2 --out o --allow-origin-change --http-version 1.1 --unconditional")).Request;
            Assert.Equal(
                (CliCommands.Curl, "a.saz", "1-2", false, true, ReplayHttpVersion.Http11, true),
                (curl.Command, curl.Input, curl.Replay.Selection, curl.Replay.IncludeSensitive, curl.Replay.AllowOriginChange,
                    curl.Replay.HttpVersion, curl.Replay.Unconditional));
            Assert.Equal(CliLimits.Default, curl.Limits);

            foreach (string sendOnly in new[]
            {
                "--repeat 2", "--parallel 2", "--timeout 5", "--max-response-mib 16", "--follow-redirects 1",
                "--connect-to 127.0.0.1:9", "--allow-connect-to", "--proxy http://127.0.0.1:9", "--allow-proxy",
                "--client-cert 0123456789ABCDEF0123456789ABCDEF01234567", "--allow-client-cert https://a.test",
                "--windows-auth", "--allow-windows-auth https://a.test",
                "--sequence 1-2", "--allow-unsafe-methods", "--verify", "--verify-diff", "--origin http://a.test",
            })
            {
                CliParseResult refused = CliCommandLine.Parse(Split("curl --url http://a.test/ --out o --include-sensitive " + sendOnly));
                Assert.Equal(
                    ("cli.usage.unsupported-option", CliCommandLine.CurlSendOnlyMessage), (refused.Error.Code, refused.Error.Message));
            }

            CliParseResult http2 = CliCommandLine.Parse(Split("curl --url https://a.test/ --out o --include-sensitive --http-version 2"));
            Assert.Equal(("cli.usage.invalid-value", CliCommandLine.CurlHttpVersionMessage), (http2.Error.Code, http2.Error.Message));
            Assert.Equal("curl needs --out DIR.", CliCommandLine.Parse(Split("curl --url https://a.test/")).Error.Message);
            Assert.Equal(
                "cli.usage.missing-option",
                CliCommandLine.Parse(Split("save-request --url https://a.test/ --out o --allow-origin-change")).Error.Code);
        }

        // --sequence turns --url into a template; a '#' without it is refused with a pointer to --sequence.
        [Fact]
        public void Parse_ReadsComposeSequences_AndPointsAFragmentAtThem()
        {
            CliParseResult sequence = CliCommandLine.Parse(Split("compose --url http://a.test/item/#?page=# --sequence 08-11 --out r"));
            CliParseResult fragment = CliCommandLine.Parse(Split("compose --url http://a.test/item# --out r"));

            Assert.True(sequence.Succeeded);
            Assert.Equal(("http://a.test/item/#?page=#", "08-11"), (sequence.Request.Replay.Url, sequence.Request.Replay.Sequence));
            Assert.Null(CliCommandLine.Parse(Split("compose --url http://a.test/ --out r")).Request.Replay.Sequence);
            Assert.Equal(CliCommandLine.SequenceNeededMessage, fragment.Error.Message);
        }

        // Errors name commands and options but never repeat values, which can be paths.
        [Theory]
        [InlineData(@"analyze C:\secret\a.saz --out C:\secret\results --bogus=C:\secret\x")]
        [InlineData(@"analyze C:\secret\a.saz --out C:\secret\results --device C:\secret")]
        [InlineData(@"C:\secret\a.saz")]
        [InlineData(@"help C:\secret")]
        [InlineData(@"--out=C:\secret")]
        [InlineData(@"replay C:\secret\a.saz --request C:\secret --out C:\secret\results")]
        [InlineData(@"replay a.saz --request 1 --out results --url http://secret.test/#secret")]
        [InlineData(@"replay a.saz --request 1 --out results --origin http://secret.test/secret")]
        [InlineData(@"replay a.saz --request 1 --out results --remove-header secret:secret")]
        [InlineData(@"compose --url secret --out results")]
        [InlineData(@"compose --url http://secret.test/ --out results --header secret")]
        [InlineData(@"compose --url http://secret.test/ --out results --method se(cret")]
        [InlineData(@"compose --url http://secret.test/ --out results --repeat secret")]
        [InlineData(@"curl --url http://secret.test/ --out results --header secret")]
        [InlineData(@"curl C:\secret\a.saz --request C:\secret --out C:\secret\results --include-sensitive")]
        [InlineData(@"curl --url http://secret.test/ --out results --repeat secret")]
        [InlineData(@"curl --url http://secret.test/ --out results --http-version secret")]
        [InlineData(@"compose --url http://a.test/ --out results --proxy http://user:secret@proxy.test:8080")]
        [InlineData(@"compose --url http://a.test/ --out results --proxy http://proxy.test/secret")]
        [InlineData(@"replay a.saz --request 1 --out results --proxy secret")]
        [InlineData(@"curl --url http://a.test/ --out results --include-sensitive --proxy http://secret.test")]
        [InlineData(@"compose --url https://a.test/ --out results --client-cert secret0123456789ABCDEF0123456789ABCDEF")]
        [InlineData(@"compose --url https://a.test/ --out results --allow-client-cert https://user:secret@a.test")]
        [InlineData(@"replay a.saz --request 1 --out results --allow-client-cert https://a.test/secret")]
        [InlineData(@"curl --url https://a.test/ --out results --include-sensitive --client-cert secret")]
        [InlineData(@"compose --url https://a.test/ --out results --windows-auth --allow-windows-auth https://user:secret@a.test")]
        [InlineData(@"replay a.saz --request 1 --out results --allow-windows-auth https://a.test/secret")]
        [InlineData(@"compose --url https://a.test/ --out results --allow-windows-auth secret")]
        [InlineData(@"curl --url https://a.test/ --out results --include-sensitive --allow-windows-auth https://secret.test")]
        public void Parse_ErrorsNeverRepeatValues(string commandLine)
        {
            CliParseResult result = CliCommandLine.Parse(Split(commandLine));

            Assert.False(result.Succeeded);
            Assert.DoesNotContain("secret", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        }

        // Unknown commands, options, doctor targets and help topics are never repeated either, even when they look like
        // names: a lowercase word with digits can be a token or a password. Only catalog names are ever printed.
        [Theory]
        [InlineData("s3cretcanary9", "cli.usage.unknown-command")]
        [InlineData("--s3cretcanary9", "cli.usage.unknown-option")]
        [InlineData("--s3cretcanary9=s3cretcanary9", "cli.usage.unknown-option")]
        [InlineData("help s3cretcanary9", "cli.usage.unknown-command")]
        [InlineData("--help s3cretcanary9", "cli.usage.unknown-command")]
        [InlineData("analyze a.saz --out results --s3cretcanary9", "cli.usage.unknown-option")]
        [InlineData("analyze a.saz --out results --s3cretcanary9=s3cretcanary9", "cli.usage.unknown-option")]
        [InlineData("analyze a.saz --out results --fail-on s3cretcanary9", "cli.usage.invalid-value")]
        [InlineData("analyze a.saz --out results --device s3cretcanary9", "cli.usage.invalid-value")]
        [InlineData("doctor --for s3cretcanary9", "cli.usage.unknown-target")]
        [InlineData("doctor --s3cretcanary9 analyze", "cli.usage.unknown-option")]
        public void Parse_NeverRepeatsUnknownWords(string commandLine, string code)
        {
            CliParseResult result = CliCommandLine.Parse(Split(commandLine));
            CliReport report = CliReport.ForParseError("run", result);

            Assert.Equal(CliExitCodes.Usage, report.ExitCode);
            Assert.Equal(code, result.Error.Code);
            Assert.DoesNotContain("s3cretcanary9", result.Error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("s3cretcanary9", Encoding.UTF8.GetString(CliJson.Serialize(report)), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void EverySupportedCapability_IsACommandThatXmatRuns()
        {
            foreach (Capability capability in CapabilityRegistry.All.Where(item => item.Status == CapabilityStatus.Supported))
            {
                string command = CliCatalog.CommandOf(capability.CliRoute);
                Assert.True(
                    CliCatalog.IsImplemented(command),
                    $"{capability.Id} is Supported, but xmat has no {command} command. Add it under XMAT/Cli.");
            }
        }

        [Fact]
        public void EveryCommandThatXmatRuns_HasASupportedCapability()
        {
            foreach (string command in CliCommands.Implemented)
            {
                Assert.Contains(
                    CapabilityRegistry.All,
                    item => item.Status == CapabilityStatus.Supported && CliCatalog.CommandOf(item.CliRoute) == command);
            }
        }

        // help covers only the commands that xmat runs, with every Supported command line.
        [Fact]
        public void Help_ListsOnlyTheCommandsThatXmatRuns()
        {
            CliReport report = CliHelp.Create("run", topic: null);

            Assert.Equal(
                new[]
                {
                    CliCommands.Analyze, CliCommands.Compose, CliCommands.Curl, CliCommands.Doctor, CliCommands.MarkCaptureText,
                    CliCommands.Replay, CliCommands.SaveRequest,
                },
                report.Commands.Select(command => command.Name));
            foreach (Capability capability in CapabilityRegistry.All.Where(item => item.Status == CapabilityStatus.Supported))
            {
                Assert.Contains("  xmat " + capability.CliRoute, report.HumanLines);
            }
        }

        // The command lines of replay, compose, save-request and curl in the capability registry, which help and docs/cli.md
        // show, name exactly the options and switches that the parser accepts.
        [Theory]
        [InlineData("request.replay", CliCommands.Replay)]
        [InlineData("request.compose", CliCommands.Compose)]
        [InlineData("request.save", CliCommands.SaveRequest)]
        [InlineData("request.curl", CliCommands.Curl)]
        public void ReplayCommandLines_NameExactlyTheOptionsThatTheParserAccepts(string capabilityId, string command)
        {
            Capability capability = CapabilityRegistry.All.Single(item => item.Id == capabilityId);
            IReadOnlySet<string> accepted = command switch
            {
                CliCommands.Replay => CliCommandLine.ReplayOptionNames,
                CliCommands.Compose => CliCommandLine.ComposeOptionNames,
                CliCommands.Curl => CliCommandLine.CurlOptionNames,
                _ => CliCommandLine.SaveRequestOptionNames,
            };

            IEnumerable<string> named = capability.CliRoute
                .Split(new[] { ' ', '[', ']' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(token => token.StartsWith("--", StringComparison.Ordinal));

            Assert.Equal(command, CliCatalog.CommandOf(capability.CliRoute));
            Assert.Equal(
                accepted.OrderBy(name => name, StringComparer.Ordinal),
                named.OrderBy(name => name, StringComparer.Ordinal));
        }

        [Theory]
        [InlineData(CliExitCodes.VerifyMismatch, 11, "mismatchFound")]
        [InlineData(CliExitCodes.RequestsFailed, 12, "requestsFailed")]
        public void ReplayExitCodes_AreAddedWithTheirStatus(int code, int documented, string status)
        {
            Assert.Equal(documented, code);
            Assert.Equal(status, CliExitCodes.Status(code));
            Assert.True(CliExitCodes.IsPublished(code));
        }

        // The JSON of a replay holds numbers, ids, outcomes, codes and artifact paths only, and lists the files of each
        // send only once they were published. It says that a run connected to another endpoint, went through a proxy, could
        // offer a client certificate or answer with Windows authentication, and with whose consent, never which endpoint,
        // proxy, certificate or origin; of each send, only the Authorization label that Windows authentication observed.
        [Fact]
        public void ReplayJson_HoldsOnlyAllowListedFields()
        {
            Assert.True(ReplayConnectTo.TryParse("198.51.100.77:8443", out ReplayConnectTo endpoint));
            Assert.True(ReplayProxy.TryParse("http://secretproxy.test:3128", out ReplayProxy proxy));
            Assert.True(ReplayClientCertificate.TryParse("ABCDEF0123456789ABCDEF0123456789ABCDEF01", out ReplayClientCertificate certificate));
            Assert.True(ReplayCredentialOrigin.TryParse("https://secret-origin.test:9443", out ReplayCredentialOrigin origin));
            var report = new CliReport(CliCommands.Replay, "run")
            {
                Replay = new CliReplayReport(
                    new CliReplay(
                        Selection: "1",
                        Url: "http://secret.test/",
                        Method: "SECRET",
                        BodyFile: @"C:\secret",
                        HttpVersion: ReplayHttpVersion.Http2,
                        ConnectTo: endpoint,
                        AllowConnectTo: true,
                        Proxy: proxy,
                        AllowProxy: true,
                        ClientCertificate: certificate,
                        AllowClientCertificate: origin,
                        WindowsAuth: true,
                        AllowWindowsAuth: origin),
                    CliLimits.Default)
                {
                    InputKind = "saz",
                    InputSha256 = new string('a', 64),
                    Items = new[]
                    {
                        new ReplayItemResult(
                            1, "d1", 7, 1, ReplayOutcome.Completed, 200, TimeSpan.FromMilliseconds(12.7), 5, 0, null,
                            new ReplayVerification(new[] { ReplayVerifier.BodyDifference }),
                            Transport: ReplayTransportOptions.ConnectToName,
                            HttpVersion: "2",
                            ResponseVersion: "2",
                            WindowsAuthConfigured: true,
                            AuthHeaderSchemeObserved: "NTLM"),
                        new ReplayItemResult(
                            2, "d1", 7, 2, ReplayOutcome.Failed, 0, TimeSpan.Zero, 0, 0, "replay.send.failed", null),
                    },
                },
            };

            using JsonDocument unpublished = JsonDocument.Parse(CliJson.Serialize(report));
            report.Artifacts.Add(new CliArtifact("summary", "summary.json"));
            using JsonDocument published = JsonDocument.Parse(CliJson.Serialize(report));
            using JsonDocument summary = JsonDocument.Parse(CliJson.SerializeReplaySummary(report));

            foreach (byte[] json in new[] { CliJson.Serialize(report), CliJson.SerializeReplaySummary(report) })
            {
                string text = Encoding.UTF8.GetString(json);
                Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("198.51.100.77", text, StringComparison.Ordinal);
                Assert.DoesNotContain("8443", text, StringComparison.Ordinal);
                Assert.DoesNotContain("3128", text, StringComparison.Ordinal);
                Assert.DoesNotContain("9443", text, StringComparison.Ordinal);
                Assert.DoesNotContain("ABCDEF0123", text, StringComparison.OrdinalIgnoreCase);
            }

            Assert.Equal(
                new[] { "schema", "command", "runId", "version", "status", "input", "limits", "options", "replays", "totals", "warnings", "errors" },
                summary.RootElement.EnumerateObject().Select(property => property.Name));
            JsonElement options = published.RootElement.GetProperty("options");
            Assert.Equal(
                ("2", true, true, true, true, true, true, true, true),
                (options.GetProperty("httpVersion").GetString(), options.GetProperty("connectTo").GetBoolean(),
                    options.GetProperty("allowConnectTo").GetBoolean(), options.GetProperty("proxy").GetBoolean(),
                    options.GetProperty("allowProxy").GetBoolean(), options.GetProperty("clientCertificate").GetBoolean(),
                    options.GetProperty("allowClientCertificate").GetBoolean(), options.GetProperty("windowsAuth").GetBoolean(),
                    options.GetProperty("allowWindowsAuth").GetBoolean()));
            JsonElement[] replays = published.RootElement.GetProperty("replays").EnumerateArray().ToArray();
            Assert.Equal(
                new[]
                {
                    "id", "device", "request", "sequence", "iteration", "outcome", "status", "durationMs", "responseBytes",
                    "redirects", "transport", "httpVersion", "responseVersion", "authHeaderSchemeObserved", "raw",
                    "requestWrite", "requestBytesWritten", "responseFraming", "code", "verify", "files",
                },
                replays[0].EnumerateObject().Select(property => property.Name));
            Assert.Equal(
                ("connect-to", "2", "2", "NTLM"),
                (replays[0].GetProperty("transport").GetString(), replays[0].GetProperty("httpVersion").GetString(),
                    replays[0].GetProperty("responseVersion").GetString(), replays[0].GetProperty("authHeaderSchemeObserved").GetString()));
            Assert.Equal(
                ("direct", "auto", JsonValueKind.Null, JsonValueKind.Null),
                (replays[1].GetProperty("transport").GetString(), replays[1].GetProperty("httpVersion").GetString(),
                    replays[1].GetProperty("responseVersion").ValueKind, replays[1].GetProperty("authHeaderSchemeObserved").ValueKind));
            Assert.Equal(("completed", 200, 12L), (replays[0].GetProperty("outcome").GetString(), replays[0].GetProperty("status").GetInt32(), replays[0].GetProperty("durationMs").GetInt64()));
            Assert.Equal("mismatch", replays[0].GetProperty("verify").GetProperty("result").GetString());
            Assert.Equal("replays/00001/response.http", replays[0].GetProperty("files").GetProperty("response").GetString());
            Assert.Equal(JsonValueKind.Null, replays[1].GetProperty("status").ValueKind);
            Assert.Equal(JsonValueKind.Null, replays[1].GetProperty("files").GetProperty("response").ValueKind);
            Assert.Equal("replays/00002/request.http", replays[1].GetProperty("files").GetProperty("request").GetString());
            Assert.False(unpublished.RootElement.GetProperty("replays")[0].TryGetProperty("files", out _));
            JsonElement totals = published.RootElement.GetProperty("totals");
            Assert.Equal((2, 1, 1, 0, 1), (totals.GetProperty("replays").GetInt32(), totals.GetProperty("completed").GetInt32(), totals.GetProperty("failed").GetInt32(), totals.GetProperty("matched").GetInt32(), totals.GetProperty("mismatched").GetInt32()));
        }

        // The options report the version that the run asks for: --http-version, or else the request file's, which a run with
        // Windows authentication makes exactly HTTP/1.1 when it is Automatic; HTTP/2 only stays "2", which the run refuses
        // with Windows authentication. Without Windows authentication, Automatic stays "auto".
        [Theory]
        [InlineData(true, "Auto", false, null, "1.1")]
        [InlineData(false, "Auto", false, null, "auto")]
        [InlineData(true, "Http11", true, null, "1.1")]
        [InlineData(true, "Http2", true, null, "2")]
        [InlineData(true, "Auto", false, "Http2", "2")]
        [InlineData(true, "Auto", true, "Http2", "1.1")]
        [InlineData(true, "Auto", false, "Auto", "1.1")]
        [InlineData(false, "Auto", false, "Http11", "1.1")]
        [InlineData(false, "Http2", true, "Http11", "2")]
        public void ReplayJson_ReportsTheVersionThatTheRunAsksFor(
            bool windowsAuth, string version, bool versionSet, string fileVersion, string reported)
        {
            var replay = new CliReplay(
                Url: "https://a.test/",
                HttpVersion: Enum.Parse<ReplayHttpVersion>(version),
                HttpVersionSet: versionSet,
                WindowsAuth: windowsAuth);
            var report = new CliReport(CliCommands.Compose, "run") { Replay = new CliReplayReport(replay, limits: null) };
            if (fileVersion != null)
            {
                report.Replay.HttpVersion = CliReplayCommand.RequestedHttpVersion(
                    replay,
                    new RequestFileContent(
                        RequestFileProfile.Content, "GET", "https://a.test/", Enum.Parse<ReplayHttpVersion>(fileVersion),
                        Array.Empty<KeyValuePair<string, string>>(), Array.Empty<byte>(), RequestOmissions.None));
            }

            using JsonDocument json = JsonDocument.Parse(CliJson.Serialize(report));

            JsonElement options = json.RootElement.GetProperty("options");
            Assert.Equal(
                (reported, windowsAuth, false),
                (options.GetProperty("httpVersion").GetString(), options.GetProperty("windowsAuth").GetBoolean(),
                    options.GetProperty("allowWindowsAuth").GetBoolean()));
        }

        // With --verify-diff, a verified send names the state of its diff.json, and its path only once published; without
        // the switch, the detail is null.
        [Fact]
        public void ReplayJson_NamesEachDetailsStateAndItsFileOnlyOncePublished()
        {
            static CliReport Report(bool verifyDiff)
            {
                var report = new CliReport(CliCommands.Replay, "run")
                {
                    Replay = new CliReplayReport(new CliReplay(Selection: "1", Verify: true, VerifyDiff: verifyDiff), CliLimits.Default)
                    {
                        Items = new[]
                        {
                            new ReplayItemResult(
                                1, "d1", 7, 1, ReplayOutcome.Completed, 200, TimeSpan.Zero, 5, 0, null,
                                new ReplayVerification(new[] { ReplayVerifier.BodyDifference })),
                        },
                    },
                };
                if (verifyDiff)
                {
                    report.Replay.Details[1] = VerifyDiffState.Limited;
                }

                return report;
            }

            static JsonElement Detail(JsonDocument document) =>
                document.RootElement.GetProperty("replays")[0].GetProperty("verify").GetProperty("detail");

            CliReport withDetails = Report(verifyDiff: true);
            using JsonDocument unpublished = JsonDocument.Parse(CliJson.Serialize(withDetails));
            withDetails.Artifacts.Add(new CliArtifact("summary", "summary.json"));
            using JsonDocument published = JsonDocument.Parse(CliJson.Serialize(withDetails));
            using JsonDocument without = JsonDocument.Parse(CliJson.Serialize(Report(verifyDiff: false)));

            Assert.Equal(
                ("limited", JsonValueKind.Null),
                (Detail(unpublished).GetProperty("state").GetString(), Detail(unpublished).GetProperty("file").ValueKind));
            Assert.Equal("replays/00001/diff.json", Detail(published).GetProperty("file").GetString());
            Assert.True(published.RootElement.GetProperty("options").GetProperty("verifyDiff").GetBoolean());
            Assert.Equal(JsonValueKind.Null, Detail(without).ValueKind);
        }

        [Theory]
        [InlineData(nameof(OperationOutcome.Succeeded), 0)]
        [InlineData(nameof(OperationOutcome.InvalidInput), 3)]
        [InlineData(nameof(OperationOutcome.NoAnalyzableData), 3)]
        [InlineData(nameof(OperationOutcome.PrerequisiteMissing), 4)]
        [InlineData(nameof(OperationOutcome.Conflict), 5)]
        [InlineData(nameof(OperationOutcome.Failed), 6)]
        [InlineData(nameof(OperationOutcome.CleanupFailed), 7)]
        [InlineData(nameof(OperationOutcome.InternalError), 70)]
        [InlineData(nameof(OperationOutcome.Cancelled), 130)]
        public void ExitCodes_FollowTheDocumentedTable(string outcome, int exitCode) =>
            Assert.Equal(exitCode, CliExitCodes.FromOutcome(Enum.Parse<OperationOutcome>(outcome)));

        // summary.json carries the run's warnings (the same data-free codes and messages as standard output) and none of
        // the run's outcome fields.
        [Fact]
        public void Summary_HoldsTheAnalysisAndItsWarnings()
        {
            var report = new CliReport(CliCommands.Analyze, "run") { Analysis = new CliAnalysisReport() };
            report.Warnings.Add(new OperationDiagnostic(
                "analysis.device.skipped", OperationDiagnosticSeverity.Warning, "A non-web-proxy device was skipped."));

            using JsonDocument summary = JsonDocument.Parse(CliJson.SerializeSummary(report));

            Assert.Equal(
                new[] { "schema", "command", "runId", "version", "input", "devices", "totals", "warnings" },
                summary.RootElement.EnumerateObject().Select(property => property.Name));
            JsonElement warning = Assert.Single(summary.RootElement.GetProperty("warnings").EnumerateArray());
            Assert.Equal("analysis.device.skipped", warning.GetProperty("code").GetString());
            Assert.Equal("A non-web-proxy device was skipped.", warning.GetProperty("message").GetString());
        }

        [Fact]
        public void MarkSummaryAndStandardOutputHaveTheFrozenSchemas()
        {
            var report =
                new CliReport(
                    CliCommands.MarkCaptureText,
                    "0123456789abcdef0123456789abcdef")
                {
                    MarkCaptureText =
                        new CliMarkCaptureTextReport
                        {
                            Interpretation =
                                CaptureTextInterpretation.LegacyOneLayer,
                            MaxInputMib = 512,
                            InputKind = "xmatcap",
                            InputSha256 = new string('a', 64),
                            OutputSha256 = new string('b', 64),
                            Counts =
                                new MarkCaptureTextCounts(
                                    2,
                                    3,
                                    4,
                                    5),
                        },
                };
            report.SetExitCode(
                CliExitCodes.Success);
            report.Artifacts.Add(
                new CliArtifact(
                    "capture",
                    "capture.xmatcap"));
            report.Artifacts.Add(
                new CliArtifact(
                    "summary",
                    "summary.json"));

            using JsonDocument output =
                JsonDocument.Parse(
                    CliJson.Serialize(
                        report));
            using JsonDocument summary =
                JsonDocument.Parse(
                    CliJson.SerializeMarkCaptureTextSummary(
                        report));

            JsonElement mark =
                output.RootElement.GetProperty(
                    "markCaptureText");
            Assert.Equal(
                "legacyOneLayer",
                mark.GetProperty(
                    "interpretation")
                    .GetString());
            Assert.Equal(
                5,
                mark.GetProperty(
                    "counts")
                    .GetProperty(
                        "valuesChanged")
                    .GetInt64());
            Assert.Equal(
                new[]
                {
                    "input",
                    "limits",
                    "interpretation",
                    "source",
                    "output",
                    "counts",
                },
                mark.EnumerateObject()
                    .Select(
                        property =>
                            property.Name));
            Assert.Equal(
                JsonValueKind.String,
                mark.GetProperty("input")
                    .GetProperty("kind")
                    .ValueKind);
            Assert.Equal(
                JsonValueKind.String,
                mark.GetProperty("input")
                    .GetProperty("sha256")
                    .ValueKind);
            Assert.Equal(
                new[]
                {
                    "kind",
                    "sha256",
                },
                mark.GetProperty("input")
                    .EnumerateObject()
                    .Select(property => property.Name));
            Assert.Equal(
                "xmatcap",
                mark.GetProperty("input")
                    .GetProperty("kind")
                    .GetString());
            Assert.Equal(
                new string('a', 64),
                mark.GetProperty("input")
                    .GetProperty("sha256")
                    .GetString());
            Assert.Equal(
                JsonValueKind.Number,
                mark.GetProperty("limits")
                    .GetProperty("maxInputMib")
                    .ValueKind);
            Assert.Equal(
                new[]
                {
                    "maxInputMib",
                },
                mark.GetProperty("limits")
                    .EnumerateObject()
                    .Select(property => property.Name));
            Assert.Equal(
                512,
                mark.GetProperty("limits")
                    .GetProperty("maxInputMib")
                    .GetInt32());
            Assert.Equal(
                JsonValueKind.Number,
                mark.GetProperty("source")
                    .GetProperty("userVersion")
                    .ValueKind);
            Assert.Equal(
                JsonValueKind.String,
                mark.GetProperty("source")
                    .GetProperty("semantics")
                    .ValueKind);
            Assert.Equal(
                new[]
                {
                    "userVersion",
                    "semantics",
                },
                mark.GetProperty("source")
                    .EnumerateObject()
                    .Select(property => property.Name));
            Assert.Equal(
                CaptureTextFormat.AmbiguousUserVersion,
                mark.GetProperty("source")
                    .GetProperty("userVersion")
                    .GetInt32());
            Assert.Equal(
                "ambiguousUnmarked",
                mark.GetProperty("source")
                    .GetProperty("semantics")
                    .GetString());
            Assert.Equal(
                new[]
                {
                    "file",
                    "sha256",
                    "userVersion",
                },
                mark.GetProperty("output")
                    .EnumerateObject()
                    .Select(property => property.Name));
            Assert.Equal(
                "capture.xmatcap",
                mark.GetProperty("output")
                    .GetProperty("file")
                    .GetString());
            Assert.Equal(
                new string('b', 64),
                mark.GetProperty("output")
                    .GetProperty("sha256")
                    .GetString());
            Assert.Equal(
                CaptureTextFormat.LiteralUserVersion,
                mark.GetProperty("output")
                    .GetProperty("userVersion")
                    .GetInt32());
            Assert.Equal(
                new[]
                {
                    "tables",
                    "rows",
                    "valuesExamined",
                    "valuesChanged",
                },
                mark.GetProperty("counts")
                    .EnumerateObject()
                    .Select(property => property.Name));
            Assert.All(
                mark.GetProperty("counts")
                    .EnumerateObject(),
                property =>
                    Assert.Equal(
                        JsonValueKind.Number,
                        property.Value.ValueKind));
            Assert.Equal(
                new long[]
                {
                    2,
                    3,
                    4,
                    5,
                },
                mark.GetProperty("counts")
                    .EnumerateObject()
                    .Select(property => property.Value.GetInt64()));
            Assert.Equal(
                "xmat-mark-capture-text/1",
                summary.RootElement.GetProperty(
                    "schema")
                    .GetString());
            Assert.Equal(
                CliReport.ToolVersion,
                summary.RootElement.GetProperty(
                    "version")
                    .GetString());
            Assert.Equal(
                new[]
                {
                    "schema",
                    "command",
                    "runId",
                    "version",
                    "input",
                    "limits",
                    "interpretation",
                    "source",
                    "output",
                    "counts",
                    "warnings",
                },
                summary.RootElement.EnumerateObject()
                    .Select(
                        property =>
                            property.Name));
            foreach (string property in new[]
            {
                "input",
                "limits",
                "interpretation",
                "source",
                "output",
                "counts",
            })
            {
                Assert.True(
                    JsonElement.DeepEquals(
                        mark.GetProperty(
                            property),
                        summary.RootElement.GetProperty(
                            property)),
                    property);
            }
        }

        // QP: a replay, save-request or curl report holds storedText once its requests were prepared, but only a run that
        // published says so: each command's guard is checked on its own, unpublished and then published.
        [Theory]
        [InlineData(CliCommands.Replay)]
        [InlineData(CliCommands.SaveRequest)]
        [InlineData(CliCommands.Curl)]
        public void UnpublishedRuns_NeverReportStoredText(string command)
        {
            var storedText = new XMAT.Operations.Analysis.StoredTextSummary("ambiguousUnmarked", "acceptedAsStored");
            CliReplay options = CliReplay.Default with { AcceptStoredText = true };
            var report = new CliReport(command, "run");
            switch (command)
            {
                case CliCommands.Replay:
                    report.Replay = new CliReplayReport(options, CliLimits.Default) { StoredText = storedText };
                    break;
                case CliCommands.SaveRequest:
                    report.SaveRequest = new CliSaveRequestReport(options, CliLimits.Default) { StoredText = storedText };
                    break;
                default:
                    report.Curl = new CliCurlReport(options, CliLimits.Default) { StoredText = storedText };
                    break;
            }

            report.Fail(CliExitCodes.Conflict, "cli.output.exists", "Nothing was published.");
            using (JsonDocument unpublished = JsonDocument.Parse(CliJson.Serialize(report)))
            {
                Assert.False(unpublished.RootElement.TryGetProperty("storedText", out _));
                Assert.True(unpublished.RootElement.GetProperty("options").GetProperty("acceptStoredText").GetBoolean());
            }

            report.Artifacts.Add(new CliArtifact("summary", "summary.json"));
            using JsonDocument published = JsonDocument.Parse(CliJson.Serialize(report));
            JsonElement reported = published.RootElement.GetProperty("storedText");
            Assert.Equal(
                ("ambiguousUnmarked", "acceptedAsStored"),
                (reported.GetProperty("semantics").GetString(), reported.GetProperty("policy").GetString()));
        }

        [Fact]
        public void FailedMarkOutput_NeverPublishesSourceOutputOrCounts()
        {
            var report =
                new CliReport(
                    CliCommands.MarkCaptureText,
                    "run")
                {
                    MarkCaptureText =
                        new CliMarkCaptureTextReport
                        {
                            Interpretation =
                                CaptureTextInterpretation.AsStored,
                            MaxInputMib = 512,
                            InputKind = "xmatcap",
                            InputSha256 = new string('a', 64),
                            OutputSha256 = new string('b', 64),
                            Counts =
                                new MarkCaptureTextCounts(
                                    1,
                                    2,
                                    3,
                                    4),
                        },
                };
            report.Fail(
                CliExitCodes.Conflict,
                "cli.output.exists",
                "Nothing was published.");

            using JsonDocument output =
                JsonDocument.Parse(
                    CliJson.Serialize(
                        report));
            JsonElement mark =
                output.RootElement.GetProperty(
                    "markCaptureText");

            Assert.False(
                mark.TryGetProperty(
                    "source",
                    out _));
            Assert.False(
                mark.TryGetProperty(
                    "output",
                    out _));
            Assert.False(
                mark.TryGetProperty(
                    "counts",
                    out _));
            Assert.Equal(
                "xmatcap",
                mark.GetProperty(
                        "input")
                    .GetProperty(
                        "kind")
                    .GetString());
            Assert.Equal(
                new[]
                {
                    "input",
                    "limits",
                    "interpretation",
                },
                mark.EnumerateObject()
                    .Select(
                        property =>
                            property.Name));
        }

        [Fact]
        public void HeadlessStorage_CannotBeSetOnceFrozen()
        {
            // PublicUtilities' static constructor freezes it; freezing here never touches the GUI's log or settings.
            Assert.Null(HeadlessStorage.Freeze());

            Assert.Throws<InvalidOperationException>(() => HeadlessStorage.Configure(
                Path.Combine(AppContext.BaseDirectory, "HeadlessTestArtifacts", "Cli", "never-used")));
            Assert.Null(HeadlessStorage.Root);
        }

        private static string[] Split(string commandLine) => commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
}
