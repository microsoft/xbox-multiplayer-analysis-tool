// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.DataAbstractionLayer;
using XMAT.Operations.Export;
using static XMAT.Tests.Operations.Export.ExportTestData;

namespace XMAT.Tests.Operations.Export
{
    public sealed class CaptureExportPublisherTests
    {
        [Fact]
        public async Task Publish_ValidatesSourceAndOpenCaptureConflictsBeforeStaging()
        {
            using var directory = new ExportTestDirectory();
            string destination = directory.PathOf("source.xmatcap");
            File.WriteAllText(destination, "source");
            string[] before = Directory.GetFileSystemEntries(directory.Root);
            var writes = 0;
            var publisher = new CaptureExportPublisher();

            await Assert.ThrowsAsync<CaptureSaveException>(
                () => publisher.PublishAsync(
                    destination,
                    replaceExisting: true,
                    path =>
                    {
                        Assert.Equal(destination, path);
                        throw new CaptureSaveException(CaptureSaveFailure.Refused);
                    },
                    (path, cancellationToken) =>
                    {
                        writes++;
                        return File.WriteAllTextAsync(path, "unexpected", cancellationToken);
                    },
                    CancellationToken.None));

            Assert.Equal(0, writes);
            Assert.Equal("source", File.ReadAllText(destination));
            Assert.Equal(before, Directory.GetFileSystemEntries(directory.Root));
        }

        [Fact]
        public async Task Publish_CommitsACompleteFileAtomicallyAndReportsItsIdentity()
        {
            using var directory = new ExportTestDirectory();
            string destination = directory.PathOf("result.xmatcap");
            File.WriteAllText(destination, "old");
            var publisher = new CaptureExportPublisher();

            PublishedCapture result = await publisher.PublishAsync(
                destination,
                replaceExisting: true,
                _ => { },
                (path, cancellationToken) => File.WriteAllTextAsync(path, "complete-new-output", cancellationToken),
                CancellationToken.None);

            Assert.Equal("complete-new-output", File.ReadAllText(destination));
            Assert.Equal(new FileInfo(destination).Length, result.Bytes);
            Assert.Equal(Sha256(destination), result.Sha256);
            Assert.True(result.ReplacedExisting);
            Assert.Equal(new[] { destination }, Directory.GetFileSystemEntries(directory.Root));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Publish_CancellationOrFailureLeavesNoPartialOutputOrStaging(bool cancel)
        {
            using var directory = new ExportTestDirectory();
            string destination = directory.PathOf("result.saz");
            File.WriteAllText(destination, "existing");
            string[] before = Directory.GetFileSystemEntries(directory.Root);
            using var cancellation = new CancellationTokenSource();
            var publisher = new CaptureExportPublisher();

            Task Publish() => publisher.PublishAsync(
                destination,
                replaceExisting: true,
                _ => { },
                async (path, cancellationToken) =>
                {
                    await File.WriteAllTextAsync(path, "partial", cancellationToken);
                    if (cancel)
                    {
                        cancellation.Cancel();
                        cancellation.Token.ThrowIfCancellationRequested();
                    }
                    throw new IOException("injected write failure");
                },
                cancellation.Token);

            if (cancel)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(Publish);
            }
            else
            {
                await Assert.ThrowsAsync<IOException>(Publish);
            }

            Assert.Equal("existing", File.ReadAllText(destination));
            Assert.Equal(before, Directory.GetFileSystemEntries(directory.Root));
        }

        [Fact]
        public async Task Publish_WithoutReplacementRefusesAnExistingDestination()
        {
            using var directory = new ExportTestDirectory();
            string destination = directory.PathOf("result.saz");
            File.WriteAllText(destination, "existing");
            var publisher = new CaptureExportPublisher();

            await Assert.ThrowsAsync<IOException>(
                () => publisher.PublishAsync(
                    destination,
                    replaceExisting: false,
                    _ => { },
                    (path, cancellationToken) => File.WriteAllTextAsync(path, "new", cancellationToken),
                    CancellationToken.None));

            Assert.Equal("existing", File.ReadAllText(destination));
            Assert.Equal(new[] { destination }, Directory.GetFileSystemEntries(directory.Root));
        }
    }
}
