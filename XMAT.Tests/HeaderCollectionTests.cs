// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Tests
{
    public class HeaderCollectionTests
    {
        [Fact]
        public void Indexer_SetsAndGetsHeader()
        {
            var headers = new HeaderCollection();
            headers["Content-Type"] = "application/json";

            Assert.Equal("application/json", headers["Content-Type"]);
        }

        [Fact]
        public void Indexer_IsCaseInsensitive()
        {
            var headers = new HeaderCollection();
            headers["Content-Type"] = "application/json";

            Assert.Equal("application/json", headers["content-type"]);
            Assert.Equal("application/json", headers["CONTENT-TYPE"]);
        }

        [Fact]
        public void GetHeaderValuesAsString_ReturnsEmpty_ForMissingKey()
        {
            var headers = new HeaderCollection();
            Assert.Equal(string.Empty, headers.GetHeaderValuesAsString("X-Missing"));
        }

        [Fact]
        public void GetHeaderValuesAsList_ReturnsNull_ForMissingKey()
        {
            var headers = new HeaderCollection();
            Assert.Null(headers.GetHeaderValuesAsList("X-Missing"));
        }

        [Fact]
        public void ToString_FormatsHeaders()
        {
            var headers = new HeaderCollection();
            headers["Content-Type"] = "application/json";
            headers["Accept"] = "text/html";

            string result = headers.ToString();
            Assert.Contains("Content-Type: application/json\r\n", result);
            Assert.Contains("Accept: text/html\r\n", result);
        }

        [Fact]
        public void Indexer_OverwritesExistingValue()
        {
            var headers = new HeaderCollection();
            headers["Content-Type"] = "text/plain";
            headers["Content-Type"] = "application/json";

            Assert.Equal("application/json", headers["Content-Type"]);
        }

        [Fact]
        public void Enumerable_IteratesOverHeaders()
        {
            var headers = new HeaderCollection();
            headers["A"] = "1";
            headers["B"] = "2";

            int count = 0;
            foreach (var _ in headers)
                count++;

            Assert.Equal(2, count);
        }
    }
}
