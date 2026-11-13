// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests
{
    public class ProxyConnectionModelHeaderTests
    {
        // The capture limit is set in whole megabytes; smaller sizes come from the proxy's own options. The notice says only
        // what the capture kept: a response that ended early may not have reached the client in full.
        [Theory]
        [InlineData(0L, "")]
        [InlineData(16L, "Capture truncated at 16 bytes; additional bytes are not captured.")]
        [InlineData(32L * 1024 * 1024, "Capture truncated at 32 MB; additional bytes are not captured.")]
        public void ResponseCaptureNotice_StatesWhereTheCaptureWasTruncated(long truncatedAt, string notice)
        {
            Localization.LoadLanguage("en-us");
            var model = new ProxyConnectionModel(
                1,
                "200",
                DateTime.UnixEpoch,
                TimeSpan.Zero,
                "https",
                "example.test",
                "GET",
                "/",
                "GET / HTTP/1.1\r\n",
                Array.Empty<byte>(),
                "HTTP/1.1 200 OK\r\n",
                Array.Empty<byte>(),
                "10.0.0.1");

            model.SetResponseCaptureDetails(string.Empty, truncatedAt);

            Assert.Equal(notice, model.ResponseCaptureNotice);
        }

        [Fact]
        public void HeaderCollectionsAreLazyAndPreserveExistingSemantics()
        {
            const string requestHeaders =
                "GET / HTTP/1.1\r\n" +
                "X-Test: first\r\n" +
                "x-test: second\r\n";
            const string responseHeaders =
                "HTTP/1.1 200 OK\r\n" +
                "X-Response: value\r\n";
            var model = new ProxyConnectionModel(
                1,
                "200",
                DateTime.UnixEpoch,
                TimeSpan.Zero,
                "https",
                "example.test",
                "GET",
                "/",
                requestHeaders,
                Array.Empty<byte>(),
                responseHeaders,
                Array.Empty<byte>(),
                "10.0.0.1");

            Assert.False(
                model.HeaderCollectionsMaterialized);
            Assert.Equal(
                "second",
                model.RequestHeaders["X-Test"]);
            Assert.True(
                model.HeaderCollectionsMaterialized);
            Assert.Equal(
                "value",
                model.ResponseHeaders["x-response"]);
            Assert.Equal(
                requestHeaders,
                model.RequestLineAndHeaders);
            Assert.Equal(
                responseHeaders,
                model.ResponseLineAndHeaders);
        }
    }
}
