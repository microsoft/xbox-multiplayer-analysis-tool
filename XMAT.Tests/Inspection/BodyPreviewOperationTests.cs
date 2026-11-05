// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.IO.Compression;
using System.Text;
using XMAT.Operations;
using XMAT.Operations.Inspection;

namespace XMAT.Tests.Inspection
{
    // BodyPreviewOperation, the bounded preview behind every body view: decoding, text, JSON and the limits, on in-memory
    // bodies only.
    public class BodyPreviewOperationTests
    {
        private const string JsonType = "application/json";
        private const BodyPreviewParts AllParts = BodyPreviewParts.Text | BodyPreviewParts.Json | BodyPreviewParts.Hex;

        [Theory]
        [InlineData("gzip")]
        [InlineData("x-gzip")]
        [InlineData("deflate")]
        [InlineData("br")]
        [InlineData("GZip, identity")]
        public void SupportedCodings_AreDecoded(string coding)
        {
            byte[] plain = Encoding.UTF8.GetBytes("h\u00e9llo w\u00f6rld");

            BodyPreview preview = Preview(Compress(coding, plain), "text/plain", coding);

            Assert.Equal((BodyPreviewState.Complete, BodyPreviewTextKind.Text), (preview.State, preview.TextKind));
            Assert.Equal("h\u00e9llo w\u00f6rld", preview.Text);
            Assert.Equal(("utf-8", plain.Length), (preview.Charset, preview.DecodedBytes));
            Assert.Empty(preview.Notices);
        }

        // HTTP's deflate is zlib; raw deflate is decoded once, as a fallback that says so.
        [Fact]
        public void RawDeflate_IsAFallbackThatSaysSo()
        {
            BodyPreview preview = Preview(Compress("raw", "raw body"u8.ToArray()), "text/plain", "deflate");

            Assert.Equal((BodyPreviewState.Complete, "raw body"), (preview.State, preview.Text));
            Assert.Equal(new[] { BodyPreviewNotice.RawDeflate }, preview.Notices);
        }

        [Theory]
        [InlineData("gzip")]
        [InlineData("deflate")]
        [InlineData("br")]
        public void CorruptCompressedData_IsMalformed_AndShownAsTheStoredBytes(string coding)
        {
            // A complete gzip header, then a deflate block of the reserved type 3; and bytes that are neither zlib nor deflate.
            byte[] corrupt = coding == "gzip"
                ? new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }
                : new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };

            BodyPreview preview = Preview(corrupt, "text/plain", coding);

            Assert.Equal((BodyPreviewState.Malformed, BodyPreviewTextKind.Bytes), (preview.State, preview.TextKind));
            Assert.Equal(Dump(corrupt), preview.Text);
            Assert.Equal(new[] { BodyPreviewNotice.MalformedCoding }, preview.Notices);
            Assert.Empty(preview.JsonDocuments);
        }

        [Theory]
        [InlineData("compress", null)]
        [InlineData("gzip, br", null)]
        [InlineData("zstd", null)]
        [InlineData(null, "gzip, chunked")]
        public void CodingsThatThePreviewDoesNotDecode_AreShownAsBytes(string contentEncoding, string transferEncoding)
        {
            byte[] body = "not decoded"u8.ToArray();

            BodyPreview preview = Preview(body, "text/plain", contentEncoding, transferEncoding);

            Assert.Equal((BodyPreviewState.Unsupported, BodyPreviewTextKind.Bytes), (preview.State, preview.TextKind));
            Assert.Equal(Dump(body), preview.Text);
            Assert.Equal(new[] { BodyPreviewNotice.UnsupportedCoding }, preview.Notices);
        }

        [Theory]
        [InlineData("text/plain; charset=utf-16le", "FF-FE-61-00-F1-00-62-00", "a\u00f1b", "utf-16le")]
        [InlineData("text/plain; charset=utf-16le", "61-00-F1-00-62-00", "a\u00f1b", "utf-16le")]
        [InlineData("text/plain; charset=UTF-16BE", "00-61-00-F1-00-62", "a\u00f1b", "utf-16be")]
        [InlineData("text/plain; charset=utf-16", "FE-FF-00-61-00-F1-00-62", "a\u00f1b", "utf-16be")]
        [InlineData("text/plain; charset=\"utf-8\"", "EF-BB-BF-63-61-66-C3-A9", "caf\u00e9", "utf-8")]
        [InlineData("text/plain", "EF-BB-BF-63-61-66-C3-A9", "caf\u00e9", "utf-8")]
        [InlineData(null, "FF-FE-61-00-62-00", "ab", "utf-16le")]
        [InlineData("text/plain; charset=iso-8859-1", "63-61-66-E9", "caf\u00e9", "iso-8859-1")]
        [InlineData("text/plain; charset=us-ascii", "61-62-63", "abc", "us-ascii")]
        public void Text_IsDecodedWithItsCharset(string contentType, string hex, string text, string charset)
        {
            BodyPreview preview = Preview(Convert.FromHexString(hex.Replace("-", "", StringComparison.Ordinal)), contentType);

            Assert.Equal((BodyPreviewState.Complete, BodyPreviewTextKind.Text), (preview.State, preview.TextKind));
            Assert.Equal((text, charset), (preview.Text, preview.Charset));
        }

        // Bytes that are not valid text are never replaced by question marks: malformed for a message that says it is text,
        // binary otherwise, and shown as bytes either way.
        [Theory]
        [InlineData("text/plain; charset=us-ascii", "63-61-66-E9", true)]
        [InlineData("text/plain", "63-C3-28", true)]
        [InlineData(JsonType, "7B-C3-28-7D", true)]
        [InlineData("image/png", "89-50-4E-47-0D-0A-1A-0A", false)]
        [InlineData(null, "61-62-00-63-64", false)]
        [InlineData("text/plain", "61-62-07-63", false)]
        public void BytesThatAreNotText_AreShownAsBytes(string contentType, string hex, bool malformed)
        {
            byte[] body = Convert.FromHexString(hex.Replace("-", "", StringComparison.Ordinal));
            BodyPreviewState state = malformed ? BodyPreviewState.Malformed : BodyPreviewState.Complete;
            string notice = malformed ? BodyPreviewNotice.MalformedText : BodyPreviewNotice.Binary;

            BodyPreview preview = Preview(body, contentType);

            Assert.Equal((state, BodyPreviewTextKind.Bytes, Dump(body)), (preview.State, preview.TextKind, preview.Text));
            Assert.Equal(new[] { notice }, preview.Notices);
            Assert.Empty(preview.JsonDocuments);
        }

        [Fact]
        public void ACharsetThatThePreviewDoesNotDecode_IsShownAsBytes()
        {
            BodyPreview preview = Preview("abc"u8.ToArray(), "text/plain; charset=windows-1252");

            Assert.Equal((BodyPreviewState.Unsupported, BodyPreviewTextKind.Bytes), (preview.State, preview.TextKind));
            Assert.Equal(new[] { BodyPreviewNotice.UnsupportedCharset }, preview.Notices);
        }

        // A cut never leaves half a character: not half a surrogate pair at the text limit, and not a partial UTF-8 sequence
        // at the decoded limit or at the end of a stored prefix, which is not invalid text.
        [Fact]
        public void Truncation_KeepsWholeCharacters()
        {
            BodyPreview textCut = Preview(Encoding.UTF8.GetBytes("ab\U0001F600cd"), "text/plain", limits: new(MaxTextChars: 3));
            BodyPreview decodedCut = Preview("a\u00e9b"u8.ToArray(), "text/plain", limits: new(MaxDecodedBytes: 2));
            BodyPreview prefix = Preview(new byte[] { 0x61, 0xC3 }, "text/plain", storedPrefix: true);

            Assert.Equal(("ab", BodyPreviewState.Truncated), (textCut.Text, textCut.State));
            Assert.Equal(new[] { BodyPreviewNotice.TextLimit }, textCut.Notices);
            Assert.Equal(("a", BodyPreviewState.Truncated), (decodedCut.Text, decodedCut.State));
            Assert.Equal(new[] { BodyPreviewNotice.DecodedLimit }, decodedCut.Notices);
            Assert.Equal(("a", BodyPreviewState.Truncated), (prefix.Text, prefix.State));
            Assert.Equal(new[] { BodyPreviewNotice.StoredPrefix }, prefix.Notices);
        }

        // A small compressed body that expands to 32 MiB decodes 4 MiB at most and shows 256 Ki characters, allocating a
        // bounded amount on the way.
        [Theory]
        [InlineData("gzip")]
        [InlineData("deflate")]
        [InlineData("br")]
        public void ACompressionBomb_StaysWithinTheLimits(string coding)
        {
            byte[] bomb = Compress(coding, Enumerable.Repeat((byte)'A', 32 * 1024 * 1024).ToArray());
            Assert.True(bomb.Length < 1024 * 1024);

            long before = GC.GetAllocatedBytesForCurrentThread();
            BodyPreview preview = Preview(bomb, "text/plain", coding);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(BodyPreviewLimits.Default.MaxDecodedBytes, preview.DecodedBytes);
            Assert.Equal(BodyPreviewLimits.Default.MaxTextChars, preview.Text.Length);
            Assert.Equal((BodyPreviewState.Truncated, bomb.Length), (preview.State, preview.StoredBytes));
            Assert.Equal(new[] { BodyPreviewNotice.DecodedLimit, BodyPreviewNotice.TextLimit }, preview.Notices);
            Assert.InRange(allocated, 0, 24L * 1024 * 1024);
        }

        // A large stored body is read only as far as the limits go, and never copied.
        [Fact]
        public void ALargePlainBody_IsReadOnlyAsFarAsTheLimitsAndNeverCopied()
        {
            byte[] body = Enumerable.Repeat((byte)'a', 20 * 1024 * 1024).ToArray();

            long before = GC.GetAllocatedBytesForCurrentThread();
            BodyPreview preview = Preview(body, "text/plain");
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(BodyPreviewState.Truncated, preview.State);
            Assert.Equal(
                new[] { BodyPreviewNotice.InputLimit, BodyPreviewNotice.DecodedLimit, BodyPreviewNotice.TextLimit },
                preview.Notices);
            Assert.Equal((BodyPreviewLimits.Default.MaxDecodedBytes, BodyPreviewLimits.Default.MaxDecodedBytes), (preview.InspectedBytes, preview.DecodedBytes));
            Assert.InRange(allocated, 0, 4L * 1024 * 1024);
        }

        [Fact]
        public void Text_HasAtMostMaxTextLinesLines()
        {
            byte[] body = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("x\n", 10_000)));

            BodyPreview preview = Preview(body, "text/plain");

            Assert.Equal(BodyPreviewLimits.Default.MaxTextLines, preview.Text.Split('\n').Length);
            Assert.Equal(new[] { BodyPreviewNotice.TextLimit }, preview.Notices);
        }

        // CR, CRLF (one break), U+2028 and U+2029 end lines as LF does.
        [Theory]
        [InlineData("\r")]
        [InlineData("\r\n")]
        [InlineData("\u2028")]
        [InlineData("\u2029")]
        public void EveryLineBreak_CountsTowardsTheLineLimit(string lineBreak)
        {
            byte[] body = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("x" + lineBreak, 10_000)));

            BodyPreview preview = Preview(body, "text/plain");

            Assert.Equal(BodyPreviewLimits.Default.MaxTextLines, preview.Text.Split(lineBreak).Length);
            Assert.Equal(new[] { BodyPreviewNotice.TextLimit }, preview.Notices);
        }

        [Fact]
        public void Json_IsFormattedAsWritten()
        {
            byte[] body = "{\"a\":[1,{\"b\":\"c\\u0041\"}],\"e\":{},\"f\":[],\"g\":null}"u8.ToArray();

            BodyPreview preview = Preview(body, JsonType + "; charset=utf-8");

            Assert.Equal((BodyPreviewState.Complete, BodyPreviewTextKind.Json), (preview.State, preview.TextKind));
            Assert.Equal(
                "{\r\n  \"a\": [\r\n    1,\r\n    {\r\n      \"b\": \"c\\u0041\"\r\n    }\r\n  ],\r\n  \"e\": {},\r\n  \"f\": [],\r\n" +
                    "  \"g\": null\r\n}",
                preview.Text);
            Assert.Empty(preview.Notices);
        }

        [Fact]
        public void JsonTree_LabelsNamesAndValues()
        {
            string longValue = new('v', 2000);
            byte[] body = Encoding.UTF8.GetBytes(
                "{\"s\":\"x\\u00e9\",\"n\":12.5,\"t\":true,\"f\":false,\"z\":null,\"o\":{},\"a\":[1],\"l\":\"" + longValue + "\"}");

            BodyPreview preview = Preview(body, "text/plain");

            BodyPreviewNode root = Assert.Single(preview.JsonDocuments);
            Assert.Equal("[root]", root.Label);
            Assert.Equal(
                new[] { "s=x\u00e9", "n=12.5", "t=true", "f=false", "z=null", "o", "a", "l=" + new string('v', 1024) + "\u2026" },
                root.Children.Select(child => child.Label));
            Assert.Equal("0=1", Assert.Single(root.Children[6].Children).Label);
            Assert.Equal(BodyPreviewTextKind.Text, preview.TextKind);
        }

        // Several documents share one budget of nodes; a document after the limit is left out and the limit is noticed.
        [Fact]
        public void SeveralJsonDocuments_ShareOneBudget()
        {
            byte[] body = "{\"a\":1}\r\n{\"b\":2}\r\n{\"c\":3}"u8.ToArray();

            BodyPreview all = Preview(body, JsonType);
            BodyPreview limited = Preview(body, JsonType, limits: new(MaxJsonNodes: 5));
            BodyPreview fewChars = Preview(body, JsonType, limits: new(MaxJsonChars: 12));

            Assert.Equal(new[] { "a=1", "b=2", "c=3" }, all.JsonDocuments.Select(root => Assert.Single(root.Children).Label));
            Assert.Equal("{\r\n  \"a\": 1\r\n}\r\n{\r\n  \"b\": 2\r\n}\r\n{\r\n  \"c\": 3\r\n}", all.Text);
            Assert.Equal(new[] { 1, 1, 0 }, limited.JsonDocuments.Select(root => root.Children.Count));
            Assert.Equal((BodyPreviewState.Truncated, true), (limited.State, limited.HasNotice(BodyPreviewNotice.JsonLimit)));
            Assert.Equal("a=1", Assert.Single(Assert.Single(fewChars.JsonDocuments).Children).Label);
            Assert.True(fewChars.HasNotice(BodyPreviewNotice.JsonLimit));
        }

        // Bytes that are not text are dumped up to MaxByteDumpBytes.
        [Fact]
        public void AByteDump_StopsAtItsLimit()
        {
            byte[] body = Enumerable.Range(0, 10_000).Select(value => (byte)(value % 7)).ToArray();

            BodyPreview preview = Preview(body, "application/octet-stream");

            Assert.Equal((BodyPreviewState.Truncated, BodyPreviewTextKind.Bytes), (preview.State, preview.TextKind));
            Assert.Equal(Dump(body.Take(BodyPreviewLimits.Default.MaxByteDumpBytes).ToArray()), preview.Text);
            Assert.Equal(new[] { BodyPreviewNotice.Binary, BodyPreviewNotice.TextLimit }, preview.Notices);
        }

        // A stored prefix that ends inside compressed data shows what decoded, never as invalid data.
        [Theory]
        [InlineData("gzip")]
        [InlineData("deflate")]
        [InlineData("br")]
        public void ACompressedPrefix_ShowsWhatDecoded(string coding)
        {
            string text = string.Concat(Enumerable.Range(0, 20_000).Select(value => value.ToString(System.Globalization.CultureInfo.InvariantCulture) + " "));
            byte[] compressed = Compress(coding, Encoding.ASCII.GetBytes(text));

            BodyPreview preview = Preview(compressed[..(compressed.Length / 2)], "text/plain", coding, storedPrefix: true);

            Assert.Equal((BodyPreviewState.Truncated, BodyPreviewTextKind.Text), (preview.State, preview.TextKind));
            Assert.StartsWith("0 1 2 3 4 5", preview.Text, StringComparison.Ordinal);
            Assert.StartsWith(preview.Text, text, StringComparison.Ordinal);
            Assert.Equal(new[] { BodyPreviewNotice.StoredPrefix }, preview.Notices);
        }

        // A whole stored body whose compressed data stops before its end (a missing gzip trailer or zlib checksum, a cut
        // Brotli stream) is told as incomplete: the decoders end quietly, so the preview checks the end itself.
        [Theory]
        [InlineData("gzip", 4)]
        [InlineData("gzip", 1000)]
        [InlineData("deflate", 4)]
        [InlineData("deflate", 1000)]
        [InlineData("br", 1000)]
        public void ACompressedBodyThatStopsEarly_IsIncomplete(string coding, int missing)
        {
            string text = "{\"items\":[" + string.Join(",", Enumerable.Range(0, 20_000)) + "]}";
            byte[] compressed = Compress(coding, Encoding.ASCII.GetBytes(text));

            BodyPreview preview = Preview(compressed[..^missing], JsonType, coding);

            Assert.Equal((BodyPreviewState.Truncated, BodyPreviewTextKind.Text), (preview.State, preview.TextKind));
            Assert.StartsWith(preview.Text, text, StringComparison.Ordinal);
            Assert.Empty(preview.JsonDocuments);
            Assert.Equal(new[] { BodyPreviewNotice.Incomplete, BodyPreviewNotice.JsonIncomplete }, preview.Notices);
        }

        // A gzip body is complete only when its deflate data ends with its whole eight-byte trailer right after it: an empty
        // member that has lost its ISIZE (whose CRC of zero equals its length of zero) is as incomplete as any other.
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(4)]
        [InlineData(7)]
        [InlineData(8)]
        public void AGzipBody_IsCompleteOnlyWithItsWholeTrailer(int missing)
        {
            byte[] empty = EmptyGzip[..^missing];
            byte[] text = Compress("gzip", "hello gzip"u8.ToArray())[..^missing];

            BodyPreview emptyPreview = Preview(empty, "text/plain", "gzip");
            BodyPreview textPreview = Preview(text, "text/plain", "gzip");

            string[] notices = missing == 0 ? Array.Empty<string>() : new[] { BodyPreviewNotice.Incomplete };
            BodyPreviewState state = missing == 0 ? BodyPreviewState.Complete : BodyPreviewState.Truncated;
            Assert.Equal((state, ""), (emptyPreview.State, emptyPreview.Text));
            Assert.Equal(notices, emptyPreview.Notices);
            Assert.Equal((state, "hello gzip"), (textPreview.State, textPreview.Text));
            Assert.Equal(notices, textPreview.Notices);
        }

        // A file name, comment, extra field and header CRC are read as zlib reads them.
        [Fact]
        public void AGzipHeaderWithOptionalFields_IsRead()
        {
            byte[] member = GzipMember("named"u8.ToArray(), extra: new byte[] { 1, 2, 3 }, name: "file.txt", comment: "a note", headerCrc: true);

            BodyPreview preview = Preview(member, "text/plain", "gzip");

            Assert.Equal((BodyPreviewState.Complete, "named"), (preview.State, preview.Text));
            Assert.Empty(preview.Notices);
        }

        // A gzip member that zlib would refuse is malformed: a wrong CRC-32, ISIZE or header CRC, reserved flags, or a next
        // member whose header is invalid.
        [Theory]
        [InlineData("crc")]
        [InlineData("isize")]
        [InlineData("header-crc")]
        [InlineData("reserved-flags")]
        [InlineData("invalid-next-member")]
        public void AGzipMemberThatZlibRefuses_IsMalformed(string damage)
        {
            // Without a header CRC for the reserved flags, which the header CRC would refuse on its own.
            byte[] member = GzipMember("checked"u8.ToArray(), name: "f", headerCrc: damage != "reserved-flags");
            switch (damage)
            {
                case "crc": member[^8] ^= 0x01; break;
                case "isize": member[^4] ^= 0x01; break;
                case "header-crc": member[13] ^= 0x01; break;
                case "reserved-flags": member[3] |= 0x20; break;
                default: member = member.Concat(new byte[] { 0x1F, 0x8B, 8, 0, 0, 0, 0, 0, 0, 3, 0xFF, 0xFF, 0xFF }).ToArray(); break;
            }

            BodyPreview preview = Preview(member, "text/plain", "gzip");

            Assert.Equal(BodyPreviewState.Malformed, preview.State);
            Assert.Equal(new[] { BodyPreviewNotice.MalformedCoding }, preview.Notices);
        }

        // Whatever follows the first gzip member, another member, a cut one or other bytes, everything that decoded is shown,
        // but the preview proves where only the first member ends.
        [Theory]
        [InlineData("member", "onetwo")]
        [InlineData("cut-member", "onetwo")]
        [InlineData("other-bytes", "one")]
        public void WhatFollowsTheFirstGzipMember_IsShownButNotProvenComplete(string follows, string text)
        {
            byte[] next = follows switch
            {
                "member" => Compress("gzip", "two"u8.ToArray()),
                "cut-member" => Compress("gzip", "two"u8.ToArray())[..^4],
                _ => Enumerable.Repeat((byte)'x', 32).ToArray(),
            };
            byte[] body = Compress("gzip", "one"u8.ToArray()).Concat(next).ToArray();

            BodyPreview preview = Preview(body, "text/plain", "gzip");

            Assert.Equal((BodyPreviewState.Truncated, text), (preview.State, preview.Text));
            Assert.Equal(new[] { BodyPreviewNotice.UnverifiedEnd }, preview.Notices);
        }

        // Each JSON pass is charged all of the body before it starts, whitespace included: with work left for one pass but
        // not two, the tree is built and the text is shown as it is.
        [Fact]
        public void EachJsonPass_IsChargedTheWholeBody()
        {
            string spaces = new(' ', 100_000);
            byte[] body = Encoding.ASCII.GetBytes("[" + spaces + "1" + spaces + "]");

            BodyPreview preview = Preview(body, JsonType, limits: new(MaxWork: (4L * body.Length) + 1000));

            Assert.Equal("0=1", Assert.Single(Assert.Single(preview.JsonDocuments).Children).Label);
            Assert.Equal((BodyPreviewState.Truncated, BodyPreviewTextKind.Text), (preview.State, preview.TextKind));
            Assert.Equal(new[] { BodyPreviewNotice.WorkLimit }, preview.Notices);
        }
        // Framing is checked to its last chunk even when the compressed data inside ends first; framing that stops before it
        // does not parse, so the body is shown as stored.
        [Theory]
        [InlineData("gzip")]
        [InlineData("deflate")]
        [InlineData("br")]
        public void AChunkedBodyWithoutItsLastChunk_IsNotComplete(string coding)
        {
            byte[] compressed = Compress(coding, "framed and compressed"u8.ToArray());
            byte[] body = Encoding.ASCII.GetBytes($"{compressed.Length:x}\r\n").Concat(compressed).Concat("\r\n"u8.ToArray()).ToArray();

            BodyPreview preview = Preview(body, "text/plain", coding, "chunked");

            Assert.Equal(BodyPreviewState.Malformed, preview.State);
            Assert.Equal(new[] { BodyPreviewNotice.FramingIgnored, BodyPreviewNotice.MalformedCoding }, preview.Notices);
        }

        // A tree stops at the depth and node limits; formatting a document deeper than the limit shows the body as it is, also
        // when the tree is not asked for.
        [Fact]
        public void DeepAndWideJson_StopAtTheLimits()
        {
            byte[] deep = Encoding.ASCII.GetBytes(new string('[', 100) + "1" + new string(']', 100));
            byte[] wide = Encoding.ASCII.GetBytes("[" + string.Join(",", Enumerable.Range(0, 5000)) + "]");

            BodyPreview deepPreview = Preview(deep, JsonType);
            BodyPreview deepText = Preview(deep, JsonType, parts: BodyPreviewParts.Text);
            BodyPreview widePreview = Preview(wide, JsonType);

            Assert.Equal(BodyPreviewLimits.Default.MaxJsonDepth, Depth(Assert.Single(deepPreview.JsonDocuments)));
            Assert.Equal((BodyPreviewState.Truncated, BodyPreviewTextKind.Text), (deepPreview.State, deepPreview.TextKind));
            Assert.Equal(Encoding.ASCII.GetString(deep), deepPreview.Text);
            Assert.Equal(new[] { BodyPreviewNotice.JsonLimit }, deepPreview.Notices);
            Assert.Equal((BodyPreviewState.Truncated, BodyPreviewTextKind.Text), (deepText.State, deepText.TextKind));
            Assert.Equal(new[] { BodyPreviewNotice.JsonLimit }, deepText.Notices);
            Assert.Equal(BodyPreviewLimits.Default.MaxJsonNodes, Count(Assert.Single(widePreview.JsonDocuments)));
            Assert.Equal((BodyPreviewState.Truncated, BodyPreviewTextKind.Json), (widePreview.State, widePreview.TextKind));
            Assert.Equal(BodyPreviewLimits.Default.MaxTextLines, widePreview.Text.Split('\n').Length);
            Assert.Equal(new[] { BodyPreviewNotice.JsonLimit, BodyPreviewNotice.TextLimit }, widePreview.Notices);
        }

        // Formatting adds indentation; the formatted text is bounded like any other.
        [Fact]
        public void JsonFormatting_StaysWithinTheTextLimit()
        {
            byte[] body = Encoding.ASCII.GetBytes("[" + string.Join(",", Enumerable.Repeat("[[[1]]]", 100)) + "]");

            BodyPreview preview = Preview(body, JsonType, limits: new(MaxTextChars: 1000));

            Assert.Equal((BodyPreviewTextKind.Json, 1000), (preview.TextKind, preview.Text.Length));
            Assert.StartsWith("[\r\n  [\r\n    [\r\n      [\r\n        1", preview.Text, StringComparison.Ordinal);
            Assert.Equal(new[] { BodyPreviewNotice.TextLimit }, preview.Notices);
        }

        // Documents followed by something that is not JSON: the tree keeps the complete documents, and the text is the body
        // as it is. Text that only starts with a number is not JSON at all.
        [Fact]
        public void JsonFollowedByOtherText_KeepsTheDocumentsBeforeIt()
        {
            byte[] body = "{\"a\":1} oops"u8.ToArray();

            BodyPreview preview = Preview(body, JsonType);
            BodyPreview plain = Preview("5 apples"u8.ToArray(), "text/plain");

            Assert.Equal("a=1", Assert.Single(Assert.Single(preview.JsonDocuments).Children).Label);
            Assert.Equal((BodyPreviewTextKind.Text, "{\"a\":1} oops"), (preview.TextKind, preview.Text));
            Assert.Equal(new[] { BodyPreviewNotice.JsonMalformed }, preview.Notices);
            Assert.Empty(plain.JsonDocuments);
            Assert.Empty(plain.Notices);
        }

        // Only part of the body: never formatted or parsed as if it were the whole document.
        [Fact]
        public void PartOfABody_IsNeverParsedAsJson()
        {
            byte[] body = "{\"a\":1,\"b\":2}"u8.ToArray();

            BodyPreview stored = Preview(body, JsonType, storedPrefix: true);
            BodyPreview input = Preview(body, JsonType, limits: new(MaxInputBytes: 8));
            BodyPreview plain = Preview("[1,2,"u8.ToArray(), null, storedPrefix: true);

            Assert.Empty(stored.JsonDocuments);
            Assert.Equal((BodyPreviewState.Truncated, BodyPreviewTextKind.Text, "{\"a\":1,\"b\":2}"), (stored.State, stored.TextKind, stored.Text));
            Assert.Equal(new[] { BodyPreviewNotice.StoredPrefix, BodyPreviewNotice.JsonIncomplete }, stored.Notices);
            Assert.Empty(input.JsonDocuments);
            Assert.Equal(new[] { BodyPreviewNotice.InputLimit, BodyPreviewNotice.JsonIncomplete }, input.Notices);
            Assert.Equal(new[] { BodyPreviewNotice.StoredPrefix, BodyPreviewNotice.JsonIncomplete }, plain.Notices);
        }

        // A missing body is never shown as an empty one.
        [Fact]
        public void EmptyAndNotRetainedBodies_AreTold()
        {
            var markers = new Dictionary<string, string>
            {
                ["x-xmat-response-body-not-kept"] = "1048576",
                ["content-type"] = JsonType,
            };

            BodyPreview empty = Preview(Array.Empty<byte>(), JsonType);
            BodyPreview notRetained = BodyPreviewOperation.Build(
                BodyPreviewRequest.FromHeaders(markers, ReadOnlyMemory<byte>.Empty, BodyPreviewSide.Response), CancellationToken.None);

            Assert.Equal((BodyPreviewState.Empty, BodyPreviewTextKind.None, ""), (empty.State, empty.TextKind, empty.Text));
            Assert.Empty(empty.Notices);
            Assert.Equal((BodyPreviewState.NotRetained, BodyPreviewTextKind.None), (notRetained.State, notRetained.TextKind));
        }

        // A head can come from a peer, so a body-not-kept marker hides nothing: it counts only on an empty body of its own
        // side, and stored bytes are shown even when a caller says the body was not kept.
        [Theory]
        [InlineData(false, "X-XMAT-Response-Body-Not-Kept", "secret", "Complete")]
        [InlineData(true, "X-XMAT-Request-Body-Not-Kept", "secret", "Complete")]
        [InlineData(false, "X-XMAT-Request-Body-Not-Kept", "", "Empty")]
        [InlineData(true, "X-XMAT-Response-Body-Not-Kept", "", "Empty")]
        [InlineData(true, "X-XMAT-Request-Body-Not-Kept", "", "NotRetained")]
        [InlineData(false, "x-xmat-response-body-not-kept", "", "NotRetained")]
        public void ABodyNotKeptMarker_NeverHidesABody(bool request, string marker, string body, string state)
        {
            var headers = new Dictionary<string, string> { [marker] = "1048576", ["Content-Type"] = "text/plain" };
            BodyPreviewSide side = request ? BodyPreviewSide.Request : BodyPreviewSide.Response;
            BodyPreviewRequest fromHeaders = BodyPreviewRequest.FromHeaders(headers, Encoding.ASCII.GetBytes(body), side);

            BodyPreview preview = BodyPreviewOperation.Build(fromHeaders, CancellationToken.None);
            BodyPreview told = BodyPreviewOperation.Build(
                new BodyPreviewRequest("secret"u8.ToArray(), "text/plain", NotRetained: true), CancellationToken.None);

            Assert.Equal(state == "NotRetained", fromHeaders.NotRetained);
            Assert.Equal((Enum.Parse<BodyPreviewState>(state), body), (preview.State, preview.Text));
            Assert.Equal((BodyPreviewState.Complete, "secret"), (told.State, told.Text));
        }

        [Theory]
        [InlineData(false, "chunked", "5\r\nhello\r\n6\r\n world\r\n0\r\n\r\n", false, "hello world", null)]
        [InlineData(false, "chunked", "hello world", false, "hello world", null)]
        [InlineData(true, "chunked", "5\r\nhello\r\n0\r\n\r\n", false, "5\r\nhello\r\n0\r\n\r\n", null)]
        [InlineData(false, "chunked", "5\r\nhelloXX0\r\n\r\n", false, "5\r\nhelloXX0\r\n\r\n", BodyPreviewNotice.FramingIgnored)]
        [InlineData(false, "chunked", "a\r\n01234", true, "01234", BodyPreviewNotice.StoredPrefix)]
        [InlineData(false, null, "5\r\nhello\r\n0\r\n\r\n", false, "5\r\nhello\r\n0\r\n\r\n", null)]
        public void ChunkFraming_IsDecodedOnlyWhereItIs(
            bool decoded, string transferEncoding, string body, bool storedPrefix, string text, string notice)
        {
            BodyFraming framing = decoded ? BodyFraming.Decoded : BodyFraming.Advisory;
            BodyPreview preview = BodyPreviewOperation.Build(
                new BodyPreviewRequest(
                    Encoding.ASCII.GetBytes(body), "text/plain", null, transferEncoding, framing, StoredPrefix: storedPrefix),
                CancellationToken.None);

            Assert.Equal(text, preview.Text);
            Assert.Equal(notice == null ? Array.Empty<string>() : new[] { notice }, preview.Notices);
        }

        [Fact]
        public void AChunkedCompressedBody_IsDeframedThenDecoded()
        {
            byte[] gzip = Compress("gzip", "framed and compressed"u8.ToArray());
            byte[] body = Encoding.ASCII.GetBytes($"{gzip.Length:x}\r\n").Concat(gzip).Concat("\r\n0\r\n\r\n"u8.ToArray()).ToArray();

            BodyPreview preview = Preview(body, "text/plain", "gzip", "chunked");

            Assert.Equal((BodyPreviewState.Complete, "framed and compressed"), (preview.State, preview.Text));
        }

        [Fact]
        public void HeaderValuesLongerThanTheLimit_AreNotUsed()
        {
            string longValue = new('x', BodyPreviewLimits.Default.MaxHeaderChars + 1);

            BodyPreview encoding = Preview("abc"u8.ToArray(), "text/plain", "gzip;" + longValue);
            BodyPreview type = Preview("{\"a\":1}"u8.ToArray(), JsonType + ";" + longValue);

            Assert.Equal(BodyPreviewState.Unsupported, encoding.State);
            Assert.Equal(new[] { BodyPreviewNotice.HeaderLimit, BodyPreviewNotice.UnsupportedCoding }, encoding.Notices);
            Assert.Equal((BodyPreviewTextKind.Text, "{\"a\":1}"), (type.TextKind, type.Text));
            Assert.Equal(new[] { BodyPreviewNotice.HeaderLimit }, type.Notices);
        }

        // The work limit stops a preview where it is; it is never followed by the raw-deflate retry.
        [Theory]
        [InlineData("deflate")]
        [InlineData("raw")]
        public void TheWorkLimit_StopsThePreview_AndIsNeverRetried(string coding)
        {
            byte[] body = Compress(coding, Enumerable.Repeat((byte)'b', 100_000).ToArray());

            BodyPreview preview = Preview(body, "text/plain", "deflate", limits: new(MaxWork: 200));

            Assert.Equal(BodyPreviewState.Truncated, preview.State);
            Assert.Contains(BodyPreviewNotice.WorkLimit, preview.Notices);
            Assert.DoesNotContain(BodyPreviewNotice.RawDeflate, preview.Notices);

            // What was read and what was decoded never add up to more work than allowed.
            Assert.InRange(preview.DecodedBytes + preview.InspectedBytes, 0, 200);
        }

        [Fact]
        public void TheHexPart_IsTheStartOfTheStoredBody_AndNeverChangesTheState()
        {
            byte[] small = { 0x00, 0x10, 0xFF };
            byte[] large = Enumerable.Repeat((byte)'a', 70_000).ToArray();

            BodyPreview smallPreview = Preview(small, "application/octet-stream", parts: BodyPreviewParts.Hex);
            BodyPreview largePreview = Preview(large, "text/plain", parts: AllParts);

            Assert.Equal(("00 10 FF", BodyPreviewTextKind.None), (smallPreview.Hex, smallPreview.TextKind));
            Assert.Equal(BodyPreviewLimits.Default.MaxTextLines, largePreview.Hex.Split("\r\n").Length);
            Assert.Equal((BodyPreviewState.Complete, 70_000), (largePreview.State, largePreview.Text.Length));
            Assert.Equal(new[] { BodyPreviewNotice.HexLimit }, largePreview.Notices);
        }

        // The Hex part is work too: it shows only what the work limit allows, and counts as read.
        [Fact]
        public void TheHexPart_IsWithinTheWorkLimit()
        {
            byte[] body = Enumerable.Range(0, 100).Select(value => (byte)value).ToArray();

            BodyPreview preview = Preview(body, null, parts: BodyPreviewParts.Hex, limits: new(MaxWork: 16));

            Assert.Equal(Dump(body[..16]), preview.Hex);
            Assert.Equal(16, preview.InspectedBytes);
            Assert.Equal(new[] { BodyPreviewNotice.HexLimit }, preview.Notices);
        }

        // A body meant as JSON, by its media type or because it starts like an object or array, that is not valid JSON says so,
        // and is shown as it is: never formatted, and never repaired.
        [Theory]
        [InlineData("{\"a\":", JsonType)]
        [InlineData("[1,2,]", JsonType)]
        [InlineData("{\"a\":1,}", JsonType)]
        [InlineData("{\"a\":", "text/plain")]
        public void InvalidJson_IsToldAndShownAsItIs(string body, string contentType)
        {
            BodyPreview preview = Preview(Encoding.ASCII.GetBytes(body), contentType);
            BodyPreview textOnly = Preview(Encoding.ASCII.GetBytes(body), contentType, parts: BodyPreviewParts.Text);

            Assert.Equal((BodyPreviewState.Complete, BodyPreviewTextKind.Text, body), (preview.State, preview.TextKind, preview.Text));
            Assert.Empty(preview.JsonDocuments);
            Assert.Equal(new[] { BodyPreviewNotice.JsonMalformed }, preview.Notices);
            Assert.Equal(contentType == JsonType ? new[] { BodyPreviewNotice.JsonMalformed } : Array.Empty<string>(), textOnly.Notices);
        }

        // A byte dump in place of the text is work too, and the stored bytes that it shows count as read.
        [Fact]
        public void AByteDump_IsWithinTheWorkLimit()
        {
            byte[] body = "not decoded at all"u8.ToArray();

            BodyPreview all = Preview(body, "text/plain", "compress");
            BodyPreview limited = Preview(body, "text/plain", "compress", limits: new(MaxWork: 4));

            Assert.Equal((body.Length, Dump(body)), (all.InspectedBytes, all.Text));
            Assert.Equal((4, Dump(body[..4])), (limited.InspectedBytes, limited.Text));
            Assert.Equal(new[] { BodyPreviewNotice.UnsupportedCoding, BodyPreviewNotice.WorkLimit }, limited.Notices);
        }

        [Fact]
        public void LimitsAreNeverRaisedAboveTheDefaults()
        {
            byte[] body = Enumerable.Repeat((byte)'a', 300 * 1024).ToArray();

            BodyPreview preview = Preview(body, "text/plain", limits: new(MaxTextChars: int.MaxValue));

            Assert.Equal(BodyPreviewLimits.Default.MaxTextChars, preview.Text.Length);
        }

        [Fact]
        public void TheStoredBody_IsNeverChanged()
        {
            byte[] gzip = Compress("gzip", "{\"a\":1}"u8.ToArray());
            byte[] framed = "7\r\n{\"a\":1}\r\n0\r\n\r\n"u8.ToArray();
            byte[][] copies = { gzip.ToArray(), framed.ToArray() };

            Preview(gzip, JsonType, "gzip", parts: AllParts);
            Preview(framed, JsonType, transferEncoding: "chunked", parts: AllParts);

            Assert.Equal(copies[0], gzip);
            Assert.Equal(copies[1], framed);
        }

        [Fact]
        public async Task ACancelledPreview_ReturnsCancelled_AndNeverAPartialPreview()
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var request = new BodyPreviewRequest("abc"u8.ToArray(), "text/plain");

            OperationResult<BodyPreview> result = await new BodyPreviewOperation().ExecuteAsync(request, OperationContext.None, cancelled.Token);

            Assert.Equal(OperationOutcome.Cancelled, result.Outcome);
            Assert.Equal(BodyPreviewNotice.Cancelled, Assert.Single(result.Diagnostics).Code);
            Assert.Null(result.Value);
            Assert.Throws<OperationCanceledException>(() => BodyPreviewOperation.Build(request, cancelled.Token));
        }

        // The work never runs on the caller's thread: ExecuteAsync returns before a slow preview is done.
        [Fact]
        public async Task APreview_RunsOffTheCallersThread()
        {
            byte[] bomb = Compress("gzip", Enumerable.Repeat((byte)'A', 32 * 1024 * 1024).ToArray());
            var request = new BodyPreviewRequest(bomb, "text/plain", "gzip");

            Task<OperationResult<BodyPreview>> running = new BodyPreviewOperation().ExecuteAsync(request, OperationContext.None, CancellationToken.None);
            bool completedOnReturn = running.IsCompleted;
            OperationResult<BodyPreview> result = await running;

            Assert.False(completedOnReturn);
            Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
            Assert.Equal(BodyPreviewLimits.Default.MaxDecodedBytes, result.Value.DecodedBytes);
        }

        private static BodyPreview Preview(
            byte[] body,
            string contentType,
            string contentEncoding = null,
            string transferEncoding = null,
            bool storedPrefix = false,
            BodyPreviewParts parts = BodyPreviewParts.Text | BodyPreviewParts.Json,
            BodyPreviewLimits limits = null) =>
            BodyPreviewOperation.Build(
                new BodyPreviewRequest(
                    body, contentType, contentEncoding, transferEncoding, BodyFraming.Advisory, false, storedPrefix, parts, limits),
                CancellationToken.None);

        // An empty gzip member: header, an empty final stored block and a trailer of zero CRC and length.
        private static readonly byte[] EmptyGzip = Convert.FromHexString("1F8B0800000000000003" + "0300" + "00000000" + "00000000");

        // A gzip member built by hand around raw deflate data, with the optional header fields that GZipStream never writes.
        // Its CRC-32 values come from GZipStream's own trailers, not from the code under test.
        private static byte[] GzipMember(byte[] data, byte[] extra = null, string name = null, string comment = null, bool headerCrc = false)
        {
            byte flags = (byte)((extra != null ? 4 : 0) | (name != null ? 8 : 0) | (comment != null ? 16 : 0) | (headerCrc ? 2 : 0));
            var header = new List<byte> { 0x1F, 0x8B, 8, flags, 0, 0, 0, 0, 0, 3 };
            if (extra != null)
            {
                header.AddRange(new[] { (byte)extra.Length, (byte)(extra.Length >> 8) });
                header.AddRange(extra);
            }

            foreach (string text in new[] { name, comment }.Where(text => text != null))
            {
                header.AddRange(Encoding.ASCII.GetBytes(text));
                header.Add(0);
            }

            if (headerCrc)
            {
                header.AddRange(Crc32(header.ToArray())[..2]);
            }

            return header.Concat(Compress("raw", data)).Concat(Crc32(data)).Concat(BitConverter.GetBytes((uint)data.Length)).ToArray();
        }

        // The CRC-32 of data as GZipStream writes it in its trailer (little-endian); zero for no data.
        private static byte[] Crc32(byte[] data) => data.Length == 0 ? new byte[4] : Compress("gzip", data)[^8..^4];
        private static byte[] Compress(string coding, byte[] plain)
        {
            using var output = new MemoryStream();
            using (Stream compressor = coding.Split(',')[0].Trim().ToUpperInvariant() switch
            {
                "GZIP" or "X-GZIP" => new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true),
                "DEFLATE" => new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true),
                "RAW" => new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true),
                "BR" => new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true),
                _ => throw new ArgumentOutOfRangeException(nameof(coding)),
            })
            {
                compressor.Write(plain);
            }

            return output.ToArray();
        }

        private static string Dump(byte[] bytes) =>
            string.Join(
                "\r\n",
                bytes.Chunk(16).Select(line => string.Join(" ", line.Select(value => value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)))));

        private static int Depth(BodyPreviewNode node) => 1 + (node.Children.Count == 0 ? 0 : node.Children.Max(Depth));

        private static int Count(BodyPreviewNode node) => 1 + node.Children.Sum(Count);
    }
}
