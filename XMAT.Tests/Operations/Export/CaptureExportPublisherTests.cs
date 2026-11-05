// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.DataAbstractionLayer;
using XMAT.Operations;
using XMAT.Operations.Export;
using XMAT.SharedInterfaces;
using XMAT.Tests.Fixtures;
using XMAT.WebServiceCapture;
using static XMAT.Tests.Operations.Export.ExportTestData;

namespace XMAT.Tests.Operations.Export
{
    public sealed class CaptureExportPublisherTests
    {
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
                allowUnprotected: false,
                _ => { },
                (path, cancellationToken) => File.WriteAllTextAsync(path, "complete-new-output", cancellationToken),
                CancellationToken.None);

            Assert.Equal("complete-new-output", File.ReadAllText(destination));
            Assert.Equal(new FileInfo(destination).Length, result.Bytes);
            Assert.Equal(Sha256(destination), result.Sha256);
            Assert.True(result.ReplacedExisting);
            Assert.True(result.Protected);
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
                allowUnprotected: false,
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
        public async Task Publish_WithoutReplacementRefusesExistingAndRacedDestinations()
        {
            using var directory = new ExportTestDirectory();
            string destination = directory.PathOf("result.saz");
            File.WriteAllText(destination, "existing");
            var publisher = new CaptureExportPublisher();

            CaptureExportDestinationException failure =
                await Assert.ThrowsAsync<CaptureExportDestinationException>(
                () => publisher.PublishAsync(
                    destination,
                    replaceExisting: false,
                    allowUnprotected: false,
                    _ => { },
                    (path, cancellationToken) => File.WriteAllTextAsync(path, "new", cancellationToken),
                    CancellationToken.None));

            Assert.Equal("export.output.exists", failure.Code);
            Assert.Equal("The export destination already exists.", failure.Message);
            Assert.Equal("existing", File.ReadAllText(destination));
            Assert.Equal(new[] { destination }, Directory.GetFileSystemEntries(directory.Root));

            File.Delete(destination);
            CaptureExportDestinationException raced =
                await Assert.ThrowsAsync<CaptureExportDestinationException>(
                    () => publisher.PublishAsync(
                        destination,
                        replaceExisting: false,
                        allowUnprotected: false,
                        _ => { },
                        async (staged, cancellationToken) =>
                        {
                            await File.WriteAllTextAsync(staged, "complete", cancellationToken);
                            await File.WriteAllTextAsync(destination, "racer", cancellationToken);
                        },
                        CancellationToken.None));
            Assert.Equal("export.output.exists", raced.Code);
            Assert.Equal("racer", File.ReadAllText(destination));
            Assert.Equal(new[] { destination }, Directory.GetFileSystemEntries(directory.Root));
        }

        [Fact]
        public async Task Publish_RefusesALoadedCaptureAndItsHardLinkAliasBeforeWriting()
        {
            using var directory = new ExportTestDirectory();
            string loaded = SyntheticCaptures.WriteXmatcap(directory.Root, "loaded.xmatcap");
            string alias = directory.PathOf("loaded-alias.xmatcap");
            CaptureAppModelTests.CreateHardLink(alias, loaded);
            var controller = new WebServiceDeviceCaptureController("Loaded", DeviceType.LocalPC, true);
            var database = new SqLtReadonlyDatabase(loaded);
            LoadedCaptureDatabaseRegistry.Assign(controller, database);
            var publisher = new CaptureExportPublisher();
            var writes = 0;
            try
            {
                foreach (string destination in new[] { loaded, alias })
                {
                    await Assert.ThrowsAsync<CaptureSaveException>(
                        () => publisher.PublishAsync(
                            destination,
                            replaceExisting: true,
                            allowUnprotected: false,
                            _ => { },
                            (path, cancellationToken) =>
                            {
                                writes++;
                                return File.WriteAllTextAsync(path, "unexpected", cancellationToken);
                            },
                            CancellationToken.None));
                }
            }
            finally
            {
                LoadedCaptureDatabaseRegistry.Release(controller);
            }

            Assert.Equal(0, writes);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Publish_UnprotectedStorageRequiresExplicitConsent(bool allowUnprotected)
        {
            using var directory = new ExportTestDirectory();
            string destination = directory.PathOf("unprotected.xmatcap");
            var publisher = new CaptureExportPublisher
            {
                StagingIsPrivate = _ => false,
            };

            if (!allowUnprotected)
            {
                CaptureExportException failure = await Assert.ThrowsAsync<CaptureExportException>(
                    () => publisher.PublishAsync(
                        destination,
                        replaceExisting: false,
                        allowUnprotected: allowUnprotected,
                        _ => { },
                        (path, cancellationToken) => File.WriteAllTextAsync(path, "unexpected", cancellationToken),
                        CancellationToken.None));
                Assert.Equal(CaptureExportErrorCodes.OutputUnprotected, failure.Code);
                Assert.Equal(OperationOutcome.Failed, failure.Outcome);
                Assert.False(File.Exists(destination));
                Assert.Empty(Directory.GetFileSystemEntries(directory.Root));
                return;
            }

            PublishedCapture result = await publisher.PublishAsync(
                destination,
                replaceExisting: false,
                allowUnprotected: allowUnprotected,
                _ => { },
                (path, cancellationToken) => File.WriteAllTextAsync(path, "complete", cancellationToken),
                CancellationToken.None);
            Assert.False(result.Protected);
            Assert.Equal("complete", File.ReadAllText(destination));
        }
    }
}
