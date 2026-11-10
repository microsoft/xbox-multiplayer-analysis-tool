// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using XMAT.Operations.Capture;

namespace XMAT.Tests.Replay
{
    // SazArchiveWriter: the entries of each session in order, Latin-1 heads, bodies as they are, escaped metadata, the
    // refusals, and the terminal states: a failed or abandoned archive never becomes a completed one.
    public class SazArchiveWriterTests
    {
        private static readonly DateTime Start = new DateTime(2026, 3, 8, 9, 59, 59, DateTimeKind.Utc).AddTicks(1234567);

        [Fact]
        public void Add_WritesEachSessionsEntriesInOrder_WithLatin1HeadsAndBodiesAsTheyAre()
        {
            byte[] binary = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
            using var destination = new MemoryStream();
            using (var writer = new SazArchiveWriter(destination))
            {
                writer.Add(
                    Session(
                        7,
                        new SazMessage("POST http://example.test/a HTTP/1.1", Fields(("Host", "example.test"), ("X-Caf", "caf\u00e9")), binary),
                        new SazMessage("HTTP/1.1 200 OK", Fields(("X-Byte", "\u00ff")), "done"u8.ToArray())),
                    CancellationToken.None);
                writer.Add(
                    Session(8, new SazMessage("GET http://example.test/b HTTP/1.1", Fields(("Host", "example.test")), default), null),
                    CancellationToken.None);
                writer.Complete(CancellationToken.None);
            }

            using ZipArchive archive = Read(destination);
            Assert.Equal(
                new[] { "raw/7_c.txt", "raw/7_m.xml", "raw/7_s.txt", "raw/8_c.txt", "raw/8_m.xml" },
                archive.Entries.Select(entry => entry.FullName));
            byte[] request = Bytes(archive, "raw/7_c.txt");
            byte[] head = Encoding.Latin1.GetBytes("POST http://example.test/a HTTP/1.1\r\nHost: example.test\r\nX-Caf: caf\u00e9\r\n\r\n");
            Assert.Equal(head.Concat(binary), request);
            Assert.Equal(0xE9, request[head.Length - 5]);
            Assert.Equal(Encoding.Latin1.GetBytes("HTTP/1.1 200 OK\r\nX-Byte: \u00ff\r\n\r\ndone"), Bytes(archive, "raw/7_s.txt"));
            Assert.Equal(Encoding.Latin1.GetBytes("GET http://example.test/b HTTP/1.1\r\nHost: example.test\r\n\r\n"), Bytes(archive, "raw/8_c.txt"));
        }

        // XmlWriter escapes every flag value, which reads back exactly; the timers are UTC instants in the round-trip format.
        [Fact]
        public void Metadata_EscapesEveryFlag_AndKeepsBothTimesAsUtcInstants()
        {
            string[] values = { "\"quoted\"", "<tag>", "a & b", "it's", "x > y", "caf\u00e9 \u20ac", "tab\tline\nreturn\rend", "" };
            using var destination = new MemoryStream();
            using (var writer = new SazArchiveWriter(destination))
            {
                writer.Add(
                    Session(3, Request(), null, values.Select((value, index) => KeyValuePair.Create("x-flag-" + index, value)).ToArray()) with
                    {
                        Duration = TimeSpan.FromTicks(25_000_001),
                    },
                    CancellationToken.None);
                writer.Complete(CancellationToken.None);
            }

            using ZipArchive archive = Read(destination);
            XDocument metadata = XDocument.Parse(Encoding.UTF8.GetString(Bytes(archive, "raw/3_m.xml")));
            XElement session = metadata.Root;
            Assert.Equal(("Session", "3", "0"), (session.Name.LocalName, (string)session.Attribute("SID"), (string)session.Attribute("BitFlags")));
            Assert.Equal(values, session.Element("SessionFlags").Elements("SessionFlag").Select(flag => (string)flag.Attribute("V")));
            Assert.Equal(
                values.Select((_, index) => "x-flag-" + index),
                session.Element("SessionFlags").Elements("SessionFlag").Select(flag => (string)flag.Attribute("N")));
            XElement timers = session.Element("SessionTimers");
            Assert.Equal("2026-03-08T09:59:59.1234567Z", (string)timers.Attribute("ClientBeginRequest"));
            Assert.Equal("2026-03-08T10:00:01.6234568Z", (string)timers.Attribute("ClientDoneResponse"));
            Assert.Equal("0", (string)timers.Attribute("DNSTime"));
        }

        public static IEnumerable<object[]> FlagsThatAnArchiveCannotHold()
        {
            yield return new object[] { new[] { KeyValuePair.Create("", "v") } };
            yield return new object[] { new[] { KeyValuePair.Create("has space", "v") } };
            yield return new object[] { new[] { KeyValuePair.Create(new string('n', SazArchiveWriter.MaxFlagNameChars + 1), "v") } };
            yield return new object[] { new[] { KeyValuePair.Create("n", new string('v', SazArchiveWriter.MaxFlagValueChars + 1)) } };
            yield return new object[] { new[] { KeyValuePair.Create("n", "control\u0001") } };
            yield return new object[] { new[] { KeyValuePair.Create("n", "lone\ud800") } };
            yield return new object[] { new[] { KeyValuePair.Create("n\u0002", "v") } };
            yield return new object[]
            {
                Enumerable.Range(0, SazArchiveWriter.MaxFlags + 1).Select(index => KeyValuePair.Create("n" + index, "v")).ToArray(),
            };
        }

        [Theory]
        [MemberData(nameof(FlagsThatAnArchiveCannotHold))]
        public void Add_RefusesFlagsThatAnArchiveCannotHold_AndTheWriterFails(KeyValuePair<string, string>[] flags)
        {
            using var destination = new MemoryStream();
            using var writer = new SazArchiveWriter(destination);

            Assert.Throws<ArgumentException>(() => writer.Add(Session(1, Request(), null, flags), CancellationToken.None));

            Assert.Throws<InvalidOperationException>(() => writer.Add(Session(2, Request(), null), CancellationToken.None));
            Assert.Throws<InvalidOperationException>(() => writer.Complete(CancellationToken.None));
        }

        [Fact]
        public void Add_AcceptsTheLargestFlagsThatAnArchiveHolds()
        {
            KeyValuePair<string, string>[] flags = Enumerable.Range(0, SazArchiveWriter.MaxFlags)
                .Select(index => KeyValuePair.Create(index.ToString("D3") + new string('n', SazArchiveWriter.MaxFlagNameChars - 3), new string('v', SazArchiveWriter.MaxFlagValueChars)))
                .ToArray();
            using var destination = new MemoryStream();
            using var writer = new SazArchiveWriter(destination);

            writer.Add(Session(1, Request(), null, flags), CancellationToken.None);
            writer.Complete(CancellationToken.None);
        }

        [Theory]
        [InlineData("X-Euro", "\u20ac")]
        [InlineData("X-Line", "a\r\nInjected: yes")]
        [InlineData("X-Feed", "a\nb")]
        [InlineData("X\u0100", "v")]
        public void Add_RefusesAHeadThatIsNotLatin1OrBreaksALine(string name, string value)
        {
            using var destination = new MemoryStream();
            using var writer = new SazArchiveWriter(destination);
            var request = new SazMessage("GET http://example.test/ HTTP/1.1", Fields((name, value)), default);

            SazWriteException refused = Assert.Throws<SazWriteException>(
                () => writer.Add(Session(1, request, null), CancellationToken.None));

            Assert.Equal(SazWriteException.HeadUnencodableCode, refused.Code);
            Assert.Equal(0, destination.Length);
            Assert.Throws<InvalidOperationException>(() => writer.Complete(CancellationToken.None));
        }

        [Fact]
        public void Add_RefusesAnIdThatTheArchiveHasAlready()
        {
            using var destination = new MemoryStream();
            using var writer = new SazArchiveWriter(destination);
            writer.Add(Session(5, Request(), null), CancellationToken.None);

            Assert.Throws<InvalidOperationException>(() => writer.Add(Session(5, Request(), null), CancellationToken.None));

            Assert.Throws<InvalidOperationException>(() => writer.Complete(CancellationToken.None));
        }

        [Fact]
        public void Add_WithACancelledToken_WritesNothing_AndTheWriterFails()
        {
            using var destination = new MemoryStream();
            using var writer = new SazArchiveWriter(destination);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() => writer.Add(Session(1, Request(), null), cancellation.Token));

            Assert.Equal(0, destination.Length);
            Assert.Throws<InvalidOperationException>(() => writer.Complete(CancellationToken.None));
        }

        // (a) A Complete that fails while it writes the central directory leaves the writer Faulted for good: it is never
        // completed later, nothing is added, and disposing it writes nothing more.
        [Fact]
        public void Complete_ThatFailsToWrite_LeavesTheWriterFaulted_AndTheArchiveIncomplete()
        {
            using var destination = new FailingStream();
            using var writer = new SazArchiveWriter(destination);
            writer.Add(Session(1, Request(), null), CancellationToken.None);
            destination.FailWrites = true;

            Assert.Throws<IOException>(() => writer.Complete(CancellationToken.None));
            destination.FailWrites = false;
            long length = destination.Length;

            Assert.Throws<InvalidOperationException>(() => writer.Complete(CancellationToken.None));
            Assert.Throws<InvalidOperationException>(() => writer.Add(Session(2, Request(), null), CancellationToken.None));
            writer.Dispose();
            Assert.Equal(length, destination.Length);
            Assert.False(HasEndOfCentralDirectory(destination.ToArray()));
        }

        // (b) A Complete that is cancelled leaves the writer Faulted, as in (a).
        [Fact]
        public void Complete_Cancelled_LeavesTheWriterFaulted_AndTheArchiveIncomplete()
        {
            using var destination = new MemoryStream();
            using var writer = new SazArchiveWriter(destination);
            writer.Add(Session(1, Request(), null), CancellationToken.None);
            long length = destination.Length;
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() => writer.Complete(cancellation.Token));

            Assert.Throws<InvalidOperationException>(() => writer.Complete(CancellationToken.None));
            writer.Dispose();
            Assert.Equal(length, destination.Length);
            Assert.False(HasEndOfCentralDirectory(destination.ToArray()));
        }

        // (c) Disposing an archive that is not complete abandons it: no central directory, and nothing completes it later.
        [Fact]
        public void Dispose_AbandonsTheArchive_AndNothingCompletesItLater()
        {
            using var destination = new MemoryStream();
            var writer = new SazArchiveWriter(destination);
            writer.Add(Session(1, Request(), null), CancellationToken.None);
            long length = destination.Length;

            writer.Dispose();

            Assert.Throws<ObjectDisposedException>(() => writer.Complete(CancellationToken.None));
            Assert.Throws<ObjectDisposedException>(() => writer.Add(Session(2, Request(), null), CancellationToken.None));
            Assert.Equal(length, destination.Length);
            Assert.False(HasEndOfCentralDirectory(destination.ToArray()));
            Assert.Throws<InvalidDataException>(() => new ZipArchive(new MemoryStream(destination.ToArray()), ZipArchiveMode.Read));
        }

        // (d) A completed archive takes nothing more, and disposing the writer leaves it as it is.
        [Fact]
        public void Complete_WritesTheArchiveOnce_AndNothingIsAddedAfterwards()
        {
            using var destination = new MemoryStream();
            var writer = new SazArchiveWriter(destination);
            writer.Add(Session(1, Request(), null), CancellationToken.None);

            writer.Complete(CancellationToken.None);
            byte[] completed = destination.ToArray();

            Assert.Throws<InvalidOperationException>(() => writer.Add(Session(2, Request(), null), CancellationToken.None));
            Assert.Throws<InvalidOperationException>(() => writer.Complete(CancellationToken.None));
            writer.Dispose();
            Assert.Equal(completed, destination.ToArray());
            Assert.True(HasEndOfCentralDirectory(completed));
            using ZipArchive archive = Read(destination);
            Assert.Equal(new[] { "raw/1_c.txt", "raw/1_m.xml" }, archive.Entries.Select(entry => entry.FullName));
        }

        // The archive is written in Create mode, which never reads or seeks its stream.
        [Fact]
        public void TheArchive_IsWrittenToAStreamThatCanNeitherReadNorSeek()
        {
            using var destination = new MemoryStream();
            using (var writeOnly = new WriteOnlyStream(destination))
            using (var writer = new SazArchiveWriter(writeOnly))
            {
                writer.Add(Session(1, Request(), new SazMessage("HTTP/1.1 204 No Content", Fields(), default)), CancellationToken.None);
                writer.Complete(CancellationToken.None);
            }

            using ZipArchive archive = Read(destination);
            Assert.Equal(new[] { "raw/1_c.txt", "raw/1_m.xml", "raw/1_s.txt" }, archive.Entries.Select(entry => entry.FullName));
        }

        [Fact]
        public void WriteBody_WritesSlicesOfAtMostOneMiB_CheckingTheTokenBeforeEach()
        {
            byte[] body = new byte[(3 * SazArchiveWriter.BodySliceBytes) + 5];
            var writes = new RecordingStream();

            SazArchiveWriter.WriteBody(writes, body, CancellationToken.None);

            Assert.Equal(
                new[] { SazArchiveWriter.BodySliceBytes, SazArchiveWriter.BodySliceBytes, SazArchiveWriter.BodySliceBytes, 5 },
                writes.Sizes);

            using var cancellation = new CancellationTokenSource();
            var cancelling = new RecordingStream { AfterWrite = cancellation.Cancel };
            Assert.Throws<OperationCanceledException>(() => SazArchiveWriter.WriteBody(cancelling, body, cancellation.Token));
            Assert.Equal(new[] { SazArchiveWriter.BodySliceBytes }, cancelling.Sizes);
        }

        // A body is cut into slices inside the archive, and still reads back whole.
        [Fact]
        public void ALargeBody_ReadsBackWhole()
        {
            byte[] body = new byte[(2 * SazArchiveWriter.BodySliceBytes) + 17];
            new Random(5).NextBytes(body);
            using var destination = new MemoryStream();
            using (var writer = new SazArchiveWriter(destination))
            {
                writer.Add(Session(1, new SazMessage("PUT http://example.test/ HTTP/1.1", Fields(), body), null), CancellationToken.None);
                writer.Complete(CancellationToken.None);
            }

            using ZipArchive archive = Read(destination);
            byte[] request = Bytes(archive, "raw/1_c.txt");
            Assert.Equal(body, request[^body.Length..]);
        }

        // A head is written into its entry through a small buffer, never built whole: writing a session with two 8 MiB
        // heads allocates far less than one of them. A copy of a head would allocate twice its length or more.
        [Fact]
        public void LargeHeads_AreWrittenThroughASmallBuffer_NeverBuiltWhole()
        {
            string large = new string('v', 8 * 1024 * 1024);
            using var destination = new CountingStream();
            using var writer = new SazArchiveWriter(destination);
            writer.Add(Session(1, Request(), new SazMessage("HTTP/1.1 200 OK", Fields(("X-Small", "warm")), default)), CancellationToken.None);
            var session = Session(
                2,
                new SazMessage("GET http://example.test/ HTTP/1.1", Fields(("X-Large", large)), default),
                new SazMessage("HTTP/1.1 200 OK", Fields(("X-Large", large)), default));

            long before = GC.GetAllocatedBytesForCurrentThread();
            writer.Add(session, CancellationToken.None);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.InRange(allocated, 0, 4L * 1024 * 1024);
            Assert.True(destination.Written > 0);
        }

        internal static bool HasEndOfCentralDirectory(byte[] bytes)
        {
            for (int index = 0; index + 3 < bytes.Length; index++)
            {
                if (bytes[index] == 0x50 && bytes[index + 1] == 0x4B && bytes[index + 2] == 0x05 && bytes[index + 3] == 0x06)
                {
                    return true;
                }
            }

            return false;
        }

        private static SazSession Session(
            long id, SazMessage request, SazMessage response, KeyValuePair<string, string>[] flags = null) =>
            new(id, request, response, Start, TimeSpan.FromMilliseconds(25), flags ?? new[] { KeyValuePair.Create("x-clientip", "0.0.0.0") });

        private static SazMessage Request() =>
            new("GET http://example.test/ HTTP/1.1", Fields(("Host", "example.test")), default);

        private static KeyValuePair<string, string>[] Fields(params (string Name, string Value)[] fields) =>
            fields.Select(field => KeyValuePair.Create(field.Name, field.Value)).ToArray();

        private static ZipArchive Read(MemoryStream destination) =>
            new(new MemoryStream(destination.ToArray()), ZipArchiveMode.Read);

        private static byte[] Bytes(ZipArchive archive, string name)
        {
            using Stream entry = archive.GetEntry(name).Open();
            using var bytes = new MemoryStream();
            entry.CopyTo(bytes);
            return bytes.ToArray();
        }

        // A memory stream whose writes fail while FailWrites is set.
        private sealed class FailingStream : MemoryStream
        {
            internal bool FailWrites { get; set; }

            public override void Write(byte[] buffer, int offset, int count)
            {
                ThrowIfFailing();
                base.Write(buffer, offset, count);
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                ThrowIfFailing();
                base.Write(buffer);
            }

            public override void WriteByte(byte value)
            {
                ThrowIfFailing();
                base.WriteByte(value);
            }

            private void ThrowIfFailing()
            {
                if (FailWrites)
                {
                    throw new IOException("The test stream refused a write.");
                }
            }
        }

        // Records the size of each write, and runs AfterWrite after each.
        private sealed class RecordingStream : Stream
        {
            internal List<int> Sizes { get; } = new();

            internal Action AfterWrite { get; set; }

            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                Sizes.Add(buffer.Length);
                AfterWrite?.Invoke();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();
        }

        // Counts what is written and keeps none of it; it can neither read nor seek.
        private sealed class CountingStream : Stream
        {
            internal long Written { get; private set; }

            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count) => Written += count;

            public override void Write(ReadOnlySpan<byte> buffer) => Written += buffer.Length;

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();
        }

        // Writes through to a memory stream, and can neither read nor seek.
        private sealed class WriteOnlyStream(MemoryStream inner) : Stream
        {
            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

            public override void Flush() => inner.Flush();

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
