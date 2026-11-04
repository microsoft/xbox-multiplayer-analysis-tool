// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit.Abstractions;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.Tests.Proxy
{
    [Collection(IsolatedProxyTests.Name)]
    public class ProxyRequestReadingTests
    {
        private static readonly Encoding Ascii =
            Encoding.ASCII;
        private readonly ITestOutputHelper _output;

        public ProxyRequestReadingTests(
            ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task FragmentedHeadersAndSeparateBody_AreForwardedAndCapturedCompletely()
        {
            await using var origin =
                await LoopbackOrigin.StartAsync();
            await using var proxy =
                await ProxyTestHarness.StartAsync();
            byte[] body =
                "fragmented-body"u8.ToArray();
            var observedBody =
                new TaskCompletionSource<byte[]>(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);
            proxy.Proxy.ReceivedWebRequest +=
                (_, args) =>
                    observedBody.TrySetResult(
                        args.Request.BodyBytes
                            .ToArray());
            string authority =
                $"127.0.0.1:{origin.Port}";
            byte[][] fragments =
            {
                Ascii.GetBytes(
                    "POST http://" +
                    authority +
                    "/fragmented HTTP/1.1\r\n"),
                Ascii.GetBytes(
                    "Host: " +
                    authority +
                    "\r\nContent-Length: " +
                    body.Length +
                    "\r\nConnection: close\r"),
                Ascii.GetBytes("\n\r\n"),
                body.AsSpan(0, 3).ToArray(),
                body.AsSpan(3).ToArray(),
            };

            string response =
                await SendRawAsync(
                    proxy.Port,
                    fragments,
                    TimeSpan.FromMilliseconds(20));

            AssertStatus(response, 200);
            Assert.Equal(
                body,
                await observedBody.Task.WaitAsync(
                    TimeSpan.FromSeconds(10)));
            Assert.Equal(
                body,
                Assert.Single(
                    origin.Requests).Body);
            Assert.Equal(
                body,
                Assert.Single(
                    proxy.CapturedRequests)
                    .BodyBytes);
        }

        [Fact]
        public async Task ChunkedExtensionsAndTrailers_AreDecodedBeforeCallbacksAndForwarding()
        {
            await using var origin =
                await LoopbackOrigin.StartAsync();
            await using var proxy =
                await ProxyTestHarness.StartAsync();
            string authority =
                $"127.0.0.1:{origin.Port}";
            byte[][] fragments =
            {
                Ascii.GetBytes(
                    "POST http://" +
                    authority +
                    "/chunked HTTP/1.1\r\n" +
                    "Host: " +
                    authority +
                    "\r\nTransfer-Encoding: chunked\r\n" +
                    "Trailer: X-Trailer\r\n" +
                    "Content-Encoding: gzip\r\n" +
                    "Connection: close\r\n\r\n" +
                    "4;name=value\r\nWi"),
                Ascii.GetBytes(
                    "ki\r\n5\r\nped"),
                Ascii.GetBytes(
                    "ia\r\n0\r\n" +
                    "X-Trailer: accepted\r\n\r\n"),
            };

            string response =
                await SendRawAsync(
                    proxy.Port,
                    fragments,
                    TimeSpan.FromMilliseconds(10));

            AssertStatus(response, 200);
            byte[] expected =
                "Wikipedia"u8.ToArray();
            ObservedRequest originRequest =
                Assert.Single(
                    origin.Requests);
            ClientRequest captured =
                Assert.Single(
                    proxy.CapturedRequests);
            Assert.Equal(expected, originRequest.Body);
            Assert.Equal(expected, captured.BodyBytes);
            Assert.Equal(
                "gzip",
                originRequest.Headers[
                    "Content-Encoding"]);
            Assert.Equal(
                "gzip",
                captured.ContentHeaders[
                    "Content-Encoding"]);
            Assert.Equal(
                string.Empty,
                captured.Headers[
                    "Transfer-Encoding"]);
            Assert.Equal(
                string.Empty,
                captured.Headers[
                    "Trailer"]);
        }

        [Theory]
        [InlineData(32 * 1024, true)]
        [InlineData((32 * 1024) + 1, false)]
        public async Task HeaderSectionBoundary_IsEnforced(
            int headerBytes,
            bool accepted)
        {
            await using var origin =
                await LoopbackOrigin.StartAsync();
            var webProxy =
                BypassLoopback();
            await using var proxy =
                await ProxyTestHarness.StartAsync(
                    webProxy);
            string request =
                ConnectRequestWithBytes(
                    origin.Port,
                    headerBytes);

            string response =
                await SendAndReadHeadersAsync(
                    proxy.Port,
                    Ascii.GetBytes(request));

            if (accepted)
            {
                AssertStatus(response, 200);
            }
            else
            {
                Assert.Equal(
                    FixedResponse(431),
                    response);
            }
        }

        [Theory]
        [InlineData(100, true)]
        [InlineData(101, false)]
        public async Task HeaderFieldBoundary_IsEnforced(
            int fields,
            bool accepted)
        {
            await using var origin =
                await LoopbackOrigin.StartAsync();
            await using var proxy =
                await ProxyTestHarness.StartAsync(
                    BypassLoopback());
            string request =
                ConnectRequestWithFields(
                    origin.Port,
                    fields);

            string response =
                await SendAndReadHeadersAsync(
                    proxy.Port,
                    Ascii.GetBytes(request));

            if (accepted)
            {
                AssertStatus(response, 200);
            }
            else
            {
                Assert.Equal(
                    FixedResponse(431),
                    response);
            }
        }

        [Theory]
        [InlineData(16, true)]
        [InlineData(17, false)]
        public async Task DecodedBodyBoundary_IsEnforced(
            int bodyBytes,
            bool accepted)
        {
            ProxyRequestReadPolicy policy =
                ProxyRequestReadPolicy.Default with
                {
                    MaxBodyBytes = 16,
                    MaxAggregateBufferedBytes =
                        1024 * 1024,
                };
            await using var origin =
                await LoopbackOrigin.StartAsync();
            await using var proxy =
                await ProxyTestHarness.StartAsync(
                    requestPolicy: policy);
            string authority =
                $"127.0.0.1:{origin.Port}";
            byte[] header =
                Ascii.GetBytes(
                    "POST http://" +
                    authority +
                    "/body-boundary HTTP/1.1\r\n" +
                    "Host: " +
                    authority +
                    "\r\nContent-Length: " +
                    bodyBytes +
                    "\r\nConnection: close\r\n\r\n");
            byte[] body =
                Enumerable.Repeat(
                        (byte)'a',
                        bodyBytes)
                    .ToArray();

            string response =
                await SendRawAsync(
                    proxy.Port,
                    new[] { header, body },
                    TimeSpan.Zero);

            if (accepted)
            {
                AssertStatus(response, 200);
                Assert.Single(origin.Requests);
            }
            else
            {
                Assert.Equal(
                    FixedResponse(413),
                    response);
                Assert.Empty(origin.Requests);
            }
        }

        [Fact]
        public async Task OversizedContentLength_IsRejectedWithoutReservationAndNextRequestRecovers()
        {
            await using var origin =
                await LoopbackOrigin.StartAsync();
            await using var proxy =
                await ProxyTestHarness.StartAsync();
            string authority =
                $"127.0.0.1:{origin.Port}";
            byte[] request =
                Ascii.GetBytes(
                    "POST http://" +
                    authority +
                    "/too-large HTTP/1.1\r\n" +
                    "Host: " +
                    authority +
                    "\r\nContent-Length: 2147483647\r\n\r\n");

            string rejected =
                await SendRawAsync(
                    proxy.Port,
                    new[] { request },
                    TimeSpan.Zero);

            Assert.Equal(
                FixedResponse(413),
                rejected);
            Assert.Equal(
                0,
                proxy.RequestBufferBudget
                    .ReservedBytes);
            using HttpClient client =
                proxy.CreateClient();
            Assert.Equal(
                "origin-ok",
                await client.GetStringAsync(
                    origin.Url("/recovered")));
            Assert.Equal(
                "/recovered",
                Assert.Single(
                    origin.Requests).Path);
            Assert.Single(
                proxy.CapturedRequests);
        }

        [Fact]
        public async Task MaximumBodyDeclarations_ReserveOnlyActualHeadersAndLeaveRoomForBody()
        {
            ProxyRequestReadPolicy policy =
                ProxyRequestReadPolicy.Default with
                {
                    MaxHeaderBytes = 2 * 1024,
                    MaxBodyBytes = 32 * 1024,
                    BodyInactivityTimeout =
                        TimeSpan.FromSeconds(15),
                    BodyTotalTimeout =
                        TimeSpan.FromSeconds(30),
                    MaxAggregateBufferedBytes =
                        128 * 1024,
                };
            await using var origin =
                await LoopbackOrigin.StartAsync();
            await using var proxy =
                await ProxyTestHarness.StartAsync(
                    requestPolicy: policy);
            string authority =
                $"127.0.0.1:{origin.Port}";
            var heldClients =
                new List<TcpClient>();
            try
            {
                long reserved = 0;
                for (var index = 0;
                    index < 4;
                    index++)
                {
                    var client =
                        new TcpClient();
                    await client.ConnectAsync(
                        IPAddress.Loopback,
                        proxy.Port);
                    heldClients.Add(client);
                    NetworkStream stream =
                        client.GetStream();
                    await stream.WriteAsync(
                        MaximumBodyDeclaration(
                            authority,
                            policy.MaxHeaderBytes,
                            policy.MaxBodyBytes,
                            out _));
                    await stream.FlushAsync();
                    long settled =
                        await WaitForStableReservationIncreaseAsync(
                            proxy.RequestBufferBudget,
                            reserved,
                            TimeSpan.FromSeconds(5));
                    Assert.InRange(
                        settled - reserved,
                        1,
                        (3L * policy.MaxHeaderBytes) - 1);
                    reserved = settled;
                }

                byte[] body =
                    new byte[policy.MaxBodyBytes];
                string response =
                    await SendRawAsync(
                        proxy.Port,
                        new[]
                        {
                            Ascii.GetBytes(
                                "POST http://" +
                                authority +
                                "/actual-body HTTP/1.1\r\n" +
                                "Host: " +
                                authority +
                                "\r\nContent-Length: " +
                                body.Length +
                                "\r\nConnection: close\r\n\r\n"),
                            body,
                        },
                        TimeSpan.Zero);

                AssertStatus(response, 200);
                Assert.Equal(
                    body,
                    Assert.Single(
                        origin.Requests).Body);
            }
            finally
            {
                foreach (TcpClient client in
                    heldClients)
                {
                    client.Dispose();
                }
                await WaitUntilAsync(
                    () =>
                        proxy.RequestBufferBudget
                            .ReservedBytes ==
                        0,
                    TimeSpan.FromSeconds(5));
            }
        }

        [Fact]
        public async Task StalledBody_RetainsConservativeHeaderChargeUntilHandoffThenReleases()
        {
            ProxyRequestReadPolicy policy =
                ProxyRequestReadPolicy.Default with
                {
                    MaxHeaderBytes = 2 * 1024,
                    MaxBodyBytes = 1,
                    BodyInactivityTimeout =
                        TimeSpan.FromSeconds(10),
                    BodyTotalTimeout =
                        TimeSpan.FromSeconds(15),
                    MaxAggregateBufferedBytes =
                        64 * 1024,
                };
            await using var origin =
                await LoopbackOrigin.StartAsync();
            await using var proxy =
                await ProxyTestHarness.StartAsync(
                    requestPolicy: policy);
            var handoffReservation =
                new TaskCompletionSource<long>(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);
            proxy.Proxy.ReceivedWebRequest +=
                (_, _) =>
                    handoffReservation.TrySetResult(
                        proxy.RequestBufferBudget
                            .ReservedBytes);
            string authority =
                $"127.0.0.1:{origin.Port}";
            byte[] declaration =
                MaximumBodyDeclaration(
                    authority,
                    policy.MaxHeaderBytes,
                    policy.MaxBodyBytes,
                    out int fillCharacters);
            using var client =
                new TcpClient();
            await client.ConnectAsync(
                IPAddress.Loopback,
                proxy.Port);
            using NetworkStream stream =
                client.GetStream();
            await stream.WriteAsync(
                declaration);
            await stream.FlushAsync();

            long retained =
                await WaitForStableReservationIncreaseAsync(
                    proxy.RequestBufferBudget,
                    0,
                    TimeSpan.FromSeconds(5));
            Assert.True(
                retained >=
                    (2L * fillCharacters),
                $"Only {retained} bytes were reserved for " +
                $"{fillCharacters} retained UTF-16 characters.");
            Assert.True(
                proxy.RequestBufferBudget
                    .PeakReservedBytes >
                retained);
            await Task.Delay(
                TimeSpan.FromMilliseconds(200));
            Assert.Equal(
                retained,
                proxy.RequestBufferBudget
                    .ReservedBytes);

            await stream.WriteAsync(
                new byte[] { (byte)'a' });
            await stream.FlushAsync();
            AssertStatus(
                await ReadToEndAsync(
                    stream,
                    TimeSpan.FromSeconds(10)),
                200);

            Assert.Equal(
                0,
                await handoffReservation.Task.WaitAsync(
                    TimeSpan.FromSeconds(5)));
            await WaitUntilAsync(
                () =>
                    proxy.RequestBufferBudget
                        .ReservedBytes ==
                    0,
                TimeSpan.FromSeconds(5));
            Assert.Equal(
                new byte[] { (byte)'a' },
                Assert.Single(
                    origin.Requests).Body);
        }

        [Fact]
        public async Task ManySmallFields_ChargeMeasuredGraphAndReleaseRawHeaderWhileBodyStalls()
        {
            const int SmallFieldCount = 64;
            string host =
                new string(
                    'h',
                    1023);
            string authority =
                host +
                ":80";
            string path =
                "http://" +
                authority +
                "/" +
                new string(
                    'p',
                    2048);
            string[] names =
                Enumerable.Range(
                        0,
                        SmallFieldCount)
                    .Select(index =>
                        $"X{index:D2}")
                    .ToArray();
            string[] values =
                Enumerable.Range(
                        0,
                        SmallFieldCount)
                    .Select(index =>
                        $"v{index:D2}")
                    .ToArray();
            var expected =
                new ClientRequest
                {
                    Method = "PUT",
                    Path = path,
                    Version = "HTTP/1.1",
                    Host = host,
                    Port = 80,
                };
            expected.Headers["Host"] =
                authority;
            for (var index = 0;
                index < SmallFieldCount;
                index++)
            {
                expected.Headers[
                    names[index]] =
                    values[index];
            }
            expected.ContentHeaders[
                "Content-Length"] =
                "1";

            var retainedStrings =
                new List<string>
                {
                    expected.Method,
                    expected.Path,
                    expected.Version,
                    expected.Host,
                    "Host",
                    authority,
                    "Content-Length",
                    "1",
                };
            for (var index = 0;
                index < SmallFieldCount;
                index++)
            {
                retainedStrings.Add(
                    names[index]);
                retainedStrings.Add(
                    values[index]);
            }
            long measuredMinimum =
                checked(
                    MeasureClientRequestAllocation() +
                    ((SmallFieldCount + 2L) *
                     MeasureSingleHeaderBookkeeping()) +
                    MeasureStringAllocations(
                        retainedStrings));
            long retainedEstimate =
                InitialProxyRequestReader
                    .EstimateRetainedRequestBytes(
                        expected);
            Assert.True(
                retainedEstimate >=
                    measuredMinimum,
                $"The {retainedEstimate} byte estimate did not cover " +
                $"the {measuredMinimum} byte measured object graph.");

            var headerText =
                new StringBuilder();
            headerText.Append(
                "PUT ");
            headerText.Append(path);
            headerText.Append(
                " HTTP/1.1\r\nHost: ");
            headerText.Append(authority);
            headerText.Append("\r\n");
            for (var index = 0;
                index < SmallFieldCount;
                index++)
            {
                headerText.Append(
                    names[index]);
                headerText.Append(": ");
                headerText.Append(
                    values[index]);
                headerText.Append("\r\n");
            }
            headerText.Append(
                "Content-Length: 1");
            string parsedText =
                headerText.ToString();
            byte[] header =
                Ascii.GetBytes(
                    parsedText +
                    "\r\n\r\n");
            ProxyRequestReadPolicy policy =
                ProxyRequestReadPolicy.Default with
                {
                    MaxHeaderBytes = 8 * 1024,
                    MaxBodyBytes = 1,
                    BodyInactivityTimeout =
                        TimeSpan.FromSeconds(10),
                    BodyTotalTimeout =
                        TimeSpan.FromSeconds(15),
                    MaxAggregateBufferedBytes =
                        1024 * 1024,
                };
            Assert.True(
                header.Length <=
                    policy.MaxHeaderBytes);
            var budget =
                new ProxyRequestBufferBudget(
                    policy);
            var reader =
                new InitialProxyRequestReader(
                    policy,
                    budget);
            var rawHeader =
                new TaskCompletionSource<
                    WeakReference<byte[]>>(
                        TaskCreationOptions
                            .RunContinuationsAsynchronously);
            reader.RawHeaderLifetimeObserver =
                reference =>
                    rawHeader.TrySetResult(
                        reference);
            var pipe =
                new System.IO.Pipelines.Pipe();
            using var cancellation =
                new CancellationTokenSource();
            Task<ProxyRequestReadResult> pending =
                reader.ReadAsync(
                    pipe.Reader,
                    cancellation.Token);
            try
            {
                await pipe.Writer.WriteAsync(
                    header);
                long retained =
                    await WaitForStableReservationIncreaseAsync(
                        budget,
                        0,
                        TimeSpan.FromSeconds(5));

                Assert.InRange(
                    retained -
                    retainedEstimate,
                    1,
                    1024);
                WeakReference<byte[]> rawHeaderReference =
                    await rawHeader.Task.WaitAsync(
                        TimeSpan.FromSeconds(5));
                GC.Collect(
                    GC.MaxGeneration,
                    GCCollectionMode.Forced,
                    blocking: true,
                    compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(
                    GC.MaxGeneration,
                    GCCollectionMode.Forced,
                    blocking: true,
                    compacting: true);
                Assert.False(
                    rawHeaderReference.TryGetTarget(
                        out _));
                long measuredTextCopy =
                    MeasureStringAllocations(
                        new[] { parsedText });
                long measuredHeaderCopy =
                    MeasureByteArrayAllocation(
                        parsedText.Length);
                Assert.True(
                    budget.PeakReservedBytes >=
                        retained +
                        measuredHeaderCopy +
                        (2 * measuredTextCopy),
                    $"Peak {budget.PeakReservedBytes} did not cover " +
                    $"retained {retained}, raw {measuredHeaderCopy} and " +
                    $"two {measuredTextCopy} byte parse copies.");
            }
            finally
            {
                cancellation.Cancel();
                try
                {
                    await pending;
                }
                catch (OperationCanceledException)
                {
                }
                await pipe.Writer.CompleteAsync();
                await pipe.Reader.CompleteAsync();
            }

            Assert.Equal(
                0,
                budget.ReservedBytes);
        }

        [Theory]
        [InlineData("request-line")]
        [InlineData("content-length")]
        [InlineData("transfer-encoding")]
        [InlineData("host")]
        [InlineData("connect")]
        [InlineData("ipv6-authority")]
        [InlineData("tiny-duplicate-fields")]
        [InlineData("tiny-unique-fields")]
        [InlineData("utf8")]
        public void SynchronousSeparatorHeavyParsing_PeakReservationCoversAllocatedBytes(
            string scenario)
        {
            ProxyRequestReadPolicy policy =
                ProxyRequestReadPolicy.Default;
            (
                byte[] request,
                ProxyRequestReadFailure expectedFailure) =
                    CreateSeparatorHeavyRequest(
                        scenario,
                        policy.MaxHeaderBytes);

            _ = RunSynchronousRead(
                request,
                policy,
                out _,
                out _);
            ProxyRequestReadResult result =
                RunSynchronousRead(
                    request,
                    policy,
                    out long peakReserved,
                    out long allocated);

            Assert.Equal(
                expectedFailure,
                result.Failure);
            if (scenario == "content-length")
            {
                Assert.Equal(
                    new byte[] { (byte)'a' },
                    result.Request.BodyBytes);
            }
            else if (scenario == "ipv6-authority")
            {
                Assert.Equal(
                    "2001:db8::1",
                    result.Request.Host);
                Assert.Equal(
                    443,
                    result.Request.Port);
            }
            else if (scenario ==
                "tiny-duplicate-fields")
            {
                Assert.Equal(
                    "a",
                    result.Request.Headers["X"]);
            }
            else if (scenario ==
                "tiny-unique-fields")
            {
                Assert.Equal(
                    "a",
                    result.Request.Headers["X98"]);
            }
            else if (scenario == "utf8")
            {
                Assert.Equal(
                    "/caf\u00e9",
                    result.Request.Path);
                Assert.Equal(
                    "ex\u00e4mple.test",
                    result.Request.Host);
                Assert.Equal(
                    "\u65e5\u672c\u8a9e",
                    result.Request.Headers[
                        "X-Name"]);
            }
#if DEBUG
            const int UnchargedRuntimeAllowance =
                3 * 1024;
#else
            const int UnchargedRuntimeAllowance =
                2 * 1024;
#endif
            // Cumulative thread allocation is a conservative oracle here,
            // not a general measurement of simultaneously live memory.
            // The content-length case measured 2,064 net uncharged bytes in
            // Debug and 1,056 in Release (runtime overhead minus parse slack);
            // each allowance adds about 1 KiB. This whole-reader oracle catches
            // input-scaling gaps. The zero-allowance authority test and the
            // many-small-fields tests catch constant undercharges.
            Assert.True(
                peakReserved >=
                    allocated -
                    UnchargedRuntimeAllowance,
                $"Peak reservation {peakReserved} did not cover " +
                $"{allocated} allocated bytes minus the " +
                $"{UnchargedRuntimeAllowance} byte runtime allowance.");
        }

        [Fact]
        public void BracketedIpv6Authority_ReservationCoversMeasuredParsingAllocations()
        {
            const string Authority =
                "\u2003[2001:0db8:0000:0000:0000:0000:0000:0001]" +
                ":000443\u2003";
            _ = MeasureAuthorityAllocation(
                Authority,
                out _,
                out _);
            long allocated =
                MeasureAuthorityAllocation(
                    Authority,
                    out string host,
                    out int? port);
            long reserved =
                InitialProxyRequestReader
                    .EstimateAuthorityParsingBytes(
                        Authority.Length);

            Assert.Equal(
                "2001:db8::1",
                host);
            Assert.Equal<int?>(
                443,
                port);
            Assert.True(
                reserved >=
                    allocated,
                $"Authority reservation {reserved} did not cover " +
                $"{allocated} allocated bytes.");
        }

        [Fact]
        public void InitialRequestContentHeaderClassification_MatchesSharedClassification()
        {
            string[] knownHeaders =
            {
                "Accept",
                "Accept-Charset",
                "Accept-Encoding",
                "Accept-Language",
                "Accept-Patch",
                "Accept-Ranges",
                "Access-Control-Allow-Credentials",
                "Access-Control-Allow-Headers",
                "Access-Control-Allow-Methods",
                "Access-Control-Allow-Origin",
                "Access-Control-Expose-Headers",
                "Access-Control-Max-Age",
                "Access-Control-Request-Headers",
                "Access-Control-Request-Method",
                "Age",
                "allow",
                "Authorization",
                "Cache-Control",
                "Connection",
                "Content-Base",
                "Content-Digest",
                "content-disposition",
                "content-encoding",
                "Content-ID",
                "content-language",
                "content-length",
                "content-location",
                "content-md5",
                "content-range",
                "Content-Script-Type",
                "Content-Security-Policy",
                "Content-Security-Policy-Report-Only",
                "Content-Style-Type",
                "Content-Transfer-Encoding",
                "content-type",
                "Content-Version",
                "Cookie",
                "Date",
                "ETag",
                "expires",
                "Expect",
                "Forwarded",
                "From",
                "Host",
                "If-Match",
                "If-Modified-Since",
                "If-None-Match",
                "If-Range",
                "If-Unmodified-Since",
                "last-modified",
                "Keep-Alive",
                "Link",
                "Location",
                "Max-Forwards",
                "Origin",
                "Pragma",
                "Proxy-Authenticate",
                "Proxy-Authorization",
                "Proxy-Connection",
                "Range",
                "Referer",
                "Retry-After",
                "Sec-WebSocket-Accept",
                "Sec-WebSocket-Extensions",
                "Sec-WebSocket-Key",
                "Sec-WebSocket-Protocol",
                "Sec-WebSocket-Version",
                "Server",
                "Set-Cookie",
                "Strict-Transport-Security",
                "TE",
                "Trailer",
                "Transfer-Encoding",
                "Upgrade",
                "User-Agent",
                "Vary",
                "Via",
                "Warning",
                "WWW-Authenticate",
                "X-Custom",
            };
            Assert.Equal(
                knownHeaders.Length,
                knownHeaders
                    .Distinct(
                        StringComparer.OrdinalIgnoreCase)
                    .Count());
            var sharedContentHeaders = 0;
            var initialContentHeaders = 0;
            foreach (string header in
                knownHeaders)
            {
                bool shared =
                    ProxyHeaderUtilities
                        .IsContentHeader(
                            header);
                bool initial =
                    InitialProxyRequestReader
                        .IsContentHeader(
                            header.AsSpan());
                Assert.Equal(
                    shared,
                    initial);
                if (shared)
                {
                    sharedContentHeaders++;
                }
                if (initial)
                {
                    initialContentHeaders++;
                }
                string uppercase =
                    header.ToUpperInvariant();
                Assert.Equal(
                    ProxyHeaderUtilities
                        .IsContentHeader(
                            uppercase),
                    InitialProxyRequestReader
                        .IsContentHeader(
                            uppercase.AsSpan()));
            }
            Assert.Equal(
                11,
                sharedContentHeaders);
            Assert.Equal(
                sharedContentHeaders,
                initialContentHeaders);
        }

        [Theory]
        [InlineData("request-spaces")]
        [InlineData("request-tabs")]
        [InlineData("request-fourth-token")]
        [InlineData("request-version")]
        [InlineData("content-length-whitespace")]
        [InlineData("content-length-leading-comma")]
        [InlineData("content-length-trailing-comma")]
        [InlineData("transfer-encoding-whitespace")]
        [InlineData("transfer-encoding-leading-comma")]
        [InlineData("transfer-encoding-trailing-comma")]
        public void RequestLineAndFramingEdgeCases_PreserveBehavior(
            string scenario)
        {
            (
                byte[] request,
                ProxyRequestReadFailure expectedFailure,
                string expectedPath,
                byte[] expectedBody) =
                    CreateParserEdgeCaseRequest(
                        scenario);
            ProxyRequestReadResult result =
                RunSynchronousRead(
                    request,
                    ProxyRequestReadPolicy.Default,
                    out _,
                    out _);

            Assert.Equal(
                expectedFailure,
                result.Failure);
            if (expectedFailure ==
                ProxyRequestReadFailure.None)
            {
                Assert.Equal(
                    expectedPath,
                    result.Request.Path);
                Assert.Equal(
                    expectedBody,
                    result.Request.BodyBytes);
            }
        }

        [Fact]
        public async Task PendingBodyWithClientHalfClose_ClosesWithoutForwarding()
        {
            await using var proxy =
                await ProxyTestHarness.StartAsync();
            byte[] header =
                Ascii.GetBytes(
                    "POST http://example.test/ HTTP/1.1\r\n" +
                    "Host: example.test\r\n" +
                    "Content-Length: " +
                    (ProxyRequestReadPolicy.Default.MaxBodyBytes + 1) +
                    "\r\n\r\n");
            byte[] pending =
                new byte[64 * 1024];

            Stopwatch elapsed =
                Stopwatch.StartNew();
            string response =
                await SendRawAndHalfCloseAsync(
                    proxy.Port,
                    new[] { header, pending });
            elapsed.Stop();

            Assert.Contains(
                response,
                new[]
                {
                    string.Empty,
                    FixedResponse(413),
                });
            Assert.True(
                elapsed.Elapsed <
                    TimeSpan.FromSeconds(5),
                $"Bounded rejection took {elapsed.Elapsed}.");
            Assert.Empty(
                proxy.CapturedRequests);
            Assert.Equal(
                0,
                proxy.RequestBufferBudget
                    .ReservedBytes);
        }

        [Theory]
        [InlineData(
            "Content-Length: 5\r\nContent-Length: 6\r\n")]
        [InlineData(
            "Content-Length: 5, 6\r\n")]
        [InlineData(
            "Content-Length: 5\r\nTransfer-Encoding: chunked\r\n")]
        [InlineData(
            "Transfer-Encoding: gzip, chunked\r\n")]
        public async Task AmbiguousOrUnsupportedFraming_FailsClosed(
            string framingHeaders)
        {
            await using var origin =
                await LoopbackOrigin.StartAsync();
            await using var proxy =
                await ProxyTestHarness.StartAsync();
            string authority =
                $"127.0.0.1:{origin.Port}";
            byte[] request =
                Ascii.GetBytes(
                    "POST http://" +
                    authority +
                    "/ambiguous HTTP/1.1\r\n" +
                    "Host: " +
                    authority +
                    "\r\n" +
                    framingHeaders +
                    "\r\n");

            string response =
                await SendRawAsync(
                    proxy.Port,
                    new[] { request },
                    TimeSpan.Zero);

            Assert.Equal(
                string.Empty,
                response);
            Assert.Empty(origin.Requests);
            Assert.Empty(
                proxy.CapturedRequests);
            Assert.Equal(
                0,
                proxy.RequestBufferBudget
                    .ReservedBytes);
        }

        [Fact]
        public async Task DuplicateHost_FailsClosed()
        {
            await using var proxy =
                await ProxyTestHarness.StartAsync();
            byte[] request =
                Ascii.GetBytes(
                    "GET http://first.example/item HTTP/1.1\r\n" +
                    "Host: first.example\r\n" +
                    "Host: second.example\r\n\r\n");

            string response =
                await SendRawAsync(
                    proxy.Port,
                    new[] { request },
                    TimeSpan.Zero);

            Assert.Equal(
                string.Empty,
                response);
            Assert.Empty(
                proxy.CapturedRequests);
            Assert.Equal(
                0,
                proxy.RequestBufferBudget
                    .ReservedBytes);
        }

        [Fact]
        public async Task IdenticalDuplicateContentLengths_AreAccepted()
        {
            await using var origin =
                await LoopbackOrigin.StartAsync();
            await using var proxy =
                await ProxyTestHarness.StartAsync();
            string authority =
                $"127.0.0.1:{origin.Port}";
            byte[] request =
                Ascii.GetBytes(
                    "POST http://" +
                    authority +
                    "/duplicate-length HTTP/1.1\r\n" +
                    "Host: " +
                    authority +
                    "\r\nContent-Length: 4\r\n" +
                    "Content-Length: 4\r\n" +
                    "Connection: close\r\n\r\ndata");

            string response =
                await SendRawAsync(
                    proxy.Port,
                    new[] { request },
                    TimeSpan.Zero);

            AssertStatus(response, 200);
            Assert.Equal(
                "data",
                Encoding.ASCII.GetString(
                    Assert.Single(
                        origin.Requests).Body));
        }

        [Fact]
        public async Task IncompleteDeclaredBody_IsNeverForwarded()
        {
            await using var origin =
                await LoopbackOrigin.StartAsync();
            await using var proxy =
                await ProxyTestHarness.StartAsync();
            string authority =
                $"127.0.0.1:{origin.Port}";
            using var client =
                new TcpClient();
            await client.ConnectAsync(
                IPAddress.Loopback,
                proxy.Port);
            using NetworkStream stream =
                client.GetStream();
            await stream.WriteAsync(
                Ascii.GetBytes(
                    "POST http://" +
                    authority +
                    "/incomplete HTTP/1.1\r\n" +
                    "Host: " +
                    authority +
                    "\r\nContent-Length: 5\r\n\r\nabc"));
            client.Client.Shutdown(
                SocketShutdown.Send);

            string response =
                await ReadToEndAsync(
                    stream,
                    TimeSpan.FromSeconds(5));

            Assert.Equal(
                string.Empty,
                response);
            Assert.Empty(origin.Requests);
            Assert.Empty(
                proxy.CapturedRequests);
            Assert.Equal(
                0,
                proxy.RequestBufferBudget
                    .ReservedBytes);
        }

        [Theory]
        [InlineData(
            "11\r\n",
            16,
            8,
            128,
            413)]
        [InlineData(
            "1;abcdefghijklmnopqrstuvwxyz\r\n",
            16,
            8,
            128,
            431)]
        [InlineData(
            "0\r\nX-Trailer: abcdefghijklmnopqrstuvwxyz\r\n\r\n",
            16,
            128,
            16,
            431)]
        public async Task ChunkAndTrailerLimits_AreEnforced(
            string chunkedBody,
            int maxBodyBytes,
            int maxChunkLineBytes,
            int maxTrailerBytes,
            int expectedStatus)
        {
            ProxyRequestReadPolicy policy =
                ProxyRequestReadPolicy.Default with
                {
                    MaxBodyBytes =
                        maxBodyBytes,
                    MaxChunkLineBytes =
                        maxChunkLineBytes,
                    MaxTrailerBytes =
                        maxTrailerBytes,
                    MaxAggregateBufferedBytes =
                        1024 * 1024,
                };
            await using var proxy =
                await ProxyTestHarness.StartAsync(
                    requestPolicy: policy);
            byte[] request =
                Ascii.GetBytes(
                    "POST http://example.test/item HTTP/1.1\r\n" +
                    "Host: example.test\r\n" +
                    "Transfer-Encoding: chunked\r\n\r\n" +
                    chunkedBody);

            string response =
                await SendRawAsync(
                    proxy.Port,
                    new[] { request },
                    TimeSpan.Zero);

            Assert.Equal(
                FixedResponse(
                    expectedStatus),
                response);
            Assert.Empty(
                proxy.CapturedRequests);
        }

        [Fact]
        public async Task ChunkCountLimit_Returns413()
        {
            ProxyRequestReadPolicy policy =
                ProxyRequestReadPolicy.Default with
                {
                    MaxChunkCount = 2,
                    MaxBodyBytes = 16,
                    MaxAggregateBufferedBytes =
                        1024 * 1024,
                };
            await using var proxy =
                await ProxyTestHarness.StartAsync(
                    requestPolicy: policy);
            byte[] request =
                ChunkedRequest(
                    "1\r\na\r\n" +
                    "1\r\nb\r\n" +
                    "1\r\nc\r\n" +
                    "0\r\n\r\n");

            string response =
                await SendRawAsync(
                    proxy.Port,
                    new[] { request },
                    TimeSpan.Zero);

            Assert.Equal(
                FixedResponse(413),
                response);
            Assert.Empty(
                proxy.CapturedRequests);
        }

        [Fact]
        public async Task TrailerFieldLimit_Returns431()
        {
            ProxyRequestReadPolicy policy =
                ProxyRequestReadPolicy.Default with
                {
                    MaxTrailerFields = 1,
                    MaxAggregateBufferedBytes =
                        1024 * 1024,
                };
            await using var proxy =
                await ProxyTestHarness.StartAsync(
                    requestPolicy: policy);
            byte[] request =
                ChunkedRequest(
                    "0\r\n" +
                    "X-First: one\r\n" +
                    "X-Second: two\r\n\r\n");

            string response =
                await SendRawAsync(
                    proxy.Port,
                    new[] { request },
                    TimeSpan.Zero);

            Assert.Equal(
                FixedResponse(431),
                response);
            Assert.Empty(
                proxy.CapturedRequests);
        }

        [Theory]
        [InlineData("Content-Length")]
        [InlineData("Transfer-Encoding")]
        [InlineData("Host")]
        public async Task FramingAndRoutingTrailers_FailClosed(
            string trailerName)
        {
            await using var proxy =
                await ProxyTestHarness.StartAsync();
            byte[] request =
                ChunkedRequest(
                    "0\r\n" +
                    trailerName +
                    ": value\r\n\r\n");

            string response =
                await SendRawAsync(
                    proxy.Port,
                    new[] { request },
                    TimeSpan.Zero);

            Assert.Equal(
                string.Empty,
                response);
            Assert.Empty(
                proxy.CapturedRequests);
        }

        [Fact]
        public async Task TruncatedChunkedBody_IsNeverForwardedAndReleasesBuffers()
        {
            await using var proxy =
                await ProxyTestHarness.StartAsync();
            using var client =
                new TcpClient();
            await client.ConnectAsync(
                IPAddress.Loopback,
                proxy.Port);
            using NetworkStream stream =
                client.GetStream();
            await stream.WriteAsync(
                ChunkedRequest(
                    "5\r\nabc"));
            client.Client.Shutdown(
                SocketShutdown.Send);

            string response =
                await ReadToEndAsync(
                    stream,
                    TimeSpan.FromSeconds(5));

            Assert.Equal(
                string.Empty,
                response);
            Assert.Empty(
                proxy.CapturedRequests);
            Assert.Equal(
                0,
                proxy.RequestBufferBudget
                    .ReservedBytes);
        }

        [Fact]
        public async Task HeaderDeadline_Returns408()
        {
            ProxyRequestReadPolicy policy =
                ProxyRequestReadPolicy.Default with
                {
                    HeaderDeadline =
                        TimeSpan.FromMilliseconds(500),
                };
            await using var proxy =
                await ProxyTestHarness.StartAsync(
                    requestPolicy: policy);
            using var client =
                new TcpClient();
            await client.ConnectAsync(
                IPAddress.Loopback,
                proxy.Port);
            using NetworkStream stream =
                client.GetStream();
            await stream.WriteAsync(
                "GET http://example.test/ HTTP/1.1\r\n"u8
                    .ToArray());

            string response =
                await ReadToEndAsync(
                    stream,
                    TimeSpan.FromSeconds(5));

            Assert.Equal(
                FixedResponse(408),
                response);
        }

        [Fact]
        public async Task BodyInactivityTimeout_Returns408()
        {
            ProxyRequestReadPolicy policy =
                ProxyRequestReadPolicy.Default with
                {
                    BodyInactivityTimeout =
                        TimeSpan.FromMilliseconds(500),
                    BodyTotalTimeout =
                        TimeSpan.FromSeconds(5),
                };
            await using var proxy =
                await ProxyTestHarness.StartAsync(
                    requestPolicy: policy);
            using var client =
                new TcpClient();
            await client.ConnectAsync(
                IPAddress.Loopback,
                proxy.Port);
            using NetworkStream stream =
                client.GetStream();
            await stream.WriteAsync(
                Ascii.GetBytes(
                    "POST http://example.test/ HTTP/1.1\r\n" +
                    "Host: example.test\r\n" +
                    "Content-Length: 5\r\n\r\na"));

            string response =
                await ReadToEndAsync(
                    stream,
                    TimeSpan.FromSeconds(5));

            Assert.Equal(
                FixedResponse(408),
                response);
        }

        [Fact]
        public async Task BodyTotalDeadline_Returns408DespiteProgress()
        {
            ProxyRequestReadPolicy policy =
                ProxyRequestReadPolicy.Default with
                {
                    BodyInactivityTimeout =
                        TimeSpan.FromMilliseconds(700),
                    BodyTotalTimeout =
                        TimeSpan.FromMilliseconds(1_500),
                };
            await using var proxy =
                await ProxyTestHarness.StartAsync(
                    requestPolicy: policy);
            using var client =
                new TcpClient();
            await client.ConnectAsync(
                IPAddress.Loopback,
                proxy.Port);
            using NetworkStream stream =
                client.GetStream();
            await stream.WriteAsync(
                Ascii.GetBytes(
                    "POST http://example.test/ HTTP/1.1\r\n" +
                    "Host: example.test\r\n" +
                    "Content-Length: 10\r\n\r\n"));
            Task<string> response =
                ReadToEndAsync(
                    stream,
                    TimeSpan.FromSeconds(6));

            for (var index = 0;
                index < 5;
                index++)
            {
                try
                {
                    await stream.WriteAsync(
                        new byte[] { (byte)'a' });
                    await stream.FlushAsync();
                }
                catch (IOException)
                {
                    break;
                }
                await Task.Delay(
                    TimeSpan.FromMilliseconds(400));
            }

            Assert.Equal(
                FixedResponse(408),
                await response);
        }

        [Fact]
        public async Task ReaderCancellation_ReleasesAggregateReservation()
        {
            ProxyRequestReadPolicy policy =
                ProxyRequestReadPolicy.Default;
            var budget =
                new ProxyRequestBufferBudget(
                    policy);
            var reader =
                new InitialProxyRequestReader(
                    policy,
                    budget);
            var pipe =
                new System.IO.Pipelines.Pipe();
            using var cancellation =
                new CancellationTokenSource();
            Task<ProxyRequestReadResult> pending =
                reader.ReadAsync(
                    pipe.Reader,
                    cancellation.Token);

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<
                OperationCanceledException>(
                    () => pending);
            Assert.Equal(
                0,
                budget.ReservedBytes);
            await pipe.Writer.CompleteAsync();
            await pipe.Reader.CompleteAsync();
        }

        [Fact]
        public async Task UncancelledFailureResponseIOException_LogsWarnWithoutAStack()
        {
            await using var proxy =
                await ProxyTestHarness.StartAsync();
            var logged =
                new TaskCompletionSource<
                    (LogLevel Level, string Message)>(
                        TaskCreationOptions
                            .RunContinuationsAsynchronously);
            ProxyRequestReadFailure observedFailure =
                ProxyRequestReadFailure.None;
            var writerTokenWasCancelled = true;
            proxy.ConnectionHandler
                .FailureResponseWriterOverride =
                    (_, failure, cancellationToken) =>
                    {
                        observedFailure = failure;
                        writerTokenWasCancelled =
                            cancellationToken
                                .IsCancellationRequested;
                        return Task.FromException(
                            new IOException(
                                "Injected uncancelled I/O failure."));
                    };
            proxy.ConnectionHandler.LogObserver =
                (level, message) =>
                {
                    if (message.Contains(
                            "Injected uncancelled I/O failure.",
                            StringComparison.Ordinal))
                    {
                        logged.TrySetResult(
                            (level, message));
                    }
                };
            using var client =
                new TcpClient();
            await client.ConnectAsync(
                IPAddress.Loopback,
                proxy.Port);
            using NetworkStream stream =
                client.GetStream();
            await stream.WriteAsync(
                Ascii.GetBytes(
                    "POST http://example.test/ HTTP/1.1\r\n" +
                    "Host: example.test\r\n" +
                    "Content-Length: " +
                    (ProxyRequestReadPolicy.Default.MaxBodyBytes + 1) +
                    "\r\n\r\n"));
            await stream.FlushAsync();

            (LogLevel level, string message) =
                await logged.Task.WaitAsync(
                    TimeSpan.FromSeconds(10));

            Assert.Equal(
                ProxyRequestReadFailure.BodyTooLarge,
                observedFailure);
            Assert.False(
                writerTokenWasCancelled);
            Assert.Equal(
                LogLevel.WARN,
                level);
            Assert.DoesNotContain(
                Environment.NewLine,
                message,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "   at ",
                message,
                StringComparison.Ordinal);
            Assert.Equal(
                0,
                proxy.RequestBufferBudget
                    .ReservedBytes);
        }

        [Fact]
        public async Task ClientResetLogsWarnOrCancellationDebugWithoutAStack()
        {
            var originStarted =
                new TaskCompletionSource(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);
            var releaseOrigin =
                new TaskCompletionSource(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);
            await using var origin =
                await LoopbackOrigin.StartAsync(
                    async context =>
                    {
                        originStarted.TrySetResult();
                        await releaseOrigin.Task;
                        await context.Response.Body.WriteAsync(
                            new byte[1024 * 1024]);
                    });
            await using var proxy =
                await ProxyTestHarness.StartAsync();
            var logged =
                new TaskCompletionSource<
                    (LogLevel Level, string Message)>(
                        TaskCreationOptions
                            .RunContinuationsAsynchronously);
            proxy.ConnectionHandler.LogObserver =
                (level, message) =>
                {
                    if (level is
                        LogLevel.WARN or
                        LogLevel.DEBUG)
                    {
                        logged.TrySetResult(
                            (level, message));
                    }
                };
            var client =
                new TcpClient();
            await client.ConnectAsync(
                IPAddress.Loopback,
                proxy.Port);
            NetworkStream stream =
                client.GetStream();
            string authority =
                $"127.0.0.1:{origin.Port}";
            await stream.WriteAsync(
                Ascii.GetBytes(
                    "GET http://" +
                    authority +
                    "/reset HTTP/1.1\r\nHost: " +
                    authority +
                    "\r\n\r\n"));
            await originStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(10));
            Assert.Equal(
                0,
                proxy.RequestBufferBudget
                    .ReservedBytes);

            client.Client.LingerState =
                new LingerOption(
                    true,
                    0);
            client.Dispose();
            releaseOrigin.TrySetResult();
            (LogLevel level, string message) =
                await logged.Task.WaitAsync(
                    TimeSpan.FromSeconds(10));

            Assert.Contains(
                level,
                new[]
                {
                    LogLevel.WARN,
                    LogLevel.DEBUG,
                });
            Assert.DoesNotContain(
                Environment.NewLine,
                message,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "   at ",
                message,
                StringComparison.Ordinal);
        }

        [Fact]
        public async Task ConnectReadAheadBytes_AreRelayedWithoutLoss()
        {
            await using var origin =
                await LoopbackOrigin.StartAsync();
            await using var proxy =
                await ProxyTestHarness.StartAsync(
                    BypassLoopback());
            string authority =
                $"127.0.0.1:{origin.Port}";
            byte[] combined =
                Ascii.GetBytes(
                    "CONNECT " +
                    authority +
                    " HTTP/1.1\r\nHost: " +
                    authority +
                    "\r\n\r\n" +
                    "GET /read-ahead HTTP/1.1\r\nHost: " +
                    authority +
                    "\r\nConnection: close\r\n\r\n");
            using var client =
                new TcpClient();
            await client.ConnectAsync(
                IPAddress.Loopback,
                proxy.Port);
            using NetworkStream stream =
                client.GetStream();

            await stream.WriteAsync(combined);
            await stream.FlushAsync();
            string response =
                await ReadUntilAsync(
                    stream,
                    "origin-ok",
                    TimeSpan.FromSeconds(10));

            AssertStatus(response, 200);
            Assert.Contains(
                "origin-ok",
                response,
                StringComparison.Ordinal);
            Assert.Equal(
                "/read-ahead",
                Assert.Single(
                    origin.Requests).Path);
            Assert.Equal(
                0,
                proxy.RequestBufferBudget
                    .ReservedBytes);
        }

        [Fact]
        public async Task IdleSocketsAndOpenTunnel_DoNotBlockANewRequest()
        {
            ProxyRequestReadPolicy policy =
                ProxyRequestReadPolicy.Default with
                {
                    MaxAggregateBufferedBytes =
                        64 * 1024,
                };
            await using var origin =
                await LoopbackOrigin.StartAsync();
            await using var proxy =
                await ProxyTestHarness.StartAsync(
                    BypassLoopback(),
                    policy);
            var idleClients =
                new List<TcpClient>();
            TcpClient tunnel = null;
            try
            {
                for (var index = 0;
                    index < 32;
                    index++)
                {
                    var idle =
                        new TcpClient();
                    await idle.ConnectAsync(
                        IPAddress.Loopback,
                        proxy.Port);
                    idleClients.Add(idle);
                }

                tunnel = new TcpClient();
                await tunnel.ConnectAsync(
                    IPAddress.Loopback,
                    proxy.Port);
                NetworkStream tunnelStream =
                    tunnel.GetStream();
                string authority =
                    $"127.0.0.1:{origin.Port}";
                await tunnelStream.WriteAsync(
                    Ascii.GetBytes(
                        "CONNECT " +
                        authority +
                        " HTTP/1.1\r\nHost: " +
                        authority +
                        "\r\n\r\n"));
                AssertStatus(
                    await ReadHeaderBytewiseAsync(
                        tunnelStream,
                        TimeSpan.FromSeconds(10)),
                    200);
                Assert.Equal(
                    0,
                    proxy.RequestBufferBudget
                        .ReservedBytes);

                using HttpClient client =
                    proxy.CreateClient();
                Assert.Equal(
                    "origin-ok",
                    await client.GetStringAsync(
                        origin.Url(
                            "/not-blocked")));
            }
            finally
            {
                tunnel?.Dispose();
                foreach (TcpClient idle in
                    idleClients)
                {
                    idle.Dispose();
                }
            }
        }

        [Fact]
        public async Task AggregateBudget_IsSharedAcrossListenersAndRecovers()
        {
            ProxyRequestReadPolicy policy =
                ProxyRequestReadPolicy.Default with
                {
                    MaxBodyBytes =
                        768 * 1024,
                    BodyInactivityTimeout =
                        TimeSpan.FromSeconds(5),
                    MaxAggregateBufferedBytes =
                        1024 * 1024,
                };
            var sharedBudget =
                new ProxyRequestBufferBudget(
                    policy);
            await using var origin =
                await LoopbackOrigin.StartAsync();
            await using var firstProxy =
                await ProxyTestHarness.StartAsync(
                    requestPolicy: policy,
                    requestBufferBudget:
                        sharedBudget);
            await using var secondProxy =
                await ProxyTestHarness.StartAsync(
                    requestPolicy: policy,
                    requestBufferBudget:
                        sharedBudget);
            using var held =
                new TcpClient();
            await held.ConnectAsync(
                IPAddress.Loopback,
                firstProxy.Port);
            NetworkStream heldStream =
                held.GetStream();
            string declaration =
                "POST http://example.test/ HTTP/1.1\r\n" +
                "Host: example.test\r\n" +
                "Content-Length: " +
                policy.MaxBodyBytes +
                "\r\n\r\n";
            await heldStream.WriteAsync(
                Ascii.GetBytes(
                    declaration));
            await WriteBodyBytesAsync(
                heldStream,
                700 * 1024);
            await WaitUntilAsync(
                () =>
                    sharedBudget.ReservedBytes >
                    0,
                TimeSpan.FromSeconds(5));

            string rejected =
                await SendPacedPartialBodyAsync(
                    secondProxy.Port,
                    Ascii.GetBytes(
                        declaration),
                    400 * 1024);

            Assert.Equal(
                FixedResponse(503),
                rejected);
            held.Dispose();
            await WaitUntilAsync(
                () =>
                    sharedBudget.ReservedBytes ==
                    0,
                TimeSpan.FromSeconds(5));
            using HttpClient client =
                secondProxy.CreateClient();
            Assert.Equal(
                "origin-ok",
                await client.GetStringAsync(
                    origin.Url(
                        "/after-budget-release")));
        }

        [Fact]
        public async Task OptionalReaderFallback_UsesTheProcessWideBudget()
        {
            await using var first =
                await ProxyTestHarness.StartAsync(
                    registerRequestReader: false);
            await using var second =
                await ProxyTestHarness.StartAsync(
                    registerRequestReader: false);

            Assert.Same(
                ProxyRequestBufferBudget.Shared,
                first.RequestBufferBudget);
            Assert.Same(
                first.RequestBufferBudget,
                second.RequestBufferBudget);
        }

        [Fact]
        public void ProductionReaderRegistration_UsesTheProcessWideBudget()
        {
            var services =
                new ServiceCollection();
            WebServiceProxy
                .AddRequestReaderServices(
                    services);
            using ServiceProvider provider =
                services.BuildServiceProvider();

            Assert.Same(
                ProxyRequestBufferBudget.Shared,
                provider.GetRequiredService<
                    ProxyRequestBufferBudget>());
            Assert.Same(
                ProxyRequestBufferBudget.Shared,
                provider.GetRequiredService<
                    InitialProxyRequestReader>()
                    .BufferBudget);
        }

        [Fact]
        public async Task WebSocketUpgradeReadAheadFrame_IsRelayed()
        {
            await using var origin =
                await LoopbackWebSocketOrigin
                    .StartAsync();
            await using var proxy =
                await ProxyTestHarness.StartAsync();
            string authority =
                $"127.0.0.1:{origin.Port}";
            byte[] header =
                Ascii.GetBytes(
                    "GET http://" +
                    authority +
                    "/socket HTTP/1.1\r\n" +
                    "Host: " +
                    authority +
                    "\r\nUpgrade: websocket\r\n" +
                    "Connection: Upgrade\r\n" +
                    "Sec-WebSocket-Version: 13\r\n" +
                    "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n\r\n");
            byte[] frame =
                MaskedTextFrame("hello");
            byte[] combined =
                new byte[
                    header.Length +
                    frame.Length];
            header.CopyTo(
                combined,
                0);
            frame.CopyTo(
                combined,
                header.Length);
            using var client =
                new TcpClient();
            await client.ConnectAsync(
                IPAddress.Loopback,
                proxy.Port);
            using NetworkStream stream =
                client.GetStream();

            await stream.WriteAsync(combined);
            await stream.FlushAsync();
            string responseHeader =
                await ReadHeaderBytewiseAsync(
                    stream,
                    TimeSpan.FromSeconds(10));
            string echoed =
                await ReadServerTextFrameAsync(
                    stream,
                    TimeSpan.FromSeconds(10));

            AssertStatus(
                responseHeader,
                101);
            Assert.Equal("hello", echoed);
            Assert.Equal(
                "hello",
                await origin.ReceivedText
                    .Task.WaitAsync(
                        TimeSpan.FromSeconds(10)));
            ClientRequest captured =
                Assert.Single(
                    proxy.CapturedRequests);
            Assert.Equal(
                "websocket",
                captured.Headers["Upgrade"]);
            Assert.Equal(
                0,
                proxy.RequestBufferBudget
                    .ReservedBytes);
        }

        [Fact]
        public async Task InterceptedHttp2Request_StillRoundTrips()
        {
            await using var origin =
                await LoopbackOrigin.StartAsync(
                    useTls: true);
            await using var proxy =
                await ProxyTestHarness.StartAsync();
            using HttpClient client =
                proxy.CreateClient();
            using var request =
                new HttpRequestMessage(
                    HttpMethod.Get,
                    origin.Url("/http2"))
                {
                    Version =
                        HttpVersion.Version20,
                    VersionPolicy =
                        HttpVersionPolicy
                            .RequestVersionExact,
                };

            using HttpResponseMessage response =
                await client.SendAsync(request);

            Assert.Equal(
                "origin-ok",
                await response.Content
                    .ReadAsStringAsync());
            Assert.Equal(
                "HTTP/2",
                Assert.Single(
                    proxy.CapturedRequests)
                    .Version);
        }

        [Fact]
        [Trait("Category", "Scaling")]
        public async Task ConcurrentMaximumDeclarations_StayWithinAggregateBudget()
        {
            if (string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable(
                        "XMAT_PROXY_REQUEST_BENCHMARKS")))
            {
                return;
            }

            ProxyRequestReadPolicy policy =
                ProxyRequestReadPolicy.Default with
                {
                    BodyInactivityTimeout =
                        TimeSpan.FromSeconds(2),
                    BodyTotalTimeout =
                        TimeSpan.FromSeconds(5),
                };
            await using var proxy =
                await ProxyTestHarness.StartAsync(
                    requestPolicy: policy);
            using Process process =
                Process.GetCurrentProcess();
            long privateBefore =
                process.PrivateMemorySize64;
            long privatePeak =
                privateBefore;
            Stopwatch elapsed =
                Stopwatch.StartNew();
            Task<string>[] requests =
                Enumerable.Range(
                        0,
                        6)
                    .Select(_ =>
                        SendBurstPartialBodyAsync(
                            proxy.Port,
                            Ascii.GetBytes(
                                "POST http://example.test/ HTTP/1.1\r\n" +
                                "Host: example.test\r\n" +
                                "Content-Length: " +
                                policy.MaxBodyBytes +
                                "\r\n\r\n"),
                            24 * 1024 * 1024))
                    .ToArray();

            Task<string[]> allResponses =
                Task.WhenAll(requests);
            while (!allResponses.IsCompleted)
            {
                process.Refresh();
                privatePeak =
                    Math.Max(
                        privatePeak,
                        process.PrivateMemorySize64);
                await Task.Delay(
                    TimeSpan.FromMilliseconds(50));
            }
            string[] responses =
                await allResponses;
            elapsed.Stop();
            process.Refresh();
            privatePeak =
                Math.Max(
                    privatePeak,
                    process.PrivateMemorySize64);

            _output.WriteLine(
                $"Peak reserved: {proxy.RequestBufferBudget.PeakReservedBytes:N0} / " +
                $"{policy.MaxAggregateBufferedBytes:N0} bytes; " +
                $"elapsed {elapsed.Elapsed}.");
            _output.WriteLine(
                $"Measured process private bytes before/peak: " +
                $"{privateBefore:N0}/{privatePeak:N0}.");
            Assert.Contains(
                FixedResponse(503),
                responses);
            Assert.All(
                responses,
                response =>
                    Assert.Contains(
                        response,
                        new[]
                        {
                            FixedResponse(503),
                            FixedResponse(408),
                        }));
            Assert.InRange(
                proxy.RequestBufferBudget
                    .PeakReservedBytes,
                1,
                policy.MaxAggregateBufferedBytes);
            Assert.True(
                proxy.RequestBufferBudget
                    .RejectedReservations >
                0);
            Assert.Equal(
                0,
                proxy.RequestBufferBudget
                    .ReservedBytes);
        }

        private static WebServiceProxy BypassLoopback()
        {
            var proxy =
                new WebServiceProxy
                {
                    BypassList =
                        new BypassListModel(),
                };
            Assert.True(
                proxy.BypassList.Add(
                    "127.0.0.1"));
            return proxy;
        }

        private static byte[] ChunkedRequest(
            string body) =>
            Ascii.GetBytes(
                "POST http://example.test/item HTTP/1.1\r\n" +
                "Host: example.test\r\n" +
                "Transfer-Encoding: chunked\r\n\r\n" +
                body);

        private static long MeasureClientRequestAllocation()
        {
            ClientRequest warm =
                AllocateClientRequest();
            GC.KeepAlive(warm);
            long before =
                GC.GetAllocatedBytesForCurrentThread();
            ClientRequest request =
                AllocateClientRequest();
            long allocated =
                GC.GetAllocatedBytesForCurrentThread() -
                before;
            GC.KeepAlive(request);
            return allocated;
        }

        private static long MeasureSingleHeaderBookkeeping()
        {
            HeaderCollection warm =
                AllocateHeaderCollection();
            warm["AA"] = "bb";
            GC.KeepAlive(warm);
            HeaderCollection headers =
                AllocateHeaderCollection();
            long before =
                GC.GetAllocatedBytesForCurrentThread();
            headers["AA"] = "bb";
            long allocated =
                GC.GetAllocatedBytesForCurrentThread() -
                before;
            GC.KeepAlive(headers);
            return allocated;
        }

        private static long MeasureStringAllocations(
            IReadOnlyList<string> values)
        {
            if (values.Count == 0)
            {
                return 0;
            }
            string warm =
                AllocateString(
                    values[0]);
            GC.KeepAlive(warm);
            var copies =
                new string[values.Count];
            long before =
                GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0;
                index < values.Count;
                index++)
            {
                copies[index] =
                    AllocateString(
                        values[index]);
            }
            long allocated =
                GC.GetAllocatedBytesForCurrentThread() -
                before;
            GC.KeepAlive(copies);
            return allocated;
        }

        private static long MeasureByteArrayAllocation(
            int length)
        {
            byte[] warm =
                AllocateByteArray(
                    length);
            GC.KeepAlive(warm);
            long before =
                GC.GetAllocatedBytesForCurrentThread();
            byte[] bytes =
                AllocateByteArray(
                    length);
            long allocated =
                GC.GetAllocatedBytesForCurrentThread() -
                before;
            GC.KeepAlive(bytes);
            return allocated;
        }

        private static long MeasureAuthorityAllocation(
            string authority,
            out string host,
            out int? port)
        {
            long before =
                GC.GetAllocatedBytesForCurrentThread();
            bool parsed =
                HostAuthority.TryParse(
                    authority,
                    out host,
                    out port);
            long allocated =
                GC.GetAllocatedBytesForCurrentThread() -
                before;
            Assert.True(parsed);
            GC.KeepAlive(host);
            return allocated;
        }

        private static (
            byte[] Request,
            ProxyRequestReadFailure ExpectedFailure,
            string ExpectedPath,
            byte[] ExpectedBody)
            CreateParserEdgeCaseRequest(
            string scenario)
        {
            string request;
            ProxyRequestReadFailure failure =
                ProxyRequestReadFailure.None;
            string path = "/edge";
            byte[] body =
                Array.Empty<byte>();
            switch (scenario)
            {
                case "request-spaces":
                    request =
                        "   GET   /edge   HTTP/1.1   \r\n" +
                        "Host: example.test\r\n\r\n";
                    break;
                case "request-tabs":
                    request =
                        "GET\t/edge\tHTTP/1.1\r\n" +
                        "Host: example.test\r\n\r\n";
                    failure =
                        ProxyRequestReadFailure.Malformed;
                    break;
                case "request-fourth-token":
                    request =
                        "GET /edge HTTP/1.1 extra\r\n" +
                        "Host: example.test\r\n\r\n";
                    failure =
                        ProxyRequestReadFailure.Malformed;
                    break;
                case "request-version":
                    request =
                        "GET /edge HTTP/2\r\n" +
                        "Host: example.test\r\n\r\n";
                    failure =
                        ProxyRequestReadFailure.Malformed;
                    break;
                case "content-length-whitespace":
                    request =
                        "POST /edge HTTP/1.1\r\n" +
                        "Host: example.test\r\n" +
                        "Content-Length:\t 1 , \t1 \t\r\n\r\n" +
                        "a";
                    body =
                        new byte[] { (byte)'a' };
                    break;
                case "content-length-leading-comma":
                    request =
                        "POST /edge HTTP/1.1\r\n" +
                        "Host: example.test\r\n" +
                        "Content-Length: ,1\r\n\r\n";
                    failure =
                        ProxyRequestReadFailure.Malformed;
                    break;
                case "content-length-trailing-comma":
                    request =
                        "POST /edge HTTP/1.1\r\n" +
                        "Host: example.test\r\n" +
                        "Content-Length: 1,\r\n\r\n";
                    failure =
                        ProxyRequestReadFailure.Malformed;
                    break;
                case "transfer-encoding-whitespace":
                    request =
                        "POST /edge HTTP/1.1\r\n" +
                        "Host: example.test\r\n" +
                        "Transfer-Encoding:\t chunked \t\r\n\r\n" +
                        "1\r\na\r\n0\r\n\r\n";
                    body =
                        new byte[] { (byte)'a' };
                    break;
                case "transfer-encoding-leading-comma":
                    request =
                        "POST /edge HTTP/1.1\r\n" +
                        "Host: example.test\r\n" +
                        "Transfer-Encoding: ,chunked\r\n\r\n";
                    failure =
                        ProxyRequestReadFailure.Malformed;
                    break;
                case "transfer-encoding-trailing-comma":
                    request =
                        "POST /edge HTTP/1.1\r\n" +
                        "Host: example.test\r\n" +
                        "Transfer-Encoding: chunked,\r\n\r\n";
                    failure =
                        ProxyRequestReadFailure.Malformed;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(scenario));
            }
            return (
                Ascii.GetBytes(
                    request),
                failure,
                path,
                body);
        }

        private static (
            byte[] Request,
            ProxyRequestReadFailure ExpectedFailure)
            CreateSeparatorHeavyRequest(
            string scenario,
            int maxHeaderBytes)
        {
            string text;
            string body = string.Empty;
            Encoding encoding =
                Ascii;
            ProxyRequestReadFailure expectedFailure =
                ProxyRequestReadFailure.None;
            switch (scenario)
            {
                case "request-line":
                {
                    int length =
                        maxHeaderBytes -
                        4;
                    var line =
                        new StringBuilder(
                            length);
                    while (line.Length <
                        length)
                    {
                        line.Append(
                            line.Length + 2 <=
                                length
                                ? "G "
                                : "G");
                    }
                    text =
                        line.ToString() +
                        "\r\n\r\n";
                    expectedFailure =
                        ProxyRequestReadFailure.Malformed;
                    break;
                }
                case "content-length":
                {
                    const string Prefix =
                        "POST / HTTP/1.1\r\n" +
                        "Host: example.test\r\n" +
                        "Content-Length: ";
                    const string Suffix =
                        "\r\n\r\n";
                    int available =
                        maxHeaderBytes -
                        Ascii.GetByteCount(
                            Prefix +
                            Suffix);
                    int tokens =
                        (available + 1) /
                        2;
                    text =
                        Prefix +
                        string.Join(
                            ",",
                            Enumerable.Repeat(
                                "1",
                                tokens)) +
                        Suffix;
                    body = "a";
                    break;
                }
                case "transfer-encoding":
                {
                    const string Prefix =
                        "GET / HTTP/1.1\r\n" +
                        "Host: example.test\r\n" +
                        "Transfer-Encoding: ";
                    const string Suffix =
                        "\r\n\r\n";
                    int available =
                        maxHeaderBytes -
                        Ascii.GetByteCount(
                            Prefix +
                            Suffix);
                    int tokens =
                        (available + 1) /
                        8;
                    text =
                        Prefix +
                        string.Join(
                            ",",
                            Enumerable.Repeat(
                                "chunked",
                                tokens)) +
                        Suffix;
                    expectedFailure =
                        ProxyRequestReadFailure.Malformed;
                    break;
                }
                case "host":
                {
                    const string Prefix =
                        "GET / HTTP/1.1\r\nHost: ";
                    const string Suffix =
                        ":80\r\n\r\n";
                    text =
                        Prefix +
                        new string(
                            'h',
                            maxHeaderBytes -
                            Ascii.GetByteCount(
                                Prefix +
                                Suffix)) +
                        Suffix;
                    break;
                }
                case "connect":
                {
                    const string Prefix =
                        "CONNECT ";
                    const string Suffix =
                        ":443 HTTP/1.1\r\n\r\n";
                    text =
                        Prefix +
                        new string(
                            'h',
                            maxHeaderBytes -
                            Ascii.GetByteCount(
                                Prefix +
                                Suffix)) +
                        Suffix;
                    break;
                }
                case "ipv6-authority":
                    text =
                        "GET / HTTP/1.1\r\nHost: " +
                        "\u2003[2001:0db8:0000:0000:0000:0000:0000:0001]" +
                        ":000443\u2003\r\n\r\n";
                    encoding =
                        Encoding.UTF8;
                    break;
                case "tiny-duplicate-fields":
                {
                    var headers =
                        new StringBuilder(
                            "GET / HTTP/1.1\r\n" +
                            "Host: example.test\r\n");
                    for (var index = 1;
                        index <
                            ProxyRequestReadPolicy
                                .Default
                                .MaxHeaderFields;
                        index++)
                    {
                        headers.Append(
                            "X: a\r\n");
                    }
                    headers.Append("\r\n");
                    text =
                        headers.ToString();
                    break;
                }
                case "tiny-unique-fields":
                {
                    var headers =
                        new StringBuilder(
                            "GET / HTTP/1.1\r\n" +
                            "Host: example.test\r\n");
                    for (var index = 0;
                        index < 99;
                        index++)
                    {
                        headers.Append('X');
                        headers.Append(index);
                        headers.Append(
                            ": a\r\n");
                    }
                    headers.Append("\r\n");
                    text =
                        headers.ToString();
                    break;
                }
                case "utf8":
                    text =
                        "GET /caf\u00e9 HTTP/1.1\r\n" +
                        "Host: ex\u00e4mple.test\r\n" +
                        "X-Name: \u65e5\u672c\u8a9e\r\n\r\n";
                    encoding =
                        Encoding.UTF8;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(scenario));
            }

            byte[] request =
                encoding.GetBytes(
                    text +
                    body);
            int bodyBytes =
                encoding.GetByteCount(
                    body);
            if (scenario is not
                "utf8" and not
                "ipv6-authority" and not
                "tiny-duplicate-fields" and not
                "tiny-unique-fields")
            {
                Assert.InRange(
                    request.Length -
                    bodyBytes,
                    maxHeaderBytes - 8,
                    maxHeaderBytes);
            }
            return (
                request,
                expectedFailure);
        }

        private static ProxyRequestReadResult
            RunSynchronousRead(
            byte[] request,
            ProxyRequestReadPolicy policy,
            out long peakReserved,
            out long allocated)
        {
            var pipe =
                new System.IO.Pipelines.Pipe();
            pipe.Writer.WriteAsync(
                    request)
                .GetAwaiter()
                .GetResult();
            pipe.Writer.Complete();
            var budget =
                new ProxyRequestBufferBudget(
                    policy);
            var reader =
                new InitialProxyRequestReader(
                    policy,
                    budget);

            long before =
                GC.GetAllocatedBytesForCurrentThread();
            Task<ProxyRequestReadResult> pending =
                reader.ReadAsync(
                    pipe.Reader,
                    CancellationToken.None);
            allocated =
                GC.GetAllocatedBytesForCurrentThread() -
                before;

            Assert.True(
                pending.IsCompletedSuccessfully,
                "The prefilled pipe did not parse synchronously.");
            ProxyRequestReadResult result =
                pending.GetAwaiter()
                    .GetResult();
            peakReserved =
                budget.PeakReservedBytes;
            Assert.Equal(
                0,
                budget.ReservedBytes);
            pipe.Reader.Complete();
            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ClientRequest AllocateClientRequest() =>
            new();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static HeaderCollection AllocateHeaderCollection() =>
            new();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string AllocateString(
            string value) =>
            new string(
                value.AsSpan());

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static byte[] AllocateByteArray(
            int length) =>
            new byte[length];

        private static byte[] MaximumBodyDeclaration(
            string authority,
            int headerBytes,
            int bodyBytes,
            out int fillCharacters)
        {
            string prefix =
                "POST http://" +
                authority +
                "/held HTTP/1.1\r\nHost: " +
                authority +
                "\r\nContent-Length: " +
                bodyBytes +
                "\r\nX-Fill: ";
            const string Suffix =
                "\r\n\r\n";
            fillCharacters =
                headerBytes -
                Ascii.GetByteCount(
                    prefix +
                    Suffix);
            Assert.True(
                fillCharacters >= 0);
            byte[] declaration =
                Ascii.GetBytes(
                    prefix +
                    new string(
                        'a',
                        fillCharacters) +
                    Suffix);
            Assert.Equal(
                headerBytes,
                declaration.Length);
            return declaration;
        }

        private static string ConnectRequestWithBytes(
            int targetPort,
            int totalBytes)
        {
            string authority =
                $"127.0.0.1:{targetPort}";
            string prefix =
                "CONNECT " +
                authority +
                " HTTP/1.1\r\nHost: " +
                authority +
                "\r\nX-Fill: ";
            const string Suffix =
                "\r\n\r\n";
            int fill =
                totalBytes -
                Ascii.GetByteCount(
                    prefix +
                    Suffix);
            Assert.True(fill >= 0);
            string request =
                prefix +
                new string(
                    'a',
                    fill) +
                Suffix;
            Assert.Equal(
                totalBytes,
                Ascii.GetByteCount(
                    request));
            return request;
        }

        private static string ConnectRequestWithFields(
            int targetPort,
            int fieldCount)
        {
            string authority =
                $"127.0.0.1:{targetPort}";
            var request =
                new StringBuilder();
            request.Append(
                "CONNECT " +
                authority +
                " HTTP/1.1\r\nHost: " +
                authority +
                "\r\n");
            for (var index = 1;
                index < fieldCount;
                index++)
            {
                request.Append(
                    "X-");
                request.Append(
                    index);
                request.Append(
                    ": value\r\n");
            }
            request.Append("\r\n");
            return request.ToString();
        }

        private static async Task<string> SendRawAsync(
            int port,
            IEnumerable<byte[]> fragments,
            TimeSpan delay)
        {
            using var client =
                new TcpClient();
            await client.ConnectAsync(
                IPAddress.Loopback,
                port);
            using NetworkStream stream =
                client.GetStream();
            foreach (byte[] fragment in fragments)
            {
                await stream.WriteAsync(fragment);
                await stream.FlushAsync();
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay);
                }
            }
            return await ReadToEndAsync(
                stream,
                TimeSpan.FromSeconds(15));
        }

        private static async Task<string>
            SendRawAndHalfCloseAsync(
                int port,
                IEnumerable<byte[]> fragments)
        {
            using var client =
                new TcpClient();
            await client.ConnectAsync(
                IPAddress.Loopback,
                port);
            using NetworkStream stream =
                client.GetStream();
            foreach (byte[] fragment in fragments)
            {
                await stream.WriteAsync(fragment);
            }
            await stream.FlushAsync();
            client.Client.Shutdown(
                SocketShutdown.Send);
            return await ReadToEndAsync(
                stream,
                TimeSpan.FromSeconds(15));
        }

        private static async Task<string>
            SendPacedPartialBodyAsync(
                int port,
                byte[] header,
                int bodyBytes)
        {
            using var client =
                new TcpClient();
            await client.ConnectAsync(
                IPAddress.Loopback,
                port);
            using NetworkStream stream =
                client.GetStream();
            Task<string> response =
                ReadToEndAllowResetAsync(
                    stream,
                    TimeSpan.FromSeconds(15));
            await stream.WriteAsync(header);
            byte[] block =
                new byte[4 * 1024];
            Array.Fill(
                block,
                (byte)'b');
            var written = 0;
            while (written < bodyBytes &&
                !response.IsCompleted)
            {
                int next =
                    Math.Min(
                        block.Length,
                        bodyBytes - written);
                try
                {
                    await stream.WriteAsync(
                        block.AsMemory(
                            0,
                            next));
                }
                catch (IOException)
                {
                    break;
                }
                written += next;
                _ = await Task.WhenAny(
                    response,
                    Task.Delay(
                        TimeSpan.FromMilliseconds(2)));
            }
            return await response;
        }

        private static async Task<string>
            SendBurstPartialBodyAsync(
                int port,
                byte[] header,
                int bodyBytes)
        {
            using var client =
                new TcpClient();
            await client.ConnectAsync(
                IPAddress.Loopback,
                port);
            using NetworkStream stream =
                client.GetStream();
            Task<string> response =
                ReadToEndAllowResetAsync(
                    stream,
                    TimeSpan.FromSeconds(15));
            await stream.WriteAsync(header);
            byte[] block =
                new byte[64 * 1024];
            Array.Fill(
                block,
                (byte)'b');
            var written = 0;
            while (written < bodyBytes &&
                !response.IsCompleted)
            {
                int next =
                    Math.Min(
                        block.Length,
                        bodyBytes - written);
                try
                {
                    await stream.WriteAsync(
                        block.AsMemory(
                            0,
                            next));
                }
                catch (IOException)
                {
                    break;
                }
                written += next;
            }
            return await response;
        }

        private static async Task WriteBodyBytesAsync(
            Stream stream,
            int count)
        {
            byte[] block =
                new byte[
                    Math.Min(
                        64 * 1024,
                        count)];
            Array.Fill(
                block,
                (byte)'b');
            var written = 0;
            while (written < count)
            {
                int next =
                    Math.Min(
                        block.Length,
                        count - written);
                await stream.WriteAsync(
                    block.AsMemory(
                        0,
                        next));
                written += next;
            }
            await stream.FlushAsync();
        }

        private static async Task<string>
            SendAndReadHeadersAsync(
                int port,
                byte[] request)
        {
            using var client =
                new TcpClient();
            await client.ConnectAsync(
                IPAddress.Loopback,
                port);
            using NetworkStream stream =
                client.GetStream();
            await stream.WriteAsync(request);
            await stream.FlushAsync();
            return await ReadHeaderBytewiseAsync(
                stream,
                TimeSpan.FromSeconds(10));
        }

        private static async Task<string>
            ReadHeaderBytewiseAsync(
                Stream stream,
                TimeSpan timeout)
        {
            using var cancellation =
                new CancellationTokenSource(
                    timeout);
            using var bytes =
                new MemoryStream();
            var matched = 0;
            byte[] terminator =
                "\r\n\r\n"u8.ToArray();
            byte[] one =
                new byte[1];
            while (matched <
                terminator.Length)
            {
                int count =
                    await stream.ReadAsync(
                        one,
                        cancellation.Token);
                if (count == 0)
                {
                    break;
                }
                bytes.WriteByte(one[0]);
                matched =
                    one[0] ==
                    terminator[matched]
                        ? matched + 1
                        : one[0] ==
                            terminator[0]
                            ? 1
                            : 0;
            }
            return Ascii.GetString(
                bytes.ToArray());
        }

        private static async Task<string>
            ReadToEndAsync(
                Stream stream,
                TimeSpan timeout)
        {
            using var cancellation =
                new CancellationTokenSource(
                    timeout);
            using var output =
                new MemoryStream();
            byte[] buffer =
                new byte[4096];
            while (true)
            {
                int count =
                    await stream.ReadAsync(
                        buffer,
                        cancellation.Token);
                if (count == 0)
                {
                    return Ascii.GetString(
                        output.ToArray());
                }
                output.Write(
                    buffer,
                    0,
                    count);
            }
        }

        private static async Task<string>
            ReadToEndAllowResetAsync(
                Stream stream,
                TimeSpan timeout)
        {
            using var cancellation =
                new CancellationTokenSource(
                    timeout);
            using var output =
                new MemoryStream();
            byte[] buffer =
                new byte[4096];
            try
            {
                while (true)
                {
                    int count =
                        await stream.ReadAsync(
                            buffer,
                            cancellation.Token);
                    if (count == 0)
                    {
                        break;
                    }
                    output.Write(
                        buffer,
                        0,
                        count);
                }
            }
            catch (IOException)
            {
                // A policy response can race the peer's final write and TCP reset.
            }
            return Ascii.GetString(
                output.ToArray());
        }

        private static async Task<string> ReadUntilAsync(
            Stream stream,
            string marker,
            TimeSpan timeout)
        {
            using var cancellation =
                new CancellationTokenSource(
                    timeout);
            using var output =
                new MemoryStream();
            byte[] buffer =
                new byte[4096];
            while (true)
            {
                int count =
                    await stream.ReadAsync(
                        buffer,
                        cancellation.Token);
                if (count == 0)
                {
                    break;
                }
                output.Write(
                    buffer,
                    0,
                    count);
                string text =
                    Ascii.GetString(
                        output.GetBuffer(),
                        0,
                        checked((int)output.Length));
                if (text.Contains(
                        marker,
                        StringComparison.Ordinal))
                {
                    return text;
                }
            }
            return Ascii.GetString(
                output.ToArray());
        }

        private static void AssertStatus(
            string response,
            int statusCode)
        {
            Assert.Contains(
                " " +
                statusCode +
                " ",
                response,
                StringComparison.Ordinal);
        }

        private static string FixedResponse(
            int statusCode) =>
            statusCode switch
            {
                408 =>
                    "HTTP/1.1 408 Request Timeout\r\n" +
                    "Connection: close\r\n" +
                    "Content-Length: 0\r\n\r\n",
                413 =>
                    "HTTP/1.1 413 Content Too Large\r\n" +
                    "Connection: close\r\n" +
                    "Content-Length: 0\r\n\r\n",
                431 =>
                    "HTTP/1.1 431 Request Header Fields Too Large\r\n" +
                    "Connection: close\r\n" +
                    "Content-Length: 0\r\n\r\n",
                503 =>
                    "HTTP/1.1 503 Service Unavailable\r\n" +
                    "Connection: close\r\n" +
                    "Content-Length: 0\r\n\r\n",
                _ => throw new ArgumentOutOfRangeException(
                    nameof(statusCode)),
            };

        private static byte[] MaskedTextFrame(
            string text)
        {
            byte[] payload =
                Encoding.UTF8.GetBytes(text);
            Assert.InRange(
                payload.Length,
                0,
                125);
            byte[] mask =
            {
                1,
                2,
                3,
                4,
            };
            byte[] frame =
                new byte[
                    2 +
                    mask.Length +
                    payload.Length];
            frame[0] = 0x81;
            frame[1] =
                (byte)(0x80 |
                payload.Length);
            mask.CopyTo(
                frame,
                2);
            for (var index = 0;
                index < payload.Length;
                index++)
            {
                frame[
                    2 +
                    mask.Length +
                    index] =
                    (byte)(payload[index] ^
                    mask[index %
                        mask.Length]);
            }
            return frame;
        }

        private static async Task<string>
            ReadServerTextFrameAsync(
                Stream stream,
                TimeSpan timeout)
        {
            using var cancellation =
                new CancellationTokenSource(
                    timeout);
            byte[] header =
                new byte[2];
            await ReadExactlyAsync(
                stream,
                header,
                cancellation.Token);
            Assert.Equal(0x81, header[0]);
            Assert.Equal(0, header[1] & 0x80);
            int length =
                header[1] & 0x7f;
            byte[] payload =
                new byte[length];
            await ReadExactlyAsync(
                stream,
                payload,
                cancellation.Token);
            return Encoding.UTF8.GetString(
                payload);
        }

        private static async Task ReadExactlyAsync(
            Stream stream,
            byte[] destination,
            CancellationToken cancellationToken)
        {
            var offset = 0;
            while (offset <
                destination.Length)
            {
                int count =
                    await stream.ReadAsync(
                        destination.AsMemory(
                            offset),
                        cancellationToken);
                if (count == 0)
                {
                    throw new EndOfStreamException();
                }
                offset += count;
            }
        }

        private static async Task WaitUntilAsync(
            Func<bool> condition,
            TimeSpan timeout)
        {
            Stopwatch elapsed =
                Stopwatch.StartNew();
            while (!condition())
            {
                if (elapsed.Elapsed >= timeout)
                {
                    Assert.Fail(
                        "The expected proxy state was not reached.");
                }
                await Task.Delay(
                    TimeSpan.FromMilliseconds(25));
            }
        }

        private static async Task<long>
            WaitForStableReservationIncreaseAsync(
            ProxyRequestBufferBudget budget,
            long previous,
            TimeSpan timeout)
        {
            Stopwatch elapsed =
                Stopwatch.StartNew();
            long last = -1;
            var stableSamples = 0;
            while (true)
            {
                long current =
                    budget.ReservedBytes;
                if (current > previous &&
                    current == last)
                {
                    stableSamples++;
                    if (stableSamples >= 8)
                    {
                        return current;
                    }
                }
                else
                {
                    stableSamples = 0;
                }
                if (elapsed.Elapsed >= timeout)
                {
                    Assert.Fail(
                        "The request-buffer reservation did not settle.");
                }
                last = current;
                await Task.Delay(
                    TimeSpan.FromMilliseconds(25));
            }
        }

        private sealed class LoopbackWebSocketOrigin :
            IAsyncDisposable
        {
            private readonly IHost _host;

            private LoopbackWebSocketOrigin(
                IHost host,
                int port,
                TaskCompletionSource<string>
                    receivedText)
            {
                _host = host;
                Port = port;
                ReceivedText = receivedText;
            }

            internal int Port { get; }

            internal TaskCompletionSource<string>
                ReceivedText { get; }

            internal static async Task<
                LoopbackWebSocketOrigin>
                StartAsync()
            {
                var received =
                    new TaskCompletionSource<string>(
                        TaskCreationOptions
                            .RunContinuationsAsynchronously);
                IHost host =
                    Host.CreateDefaultBuilder()
                        .ConfigureWebHostDefaults(
                            web =>
                            {
                                web.ConfigureKestrel(
                                    options =>
                                        options.Listen(
                                            IPAddress.Loopback,
                                            0));
                                web.Configure(
                                    app =>
                                    {
                                        app.UseWebSockets();
                                        app.Run(
                                            async context =>
                                            {
                                                if (!context
                                                    .WebSockets
                                                    .IsWebSocketRequest)
                                                {
                                                    context
                                                        .Response
                                                        .StatusCode =
                                                        StatusCodes
                                                            .Status400BadRequest;
                                                    return;
                                                }

                                                using WebSocket socket =
                                                    await context
                                                        .WebSockets
                                                        .AcceptWebSocketAsync();
                                                byte[] buffer =
                                                    new byte[128];
                                                WebSocketReceiveResult result =
                                                    await socket
                                                        .ReceiveAsync(
                                                            buffer,
                                                            CancellationToken.None);
                                                string text =
                                                    Encoding.UTF8
                                                        .GetString(
                                                            buffer,
                                                            0,
                                                            result.Count);
                                                received.TrySetResult(
                                                    text);
                                                await socket.SendAsync(
                                                    new ArraySegment<byte>(
                                                        buffer,
                                                        0,
                                                        result.Count),
                                                    WebSocketMessageType.Text,
                                                    true,
                                                    CancellationToken.None);
                                            });
                                    });
                            })
                        .Build();
                await host.StartAsync();
                IServerAddressesFeature addresses =
                    host.Services
                        .GetRequiredService<IServer>()
                        .Features
                        .Get<
                            IServerAddressesFeature>();
                int port =
                    new Uri(
                        addresses.Addresses
                            .Single())
                    .Port;
                return new LoopbackWebSocketOrigin(
                    host,
                    port,
                    received);
            }

            public async ValueTask DisposeAsync()
            {
                await _host.StopAsync();
                _host.Dispose();
            }
        }
    }
}
