// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.IO.Compression;
using System.Text;
using XMAT.Operations.Replay;
using XMAT.Tests.Fixtures;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests
{
    // A SAZ head's repeated fields stay one line each, in order and under their names as written, through both readers
    // (FiddlerSazHandler.ImportSazFile and BoundedSazReader) and a plain export read again. Two Set-Cookie fields, whose
    // Expires dates hold commas, are never joined into one.
    public class SazRepeatedFieldTests
    {
        private const string Request =
            "POST http://example.test/items?x=1 HTTP/1.1\r\n" +
            "Host: example.test\r\n" +
            "Accept: text/plain\r\n" +
            "X-Repeat: one\r\n" +
            "Cookie: a=1\r\n" +
            "x-repeat:two  \r\n" +
            "Accept: application/json\r\n" +
            "X-Folded: a, b\r\n" +
            "Content-Length: 4\r\n" +
            "\r\n" +
            "body";

        private const string Response =
            "HTTP/1.1 200 OK\r\n" +
            "Set-Cookie: a=1; Expires=Wed, 21 Oct 2026 07:28:00 GMT; Path=/\r\n" +
            "Content-Type: text/plain\r\n" +
            "set-cookie: b=2; Expires=Thu, 22 Oct 2026 07:28:00 GMT; HttpOnly\r\n" +
            "X-Repeat: one\r\n" +
            "X-Repeat: two\r\n" +
            "Content-Length: 2\r\n" +
            "\r\n" +
            "ok";

        private const string RequestHead =
            "POST http://example.test/items?x=1 HTTP/1.1\r\n" +
            "Host: example.test\r\n" +
            "Accept: text/plain\r\n" +
            "X-Repeat: one\r\n" +
            "Cookie: a=1\r\n" +
            "x-repeat: two\r\n" +
            "Accept: application/json\r\n" +
            "X-Folded: a, b\r\n" +
            "Content-Length: 4\r\n" +
            "\r\n";

        private const string ResponseHead =
            "HTTP/1.1 200 OK\r\n" +
            "Set-Cookie: a=1; Expires=Wed, 21 Oct 2026 07:28:00 GMT; Path=/\r\n" +
            "Content-Type: text/plain\r\n" +
            "set-cookie: b=2; Expires=Thu, 22 Oct 2026 07:28:00 GMT; HttpOnly\r\n" +
            "X-Repeat: one\r\n" +
            "X-Repeat: two\r\n" +
            "Content-Length: 2\r\n" +
            "\r\n";

        [Fact]
        public void RepeatedFields_KeepEveryLineInOrder_ThroughBothReaders_AndAPlainExportReadAgain()
        {
            using var directory = new TestDirectory();
            string archive = WriteArchive(
                Path.Combine(directory.Path, "repeated.saz"), Encoding.Latin1.GetBytes(Request), Encoding.Latin1.GetBytes(Response));
            foreach (ProxyConnectionModel record in ReadWithBothReaders(archive))
            {
                AssertKept(record);
            }

            string exported = Path.Combine(directory.Path, "exported.saz");
            FiddlerSazHandler.CreateSazFile(exported, FiddlerSazHandler.ImportSazFile(archive, CancellationToken.None));
            foreach (ProxyConnectionModel record in ReadWithBothReaders(exported))
            {
                AssertKept(record);
            }
        }

        // An imported record looks its head up as every record does (ProxyConnectionModel): a repeated name gives its last
        // line, while the head keeps both. Verification reads the content coding so too, as it does for a native capture,
        // and a replay that sends the same content with that coding matches.
        [Fact]
        public void AnImportedRecordsLookup_GivesTheLastLine_AndVerificationReadsItSo()
        {
            using var directory = new TestDirectory();
            byte[] content = Encoding.ASCII.GetBytes("verified content");
            byte[] response = Encoding.Latin1.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Encoding: identity\r\nX-Repeat: one\r\ncontent-encoding: gzip\r\nX-Repeat: two\r\n\r\n")
                .Concat(Gzip(content))
                .ToArray();
            string archive = WriteArchive(
                Path.Combine(directory.Path, "coded.saz"),
                Encoding.Latin1.GetBytes("GET http://example.test/v HTTP/1.1\r\nHost: example.test\r\n\r\n"),
                response);

            foreach (ProxyConnectionModel record in ReadWithBothReaders(archive))
            {
                Assert.Contains(
                    "\r\nContent-Encoding: identity\r\nX-Repeat: one\r\ncontent-encoding: gzip\r\nX-Repeat: two\r\n\r\n",
                    record.ResponseLineAndHeaders);
                Assert.Equal("gzip", record.ResponseHeaders["Content-Encoding"]);
                Assert.Equal("two", record.ResponseHeaders["x-repeat"]);
                var replay = new ReplayExchange
                {
                    Outcome = ReplayOutcome.Completed,
                    StatusCode = 200,
                    Response = new ReplayMessage(
                        "HTTP/1.1 200 OK",
                        Array.Empty<KeyValuePair<string, string>>(),
                        new[] { new KeyValuePair<string, string>("Content-Encoding", "gzip") },
                        Gzip(content)),
                };
                Assert.True(ReplayVerifier.CompareSides(record, replay, 1 << 20).Verification.Matches);
            }
        }

        // The head keeps every line, and a value that was already one list stays one line, never split. The record reads its
        // head as every record does (ProxyConnectionModel), so that value is also one value in its lookup.
        private static void AssertKept(ProxyConnectionModel record)
        {
            Assert.Equal(RequestHead, record.RequestLineAndHeaders);
            Assert.Equal(ResponseHead, record.ResponseLineAndHeaders);
            Assert.Equal(Encoding.ASCII.GetBytes("body"), record.RequestBody);
            Assert.Equal(Encoding.ASCII.GetBytes("ok"), record.ResponseBody);
            Assert.Equal("a, b", record.RequestHeaders["x-folded"]);
        }

        private static byte[] Gzip(byte[] content)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                gzip.Write(content, 0, content.Length);
            }

            return output.ToArray();
        }

        private static IEnumerable<ProxyConnectionModel> ReadWithBothReaders(string path)
        {
            ProxyConnectionModel imported = Assert.Single(FiddlerSazHandler.ImportSazFile(path, CancellationToken.None));
            using FileStream stream = File.OpenRead(path);
            BoundedSazReadResult read = BoundedSazReader.Read(
                stream,
                new BoundedSazLimits(
                    1L << 30,
                    1L << 28,
                    1L << 30,
                    1000,
                    BoundedSazReader.EntryLimitForRecords(1000),
                    BoundedSazReader.DefaultMaxCentralDirectoryBytes),
                CancellationToken.None);
            Assert.Equal(0, read.SkippedIncompleteFrames);
            Assert.Equal(0, read.SkippedMalformedHeaderLines);
            return new[] { imported, Assert.Single(read.Records) };
        }

        private static string WriteArchive(string path, byte[] request, byte[] response)
        {
            using (FileStream file = File.Create(path))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "raw/0001_c.txt", request);
                WriteEntry(archive, "raw/0001_s.txt", response);
                WriteEntry(
                    archive,
                    "raw/0001_m.xml",
                    Encoding.UTF8.GetBytes(
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?><Session SID=\"1\" BitFlags=\"0\">" +
                            "<SessionTimers ClientBeginRequest=\"2026-01-01T00:00:00.0000000Z\" " +
                            "ClientDoneResponse=\"2026-01-01T00:00:01.0000000Z\" />" +
                            "<SessionFlags><SessionFlag N=\"x-clientip\" V=\"127.0.0.1\" /></SessionFlags></Session>"));
            }

            return path;
        }

        private static void WriteEntry(ZipArchive archive, string name, byte[] bytes)
        {
            using Stream stream = archive.CreateEntry(name).Open();
            stream.Write(bytes, 0, bytes.Length);
        }

        private sealed class TestDirectory : IDisposable
        {
            internal TestDirectory()
            {
                Path = System.IO.Path.Combine(
                    AppContext.BaseDirectory,
                    "SazTestArtifacts",
                    $"XMAT-repeated-{Guid.NewGuid():N}");
                Directory.CreateDirectory(Path);
            }

            internal string Path { get; }

            public void Dispose() => TestFolderCleanup.Delete(Path);
        }
    }
}
