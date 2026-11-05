// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Reflection;
using System.Text;
using System.Text.Json;
using XMAT.Operations;
using XMAT.Operations.Replay;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Replay
{
    // VerifyDiffOperation without any network: a captured response and a replay's, built in memory.
    public sealed class VerifyDiffOperationTests
    {
        private const long DecodeLimit = 64L * 1024 * 1024;
        private const string TextType = "Content-Type: text/plain\r\n";
        private const string JsonType = "Content-Type: application/json\r\n";

        // The detail never changes the verdict: it is verify's own, from the same comparison.
        [Theory]
        [InlineData(200, "same", 200, "same")]
        [InlineData(200, "same", 503, "same")]
        [InlineData(200, "same", 200, "other")]
        [InlineData(200, "same", 404, "other")]
        public void Verdict_IsVerifysOwn(int originalStatus, string originalBody, int actualStatus, string actualBody)
        {
            ProxyConnectionModel original = Original(TextType, Utf8(originalBody), originalStatus.ToString());
            ReplayExchange actual = Actual(Utf8(actualBody), actualStatus);

            VerifyDiff diff = Diff(original, actual);

            Assert.Equal(ReplayVerifier.Compare(original, actual, DecodeLimit).Differences, diff.Verification.Differences);
            Assert.Equal(((int?)originalStatus, actualStatus), (diff.OriginalStatus, diff.ActualStatus));
        }

        [Fact]
        public void StatusOnly_ShowsAnEqualBody()
        {
            VerifyDiff diff = Diff(Original(TextType, Utf8("same")), Actual(Utf8("same"), 503));

            Assert.Equal(new[] { ReplayVerifier.StatusDifference }, diff.Verification.Differences);
            Assert.Equal((true, null, null, null), (diff.Body.Equal, diff.Body.Text, diff.Body.Binary, diff.Body.Json));
            Assert.Equal(VerifyDiffState.Complete, diff.State);
            Assert.Empty(diff.Limitations);
            Assert.Equal((0L, 0L), (diff.Original.ExaminedBytes, diff.Actual.ExaminedBytes));
        }

        [Fact]
        public void Text_ShowsTheChangedLinesWithThreeLinesOfContext()
        {
            string original = string.Concat(Enumerable.Range(1, 20).Select(line => $"line {line}\n"));

            VerifyDiff diff = Diff(Original(TextType, Utf8(original)), Actual(Utf8(original.Replace("line 10\n", "line ten\n"))));

            VerifyTextHunk hunk = Assert.Single(diff.Body.Text.Hunks);
            Assert.Equal(VerifyEquivalence.Different, diff.Body.Text.Equivalence);
            Assert.Equal((7, 7, 7, 7), (hunk.OriginalStart, hunk.OriginalCount, hunk.ActualStart, hunk.ActualCount));
            Assert.Equal(
                new[] { "  line 7", "  line 8", "  line 9", "- line 10", "+ line ten", "  line 11", "  line 12", "  line 13" },
                hunk.Lines.Select(Render));
            Assert.All(hunk.Lines, line => Assert.Equal(("\n", false), (line.Break, line.Cut)));
            Assert.Equal((VerifyDiffState.Complete, "utf-8", "utf-8"), (diff.State, diff.Original.Charset, diff.Actual.Charset));
            Assert.Null(diff.Body.Binary);
        }

        // A hunk at the start or end of a text keeps the context it has, and a side with no lines names the line it follows.
        [Fact]
        public void Text_AddedLinesAtTheEndFollowTheLastLine()
        {
            VerifyDiff diff = Diff(Original(TextType, Utf8("a\nb\n")), Actual(Utf8("a\nb\nc\n")));

            VerifyTextHunk hunk = Assert.Single(diff.Body.Text.Hunks);
            Assert.Equal((1, 2, 1, 3), (hunk.OriginalStart, hunk.OriginalCount, hunk.ActualStart, hunk.ActualCount));
            Assert.Equal(new[] { "  a", "  b", "+ c" }, hunk.Lines.Select(Render));

            VerifyTextHunk inserted = Assert.Single(Diff(Original(TextType, Utf8("")), Actual(Utf8("x\n"))).Body.Text.Hunks);
            Assert.Equal((0, 0, 1, 1), (inserted.OriginalStart, inserted.OriginalCount, inserted.ActualStart, inserted.ActualCount));
        }

        [Fact]
        public void Text_LinesThatDifferOnlyInTheirBreakDiffer()
        {
            VerifyDiff diff = Diff(Original(TextType, Utf8("a\r\nb\r\n")), Actual(Utf8("a\nb\n")));

            Assert.Equal(VerifyEquivalence.Different, diff.Body.Text.Equivalence);
            VerifyTextLine[] lines = Assert.Single(diff.Body.Text.Hunks).Lines.ToArray();
            Assert.Equal(
                new[] { ("a", "\r\n"), ("b", "\r\n") },
                lines.Where(line => line.Kind == VerifyLineKind.Removed).Select(line => (line.Text, line.Break)));
            Assert.Equal(
                new[] { ("a", "\n"), ("b", "\n") },
                lines.Where(line => line.Kind == VerifyLineKind.Added).Select(line => (line.Text, line.Break)));
        }

        // Equal text in another charset, or with a byte order mark, is the same text: only its bytes differ.
        [Theory]
        [InlineData("utf-8-bom", "utf-8")]
        [InlineData("utf-16le", "utf-16le")]
        public void Text_OnlyTheCharsetOrByteOrderMarkDiffers(string encoding, string charset)
        {
            const string Text = "h\u00e9llo\nw\u00f6rld\n";
            byte[] original = encoding == "utf-16le"
                ? Encoding.Unicode.GetBytes(Text)
                : new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Utf8(Text)).ToArray();

            VerifyDiff diff = Diff(Original($"Content-Type: text/plain; charset={charset}\r\n", original), Actual(Utf8(Text)));

            Assert.Equal(new[] { ReplayVerifier.BodyDifference }, diff.Verification.Differences);
            Assert.Equal(VerifyEquivalence.Same, diff.Body.Text.Equivalence);
            Assert.Empty(diff.Body.Text.Hunks);
            Assert.Equal((charset, "utf-8", VerifyDiffState.Complete), (diff.Original.Charset, diff.Actual.Charset, diff.State));
        }

        // A side examined only in part may go on as the other does: its lines ending first prove nothing, and the texts are
        // never called the same.
        [Fact]
        public void Text_ASideExaminedInPartIsNeverProvenTheSameOrDifferentByItsEnd()
        {
            string text = string.Concat(Enumerable.Repeat("line\n", 50));
            var limits = new VerifyDiffLimits(MaxExaminedBytes: 200);

            VerifyDiff diff = Diff(
                Original(TextType, Utf8(text)),
                Actual(Encoding.Unicode.GetBytes(text), contentType: "text/plain; charset=utf-16le"),
                limits);

            Assert.Equal(VerifyEquivalence.Unknown, diff.Body.Text.Equivalence);
            Assert.Empty(diff.Body.Text.Hunks);
            Assert.Contains(VerifyDiffLimitation.ExaminedLimit, diff.Limitations);
            Assert.Equal(VerifyDiffState.Limited, diff.State);
            Assert.Equal((250L, 200L, 500L, 200L), (diff.Original.ComparedBytes, diff.Original.ExaminedBytes, diff.Actual.ComparedBytes, diff.Actual.ExaminedBytes));

            VerifyDiff changed = Diff(
                Original(TextType, Utf8(text)),
                Actual(Encoding.Unicode.GetBytes("line\nline\nlime\n" + text[15..]), contentType: "text/plain; charset=utf-16le"),
                limits);

            Assert.Equal(VerifyEquivalence.Different, changed.Body.Text.Equivalence);
            Assert.Contains(
                changed.Body.Text.Hunks.SelectMany(hunk => hunk.Lines),
                line => line.Kind == VerifyLineKind.Added && line.Text == "lime");

            // The same with the sides the other way: the original runs out of lines first.
            VerifyDiff swapped = Diff(
                Original("Content-Type: text/plain; charset=utf-16le\r\n", Encoding.Unicode.GetBytes(text)),
                Actual(Utf8(text)),
                limits);

            Assert.Equal(VerifyEquivalence.Unknown, swapped.Body.Text.Equivalence);
            Assert.Empty(swapped.Body.Text.Hunks);
        }

        // The examined bytes can stop inside a line: that line is left out, so a cut line never shows as a difference.
        [Fact]
        public void Text_ALineCutByTheExaminedLimitIsLeftOut()
        {
            const string Text = "line1\nline2\nabcdXYZ\n";

            VerifyDiff diff = Diff(
                Original(TextType, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Utf8(Text)).ToArray()),
                Actual(Utf8(Text)),
                new VerifyDiffLimits(MaxExaminedBytes: 16));

            Assert.Equal(VerifyEquivalence.Unknown, diff.Body.Text.Equivalence);
            Assert.Empty(diff.Body.Text.Hunks);
        }

        // Changes with fewer than seven unchanged lines between them share one hunk.
        [Fact]
        public void Text_ChangesCloseTogetherShareAHunk()
        {
            string original = string.Concat(Enumerable.Range(1, 20).Select(line => $"l{line}\n"));
            string actual = original.Replace("l5\n", "five\n").Replace("l11\n", "eleven\n");

            VerifyTextHunk hunk = Assert.Single(Diff(Original(TextType, Utf8(original)), Actual(Utf8(actual))).Body.Text.Hunks);

            Assert.Equal((2, 13, 2, 13), (hunk.OriginalStart, hunk.OriginalCount, hunk.ActualStart, hunk.ActualCount));
        }

        // Equal hashes never prove equal lines: two different lines whose hashes collide still differ.
        [Fact]
        public void Lines_WithCollidingHashesAreComparedByTheirText()
        {
            var seen = new Dictionary<int, string>();
            string first = null;
            string second = null;
            for (int index = 0; first == null && index < 2_000_000; index++)
            {
                string text = index.ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
                int hash = VerifyDiffTextSide.LineHash(text, VerifyDiffTextSide.LineFeed);
                if (seen.TryGetValue(hash, out string other))
                {
                    (first, second) = (other, text);
                }
                else
                {
                    seen.Add(hash, text);
                }
            }

            Assert.NotNull(first);
            VerifyDiff diff = Diff(Original(TextType, Utf8($"same\n{first}\n")), Actual(Utf8($"same\n{second}\n")));

            Assert.Equal(VerifyEquivalence.Different, diff.Body.Text.Equivalence);
            Assert.Equal(
                new[] { "- " + first, "+ " + second },
                Assert.Single(diff.Body.Text.Hunks).Lines.Where(line => line.Kind != VerifyLineKind.Context).Select(Render));
        }

        [Fact]
        public void Values_NeverEndInsideASurrogatePair()
        {
            string text = new string('x', 1023) + "\U0001F600" + "tail";

            VerifyDiff diff = Diff(Original(TextType, Utf8(text + "\n")), Actual(Utf8("other\n")));

            VerifyTextLine removed = Assert.Single(diff.Body.Text.Hunks).Lines.First(line => line.Kind == VerifyLineKind.Removed);
            Assert.Equal((1023, true), (removed.Text.Length, removed.Cut));
            Assert.Equal(VerifyDiffJson.Serialize(diff).Length, diff.SerializedBytes);
        }

        [Fact]
        public void Binary_ShowsTheDifferingRunsAndTheBytesOnlyTheLongerSideHas()
        {
            byte[] original = new byte[64];
            byte[] actual = original.Concat(new byte[] { 0xAA, 0xBB }).ToArray();
            actual[10] = 1;
            actual[11] = 2;
            actual[40] = 9;

            VerifyDiff diff = Diff(
                Original("Content-Type: application/octet-stream\r\n", original),
                Actual(actual, contentType: "application/octet-stream"));

            Assert.Null(diff.Body.Text);
            Assert.Equal((64L, 66L), (diff.Body.Binary.OriginalLength, diff.Body.Binary.ActualLength));
            Assert.Equal(
                new[]
                {
                    new VerifyByteRange(10, 2, 2, "00 00", "01 02"),
                    new VerifyByteRange(40, 1, 1, "00", "09"),
                    new VerifyByteRange(64, 0, 2, string.Empty, "AA BB"),
                },
                diff.Body.Binary.Ranges);
            Assert.Equal((null, null), (diff.Original.Charset, diff.Actual.Charset));
            Assert.Equal(VerifyDiffState.Complete, diff.State);
        }

        // Excerpts are read only from the examined bytes: bytes beyond them show as lengths alone.
        [Fact]
        public void Binary_ExcerptsComeOnlyFromTheExaminedBytes()
        {
            VerifyDiff diff = Diff(
                Original("Content-Type: application/octet-stream\r\n", new byte[40]),
                Actual(new byte[60]),
                new VerifyDiffLimits(MaxExaminedBytes: 32));

            VerifyByteRange tail = Assert.Single(diff.Body.Binary.Ranges);
            Assert.Equal(
                (40L, 0L, 20L, string.Empty, string.Empty),
                (tail.Offset, tail.OriginalLength, tail.ActualLength, tail.Original, tail.Actual));
            Assert.Contains(VerifyDiffLimitation.ExaminedLimit, diff.Limitations);
        }

        [Theory]
        [InlineData("b\r\n{\"ok\":true}\r\n0\r\n\r\n", "Removed")]
        [InlineData("b\r\n{\"ok\":true}\r\n", "NotRemoved")]
        [InlineData("{\"ok\":true}", "None")]
        public void Sides_SayHowTheirFramingWasRead(string stored, string framing)
        {
            VerifyDiff diff = Diff(
                Original(JsonType + "Transfer-Encoding: chunked\r\n", Utf8(stored)), Actual(Utf8("{\"ok\":false}")));

            Assert.Equal((Enum.Parse<VerifyFraming>(framing), VerifyFraming.None), (diff.Original.Framing, diff.Actual.Framing));
        }

        [Fact]
        public void Sides_BeyondTheDecodeLimitAreStoredBytes()
        {
            VerifyDiff diff = VerifyDiffOperation.Compare(
                new VerifyDiffRequest(
                    Original(TextType + "Content-Encoding: gzip\r\n", ReplayCoreTests.Gzip(new byte[4096])), Actual(new byte[4096]), 1024),
                CancellationToken.None);

            Assert.Equal((VerifyBodyBasis.DecodedLimit, VerifyDecodeEnd.NotApplicable), (diff.Original.Basis, diff.Original.End));
            Assert.NotNull(diff.Body.Binary);
        }

        // Stored bytes that verify compared because it did not decode them are never shown as the content.
        [Theory]
        [InlineData("compress", "UnsupportedCoding")]
        [InlineData("gzip, br", "UnsupportedCoding")]
        [InlineData("gzip", "MalformedCoding")]
        public void StoredBytes_AreShownAsBytes(string coding, string basis)
        {
            VerifyDiff diff = Diff(
                Original(TextType + $"Content-Encoding: {coding}\r\n", Utf8("hello\n")), Actual(Utf8("hello world\n")));

            Assert.Equal((Enum.Parse<VerifyBodyBasis>(basis), VerifyBodyBasis.Plain), (diff.Original.Basis, diff.Actual.Basis));
            Assert.Equal(VerifyDecodeEnd.NotApplicable, diff.Original.End);
            Assert.Null(diff.Body.Text);
            Assert.NotNull(diff.Body.Binary);
        }

        // Each side keeps its own basis, and its own lines and values: what was removed is the original's.
        [Fact]
        public void Sides_KeepTheirOwnBasesLinesAndValues()
        {
            VerifyDiff diff = Diff(
                Original(JsonType + "Content-Encoding: gzip\r\n", ReplayCoreTests.Gzip(Utf8("{\"a\":1}\n"))),
                Actual(Utf8("{\"a\":2}\n")));

            Assert.Equal((VerifyBodyBasis.Decoded, VerifyBodyBasis.Plain), (diff.Original.Basis, diff.Actual.Basis));
            Assert.Equal((VerifyDecodeEnd.Complete, VerifyDecodeEnd.NotApplicable), (diff.Original.End, diff.Actual.End));
            Assert.Equal(
                new[] { "- {\"a\":1}", "+ {\"a\":2}" }, Assert.Single(diff.Body.Text.Hunks).Lines.Select(Render));
            VerifyJsonChange change = Assert.Single(diff.Body.Json.Changes);
            Assert.Equal(("$.a", "1", "2"), (change.Path, change.Original, change.Actual));
        }

        [Fact]
        public void Json_ShowsChangedAddedAndRemovedValuesWithTheirPaths()
        {
            VerifyDiff diff = Diff(
                Original(JsonType, Utf8("{\"id\":1,\"name\":\"a\",\"tags\":[\"x\",\"y\",\"z\"],\"b c\":true}")),
                Actual(Utf8("{\"id\":2,\"name\":\"a\",\"tags\":[\"x\",\"q\"],\"extra\":null}")));

            Assert.Equal(VerifyEquivalence.Different, diff.Body.Json.Values);
            Assert.Empty(diff.Body.Json.Explanations);
            Assert.Equal(
                new[]
                {
                    new VerifyJsonChange("$.id", false, VerifyJsonChangeKind.Changed, "1", false, "2", false),
                    new VerifyJsonChange("$.tags[1]", false, VerifyJsonChangeKind.Changed, "\"y\"", false, "\"q\"", false),
                    new VerifyJsonChange("$.tags[2]", false, VerifyJsonChangeKind.Removed, "\"z\"", false, null, false),
                    new VerifyJsonChange("$[\"b c\"]", false, VerifyJsonChangeKind.Removed, "true", false, null, false),
                    new VerifyJsonChange("$.extra", false, VerifyJsonChangeKind.Added, null, false, "null", false),
                },
                diff.Body.Json.Changes);
            Assert.Null(diff.Body.JsonUnavailable);
        }

        // Documents with equal values say how their bytes differ.
        [Theory]
        [InlineData("{\"a\":1,\"b\":[1,2]}", "{ \"a\" : 1, \"b\" : [ 1, 2 ] }", "formatting")]
        [InlineData("{\"a\":1,\"b\":2}", "{\"b\":2,\"a\":1}", "member-order")]
        [InlineData("{\"a\":\"\\u0041\"}", "{\"a\":\"A\"}", "escapes")]
        [InlineData("{\"\\u0061\":1}", "{\"a\":1}", "escapes")]
        [InlineData("{\"a\":2,\"b\":1}", "{ \"b\":1, \"a\":2 }", "formatting,member-order")]
        public void Json_EqualValuesSayHowTheirBytesDiffer(string original, string actual, string explanations)
        {
            VerifyDiff diff = Diff(Original(JsonType, Utf8(original)), Actual(Utf8(actual)));

            Assert.Equal(VerifyEquivalence.Same, diff.Body.Json.Values);
            Assert.Equal(explanations, string.Join(",", diff.Body.Json.Explanations));
            Assert.Empty(diff.Body.Json.Changes);
        }

        [Fact]
        public void Json_AByteOrderMarkIsFormatting()
        {
            VerifyDiff diff = Diff(
                Original(JsonType, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Utf8("{\"a\":1}")).ToArray()),
                Actual(Utf8("{\"a\":1}")));

            Assert.Equal(VerifyEquivalence.Same, diff.Body.Text.Equivalence);
            Assert.Equal((VerifyEquivalence.Same, VerifyJsonExplanation.Formatting), (diff.Body.Json.Values, Assert.Single(diff.Body.Json.Explanations)));
        }

        // Numbers compare by their exact text, so a representation that a parser would round is never called equal.
        [Theory]
        [InlineData("1.0", "1")]
        [InlineData("1e2", "100")]
        [InlineData("12345678901234567890", "12345678901234567891")]
        public void Json_NumbersCompareByTheirExactText(string original, string actual)
        {
            VerifyDiff diff = Diff(Original(JsonType, Utf8($"[{original}]")), Actual(Utf8($"[{actual}]")));

            VerifyJsonChange change = Assert.Single(diff.Body.Json.Changes);
            Assert.Equal(("$[0]", VerifyJsonChangeKind.Changed, original, actual), (change.Path, change.Kind, change.Original, change.Actual));
        }

        [Fact]
        public void Json_DuplicateKeysHaveNoJsonView()
        {
            VerifyDiff diff = Diff(Original(JsonType, Utf8("{\"a\":1,\"a\":2}")), Actual(Utf8("{\"a\":2}")));

            Assert.Null(diff.Body.Json);
            Assert.Equal(VerifyJsonUnavailable.DuplicateKeys, diff.Body.JsonUnavailable);
            Assert.Equal(VerifyEquivalence.Different, diff.Body.Text.Equivalence);
        }

        [Theory]
        [InlineData("{\"a\":1", "{\"a\":2}", "invalid")]
        [InlineData("{\"a\":1} {}", "{\"a\":2}", "multiple-documents")]
        [InlineData("[1] [2]", "[1]", "multiple-documents")]
        [InlineData("{\"a\":1}", "{\"a\":2,}", "invalid")]
        [InlineData("{\"a\":1}", "{\"a\":2} // note", "invalid")]
        public void Json_ADocumentThatIsNotOneValidDocumentHasNoJsonView(string original, string actual, string reason)
        {
            VerifyDiff diff = Diff(Original(JsonType, Utf8(original)), Actual(Utf8(actual)));

            Assert.Null(diff.Body.Json);
            Assert.Equal(reason, diff.Body.JsonUnavailable);
        }

        [Theory]
        [InlineData("depth", "[[[1]]]", "[[[2]]]", "depth-limit")]
        [InlineData("nodes", "[1,2,3,4]", "[1,2,3,5]", "node-limit")]
        public void Json_ADocumentBeyondItsLimitsHasNoJsonView(string limit, string original, string actual, string reason)
        {
            VerifyDiffLimits limits = limit == "depth" ? new VerifyDiffLimits(MaxJsonDepth: 2) : new VerifyDiffLimits(MaxJsonNodes: 3);

            VerifyDiff diff = Diff(Original(JsonType, Utf8(original)), Actual(Utf8(actual)), limits);

            Assert.Null(diff.Body.Json);
            Assert.Equal(reason, diff.Body.JsonUnavailable);
            Assert.Contains(VerifyDiffLimitation.JsonLimit, diff.Limitations);
            Assert.Equal(VerifyDiffState.Limited, diff.State);
        }

        [Fact]
        public void Json_OnlyUtf8AndWholeDocumentsHaveAJsonView()
        {
            VerifyDiff utf16 = Diff(
                Original("Content-Type: application/json; charset=utf-16le\r\n", Encoding.Unicode.GetBytes("{\"a\":1}")),
                Actual(Utf8("{\"a\":2}")));
            VerifyDiff part = Diff(
                Original(JsonType, Utf8("{\"a\":1,\"b\":\"" + new string('x', 300) + "\"}")),
                Actual(Utf8("{\"a\":2,\"b\":\"" + new string('x', 300) + "\"}")),
                new VerifyDiffLimits(MaxExaminedBytes: 100));
            VerifyDiff cut = Diff(
                Original(JsonType + "Content-Encoding: gzip\r\n", ReplayCoreTests.Gzip(Utf8("{\"a\":1}"))[..^4]),
                Actual(Utf8("{\"a\":2}")));

            Assert.Equal((null, VerifyJsonUnavailable.Charset), (utf16.Body.Json, utf16.Body.JsonUnavailable));
            Assert.Equal((null, VerifyJsonUnavailable.NotWhole), (part.Body.Json, part.Body.JsonUnavailable));
            Assert.Equal((null, VerifyJsonUnavailable.NotWhole), (cut.Body.Json, cut.Body.JsonUnavailable));
        }

        // Verify's decoders end quietly on truncated input, so where decoded content ends is checked for matches too.
        [Theory]
        [InlineData("complete", "Complete", null)]
        [InlineData("cut", "Incomplete", VerifyDiffLimitation.OriginalIncomplete)]
        [InlineData("members", "Unproven", VerifyDiffLimitation.OriginalEndUnproven)]
        public void DecodedEnds_AreCheckedForMatchesToo(string shape, string end, string limitation)
        {
            byte[] stored = shape switch
            {
                "complete" => ReplayCoreTests.Gzip(Utf8("hello")),
                "cut" => ReplayCoreTests.Gzip(Utf8("hello"))[..^4],
                _ => ReplayCoreTests.Gzip(Utf8("he")).Concat(ReplayCoreTests.Gzip(Utf8("llo"))).ToArray(),
            };

            VerifyDiff diff = Diff(Original(TextType + "Content-Encoding: gzip\r\n", stored), Actual(Utf8("hello")));

            Assert.True(diff.Verification.Matches);
            Assert.True(diff.Body.Equal);
            Assert.Equal((VerifyBodyBasis.Decoded, Enum.Parse<VerifyDecodeEnd>(end)), (diff.Original.Basis, diff.Original.End));
            Assert.Equal(limitation == null ? Array.Empty<string>() : new[] { limitation }, diff.Limitations);
            Assert.Equal(limitation == null ? VerifyDiffState.Complete : VerifyDiffState.Limited, diff.State);
        }

        // An end is proven only where the preview's decoder decodes as many bytes as verify compared.
        [Fact]
        public void DecodedEnds_AreUnprovenWhenThePreviewDecodesOtherwise()
        {
            var original = new VerifyBody(
                Utf8("hel"), VerifyBodyBasis.Decoded, VerifyFraming.None, ReplayCoreTests.Gzip(Utf8("hello")), "text/plain", "gzip", null);
            var actual = new VerifyBody(Utf8("hel"), VerifyBodyBasis.Plain, VerifyFraming.None, Utf8("hel"), "text/plain", null, null);

            VerifyDiff diff = VerifyDiffOperation.Explain(
                new ReplayComparison(new ReplayVerification(Array.Empty<string>()), 200, 200, original, actual),
                VerifyDiffLimits.Default,
                CancellationToken.None);

            Assert.Equal(VerifyDecodeEnd.Unproven, diff.Original.End);
            Assert.Equal(new[] { VerifyDiffLimitation.OriginalEndUnproven }, diff.Limitations);
        }

        // The JSON comparison found a change that the characters left cannot show: the values still differ.
        [Fact]
        public void Json_AChangeTheCharactersCannotShowStillDiffers()
        {
            VerifyDiff diff = Diff(
                Original(JsonType, Utf8("{\"a\":1}")), Actual(Utf8("{\"a\":2}")), new VerifyDiffLimits(MaxChars: 7));

            Assert.Equal(VerifyEquivalence.Different, diff.Body.Json.Values);
            Assert.Empty(diff.Body.Json.Changes);
            Assert.Contains(VerifyDiffLimitation.CharLimit, diff.Limitations);
        }

        [Fact]
        public void Hunks_StopAtMaxHunks()
        {
            string original = string.Concat(Enumerable.Range(1, 40).Select(line => $"l{line}\n"));
            string actual = original;
            foreach (int line in new[] { 2, 12, 22, 32 })
            {
                actual = actual.Replace($"l{line}\n", $"changed {line}\n");
            }

            VerifyDiff all = Diff(Original(TextType, Utf8(original)), Actual(Utf8(actual)));
            VerifyDiff limited = Diff(Original(TextType, Utf8(original)), Actual(Utf8(actual)), new VerifyDiffLimits(MaxHunks: 3));

            Assert.Equal(4, all.Body.Text.Hunks.Count);
            Assert.Equal(3, limited.Body.Text.Hunks.Count);
            Assert.Equal(new[] { VerifyDiffLimitation.HunkLimit }, limited.Limitations);
        }

        [Fact]
        public void Lines_AreCutAtMaxValueCharsAndMarked()
        {
            VerifyDiff diff = Diff(
                Original(TextType, Utf8(new string('x', 2000) + "\n")), Actual(Utf8(new string('y', 2000) + "\n")));

            VerifyTextLine[] lines = Assert.Single(diff.Body.Text.Hunks).Lines.ToArray();
            Assert.All(lines, line => Assert.Equal((1024, true), (line.Text.Length, line.Cut)));
            Assert.Equal(new[] { VerifyDiffLimitation.ValueCut }, diff.Limitations);
        }

        [Fact]
        public void Characters_StopAtMaxChars()
        {
            VerifyDiff diff = Diff(
                Original(TextType, Utf8(new string('x', 50) + "\n")),
                Actual(Utf8(new string('y', 50) + "\n")),
                new VerifyDiffLimits(MaxChars: 10));

            VerifyTextLine line = Assert.Single(Assert.Single(diff.Body.Text.Hunks).Lines);
            Assert.Equal((VerifyLineKind.Removed, "xxxxxxxxxx", true), (line.Kind, line.Text, line.Cut));
            Assert.Contains(VerifyDiffLimitation.CharLimit, diff.Limitations);
            Assert.Equal(VerifyEquivalence.Different, diff.Body.Text.Equivalence);
        }

        // Steps or scratch that run out stop the comparison of the lines between the common ones, which then show as one
        // coarse hunk: still a correct difference.
        [Theory]
        [InlineData("steps", VerifyDiffLimitation.StepLimit)]
        [InlineData("scratch", VerifyDiffLimitation.ScratchLimit)]
        public void Limits_LeaveACoarseHunk(string limit, string limitation)
        {
            string original = string.Concat(Enumerable.Range(1, 6).Select(line => $"a{line}\n"));
            string actual = string.Concat(Enumerable.Range(1, 6).Select(line => $"b{line}\n"));
            VerifyDiffLimits limits = limit == "steps" ? new VerifyDiffLimits(MaxSteps: 20) : new VerifyDiffLimits(MaxScratchBytes: 270);

            VerifyDiff diff = Diff(Original(TextType, Utf8(original)), Actual(Utf8(actual)), limits);

            Assert.Equal(new[] { limitation }, diff.Limitations);
            Assert.Equal(VerifyEquivalence.Different, diff.Body.Text.Equivalence);
            Assert.Equal(
                Enumerable.Range(1, 6).Select(line => $"- a{line}").Concat(Enumerable.Range(1, 6).Select(line => $"+ b{line}")),
                Assert.Single(diff.Body.Text.Hunks).Lines.Select(Render));
        }

        // Only MaxLines lines of each side are compared: equal ones then prove nothing.
        [Fact]
        public void Lines_AreBoundedPerSide()
        {
            string text = string.Concat(Enumerable.Range(1, 10).Select(line => $"l{line}\n"));
            var limits = new VerifyDiffLimits(MaxLines: 5);

            VerifyDiff same = Diff(
                Original(TextType, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Utf8(text)).ToArray()), Actual(Utf8(text)), limits);
            VerifyDiff different = Diff(Original(TextType, Utf8(text)), Actual(Utf8(text.Replace("l2\n", "two\n"))), limits);

            Assert.Equal(VerifyEquivalence.Unknown, same.Body.Text.Equivalence);
            Assert.Equal(new[] { VerifyDiffLimitation.LineLimit }, same.Limitations);
            Assert.Equal(VerifyEquivalence.Different, different.Body.Text.Equivalence);
        }

        [Fact]
        public void ByteRanges_StopAtMaxChanges()
        {
            byte[] original = new byte[20];
            byte[] actual = new byte[20];
            foreach (int offset in new[] { 0, 2, 4, 6, 8 })
            {
                actual[offset] = 1;
            }

            VerifyDiff diff = Diff(
                Original("Content-Type: application/octet-stream\r\n", original), Actual(actual), new VerifyDiffLimits(MaxChanges: 3));

            Assert.Equal(new long[] { 0, 2, 4 }, diff.Body.Binary.Ranges.Select(range => range.Offset));
            Assert.Equal(new[] { VerifyDiffLimitation.ChangeLimit }, diff.Limitations);
        }

        [Fact]
        public void JsonChanges_StopAtMaxChangesAndStillDiffer()
        {
            VerifyDiff diff = Diff(
                Original(JsonType, Utf8("[1,2,3,4,5]")), Actual(Utf8("[6,7,8,9,10]")), new VerifyDiffLimits(MaxChanges: 2));

            Assert.Equal(VerifyEquivalence.Different, diff.Body.Json.Values);
            Assert.Equal(new[] { "$[0]", "$[1]" }, diff.Body.Json.Changes.Select(change => change.Path));
            Assert.Contains(VerifyDiffLimitation.ChangeLimit, diff.Limitations);
        }

        [Fact]
        public void Work_IsBounded()
        {
            byte[] original = Enumerable.Repeat((byte)0, 1000).ToArray();
            byte[] actual = Enumerable.Repeat((byte)1, 1000).ToArray();

            VerifyDiff diff = Diff(
                Original("Content-Type: application/octet-stream\r\n", original), Actual(actual), new VerifyDiffLimits(MaxWork: 64));

            Assert.Contains(VerifyDiffLimitation.WorkLimit, diff.Limitations);
            VerifyByteRange range = Assert.Single(diff.Body.Binary.Ranges);
            Assert.Equal((0L, 64L), (range.Offset, range.OriginalLength));
        }

        [Fact]
        public void JsonPathsAndValues_AreCutAndMarked()
        {
            string name = new('k', 2000);
            VerifyDiff path = Diff(
                Original(JsonType, Utf8($"{{\"{name}\":1}}")), Actual(Utf8($"{{\"{name}\":2}}")));
            VerifyDiff value = Diff(
                Original(JsonType, Utf8($"{{\"a\":\"{new string('x', 3000)}\"}}")),
                Actual(Utf8($"{{\"a\":\"{new string('y', 3000)}\"}}")));

            VerifyJsonChange cutPath = Assert.Single(path.Body.Json.Changes);
            Assert.Equal((true, 1024, false, false), (cutPath.PathCut, cutPath.Path.Length, cutPath.OriginalCut, cutPath.ActualCut));
            VerifyJsonChange cutValue = Assert.Single(value.Body.Json.Changes);
            Assert.Equal((false, true, true, 1024), (cutValue.PathCut, cutValue.OriginalCut, cutValue.ActualCut, cutValue.Original.Length));
            Assert.Contains(VerifyDiffLimitation.ValueCut, value.Limitations);
        }

        [Fact]
        public void Serialized_TheDetailIsDiffJsonVersion1()
        {
            VerifyDiff diff = Diff(Original(JsonType, Utf8("{\"id\":1}\n")), Actual(Utf8("{\"id\":2}\n")));

            byte[] bytes = VerifyDiffJson.Serialize(diff);

            Assert.Equal(bytes.Length, diff.SerializedBytes);
            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement root = document.RootElement;
            Assert.Equal(
                ("xmat.verify-diff", 1, "complete", "mismatch", "body"),
                (root.GetProperty("schema").GetString(), root.GetProperty("version").GetInt32(), root.GetProperty("state").GetString(),
                    root.GetProperty("result").GetString(), root.GetProperty("differences")[0].GetString()));
            Assert.Equal((200, 200), (root.GetProperty("status").GetProperty("original").GetInt32(), root.GetProperty("status").GetProperty("actual").GetInt32()));
            JsonElement original = root.GetProperty("original");
            Assert.Equal(
                ("plain", "none", "notApplicable", 9, 9, "utf-8"),
                (original.GetProperty("basis").GetString(), original.GetProperty("framing").GetString(), original.GetProperty("end").GetString(),
                    original.GetProperty("comparedBytes").GetInt32(), original.GetProperty("examinedBytes").GetInt32(), original.GetProperty("charset").GetString()));
            JsonElement body = root.GetProperty("body");
            Assert.Equal(JsonValueKind.Null, body.GetProperty("binary").ValueKind);
            JsonElement line = body.GetProperty("text").GetProperty("hunks")[0].GetProperty("lines")[0];
            Assert.Equal(
                ("different", "removed", "{\"id\":1}", "\n", false),
                (body.GetProperty("text").GetProperty("equivalence").GetString(), line.GetProperty("kind").GetString(),
                    line.GetProperty("text").GetString(), line.GetProperty("break").GetString(), line.GetProperty("cut").GetBoolean()));
            JsonElement change = body.GetProperty("json").GetProperty("changes")[0];
            Assert.Equal(
                ("different", "$.id", "changed", "1", "2"),
                (body.GetProperty("json").GetProperty("values").GetString(), change.GetProperty("path").GetString(),
                    change.GetProperty("kind").GetString(), change.GetProperty("original").GetString(), change.GetProperty("actual").GetString()));
            Assert.Equal(JsonValueKind.Null, body.GetProperty("jsonUnavailable").ValueKind);
        }

        // A detail too large for its limit keeps everything but its body, measured once, within the limit.
        [Fact]
        public void Serialized_ADetailTooLargeKeepsOnlyItsMetadata()
        {
            string original = string.Concat(Enumerable.Range(1, 300).Select(line => $"original line {line}\n"));
            string actual = string.Concat(Enumerable.Range(1, 300).Select(line => $"replayed line {line}\n"));

            VerifyDiff diff = Diff(
                Original(TextType, Utf8(original)),
                Actual(Utf8(actual), 500),
                new VerifyDiffLimits(MaxSerializedBytes: VerifyDiffLimits.MinSerializedBytes));

            Assert.Equal(VerifyDiffState.Unavailable, diff.State);
            Assert.Null(diff.Body);
            Assert.Contains(VerifyDiffLimitation.SerializedLimit, diff.Limitations);
            Assert.Equal(new[] { ReplayVerifier.StatusDifference, ReplayVerifier.BodyDifference }, diff.Verification.Differences);
            Assert.InRange(diff.SerializedBytes, 1, VerifyDiffLimits.MinSerializedBytes);
            Assert.Equal(VerifyDiffJson.Serialize(diff).Length, diff.SerializedBytes);
        }

        [Fact]
        public void Serialized_TheLargestMetadataFitsTheLeastLimit()
        {
            string[] every = typeof(VerifyDiffLimitation)
                .GetFields(BindingFlags.NonPublic | BindingFlags.Static)
                .Where(field => field.IsLiteral)
                .Select(field => (string)field.GetValue(null))
                .ToArray();
            var side = new VerifyDiffSide(
                VerifyBodyBasis.UnsupportedCoding, VerifyFraming.NotRemoved, VerifyDecodeEnd.NotApplicable, long.MaxValue, long.MaxValue, "iso-8859-1");
            var diff = new VerifyDiff(
                VerifyDiffState.Unavailable,
                every,
                new ReplayVerification(new[] { ReplayVerifier.StatusDifference, ReplayVerifier.BodyDifference }),
                int.MinValue,
                int.MinValue,
                side,
                side,
                null,
                0);

            Assert.True(every.Length >= 16);
            Assert.InRange(VerifyDiffJson.Serialize(diff).Length, 1, VerifyDiffLimits.MinSerializedBytes);
            Assert.Equal(VerifyDiffLimits.MinSerializedBytes, new VerifyDiffLimits(MaxSerializedBytes: 1).Clamped().MaxSerializedBytes);
        }

        // A failure while the detail is built leaves it Unavailable, with the verdict unchanged.
        [Fact]
        public void Explain_AFailureLeavesTheDetailUnavailableWithItsVerdict()
        {
            var verification = new ReplayVerification(new[] { ReplayVerifier.BodyDifference });
            var actual = new VerifyBody(Utf8("x"), VerifyBodyBasis.Plain, VerifyFraming.None, Utf8("x"), null, null, null);

            VerifyDiff diff = VerifyDiffOperation.Explain(
                new ReplayComparison(verification, 200, 200, null, actual), VerifyDiffLimits.Default, CancellationToken.None);

            Assert.Equal((VerifyDiffState.Unavailable, null), (diff.State, diff.Body));
            Assert.Equal(new[] { VerifyDiffLimitation.Internal }, diff.Limitations);
            Assert.Same(verification, diff.Verification);
            Assert.Equal(VerifyDiffJson.Serialize(diff).Length, diff.SerializedBytes);
        }

        [Fact]
        public async Task Cancellation_Propagates()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var request = new VerifyDiffRequest(Original(TextType, Utf8("a\n")), Actual(Utf8("b\n")), DecodeLimit);

            Assert.ThrowsAny<OperationCanceledException>(() => VerifyDiffOperation.Compare(request, cancellation.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => VerifyDiffOperation.CompareAsync(request, cancellation.Token));
            Assert.Equal(
                OperationOutcome.Cancelled,
                (await new VerifyDiffOperation().ExecuteAsync(request, OperationContext.None, cancellation.Token)).Outcome);
        }

        // A detail can be handed around and kept: none of its lists can be changed.
        [Fact]
        public void Details_CannotBeChanged()
        {
            VerifyDiff text = Diff(Original(JsonType, Utf8("{\"a\":1}\n")), Actual(Utf8("{\"a\":2}\n"), 500));
            VerifyDiff bytes = Diff(Original("Content-Type: application/octet-stream\r\n", new byte[4]), Actual(new byte[] { 0, 1, 0, 0 }));

            Assert.Throws<NotSupportedException>(() => ((IList<string>)text.Verification.Differences).Add("x"));
            Assert.Throws<NotSupportedException>(() => ((IList<string>)text.Limitations).Add("x"));
            Assert.Throws<NotSupportedException>(() => ((IList<VerifyTextHunk>)text.Body.Text.Hunks)[0] = null);
            Assert.Throws<NotSupportedException>(() => ((IList<VerifyTextLine>)text.Body.Text.Hunks[0].Lines)[0] = null);
            Assert.Throws<NotSupportedException>(() => ((IList<VerifyJsonChange>)text.Body.Json.Changes)[0] = null);
            Assert.Throws<NotSupportedException>(() => ((IList<VerifyByteRange>)bytes.Body.Binary.Ranges)[0] = null);
        }

        private static VerifyDiff Diff(ProxyConnectionModel original, ReplayExchange actual, VerifyDiffLimits limits = null) =>
            VerifyDiffOperation.Compare(new VerifyDiffRequest(original, actual, DecodeLimit, limits), CancellationToken.None);

        private static ProxyConnectionModel Original(string headers, byte[] body, string status = "200") =>
            new(1, status, DateTime.UtcNow, TimeSpan.Zero, "http", "127.0.0.1:1", "GET", "/", "GET / HTTP/1.1\r\n", [],
                $"HTTP/1.1 {status} X\r\n{headers}", body, "127.0.0.1");

        private static ReplayExchange Actual(byte[] body, int status = 200, string contentType = null, string contentEncoding = null)
        {
            var headers = new List<KeyValuePair<string, string>>();
            if (contentType != null)
            {
                headers.Add(new("Content-Type", contentType));
            }

            if (contentEncoding != null)
            {
                headers.Add(new("Content-Encoding", contentEncoding));
            }

            return new ReplayExchange
            {
                Outcome = ReplayOutcome.Completed,
                StatusCode = status,
                Response = new ReplayMessage($"HTTP/1.1 {status} X", null, headers, body),
            };
        }

        private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

        private static string Render(VerifyTextLine line) =>
            line.Kind switch
            {
                VerifyLineKind.Removed => "- ",
                VerifyLineKind.Added => "+ ",
                _ => "  ",
            } + line.Text;
    }
}
