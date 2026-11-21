// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XMAT.SharedInterfaces;

namespace XMAT.ApplicationServices
{
    internal static class QueryResultExporter
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        internal static async Task WriteAsync(
            IQueryResult result,
            Stream destination,
            QueryExportFormat format,
            long maximumBytes,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(result);
            ArgumentNullException.ThrowIfNull(destination);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
            cancellationToken.ThrowIfCancellationRequested();
            using var bounded = new BoundedOutput(destination, maximumBytes);
            if (format == QueryExportFormat.Csv)
            {
                await WriteCsvAsync(result, bounded, cancellationToken);
            }
            else if (format == QueryExportFormat.Json)
            {
                await WriteJsonAsync(result, bounded, cancellationToken);
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(format));
            }
        }

        private static async Task WriteCsvAsync(IQueryResult result, Stream destination, CancellationToken cancellationToken)
        {
            await using var writer = new StreamWriter(destination, new UTF8Encoding(false), leaveOpen: true);
            for (var index = 0; index < result.Columns.Count; index++)
            {
                if (index != 0)
                    await writer.WriteAsync(",");
                await QuotedAsync(writer, result.Columns[index].Name, cancellationToken);
            }
            await writer.WriteAsync("\r\n");
            long offset = 0;
            while (offset < result.RowCount)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await result.ReadPageAsync(offset, 1000, 1024 * 1024, cancellationToken);
                foreach (var row in page.Rows)
                {
                    for (var index = 0; index < row.Cells.Count; index++)
                    {
                        if (index != 0)
                            await writer.WriteAsync(",");
                        var cell = row.Cells[index];
                        switch (cell.Kind)
                        {
                            case QueryValueKind.Null:
                                break;
                            case QueryValueKind.Integer:
                                await writer.WriteAsync(((long)cell.Value).ToString(CultureInfo.InvariantCulture));
                                break;
                            case QueryValueKind.Real:
                                await writer.WriteAsync(((double)cell.Value).ToString("R", CultureInfo.InvariantCulture));
                                break;
                            case QueryValueKind.Text:
                                await writer.WriteAsync("\"");
                                if (cell.IsTruncated)
                                {
                                    await TextChunksAsync(result, row.Ordinal, index, async (chars, _) =>
                                        await EscapedCsvAsync(writer, chars, cancellationToken), cancellationToken);
                                }
                                else
                                {
                                    await EscapedCsvAsync(writer, ((string)cell.Value).AsMemory(), cancellationToken);
                                }
                                await writer.WriteAsync("\"");
                                break;
                            case QueryValueKind.Blob:
                                if (cell.IsTruncated)
                                {
                                    await Base64CsvAsync(result, row.Ordinal, index, writer, cancellationToken);
                                }
                                else
                                {
                                    await writer.WriteAsync(Convert.ToBase64String((byte[])cell.Value));
                                }
                                break;
                            default:
                                throw new QueryException(QueryError.InvalidValue, "Unsupported export value.");
                        }
                    }
                    await writer.WriteAsync("\r\n");
                }
                if (page.Rows.Count == 0)
                    throw new InvalidDataException("A result page made no progress.");
                offset += page.Rows.Count;
                await writer.FlushAsync(cancellationToken);
            }
            await writer.FlushAsync(cancellationToken);
        }

        private static async Task WriteJsonAsync(IQueryResult result, Stream destination, CancellationToken cancellationToken)
        {
            await using var writer = new Utf8JsonWriter(destination);
            writer.WriteStartObject();
            writer.WritePropertyName("columns");
            JsonSerializer.Serialize(writer, result.Columns, JsonOptions);
            writer.WritePropertyName("rows");
            writer.WriteStartArray();
            long offset = 0;
            while (offset < result.RowCount)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await result.ReadPageAsync(offset, 1000, 1024 * 1024, cancellationToken);
                foreach (var row in page.Rows)
                {
                    writer.WriteStartArray();
                    for (var index = 0; index < row.Cells.Count; index++)
                    {
                        var cell = row.Cells[index];
                        switch (cell.Kind)
                        {
                            case QueryValueKind.Null:
                                writer.WriteNullValue();
                                break;
                            case QueryValueKind.Integer:
                                writer.WriteNumberValue((long)cell.Value);
                                break;
                            case QueryValueKind.Real when double.IsFinite((double)cell.Value):
                                writer.WriteNumberValue((double)cell.Value);
                                break;
                            case QueryValueKind.Real:
                                writer.WriteStartObject();
                                writer.WriteString("$type", "real");
                                writer.WriteString("value", ((double)cell.Value).ToString("R", CultureInfo.InvariantCulture));
                                writer.WriteEndObject();
                                break;
                            case QueryValueKind.Text:
                                if (cell.IsTruncated)
                                {
                                    await TextChunksAsync(result, row.Ordinal, index, async (chars, final) =>
                                    {
                                        writer.WriteStringValueSegment(chars.Span, final);
                                        await writer.FlushAsync(cancellationToken);
                                    }, cancellationToken);
                                }
                                else
                                {
                                    writer.WriteStringValue((string)cell.Value);
                                }
                                break;
                            case QueryValueKind.Blob:
                                writer.WriteStartObject();
                                writer.WriteString("$type", "blob");
                                writer.WritePropertyName("base64");
                                if (cell.IsTruncated)
                                {
                                    long position = 0;
                                    QueryValueChunk chunk;
                                    do
                                    {
                                        chunk = await result.ReadValueAsync(row.Ordinal, index, position, 4096, cancellationToken);
                                        writer.WriteBase64StringSegment(chunk.Bytes, !chunk.HasMore);
                                        await writer.FlushAsync(cancellationToken);
                                        position += chunk.Bytes.Length;
                                    } while (chunk.HasMore);
                                }
                                else
                                {
                                    writer.WriteBase64StringValue((byte[])cell.Value);
                                }
                                writer.WriteEndObject();
                                break;
                            default:
                                throw new QueryException(QueryError.InvalidValue, "Unsupported export value.");
                        }
                    }
                    writer.WriteEndArray();
                }
                if (page.Rows.Count == 0)
                    throw new InvalidDataException("A result page made no progress.");
                offset += page.Rows.Count;
                await writer.FlushAsync(cancellationToken);
            }
            writer.WriteEndArray();
            writer.WriteNumber("rowCount", result.RowCount);
            writer.WriteBoolean("complete", true);
            writer.WriteEndObject();
            await writer.FlushAsync(cancellationToken);
        }

        private static async Task TextChunksAsync(
            IQueryResult result, long row, int column,
            Func<ReadOnlyMemory<char>, bool, Task> write, CancellationToken cancellationToken)
        {
            var decoder = Encoding.UTF8.GetDecoder();
            var chars = new char[Encoding.UTF8.GetMaxCharCount(4096)];
            long position = 0;
            QueryValueChunk chunk;
            do
            {
                chunk = await result.ReadValueAsync(row, column, position, 4096, cancellationToken);
                var length = decoder.GetChars(chunk.Bytes, chars, flush: !chunk.HasMore);
                await write(chars.AsMemory(0, length), !chunk.HasMore);
                position += chunk.Bytes.Length;
            } while (chunk.HasMore);
        }

        private static async Task Base64CsvAsync(
            IQueryResult result, long row, int column, TextWriter writer, CancellationToken cancellationToken)
        {
            var buffer = new byte[4098];
            var carry = 0;
            long position = 0;
            QueryValueChunk chunk;
            do
            {
                chunk = await result.ReadValueAsync(row, column, position, 4096, cancellationToken);
                chunk.Bytes.CopyTo(buffer, carry);
                var length = carry + chunk.Bytes.Length;
                var encode = chunk.HasMore ? length - length % 3 : length;
                await writer.WriteAsync(Convert.ToBase64String(buffer, 0, encode));
                carry = length - encode;
                Array.Copy(buffer, encode, buffer, 0, carry);
                position += chunk.Bytes.Length;
            } while (chunk.HasMore);
        }

        private static async Task QuotedAsync(TextWriter writer, string value, CancellationToken cancellationToken)
        {
            await writer.WriteAsync("\"");
            await EscapedCsvAsync(writer, (value ?? string.Empty).AsMemory(), cancellationToken);
            await writer.WriteAsync("\"");
        }

        private static async Task EscapedCsvAsync(TextWriter writer, ReadOnlyMemory<char> value, CancellationToken cancellationToken)
        {
            var position = 0;
            while (position < value.Length)
            {
                var quote = value.Span[position..].IndexOf('"');
                if (quote < 0)
                {
                    await writer.WriteAsync(value[position..], cancellationToken);
                    break;
                }
                await writer.WriteAsync(value.Slice(position, quote), cancellationToken);
                await writer.WriteAsync("\"\"");
                position += quote + 1;
            }
        }

        private sealed class BoundedOutput(Stream destination, long maximumBytes) : Stream
        {
            private long _written;
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => _written;
            public override long Position { get => _written; set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Flush() => destination.Flush();
            public override Task FlushAsync(CancellationToken cancellationToken) => destination.FlushAsync(cancellationToken);
            public override void Write(byte[] buffer, int offset, int count)
            {
                Check(count);
                destination.Write(buffer, offset, count);
            }
            public override void Write(ReadOnlySpan<byte> buffer)
            {
                Check(buffer.Length);
                destination.Write(buffer);
            }
            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Check(buffer.Length);
                await destination.WriteAsync(buffer, cancellationToken);
            }
            private void Check(int count)
            {
                if (count > maximumBytes - _written)
                {
                    throw new QueryException(QueryError.ResultLimit, "The export exceeded its byte budget.");
                }
                _written += count;
            }
        }
    }
}
