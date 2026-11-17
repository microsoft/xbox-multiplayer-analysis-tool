// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.DataAbstractionLayer;
using XMAT.Operations.Captures;
using XMAT.Tests.Cli;
using XMAT.WebServiceCapture;
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

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ResponseHeadersAreChargedInStoredUtf8Bytes(bool trailers)
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
                    };
                    // One UTF-16 code unit but three UTF-8 bytes each: only the stored size exceeds the limit.
                    string oversized = new('\u20AC', (int)(ProxyCaptureRecorder.MaximumUpdateBytes / 3) + 1);
                    if (trailers)
                        response.Trailers.Add("X-Trailer", oversized);
                    else
                        response.StatusDescription = oversized;

                    Assert.False(recorder.RecordResponse(new ClientRequest { RequestNumber = 1 }, response, DateTime.UtcNow));
                    Assert.True(recorder.Failure.Task.IsCompleted);
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ForwardedResponsesKeepTrailersTruncationAndHeaderProvenance(bool truncated)
        {
            using var directory = new CliTestDirectory();
            string path = Path.Join(directory.Root, "stream.xmatcap");
            var response = new ServerResponse
            {
                Version = "HTTP/1.1", Status = "200", StatusDescription = "OK",
                BodyBytes = "captured-prefix"u8.ToArray(),
            };
            response.CaptureTruncatedAt = truncated ? response.BodyBytes.Length : 0;
            response.Trailers.Add("X-Checksum", "synthetic");
            using (var recorder = new ProxyCaptureRecorder(path))
            {
                var proxy = new WebServiceProxy();
                recorder.Attach(proxy);
                var request = new ClientRequest
                {
                    RequestNumber = 1, Version = "HTTP/1.1", Scheme = "http", Host = "localhost",
                    Port = 80, Method = "GET", Path = "/stream",
                };
                Assert.True(await proxy.RaiseReceivedWebRequestAsync(1, request, CancellationToken.None));
                Assert.True(await proxy.RaiseReceivedWebResponseAsync(
                    1, request, response, CancellationToken.None, responseForwarded: true));
            }

            using var saved = new SqLtReadonlyDatabase(path);
            var row = Assert.Single(saved.TableByName(WebServiceCaptureMethod.TableName).Dataset.DataRecords);
            Assert.Equal(response.CaptureTruncatedAt, WebProxyCaptureSchema.ReadResponseBodyTruncatedAt(row));
            Assert.Equal(response.Trailers.ToString(), WebProxyCaptureSchema.ReadResponseTrailers(row));
            Assert.Equal(HeaderProvenance.NoKnownLoss, WebProxyCaptureSchema.ReadHeaderProvenance(row));
            Assert.Equal(response.BodyBytes, Convert.FromBase64String(row.Str(WebServiceCaptureMethod.FieldKey_ResponseBody)));
        }
    }
}
