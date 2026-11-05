// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;

namespace XMAT.Operations.Replay
{
    // diff.json: one verification detail (VerifyDiff), schema "xmat.verify-diff", version 1, enum values in camel case.
    // It is written through a buffer that refuses to hold more than the cap and a writer's room for one token, so a detail
    // too large for the cap costs at most that much memory before it is refused.
    internal static class VerifyDiffJson
    {
        private static readonly JsonWriterOptions Options = new() { Indented = true };

        // The detail as diff.json, or null when it is longer than maxBytes.
        internal static byte[] Serialize(VerifyDiff diff, int maxBytes = int.MaxValue)
        {
            ArgumentNullException.ThrowIfNull(diff);
            var output = new CappedBuffer(maxBytes);
            try
            {
                using (var writer = new Utf8JsonWriter(output, Options))
                {
                    Write(writer, diff);
                }
            }
            catch (CapReachedException)
            {
                return null;
            }

            return output.ToArray();
        }

        private static void Write(Utf8JsonWriter writer, VerifyDiff diff)
        {
            writer.WriteStartObject();
            writer.WriteString("schema", VerifyDiff.Schema);
            writer.WriteNumber("version", VerifyDiff.SchemaVersion);
            writer.WriteString("state", Name(diff.State));
            WriteStrings(writer, "limitations", diff.Limitations);
            writer.WriteString("result", diff.Verification.Matches ? "match" : "mismatch");
            WriteStrings(writer, "differences", diff.Verification.Differences);
            writer.WriteStartObject("status");
            if (diff.OriginalStatus is int originalStatus)
            {
                writer.WriteNumber("original", originalStatus);
            }
            else
            {
                writer.WriteNull("original");
            }

            writer.WriteNumber("actual", diff.ActualStatus);
            writer.WriteEndObject();
            WriteSide(writer, "original", diff.Original);
            WriteSide(writer, "actual", diff.Actual);
            if (diff.Body == null)
            {
                writer.WriteNull("body");
            }
            else
            {
                WriteBody(writer, diff.Body);
            }

            writer.WriteEndObject();
        }

        private static void WriteSide(Utf8JsonWriter writer, string name, VerifyDiffSide side)
        {
            if (side == null)
            {
                writer.WriteNull(name);
                return;
            }

            writer.WriteStartObject(name);
            writer.WriteString("basis", Name(side.Basis));
            writer.WriteString("framing", Name(side.Framing));
            writer.WriteString("end", Name(side.End));
            writer.WriteNumber("comparedBytes", side.ComparedBytes);
            writer.WriteNumber("examinedBytes", side.ExaminedBytes);
            writer.WriteString("charset", side.Charset);
            writer.WriteEndObject();
        }

        private static void WriteBody(Utf8JsonWriter writer, VerifyBodyDiff body)
        {
            writer.WriteStartObject("body");
            writer.WriteBoolean("equal", body.Equal);
            if (body.Text == null)
            {
                writer.WriteNull("text");
            }
            else
            {
                writer.WriteStartObject("text");
                writer.WriteString("equivalence", Name(body.Text.Equivalence));
                writer.WriteStartArray("hunks");
                foreach (VerifyTextHunk hunk in body.Text.Hunks)
                {
                    writer.WriteStartObject();
                    WriteSpan(writer, "original", hunk.OriginalStart, hunk.OriginalCount);
                    WriteSpan(writer, "actual", hunk.ActualStart, hunk.ActualCount);
                    writer.WriteStartArray("lines");
                    foreach (VerifyTextLine line in hunk.Lines)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("kind", Name(line.Kind));
                        writer.WriteString("text", line.Text);
                        writer.WriteString("break", line.Break);
                        writer.WriteBoolean("cut", line.Cut);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            if (body.Binary == null)
            {
                writer.WriteNull("binary");
            }
            else
            {
                writer.WriteStartObject("binary");
                writer.WriteNumber("originalLength", body.Binary.OriginalLength);
                writer.WriteNumber("actualLength", body.Binary.ActualLength);
                writer.WriteStartArray("ranges");
                foreach (VerifyByteRange range in body.Binary.Ranges)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("offset", range.Offset);
                    writer.WriteNumber("originalLength", range.OriginalLength);
                    writer.WriteNumber("actualLength", range.ActualLength);
                    writer.WriteString("original", range.Original);
                    writer.WriteString("actual", range.Actual);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            if (body.Json == null)
            {
                writer.WriteNull("json");
            }
            else
            {
                writer.WriteStartObject("json");
                writer.WriteString("values", Name(body.Json.Values));
                WriteStrings(writer, "explanations", body.Json.Explanations);
                writer.WriteStartArray("changes");
                foreach (VerifyJsonChange change in body.Json.Changes)
                {
                    writer.WriteStartObject();
                    writer.WriteString("path", change.Path);
                    writer.WriteBoolean("pathCut", change.PathCut);
                    writer.WriteString("kind", Name(change.Kind));
                    writer.WriteString("original", change.Original);
                    writer.WriteBoolean("originalCut", change.OriginalCut);
                    writer.WriteString("actual", change.Actual);
                    writer.WriteBoolean("actualCut", change.ActualCut);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteString("jsonUnavailable", body.JsonUnavailable);
            writer.WriteEndObject();
        }

        private static void WriteSpan(Utf8JsonWriter writer, string name, int start, int count)
        {
            writer.WriteStartObject(name);
            writer.WriteNumber("start", start);
            writer.WriteNumber("count", count);
            writer.WriteEndObject();
        }

        private static void WriteStrings(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
        {
            writer.WriteStartArray(name);
            foreach (string value in values)
            {
                writer.WriteStringValue(value);
            }

            writer.WriteEndArray();
        }

        private static string Name<T>(T value)
            where T : struct, Enum =>
            JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

        // At most max bytes written, and the room a writer asks for beyond them to write one more token (its largest is a
        // value of MaxValueChars characters, escaped); CapReachedException otherwise, and from then on for every call, so a
        // writer that flushes as it is disposed fails the same way.
        private sealed class CappedBuffer : IBufferWriter<byte>
        {
            private const int Slack = 16 * 1024;

            private readonly long _max;
            private byte[] _buffer = Array.Empty<byte>();
            private int _written;
            private bool _reached;

            internal CappedBuffer(int max)
            {
                _max = max;
            }

            public void Advance(int count)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(count);
                ThrowIfReached();
                if (count > _buffer.Length - _written)
                {
                    throw new InvalidOperationException("The writer advanced past its buffer.");
                }

                if (_written + count > _max)
                {
                    Reach();
                }

                _written += count;
            }

            public Memory<byte> GetMemory(int sizeHint = 0)
            {
                Ensure(sizeHint);
                return _buffer.AsMemory(_written);
            }

            public Span<byte> GetSpan(int sizeHint = 0)
            {
                Ensure(sizeHint);
                return _buffer.AsSpan(_written);
            }

            internal byte[] ToArray() => _buffer.AsSpan(0, _written).ToArray();

            private void Ensure(int sizeHint)
            {
                ThrowIfReached();
                long needed = (long)_written + Math.Max(sizeHint, 1);
                if (needed <= _buffer.Length)
                {
                    return;
                }

                long most = Math.Min(_max + Slack, Array.MaxLength);
                if (needed > most)
                {
                    Reach();
                }

                Array.Resize(ref _buffer, (int)Math.Min(Math.Max(needed, Math.Max(4096L, 2L * _buffer.Length)), most));
            }

            private void ThrowIfReached()
            {
                if (_reached)
                {
                    throw new CapReachedException();
                }
            }

            private void Reach()
            {
                _reached = true;
                throw new CapReachedException();
            }
        }

        private sealed class CapReachedException : Exception
        {
        }
    }
}
