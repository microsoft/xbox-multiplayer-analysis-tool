// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.WebServiceCapture;

namespace XMAT.Tests.Replay
{
    // A Raw mode body's bytes, built in slices off the UI thread (RawBodyBytes, AA-5): exactly the bytes that the whole input
    // makes (RequestReplayDraft.GetBodyBytes), wherever a slice ends, of the length that TryCountBody counts; a held body is
    // its own array; and the token is checked before each slice.
    public class RawBodyBytesTests
    {
        public static TheoryData<int> SliceSizes => new() { 1, 2, 3, 4, 5, 6, 7, 8, 9, RawBodyBytes.DefaultSliceChars };

        // Text: surrogate pairs, two- and three-byte characters and ASCII, with a pair placed across every boundary
        // position that a slice of up to 9 characters can have.
        [Theory]
        [MemberData(nameof(SliceSizes))]
        public void Text_IsItsWholeUtf8_WhereverASliceEnds(int sliceChars)
        {
            for (int offset = 0; offset < 10; offset++)
            {
                string text = new string('x', offset) + "\ud83d\ude00\u00e9\u20aca\ud83d\ude00b\u00e9\ud83d\ude00\u20ac";
                AssertBuildsTheWholeInputsBytes(text, base64: false, sliceChars);
            }

            AssertBuildsTheWholeInputsBytes(string.Empty, base64: false, sliceChars);
        }

        // Base64: groups of four split at every position, and a space, tab, CR or LF at every position, between and after
        // '=' and '==' padding included. Bits past a padded group's bytes are not read, as Convert.FromBase64String does not
        // read them.
        [Theory]
        [MemberData(nameof(SliceSizes))]
        public void Base64_IsItsWholeDecoding_WhereverASliceEndsOrWhiteSpaceIs(int sliceChars)
        {
            string[] encoded = { "", "QQ==", "QUI=", "QUJD", "AC==", "SGVsbG8sIHdvcmxkIQ==", "4pyTIMOgIGxhIG1vZGU=" };
            foreach (string text in encoded)
            {
                AssertBuildsTheWholeInputsBytes(text, base64: true, sliceChars);
                for (int position = 0; position <= text.Length; position++)
                {
                    foreach (char space in " \t\r\n")
                    {
                        AssertBuildsTheWholeInputsBytes(text.Insert(position, space.ToString()), base64: true, sliceChars);
                        AssertBuildsTheWholeInputsBytes(
                            text.Insert(position, space.ToString()) + space, base64: true, sliceChars);
                    }
                }
            }
        }

        // A held body is its own array, never copied.
        [Fact]
        public void AHeldBody_IsItsOwnArray()
        {
            byte[] held = { 1, 2, 3 };

            byte[] built = RawBodyBytes.Build(
                new RawBodySource(null, false, held), new BodyCount(3, BodyProblem.None), CancellationToken.None);

            Assert.Same(held, built);
        }

        // The token is checked before each slice and after the last: a cancel after any slice stops the construction.
        [Theory]
        [InlineData(false, 0)]
        [InlineData(false, 3)]
        [InlineData(true, 0)]
        [InlineData(true, 4)]
        public void ACancelAfterASlice_StopsTheConstruction(bool base64, int slice)
        {
            string text = base64 ? "SGVsbG8sIHdvcmxkIQ==" : "0123456789ab\u00e9";
            var draft = new RequestReplayDraft { BodyText = text, BodyIsBase64 = base64 };
            BodyCount count = draft.TryCountBody(CancellationToken.None);
            using var cancellation = new CancellationTokenSource();
            var built = new List<int>();

            Assert.Throws<OperationCanceledException>(() => RawBodyBytes.Build(
                new RawBodySource(text, base64, null),
                count,
                cancellation.Token,
                4,
                index =>
                {
                    built.Add(index);
                    if (index == slice)
                    {
                        cancellation.Cancel();
                    }
                }));
            Assert.Equal(Enumerable.Range(0, slice + 1), built);
        }

        private static void AssertBuildsTheWholeInputsBytes(string text, bool base64, int sliceChars)
        {
            var draft = new RequestReplayDraft { BodyText = text, BodyIsBase64 = base64 };
            BodyCount count = draft.TryCountBody(CancellationToken.None);
            Assert.Equal(BodyProblem.None, count.Problem);

            byte[] built = RawBodyBytes.Build(new RawBodySource(text, base64, null), count, CancellationToken.None, sliceChars);

            Assert.Equal(draft.GetBodyBytes(), built);
            Assert.Equal(count.Length, built.LongLength);
        }
    }
}
