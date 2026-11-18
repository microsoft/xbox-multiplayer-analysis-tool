// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace XMAT.Operations.Replay
{
    // diff.json: one verification detail (VerifyDiff), schema "xmat.verify-diff", version 1, enum values in camel case.
    // Measure counts its bytes without keeping them, in one block no larger than a writer's room for one token; Write streams
    // it; Serialize returns its bytes.
    internal static class VerifyDiffJson
    {
        private static readonly JsonWriterOptions Options = new() { Indented = true };

        // The length of the detail as diff.json, or -1 once it is longer than maxBytes, which stops the writing.
        internal static int Measure(VerifyDiff diff, int maxBytes)
        {
            ArgumentNullException.ThrowIfNull(diff);
            var counter = new CountingBuffer(maxBytes);
            try
            {
                using (var writer = new Utf8JsonWriter(counter, Options))
                {
                    Write(writer, diff);
                }
            }
            catch (CapReachedException)
            {
                return -1;
            }

            return counter.Written;
        }

        // Writes the detail as diff.json to stream.
        internal static void Write(VerifyDiff diff, Stream stream)
        {
            ArgumentNullException.ThrowIfNull(diff);
            ArgumentNullException.ThrowIfNull(stream);
            using var writer = new Utf8JsonWriter(stream, Options);
            Write(writer, diff);
        }

        // The detail as diff.json.
        internal static byte[] Serialize(VerifyDiff diff)
        {
            using var bytes = new MemoryStream();
            Write(diff, bytes);
            return bytes.ToArray();
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

        // Counts the bytes that a writer writes, up to max, and keeps none of them: each request gets the same block, as large
        // as the largest token that the writer asks room for (a value of MaxValueChars characters escaped is a few KiB).
        // CapReachedException once more than max were written, and from then on for every call, so a writer that flushes as
        // it is disposed fails the same way.
        private sealed class CountingBuffer : IBufferWriter<byte>
        {
            private const int MostBlockBytes = 64 * 1024;

            private readonly long _max;
            private byte[] _block = Array.Empty<byte>();
            private long _written;
            private bool _reached;

            internal CountingBuffer(int max)
            {
                _max = max;
            }

            internal int Written => (int)_written;

            public void Advance(int count)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(count);
                ThrowIfReached();
                if (count > _block.Length)
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
                return _block;
            }

            public Span<byte> GetSpan(int sizeHint = 0)
            {
                Ensure(sizeHint);
                return _block;
            }

            private void Ensure(int sizeHint)
            {
                ThrowIfReached();
                int wanted = Math.Max(sizeHint, 4096);
                if (wanted > MostBlockBytes)
                {
                    Reach();
                }

                if (_block.Length < wanted)
                {
                    _block = new byte[wanted];
                }
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
