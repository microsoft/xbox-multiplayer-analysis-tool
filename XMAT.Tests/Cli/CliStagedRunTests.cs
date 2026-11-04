// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using XMAT.Cli;

namespace XMAT.Tests.Cli
{
    // The publication boundary of a run (CliStagedRun), in process with a synthetic producer and deterministic barriers
    // instead of timing: cancellation is checked before each commit attempt (the atomic rename of the staging folder to
    // --out), a published run keeps its result, and a cleanup failure (exit code 7) wins over everything. XmatSignalTests
    // sends a real console signal. Also what a run reports when its staging folder is not private, or when its report
    // cannot be serialized.
    public class CliStagedRunTests
    {
        private const string Results = "results";

        [Theory]
        [InlineData("while-producing")]
        [InlineData("before-commit")]
        public async Task CancellationBeforeTheCommit_PublishesNothingAndReturns130(string point)
        {
            using var directory = new CliTestDirectory();
            using var cancellation = new CancellationTokenSource();
            var report = NewReport();
            CliOutputFolder output = Reserve(directory, report);
            var observer = new CommitObserver
            {
                Before = () =>
                {
                    if (point == "before-commit")
                    {
                        cancellation.Cancel();
                    }
                },
            };

            await CliStagedRun.CompleteAsync(
                report,
                output,
                _ =>
                {
                    // A producer that ignores cancellation: the boundary still checks before it writes and commits.
                    if (point == "while-producing")
                    {
                        cancellation.Cancel();
                    }

                    return Produce(report, CliExitCodes.FindingsFound);
                },
                observer,
                cancellation.Token);

            Assert.Equal(CliExitCodes.Cancelled, report.ExitCode);
            Assert.Equal("cancelled", report.Status);
            Assert.Equal(new[] { "cli.cancelled" }, Codes(report));
            Assert.Empty(report.Artifacts);
            Assert.Equal(CliCleanupStates.Completed, report.CleanupState);
            Assert.False(observer.Committed);
            Assert.Empty(directory.Entries());
        }

        [Theory]
        [InlineData(CliExitCodes.Success, "succeeded")]
        [InlineData(CliExitCodes.FindingsFound, "findingsFound")]
        public async Task ASignalAfterTheCommit_ChangesNothing(int exitCode, string status)
        {
            using var directory = new CliTestDirectory();
            using var cancellation = new CancellationTokenSource();
            var report = NewReport();
            CliOutputFolder output = Reserve(directory, report);
            var observer = new CommitObserver { After = cancellation.Cancel };

            await CliStagedRun.CompleteAsync(report, output, _ => Produce(report, exitCode), observer, cancellation.Token);

            Assert.True(cancellation.IsCancellationRequested);
            Assert.True(observer.Committed);
            Assert.Equal(exitCode, report.ExitCode);
            Assert.Equal(status, report.Status);
            Assert.Empty(report.Errors);
            Assert.Equal(
                new[] { "summary:summary.json" },
                report.Artifacts.Select(artifact => artifact.Kind + ":" + artifact.Path));
            Assert.Equal(CliCleanupStates.Completed, report.CleanupState);
            Assert.Equal(new[] { Results }, directory.Entries());
            Assert.Equal("{}", File.ReadAllText(Path.Combine(directory.Root, Results, "summary.json")));
        }

        // A file that is open in the staging folder blocks the rename, as antivirus scanners briefly do, and xmat retries.
        // Cancellation during the retries wins at once: no further attempt and nothing published.
        [Fact]
        public async Task CancellationWhileTheCommitIsRetried_StopsAndPublishesNothing()
        {
            using var directory = new CliTestDirectory();
            using var cancellation = new CancellationTokenSource();
            var report = NewReport();
            CliOutputFolder output = Reserve(directory, report);
            FileStream blocker = null;
            var observer = new CommitObserver
            {
                AttemptFailed = _ =>
                {
                    cancellation.Cancel();
                    blocker.Dispose();
                },
            };

            try
            {
                await CliStagedRun.CompleteAsync(
                    report,
                    output,
                    _ =>
                    {
                        blocker = OpenBlocker(output);
                        return Produce(report, CliExitCodes.Success);
                    },
                    observer,
                    cancellation.Token);
            }
            finally
            {
                blocker?.Dispose();
            }

            Assert.Equal(1, observer.FailedAttempts);
            Assert.False(observer.Committed);
            Assert.Equal(CliExitCodes.Cancelled, report.ExitCode);
            Assert.Equal(new[] { "cli.cancelled" }, Codes(report));
            Assert.Empty(report.Artifacts);
            Assert.Equal(CliCleanupStates.Completed, report.CleanupState);
            Assert.Empty(directory.Entries());
        }

        // The staging folder cannot be removed after the cancellation: exit code 7, and the cancellation is still reported.
        [Fact]
        public async Task ACleanupFailure_WinsOverCancellation()
        {
            using var directory = new CliTestDirectory();
            using var cancellation = new CancellationTokenSource();
            var report = NewReport();
            CliOutputFolder output = Reserve(directory, report);
            FileStream blocker = null;
            var observer = new CommitObserver { AttemptFailed = _ => cancellation.Cancel() };

            try
            {
                await CliStagedRun.CompleteAsync(
                    report,
                    output,
                    _ =>
                    {
                        blocker = OpenBlocker(output);
                        return Produce(report, CliExitCodes.Success);
                    },
                    observer,
                    cancellation.Token);
            }
            finally
            {
                blocker?.Dispose();
            }

            Assert.Equal(CliExitCodes.CleanupFailed, report.ExitCode);
            Assert.Equal("failed", report.Status);
            Assert.Equal(new[] { "cli.cancelled", "cli.cleanup.failed" }, Codes(report));
            Assert.Equal(CliCleanupStates.Failed, report.CleanupState);
            Assert.Empty(report.Artifacts);
            Assert.False(Directory.Exists(Path.Combine(directory.Root, Results)));
            Assert.True(Directory.Exists(output.StagingPath));
        }

        // The operation reported that it could not clean up (exit code 7); a cancellation that follows does not replace it.
        [Fact]
        public async Task ACleanupFailureOfTheOperation_StandsWhenTheRunIsCancelled()
        {
            using var directory = new CliTestDirectory();
            using var cancellation = new CancellationTokenSource();
            var report = NewReport();
            CliOutputFolder output = Reserve(directory, report);

            await CliStagedRun.CompleteAsync(
                report,
                output,
                token =>
                {
                    report.Fail(CliExitCodes.CleanupFailed, "test.cleanup.failed", "The operation could not clean up.");
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                    return Produce(report, CliExitCodes.Success);
                },
                null,
                cancellation.Token);

            Assert.Equal(CliExitCodes.CleanupFailed, report.ExitCode);
            Assert.Equal(new[] { "test.cleanup.failed", "cli.cancelled" }, Codes(report));
            Assert.Empty(report.Artifacts);
            Assert.Empty(directory.Entries());
        }

        [Fact]
        public void ExitCode7_IsNeverReplaced()
        {
            var report = NewReport();
            report.Cancel();
            report.Fail(CliExitCodes.CleanupFailed, "cli.cleanup.failed", "Cleanup failed.");

            report.Cancel();
            report.SetExitCode(CliExitCodes.FindingsFound);
            report.Fail(CliExitCodes.Failed, "cli.output.write-failed", "Write failed.");

            Assert.Equal(CliExitCodes.CleanupFailed, report.ExitCode);
            Assert.Equal("failed", report.Status);
        }

        // Storage without Windows permissions (FAT, exFAT, some shares) silently drops the access control that Reserve sets,
        // and storage that maps permissions imperfectly can drop their inheritance. A staging folder whose access control
        // changed after Reserve stands in for both: the run refuses before it produces anything, publishes nothing and
        // removes the staging folder. The folder as Reserve made it reads back as private.
        [Theory]
        [InlineData("inherits")]
        [InlineData("grants-everyone")]
        [InlineData("not-inheritable")]
        public async Task AStagingFolderThatIsNotPrivate_FailsClosedBeforeAnythingIsProduced(string change)
        {
            using var directory = new CliTestDirectory();
            var report = NewReport();
            CliOutputFolder output = Reserve(directory, report);
            Assert.True(output.IsPrivate());
            var staging = new DirectoryInfo(output.StagingPath);
            DirectorySecurity security = staging.GetAccessControl();
            if (change == "inherits")
            {
                security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            }
            else if (change == "grants-everyone")
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                    FileSystemRights.ReadAndExecute,
                    AccessControlType.Allow));
            }
            else
            {
                // The same user and rights, but files created in the folder no longer inherit them.
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                security.PurgeAccessRules(identity.User);
                security.AddAccessRule(new FileSystemAccessRule(
                    identity.User,
                    FileSystemRights.FullControl,
                    InheritanceFlags.None,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }

            staging.SetAccessControl(security);
            bool produced = false;

            await CliStagedRun.CompleteAsync(
                report,
                output,
                _ =>
                {
                    produced = true;
                    return Produce(report, CliExitCodes.Success);
                },
                null,
                CancellationToken.None);

            Assert.False(produced);
            Assert.Equal(CliExitCodes.Failed, report.ExitCode);
            Assert.Equal(new[] { "cli.output.unprotected" }, Codes(report));
            Assert.Empty(report.Artifacts);
            Assert.Equal(CliCleanupStates.Completed, report.CleanupState);
            Assert.Empty(directory.Entries());
        }

        // A report that cannot be serialized (here a null warning, which also breaks the text on standard error) is replaced
        // by the fixed fallback document. Exit code 7 stays, and a published run keeps its result and lists its artifacts;
        // only a run whose document was its result, such as --version, becomes an internal error.
        [Theory]
        [InlineData("published", CliExitCodes.Success)]
        [InlineData("published-findings", CliExitCodes.FindingsFound)]
        [InlineData("cleanup-failed", CliExitCodes.CleanupFailed)]
        [InlineData("version", CliExitCodes.Internal)]
        public void AReportThatCannotBeSerialized_IsReplacedByTheFallbackDocument(string run, int exitCode)
        {
            CliReport report = run == "version" ? CliReport.ForVersion(Guid.NewGuid().ToString("N")) : NewReport();
            bool published = run.StartsWith("published", StringComparison.Ordinal);
            if (published)
            {
                report.SetExitCode(exitCode);
                report.Artifacts.Add(new CliArtifact("summary", "summary.json"));
                report.CleanupState = CliCleanupStates.Completed;
            }
            else if (run == "cleanup-failed")
            {
                report.Fail(CliExitCodes.CleanupFailed, "cli.cleanup.failed", "Cleanup failed.");
                report.CleanupState = CliCleanupStates.Failed;
            }

            report.Warnings.Add(null);
            using var output = new MemoryStream();
            using var error = new StringWriter();

            CliApplication.Write(new CliConsole(output, error), report);

            Assert.Equal(exitCode, report.ExitCode);
            string text = Encoding.UTF8.GetString(output.ToArray());
            Assert.EndsWith("\r\n", text, StringComparison.Ordinal);
            using JsonDocument document = JsonDocument.Parse(text);
            JsonElement json = document.RootElement;
            Assert.Equal(
                new[] { "schema", "command", "runId", "status", "exitCode", "artifacts", "cleanup", "warnings", "errors" },
                json.EnumerateObject().Select(property => property.Name));
            Assert.Equal("xmat-cli/1", json.GetProperty("schema").GetString());
            Assert.Equal(report.Command, json.GetProperty("command").GetString());
            Assert.Equal(report.RunId, json.GetProperty("runId").GetString());
            Assert.Equal(CliExitCodes.Status(exitCode), json.GetProperty("status").GetString());
            Assert.Equal(exitCode, json.GetProperty("exitCode").GetInt32());
            Assert.Equal(
                published ? new[] { "summary:summary.json" } : Array.Empty<string>(),
                json.GetProperty("artifacts").EnumerateArray()
                    .Select(artifact => artifact.GetProperty("kind").GetString() + ":" + artifact.GetProperty("path").GetString()));
            Assert.Equal(report.CleanupState, json.GetProperty("cleanup").GetProperty("state").GetString());
            Assert.Empty(json.GetProperty("warnings").EnumerateArray());
            Assert.Equal(
                new[] { "cli.internal.output" },
                json.GetProperty("errors").EnumerateArray().Select(diagnostic => diagnostic.GetProperty("code").GetString()));
            Assert.Contains("xmat: internal error (System.NullReferenceException).", error.ToString(), StringComparison.Ordinal);
        }

        private static CliReport NewReport() => new(CliCommands.Analyze, Guid.NewGuid().ToString("N"));

        private static CliOutputFolder Reserve(CliTestDirectory directory, CliReport report) =>
            CliOutputFolder.Reserve(Path.Combine(directory.Root, Results), report.RunId);

        private static Task<IReadOnlyList<CliStagedFile>> Produce(CliReport report, int exitCode)
        {
            report.SetExitCode(exitCode);
            return Task.FromResult<IReadOnlyList<CliStagedFile>>(new[]
            {
                new CliStagedFile("summary", "summary.json", Encoding.UTF8.GetBytes("{}")),
            });
        }

        // An open file without delete sharing: the staging folder can be neither renamed nor removed while it is open.
        private static FileStream OpenBlocker(CliOutputFolder output) =>
            new(Path.Combine(output.StagingPath, "blocker.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);

        private static string[] Codes(CliReport report) => report.Errors.Select(error => error.Code).ToArray();

        private sealed class CommitObserver : ICliCommitObserver
        {
            internal Action Before { get; init; }

            internal Action<int> AttemptFailed { get; init; }

            internal Action After { get; init; }

            internal int FailedAttempts { get; private set; }

            internal bool Committed { get; private set; }

            public void BeforeCommit() => Before?.Invoke();

            public void CommitAttemptFailed(int attempt)
            {
                FailedAttempts = attempt;
                AttemptFailed?.Invoke(attempt);
            }

            public void AfterCommit()
            {
                Committed = true;
                After?.Invoke();
            }
        }
    }
}
