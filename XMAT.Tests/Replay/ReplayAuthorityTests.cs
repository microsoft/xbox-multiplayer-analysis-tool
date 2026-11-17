// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Globalization;
using XMAT.Operations.Replay;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Replay
{
    // The authority that replay records (ReplayAuthority) is the Host that the HTTP stack sends: shown against a loopback
    // server, which every connection reaches through Connect to, whatever the URL's host. Nothing leaves the machine.
    public class ReplayAuthorityTests
    {
        public ReplayAuthorityTests() => Localization.LoadLanguage("en-us");

        [Theory]
        [InlineData("http://b\u00fccher.example:{port}/\u00e4 b?\u00f6=1", "xn--bcher-kva.example:{port}")]
        [InlineData("http://b\u00fccher.example/default-port", "xn--bcher-kva.example")]
        [InlineData("http://example.test:80/explicit-default-port", "example.test")]
        [InlineData("http://[::1]:{port}/v6", "[::1]:{port}")]
        [InlineData("http://[fe80::1%251]:{port}/zone", "[fe80::1]:{port}")]
        [InlineData("http://EXAMPLE.test:{port}/case", "example.test:{port}")]
        [InlineData("http://127.0.0.1:{port}/v4", "127.0.0.1:{port}")]
        public async Task TheRecordedHost_IsTheHostThatWasSent(string template, string expected)
        {
            await using var server = new LoopbackHttpServer(_ => LoopbackResponse.Ok("ok"));
            string port = server.Port.ToString(CultureInfo.InvariantCulture);
            Assert.True(ReplayConnectTo.TryParse("127.0.0.1:" + port, out ReplayConnectTo connectTo));
            using var sender = new ReplaySender(new ReplaySenderSetup(ConnectTo: connectTo));

            ReplayExchange exchange = await sender.SendAsync(
                new RequestReplayDraft { Url = template.Replace("{port}", port) }, ReplaySendOptions.Default, CancellationToken.None);

            Assert.Equal(ReplayOutcome.Completed, exchange.Outcome);
            string sent = Assert.Single(server.Requests).Header("Host");
            Assert.Equal(expected.Replace("{port}", port), sent);
            Assert.Equal(new KeyValuePair<string, string>("Host", sent), exchange.Request.Headers[0]);
            Assert.Equal(sent, ReplayAuthority.Ascii(exchange.RequestUri));

            // The Replays row names the same host and port, and its head the same Host.
            ProxyConnectionModel row = ReplayRecorder.ToRow(1, ReplayProvenance.Composed, exchange);
            Assert.True(HostAuthority.TryParse(sent, out string host, out int? sentPort));
            Assert.Equal((host, sentPort?.ToString(CultureInfo.InvariantCulture) ?? ""), (row.Host, row.Port));
            Assert.Contains("\r\nHost: " + sent + "\r\n", row.RequestLineAndHeaders, StringComparison.Ordinal);
        }
    }
}
