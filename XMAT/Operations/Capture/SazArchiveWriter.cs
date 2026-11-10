// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Xml;

namespace XMAT.Operations.Capture
{
    // One HTTP message of a session: its start line, its header fields in order, and its body.
    internal sealed record SazMessage(
        string StartLine,
        IReadOnlyList<KeyValuePair<string, string>> Fields,
        ReadOnlyMemory<byte> Body);

    // One session of a Fiddler archive. Id names its entries and is unique in the archive; Response is null for a request
    // that got none; Started is a UTC time or null when it is unknown; Flags are its session flags, in order.
    internal sealed record SazSession(
        long Id,
        SazMessage Request,
        SazMessage Response,
        DateTime? Started,
        TimeSpan Duration,
        IReadOnlyList<KeyValuePair<string, string>> Flags);

    // A head that a Fiddler archive cannot hold as it is: a character beyond Latin-1, or a line break inside a line.
    internal sealed class SazWriteException : Exception
    {
        internal const string HeadUnencodableCode = "replay.saz.head-unencodable";

        internal SazWriteException(string code, string message)
            : base(message)
        {
            Code = code;
        }

        internal string Code { get; }
    }

    // Writes a Fiddler archive (.saz) into a stream that the caller owns and closes, one session at a time, so that it
    // holds no more than the session that it writes. Each session is raw/{Id}_c.txt (the request), raw/{Id}_m.xml (its
    // timers and flags) and, when there is a response, raw/{Id}_s.txt. A head is its start line and fields as Latin-1
    // bytes, which keep every byte that HTTP carries, never replaced, written straight into its entry through a buffer of
    // HeadBufferBytes, never built whole; a body is written as it is, in slices of at most BodySliceBytes, with the token
    // checked before each.
    //
    // The writer is Open until one of its terminal states: Completed, once Complete has written the archive's central
    // directory; Faulted, once an exception escaped Add or Complete; abandoned, once disposed before completing. An
    // archive that was not completed has no central directory, so no reader takes it for a whole archive: the caller
    // discards it. Neither a failure nor an abandoned archive can become a completed one.
    internal sealed class SazArchiveWriter : IDisposable
    {
        internal const int BodySliceBytes = 1024 * 1024;
        internal const int HeadBufferBytes = 16 * 1024;
        internal const int MaxFlags = 64;
        internal const int MaxFlagNameChars = 128;
        internal const int MaxFlagValueChars = 4096;
        internal const string UnknownTimeText =
            "0001-01-01T00:00:00.0000000";

        private static readonly string[] RequestTimers =
        {
            "ClientConnected", "ClientBeginRequest", "GotRequestHeaders", "ClientDoneRequest",
        };

        private static readonly string[] ZeroTimers = { "GatewayTime", "DNSTime", "TCPConnectTime", "HTTPSHandshakeTime" };

        private static readonly string[] ResponseTimers =
        {
            "ServerConnected", "FiddlerBeginRequest", "ServerGotRequest", "ServerBeginResponse", "GotResponseHeaders",
            "ServerDoneResponse", "ClientBeginResponse", "ClientDoneResponse",
        };

        private enum WriterState
        {
            Open,
            Completed,
            Faulted,
        }

        private readonly ZipArchive _archive;
        private readonly HashSet<long> _ids = new();
        private WriterState _state;
        private bool _disposed;

        internal SazArchiveWriter(Stream destination)
        {
            ArgumentNullException.ThrowIfNull(destination);
            _archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        }

        // Writes one session, once the token is checked: ArgumentException for a session that an archive cannot hold,
        // including its flags (at most MaxFlags, each a name of 1 to MaxFlagNameChars XML characters without white space
        // and a value of at most MaxFlagValueChars XML characters), SazWriteException for a head that is not Latin-1 or has
        // a line break inside a line, and InvalidOperationException for an Id that the archive has already. Any exception
        // leaves the writer Faulted.
        internal void Add(SazSession session, CancellationToken cancellationToken)
        {
            ThrowUnlessOpen();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(session);
                ArgumentNullException.ThrowIfNull(session.Request);
                if (session.Started is DateTime started &&
                    started.Kind != DateTimeKind.Utc)
                {
                    throw new ArgumentException("A session's start is a UTC time.", nameof(session));
                }

                ArgumentOutOfRangeException.ThrowIfLessThan(session.Duration, TimeSpan.Zero, nameof(session));
                if (!session.Started.HasValue &&
                    session.Duration != TimeSpan.Zero)
                {
                    throw new ArgumentException(
                        "A session without a start cannot have a duration.",
                        nameof(session));
                }
                IReadOnlyList<KeyValuePair<string, string>> flags =
                    session.Flags ?? Array.Empty<KeyValuePair<string, string>>();
                CheckFlags(flags);
                CheckHead(session.Request);
                if (session.Response != null)
                {
                    CheckHead(session.Response);
                }

                if (!_ids.Add(session.Id))
                {
                    throw new InvalidOperationException("The archive already has a session with this Id.");
                }

                string id = session.Id.ToString(CultureInfo.InvariantCulture);
                WriteMessageEntry($"raw/{id}_c.txt", session.Request, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                WriteEntry($"raw/{id}_m.xml", stream => WriteMetadata(stream, session, flags));
                if (session.Response != null)
                {
                    WriteMessageEntry($"raw/{id}_s.txt", session.Response, cancellationToken);
                }
            }
            catch
            {
                _state = WriterState.Faulted;
                throw;
            }
        }

        // Writes the central directory, once: the archive is whole. A cancelled token or a failure to write leaves the
        // writer Faulted, and the archive incomplete for good.
        internal void Complete(CancellationToken cancellationToken)
        {
            ThrowUnlessOpen();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                _archive.Dispose();
                _state = WriterState.Completed;
            }
            catch
            {
                _state = WriterState.Faulted;
                throw;
            }
        }

        // Abandons an archive that was not completed: nothing more is written to the stream, its central directory
        // included. A completed archive is left as it is.
        public void Dispose()
        {
            _disposed = true;
        }

        // A body in slices of at most BodySliceBytes, the token checked before each.
        internal static void WriteBody(Stream stream, ReadOnlySpan<byte> body, CancellationToken cancellationToken)
        {
            for (int offset = 0; offset < body.Length; offset += BodySliceBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                stream.Write(body.Slice(offset, Math.Min(BodySliceBytes, body.Length - offset)));
            }
        }

        private void ThrowUnlessOpen()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state != WriterState.Open)
            {
                throw new InvalidOperationException(
                    _state == WriterState.Completed
                        ? "The archive is complete."
                        : "The archive failed, and is not completed.");
            }
        }

        private void WriteMessageEntry(string name, SazMessage message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteEntry(
                name,
                stream =>
                {
                    WriteHead(stream, message);
                    WriteBody(stream, message.Body.Span, cancellationToken);
                });
        }

        // An entry is deflated only when that makes it no larger than its content, and stored otherwise, so that no
        // entry outgrows what it holds: Fastest codes bytes that do not repeat with fixed Huffman codes of up to 9 bits a
        // byte, an eighth more than random content. The content is written twice, once to measure and once to keep.
        private void WriteEntry(string name, Action<Stream> content)
        {
            CompressionLevel level = DeflatesWithoutGrowing(content)
                ? CompressionLevel.Fastest
                : CompressionLevel.NoCompression;
            using Stream stream = _archive.CreateEntry(name, level).Open();
            content(stream);
        }

        // Whether Fastest deflate codes the content in no more bytes than it has. The code goes to a sink that only
        // counts it, and the measure gives up as soon as the code is longer than the content so far.
        internal static bool DeflatesWithoutGrowing(Action<Stream> content)
        {
            var sink = new CountingSink();
            var measure = new GrowthMeasure(sink);
            using (var deflate = new DeflateStream(sink, CompressionLevel.Fastest, leaveOpen: true))
            {
                measure.Deflate = deflate;
                content(measure);
            }

            return !measure.Grew && sink.Count <= measure.Count;
        }

        // A head that the archive can hold: its start line and fields are Latin-1, with no line break inside a line. Both
        // heads of a session are checked before any of its entries is written, without building either.
        private static void CheckHead(SazMessage message)
        {
            ArgumentNullException.ThrowIfNull(message.StartLine);
            CheckLine(message.StartLine);
            foreach (KeyValuePair<string, string> field in message.Fields ?? Array.Empty<KeyValuePair<string, string>>())
            {
                CheckLine(field.Key ?? string.Empty);
                CheckLine(field.Value ?? string.Empty);
            }
        }

        private static void CheckLine(string text)
        {
            foreach (char character in text)
            {
                if (character > '\u00FF' || character is '\r' or '\n')
                {
                    throw new SazWriteException(
                        SazWriteException.HeadUnencodableCode,
                        "A head of a Fiddler archive is Latin-1, without a line break inside a line.");
                }
            }
        }

        // The head straight into its entry as Latin-1, through a buffer of HeadBufferBytes, and never built whole: once
        // checked (CheckHead), each of its characters is its own byte.
        private static void WriteHead(Stream stream, SazMessage message)
        {
            var head = new HeadWriter(stream);
            head.Append(message.StartLine).Append("\r\n");
            foreach (KeyValuePair<string, string> field in message.Fields ?? Array.Empty<KeyValuePair<string, string>>())
            {
                head.Append(field.Key ?? string.Empty).Append(": ").Append(field.Value ?? string.Empty).Append("\r\n");
            }

            head.Append("\r\n").Flush();
        }

        private sealed class HeadWriter
        {
            private readonly Stream _stream;
            private readonly byte[] _buffer = new byte[HeadBufferBytes];
            private int _count;

            internal HeadWriter(Stream stream)
            {
                _stream = stream;
            }

            internal HeadWriter Append(string text)
            {
                foreach (char character in text)
                {
                    if (_count == _buffer.Length)
                    {
                        Flush();
                    }

                    _buffer[_count++] = (byte)character;
                }

                return this;
            }

            internal void Flush()
            {
                _stream.Write(_buffer, 0, _count);
                _count = 0;
            }
        }

        // A stream that keeps nothing and counts what is written to it.
        private sealed class CountingSink : Stream
        {
            internal long Count { get; private set; }

            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count) => Count += count;

            public override void Write(ReadOnlySpan<byte> buffer) => Count += buffer.Length;

            public override void WriteByte(byte value) => Count++;

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();
        }

        // Counts the content and passes it to the deflate stream until the code is longer than the content so far;
        // from then on it only counts, since the entry will be stored.
        private sealed class GrowthMeasure(CountingSink sink) : Stream
        {
            internal DeflateStream Deflate { get; set; }

            internal long Count { get; private set; }

            internal bool Grew { get; private set; }

            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count) =>
                Write(buffer.AsSpan(offset, count));

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                Count += buffer.Length;
                if (Grew)
                {
                    return;
                }

                Deflate.Write(buffer);
                Grew = sink.Count > Count;
            }

            public override void WriteByte(byte value) => Write(new ReadOnlySpan<byte>(in value));

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();
        }

        private static void CheckFlags(IReadOnlyList<KeyValuePair<string, string>> flags)
        {
            if (flags.Count > MaxFlags)
            {
                throw new ArgumentException("A session has too many flags.", nameof(flags));
            }

            foreach (KeyValuePair<string, string> flag in flags)
            {
                string name = flag.Key;
                string value = flag.Value ?? string.Empty;
                if (string.IsNullOrEmpty(name) || name.Length > MaxFlagNameChars || !IsXmlText(name) ||
                    HasWhiteSpace(name) || value.Length > MaxFlagValueChars || !IsXmlText(value))
                {
                    throw new ArgumentException("A session flag is not one that an archive holds.", nameof(flags));
                }
            }
        }

        private static bool IsXmlText(string text)
        {
            try
            {
                XmlConvert.VerifyXmlChars(text);
                return true;
            }
            catch (XmlException)
            {
                return false;
            }
        }

        private static bool HasWhiteSpace(string text)
        {
            foreach (char character in text)
            {
                if (char.IsWhiteSpace(character))
                {
                    return true;
                }
            }

            return false;
        }

        // The session's timers and flags, as Fiddler writes them, through XmlWriter, which escapes every value and keeps
        // a tab or line break in one as a character reference. Both times are UTC in the round-trip format: the request's
        // events at its start, the response's at its end.
        private static void WriteMetadata(
            Stream stream, SazSession session, IReadOnlyList<KeyValuePair<string, string>> flags)
        {
            string begin = session.Started?.ToString(
                "o",
                CultureInfo.InvariantCulture) ??
                UnknownTimeText;
            string done = session.Started.HasValue
                ? (session.Started.Value + session.Duration).ToString(
                    "o",
                    CultureInfo.InvariantCulture)
                : UnknownTimeText;
            var settings = new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                Indent = true,
                NewLineHandling = NewLineHandling.Entitize,
                CloseOutput = false,
            };
            using XmlWriter writer = XmlWriter.Create(stream, settings);
            writer.WriteStartDocument();
            writer.WriteStartElement("Session");
            writer.WriteAttributeString("SID", session.Id.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("BitFlags", "0");
            writer.WriteStartElement("SessionTimers");
            foreach (string name in RequestTimers)
            {
                writer.WriteAttributeString(name, begin);
            }

            foreach (string name in ZeroTimers)
            {
                writer.WriteAttributeString(name, "0");
            }

            foreach (string name in ResponseTimers)
            {
                writer.WriteAttributeString(name, done);
            }

            writer.WriteEndElement();
            writer.WriteStartElement("SessionFlags");
            foreach (KeyValuePair<string, string> flag in flags)
            {
                writer.WriteStartElement("SessionFlag");
                writer.WriteAttributeString("N", flag.Key);
                writer.WriteAttributeString("V", flag.Value ?? string.Empty);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }
    }
}
