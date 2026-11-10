// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;
using XMAT.DataAbstractionLayer;
using XMAT.Operations.Processes;
using XMAT.SharedInterfaces;
using XMAT.XboxLiveCaptureAnalysis;

namespace XMAT.Operations.Capture
{
    internal abstract class SazExportSource
    {
        private const string RequestNumberField = "RequestNumber";
        private const string RequestTimestampField = "RequestTimestamp";
        private const string ResponseTimestampField = "ResponseTimestamp";
        private const string RequestSchemeField = "RequestScheme";
        private const string RequestHeadField = "RequestLineAndHeaders";
        private const string RequestBodyField = "RequestBody";
        private const string ResponseHeadField = "ResponseLineAndHeaders";
        private const string ResponseBodyField = "ResponseBody";
        private const string ClientAddressField = "ClientIP";
        private static readonly Encoding Utf8 = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false);

        protected SazExportSource(CaptureTextSource textSource)
        {
            ArgumentNullException.ThrowIfNull(textSource);
            TextSource = textSource;
        }

        internal static SazExportSource Active(
            IDataTable table,
            string deviceName,
            CaptureTextSource textSource)
        {
            return table switch
            {
                null => new EmptySource(textSource),
                SqLtDataTable sqlite => new ActiveSource(
                    sqlite,
                    deviceName,
                    textSource),
                _ => new UnsupportedSource(textSource),
            };
        }

        internal static SazExportSource Loaded(
            IDataset dataset,
            string deviceName,
            CaptureTextSource textSource)
        {
            return dataset switch
            {
                null => new EmptySource(textSource),
                SqLtDataset sqlite => new LoadedSource(
                    sqlite,
                    deviceName,
                    textSource),
                _ => new UnsupportedSource(textSource),
            };
        }

        internal static SazExportSource FromRecords(
            IReadOnlyList<IDataRecord> records,
            CaptureTextSource textSource)
        {
            return new RecordSource(
                records ?? Array.Empty<IDataRecord>(),
                textSource);
        }

        internal CaptureTextSource TextSource { get; }

        internal virtual bool IsUnsupported => false;

        internal abstract long? GetWatermark(
            CancellationToken cancellationToken);

        internal abstract SazExportPage ReadPage(
            long? afterRowId,
            long throughRowId,
            int scan,
            SazExportBudgets budgets,
            CancellationToken cancellationToken);

        internal abstract StoredRow ReadRow(
            SazExportKey key,
            SazExportBudgets budgets,
            CancellationToken cancellationToken);

        internal virtual SazExportPage ReadPageForKind(
            long? afterRowId,
            long throughRowId,
            int scan,
            SazExportBudgets budgets,
            SazExportKind kind,
            CancellationToken cancellationToken) =>
            ReadPage(
                afterRowId,
                throughRowId,
                scan,
                budgets,
                cancellationToken);

        internal virtual StoredRow ReadRowForKind(
            SazExportKey key,
            SazExportBudgets budgets,
            SazExportKind kind,
            CancellationToken cancellationToken) =>
            ReadRow(
                key,
                budgets,
                cancellationToken);

        private class EmptySource : SazExportSource
        {
            internal EmptySource(CaptureTextSource textSource)
                : base(textSource)
            {
            }

            internal override long? GetWatermark(
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }

            internal override SazExportPage ReadPage(
                long? afterRowId,
                long throughRowId,
                int scan,
                SazExportBudgets budgets,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new(
                    Array.Empty<SazExportKey>(),
                    afterRowId);
            }

            internal override StoredRow ReadRow(
                SazExportKey key,
                SazExportBudgets budgets,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new ArgumentOutOfRangeException(nameof(key));
            }
        }

        private sealed class UnsupportedSource : EmptySource
        {
            internal UnsupportedSource(CaptureTextSource textSource)
                : base(textSource)
            {
            }

            internal override bool IsUnsupported => true;
        }

        private sealed class ActiveSource : EmptySource
        {
            internal ActiveSource(
                SqLtDataTable table,
                string deviceName,
                CaptureTextSource textSource)
                : base(textSource)
            {
                Table = table;
                DeviceName = deviceName;
            }

            internal SqLtDataTable Table { get; }

            internal string DeviceName { get; }

            internal override long? GetWatermark(
                CancellationToken cancellationToken) =>
                Table.ReadForSazExport(
                    (connection, table) =>
                        SqliteSazExportReader.MaxRowId(
                            connection,
                            table,
                            cancellationToken));

            internal override SazExportPage ReadPage(
                long? afterRowId,
                long throughRowId,
                int scan,
                SazExportBudgets budgets,
                CancellationToken cancellationToken) =>
                ReadPageForKind(
                    afterRowId,
                    throughRowId,
                    scan,
                    budgets,
                    SazExportKind.Plain,
                    cancellationToken);

            internal override SazExportPage ReadPageForKind(
                long? afterRowId,
                long throughRowId,
                int scan,
                SazExportBudgets budgets,
                SazExportKind kind,
                CancellationToken cancellationToken) =>
                Table.ReadForSazExport(
                    (connection, table) =>
                        SqliteSazExportReader.ReadPage(
                            connection,
                            table,
                            DeviceName,
                            afterRowId,
                            throughRowId,
                            scan,
                            budgets,
                            kind,
                            cancellationToken));

            internal override StoredRow ReadRow(
                SazExportKey key,
                SazExportBudgets budgets,
                CancellationToken cancellationToken) =>
                ReadRowForKind(
                    key,
                    budgets,
                    SazExportKind.Plain,
                    cancellationToken);

            internal override StoredRow ReadRowForKind(
                SazExportKey key,
                SazExportBudgets budgets,
                SazExportKind kind,
                CancellationToken cancellationToken) =>
                Table.ReadForSazExport(
                    (connection, table) =>
                        SqliteSazExportReader.ReadRow(
                            connection,
                            table,
                            key,
                            budgets,
                            kind,
                            cancellationToken));
        }

        private sealed class LoadedSource : EmptySource
        {
            internal LoadedSource(
                SqLtDataset dataset,
                string deviceName,
                CaptureTextSource textSource)
                : base(textSource)
            {
                Dataset = dataset;
                DeviceName = deviceName;
            }

            internal SqLtDataset Dataset { get; }

            internal string DeviceName { get; }

            internal override long? GetWatermark(
                CancellationToken cancellationToken) =>
                Dataset.ReadForSazExport(
                    (connection, table) =>
                        SqliteSazExportReader.MaxRowId(
                            connection,
                            table,
                            cancellationToken));

            internal override SazExportPage ReadPage(
                long? afterRowId,
                long throughRowId,
                int scan,
                SazExportBudgets budgets,
                CancellationToken cancellationToken) =>
                ReadPageForKind(
                    afterRowId,
                    throughRowId,
                    scan,
                    budgets,
                    SazExportKind.Plain,
                    cancellationToken);

            internal override SazExportPage ReadPageForKind(
                long? afterRowId,
                long throughRowId,
                int scan,
                SazExportBudgets budgets,
                SazExportKind kind,
                CancellationToken cancellationToken) =>
                Dataset.ReadForSazExport(
                    (connection, table) =>
                        SqliteSazExportReader.ReadPage(
                            connection,
                            table,
                            DeviceName,
                            afterRowId,
                            throughRowId,
                            scan,
                            budgets,
                            kind,
                            cancellationToken));

            internal override StoredRow ReadRow(
                SazExportKey key,
                SazExportBudgets budgets,
                CancellationToken cancellationToken) =>
                ReadRowForKind(
                    key,
                    budgets,
                    SazExportKind.Plain,
                    cancellationToken);

            internal override StoredRow ReadRowForKind(
                SazExportKey key,
                SazExportBudgets budgets,
                SazExportKind kind,
                CancellationToken cancellationToken) =>
                Dataset.ReadForSazExport(
                    (connection, table) =>
                        SqliteSazExportReader.ReadRow(
                            connection,
                            table,
                            key,
                            budgets,
                            kind,
                            cancellationToken));
        }

        private sealed class RecordSource : SazExportSource
        {
            internal RecordSource(
                IReadOnlyList<IDataRecord> records,
                CaptureTextSource textSource)
                : base(textSource)
            {
                Records = records
                    .Where(static record => record != null)
                    .OrderBy(static record => record.RowId)
                    .ToArray();
            }

            internal IReadOnlyList<IDataRecord> Records { get; }

            internal override long? GetWatermark(
                CancellationToken cancellationToken)
            {
                long? watermark = null;
                foreach (IDataRecord record in Records)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (record != null &&
                        (!watermark.HasValue ||
                         record.RowId > watermark.Value))
                    {
                        watermark = record.RowId;
                    }
                }

                return watermark;
            }

            internal override SazExportPage ReadPage(
                long? afterRowId,
                long throughRowId,
                int scan,
                SazExportBudgets budgets,
                CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(budgets);
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scan);

                var keys = new List<SazExportKey>();
                long? last = afterRowId;
                foreach (IDataRecord record in Records)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (record == null ||
                        record.RowId <= (afterRowId ?? long.MinValue) ||
                        record.RowId > throughRowId)
                    {
                        continue;
                    }

                    last = record.RowId;
                    keys.Add(
                        new SazExportKey(
                            record.RowId,
                            record.Int(RequestNumberField),
                            PlannedBytes(
                                record,
                                budgets)));
                    if (keys.Count == scan)
                    {
                        break;
                    }
                }

                return new(keys, last);
            }

            internal override StoredRow ReadRow(
                SazExportKey key,
                SazExportBudgets budgets,
                CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(budgets);
                foreach (IDataRecord record in Records)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (record != null &&
                        record.RowId == key.RowId)
                    {
                        return ReadRecord(
                            record,
                            key,
                            budgets,
                            cancellationToken);
                    }
                }

                throw new ArgumentOutOfRangeException(nameof(key));
            }

            private static long PlannedBytes(
                IDataRecord record,
                SazExportBudgets budgets)
            {
                string requestHead =
                    record.Str(RequestHeadField);
                string responseHead =
                    record.Str(ResponseHeadField);
                long requestHeadBytes =
                    ByteCount(requestHead);
                long responseHeadBytes =
                    ByteCount(responseHead);
                if (requestHeadBytes >
                        budgets.MaxHeadReadBytes ||
                    responseHeadBytes >
                        budgets.MaxHeadReadBytes)
                {
                    return 0;
                }

                long total = checked(
                    requestHeadBytes +
                    responseHeadBytes);
                total = checked(
                    total +
                    PlannedBodyBytes(
                        record,
                        RequestBodyField));
                total = checked(
                    total +
                    PlannedBodyBytes(
                        record,
                        ResponseBodyField));
                total = checked(
                    total +
                    PlannedSmallFieldBytes(
                        record.Str(RequestSchemeField),
                        budgets.MaxSchemeBytes));
                total = checked(
                    total +
                    PlannedSmallFieldBytes(
                        record.Str(RequestTimestampField),
                        budgets.MaxTimeBytes));
                total = checked(
                    total +
                    PlannedSmallFieldBytes(
                        record.Str(ResponseTimestampField),
                        budgets.MaxTimeBytes));
                return checked(
                    total +
                    PlannedSmallFieldBytes(
                        record.Str(ClientAddressField),
                        budgets.MaxClientAddressBytes));
            }

            private static long PlannedBodyBytes(
                IDataRecord record,
                string field)
            {
                if (record is IRawBodyDataRecord raw)
                {
                    return raw.RawBody(field).Length;
                }

                string text = record.Str(field);
                if (string.IsNullOrEmpty(text) ||
                    text.Length % 4 != 0)
                {
                    return 0;
                }

                return ByteCount(text);
            }

            private static long PlannedSmallFieldBytes(
                string value,
                int maximumBytes)
            {
                long bytes = ByteCount(value);
                return bytes <= maximumBytes
                    ? bytes
                    : 0;
            }

            private static StoredRow ReadRecord(
                IDataRecord record,
                SazExportKey key,
                SazExportBudgets budgets,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string requestHead =
                    record.Str(RequestHeadField);
                string responseHead =
                    record.Str(ResponseHeadField);
                long requestHeadBytes =
                    ByteCount(requestHead);
                long responseHeadBytes =
                    ByteCount(responseHead);
                if (requestHeadBytes >
                        budgets.MaxHeadReadBytes ||
                    responseHeadBytes >
                        budgets.MaxHeadReadBytes)
                {
                    return StoredRow.Oversized(
                        key.RowId,
                        key.RequestNumber);
                }

                long bytesRead = checked(
                    requestHeadBytes +
                    responseHeadBytes);
                if (TooManyStoredHeadLines(
                        requestHead,
                        budgets.MaxHeadFieldLines) ||
                    TooManyStoredHeadLines(
                        responseHead,
                        budgets.MaxHeadFieldLines))
                {
                    return StoredRow.Oversized(
                        key.RowId,
                        key.RequestNumber,
                        bytesRead);
                }

                StoredBody requestBody = ReadBody(
                    record,
                    RequestBodyField,
                    budgets.MaxBodyBytes,
                    ref bytesRead,
                    cancellationToken);
                StoredBody responseBody = ReadBody(
                    record,
                    ResponseBodyField,
                    budgets.MaxBodyBytes,
                    ref bytesRead,
                    cancellationToken);
                string scheme = ReadSmallField(
                    record.Str(RequestSchemeField),
                    budgets.MaxSchemeBytes,
                    ref bytesRead,
                    out bool schemeUnavailable);
                string requestTimestamp = ReadSmallField(
                    record.Str(RequestTimestampField),
                    budgets.MaxTimeBytes,
                    ref bytesRead,
                    out bool requestTimestampUnavailable);
                string responseTimestamp = ReadSmallField(
                    record.Str(ResponseTimestampField),
                    budgets.MaxTimeBytes,
                    ref bytesRead,
                    out bool responseTimestampUnavailable);
                string clientAddress = ReadSmallField(
                    record.Str(ClientAddressField),
                    budgets.MaxClientAddressBytes,
                    ref bytesRead,
                    out bool clientAddressUnavailable);
                return new StoredRow(
                    key.RowId,
                    key.RequestNumber,
                    StoredRowState.Available,
                    bytesRead,
                    requestHead,
                    requestBody,
                    responseHead,
                    responseBody,
                    scheme,
                    schemeUnavailable,
                    requestTimestamp,
                    requestTimestampUnavailable,
                    responseTimestamp,
                    responseTimestampUnavailable,
                    clientAddress,
                    clientAddressUnavailable)
                {
                    ClientProcess =
                        ClientProcessRecordCodec.Read(
                            record),
                };
            }

            private static StoredBody ReadBody(
                IDataRecord record,
                string field,
                long maximumBytes,
                ref long bytesRead,
                CancellationToken cancellationToken)
            {
                if (record is IRawBodyDataRecord raw)
                {
                    ReadOnlyMemory<byte> body =
                        raw.RawBody(field);
                    bytesRead = checked(
                        bytesRead +
                        body.Length);
                    return body.Length > maximumBytes
                        ? StoredBody.NotRead(
                            body.Length,
                            undecodable: false)
                        : StoredBody.FromBytes(
                            body.ToArray());
                }

                string text = record.Str(field);
                if (string.IsNullOrEmpty(text))
                {
                    return StoredBody.Empty;
                }

                if (text.Length % 4 != 0)
                {
                    return StoredBody.NotRead(
                        null,
                        undecodable: true);
                }

                bytesRead = checked(
                    bytesRead +
                    ByteCount(text));
                if (!TryDecodedLength(
                        text,
                        out int decodedLength))
                {
                    return StoredBody.NotRead(
                        null,
                        undecodable: true);
                }

                if (decodedLength > maximumBytes)
                {
                    return StoredBody.NotRead(
                        decodedLength,
                        undecodable: false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                byte[] decoded =
                    new byte[decodedLength];
                if (!Convert.TryFromBase64Chars(
                        text,
                        decoded,
                        out int written) ||
                    written != decodedLength)
                {
                    return StoredBody.NotRead(
                        null,
                        undecodable: true);
                }

                return StoredBody.FromBytes(decoded);
            }

            private static string ReadSmallField(
                string value,
                int maximumBytes,
                ref long bytesRead,
                out bool unavailable)
            {
                long bytes = ByteCount(value);
                unavailable = bytes > maximumBytes;
                if (unavailable)
                {
                    return null;
                }

                bytesRead = checked(bytesRead + bytes);
                return value;
            }

            private static bool TooManyStoredHeadLines(
                string head,
                int maximumFieldLines)
            {
                if (string.IsNullOrEmpty(head))
                {
                    return false;
                }

                int lines = 1;
                foreach (char character in head)
                {
                    if (character == '\n' &&
                        ++lines >
                            maximumFieldLines + 1)
                    {
                        return true;
                    }
                }

                return false;
            }

            private static bool TryDecodedLength(
                string text,
                out int decodedLength)
            {
                decodedLength = 0;
                if (text.Length == 0)
                {
                    return true;
                }

                int padding = 0;
                if (text[^1] == '=')
                {
                    padding++;
                    if (text.Length > 1 &&
                        text[^2] == '=')
                    {
                        padding++;
                    }
                }

                for (int index = 0;
                    index < text.Length - padding;
                    index++)
                {
                    char value = text[index];
                    if (!char.IsAsciiLetterOrDigit(value) &&
                        value is not ('+' or '/'))
                    {
                        return false;
                    }
                }

                for (int index =
                        text.Length - padding;
                    index < text.Length;
                    index++)
                {
                    if (text[index] != '=')
                    {
                        return false;
                    }
                }

                decodedLength = checked(
                    (text.Length / 4 * 3) -
                    padding);
                return true;
            }
        }

        private static long ByteCount(string value) =>
            string.IsNullOrEmpty(value)
                ? 0
                : Utf8.GetByteCount(value);
    }

    internal static class SqliteSazExportReader
    {
        private const int SliceBytes = 1024 * 1024;
        private const string DeviceName = "DeviceName";
        private const string RequestNumber = "RequestNumber";
        private const string RequestTimestamp = "RequestTimestamp";
        private const string ResponseTimestamp = "ResponseTimestamp";
        private const string RequestScheme = "RequestScheme";
        private const string RequestHead = "RequestLineAndHeaders";
        private const string RequestBody = "RequestBody";
        private const string ResponseHead = "ResponseLineAndHeaders";
        private const string ResponseBody = "ResponseBody";
        private const string ClientAddress = "ClientIP";
        private const int MaxProcessFieldBytes = 4096;
        private static readonly Encoding Utf8 =
            new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false);

        internal static long? MaxRowId(
            SqliteConnection connection,
            string table,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(connection);
            cancellationToken.ThrowIfCancellationRequested();
            using SqliteCommand command =
                connection.CreateCommand();
            command.CommandText =
                $"SELECT max(id) FROM {Quote(table)}";
            object value = command.ExecuteScalar();
            return value == null ||
                value == DBNull.Value
                ? null
                : Convert.ToInt64(
                    value,
                    System.Globalization
                        .CultureInfo.InvariantCulture);
        }

        internal static SazExportPage ReadPage(
            SqliteConnection connection,
            string table,
            string deviceName,
            long? afterRowId,
            long throughRowId,
            int scan,
            SazExportBudgets budgets,
            SazExportKind kind,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(budgets);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scan);
            cancellationToken.ThrowIfCancellationRequested();
            using SqliteCommand command =
                connection.CreateCommand();
            command.CommandText =
                "SELECT id," +
                $"CASE WHEN typeof({Quote(DeviceName)}) = 'text' AND " +
                $"octet_length({Quote(DeviceName)}) = $nameBytes " +
                $"THEN {Quote(DeviceName)} = $name ELSE 0 END," +
                $"CASE WHEN typeof({Quote(RequestNumber)}) = 'integer' " +
                $"THEN {Quote(RequestNumber)} ELSE NULL END," +
                ValueColumns() +
                $" FROM {Quote(table)}" +
                " WHERE ($after IS NULL OR id > $after)" +
                " AND id <= $through" +
                " ORDER BY id LIMIT $scan";
            command.Parameters.AddWithValue(
                "$nameBytes",
                Utf8.GetByteCount(
                    deviceName ?? string.Empty));
            command.Parameters.AddWithValue(
                "$name",
                deviceName ?? string.Empty);
            command.Parameters.AddWithValue(
                "$after",
                afterRowId.HasValue
                    ? afterRowId.Value
                    : DBNull.Value);
            command.Parameters.AddWithValue(
                "$through",
                throughRowId);
            command.Parameters.AddWithValue(
                "$scan",
                scan);
            var keys = new List<SazExportKey>();
            long? last = afterRowId;
            using SqliteDataReader reader =
                command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                long rowId = reader.GetInt64(0);
                last = rowId;
                if (reader.GetInt64(1) == 0)
                {
                    continue;
                }

                long? number = reader.IsDBNull(2)
                    ? null
                    : reader.GetInt64(2);
                RowInformation information =
                    ReadInformation(
                        reader,
                        rowId,
                        number,
                        3);
                keys.Add(
                    new(
                        rowId,
                        number,
                        PlannedBytes(
                            information,
                            budgets,
                            kind)));
            }

            return new(keys, last);
        }

        internal static StoredRow ReadRow(
            SqliteConnection connection,
            string table,
            SazExportKey key,
            SazExportBudgets budgets,
            SazExportKind kind,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(budgets);
            cancellationToken.ThrowIfCancellationRequested();
            RowInformation information =
                ReadInformation(
                    connection,
                    table,
                    key,
                    cancellationToken);
            if (information == null)
            {
                return StoredRow.Unreadable(
                    key.RowId,
                    key.RequestNumber);
            }

            if (!information.RequestHead.IsTextOrNull ||
                !information.ResponseHead.IsTextOrNull)
            {
                return StoredRow.Unreadable(
                    key.RowId,
                    key.RequestNumber);
            }

            if (information.RequestHead.Bytes >
                    budgets.MaxHeadReadBytes ||
                information.ResponseHead.Bytes >
                    budgets.MaxHeadReadBytes)
            {
                return StoredRow.Oversized(
                    key.RowId,
                    key.RequestNumber);
            }

            long bytesRead = 0;
            string requestHead = ReadText(
                connection,
                table,
                RequestHead,
                key.RowId,
                information.RequestHead,
                cancellationToken);
            bytesRead = checked(
                bytesRead +
                information.RequestHead.Bytes);
            if (TooManyStoredHeadLines(
                requestHead,
                budgets.MaxHeadFieldLines))
            {
                return StoredRow.Oversized(
                    key.RowId,
                    key.RequestNumber,
                    bytesRead);
            }

            if (!HasStartLine(requestHead))
            {
                return StoredRow.WithRequestHeadOnly(
                    key.RowId,
                    key.RequestNumber,
                    bytesRead,
                    requestHead);
            }

            string responseHead = ReadText(
                connection,
                table,
                ResponseHead,
                key.RowId,
                information.ResponseHead,
                cancellationToken);
            bytesRead = checked(
                bytesRead +
                information.ResponseHead.Bytes);
            if (TooManyStoredHeadLines(
                responseHead,
                budgets.MaxHeadFieldLines))
            {
                return StoredRow.Oversized(
                    key.RowId,
                    key.RequestNumber,
                    bytesRead);
            }

            StoredBody requestBody = ReadBody(
                connection,
                table,
                RequestBody,
                key.RowId,
                information.RequestBody,
                budgets.MaxBodyBytes,
                kind,
                ref bytesRead,
                cancellationToken);
            StoredBody responseBody = ReadBody(
                connection,
                table,
                ResponseBody,
                key.RowId,
                information.ResponseBody,
                budgets.MaxBodyBytes,
                kind,
                ref bytesRead,
                cancellationToken);
            string scheme = ReadSmallField(
                connection,
                table,
                RequestScheme,
                key.RowId,
                information.Scheme,
                budgets.MaxSchemeBytes,
                ref bytesRead,
                out bool schemeUnavailable,
                cancellationToken);
            string requestTimestamp = ReadSmallField(
                connection,
                table,
                RequestTimestamp,
                key.RowId,
                information.RequestTimestamp,
                budgets.MaxTimeBytes,
                ref bytesRead,
                out bool requestTimestampUnavailable,
                cancellationToken);
            string responseTimestamp = ReadSmallField(
                connection,
                table,
                ResponseTimestamp,
                key.RowId,
                information.ResponseTimestamp,
                budgets.MaxTimeBytes,
                ref bytesRead,
                out bool responseTimestampUnavailable,
                cancellationToken);
            string clientAddress = ReadSmallField(
                connection,
                table,
                ClientAddress,
                key.RowId,
                information.ClientAddress,
                budgets.MaxClientAddressBytes,
                ref bytesRead,
                out bool clientAddressUnavailable,
                cancellationToken);
            return new StoredRow(
                key.RowId,
                key.RequestNumber,
                StoredRowState.Available,
                bytesRead,
                requestHead,
                requestBody,
                responseHead,
                responseBody,
                scheme,
                schemeUnavailable,
                requestTimestamp,
                requestTimestampUnavailable,
                responseTimestamp,
                responseTimestampUnavailable,
                clientAddress,
                clientAddressUnavailable)
            {
                ClientProcess =
                    ReadClientProcess(
                        connection,
                        table,
                        key.RowId,
                        cancellationToken),
            };
        }

        // Captures saved before process attribution have no process columns; their sessions stay unattributed.
        private static ClientProcessInfo ReadClientProcess(
            SqliteConnection connection,
            string table,
            long rowId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!HasProcessColumns(
                connection,
                table))
            {
                return ClientProcessInfo.NotRecorded;
            }

            using SqliteCommand command =
                connection.CreateCommand();
            command.CommandText =
                "SELECT " +
                BoundedText(WebProxyCaptureSchema.ClientProcessName) + "," +
                $"CASE WHEN typeof({Quote(WebProxyCaptureSchema.ClientProcessId)}) = 'integer' " +
                $"THEN {Quote(WebProxyCaptureSchema.ClientProcessId)} ELSE NULL END," +
                BoundedText(WebProxyCaptureSchema.ClientProcessStartTime) + "," +
                BoundedText(WebProxyCaptureSchema.ClientProcessStatus) +
                $" FROM {Quote(table)} WHERE id = $id";
            command.Parameters.AddWithValue(
                "$id",
                rowId);
            using SqliteDataReader reader =
                command.ExecuteReader();
            if (!reader.Read())
            {
                return ClientProcessInfo.NotRecorded;
            }

            return ClientProcessRecordCodec.FromValues(
                reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3));
        }

        private static string BoundedText(
            string column) =>
            $"CASE WHEN typeof({Quote(column)}) = 'text' AND " +
            $"length(CAST({Quote(column)} AS BLOB)) <= {MaxProcessFieldBytes} " +
            $"THEN {Quote(column)} ELSE NULL END";

        private static bool HasProcessColumns(
            SqliteConnection connection,
            string table)
        {
            using SqliteCommand command =
                connection.CreateCommand();
            command.CommandText =
                "SELECT count(*) FROM pragma_table_info($table) " +
                "WHERE name IN ($name,$id,$start,$status)";
            command.Parameters.AddWithValue(
                "$table",
                table);
            command.Parameters.AddWithValue(
                "$name",
                WebProxyCaptureSchema.ClientProcessName);
            command.Parameters.AddWithValue(
                "$id",
                WebProxyCaptureSchema.ClientProcessId);
            command.Parameters.AddWithValue(
                "$start",
                WebProxyCaptureSchema.ClientProcessStartTime);
            command.Parameters.AddWithValue(
                "$status",
                WebProxyCaptureSchema.ClientProcessStatus);
            return Convert.ToInt64(
                    command.ExecuteScalar(),
                    System.Globalization
                        .CultureInfo.InvariantCulture) ==
                4;
        }

        private static RowInformation ReadInformation(
            SqliteConnection connection,
            string table,
            SazExportKey key,
            CancellationToken cancellationToken)
        {
            using SqliteCommand command =
                connection.CreateCommand();
            command.CommandText =
                "SELECT " +
                $"CASE WHEN typeof({Quote(RequestNumber)}) = 'integer' " +
                $"THEN {Quote(RequestNumber)} ELSE NULL END," +
                ValueColumns() +
                $" FROM {Quote(table)} WHERE id = $id";
            command.Parameters.AddWithValue(
                "$id",
                key.RowId);
            using SqliteDataReader reader =
                command.ExecuteReader();
            cancellationToken.ThrowIfCancellationRequested();
            if (!reader.Read())
            {
                return null;
            }

            long? number = reader.IsDBNull(0)
                ? null
                : reader.GetInt64(0);
            return ReadInformation(
                reader,
                key.RowId,
                number,
                1);
        }

        private static RowInformation ReadInformation(
            SqliteDataReader reader,
            long rowId,
            long? requestNumber,
            int offset) =>
            new(
                rowId,
                requestNumber,
                Value(reader, offset),
                Value(reader, offset + 2),
                Value(reader, offset + 4),
                Value(reader, offset + 6),
                Value(reader, offset + 8),
                Value(reader, offset + 10),
                Value(reader, offset + 12),
                Value(reader, offset + 14));

        private static ValueInformation Value(
            SqliteDataReader reader,
            int offset) =>
            new(
                reader.GetString(offset),
                reader.IsDBNull(offset + 1)
                    ? 0
                    : reader.GetInt64(offset + 1));

        private static string ValueColumns() =>
            string.Join(
                ",",
                new[]
                {
                    ValueColumn(RequestHead),
                    ValueColumn(RequestBody),
                    ValueColumn(ResponseHead),
                    ValueColumn(ResponseBody),
                    ValueColumn(RequestScheme),
                    ValueColumn(RequestTimestamp),
                    ValueColumn(ResponseTimestamp),
                    ValueColumn(ClientAddress),
                });

        private static string ValueColumn(string name) =>
            $"typeof({Quote(name)}),octet_length({Quote(name)})";

        private static long PlannedBytes(
            RowInformation information,
            SazExportBudgets budgets,
            SazExportKind kind)
        {
            _ = kind;
            if (!information.RequestHead.IsTextOrNull ||
                !information.ResponseHead.IsTextOrNull ||
                information.RequestHead.Bytes >
                    budgets.MaxHeadReadBytes ||
                information.ResponseHead.Bytes >
                    budgets.MaxHeadReadBytes)
            {
                return 0;
            }

            long total = checked(
                information.RequestHead.Bytes +
                information.ResponseHead.Bytes);
            total = checked(
                total +
                PlannedBodyBytes(
                    information.RequestBody));
            total = checked(
                total +
                PlannedBodyBytes(
                    information.ResponseBody));
            total = checked(
                total +
                PlannedSmallBytes(
                    information.Scheme,
                    budgets.MaxSchemeBytes));
            total = checked(
                total +
                PlannedSmallBytes(
                    information.RequestTimestamp,
                    budgets.MaxTimeBytes));
            total = checked(
                total +
                PlannedSmallBytes(
                    information.ResponseTimestamp,
                    budgets.MaxTimeBytes));
            return checked(
                total +
                PlannedSmallBytes(
                    information.ClientAddress,
                    budgets.MaxClientAddressBytes));
        }

        private static long PlannedBodyBytes(
            ValueInformation value) =>
            value.IsNull ||
            !value.IsText ||
            value.Bytes == 0 ||
            value.Bytes % 4 != 0
                ? 0
                : value.Bytes;

        private static long PlannedSmallBytes(
            ValueInformation value,
            int maximumBytes) =>
            value.IsText &&
            value.Bytes <= maximumBytes
                ? value.Bytes
                : 0;

        private static string ReadSmallField(
            SqliteConnection connection,
            string table,
            string column,
            long rowId,
            ValueInformation information,
            int maximumBytes,
            ref long bytesRead,
            out bool unavailable,
            CancellationToken cancellationToken)
        {
            unavailable = !information.IsNull &&
                (!information.IsText ||
                 information.Bytes > maximumBytes);
            if (information.IsNull ||
                unavailable)
            {
                return null;
            }

            string value = ReadText(
                connection,
                table,
                column,
                rowId,
                information,
                cancellationToken);
            bytesRead = checked(
                bytesRead +
                information.Bytes);
            return value;
        }

        private static string ReadText(
            SqliteConnection connection,
            string table,
            string column,
            long rowId,
            ValueInformation information,
            CancellationToken cancellationToken)
        {
            if (information.IsNull)
            {
                return null;
            }

            byte[] bytes = ReadBlob(
                connection,
                table,
                column,
                rowId,
                checked((int)information.Bytes),
                cancellationToken);
            return Utf8.GetString(bytes);
        }

        private static StoredBody ReadBody(
            SqliteConnection connection,
            string table,
            string column,
            long rowId,
            ValueInformation information,
            long maximumBytes,
            SazExportKind kind,
            ref long bytesRead,
            CancellationToken cancellationToken)
        {
            if (information.IsNull ||
                information.Bytes == 0)
            {
                return StoredBody.Empty;
            }

            if (!information.IsText ||
                information.Bytes % 4 != 0)
            {
                return StoredBody.NotRead(
                    null,
                    undecodable: true);
            }

            int tailLength = checked(
                (int)Math.Min(
                    2,
                    information.Bytes));
            byte[] tail = ReadBlobTail(
                connection,
                table,
                column,
                rowId,
                tailLength,
                cancellationToken);
            int padding = 0;
            for (int index = tail.Length - 1;
                index >= 0 &&
                tail[index] == (byte)'=' &&
                padding < 2;
                index--)
            {
                padding++;
            }

            long decodedLength = checked(
                information.Bytes / 4 * 3 -
                padding);
            if (decodedLength > maximumBytes)
            {
                if (kind == SazExportKind.Sanitized)
                {
                    bytesRead = checked(
                        bytesRead +
                        tailLength);
                    return StoredBody.NotRead(
                        decodedLength,
                        undecodable: false);
                }

                bool valid = DecodeBody(
                    connection,
                    table,
                    column,
                    rowId,
                    information.Bytes,
                    null,
                    cancellationToken);
                bytesRead = checked(
                    bytesRead +
                    information.Bytes);
                return StoredBody.NotRead(
                    valid
                        ? decodedLength
                        : null,
                    undecodable: !valid);
            }

            byte[] body =
                new byte[checked((int)decodedLength)];
            bool decoded = DecodeBody(
                connection,
                table,
                column,
                rowId,
                information.Bytes,
                body,
                cancellationToken);
            bytesRead = checked(
                bytesRead +
                information.Bytes);
            return decoded
                ? StoredBody.FromBytes(body)
                : StoredBody.NotRead(
                    null,
                    undecodable: true);
        }

        private static bool DecodeBody(
            SqliteConnection connection,
            string table,
            string column,
            long rowId,
            long encodedBytes,
            byte[] output,
            CancellationToken cancellationToken)
        {
            byte[] input =
                ArrayPool<byte>.Shared.Rent(
                    SliceBytes);
            byte[] scratch =
                ArrayPool<byte>.Shared.Rent(
                    SliceBytes);
            try
            {
                using var blob = new SqliteBlob(
                    connection,
                    table,
                    column,
                    rowId,
                    readOnly: true);
                long remaining = encodedBytes;
                int outputOffset = 0;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = checked(
                        (int)Math.Min(
                            SliceBytes,
                            remaining));
                    if (remaining > count)
                    {
                        count -= count % 4;
                    }

                    ReadExactly(
                        blob,
                        input,
                        count);
                    bool final = remaining == count;
                    Span<byte> destination =
                        output == null
                            ? scratch.AsSpan()
                            : output.AsSpan(
                                outputOffset);
                    OperationStatus status =
                        Base64.DecodeFromUtf8(
                            input.AsSpan(
                                0,
                                count),
                            destination,
                            out int consumed,
                            out int written,
                            final);
                    if (status !=
                            OperationStatus.Done ||
                        consumed != count)
                    {
                        return false;
                    }

                    outputOffset = checked(
                        outputOffset +
                        written);
                    remaining -= count;
                }

                return output == null ||
                    outputOffset == output.Length;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(input);
                ArrayPool<byte>.Shared.Return(scratch);
            }
        }

        private static byte[] ReadBlob(
            SqliteConnection connection,
            string table,
            string column,
            long rowId,
            int length,
            CancellationToken cancellationToken)
        {
            if (length == 0)
            {
                return Array.Empty<byte>();
            }

            using var blob = new SqliteBlob(
                connection,
                table,
                column,
                rowId,
                readOnly: true);
            var bytes = new byte[length];
            int offset = 0;
            while (offset < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = Math.Min(
                    SliceBytes,
                    bytes.Length - offset);
                int read = blob.Read(
                    bytes,
                    offset,
                    count);
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }

                offset += read;
            }

            return bytes;
        }

        private static byte[] ReadBlobTail(
            SqliteConnection connection,
            string table,
            string column,
            long rowId,
            int length,
            CancellationToken cancellationToken)
        {
            using var blob = new SqliteBlob(
                connection,
                table,
                column,
                rowId,
                readOnly: true);
            blob.Seek(
                -length,
                System.IO.SeekOrigin.End);
            var bytes = new byte[length];
            cancellationToken.ThrowIfCancellationRequested();
            ReadExactly(
                blob,
                bytes,
                length);
            return bytes;
        }

        private static void ReadExactly(
            SqliteBlob blob,
            byte[] buffer,
            int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = blob.Read(
                    buffer,
                    offset,
                    count - offset);
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }

                offset += read;
            }
        }

        private static bool TooManyStoredHeadLines(
            string head,
            int maximumFieldLines)
        {
            if (string.IsNullOrEmpty(head))
            {
                return false;
            }

            int lines = 1;
            foreach (char character in head)
            {
                if (character == '\n' &&
                    ++lines >
                        maximumFieldLines + 1)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasStartLine(string head)
        {
            if (string.IsNullOrEmpty(head))
            {
                return false;
            }

            int end = head.IndexOf('\n');
            if (end < 0)
            {
                end = head.Length;
            }

            if (end > 0 &&
                head[end - 1] == '\r')
            {
                end--;
            }

            return end > 0;
        }

        private static string Quote(string value) =>
            "\"" +
            value.Replace(
                "\"",
                "\"\"",
                StringComparison.Ordinal) +
            "\"";

        private readonly record struct ValueInformation(
            string Type,
            long Bytes)
        {
            internal bool IsNull =>
                Type == "null";

            internal bool IsText =>
                Type == "text";

            internal bool IsTextOrNull =>
                IsText || IsNull;
        }

        private sealed record RowInformation(
            long RowId,
            long? RequestNumber,
            ValueInformation RequestHead,
            ValueInformation RequestBody,
            ValueInformation ResponseHead,
            ValueInformation ResponseBody,
            ValueInformation Scheme,
            ValueInformation RequestTimestamp,
            ValueInformation ResponseTimestamp,
            ValueInformation ClientAddress);
    }

    internal enum StoredRowState
    {
        Available,
        Unreadable,
        Oversized,
    }

    internal enum StoredBodyState
    {
        Empty,
        Bytes,
        NotRead,
    }

    internal sealed class StoredBody
    {
        private StoredBody(
            StoredBodyState state,
            byte[] bytes,
            long? knownLength,
            bool undecodable)
        {
            State = state;
            Bytes = bytes ?? Array.Empty<byte>();
            KnownLength = knownLength;
            Undecodable = undecodable;
        }

        internal static StoredBody Empty { get; } =
            new(
                StoredBodyState.Empty,
                Array.Empty<byte>(),
                0,
                false);

        internal static StoredBody FromBytes(byte[] bytes)
        {
            ArgumentNullException.ThrowIfNull(bytes);
            return bytes.Length == 0
                ? Empty
                : new StoredBody(
                    StoredBodyState.Bytes,
                    bytes,
                    bytes.Length,
                    false);
        }

        internal static StoredBody NotRead(
            long? knownLength,
            bool undecodable) =>
            new(
                StoredBodyState.NotRead,
                Array.Empty<byte>(),
                knownLength,
                undecodable);

        internal StoredBodyState State { get; }

        internal byte[] Bytes { get; }

        internal long? KnownLength { get; }

        internal bool Undecodable { get; }

        internal bool HasContent =>
            State != StoredBodyState.Empty;
    }

    internal sealed class StoredRow
    {
        internal StoredRow(
            long rowId,
            long? requestNumber,
            StoredRowState state,
            long bytesRead,
            string requestHead,
            StoredBody requestBody,
            string responseHead,
            StoredBody responseBody,
            string scheme,
            bool schemeUnavailable,
            string requestTimestamp,
            bool requestTimestampUnavailable,
            string responseTimestamp,
            bool responseTimestampUnavailable,
            string clientAddress,
            bool clientAddressUnavailable)
        {
            RowId = rowId;
            RequestNumber = requestNumber;
            State = state;
            BytesRead = bytesRead;
            RequestHead = requestHead;
            RequestBody = requestBody ?? StoredBody.Empty;
            ResponseHead = responseHead;
            ResponseBody = responseBody ?? StoredBody.Empty;
            Scheme = scheme;
            SchemeUnavailable = schemeUnavailable;
            RequestTimestamp = requestTimestamp;
            RequestTimestampUnavailable =
                requestTimestampUnavailable;
            ResponseTimestamp = responseTimestamp;
            ResponseTimestampUnavailable =
                responseTimestampUnavailable;
            ClientAddress = clientAddress;
            ClientAddressUnavailable =
                clientAddressUnavailable;
        }

        internal long RowId { get; }

        internal long? RequestNumber { get; }

        internal StoredRowState State { get; }

        internal long BytesRead { get; }

        internal string RequestHead { get; }

        internal StoredBody RequestBody { get; }

        internal string ResponseHead { get; }

        internal StoredBody ResponseBody { get; }

        internal string Scheme { get; }

        internal bool SchemeUnavailable { get; }

        internal string RequestTimestamp { get; }

        internal bool RequestTimestampUnavailable { get; }

        internal string ResponseTimestamp { get; }

        internal bool ResponseTimestampUnavailable { get; }

        internal string ClientAddress { get; }

        internal bool ClientAddressUnavailable { get; }

        internal ClientProcessInfo ClientProcess { get; init; } =
            ClientProcessInfo.NotRecorded;
        internal static StoredRow Oversized(
            long rowId,
            long? requestNumber,
            long bytesRead = 0) =>
            WithState(
                rowId,
                requestNumber,
                StoredRowState.Oversized,
                bytesRead);

        internal static StoredRow Unreadable(
            long rowId,
            long? requestNumber,
            long bytesRead = 0) =>
            WithState(
                rowId,
                requestNumber,
                StoredRowState.Unreadable,
                bytesRead);

        internal static StoredRow WithRequestHeadOnly(
            long rowId,
            long? requestNumber,
            long bytesRead,
            string requestHead) =>
            new(
                rowId,
                requestNumber,
                StoredRowState.Available,
                bytesRead,
                requestHead,
                StoredBody.Empty,
                null,
                StoredBody.Empty,
                null,
                false,
                null,
                false,
                null,
                false,
                null,
                false);

        private static StoredRow WithState(
            long rowId,
            long? requestNumber,
            StoredRowState state,
            long bytesRead) =>
            new(
                rowId,
                requestNumber,
                state,
                bytesRead,
                null,
                StoredBody.Empty,
                null,
                StoredBody.Empty,
                null,
                false,
                null,
                false,
                null,
                false,
                null,
                false);
    }
}
