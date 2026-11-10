// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.Operations.Replay;
using XMAT.Tests.Proxy;
using XMAT.WebServiceCapture;

namespace XMAT.Tests.Replay
{
    // Request file tests that run alone: one measures allocations across the process, where the reading runs on a thread
    // pool thread, so no other test's allocations count; two hold bodies of the 64 MiB limit, whose size other tests should
    // not run beside.
    [Collection(IsolatedProxyTests.Name)]
    public sealed class RequestFileIsolatedTests
    {
        // A file larger than the limit is refused from its length, before anything of its size is read or allocated: a
        // reader that read it first would allocate at least 96 MiB.
        [Fact]
        public async Task AFileBeyondTheLimit_IsRefusedBeforeItIsRead()
        {
            string path = Path.Join(Path.GetTempPath(), "xmat-request-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                using (var stream = new FileStream(path, FileMode.CreateNew))
                {
                    stream.SetLength(RequestFileLimits.MaxFileBytes + 1);
                }

                long before = GC.GetTotalAllocatedBytes(precise: true);
                RequestFileReadResult result = await RequestFileReader.ReadFileAsync(path, CancellationToken.None);
                long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

                Assert.Equal(RequestFileCodes.TooLarge, result.Code);
                Assert.InRange(allocated, 0, 32L * 1024 * 1024);
            }
            finally
            {
                File.Delete(path);
            }
        }

        // A body of exactly 64 MiB, the limit, round-trips: the writer and the reader both take it, and its Base64 is padded
        // at its end only (64 MiB is one byte over a whole number of 3-byte groups, so "=="). It runs alone for its size, and
        // compares bytes as spans, without a string of the file's size.
        [Fact]
        public void ABodyOfExactlyTheLimit_RoundTrips()
        {
            var body = new byte[RequestFileLimits.MaxBodyBytes];
            for (int index = 0; index < body.Length; index++)
            {
                body[index] = (byte)(index * 7);
            }

            var draft = new RequestReplayDraft { Method = "PUT", Url = "https://example.test/" };
            draft.SetBodyBytes(body);
            int base64Length = 4 * ((RequestFileLimits.MaxBodyBytes + 2) / 3);
            using var file = new MemoryStream(base64Length + 4096);

            RequestFileWriter.Write(draft, ReplayHttpVersion.Http11, RequestFileProfile.Content, file, CancellationToken.None);
            ReadOnlySpan<byte> written = file.GetBuffer().AsSpan(0, (int)file.Length);
            int start = written.IndexOf("\"body\": \""u8) + 9;
            ReadOnlySpan<byte> base64 = written[start..(start + written[start..].IndexOf((byte)'"'))];
            RequestFileReadResult result = RequestFileReader.Read(written);

            Assert.Equal(base64Length, base64.Length);
            Assert.Equal(base64Length - 2, base64.IndexOf((byte)'='));
            Assert.Null(result.Code);
            Assert.True(body.AsSpan().SequenceEqual(result.Content.Body));
        }

        // Saving refuses a body one byte over the limit before it writes anything, as reading refuses one
        // (RequestFileTests.ABodyBeyondTheLimit_IsRefusedBeforeItIsDecoded).
        [Fact]
        public void SavingABodyBeyondTheLimit_WritesNothing()
        {
            var draft = new RequestReplayDraft { Method = "PUT", Url = "https://example.test/" };
            draft.SetBodyBytes(new byte[RequestFileLimits.MaxBodyBytes + 1]);
            using var file = new MemoryStream();

            RequestFileException refused = Assert.Throws<RequestFileException>(() => RequestFileWriter.Write(
                draft, ReplayHttpVersion.Http11, RequestFileProfile.Content, file, CancellationToken.None));

            Assert.Equal(RequestFileCodes.RequestInvalid, refused.Code);
            Assert.Equal(0, file.Length);
        }
    }
}
