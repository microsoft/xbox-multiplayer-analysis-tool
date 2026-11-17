// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.Operations.Captures;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Tests.Operations.Captures
{
    public sealed class ProxyCaptureRecorderTests
    {
        [Fact]
        public void OversizedResponseStopsCaptureBeforeEncodingBody()
        {
            string path = Path.Join(Path.GetTempPath(), "XMAT-recorder-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                using (var recorder = new ProxyCaptureRecorder(path))
                {
                    var response = new ServerResponse
                    {
                        Version = "HTTP/1.1",
                        Status = "200",
                        StatusDescription = "OK",
                        // Base64 of this body alone is just over the per-update limit.
                        BodyBytes = new byte[ProxyCaptureRecorder.MaximumUpdateBytes / 4 * 3 + 3],
                    };

                    long before = GC.GetAllocatedBytesForCurrentThread();
                    bool recorded = recorder.RecordResponse(new ClientRequest { RequestNumber = 1 }, response, DateTime.UtcNow);
                    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

                    Assert.False(recorded);
                    Assert.True(recorder.Failure.Task.IsCompleted);
                    Assert.True(allocated < 1024 * 1024, $"Allocated {allocated} bytes before rejecting the response.");
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void ResponseHeadersAreChargedInStoredUtf8Bytes()
        {
            string path = Path.Join(Path.GetTempPath(), "XMAT-recorder-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                using (var recorder = new ProxyCaptureRecorder(path))
                {
                    var response = new ServerResponse
                    {
                        Version = "HTTP/1.1",
                        Status = "200",
                        // One UTF-16 code unit but three UTF-8 bytes each: only the stored size exceeds the limit.
                        StatusDescription = new string('\u20AC', (int)(ProxyCaptureRecorder.MaximumUpdateBytes / 3) + 1),
                    };

                    Assert.False(recorder.RecordResponse(new ClientRequest { RequestNumber = 1 }, response, DateTime.UtcNow));
                    Assert.True(recorder.Failure.Task.IsCompleted);
                }
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
