// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using XMAT.Operations.Replay;

namespace XMAT.Tests.Replay
{
    // Dual-parser conformance: every image that Raw mode accepts arrives at an in-process
    // loopback Kestrel, with limits at or above Raw mode's, as exactly one request, read as the image means it: its method,
    // its raw request-target, every field of the image (names ignoring ASCII case, values OWS-trimmed) and the exact
    // decoded entity. Kestrel's own framing fields are not compared, and no claim is made about other stacks.
    public sealed class RawKestrelConformanceTests
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

        [Fact]
        public async Task EveryAcceptedImage_ArrivesAsExactlyOneRequest_ReadAsTheImageMeansIt()
        {
            var received = new ConcurrentQueue<Received>();
            using IHost host = new HostBuilder().ConfigureWebHost(web =>
            {
                web.UseKestrel(options =>
                {
                    options.Limits.MaxRequestLineSize = 32 * 1024;
                    options.Limits.MaxRequestHeadersTotalSize = 128 * 1024;
                    options.Limits.MaxRequestHeaderCount = 512;
                    options.Limits.MaxRequestBodySize = null;
                    options.Limits.MaxRequestBufferSize = null;
                    options.Listen(IPAddress.Loopback, 0);
                });
                web.Configure(app => app.Run(async context =>
                {
                    IHttpRequestFeature feature = context.Features.Get<IHttpRequestFeature>();
                    using var body = new MemoryStream();
                    await context.Request.Body.CopyToAsync(body);
                    received.Enqueue(new Received(
                        context.Request.Method,
                        feature.RawTarget,
                        context.Request.Headers.ToDictionary(header => header.Key, header => header.Value.ToString(), StringComparer.OrdinalIgnoreCase),
                        body.ToArray()));
                    context.Response.StatusCode = 200;
                    context.Response.ContentLength = 0;
                }));
            }).Build();
            await host.StartAsync();
            try
            {
                int port = new Uri(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses.First()).Port;
                Assert.True(ReplayRawTarget.TryParse($"http://127.0.0.1:{port}", out ReplayRawTarget literal));
                await SendAllAsync(literal, RawSenderSetup.Direct, Corpus(port), received);

                // A name, routed to the listener through test-only seams (production connect-to is HTTPS only): Host in any
                // case with origin-form, and exactly the written authority with absolute-form, in each default-port form.
                Assert.True(ReplayRawTarget.TryParse($"http://xmat-raw.test:{port}", out ReplayRawTarget named));
                await SendAllAsync(named, new RawSenderSetup(Resolver: Loopback), NamedCorpus(port), received);
                Assert.True(ReplayRawTarget.TryParse("http://xmat-raw.test", out ReplayRawTarget defaultPort));
                var toListener = new RawSenderSetup(
                    Resolver: Loopback,
                    Connector: (_, token) => RawSenderSetup.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), token));
                await SendAllAsync(defaultPort, toListener, DefaultPortCorpus(), received);
            }
            finally
            {
                await host.StopAsync();
            }
        }

        private static Task<IPAddress[]> Loopback(string host, CancellationToken token) =>
            Task.FromResult(host == "xmat-raw.test" ? new[] { IPAddress.Loopback } : Array.Empty<IPAddress>());

        private static async Task SendAllAsync(
            ReplayRawTarget target, RawSenderSetup setup, IEnumerable<string> corpus, ConcurrentQueue<Received> received)
        {
            foreach (string request in corpus)
            {
                RawImageResult admitted = RawRequest.FromBytes(target, Encoding.Latin1.GetBytes(request), false, CancellationToken.None);
                Assert.True(admitted.Code == null, $"{admitted.Code} at {admitted.Line}: {Shorten(request)}");
                RawRequestImage image = admitted.Image;
                int before = received.Count;

                ReplayExchange exchange = await new RawSender(setup)
                    .SendAsync(image, new RawSendOptions(TimeSpan.FromSeconds(30), 1024), CancellationToken.None)
                    .WaitAsync(Wait);

                Assert.True(
                    exchange.Outcome == ReplayOutcome.Completed && exchange.StatusCode == 200,
                    $"{exchange.Outcome} {exchange.Code} {exchange.StatusCode}: {Shorten(request)}");
                Assert.Equal(before + 1, received.Count);
                Received read = received.ToArray()[^1];
                Assert.Equal(image.Method, read.Method);
                Assert.Equal(image.RequestTarget, read.RawTarget);
                foreach (KeyValuePair<string, string> field in image.Fields)
                {
                    if (field.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                        field.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    Assert.True(read.Fields.TryGetValue(field.Key, out string value), $"{field.Key} missing: {Shorten(request)}");
                    Assert.Equal(field.Value, value);
                }

                Assert.Equal(image.CopyEntity(), read.Body);
            }
        }

        private static IEnumerable<string> NamedCorpus(int port)
        {
            yield return $"GET / HTTP/1.1\r\nHost: xmat-raw.test:{port}\r\n\r\n";
            yield return $"GET / HTTP/1.1\r\nHost: XMAT-RAW.Test:{port}\r\n\r\n";
            yield return $"GET http://xmat-raw.test:{port}/a HTTP/1.1\r\nHost: xmat-raw.test:{port}\r\n\r\n";
            yield return $"GET http://xmat-raw.test:{port}/a?b HTTP/1.1\r\nHost: \txmat-raw.test:{port} \r\n\r\n";
        }

        private static IEnumerable<string> DefaultPortCorpus()
        {
            yield return "GET / HTTP/1.1\r\nHost: xmat-raw.test\r\n\r\n";
            yield return "GET / HTTP/1.1\r\nHost: XMAT-raw.TEST:80\r\n\r\n";
            yield return "GET http://xmat-raw.test/a HTTP/1.1\r\nHost: xmat-raw.test\r\n\r\n";
            yield return "GET http://xmat-raw.test:80/a HTTP/1.1\r\nHost: xmat-raw.test:80\r\n\r\n";
        }

        // The accepted images of the request-rule corpus, and one at each of Raw mode's limits.
        private static IEnumerable<string> Corpus(int port)
        {
            string host = $"Host: 127.0.0.1:{port}\r\n";
            yield return "GET / HTTP/1.1\r\n" + host + "\r\n";
            yield return $"GET http://127.0.0.1:{port}/a?b HTTP/1.1\r\n" + host + "\r\n";
            yield return $"GET http://127.0.0.1:{port}/a HTTP/1.1\r\n" + host + "\r\n";
            yield return "GET /!$&'()*+,;=:@-._~%2Fa/b?c/d?e HTTP/1.1\r\n" + host + "\r\n";
            yield return "GET / HTTP/1.1\r\n" + host + "Connection: close\r\n\r\n";
            yield return "GET / HTTP/1.1\r\n" + host + "X-Empty:\r\nX-Tab: a\tb\r\nX-Spaced: \t padded \t\r\n\r\n";
            yield return "PURGE / HTTP/1.1\r\n" + host + "\r\n";
            yield return "purge / HTTP/1.1\r\n" + host + "\r\n";
            yield return "HEAD / HTTP/1.1\r\n" + host + "Content-Length: 0\r\n\r\n";
            yield return "GET / HTTP/1.1\r\n" + host + "Content-Length: 1\r\n\r\nx";
            yield return "OPTIONS / HTTP/1.1\r\n" + host + "Transfer-Encoding: chunked\r\n\r\n0\r\n\r\n";
            yield return "POST / HTTP/1.1\r\n" + host + "Content-Length: 5\r\n\r\nhello";
            yield return "POST / HTTP/1.1\r\n" + host + "content-length: 4\r\n\r\n\u0000\u00ff\r\n";
            yield return "POST / HTTP/1.1\r\n" + host + "Transfer-Encoding: chunked\r\n\r\n5\r\nhello\r\nA\r\n0123456789\r\na\r\n0123456789\r\n0\r\n\r\n";
            yield return "PUT /x HTTP/1.1\r\n" + host + "Content-Length: 128\r\n\r\n" + new string(Enumerable.Range(0x80, 128).Select(i => (char)i).ToArray());

            // At the limits: the head, the request line, the field lines and the method.
            string start = "GET / HTTP/1.1\r\n" + host + "X-Pad: ";
            yield return start + new string('a', RawRequest.MaxHeadBytes - start.Length - 4) + "\r\n\r\n";
            string line = "GET /" + new string('a', RawRequest.MaxRequestLineBytes - "GET /".Length - " HTTP/1.1\r\n".Length) + " HTTP/1.1\r\n";
            yield return line + host + "\r\n";
            yield return "GET / HTTP/1.1\r\n" + host +
                string.Concat(Enumerable.Range(1, RawRequest.MaxFieldLines - 1).Select(i => $"X-{i}: v\r\n")) + "\r\n";
            yield return new string('M', RawRequest.MaxMethodChars) + " / HTTP/1.1\r\n" + host + "\r\n";

            // A larger chunked entity, many chunks.
            var chunks = new StringBuilder();
            for (int index = 0; index < 1000; index++)
            {
                chunks.Append("400\r\n").Append(new string((char)('a' + (index % 26)), 1024)).Append("\r\n");
            }

            yield return "POST /big HTTP/1.1\r\n" + host + "Transfer-Encoding: chunked\r\n\r\n" + chunks + "0\r\n\r\n";
        }

        private static string Shorten(string request) => request.Length <= 80 ? request : request[..80] + "...";

        private sealed record Received(string Method, string RawTarget, Dictionary<string, string> Fields, byte[] Body);
    }
}
