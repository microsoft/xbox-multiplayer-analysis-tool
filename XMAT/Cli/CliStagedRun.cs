// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.Cli
{
    // A file that a run publishes. Path is relative to --out and uses '/', as in the JSON document.
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
    // - The staging folder must be private: its access control is read back before anything is produced, and a folder
    //   that is not private (storage without Windows permissions) fails the run with exit code 6 and publishes nothing.
    // - Cancellation is checked while the results are produced and written, and before each commit attempt. A run
    //   cancelled at one of these checks publishes nothing, removes the staging folder and exits with code 130.
    // - An attempt that passed its check may still complete the rename although a signal races with it. A published run
    //   keeps its completion result: its artifacts and its success or findings outcome.
    // - A run whose operation completed with a failure (produce returned null) keeps that outcome, even if a signal
    //   arrived meanwhile; its staging folder is still removed.
    // - A cleanup failure takes precedence: when the staging folder cannot be removed, the exit code is 7, whatever
    //   happened before (CliReport.Fail).
    internal static class CliStagedRun
    {
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
                    report.Fail(
                        CliExitCodes.Failed,
                        "cli.output.unprotected",
                        "xmat cannot keep its output private to your account where the --out folder would be created; it " +
                            "needs a file system with Windows permissions, such as NTFS. Nothing was published.");
                    return report;
                }

                IReadOnlyList<CliStagedFile> files = await produce(cancellationToken).ConfigureAwait(false);
                if (files != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (CliStagedFile file in files)
                    {
                        output.WriteFile(file.Path, file.Contents);
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
