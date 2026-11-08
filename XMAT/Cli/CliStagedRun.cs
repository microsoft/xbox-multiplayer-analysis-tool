// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.Cli
{
    // A file that a run publishes. Path is relative to --out and uses '/', as in the JSON document. Contents is null for
    // a folder or file that the run already wrote into the staging folder itself, such as replay's messages.
    internal sealed record CliStagedFile(string Kind, string Path, byte[] Contents);

    // Points in the publication of a run where tests synchronize; production passes none.
    internal interface ICliCommitObserver
    {
        void BeforeCommit();

        void CommitAttemptFailed(int attempt);

        void AfterCommit();
    }

    // The publication boundary of a run that writes output. The results are produced in the private staging folder and
    // published by one atomic rename of that folder to --out: the commit.
    // - The staging folder must be private: it must be on a local NTFS volume and its owner and access control are read
    //   back through its handle before anything is produced; otherwise the run fails with exit code 6 and publishes
    //   nothing.
    // - Cancellation is checked while the results are produced and written, and before each commit attempt. A run
    //   cancelled at one of these checks publishes nothing, removes the staging folder and exits with code 130.
    // - An attempt that passed its check may still complete the rename although a signal races with it. A published run
    //   keeps its completion result: its artifacts and its success or findings outcome.
    // - A run whose operation completed with a failure (produce returned null) keeps that outcome, even if a signal
    //   arrived meanwhile; its staging folder is still removed.
    // - A cleanup failure takes precedence: when the staging folder cannot be removed, the exit code is 7, whatever
    //   happened before (CliReport.Fail).
    // - compose --raw publishes through CompleteAfterSendsAsync instead, whose sends are published once they began.
    internal static class CliStagedRun
    {
        // How long a raw run's publication may take once the run's cancellation arrived (contract 2.9).
        internal static readonly TimeSpan FinalizationBound = TimeSpan.FromSeconds(30);

        // Test seam, null in production: a bound for the finalization that is only used when it is shorter. AsyncLocal, so
        // it reaches only the run of the test that sets it.
        internal static readonly AsyncLocal<TimeSpan?> FinalizationBoundSeam = new();

        // The finalization's bound for this run: FinalizationBound, or the seam's when that is shorter and not negative, so
        // no seam can lengthen it or turn it off (Timeout.InfiniteTimeSpan is negative).
        internal static TimeSpan EffectiveFinalizationBound =>
            FinalizationBoundSeam.Value is TimeSpan shorter && shorter >= TimeSpan.Zero && shorter < FinalizationBound
                ? shorter
                : FinalizationBound;
        // produce returns the files to publish, or null when the run ended without results and the report says why.
        internal static async Task<CliReport> CompleteAsync(
            CliReport report,
            CliOutputFolder output,
            Func<CancellationToken, Task<IReadOnlyList<CliStagedFile>>> produce,
            ICliCommitObserver observer,
            CancellationToken cancellationToken)
        {
            bool committed = false;
            try
            {
                if (!output.IsPrivate())
                {
                    CliFailure failure = CliOutputFolder.Unprotected();
                    report.Fail(failure.ExitCode, failure.Code, failure.Message);
                    return report;
                }

                IReadOnlyList<CliStagedFile> files = await produce(cancellationToken).ConfigureAwait(false);
                if (files != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (CliStagedFile file in files)
                    {
                        if (file.Contents != null)
                        {
                            output.WriteFile(file.Path, file.Contents);
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    output.RemoveWorkFolder(cancellationToken);
                    observer?.BeforeCommit();
                    committed = output.TryCommit(observer, cancellationToken);
                    if (committed)
                    {
                        foreach (CliStagedFile file in files)
                        {
                            report.Artifacts.Add(new CliArtifact(file.Kind, file.Path));
                        }

                        report.CleanupState = CliCleanupStates.Completed;
                        observer?.AfterCommit();
                    }
                    else
                    {
                        report.Fail(
                            CliExitCodes.Conflict,
                            "cli.output.exists",
                            "The --out folder appeared while xmat was running; nothing was published.");
                    }
                }
            }
            catch (OperationCanceledException) when (!committed && cancellationToken.IsCancellationRequested)
            {
                report.Cancel();
            }
            catch (Exception ex) when (!committed && CliOutputFolder.IsFileSystemFailure(ex))
            {
                report.Fail(
                    CliExitCodes.Failed,
                    "cli.output.write-failed",
                    "xmat could not write or publish its output; nothing was published.");
            }
            catch (Exception ex) when (!committed && !ExceptionUtilities.IsFatal(ex))
            {
                report.Fail(
                    CliExitCodes.Internal,
                    "cli.internal",
                    "xmat stopped because of an internal error; nothing was published.");

                // The exception type helps a bug report; its message can hold paths or captured data, so it is never shown.
                report.HumanLines.Add($"xmat: internal error ({ex.GetType().FullName}).");
            }
            finally
            {
                if (!committed)
                {
                    Discard(output, report);
                }
            }

            return report;
        }

        // A run whose sends are published once one began (compose --raw, contract 2.9). produce sends on the run's token and
        // writes what its sends leave on finalization, a token of its own that the run's cancellation does not cancel: its
        // bound (FinalizationBound, which tests may only shorten) starts when that cancellation arrives. The files that
        // produce returns, the removal of the work folder and the commit use finalization too, so a run that the operation
        // ended, cancelled after a send began or not, keeps its outcome. A run cancelled before its operation returned is
        // produce's to report. A finalization that fails, passes its bound or does not commit publishes nothing and
        // discards the run, as CompleteAsync does: exit code 6 for a write failure and 130 for the bound, and the report
        // then keeps no cli.cancelled-after-send, whose message says that the sends were published.
        internal static async Task<CliReport> CompleteAfterSendsAsync(
            CliReport report,
            CliOutputFolder output,
            Func<CancellationToken, CancellationToken, Task<IReadOnlyList<CliStagedFile>>> produce,
            ICliCommitObserver observer,
            CancellationToken cancellationToken)
        {
            TimeSpan bound = EffectiveFinalizationBound;
            using var finalization = new CancellationTokenSource();
            using CancellationTokenRegistration startBound = cancellationToken.Register(() =>
            {
                try
                {
                    finalization.CancelAfter(bound);
                }
                catch (ObjectDisposedException)
                {
                    // The run has already finished.
                }
            });
            bool committed = false;
            try
            {
                if (!output.IsPrivate())
                {
                    CliFailure failure = CliOutputFolder.Unprotected();
                    report.Fail(failure.ExitCode, failure.Code, failure.Message);
                    return report;
                }

                IReadOnlyList<CliStagedFile> files =
                    await produce(cancellationToken, finalization.Token).ConfigureAwait(false);
                if (files != null)
                {
                    CancellationToken token = finalization.Token;
                    token.ThrowIfCancellationRequested();
                    foreach (CliStagedFile file in files)
                    {
                        if (file.Contents != null)
                        {
                            output.WriteFile(file.Path, file.Contents);
                        }

                        token.ThrowIfCancellationRequested();
                    }

                    output.RemoveWorkFolder(token);
                    observer?.BeforeCommit();
                    committed = output.TryCommit(observer, token);
                    if (committed)
                    {
                        foreach (CliStagedFile file in files)
                        {
                            report.Artifacts.Add(new CliArtifact(file.Kind, file.Path));
                        }

                        report.CleanupState = CliCleanupStates.Completed;
                        observer?.AfterCommit();
                    }
                    else
                    {
                        report.Fail(
                            CliExitCodes.Conflict,
                            "cli.output.exists",
                            "The --out folder appeared while xmat was running; nothing was published.");
                    }
                }
            }
            catch (OperationCanceledException) when (
                !committed && (cancellationToken.IsCancellationRequested || finalization.IsCancellationRequested))
            {
                WithoutCancelledAfterSend(report);
                report.Cancel();
            }
            catch (Exception ex) when (!committed && CliOutputFolder.IsFileSystemFailure(ex))
            {
                WithoutCancelledAfterSend(report);
                report.Fail(
                    CliExitCodes.Failed,
                    "cli.output.write-failed",
                    "xmat could not write or publish its output; nothing was published.");
            }
            catch (Exception ex) when (!committed && !ExceptionUtilities.IsFatal(ex))
            {
                WithoutCancelledAfterSend(report);
                report.Fail(
                    CliExitCodes.Internal,
                    "cli.internal",
                    "xmat stopped because of an internal error; nothing was published.");

                // The exception type helps a bug report; its message can hold paths or captured data, so it is never shown.
                report.HumanLines.Add($"xmat: internal error ({ex.GetType().FullName}).");
            }
            finally
            {
                if (!committed)
                {
                    WithoutCancelledAfterSend(report);
                    Discard(output, report);
                }
            }

            return report;
        }

        // A run that publishes nothing never says that its sends were published.
        private static void WithoutCancelledAfterSend(CliReport report) =>
            report.Errors.RemoveAll(error => error.Code == CliReplayCommand.CancelledAfterSendCode);

        private static void Discard(CliOutputFolder output, CliReport report)
        {
            if (output.TryDiscard())
            {
                report.CleanupState = CliCleanupStates.Completed;
                return;
            }

            report.CleanupState = CliCleanupStates.Failed;
            report.Fail(
                CliExitCodes.CleanupFailed,
                "cli.cleanup.failed",
                "xmat could not remove its private staging folder next to the --out folder.");
        }
    }
}
