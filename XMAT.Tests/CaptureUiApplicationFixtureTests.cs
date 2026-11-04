// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

namespace XMAT.Tests
{
    public class CaptureUiApplicationFixtureTests
    {
        // A run's storage folder is kept while one of its files is still open and deleted once it is closed. Folders of
        // other runs, and folders not named like a run's storage, are never deleted.
        [Fact]
        public async Task TryDeleteStorage_DeletesOnlyTheRunsOwnFolder_OnceItsFilesAreClosed()
        {
            var root = Path.Join(AppContext.BaseDirectory, "StorageCleanupFixtures", Guid.NewGuid().ToString("N"));
            var owned = Path.Join(root, CaptureUiApplicationFixture.StoragePrefix + "owned");
            var other = Path.Join(root, CaptureUiApplicationFixture.StoragePrefix + "other");
            var openLog = Path.Join(owned, "Logs", "application.log");
            Directory.CreateDirectory(Path.GetDirectoryName(openLog));
            Directory.CreateDirectory(other);
            try
            {
                using (new FileStream(openLog, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    Assert.False(
                        await CaptureUiApplicationFixture.TryDeleteStorageAsync(owned, TimeSpan.FromMilliseconds(300)));
                    Assert.True(File.Exists(openLog));
                }

                Assert.True(await CaptureUiApplicationFixture.TryDeleteStorageAsync(owned, TimeSpan.FromSeconds(10)));
                Assert.False(Directory.Exists(owned));
                Assert.True(Directory.Exists(other));
                await Assert.ThrowsAsync<ArgumentException>(
                    () => CaptureUiApplicationFixture.TryDeleteStorageAsync(root, TimeSpan.Zero));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
