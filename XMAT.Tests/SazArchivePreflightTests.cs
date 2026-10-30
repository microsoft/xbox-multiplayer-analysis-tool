// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.IO.Compression;
using XMAT.Tests.Fixtures;
using XMAT.WebServiceCapture;

namespace XMAT.Tests
{
    public class SazArchivePreflightTests
    {
        [Fact]
        public void Preflight_AcceptsArchivesWrittenByXmatAndZip64Directories()
        {
            using var directory = new TestDirectory();
            string plain = SyntheticCaptures.WriteSaz(
                directory.Path,
                "plain.saz",
                new SyntheticSazOptions(RequestsPerConsole: 2));
            string zip64 = SyntheticCaptures.WriteZip64Saz(
                directory.Path,
                "zip64.saz");

            Validate(plain);
            Validate(zip64);

            Assert.Equal(
                2,
                FiddlerSazHandler.ImportSazFile(
                    plain,
                    CancellationToken.None).Count);
            Assert.Single(
                FiddlerSazHandler.ImportSazFile(
                    zip64,
                    CancellationToken.None));
        }

        [Fact]
        public void Preflight_EntryCountLimit_RejectsTheNextEntry()
        {
            using var directory = new TestDirectory();
            string path = SyntheticCaptures.WriteZipWithEntries(
                directory.Path,
                "too-many-entries.saz",
                entryCount: 3);

            Validate(
                path,
                maxEntries: 3);
            SazArchiveException exception =
                Assert.Throws<SazArchiveException>(
                    () => Validate(
                        path,
                        maxEntries: 2));

            Assert.Equal(
                SazArchiveFailure.TooManyEntries,
                exception.Failure);
        }

        [Fact]
        public void Preflight_CentralDirectoryByteLimit_RejectsALargerDirectory()
        {
            using var directory = new TestDirectory();
            string path = SyntheticCaptures.WriteZipWithEntries(
                directory.Path,
                "large-directory.saz",
                entryCount: 1,
                entryNameLength: 256);

            SazArchiveException exception =
                Assert.Throws<SazArchiveException>(
                    () => Validate(
                        path,
                        maxCentralDirectoryBytes: 64));

            Assert.Equal(
                SazArchiveFailure.CentralDirectoryTooLarge,
                exception.Failure);
        }

        [Fact]
        public void Preflight_LargerLimitsAreCappedAtTheCeilings()
        {
            using var directory = new TestDirectory();
            string path = SyntheticCaptures.WriteZip64SazDeclaringEntries(
                directory.Path,
                "ceiling.saz",
                SazArchivePreflight.MaxArchiveEntries + 1);

            SazArchiveException exception =
                Assert.Throws<SazArchiveException>(
                    () => Validate(
                        path,
                        maxEntries: long.MaxValue,
                        maxCentralDirectoryBytes: long.MaxValue));

            Assert.Equal(
                SazArchiveFailure.TooManyEntries,
                exception.Failure);
        }

        [Fact]
        public void Import_RejectsAnArchiveDeclaringMoreEntriesThanTheCeilingBeforeReadingIt()
        {
            using var directory = new TestDirectory();
            string path = SyntheticCaptures.WriteZip64SazDeclaringEntries(
                directory.Path,
                "declared-entries.saz",
                SazArchivePreflight.MaxArchiveEntries + 1);

            SazArchiveException exception =
                Assert.Throws<SazArchiveException>(
                    () => FiddlerSazHandler.ImportSazFileWithDiagnostics(
                        path,
                        CancellationToken.None));

            Assert.Equal(
                SazArchiveFailure.TooManyEntries,
                exception.Failure);
        }

        [Fact]
        public void Preflight_UnderstatedEntryCount_IsMalformed()
        {
            using var directory = new TestDirectory();
            string path = SyntheticCaptures.WriteUnderstatedEntryCountZip(
                directory.Path,
                "understated-count.saz");

            SazArchiveException exception =
                Assert.Throws<SazArchiveException>(
                    () => Validate(path));

            Assert.Equal(
                SazArchiveFailure.Malformed,
                exception.Failure);
            Assert.Null(exception.InnerException);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Preflight_CentralDirectoryOutsideTheFile_IsMalformed(
            bool corruptSize)
        {
            using var directory = new TestDirectory();
            string path =
                SyntheticCaptures.WriteInvalidCentralDirectoryBoundsZip(
                    directory.Path,
                    "invalid-directory-bounds.saz",
                    corruptSize);

            SazArchiveException exception =
                Assert.Throws<SazArchiveException>(
                    () => Validate(path));

            Assert.Equal(
                SazArchiveFailure.Malformed,
                exception.Failure);
        }

        [Fact]
        public void Preflight_DecoyEndRecordInTheComment_IsMalformed()
        {
            using var directory = new TestDirectory();
            string path =
                SyntheticCaptures.WriteSazWithDecoyEndRecordComment(
                    directory.Path,
                    "decoy-end-record.saz");

            SazArchiveException exception =
                Assert.Throws<SazArchiveException>(
                    () => Validate(path));

            Assert.Equal(
                SazArchiveFailure.Malformed,
                exception.Failure);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Preflight_Zip64BoundaryOverflow_IsMalformed(
            bool unrepresentableRecordOffset)
        {
            using var directory = new TestDirectory();
            string path = SyntheticCaptures.WriteZip64Saz(
                directory.Path,
                "invalid-zip64.saz",
                zip64RecordOffset:
                    unrepresentableRecordOffset ? ulong.MaxValue : null,
                zip64RecordBodySize:
                    unrepresentableRecordOffset ? 44UL : ulong.MaxValue);

            SazArchiveException exception =
                Assert.Throws<SazArchiveException>(
                    () => Validate(path));

            Assert.Equal(
                SazArchiveFailure.Malformed,
                exception.Failure);
            if (unrepresentableRecordOffset)
            {
                Assert.Null(exception.InnerException);
            }
            else
            {
                Assert.IsType<OverflowException>(
                    exception.InnerException);
            }
        }

        [Fact]
        public void Preflight_MultiDiskArchive_IsMalformed()
        {
            using var directory = new TestDirectory();
            string path = SyntheticCaptures.WriteZipWithEntries(
                directory.Path,
                "multi-disk.saz",
                entryCount: 1);
            byte[] bytes = File.ReadAllBytes(path);
            int endRecord = bytes.Length - 22;
            BinaryPrimitives.WriteUInt16LittleEndian(
                bytes.AsSpan(endRecord + 4, 2),
                1);
            File.WriteAllBytes(path, bytes);

            SazArchiveException exception =
                Assert.Throws<SazArchiveException>(
                    () => Validate(path));

            Assert.Equal(
                SazArchiveFailure.Malformed,
                exception.Failure);
        }

        [Fact]
        public void Preflight_ReadsOnlyTheArchiveDirectory()
        {
            using var directory = new TestDirectory();
            string path = Path.Combine(
                directory.Path,
                "large-entry.saz");
            using (var archive = ZipFile.Open(
                path,
                ZipArchiveMode.Create))
            {
                ZipArchiveEntry entry = archive.CreateEntry(
                    "raw/1_c.txt",
                    CompressionLevel.NoCompression);
                using Stream stream = entry.Open();
                stream.Write(new byte[1024 * 1024]);
            }
            using var input = new CountingStream(
                File.OpenRead(path));

            SazArchivePreflight.Validate(
                input,
                CancellationToken.None);

            Assert.InRange(
                input.BytesRead,
                1L,
                64L * 1024 + 1024);
        }

        private static void Validate(
            string path,
            long maxEntries = SazArchivePreflight.MaxArchiveEntries,
            long maxCentralDirectoryBytes = SazArchivePreflight.MaxCentralDirectoryBytes)
        {
            using var input = File.OpenRead(path);
            SazArchivePreflight.Validate(
                input,
                maxEntries,
                maxCentralDirectoryBytes,
                CancellationToken.None);
        }

        private sealed class CountingStream : Stream
        {
            private readonly Stream _inner;

            internal CountingStream(Stream inner)
            {
                _inner = inner;
            }

            internal long BytesRead { get; private set; }

            public override bool CanRead => _inner.CanRead;

            public override bool CanSeek => _inner.CanSeek;

            public override bool CanWrite => false;

            public override long Length => _inner.Length;

            public override long Position
            {
                get => _inner.Position;
                set => _inner.Position = value;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int read = _inner.Read(buffer, offset, count);
                BytesRead += read;
                return read;
            }

            public override int Read(Span<byte> buffer)
            {
                int read = _inner.Read(buffer);
                BytesRead += read;
                return read;
            }

            public override long Seek(long offset, SeekOrigin origin) =>
                _inner.Seek(offset, origin);

            public override void Flush()
            {
            }

            public override void SetLength(long value) =>
                throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _inner.Dispose();
                }
                base.Dispose(disposing);
            }
        }

        private sealed class TestDirectory : IDisposable
        {
            internal TestDirectory()
            {
                Path = System.IO.Path.Combine(
                    AppContext.BaseDirectory,
                    "SazTestArtifacts",
                    $"XMAT-archive-{Guid.NewGuid():N}");
                Directory.CreateDirectory(Path);
            }

            internal string Path { get; }

            public void Dispose()
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(
                        Path,
                        true);
                }
            }
        }
    }
}
