// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text;
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

        // The header parser that the dictionaries and HasRequestHeader share reads every head exactly as the former
        // split did (OldStringToHeaders, kept as it was): the same names, with the same spelling, and the same values, on
        // heads chosen for the split's odd corners and on random ones; and HasRequestHeader, before and after the dictionary
        // is made, answers as its ContainsKey.
        [Fact]
        public void HeaderParser_ReadsEveryHeadAsTheFormerSplit()
        {
            var heads = new List<string>
            {
                null,
                "",
                "GET / HTTP/1.1",
                "GET / HTTP/1.1\r\n",
                "GET / HTTP/1.1\r\nName: value\r\n\r\n",
                "GET / HTTP/1.1\r\n:Upgrade: x\r\nName::: v\r\nName2: a: b\r\nEmpty:\r\n: v\r\n  :  : Late : w \r\n",
                "GET / HTTP/1.1\r\nUpgrade:   \r\nSec-WebSocket-Key\r\nX-A:\t\u00A0spaced\u00A0\t\r\n\u00A0Name\u00A0: nb\r\n",
                "GET / HTTP/1.1\r\nUPGRADE: one\r\nupgrade: two\r\nX: 1\rY: 2\nZ: 3\r\n",
                "GET /a:b HTTP/1.1\r\nHost: example.test:8080\r\n\r\nAfter: blank\r\n",
                "Upgrade: on the first line\r\nX: y",
                "GET / HTTP/1.1\r\r\nA: 1\r\n\nB: 2\r\n\r\r\nC: 3",
                "GET / HTTP/1.1\r\n:::\r\n::x::\r\n x \r\n:x\r\nx:\r\n",
            };
            var random = new Random(1234);
            const string alphabet = ":: \t\u00A0aBxU-\r\n";
            for (int head = 0; head < 2000; head++)
            {
                var text = new StringBuilder("GET / HTTP/1.1");
                for (int line = random.Next(4); line >= 0; line--)
                {
                    text.Append("\r\n");
                    for (int length = random.Next(12); length > 0; length--)
                    {
                        text.Append(alphabet[random.Next(alphabet.Length)]);
                    }
                }

                heads.Add(text.ToString());
            }

            string[] names = { "Upgrade", "upgrade", "Sec-WebSocket-Key", "Name", "Late", "X", "x", "a", "aB", "U", "-", "Missing" };
            foreach (string head in heads)
            {
                Dictionary<string, string> expected = OldStringToHeaders(head);
                var cold = Row(head);
                foreach (string name in names)
                {
                    Assert.True(expected.ContainsKey(name) == cold.HasRequestHeader(name), Describe(head, name));
                }

                Assert.False(cold.HeaderCollectionsMaterialized);
                Assert.Equal(
                    expected.OrderBy(header => header.Key, StringComparer.Ordinal),
                    cold.RequestHeaders.OrderBy(header => header.Key, StringComparer.Ordinal));
                foreach (string name in names)
                {
                    Assert.True(expected.ContainsKey(name) == cold.HasRequestHeader(name), Describe(head, name));
                }
            }
        }

        // A row whose head is huge, such as one whose request line holds a long URL, is checked for a header without
        // making its dictionary or copying its head.
        [Fact]
        public void HasRequestHeader_ReadsALargeHeadInPlace()
        {
            string head = "GET /" + new string('a', 16 * 1024 * 1024) + " HTTP/1.1\r\nHost: example.test\r\nUpgrade: websocket\r\n";
            var warmUp = Row("GET / HTTP/1.1\r\nX: 1\r\n");
            Assert.True(warmUp.HasRequestHeader("X"));
            Assert.False(warmUp.HasRequestHeader("Upgrade"));
            var row = Row(head);

            long before = GC.GetAllocatedBytesForCurrentThread();
            bool upgrade = row.HasRequestHeader("upgrade");
            bool missing = row.HasRequestHeader("Missing");
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(upgrade);
            Assert.False(missing);
            Assert.True(allocated < 64 * 1024, $"Checking for a header allocated {allocated} bytes.");
            Assert.False(row.HeaderCollectionsMaterialized);
        }

        private static ProxyConnectionModel Row(string requestLineAndHeaders) =>
            new(1, "200", DateTime.UnixEpoch, TimeSpan.Zero, "https", "example.test", "GET", "/", requestLineAndHeaders,
                Array.Empty<byte>(), "HTTP/1.1 200 OK\r\n", Array.Empty<byte>(), "10.0.0.1");

        private static string Describe(string head, string name) =>
            $"{name} in {(head == null ? "null" : string.Concat(head.Select(c => c < 0x20 || c > 0x7E ? $"\\u{(int)c:X4}" : c.ToString())))}";

        // ProxyConnectionModel.StringToHeaders before the shared parser, as it was.
        private static Dictionary<string, string> OldStringToHeaders(string firstLineAndHeaders)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(firstLineAndHeaders))
            {
                string[] lines = firstLineAndHeaders.Split("\r\n");

                for (int i = 1; i < lines.Length; i++)
                {
                    string line = lines[i];
                    string[] header = line.Split(':', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (header.Length < 2)
                    {
                        // TODO: we have a bad header
                    }
                    else
                    {
                        string name = header[0].Trim();
                        string value = header[1].Trim();

                        headers[name] = value;
                    }
                }
            }
            return headers;
        }
    }
}
