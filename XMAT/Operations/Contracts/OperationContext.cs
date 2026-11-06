// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;

namespace XMAT.Operations
{
    // What an operation may use from its caller: a scratch directory and progress. Operations never show UI; the GUI
    // and the command line adapt progress and results in their own way.
    internal sealed class OperationContext
    {
        internal OperationContext(
            string workingDirectory = null,
            IProgress<OperationProgress> progress = null)
        {
            WorkingDirectory = workingDirectory;
            Progress = progress ?? NoProgress.Instance;
        }

        internal static OperationContext None { get; } = new();

        // Caller-owned scratch directory; null means the operation must not write files.
        internal string WorkingDirectory { get; }

        // Never null; a no-op when the caller supplied none.
        internal IProgress<OperationProgress> Progress { get; }

        private sealed class NoProgress : IProgress<OperationProgress>
        {
            internal static readonly NoProgress Instance = new();

            public void Report(OperationProgress value)
            {
            }
        }
    }
}
