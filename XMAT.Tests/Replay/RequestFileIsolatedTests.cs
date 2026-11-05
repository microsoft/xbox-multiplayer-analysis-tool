// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.Operations.Replay;
using XMAT.Tests.Proxy;

namespace XMAT.Tests.Replay
{
    // Request file tests that measure allocations across the process, where the reading runs on a thread pool thread: the
    // collection runs them alone, so no other test's allocations count.
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
    }
}
