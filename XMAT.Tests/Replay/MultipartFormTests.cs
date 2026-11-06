// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.RegularExpressions;
using XMAT.Operations;
using XMAT.Operations.Replay;
using XMAT.WebServiceCapture;

namespace XMAT.Tests.Replay
{
    // Slice I4's multipart forms (MultipartForm): the bytes that a form is, how its names, file names and types are written
    // or refused, its limits, measured before anything of their size is allocated, its random boundary, cancellation within
    // one unit of work, and how a form is applied to a request. Contract: files/slice-i4-contracts-v3.md, M1-M11.
    public sealed class MultipartFormTests
    {
        private const int Unit = MultipartForm.WorkChunk;

        private static readonly byte[] D0 = Draw(0);
        private static readonly byte[] D1 = Draw(1);
        private static readonly string B0 = BoundaryOf(D0);
        private static readonly string B1 = BoundaryOf(D1);

        // M1: the exact bytes of a text part, a typed text part, a file part, a repeated name with an empty value and an
        // empty typed file, and the closing delimiter; every part's framing is what it measures.
        [Fact]
        public void Build_WritesTheFormsBytes_ExactlyAsMeasured()
        {
            byte[] file = { 0x00, 0xFF, (byte)'\r', (byte)'\n' };
            MultipartPart[] parts =
            {
                Text("field", "value"),
                Text("typed", "line1\r\nline2", "text/plain; charset=utf-8"),
                FilePart("upload", "data.bin", file),
                Text("field", ""),
                FilePart("empty", "empty.txt", Array.Empty<byte>(), "text/plain"),
            };

            MultipartFormBody form = MultipartForm.Build(parts, CancellationToken.None, new Draws(D0).Next);

            byte[] expected = Concat(
                Ascii($"--{B0}\r\nContent-Disposition: form-data; name=\"field\"\r\n\r\nvalue\r\n"),
                Ascii($"--{B0}\r\nContent-Disposition: form-data; name=\"typed\"\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nline1\r\nline2\r\n"),
                Ascii($"--{B0}\r\nContent-Disposition: form-data; name=\"upload\"; filename=\"data.bin\"\r\nContent-Type: application/octet-stream\r\n\r\n"),
                file,
                Ascii("\r\n"),
                Ascii($"--{B0}\r\nContent-Disposition: form-data; name=\"field\"\r\n\r\n\r\n"),
                Ascii($"--{B0}\r\nContent-Disposition: form-data; name=\"empty\"; filename=\"empty.txt\"\r\nContent-Type: text/plain\r\n\r\n\r\n"),
                Ascii($"--{B0}--\r\n"));
            Assert.Equal(expected, form.Body);
            Assert.Equal((B0, "multipart/form-data; boundary=" + B0), (form.Boundary, form.ContentType));
            Assert.Equal(form.Body.LongLength, MultipartForm.Length(parts));
            Assert.Equal(parts, form.Parts);
            Assert.Equal($"--{B0}\r\nContent-Disposition: form-data; name=\"field\"\r\n\r\n".Length + 2, parts[0].FramingLength);
            Assert.All(parts, part => Assert.Equal(
                MultipartForm.PartFramingLength(part.Kind == MultipartPartKind.File, part.Name, part.FileName, part.MediaType),
                part.FramingLength));
            Assert.Equal(48, MultipartForm.ClosingLength);
        }

        // M2: a name is written in a quoted-string, its quote and backslash escaped.
        [Theory]
        [InlineData("a\"b", "a\\\"b")]
        [InlineData("a\\b", "a\\\\b")]
        [InlineData(" ~!", " ~!")]
        public void Names_AreWrittenInAQuotedString(string name, string written)
        {
            MultipartFormBody form = MultipartForm.Build(new[] { Text(name, "v") }, CancellationToken.None);

            Assert.Contains($"name=\"{written}\"\r\n", Encoding.ASCII.GetString(form.Body), StringComparison.Ordinal);
        }

        // M2: a name that is empty, too long or not printable ASCII is refused, never escaped away.
        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("a\rb")]
        [InlineData("a\nb")]
        [InlineData("a\tb")]
        [InlineData("a\u007fb")]
        [InlineData("caf\u00e9")]
        [InlineData("\u0000")]
        public void Names_ThatAreNotPrintableAscii_AreRefused(string name)
        {
            Assert.Equal(MultipartCodes.InvalidName, Code(() => Text(name, "v")));
            Assert.Equal(MultipartCodes.InvalidName, Code(() => FilePart(name, "f.bin", new byte[1])));
        }

        [Fact]
        public void Names_AreAtMost256Characters()
        {
            Assert.Equal(256, Text(new string('n', 256), "v").Name.Length);
            Assert.Equal(MultipartCodes.InvalidName, Code(() => Text(new string('n', 257), "v")));
        }

        // M3: a file name is written as its UTF-8 bytes, those from 0x80 and every '%' percent-encoded in uppercase hex, a
        // quote escaped; never filename*. Its measured framing is what is written.
        [Theory]
        [InlineData("report.pdf", "report.pdf")]
        [InlineData("100%.txt", "100%25.txt")]
        [InlineData("caf\u00e9.txt", "caf%C3%A9.txt")]
        [InlineData("\U0001F600.png", "%F0%9F%98%80.png")]
        [InlineData("a\"b.txt", "a\\\"b.txt")]
        public void FileNames_ArePercentEncoded_AsRfc7578Says(string fileName, string written)
        {
            MultipartPart part = FilePart("f", fileName, new byte[] { 1 });
            MultipartFormBody form = MultipartForm.Build(new[] { part }, CancellationToken.None, new Draws(D0).Next);
            string body = Encoding.ASCII.GetString(form.Body);

            Assert.Contains($"; filename=\"{written}\"\r\n", body, StringComparison.Ordinal);
            Assert.DoesNotContain("filename*", body, StringComparison.Ordinal);
            Assert.Equal(form.Body.LongLength, MultipartForm.Length(new[] { part }));
        }

        // M3: only a file's own name, well-formed and without controls, is a file name; a lone surrogate is refused before
        // any spelling of it is made.
        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData(".")]
        [InlineData("..")]
        [InlineData("a/b")]
        [InlineData("a\\b")]
        [InlineData("a\u0001b")]
        [InlineData("a\u0085b")]
        [InlineData("a\u007fb")]
        [InlineData("lone-high-start")]
        [InlineData("lone-low")]
        [InlineData("lone-high-end")]
        public void FileNames_ThatAreNotAFilesOwnWellFormedName_AreRefused(string fileName)
        {
            fileName = fileName switch
            {
                "lone-high-start" => "\ud800.txt",
                "lone-low" => "a\udc00",
                "lone-high-end" => "a\ud800",
                _ => fileName,
            };

            Assert.False(MultipartForm.IsValidFileName(fileName));
            Assert.Equal(MultipartCodes.InvalidFileName, Code(() => FilePart("f", fileName, new byte[1])));
        }

        [Fact]
        public void FileNames_AreAtMost255Characters()
        {
            Assert.Equal(255, FilePart("f", new string('a', 255), new byte[1]).FileName.Length);
            Assert.Equal(MultipartCodes.InvalidFileName, Code(() => FilePart("f", new string('a', 256), new byte[1])));
        }

        // M4: a media type is TYPE/SUBTYPE with optional parameters, written as given; a file part without one is
        // application/octet-stream, and a text part without one has no Content-Type line.
        [Theory]
        [InlineData("text/plain")]
        [InlineData("application/json; charset=utf-8")]
        [InlineData("text/plain;charset=\"utf-8\"")]
        [InlineData("a/b; x=\"q\\\"d\"")]
        [InlineData("a/b \t; x=1")]
        public void MediaTypes_AreWrittenAsGiven(string mediaType)
        {
            MultipartFormBody form = MultipartForm.Build(new[] { Text("t", "v", mediaType) }, CancellationToken.None);

            Assert.Contains($"\r\nContent-Type: {mediaType}\r\n\r\nv\r\n", Encoding.ASCII.GetString(form.Body), StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("")]
        [InlineData("text")]
        [InlineData("text/")]
        [InlineData("/plain")]
        [InlineData("text/plain\r\nX-Injected: 1")]
        [InlineData("text/plain; x")]
        [InlineData("text/plain; x=")]
        [InlineData("text/pl\u00e4in")]
        [InlineData("text/plain; x=\"\u00e4\"")]
        [InlineData("text/plain ")]
        [InlineData("text/plain;")]
        [InlineData("text/plain; x=\"open")]
        public void MediaTypes_ThatAreNotValid_AreRefused(string mediaType)
        {
            Assert.Equal(MultipartCodes.InvalidMediaType, Code(() => Text("t", "v", mediaType)));
            Assert.Equal(MultipartCodes.InvalidMediaType, Code(() => FilePart("f", "f.bin", new byte[1], mediaType)));
        }

        [Fact]
        public void MediaTypes_DefaultForFilesOnly_AndAreAtMost256Characters()
        {
            string body = Encoding.ASCII.GetString(MultipartForm.Build(
                new[] { Text("t", "v"), FilePart("f", "f.bin", new byte[] { 0x41 }) }, CancellationToken.None).Body);

            Assert.Contains("name=\"t\"\r\n\r\nv\r\n", body, StringComparison.Ordinal);
            Assert.Contains("filename=\"f.bin\"\r\nContent-Type: application/octet-stream\r\n\r\nA\r\n", body, StringComparison.Ordinal);
            Assert.True(MultipartForm.IsValidMediaType("a/" + new string('b', 254)));
            Assert.False(MultipartForm.IsValidMediaType("a/" + new string('b', 255)));
        }

        // M5: a form has 1 to 128 parts.
        [Fact]
        public void Build_TakesOneTo128Parts()
        {
            MultipartPart[] parts = Enumerable.Range(0, 129).Select(index => Text("p" + index, "v")).ToArray();

            Assert.Equal(MultipartCodes.Empty, Code(() => MultipartForm.Build(Array.Empty<MultipartPart>(), CancellationToken.None)));
            Assert.Equal(MultipartCodes.TooManyParts, Code(() => MultipartForm.Build(parts, CancellationToken.None)));
            Assert.Equal(128, MultipartForm.Build(parts[..128], CancellationToken.None).Parts.Count);
        }

        // M5: exactly 64 MiB is accepted. One byte more, from framing alone, since the content fits by itself, is refused
        // before anything of the body's size is allocated.
        [Fact]
        public void Build_AcceptsExactly64MiB_AndRefusesOneByteMoreOfFraming_BeforeAllocating()
        {
            long framing = MultipartForm.PartFramingLength(true, "f", "f.bin", null);
            byte[] content = new byte[MultipartForm.MaxBodyBytes - framing - MultipartForm.ClosingLength];
            MultipartPart[] exact = { FilePart("f", "f.bin", content) };
            MultipartPart[] over = { FilePart("ff", "f.bin", content) };

            Assert.Equal(MultipartForm.MaxBodyBytes, MultipartForm.Build(exact, CancellationToken.None).Body.LongLength);
            Assert.Equal(MultipartForm.MaxBodyBytes + 1, MultipartForm.Length(over));
            long before = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(MultipartCodes.TooLarge, Code(() => MultipartForm.Build(over, CancellationToken.None)));
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(allocated < 1024 * 1024, $"Refusing the form allocated {allocated} bytes.");
        }

        // M5: texts that are each valid but together larger than 64 MiB are measured without encoding them and refused
        // before any of them is encoded.
        [Fact]
        public void Build_RefusesTextsThatTogetherExceed64MiB_BeforeEncodingAny()
        {
            string value = new('\u4e00', 8 * 1024 * 1024);
            long before = GC.GetAllocatedBytesForCurrentThread();
            MultipartPart[] parts = { Text("a", value), Text("b", value), Text("c", value) };
            long measuring = GC.GetAllocatedBytesForCurrentThread() - before;
            before = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(MultipartCodes.TooLarge, Code(() => MultipartForm.Build(parts, CancellationToken.None)));
            long refusing = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.All(parts, part => Assert.Equal(24L * 1024 * 1024, part.ContentLength));
            Assert.True(measuring < 1024 * 1024, $"Measuring the texts allocated {measuring} bytes.");
            Assert.True(refusing < 1024 * 1024, $"Refusing the form allocated {refusing} bytes.");
        }

        // M6: the boundary is "xmat-form-" and the 32 lowercase hex digits of 16 random bytes; a test seam must give exactly
        // 16 bytes.
        [Fact]
        public void Boundary_IsSixteenRandomBytes_AndASeamMustGiveExactlySixteen()
        {
            MultipartPart[] parts = { Text("a", "b") };

            Assert.Matches("^xmat-form-[0-9a-f]{32}$", MultipartForm.Build(parts, CancellationToken.None).Boundary);
            Assert.Throws<InvalidOperationException>(() => MultipartForm.Build(parts, CancellationToken.None, () => new byte[15]));
            Assert.Throws<InvalidOperationException>(() => MultipartForm.Build(parts, CancellationToken.None, () => new byte[17]));
            Assert.Throws<InvalidOperationException>(() => MultipartForm.Build(parts, CancellationToken.None, () => null));
        }

        // M6: a boundary that occurs in a part's content or header lines is drawn again.
        [Theory]
        [InlineData("text")]
        [InlineData("file")]
        [InlineData("name")]
        [InlineData("file-name")]
        [InlineData("type")]
        public void Boundary_ThatAPartHolds_IsDrawnAgain(string where)
        {
            MultipartPart part = where switch
            {
                "text" => Text("a", "x" + B0 + "y"),
                "file" => FilePart("a", "a.bin", Ascii("x" + B0 + "y")),
                "name" => Text(B0, "v"),
                "file-name" => FilePart("a", B0 + ".txt", new byte[1]),
                _ => Text("a", "v", "a/b; x=" + B0),
            };
            var draws = new Draws(D0, D1);

            MultipartFormBody form = MultipartForm.Build(new[] { part }, CancellationToken.None, draws.Next);

            Assert.Equal((B1, 2), (form.Boundary, draws.Count));
        }

        // M6: at most five boundaries are drawn: the fifth is used when it does not collide, and five that collide refuse
        // the form.
        [Fact]
        public void Boundary_IsDrawnAtMostFiveTimes()
        {
            byte[][] draws = Enumerable.Range(0, 5).Select(Draw).ToArray();
            string fourCollide = string.Concat(draws.Take(4).Select(BoundaryOf));
            string fiveCollide = string.Concat(draws.Select(BoundaryOf));
            var fifth = new Draws(draws);
            var none = new Draws(draws);

            MultipartFormBody form = MultipartForm.Build(new[] { Text("a", fourCollide) }, CancellationToken.None, fifth.Next);
            string code = Code(() => MultipartForm.Build(new[] { Text("a", fiveCollide) }, CancellationToken.None, none.Next));

            Assert.Equal((BoundaryOf(draws[4]), 5), (form.Boundary, fifth.Count));
            Assert.Equal((MultipartCodes.BoundaryCollision, 5), (code, none.Count));
        }

        // M6: a boundary across the edge of two windows of scanning is found, in a file's bytes and in a text, wherever it
        // starts around the overlap: whole in the first window, or across its end.
        [Theory]
        [InlineData(true, 42)]
        [InlineData(true, 41)]
        [InlineData(true, 20)]
        [InlineData(true, 1)]
        [InlineData(false, 42)]
        [InlineData(false, 41)]
        [InlineData(false, 20)]
        [InlineData(false, 1)]
        public void Boundary_AcrossTwoScanUnits_IsFound(bool file, int beforeTheEdge)
        {
            int at = Unit - beforeTheEdge;
            MultipartPart part;
            if (file)
            {
                byte[] bytes = new byte[2 * Unit];
                Ascii(B0).CopyTo(bytes, at);
                part = FilePart("a", "a.bin", bytes);
            }
            else
            {
                part = Text("a", new string('a', at) + B0 + new string('a', Unit));
            }

            var draws = new Draws(D0, D1);

            Assert.Equal(B1, MultipartForm.Build(new[] { part }, CancellationToken.None, draws.Next).Boundary);
            Assert.Equal(2, draws.Count);
        }

        // M7: text is strict UTF-8. CountText is exact across the edge of two units, a surrogate pair split by it included,
        // and -1 for a lone surrogate anywhere; Build encodes a pair split between two units whole.
        [Fact]
        public void Text_IsStrictUtf8_CountedAndEncodedExactlyAcrossUnits()
        {
            string split = new string('a', Unit - 1) + "\U0001F600" + "b";
            var random = new Random(4);
            string[] mixed = Enumerable.Range(0, 50).Select(_ => string.Concat(Enumerable.Range(0, random.Next(0, 40))
                .Select(_ => random.Next(4) switch { 0 => "a", 1 => "\u00e9", 2 => "\u4e00", _ => "\U0001F600" }))).ToArray();

            Assert.Equal(Unit - 1 + 4 + 1, MultipartForm.CountText(split, CancellationToken.None));
            Assert.All(mixed, value => Assert.Equal(Encoding.UTF8.GetByteCount(value), MultipartForm.CountText(value, CancellationToken.None)));
            Assert.Equal(-1, MultipartForm.CountText(new string('a', Unit - 1) + "\ud800" + "a", CancellationToken.None));
            Assert.Equal(-1, MultipartForm.CountText("\udc00a", CancellationToken.None));
            Assert.Equal(-1, MultipartForm.CountText("a\ud800", CancellationToken.None));
            Assert.Equal(MultipartCodes.InvalidText, Code(() => Text("a", "x\ud800y")));

            MultipartFormBody form = MultipartForm.Build(new[] { Text("a", split) }, CancellationToken.None, new Draws(D0).Next);
            Assert.Equal(
                Concat(Ascii($"--{B0}\r\nContent-Disposition: form-data; name=\"a\"\r\n\r\n"), Encoding.UTF8.GetBytes(split), Ascii($"\r\n--{B0}--\r\n")),
                form.Body);
        }

        // M8: the body is the form's own: changing the arrays and the list that it was built from changes nothing in it.
        [Fact]
        public void Body_IsIndependentOfItsInputs()
        {
            byte[] bytes = { 1, 2, 3 };
            var parts = new List<MultipartPart> { FilePart("f", "f.bin", bytes) };
            MultipartFormBody form = MultipartForm.Build(parts, CancellationToken.None);
            byte[] built = form.Body.ToArray();

            bytes[0] = 9;
            parts.Clear();

            Assert.Equal(built, form.Body);
            Assert.Single(form.Parts);
        }

        // M9: a strict reader gets back every part as written: the names, the file names as written (percent-encoded: no
        // reader is assumed to decode them), the types and the contents.
        [Fact]
        public void Body_ReadsBackAsItsParts()
        {
            byte[] file = Enumerable.Range(0, 300).Select(value => (byte)value).ToArray();
            MultipartPart[] parts =
            {
                Text("q\"uote", "first\r\nline"),
                FilePart("upload", "r\u00e9sum\u00e9 100%.pdf", file, "application/pdf"),
                Text("empty", ""),
            };
            MultipartFormBody form = MultipartForm.Build(parts, CancellationToken.None);

            List<(string Disposition, string Type, byte[] Content)> read = ReadBack(form.Body, form.Boundary);

            Assert.Equal(
                new[]
                {
                    "form-data; name=\"q\\\"uote\"",
                    "form-data; name=\"upload\"; filename=\"r%C3%A9sum%C3%A9 100%25.pdf\"",
                    "form-data; name=\"empty\"",
                },
                read.Select(part => part.Disposition));
            Assert.Equal(new[] { null, "application/pdf", null }, read.Select(part => part.Type));
            Assert.Equal(Encoding.UTF8.GetBytes("first\r\nline"), read[0].Content);
            Assert.Equal(file, read[1].Content);
            Assert.Empty(read[2].Content);
        }

        // M9: sent to a loopback server, the form arrives as its exact bytes with its Content-Type.
        [Fact]
        public async Task Form_ArrivesAsItsExactBytes()
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok());
            MultipartFormBody form = MultipartForm.Build(
                new[] { Text("a", "b"), FilePart("f", "f.bin", new byte[] { 0, 1, 2 }) }, CancellationToken.None);
            var draft = new RequestReplayDraft { Method = "POST", Url = server.Origin + "/upload", HeadersText = "Content-Type: text/plain" };

            OperationResult<ReplayResult> result = await new ReplayOperation().ExecuteAsync(
                new ReplayRequest(ReplaySource.FromDraft(draft), new ReplayOptions(AllowUnsafeMethods: true), new ReplayEdits(Form: form)),
                OperationContext.None,
                CancellationToken.None);

            Assert.True(result.Succeeded);
            LoopbackRequest received = Assert.Single(server.Requests);
            Assert.Equal(form.Body, received.Body);
            Assert.Equal(form.ContentType, received.Header("Content-Type"));
            Assert.Single(received.Headers, header => header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));
        }

        // M10: a token cancelled before the work starts stops CountText, Text and Build before any unit runs, and Build
        // allocates nothing of the body's size.
        [Fact]
        public void Cancelled_BeforeTheWork_RunsNoUnit()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var units = new Units(null);
            MultipartPart[] parts = { FilePart("f", "f.bin", new byte[3 * Unit]) };

            Assert.Throws<OperationCanceledException>(() => MultipartForm.CountText("abc", cancellation.Token, units.Seen));
            Assert.Throws<OperationCanceledException>(() => MultipartPart.Text("a", "abc", null, cancellation.Token, units.Seen));
            Assert.Throws<OperationCanceledException>(() => MultipartForm.Build(Array.Empty<MultipartPart>(), cancellation.Token));
            long before = GC.GetAllocatedBytesForCurrentThread();
            Assert.Throws<OperationCanceledException>(() => MultipartForm.Build(parts, cancellation.Token, null, units.Seen));
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Empty(units.All);
            Assert.True(allocated < 1024 * 1024, $"A cancelled build allocated {allocated} bytes.");
        }

        // M10: cancelled in the first unit of measuring, CountText stops before another unit.
        [Fact]
        public void Cancelled_WhileMeasuring_StopsWithinAUnit()
        {
            using var cancellation = new CancellationTokenSource();
            var units = new Units(cancellation, MultipartStage.Measure);

            Assert.Throws<OperationCanceledException>(
                () => MultipartForm.CountText(new string('a', 3 * Unit), cancellation.Token, units.Seen));

            Assert.Equal(new[] { (MultipartStage.Measure, 0, (long)Unit) }, units.All);
        }

        // M10: cancelled in the first unit of scanning a file's bytes or a text, Build stops before another unit and
        // writes nothing; cancelled in the first unit of writing, by copying a file or encoding a text, it stops before
        // another unit and returns nothing.
        [Theory]
        [InlineData(false, true)]
        [InlineData(false, false)]
        [InlineData(true, true)]
        [InlineData(true, false)]
        public void Cancelled_WhileScanningOrWriting_StopsWithinAUnit(bool writing, bool file)
        {
            MultipartStage stage = writing ? MultipartStage.Write : MultipartStage.Scan;
            MultipartPart part = file ? FilePart("f", "f.bin", new byte[3 * Unit]) : Text("t", new string('a', 3 * Unit));
            using var cancellation = new CancellationTokenSource();
            var units = new Units(cancellation, stage);

            Assert.Throws<OperationCanceledException>(
                () => MultipartForm.Build(new[] { part }, cancellation.Token, null, units.Seen));

            var scanned = units.All.Where(unit => unit.Stage == MultipartStage.Scan).ToArray();
            var written = units.All.Where(unit => unit.Stage == MultipartStage.Write).ToArray();
            if (stage == MultipartStage.Scan)
            {
                Assert.Equal(new[] { (MultipartStage.Scan, 0, (long)Unit) }, scanned);
                Assert.Empty(written);
            }
            else
            {
                Assert.Equal(3L * Unit, scanned[^1].Done);
                Assert.Equal(new[] { (MultipartStage.Write, 0, (long)Unit) }, written);
            }
        }

        // Every unit of work is at most WorkChunk characters or bytes: a measuring unit ends before a surrogate pair rather
        // than after it; scanning windows of WorkChunk each start BoundaryLength - 1 before the end of the one before; and
        // writing goes WorkChunk at a time.
        [Fact]
        public void Units_AreAtMostOneWorkChunk()
        {
            const long Overlap = MultipartForm.BoundaryLength - 1;
            var measured = new Units(null);
            MultipartForm.CountText(new string('a', Unit - 1) + "\U0001F600" + new string('a', Unit), CancellationToken.None, measured.Seen);
            var file = new Units(null);
            MultipartForm.Build(new[] { FilePart("f", "f.bin", new byte[3 * Unit]) }, CancellationToken.None, null, file.Seen);
            var text = new Units(null);
            MultipartForm.Build(new[] { Text("t", new string('a', 3 * Unit)) }, CancellationToken.None, null, text.Seen);

            Assert.Equal(new long[] { Unit - 1, 2L * Unit - 1, 2L * Unit + 1 }, measured.All.Select(unit => unit.Done));
            long[] windows = { Unit, 2L * Unit - Overlap, 3L * Unit - 2 * Overlap, 3L * Unit };
            long[] writes = { Unit, 2L * Unit, 3L * Unit };
            foreach (Units built in new[] { file, text })
            {
                Assert.Equal(windows, built.All.Where(unit => unit.Stage == MultipartStage.Scan).Select(unit => unit.Done));
                Assert.Equal(writes, built.All.Where(unit => unit.Stage == MultipartStage.Write).Select(unit => unit.Done));
            }
        }

        // M10: cancelled while a boundary is drawn, after the form was admitted, Build stops before scanning with it.
        [Fact]
        public void Cancelled_WhileDrawing_StopsBeforeScanning()
        {
            using var cancellation = new CancellationTokenSource();
            var units = new Units(null);
            byte[] Cancel()
            {
                cancellation.Cancel();
                return Draw(0);
            }

            Assert.Throws<OperationCanceledException>(() => MultipartForm.Build(
                new[] { FilePart("f", "f.bin", new byte[3 * Unit]) }, cancellation.Token, Cancel, units.Seen));

            Assert.Empty(units.All);
        }

        // M11: the form's Content-Type replaces every Content-Type line in the first one's place, or is appended; every
        // other line keeps its name, value and order.
        [Theory]
        [InlineData("A: 1\r\nContent-Type: text/plain\r\nB: 2\r\ncontent-type: x\r\nC: 3", "A: 1\r\nContent-Type: multipart/form-data; boundary=Z\r\nB: 2\r\nC: 3")]
        [InlineData("A: 1\r\nB:  2 ", "A: 1\r\nB: 2\r\nContent-Type: multipart/form-data; boundary=Z")]
        [InlineData("", "Content-Type: multipart/form-data; boundary=Z")]
        [InlineData("Connection: keep-alive, Content-Typex", "Connection: keep-alive, Content-Typex\r\nContent-Type: multipart/form-data; boundary=Z")]
        public void WithContentType_ReplacesEveryContentTypeLine_AndKeepsTheOthers(string headers, string expected)
        {
            Assert.Equal(expected, MultipartForm.WithContentType(headers, "multipart/form-data; boundary=Z"));
        }

        // M11: applying a form sets its Content-Type and completes only the body; a malformed header block, a Connection
        // header that nominates Content-Type, in any case or position, or a header block still left out refuses it, and the
        // request is unchanged.
        [Theory]
        [InlineData("X-A: 1\r\nBad Line", "format")]
        [InlineData("Connection: content-type", MultipartCodes.ContentTypeDropped)]
        [InlineData("Connection: keep-alive, Content-Type", MultipartCodes.ContentTypeDropped)]
        [InlineData("Connection: close\r\nX-A: 1\r\nConnection:  CONTENT-TYPE ,x", MultipartCodes.ContentTypeDropped)]
        [InlineData(null, MultipartCodes.HeadersOmitted)]
        public void ApplyTo_RefusesARequestThatWouldLoseTheFormsContentType_AndChangesNothing(string headers, string refusal)
        {
            RequestReplayDraft draft = headers == null ? Template() : new RequestReplayDraft { HeadersText = headers };
            draft.SetBodyBytes(new byte[] { 7 });
            (string Headers, byte[] Body, RequestOmissions Omissions) before = (draft.HeadersText, draft.GetBodyBytes(), draft.Omissions);
            MultipartFormBody form = MultipartForm.Build(new[] { Text("a", "b") }, CancellationToken.None);

            if (refusal == "format")
                Assert.Throws<FormatException>(() => MultipartForm.ApplyTo(draft, form));
            else
                Assert.Equal(refusal, Code(() => MultipartForm.ApplyTo(draft, form)));

            Assert.Equal(before, (draft.HeadersText, draft.GetBodyBytes(), draft.Omissions));
        }

        [Fact]
        public void ApplyTo_SetsTheContentType_AndCompletesOnlyTheBody()
        {
            RequestReplayDraft draft = Template();
            draft.ReplaceHeaders("X-A: 1");
            MultipartFormBody form = MultipartForm.Build(new[] { Text("a", "b") }, CancellationToken.None);

            MultipartForm.ApplyTo(draft, form);

            Assert.Equal("X-A: 1\r\nContent-Type: " + form.ContentType, draft.HeadersText);
            Assert.Same(form.Body, draft.GetBodyBytes());
            Assert.Equal((false, true, false, false), (draft.Omissions.Method, draft.Omissions.Url, draft.Omissions.Headers, draft.Omissions.Body));
        }

        // M11: whether a Connection line nominates a header is exactly whether building the request drops it.
        [Fact]
        public void ConnectionNominations_AreWhatBuildingTheRequestDrops()
        {
            var random = new Random(11);
            string[] names = { "X-Test", "x-test", "X-TEST", "X-Other", "X-Testy", "keep-alive", "close" };
            for (int round = 0; round < 200; round++)
            {
                var lines = new List<string> { "X-Test: 1" };
                int connections = random.Next(0, 3);
                for (int index = 0; index < connections; index++)
                {
                    IEnumerable<string> listed = Enumerable.Range(0, random.Next(1, 4)).Select(_ => names[random.Next(names.Length)]);
                    lines.Insert(random.Next(lines.Count + 1), "Connection: " + string.Join(random.Next(2) == 0 ? "," : " , ", listed));
                }

                var draft = new RequestReplayDraft { Url = "http://127.0.0.1:9/", HeadersText = string.Join("\r\n", lines) };
                using HttpRequestMessage message = draft.CreateRequest();

                Assert.Equal(
                    !message.Headers.Contains("X-Test"),
                    RequestReplayDraft.IsNominatedByConnection(RequestReplayDraft.ParseHeaderText(draft.HeadersText), "X-Test"));
            }
        }

        private static MultipartPart Text(string name, string value, string mediaType = null) =>
            MultipartPart.Text(name, value, mediaType, CancellationToken.None);

        private static MultipartPart FilePart(string name, string fileName, byte[] bytes, string mediaType = null) =>
            MultipartPart.File(name, fileName, bytes, mediaType);

        private static string Code(Action action) => Assert.Throws<MultipartFormException>(action).Code;

        private static string Code(Func<object> action) => Assert.Throws<MultipartFormException>(action).Code;

        private static byte[] Draw(int seed) => Enumerable.Range(seed * 16, 16).Select(value => (byte)value).ToArray();

        private static string BoundaryOf(byte[] draw) => "xmat-form-" + Convert.ToHexString(draw).ToLowerInvariant();

        private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

        private static byte[] Concat(params byte[][] pieces) => pieces.SelectMany(piece => piece).ToArray();

        // A request file's request that leaves out its URL, headers and body, as a template saves it.
        private static RequestReplayDraft Template() => RequestReplayDraft.FromRequestFile(RequestFileReader.Read(Encoding.UTF8.GetBytes(
            "{ \"schema\": \"xmat-request/1\", \"profile\": \"template\", \"method\": \"POST\", \"url\": null, \"httpVersion\": \"auto\", " +
            "\"headers\": null, \"body\": null, \"omitted\": [\"url\", \"headers\", \"body\"] }")).Content);

        // Reads a form strictly back into its parts: each Content-Disposition and Content-Type value as written, and content.
        private static List<(string Disposition, string Type, byte[] Content)> ReadBack(byte[] body, string boundary)
        {
            string text = Encoding.Latin1.GetString(body);
            Assert.StartsWith("--" + boundary + "\r\n", text, StringComparison.Ordinal);
            Assert.EndsWith("\r\n--" + boundary + "--\r\n", text, StringComparison.Ordinal);
            string inner = text[(boundary.Length + 4)..^(boundary.Length + 8)];
            var parts = new List<(string, string, byte[])>();
            foreach (string part in inner.Split("\r\n--" + boundary + "\r\n"))
            {
                int end = part.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                string[] head = part[..end].Split("\r\n");
                Assert.All(head, line => Assert.Matches(new Regex("^[A-Za-z-]+: [\\x20-\\x7e]*$"), line));
                string Value(string name) => head.Where(line => line.StartsWith(name + ": ", StringComparison.Ordinal))
                    .Select(line => line[(name.Length + 2)..]).SingleOrDefault();
                parts.Add((Value("Content-Disposition"), Value("Content-Type"), Encoding.Latin1.GetBytes(part[(end + 4)..])));
            }

            return parts;
        }

        // A random seam that gives these draws in turn and counts them.
        private sealed class Draws
        {
            private readonly byte[][] _draws;

            internal Draws(params byte[][] draws) => _draws = draws;

            internal int Count { get; private set; }

            internal byte[] Next() => _draws[Count++].ToArray();
        }

        // A progress seam that records every unit, and cancels the token, without throwing, in the first unit of a stage.
        private sealed class Units
        {
            private readonly CancellationTokenSource _cancellation;
            private readonly MultipartStage? _cancelIn;

            internal Units(CancellationTokenSource cancellation, MultipartStage? cancelIn = null)
            {
                _cancellation = cancellation;
                _cancelIn = cancelIn;
            }

            internal List<(MultipartStage Stage, int Part, long Done)> All { get; } = new();

            internal void Seen(MultipartStage stage, int part, long done)
            {
                All.Add((stage, part, done));
                if (stage == _cancelIn)
                    _cancellation.Cancel();
            }
        }
    }
}
