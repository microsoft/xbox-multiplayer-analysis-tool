// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text;
using XMAT.WebServiceCapture;

namespace XMAT.Tests
{
    // RequestReplayDraft's cancellable count of its body (TryCountBody) against GetBodyLength and the exact
    // bytes, at every slice boundary; and the captured origin that a recorded send restores (CapturedOrigin,
    // WithCapturedOrigin).
    public sealed class RequestReplayDraftCountTests
    {
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        public static TheoryData<string> Texts => new()
        {
            string.Empty,
            "a",
            "hello, world",
            "caf\u00e9",
            "\u20ac10",
            "\U0001F600",
            "a\U0001F600b",
            "\U0001F600\U0001F600",
            "x\u00e9\u20ac\U0001F600y\r\n\t",
            "\uFFFF\uFFFE\u0000",
            "\uD83D",
            "a\uD83D",
            "\uD83Db",
            "\uDE00",
            "a\uDE00b",
            "\uD83D\uD83D\uDE00",
            "\uDE00\uD83D",
            "\U0001F600\uDE00",
        };

        public static TheoryData<string> Base64Texts => new()
        {
            string.Empty,
            "AA==",
            "AAA=",
            "AAAA",
            "aGVsbG8=",
            "aGVsbG8h",
            "aGVs bG8=",
            "aG\r\nVsbG8=",
            " aGVsbG8=",
            "aGVsbG8= ",
            "aGVsbG8 =",
            "aGVsbA= =",
            "aGVsbA==\t\n",
            "+/+/",
            "A",
            "AA",
            "AAA",
            "A===",
            "AB=C",
            "AB==A",
            "====",
            "AB*=",
            "AB-_",
            "\u00e9AAA",
            "AAAA=",
        };

        [Theory]
        [MemberData(nameof(Texts))]
        public void TryCountBody_AgreesWithGetBodyLength_AndTheExactBytes_AtEverySliceBoundary(string text)
        {
            var draft = new RequestReplayDraft { BodyText = text, BodyIsBase64 = false };
            long? whole = LengthOrNull(draft);
            byte[] bytes = BytesOrNull(() => StrictUtf8.GetBytes(text));
            Assert.Equal(whole == null, bytes == null);

            for (int slice = 1; slice <= text.Length + 1; slice++)
            {
                BodyCount count = draft.TryCountBody(slice, CancellationToken.None);

                Assert.Equal(whole ?? 0, count.Length);
                Assert.Equal(whole == null ? BodyProblem.NotText : BodyProblem.None, count.Problem);
                Assert.Equal(bytes?.LongLength ?? 0, count.Length);
            }

            Assert.Equal(new BodyCount(whole ?? 0, whole == null ? BodyProblem.NotText : BodyProblem.None),
                draft.TryCountBody(CancellationToken.None));
        }

        [Theory]
        [MemberData(nameof(Base64Texts))]
        public void TryCountBody_AgreesWithGetBodyLength_AndTheDecodedBytes_ForBase64_AtEverySliceBoundary(string text)
        {
            var draft = new RequestReplayDraft { BodyText = text, BodyIsBase64 = true };
            long? whole = LengthOrNull(draft);
            byte[] bytes = BytesOrNull(() => Convert.FromBase64String(text));
            Assert.Equal(whole == null, bytes == null);

            for (int slice = 1; slice <= text.Length + 1; slice++)
            {
                BodyCount count = draft.TryCountBody(slice, CancellationToken.None);

                Assert.Equal(whole ?? 0, count.Length);
                Assert.Equal(whole == null ? BodyProblem.NotBase64 : BodyProblem.None, count.Problem);
                Assert.Equal(bytes?.LongLength ?? 0, count.Length);
            }
        }

        [Fact]
        public void TryCountBody_ReadsASurrogatePairOrPaddingThatASliceBoundarySplits_AsOne()
        {
            // "a" then the pair: a slice of 2 ends between its halves.
            var pair = new RequestReplayDraft { BodyText = "a\U0001F600b", BodyIsBase64 = false };
            Assert.Equal(new BodyCount(6, BodyProblem.None), pair.TryCountBody(2, CancellationToken.None));

            // A high half at the end of one slice, then something other than a low half.
            var lone = new RequestReplayDraft { BodyText = "a\uD83Db", BodyIsBase64 = false };
            Assert.Equal(new BodyCount(0, BodyProblem.NotText), lone.TryCountBody(2, CancellationToken.None));

            // Padding that a boundary splits counts once, and nothing may follow it in the next slice.
            var padded = new RequestReplayDraft { BodyText = "AA==", BodyIsBase64 = true };
            Assert.Equal(new BodyCount(1, BodyProblem.None), padded.TryCountBody(3, CancellationToken.None));
            var afterPadding = new RequestReplayDraft { BodyText = "AB=C", BodyIsBase64 = true };
            Assert.Equal(new BodyCount(0, BodyProblem.NotBase64), afterPadding.TryCountBody(3, CancellationToken.None));
        }

        [Fact]
        public void TryCountBody_CountsSharedBytesAsTheyAre()
        {
            var draft = new RequestReplayDraft();
            draft.SetBodyBytes(new byte[] { 0xFF, 0xFE, 0x00, 0x22, 0x22 });

            Assert.Equal(new BodyCount(5, BodyProblem.None), draft.TryCountBody(1, CancellationToken.None));
            Assert.Equal(draft.GetBodyLength(), draft.TryCountBody(CancellationToken.None).Length);
        }

        [Theory]
        [InlineData(false, "caf\u00e9 \U0001F600 text!", 4)]
        [InlineData(true, "aGVs bG8g d29y bGQh", 4)]
        public void TryCountBody_StopsWhenCancelled_InAnySlice(bool base64, string text, int sliceChars)
        {
            int slices = (text.Length + sliceChars - 1) / sliceChars;
            using (var before = new CancellationTokenSource())
            {
                before.Cancel();
                var draft = new RequestReplayDraft { BodyText = text, BodyIsBase64 = base64 };
                Assert.Throws<OperationCanceledException>(() => draft.TryCountBody(sliceChars, before.Token));
            }

            for (int cancelIn = 0; cancelIn < slices; cancelIn++)
            {
                using var source = new CancellationTokenSource();
                var draft = new RequestReplayDraft { BodyText = text, BodyIsBase64 = base64 };
                var read = new List<int>();

                Assert.Throws<OperationCanceledException>(() => draft.TryCountBody(sliceChars, source.Token, slice =>
                {
                    read.Add(slice);
                    if (slice == cancelIn)
                        source.Cancel();
                }));

                Assert.Equal(Enumerable.Range(0, cancelIn + 1), read);
            }
        }

        [Fact]
        public void TryCountBody_RefusesASliceSizeBelowOne()
        {
            var draft = new RequestReplayDraft { BodyText = "a" };

            Assert.Throws<ArgumentOutOfRangeException>(() => draft.TryCountBody(0, CancellationToken.None));
        }

        [Fact]
        public void WithCapturedOrigin_GivesACopyWithThatOrigin_AndChangesNothingElse()
        {
            var draft = new RequestReplayDraft
            {
                Method = "POST",
                Url = "https://a.example/x?y",
                HeadersText = "X-A: 1",
                BodyText = "body",
            };
            Assert.Null(draft.CapturedOrigin);
            Assert.False(draft.ChangesOrigin);
            var origin = new Uri("https://b.example:8443/");

            RequestReplayDraft copy = draft.WithCapturedOrigin(origin);

            Assert.NotSame(draft, copy);
            Assert.Same(origin, copy.CapturedOrigin);
            Assert.True(copy.ChangesOrigin);
            Assert.Equal(
                (draft.Method, draft.Url, draft.HeadersText, draft.BodyText, draft.BodyIsBase64),
                (copy.Method, copy.Url, copy.HeadersText, copy.BodyText, copy.BodyIsBase64));
            Assert.Null(draft.CapturedOrigin);
            Assert.False(copy.WithUrl("https://b.example:8443/z").ChangesOrigin);
        }

        [Fact]
        public void WithCapturedOrigin_RefusesANullOrRelativeOrigin_SoNoOriginIsErased()
        {
            var draft = new RequestReplayDraft { Url = "https://a.example/" };

            Assert.Throws<ArgumentNullException>(() => draft.WithCapturedOrigin(null));
            Assert.Throws<ArgumentException>(() => draft.WithCapturedOrigin(new Uri("/x", UriKind.Relative)));
        }

        [Fact]
        public void CapturedOrigin_IsTheOriginThatAUrlSuppliedFirstGives()
        {
            var draft = new RequestReplayDraft();
            draft.CompleteUrl("https://first.example/a");
            draft.CompleteUrl("https://second.example/b");

            Assert.Equal(new Uri("https://first.example/a"), draft.CapturedOrigin);
            Assert.True(draft.ChangesOrigin);
        }

        private static long? LengthOrNull(RequestReplayDraft draft)
        {
            try
            {
                return draft.GetBodyLength();
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private static byte[] BytesOrNull(Func<byte[]> decode)
        {
            try
            {
                return decode();
            }
            catch (Exception exception) when (exception is FormatException or EncoderFallbackException)
            {
                return null;
            }
        }
    }
}
