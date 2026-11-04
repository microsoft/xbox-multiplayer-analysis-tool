// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests
{
    public class ProxyConnectionModelHeaderTests
    {
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
