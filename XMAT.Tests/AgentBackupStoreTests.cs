// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using XMAT.ApplicationServices;
using XMAT.DataAbstractionLayer;
using XMAT.SharedInterfaces;

namespace XMAT.Tests
{
    public class AgentBackupStoreTests
    {
        [Fact]
        public async Task NativeBackupsRetainOriginalRowsAndLoadThroughTheExistingDal()
        {
            using var directory = new TestDirectory();
            var store = new AgentBackupStore(Path.Combine(directory.Path, "Backups"));
            using var source = new SqLtDatabase();
            source.Initialize();
            var table = source.CreateTable("CaptureDevices",
                new Field<long>("DeviceType"), new Field<string>("DeviceName"), new Field<long>("CaptureType"),
                new Field<string>("Name"));
            table.AddRow(0L, "fixture", 0L, "original-sensitive-value");
            var receipt = await store.CaptureAsync(Guid.NewGuid(), Guid.NewGuid(), "1",
                "clear", "device:fixture", source, default);
            try
            {
                table.RemoveRowsWhere();
                using var restored = new SqLtReadonlyDatabase(Path.Combine(receipt.Directory, "capture.xmatcap"));
                Assert.Equal("original-sensitive-value", restored.TableByName("CaptureDevices").Dataset.DataRecords.Single().Str("Name"));
                var committed = await store.CommittedAsync(receipt);
                Assert.True(committed.OutcomeRecorded);
                using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(receipt.Directory, "manifest.json")));
                Assert.Equal("Committed", manifest.RootElement.GetProperty("actionStatus").GetString());
                Assert.Contains("Memory-only WebSocket messages",
                    manifest.RootElement.GetProperty("exclusions").EnumerateArray().Select(value => value.GetString()));
            }
            finally { receipt.Pin.Dispose(); }
        }

        [Fact]
        public async Task FilePreimagesAreExactDurableAndReplayedWithoutDuplicatingTheOperation()
        {
            using var directory = new TestDirectory();
            var store = new AgentBackupStore(Path.Combine(directory.Path, "Backups"));
            var original = Enumerable.Range(0, 100000).Select(index => (byte)(index % 256)).ToArray();
            using var source = new MemoryStream(original);
            var operation = Guid.NewGuid();
            var path = Path.Combine(directory.Path, "file.bin");
            var first = await store.FileAsync(operation, path, source, default);
            var replay = await store.FileAsync(operation, path, source, default);
            try
            {
                Assert.Equal(first.BackupId, replay.BackupId);
                Assert.Single(Directory.GetDirectories(Path.Combine(directory.Path, "Backups")));
                Assert.Equal(original, File.ReadAllBytes(Path.Combine(first.Directory, "previous-file.bin")));
                Assert.Equal(original, source.ToArray());
            }
            finally
            {
                first.Pin.Dispose();
                replay.Pin.Dispose();
            }
        }

        [Fact]
        public async Task RetentionCountsOperationsRatherThanIndividualFilePartsAndKeepsTheNewest()
        {
            using var directory = new TestDirectory();
            var root = Path.Combine(directory.Path, "Backups");
            var store = new AgentBackupStore(root, maximumBundles: 2);
            var old = Guid.NewGuid();
            for (var index = 0; index < 3; index++)
            {
                using var source = new MemoryStream(Encoding.UTF8.GetBytes("part" + index));
                var receipt = await store.FileAsync(old, Path.Combine(directory.Path, $"file{index}.txt"), source, default);
                receipt.Pin.Dispose();
            }
            Assert.Equal(3, Directory.GetDirectories(root).Length);
            for (var index = 0; index < 2; index++)
            {
                using var source = new MemoryStream([1, 2, 3]);
                var receipt = await store.FileAsync(Guid.NewGuid(), Path.Combine(directory.Path, "new.txt"), source, default);
                receipt.Pin.Dispose();
            }
            Assert.Equal(2, Directory.GetDirectories(root).Length);
            foreach (var path in Directory.GetDirectories(root))
            {
                using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "manifest.json")));
                Assert.NotEqual(old, manifest.RootElement.GetProperty("operationId").GetGuid());
            }
        }

        [Fact]
        public async Task ActiveRecoveryCopiesArePinnedAndRetentionFailureDoesNotRemoveThem()
        {
            using var directory = new TestDirectory();
            var root = Path.Combine(directory.Path, "Backups");
            var store = new AgentBackupStore(root, maximumBundles: 1);
            using var firstSource = new MemoryStream([1, 2, 3]);
            var first = await store.FileAsync(Guid.NewGuid(), Path.Combine(directory.Path, "first.bin"), firstSource, default);
            try
            {
                using var secondSource = new MemoryStream([4, 5, 6]);
                var failure = await Assert.ThrowsAsync<AgentBackupException>(() =>
                    store.FileAsync(Guid.NewGuid(), Path.Combine(directory.Path, "second.bin"), secondSource, default));
                Assert.Null(failure.Prepared);
                Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(first.Directory, "previous-file.bin")));
                Assert.Single(Directory.GetDirectories(root));
            }
            finally { first.Pin.Dispose(); }
        }

        [Fact]
        public async Task OversizedOrCancelledBackupsDoNotPublishOrChangeTheInput()
        {
            using var directory = new TestDirectory();
            var root = Path.Combine(directory.Path, "Backups");
            var store = new AgentBackupStore(root, maximumBytes: 4096);
            using var source = new MemoryStream(new byte[5000]);
            await Assert.ThrowsAsync<AgentBackupException>(() =>
                store.FileAsync(Guid.NewGuid(), Path.Combine(directory.Path, "large.bin"), source, default));
            Assert.Empty(Directory.GetDirectories(root));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.FileAsync(Guid.NewGuid(), Path.Combine(directory.Path, "cancel.bin"), source, cancelled.Token));
            Assert.Empty(Directory.GetDirectories(root));
            Assert.Equal(5000, source.Length);
        }

        [Fact]
        public void BoundedNativeBackupCancelsBeforePublicationAndPreservesAnExistingDestination()
        {
            using var directory = new TestDirectory();
            using var source = new SqLtDatabase();
            source.Initialize();
            source.CreateTable("Data", new Field<string>("Value")).AddRow("original");
            var path = Path.Combine(directory.Path, "existing.xmatcap");
            File.WriteAllText(path, "previous");
            using var cancellation = new CancellationTokenSource();
            source.BackupFailureInjection = _ => cancellation.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => source.SaveToFile(path, 1024 * 1024, cancellation.Token));
            Assert.Equal("previous", File.ReadAllText(path));
            Assert.Single(Directory.GetFiles(directory.Path));
        }

        private sealed class TestDirectory : IDisposable
        {
            internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "XMAT.Backups." + Guid.NewGuid().ToString("N"));
            internal TestDirectory() => Directory.CreateDirectory(Path);
            public void Dispose() => Directory.Delete(Path, true);
        }
    }
}
