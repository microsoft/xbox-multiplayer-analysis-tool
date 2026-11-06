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

            File.Delete(destination);
            Directory.CreateDirectory(destination);
            CaptureExportDestinationException directoryFailure =
                await Assert.ThrowsAsync<CaptureExportDestinationException>(
                    () => publisher.PublishAsync(
                        destination,
                        replaceExisting: false,
                        allowUnprotected: false,
                        _ => { },
                        (path, cancellationToken) => File.WriteAllTextAsync(path, "unexpected", cancellationToken),
                        CancellationToken.None));
            Assert.Equal("export.output.exists", directoryFailure.Code);
            Assert.Empty(Directory.GetFileSystemEntries(destination));
        }

        [Fact]
        public async Task Publish_NativeAlreadyExistsAfterTheCommitCheckIsAConflict()
        {
            using var directory = new ExportTestDirectory();
            string destination = directory.PathOf("raced.xmatcap");
            var moves = 0;
            var publisher = new CaptureExportPublisher
            {
                MoveFile = (source, target, overwrite) =>
                {
                    moves++;
                    File.WriteAllText(target, "racer");
                    File.Move(source, target, overwrite);
                },
            };

            CaptureExportDestinationException failure =
                await Assert.ThrowsAsync<CaptureExportDestinationException>(
                    () => publisher.PublishAsync(
                        destination,
                        replaceExisting: false,
                        allowUnprotected: false,
                        _ => { },
                        (staged, cancellationToken) =>
                            File.WriteAllTextAsync(staged, "complete", cancellationToken),
                        CancellationToken.None));

            Assert.Equal("export.output.exists", failure.Code);
            Assert.Equal(1, moves);
            Assert.Equal("racer", File.ReadAllText(destination));
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
                    CaptureSaveException failure = await Assert.ThrowsAsync<CaptureSaveException>(
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
                    Assert.Equal(CaptureSaveFailure.Refused, failure.Failure);
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

        [Theory]
        [InlineData("transient")]
        [InlineData("cancel")]
        [InlineData("cleanup")]
        public async Task Publish_RetriesWithoutRepeatingTheWriter(string mode)
        {
            using var directory = new ExportTestDirectory();
            string destination = directory.PathOf("retry.xmatcap");
            using var cancellation = new CancellationTokenSource();
            FileStream blocker = null;
            var moves = 0;
            var writes = 0;
            var publisher = new CaptureExportPublisher
            {
                MoveFile = (source, target, overwrite) =>
                {
                    moves++;
                    if (mode == "transient" && moves == 1)
                    {
                        throw new IOException("injected sharing violation", 32);
                    }
                    File.Move(source, target, overwrite);
                },
            };
            Task<PublishedCapture> publish = publisher.PublishAsync(
                destination,
                replaceExisting: true,
                allowUnprotected: false,
                _ => { },
                (staged, _) =>
                {
                    writes++;
                    File.WriteAllText(staged, "complete");
                    string blocked = mode == "cleanup" ? staged : destination;
                    if (mode != "cleanup")
                    {
                        File.WriteAllText(destination, "old");
                    }
                    if (mode != "transient")
                    {
                        blocker = new FileStream(blocked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    }
                    return mode == "cleanup"
                        ? Task.FromException(new IOException("injected write failure"))
                        : Task.CompletedTask;
                },
                cancellation.Token);

            try
            {
                if (mode == "transient")
                {
                    PublishedCapture result = await publish;
                    Assert.Equal(2, moves);
                    Assert.Equal("complete", File.ReadAllText(destination));
                    Assert.True(result.Protected);
                    Assert.Equal(new[] { destination }, Directory.GetFileSystemEntries(directory.Root));
                }
                else if (mode == "cancel")
                {
                    cancellation.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publish);
                }
                else
                {
                    await Assert.ThrowsAsync<CaptureExportCleanupException>(() => publish);
                }
            }
            finally
            {
                blocker?.Dispose();
            }
            Assert.Equal(1, writes);
        }
    }
}
