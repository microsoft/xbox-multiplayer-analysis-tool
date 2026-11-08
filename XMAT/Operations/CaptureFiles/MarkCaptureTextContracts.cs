// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using XMAT.DataAbstractionLayer;

namespace XMAT.Operations.CaptureFiles
{
    internal enum CaptureTextInterpretation
    {
        AsStored,
        LegacyOneLayer,
    }

    internal sealed record MarkCaptureTextCounts(
        long Tables,
        long Rows,
        long ValuesExamined,
        long ValuesChanged);

    internal sealed record MarkCaptureTextResult(
        string InputSha256,
        long MaxInputBytes,
        CaptureTextInterpretation Interpretation,
        string OutputSha256,
        MarkCaptureTextCounts Counts);

    internal sealed record MarkCaptureTextRequest(
        MarkCaptureTextPlan Plan,
        MarkCaptureTextOutput Output);

    internal sealed record CaptureTextRewriteColumn(
        string Table,
        string Column);

    internal sealed class MarkCaptureTextPlan : IDisposable
    {
        private int _used;
        private SqLtReadonlyDatabase _database;

        internal MarkCaptureTextPlan(
            SqLtReadonlyDatabase database,
            CaptureTextInterpretation interpretation,
            long maxInputBytes,
            string inputSha256,
            MarkCaptureTextCounts counts,
            IReadOnlyList<CaptureTextRewriteColumn> columns)
        {
            _database =
                database ??
                throw new ArgumentNullException(
                    nameof(database));
            Interpretation = interpretation;
            MaxInputBytes = maxInputBytes;
            InputSha256 = inputSha256;
            Counts = counts ??
                throw new ArgumentNullException(
                    nameof(counts));
            Columns = columns ??
                throw new ArgumentNullException(
                    nameof(columns));
        }

        internal SqLtReadonlyDatabase Database =>
            _database ??
            throw new ObjectDisposedException(
                nameof(MarkCaptureTextPlan));

        internal CaptureTextInterpretation Interpretation { get; }

        internal long MaxInputBytes { get; }

        internal string InputSha256 { get; }

        internal MarkCaptureTextCounts Counts { get; }

        internal IReadOnlyList<CaptureTextRewriteColumn> Columns { get; }

        internal bool TryUse() =>
            Interlocked.CompareExchange(
                ref _used,
                1,
                0) == 0;

        public void Dispose() =>
            Interlocked.Exchange(
                ref _database,
                null)
                ?.Dispose();
    }

    internal sealed class MarkCaptureTextOutput : IDisposable
    {
        private SafeFileHandle _folder;

        internal MarkCaptureTextOutput(
            SafeFileHandle folder,
            string fileName)
        {
            ArgumentNullException.ThrowIfNull(folder);
            ArgumentException.ThrowIfNullOrWhiteSpace(
                fileName);
            if (Path.GetFileName(fileName) !=
                    fileName ||
                fileName is "." or "..")
            {
                throw new ArgumentException(
                    "The repair output name must be one root file name.",
                    nameof(fileName));
            }

            _folder = folder;
            FileName = fileName;
        }

        internal SafeFileHandle Folder =>
            _folder ??
            throw new ObjectDisposedException(
                nameof(MarkCaptureTextOutput));

        internal string FileName { get; }

        public void Dispose() =>
            Interlocked.Exchange(
                ref _folder,
                null)
                ?.Dispose();
    }
}
