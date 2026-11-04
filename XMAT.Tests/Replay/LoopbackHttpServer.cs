// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace XMAT.Tests.Replay
{
    internal sealed record LoopbackRequest(
        string Method, string Target, IReadOnlyList<KeyValuePair<string, string>> Headers, byte[] Body)
    {
        internal string Header(string name) =>
            Headers.Where(header => header.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                .Select(header => header.Value)
                .FirstOrDefault();
    }

    // Headers holds complete header lines ("Name: value\r\n"). Hang never answers; Chunked sends the body as one chunk;
    // NoLength sends no Content-Length of its own, for responses whose Headers declare one without a body (HEAD, 304).
    internal sealed record LoopbackResponse(
        int Status = 200,
        string Reason = "OK",
        string Headers = "",
        byte[] Body = null,
        bool Chunked = false,
        bool Hang = false,
        bool NoLength = false)
    {
        internal static LoopbackResponse Ok(string body = "") => new(Body: Encoding.UTF8.GetBytes(body));

        internal static LoopbackResponse Redirect(int status, string location) =>
            new(status, "Redirect", $"Location: {location}\r\n");
    }

    // A minimal HTTP/1.1 origin server on 127.0.0.1 for replay tests: one request per connection, answered by the
    // callback. It listens on the loopback address only, so nothing outside the machine can reach it.
    internal sealed class LoopbackHttpServer : IAsyncDisposable
    {
        private const int MaxHeadBytes = 64 * 1024;

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<LoopbackRequest, Task<LoopbackResponse>> _respond;
        private readonly ConcurrentQueue<LoopbackRequest> _requests = new();
        private readonly ConcurrentBag<Task> _serving = new();
        private readonly Task _accepting;
        private int _connections;

        internal LoopbackHttpServer(Func<LoopbackRequest, LoopbackResponse> respond)
            : this(request => Task.FromResult(respond(request)))
        {
        }

        internal LoopbackHttpServer(Func<LoopbackRequest, Task<LoopbackResponse>> respond)
        {
            _respond = respond;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _accepting = AcceptAsync();
        }

        internal int Port { get; }

        internal string Origin => $"http://127.0.0.1:{Port}";

        // In arrival order.
        internal IReadOnlyList<LoopbackRequest> Requests => _requests.ToArray();

        // Every connection accepted, including those that never sent a complete request.
        internal int Connections => Volatile.Read(ref _connections);

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _accepting;
            await Task.WhenAll(_serving);
            _stop.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                Interlocked.Increment(ref _connections);
                _serving.Add(ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    NetworkStream stream = client.GetStream();
                    LoopbackRequest request = await ReadRequestAsync(stream, _stop.Token);
                    if (request == null)
                    {
                        return;
                    }

                    _requests.Enqueue(request);
                    LoopbackResponse response = await _respond(request);
                    if (response.Hang)
                    {
                        await Task.Delay(Timeout.Infinite, _stop.Token);
                    }

                    await stream.WriteAsync(Serialize(response), _stop.Token);
                }
                catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
                {
                    // The client went away, the server is stopping or the callback failed: the connection just closes.
                }
            }
        }

        private static async Task<LoopbackRequest> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
        {
            var buffer = new MemoryStream();
            var chunk = new byte[4096];
            int headEnd;
            while ((headEnd = IndexOfHeadEnd(buffer)) < 0)
            {
                int read = await stream.ReadAsync(chunk, cancellationToken);
                if (read == 0 || buffer.Length + read > MaxHeadBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            byte[] bytes = buffer.ToArray();
            string[] lines = Encoding.Latin1.GetString(bytes, 0, headEnd).Split("\r\n");
            string[] requestLine = lines[0].Split(' ');
            var headers = new List<KeyValuePair<string, string>>();
            foreach (string line in lines.Skip(1))
            {
                int separator = line.IndexOf(':');
                headers.Add(new(line[..separator], line[(separator + 1)..].Trim()));
            }

            int length = headers
                .Where(header => header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                .Select(header => int.Parse(header.Value, CultureInfo.InvariantCulture))
                .FirstOrDefault();
            var body = new MemoryStream();
            body.Write(bytes, headEnd + 4, bytes.Length - headEnd - 4);
            while (body.Length < length)
            {
                int read = await stream.ReadAsync(chunk, cancellationToken);
                if (read == 0)
                {
                    return null;
                }

                body.Write(chunk, 0, read);
            }

            return new LoopbackRequest(requestLine[0], requestLine[1], headers, body.ToArray());
        }

        private static int IndexOfHeadEnd(MemoryStream buffer) =>
            buffer.GetBuffer().AsSpan(0, (int)buffer.Length).IndexOf("\r\n\r\n"u8);

        private static byte[] Serialize(LoopbackResponse response)
        {
            byte[] body = response.Body ?? Array.Empty<byte>();
            var head = new StringBuilder($"HTTP/1.1 {response.Status} {response.Reason}\r\n").Append(response.Headers);
            if (response.Chunked)
            {
                head.Append("Transfer-Encoding: chunked\r\n");
            }
            else if (!response.NoLength)
            {
                head.Append($"Content-Length: {body.Length}\r\n");
            }
            head.Append("Connection: close\r\n\r\n");
            var message = new MemoryStream();
            message.Write(Encoding.Latin1.GetBytes(head.ToString()));
            if (response.Chunked)
            {
                if (body.Length > 0)
                {
                    message.Write(Encoding.ASCII.GetBytes($"{body.Length:x}\r\n"));
                    message.Write(body);
                    message.Write("\r\n"u8);
                }

                message.Write("0\r\n\r\n"u8);
            }
            else
            {
                message.Write(body);
            }

            return message.ToArray();
        }
    }
}
