// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using SQLitePCL;
using XMAT.DataAbstractionLayer;
using XMAT.Operations.Storage;

namespace XMAT.Operations.CaptureFiles
{
    internal sealed class MarkCaptureTextOperation :
        IOperation<MarkCaptureTextRequest, MarkCaptureTextResult>
    {
        internal const string OutputInvalidCode =
            "capture.repair.output-invalid";
        internal const string OutputInvalidMessage =
            "The prepared repair output is not an empty regular file.";
        internal const string PlanUsedCode =
            "capture.repair.plan-used";

        private const int ScanBufferBytes = 64 * 1024;
        private static readonly byte[] SqliteHeader =
            Encoding.ASCII.GetBytes(
                "SQLite format 3\0");
        private static readonly IReadOnlyDictionary<string, string[]> RewriteColumns =
            new Dictionary<string, string[]>(
                StringComparer.Ordinal)
            {
                ["CaptureDevices"] =
                    new[] { "DeviceName" },
                ["WebProxyConnections"] =
                    new[]
                    {
                        "DeviceName",
                        "RequestTimestamp",
                        "ResponseTimestamp",
                        "RequestScheme",
                        "RequestHost",
                        "RequestPort",
                        "RequestMethod",
                        "RequestPath",
                        "RequestStatus",
                        "RequestLineAndHeaders",
                        "RequestBody",
                        "ResponseLineAndHeaders",
                        "ResponseBody",
                        "ClientIP",
                    },
                ["NetworkTracePackets"] =
                    new[]
                    {
                        "DeviceName",
                        "Timestamp",
                        "MediaType",
                        "Payload",
                    },
            };

        // Test-only checkpoints: output-opened, copy-verified and repair-closed.
        internal Action<string> Checkpoint { get; set; }

        internal string InputSha256 { get; private set; }

        internal async Task<OperationResult<MarkCaptureTextPlan>> PrepareAsync(
            string path,
            CaptureTextInterpretation interpretation,
            long maxInputBytes,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            context ??= OperationContext.None;
            InputSha256 = null;
            try
            {
                MarkCaptureTextPlan plan =
                    await Task.Run(
                        () => PrepareCore(
                            path,
                            interpretation,
                            maxInputBytes,
                            context,
                            cancellationToken),
                        CancellationToken.None).ConfigureAwait(false);
                return OperationResult<MarkCaptureTextPlan>.Success(
                    plan);
            }
            catch (OperationCanceledException)
            {
                return Failure<MarkCaptureTextPlan>(
                    OperationOutcome.Cancelled,
                    "capture.repair.cancelled",
                    "Capture text repair was cancelled.");
            }
            catch (MarkCaptureTextInputException exception)
            {
                return Failure<MarkCaptureTextPlan>(
                    OperationOutcome.InvalidInput,
                    exception.Code,
                    exception.DiagnosticMessage);
            }
            catch (SqliteException exception) when (
                exception.SqliteErrorCode ==
                    raw.SQLITE_TOOBIG)
            {
                return Failure<MarkCaptureTextPlan>(
                    OperationOutcome.InvalidInput,
                    "analysis.input.limit",
                    "The analysis input exceeds a configured safety limit.");
            }
            catch (SqliteException)
            {
                return Failure<MarkCaptureTextPlan>(
                    OperationOutcome.InvalidInput,
                    "analysis.input.malformed",
                    "The capture database is malformed.");
            }
            catch (Exception exception) when (
                !ExceptionUtilities.IsFatal(
                    exception))
            {
                return Failure<MarkCaptureTextPlan>(
                    OperationOutcome.InternalError,
                    "capture.repair.internal",
                    "Capture text repair could not be prepared because of an internal failure.");
            }
        }

        public async Task<OperationResult<MarkCaptureTextResult>> ExecuteAsync(
            MarkCaptureTextRequest request,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            context ??= OperationContext.None;
            try
            {
                MarkCaptureTextResult result =
                    await Task.Run(
                        () => ExecuteCore(
                            request,
                            context,
                            cancellationToken),
                        CancellationToken.None).ConfigureAwait(false);
                return OperationResult<MarkCaptureTextResult>.Success(
                    result);
            }
            catch (OperationCanceledException)
            {
                return Failure<MarkCaptureTextResult>(
                    OperationOutcome.Cancelled,
                    "capture.repair.cancelled",
                    "Capture text repair was cancelled.");
            }
            catch (MarkCaptureTextInputException exception)
            {
                return Failure<MarkCaptureTextResult>(
                    exception.Outcome,
                    exception.Code,
                    exception.DiagnosticMessage);
            }
            catch (Exception exception) when (
                !ExceptionUtilities.IsFatal(
                    exception))
            {
                return Failure<MarkCaptureTextResult>(
                    OperationOutcome.InternalError,
                    "capture.repair.internal",
                    "Capture text repair could not be completed because of an internal failure.");
            }
        }

        private MarkCaptureTextPlan PrepareCore(
            string path,
            CaptureTextInterpretation interpretation,
            long maxInputBytes,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                maxInputBytes);
            if (!Enum.IsDefined(
                    typeof(CaptureTextInterpretation),
                    interpretation))
            {
                throw new MarkCaptureTextInputException(
                    "cli.usage.invalid-value",
                    "The capture text interpretation is invalid.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path) ||
                !File.Exists(path))
            {
                throw new MarkCaptureTextInputException(
                    "analysis.input.not-found",
                    "The analysis input was not found.");
            }

            if (!HasSqliteHeader(
                    path,
                    maxInputBytes))
            {
                throw new MarkCaptureTextInputException(
                    "capture.repair.input-not-xmatcap",
                    "The input is not an XMAT capture file.");
            }

            SqLtReadonlyDatabase database;
            try
            {
                database =
                    new SqLtReadonlyDatabase(
                        path,
                        SqLtSchemaProfile.CreateRepair(
                            maxInputBytes));
            }
            catch (SqLtSchemaException exception)
            {
                throw MapSchemaFailure(
                    exception);
            }
            catch (FileNotFoundException exception)
            {
                throw new MarkCaptureTextInputException(
                    "analysis.input.not-found",
                    "The analysis input was not found.",
                    exception);
            }
            catch (Exception exception) when (
                exception is IOException or
                    UnauthorizedAccessException)
            {
                throw new MarkCaptureTextInputException(
                    "analysis.input.unreadable",
                    "The analysis input could not be read.",
                    exception);
            }

            try
            {
                SqLtReadonlyDatabase.ApplyRepairLengthLimit(
                    database.Connection,
                    maxInputBytes);
                if (database.CaptureTextSource.Semantics ==
                    CaptureTextSemantics.Literal)
                {
                    throw new MarkCaptureTextInputException(
                        "capture.repair.already-marked",
                        "The capture file is already marked for literal text storage; no text interpretation was applied.");
                }

                byte[] hash =
                    database.ComputeSha256(
                        (done, total) =>
                            Report(
                                context,
                                "capture-text.hashing",
                                done,
                                total),
                        cancellationToken);
                string inputSha256 =
                    Convert.ToHexString(hash)
                        .ToLowerInvariant();
                InputSha256 = inputSha256;
                IReadOnlyList<CaptureTextRewriteColumn> columns =
                    ColumnsOf(
                        database.ValidatedSchema);
                MarkCaptureTextCounts counts =
                    CountRows(
                        database,
                        columns,
                        interpretation,
                        context,
                        cancellationToken);
                return new MarkCaptureTextPlan(
                    database,
                    interpretation,
                    maxInputBytes,
                    inputSha256,
                    counts,
                    columns);
            }
            catch
            {
                database.Dispose();
                throw;
            }
        }

        private MarkCaptureTextResult ExecuteCore(
            MarkCaptureTextRequest request,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.Plan);
            ArgumentNullException.ThrowIfNull(request.Output);
            if (!request.Plan.TryUse())
            {
                throw new MarkCaptureTextInputException(
                    PlanUsedCode,
                    "The prepared capture text repair plan has already been used.");
            }

            MarkCaptureTextPlan plan =
                request.Plan;
            SqLtReadonlyDatabase source =
                plan.Database;
            cancellationToken.ThrowIfCancellationRequested();
            SafeFileHandle guard;
            try
            {
                guard =
                    AnchoredHandles.OpenExistingFile(
                        request.Output.Folder,
                        request.Output.FileName);
                if (guard == null ||
                    !IsEmptyRegularFile(
                        guard))
                {
                    guard?.Dispose();
                    throw OutputInvalid();
                }
            }
            catch (MarkCaptureTextInputException)
            {
                throw;
            }
            catch (FileNotFoundException exception)
            {
                throw new MarkCaptureTextInputException(
                    "analysis.input.not-found",
                    "The analysis input was not found.",
                    exception);
            }
            catch (Exception exception) when (
                AnchoredFileFailure(exception))
            {
                throw OutputInvalid(
                    exception);
            }

            using (guard)
            {
                Checkpoint?.Invoke(
                    "output-opened");
                source.CopySnapshotToExisting(
                    guard,
                    (done, total) =>
                        Report(
                            context,
                            "capture-text.copying",
                            done,
                            total),
                    cancellationToken);
                AnchoredHandles.FlushFileBuffers(
                    guard);
                string copiedSha256 =
                    Hash(
                        guard,
                        cancellationToken);
                if (!string.Equals(
                        copiedSha256,
                        plan.InputSha256,
                        StringComparison.Ordinal))
                {
                    throw new MarkCaptureTextInputException(
                        "analysis.input.unreadable",
                        "The analysis input could not be read.");
                }

                Checkpoint?.Invoke(
                    "copy-verified");
                string outputPath =
                    AnchoredHandles.FinalPath(
                        guard);
                Repair(
                    outputPath,
                    plan,
                    context,
                    cancellationToken);
                AnchoredHandles.FlushFileBuffers(
                    guard);
                Checkpoint?.Invoke(
                    "repair-closed");
                string outputSha256 =
                    Hash(
                        guard,
                        cancellationToken);
                string sourceSha256 =
                    Convert.ToHexString(
                            source.ComputeSha256(
                                cancellationToken))
                        .ToLowerInvariant();
                if (!string.Equals(
                        sourceSha256,
                        plan.InputSha256,
                        StringComparison.Ordinal))
                {
                    throw new MarkCaptureTextInputException(
                        "analysis.input.unreadable",
                        "The analysis input could not be read.");
                }

                return new MarkCaptureTextResult(
                    plan.InputSha256,
                    plan.MaxInputBytes,
                    plan.Interpretation,
                    outputSha256,
                    plan.Counts);
            }
        }

        private static bool HasSqliteHeader(
            string path,
            long maxInputBytes)
        {
            try
            {
                using var input =
                    new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        SqliteHeader.Length,
                        FileOptions.SequentialScan);
                if (input.Length >
                    maxInputBytes)
                {
                    throw new MarkCaptureTextInputException(
                        "analysis.input.too-large",
                        "The analysis input exceeds the configured size limit.");
                }

                Span<byte> header =
                    stackalloc byte[SqliteHeader.Length];
                return input.Read(header) ==
                        header.Length &&
                    header.SequenceEqual(
                        SqliteHeader);
            }
            catch (MarkCaptureTextInputException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or
                    UnauthorizedAccessException)
            {
                throw new MarkCaptureTextInputException(
                    "analysis.input.unreadable",
                    "The analysis input could not be read.",
                    exception);
            }
        }

        private static IReadOnlyList<CaptureTextRewriteColumn> ColumnsOf(
            SqLtValidatedSchema schema)
        {
            var result =
                new List<CaptureTextRewriteColumn>();
            foreach ((string canonical, string[] columns) in
                RewriteColumns)
            {
                if (!schema.Tables.TryGetValue(
                        canonical,
                        out SqLtValidatedTable table))
                {
                    continue;
                }

                foreach (string column in columns)
                {
                    SqLtSchemaColumn actual =
                        table.Columns.FirstOrDefault(
                            candidate =>
                                candidate.Name.Equals(
                                    column,
                                    StringComparison.OrdinalIgnoreCase));
                    if (actual != null)
                    {
                        result.Add(
                            new CaptureTextRewriteColumn(
                                table.SqliteName,
                                actual.Name));
                    }
                }
            }

            return result;
        }

        private static MarkCaptureTextCounts CountRows(
            SqLtReadonlyDatabase database,
            IReadOnlyList<CaptureTextRewriteColumn> columns,
            CaptureTextInterpretation interpretation,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            long rows = 0;
            foreach (SqLtValidatedTable table in
                database.ValidatedSchema.Tables.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var command =
                    database.Connection.CreateCommand();
                command.CommandText =
                    $"SELECT COUNT(*) FROM {Quote(table.SqliteName)}";
                rows = checked(
                    rows +
                    (long)command.ExecuteScalar());
            }

            if (interpretation ==
                CaptureTextInterpretation.AsStored)
            {
                return new MarkCaptureTextCounts(
                    database.ValidatedSchema.Tables.Count,
                    rows,
                    0,
                    0);
            }

            long values =
                CountValues(
                    database.Connection,
                    columns);
            long examined = 0;
            long changed = 0;
            foreach (CaptureTextRewriteColumn column in
                columns)
            {
                using var command =
                    database.Connection.CreateCommand();
                command.CommandText =
                    $"SELECT id,typeof({Quote(column.Column)}) FROM {Quote(column.Table)}";
                using SqliteDataReader reader =
                    command.ExecuteReader();
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!string.Equals(
                            reader.GetString(1),
                            "text",
                            StringComparison.Ordinal))
                    {
                        throw NotLegacy();
                    }

                    bool hasQuotes =
                        ValidateBlob(
                            database.Connection,
                            column,
                            reader.GetInt64(0),
                            cancellationToken);
                    examined++;
                    if (hasQuotes)
                    {
                        changed++;
                    }
                    Report(
                        context,
                        "capture-text.validating",
                        examined,
                        values);
                }
            }

            Report(
                context,
                "capture-text.validating",
                values,
                values);
            return new MarkCaptureTextCounts(
                database.ValidatedSchema.Tables.Count,
                rows,
                examined,
                changed);
        }

        private static long CountValues(
            SqliteConnection connection,
            IReadOnlyList<CaptureTextRewriteColumn> columns)
        {
            long values = 0;
            foreach (IGrouping<string, CaptureTextRewriteColumn> table in
                columns.GroupBy(
                    column => column.Table,
                    StringComparer.Ordinal))
            {
                using var command =
                    connection.CreateCommand();
                command.CommandText =
                    $"SELECT COUNT(*) FROM {Quote(table.Key)}";
                values = checked(
                    values +
                    checked(
                        (long)command.ExecuteScalar() *
                        table.LongCount()));
            }

            return values;
        }

        private static bool ValidateBlob(
            SqliteConnection connection,
            CaptureTextRewriteColumn column,
            long rowId,
            CancellationToken cancellationToken)
        {
            using var blob =
                new SqliteBlob(
                    connection,
                    column.Table,
                    column.Column,
                    rowId,
                    readOnly: true);
            byte[] bytes =
                ArrayPool<byte>.Shared.Rent(
                    ScanBufferBytes);
            char[] chars =
                ArrayPool<char>.Shared.Rent(
                    ScanBufferBytes + 2);
            Decoder decoder =
                new UTF8Encoding(
                    false,
                    true)
                    .GetDecoder();
            bool oddQuotes = false;
            bool hasQuotes = false;
            try
            {
                long readTotal = 0;
                while (readTotal < blob.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read =
                        blob.Read(
                            bytes,
                            0,
                            (int)Math.Min(
                                bytes.Length,
                                blob.Length -
                                readTotal));
                    if (read == 0)
                    {
                        throw new EndOfStreamException();
                    }

                    for (int index = 0;
                        index < read;
                        index++)
                    {
                        if (bytes[index] == 0x22)
                        {
                            oddQuotes = !oddQuotes;
                            hasQuotes = true;
                        }
                        else if (oddQuotes)
                        {
                            throw NotLegacy();
                        }
                    }

                    readTotal += read;
                    decoder.Convert(
                        bytes,
                        0,
                        read,
                        chars,
                        0,
                        chars.Length,
                        readTotal == blob.Length,
                        out int bytesUsed,
                        out _,
                        out _);
                    if (bytesUsed !=
                        read)
                    {
                        throw NotLegacy();
                    }
                }

                if (blob.Length == 0)
                {
                    decoder.Convert(
                        Array.Empty<byte>(),
                        0,
                        0,
                        chars,
                        0,
                        chars.Length,
                        true,
                        out _,
                        out _,
                        out _);
                }
            }
            catch (DecoderFallbackException exception)
            {
                throw NotLegacy(
                    exception);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(
                    bytes);
                ArrayPool<char>.Shared.Return(
                    chars);
            }

            if (oddQuotes)
            {
                throw NotLegacy();
            }

            return hasQuotes;
        }

        private static void Repair(
            string outputPath,
            MarkCaptureTextPlan plan,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            var connectionString =
                new SqliteConnectionStringBuilder
                {
                    DataSource = outputPath,
                    Mode = SqliteOpenMode.ReadWrite,
                    Pooling = false,
                }.ToString();
            using var connection =
                new SqliteConnection(
                    connectionString);
            connection.Open();
            SqLtReadonlyDatabase.ApplyRepairLengthLimit(
                connection,
                plan.MaxInputBytes);
            using SqliteTransaction transaction =
                connection.BeginTransaction();
            long completed = 0;
            if (plan.Interpretation ==
                CaptureTextInterpretation.LegacyOneLayer)
            {
                foreach (CaptureTextRewriteColumn column in
                    plan.Columns)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var command =
                        connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText =
                        $"UPDATE {Quote(column.Table)} SET {Quote(column.Column)}=" +
                        $"replace({Quote(column.Column)},$encoded,$decoded)";
                    command.Parameters.AddWithValue(
                        "$encoded",
                        "\"\"");
                    command.Parameters.AddWithValue(
                        "$decoded",
                        "\"");
                    command.ExecuteNonQuery();
                    completed++;
                    Report(
                        context,
                        "capture-text.rewriting",
                        completed,
                        plan.Columns.Count);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            using (var marker =
                connection.CreateCommand())
            {
                marker.Transaction = transaction;
                marker.CommandText =
                    $"PRAGMA user_version={CaptureTextFormat.LiteralUserVersion}";
                marker.ExecuteNonQuery();
            }
            using (var read =
                connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText =
                    "PRAGMA user_version";
                if ((long)read.ExecuteScalar() !=
                    CaptureTextFormat.LiteralUserVersion)
                {
                    throw new InvalidOperationException(
                        "The repaired capture did not keep its literal text marker.");
                }
            }

            transaction.Commit();
        }

        private static bool IsEmptyRegularFile(
            SafeFileHandle handle)
        {
            AnchoredFileFacts facts =
                AnchoredHandles.FileFacts(
                    handle);
            return facts.Length == 0 &&
                !facts.IsDirectory &&
                !facts.IsReparsePoint &&
                facts.LinkCount == 1;
        }

        private static string Hash(
            SafeFileHandle handle,
            CancellationToken cancellationToken) =>
            Convert.ToHexString(
                    SqLtReadonlyDatabase.ComputeSha256(
                        handle,
                        cancellationToken))
                .ToLowerInvariant();

        private static string Quote(string identifier) =>
            "\"" +
            identifier.Replace(
                "\"",
                "\"\"",
                StringComparison.Ordinal) +
            "\"";

        private static void Report(
            OperationContext context,
            string phase,
            long done,
            long total) =>
            context.Progress.Report(
                new OperationProgress(
                    phase,
                    total == 0
                        ? 1
                        : (double)done /
                            total));

        private static bool AnchoredFileFailure(
            Exception exception) =>
            exception is IOException or
                UnauthorizedAccessException or
                System.Security.SecurityException;

        private static MarkCaptureTextInputException OutputInvalid(
            Exception innerException = null) =>
            new(
                OperationOutcome.Failed,
                OutputInvalidCode,
                OutputInvalidMessage,
                innerException);

        private static MarkCaptureTextInputException NotLegacy(
            Exception innerException = null) =>
            new(
                "capture.repair.not-legacy-encoding",
                "The capture does not have the one-pass legacy XMAT text encoding required by legacy-one-layer; no output was created.",
                innerException);

        private static MarkCaptureTextInputException MapSchemaFailure(
            SqLtSchemaException exception) =>
            exception.Failure switch
            {
                SqLtSchemaFailure.Limit =>
                    new MarkCaptureTextInputException(
                        "analysis.input.limit",
                        "The analysis input exceeds a configured safety limit.",
                        exception),
                SqLtSchemaFailure.Sidecar or
                SqLtSchemaFailure.WriteAheadLog =>
                    new MarkCaptureTextInputException(
                        "analysis.input.live-database",
                        "A live or write-ahead capture database is unsupported.",
                        exception),
                SqLtSchemaFailure.UnsupportedLocation =>
                    new MarkCaptureTextInputException(
                        "analysis.input.location-unsupported",
                        "The capture database location cannot be opened safely.",
                        exception),
                SqLtSchemaFailure.UnsupportedVersion =>
                    new MarkCaptureTextInputException(
                        "capture.format.future",
                        "The capture file uses a newer XMAT capture format and cannot be opened by this version of XMAT.",
                        exception),
                SqLtSchemaFailure.Unsupported =>
                    new MarkCaptureTextInputException(
                        "capture.repair.input-not-xmatcap",
                        "The input is not an XMAT capture file.",
                        exception),
                _ =>
                    new MarkCaptureTextInputException(
                        "analysis.input.schema",
                        "The capture database schema is invalid.",
                        exception),
            };

        private static OperationResult<T> Failure<T>(
            OperationOutcome outcome,
            string code,
            string message) =>
            OperationResult<T>.Failure(
                outcome,
                new OperationDiagnostic(
                    code,
                    outcome == OperationOutcome.Cancelled
                        ? OperationDiagnosticSeverity.Info
                        : OperationDiagnosticSeverity.Error,
                    message));

        private sealed class MarkCaptureTextInputException :
            Exception
        {
            internal MarkCaptureTextInputException(
                string code,
                string diagnosticMessage,
                Exception innerException = null)
                : this(
                    OperationOutcome.InvalidInput,
                    code,
                    diagnosticMessage,
                    innerException)
            {
            }

            internal MarkCaptureTextInputException(
                OperationOutcome outcome,
                string code,
                string diagnosticMessage,
                Exception innerException = null)
                : base(
                    "The capture text repair input is invalid.",
                    innerException)
            {
                Outcome = outcome;
                Code = code;
                DiagnosticMessage = diagnosticMessage;
            }

            internal OperationOutcome Outcome { get; }

            internal string Code { get; }

            internal string DiagnosticMessage { get; }
        }
    }
}
