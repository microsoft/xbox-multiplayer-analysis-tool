// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using XMAT.Cli;
using XMAT.Operations.Replay;
using XMAT.Tests.Fixtures;
using XMAT.Tests.Proxy;
using XMAT.Tests.Replay;

namespace XMAT.Tests.Cli
{
    // xmat's URLs whose host has no IDN form, whether Uri's IDN host for it throws or is the name unconverted, are refused
    // before anything runs, as invalid values or as an invalid request file, while an IPv6 zone of any characters is kept
    // as data; and a run never publishes a send without its request, which summary.json would name. Loopback servers only;
    // nothing leaves the machine.
    [Collection(IsolatedProxyTests.Name)]
    public sealed class XmatIdnHostTests
    {
        private const string NoIdnUrl = "http://" + SyntheticCaptures.IdnTooLongHost + "/";

        // Every URL and origin option, for a host whose IDN host throws and for one that Uri keeps unconverted; save-request
        // with its default template profile, which would leave the URL out, included.
        public static TheoryData<string[]> CommandLines
        {
            get
            {
                var data = new TheoryData<string[]>();
                foreach (string host in new[] { SyntheticCaptures.IdnTooLongHost, SyntheticCaptures.NonAsciiBasicHost })
                {
                    string url = "http://" + host + "/";
                    data.Add(new[] { "compose", "--url", url, "--out", "results" });
                    data.Add(new[] { "compose", "--url", url + "item/#", "--sequence", "1-2", "--out", "results" });
                    data.Add(new[] { "save-request", "--url", url, "--out", "results" });
                    data.Add(new[] { "replay", "capture.saz", "--request", "1", "--url", url, "--out", "results" });
                    data.Add(new[] { "replay", "capture.saz", "--request", "1", "--origin", "https://" + host, "--out", "results" });
                    data.Add(new[] { "curl", "capture.saz", "--request", "1", "--url", url, "--include-sensitive", "--out", "results" });
                }

                return data;
            }
        }

        public static TheoryData<string> HostsWithoutAnIdnForm => new()
        {
            SyntheticCaptures.IdnTooLongHost,
            SyntheticCaptures.NonAsciiBasicHost,
        };

        [Theory]
        [MemberData(nameof(CommandLines))]
        public void AUrlOrOriginWhoseHostHasNoIdnForm_IsAnInvalidValue(string[] args)
        {
            CliParseResult result = CliCommandLine.Parse(args);

            Assert.False(result.Succeeded);
            Assert.Equal("cli.usage.invalid-value", result.Error.Code);
        }

        // Before hosts were checked, this sequence failed as an internal error (exit code 70).
        [Fact]
        public async Task ASequenceWhoseHostHasNoIdnForm_IsRefusedAsUsage_AndCreatesNothing()
        {
            using var directory = new CliTestDirectory();

            XmatRun run = await XmatProcess.RunAsync(
                directory.Root, "compose", "--url", NoIdnUrl + "item/#", "--sequence", "1-2", "--out", "results");

            CliAssert.ExitCode(2, run);
            Assert.Equal(new[] { "cli.usage.invalid-value" }, run.ErrorCodes());
            Assert.False(Directory.Exists(Path.Join(directory.Root, "results")));
            CliAssert.NoStagingFolders(directory.Root);
        }

        // A request file's URL is checked as it is read: one whose host has no IDN form is an invalid request file.
        [Theory]
        [MemberData(nameof(HostsWithoutAnIdnForm))]
        public async Task ARequestFileWhoseHostHasNoIdnForm_IsAnInvalidRequestFile(string host)
        {
            using var directory = new CliTestDirectory();
            string requestFile = WriteRequestFile(directory.Root, "request.json", "http://" + host + "/item");

            XmatRun run = await XmatProcess.RunAsync(directory.Root, "compose", "--request-file", requestFile, "--out", "results");

            CliAssert.ExitCode(3, run);
            Assert.Equal(new[] { RequestFileCodes.Invalid }, run.ErrorCodes());
            Assert.False(Directory.Exists(Path.Join(directory.Root, "results")));
            CliAssert.NoStagingFolders(directory.Root);
        }

        // Saving it is refused as reading it is, also as a template, which would leave its URL out: save-request sends
        // nothing, but a file it saves is one that a later run reads.
        [Theory]
        [MemberData(nameof(HostsWithoutAnIdnForm))]
        public async Task ARequestFileWhoseHostHasNoIdnForm_IsNotSaved_EvenAsATemplate(string host)
        {
            using var directory = new CliTestDirectory();
            string requestFile = WriteRequestFile(directory.Root, "request.json", "http://" + host + "/item");

            XmatRun run = await XmatProcess.RunAsync(
                directory.Root, "save-request", "--request-file", requestFile, "--out", "saved");

            CliAssert.ExitCode(3, run);
            Assert.Equal(new[] { RequestFileCodes.Invalid }, run.ErrorCodes());
            Assert.False(Directory.Exists(Path.Join(directory.Root, "saved")));
            CliAssert.NoStagingFolders(directory.Root);
        }

        // An IPv6 zone of any characters is data, and the request file's own origin: saving the file's content keeps its URL
        // exactly, and is no change of origin that would need --allow-origin-change.
        [Fact]
        public async Task ARequestFilesZoneOfAnyCharacters_IsSavedWithItsContent()
        {
            using var directory = new CliTestDirectory();
            const string Url = "http://[fe80::1%25\u00e9]/loaded";
            string requestFile = WriteRequestFile(directory.Root, "scoped.json", Url);

            XmatRun run = await XmatProcess.RunAsync(
                directory.Root, "save-request", "--request-file", requestFile, "--include-sensitive", "--out", "saved");

            CliAssert.ExitCode(0, run);
            using JsonDocument saved = JsonDocument.Parse(
                File.ReadAllText(Path.Join(directory.Root, "saved", "requests", "00001.json")));
            Assert.Equal(Url, saved.RootElement.GetProperty("url").GetString());
            CliAssert.NoStagingFolders(directory.Root);
        }

        // A send that reaches the outputs without its request, after one that completed, fails the run as an internal error
        // and publishes nothing, so no summary.json names a request.http that was never written: a parsed send without its
        // described request, or a raw send in a run that has no raw image. The run says which invariant failed by its type.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ASendWithoutItsRequest_FailsTheRun_AndPublishesNothing(bool raw)
        {
            using var directory = new CliTestDirectory();
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok("ok"));
            CliParseResult parsed = CliCommandLine.Parse(new[]
            {
                "compose", "--url", server.Origin + "/item/#", "--sequence", "1-2", "--out", Path.Join(directory.Root, "results"),
            });
            Assert.True(parsed.Succeeded);
            int given = 0;
            CliReplayCommand.ExchangeSeam.Value = exchange => ++given == 2 ? WithoutRequest(exchange, raw) : exchange;
            CliReport report;
            try
            {
                report = await CliReplayCommand.RunAsync(parsed.Request, "run", configureStorage: false, CancellationToken.None);
            }
            finally
            {
                CliReplayCommand.ExchangeSeam.Value = null;
            }

            Assert.Equal(2, given);
            Assert.Equal(2, server.Requests.Count);
            Assert.Equal(CliExitCodes.Internal, report.ExitCode);
            Assert.Equal(new[] { "cli.internal" }, report.Errors.Select(error => error.Code));
            Assert.Contains("xmat: internal error (System.InvalidOperationException).", report.HumanLines);
            Assert.Empty(report.Artifacts);
            Assert.Equal(CliCleanupStates.Completed, report.CleanupState);
            Assert.False(Directory.Exists(Path.Join(directory.Root, "results")));
            CliAssert.NoStagingFolders(directory.Root);
        }

        // The exchange that a send gave, without its described request; or, when raw, marked as a raw send, whose request is
        // the run's raw image.
        private static ReplayExchange WithoutRequest(ReplayExchange exchange, bool raw) =>
            new()
            {
                Outcome = exchange.Outcome,
                Code = exchange.Code,
                Request = raw ? exchange.Request : null,
                RequestUri = exchange.RequestUri,
                Started = exchange.Started,
                Response = exchange.Response,
                StatusCode = exchange.StatusCode,
                ReasonPhrase = exchange.ReasonPhrase,
                Version = exchange.Version,
                Duration = exchange.Duration,
                Redirects = exchange.Redirects,
                ProvenanceOrigin = exchange.ProvenanceOrigin,
                Raw = raw,
            };

        // A content request file with a GET to url and no headers or body.
        private static string WriteRequestFile(string root, string name, string url)
        {
            string path = Path.Join(root, name);
            File.WriteAllText(
                path,
                "{\"schema\":\"xmat-request/1\",\"profile\":\"content\",\"method\":\"GET\",\"url\":\"" + url + "\"," +
                    "\"httpVersion\":\"auto\",\"headers\":[],\"body\":\"\",\"omitted\":[]}");
            return path;
        }
    }
}
