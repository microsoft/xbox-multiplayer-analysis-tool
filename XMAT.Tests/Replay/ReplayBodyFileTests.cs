// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.Operations.Replay;

namespace XMAT.Tests.Replay
{
    // The body file that xmat's --body-file and the composer's Upload File read: bounded while it is read, not only by the
    // length it had at first. XmatReplayTests covers the codes of files that cannot be read.
    public sealed class ReplayBodyFileTests
    {
        // At the limit is accepted and one byte more is refused, also from a stream whose length said less at first, as a
        // file that grows while it is read; what was read is returned exactly.
        [Theory]
        [InlineData(64, 64, true)]
        [InlineData(65, 65, false)]
        [InlineData(65, 1, false)]
        [InlineData(40, 1, true)]
        public async Task Read_RefusesMoreThanTheLimit_WhateverTheLengthSaidAtFirst(int length, int reported, bool accepted)
        {
            byte[] bytes = Enumerable.Range(0, length).Select(value => (byte)(255 - value)).ToArray();
            using var stream = new ReportedLengthStream(bytes, reported);

            ReplayBodyRead read = await ReplayBodyFile.ReadAsync(stream, 64, CancellationToken.None);

            Assert.Equal(accepted ? null : ReplayBodyFile.TooLargeCode, read.Code);
            Assert.Equal(accepted ? bytes : null, read.Body);
        }

        // A file read with a budget, as a multipart form's files are (CliRequestBody): with no budget left, only an empty file
        // is read, and a longer one, or a stream that grows past the budget, is refused; a negative budget is a mistake.
        [Fact]
        public async Task Read_WithNoBudgetLeft_ReadsOnlyAnEmptyFile()
        {
            string path = Path.Join(AppContext.BaseDirectory, "HeadlessTestArtifacts", "Replay", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            try
            {
                string empty = Path.Join(path, "empty.bin");
                string one = Path.Join(path, "one.bin");
                File.WriteAllBytes(empty, Array.Empty<byte>());
                File.WriteAllBytes(one, new byte[] { 1 });
                using var growing = new ReportedLengthStream(new byte[] { 1 }, 0);

                ReplayBodyRead emptyRead = await ReplayBodyFile.ReadAsync(empty, 0, CancellationToken.None);
                ReplayBodyRead oneRead = await ReplayBodyFile.ReadAsync(one, 0, CancellationToken.None);
                ReplayBodyRead grown = await ReplayBodyFile.ReadAsync(growing, 0, CancellationToken.None);

                Assert.Equal(Array.Empty<byte>(), emptyRead.Body);
                Assert.Equal((null, ReplayBodyFile.TooLargeCode), (oneRead.Body, oneRead.Code));
                Assert.Equal((null, ReplayBodyFile.TooLargeCode), (grown.Body, grown.Code));
                Assert.Equal(new byte[] { 1 }, (await ReplayBodyFile.ReadAsync(one, 1, CancellationToken.None)).Body);
                await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ReplayBodyFile.ReadAsync(one, -1, CancellationToken.None));
            }
            finally
            {
                Directory.Delete(path, recursive: true);
            }
        }

        [Fact]
        public async Task Read_StopsWhenCancelled()
        {
            string path = Path.Join(AppContext.BaseDirectory, "HeadlessTestArtifacts", "Replay", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            try
            {
                string file = Path.Join(path, "body.bin");
                File.WriteAllBytes(file, new byte[16]);
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReplayBodyFile.ReadAsync(file, cancellation.Token));
            }
            finally
            {
                Directory.Delete(path, recursive: true);
            }
        }

        // A stream whose length may say less than it holds.
        private sealed class ReportedLengthStream : MemoryStream
        {
            private readonly long _reported;

            internal ReportedLengthStream(byte[] bytes, long reported)
                : base(bytes, writable: false) => _reported = reported;

            public override long Length => _reported;
        }
    }
}
