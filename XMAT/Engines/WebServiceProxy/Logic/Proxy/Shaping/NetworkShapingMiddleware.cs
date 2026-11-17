// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;

namespace XMAT.WebServiceCapture.Proxy
{
    internal static class NetworkShapingMiddleware
    {
        private const int ChunkSize = 16 * 1024;

        internal static IConnectionBuilder UseNetworkShaping(
            this IConnectionBuilder builder,
            NetworkShapingController controller)
        {
            ArgumentNullException.ThrowIfNull(controller);
            return builder.Use(next => context => RunAsync(context, next, controller));
        }

        private static async Task RunAsync(
            ConnectionContext context,
            ConnectionDelegate next,
            NetworkShapingController controller)
        {
            if (!controller.Register(context))
            {
                context.Abort(new ConnectionAbortedException("New proxy connections rejected by shaping."));
                return;
            }

            IDuplexPipe original = context.Transport;
            var input = new Pipe();
            var output = new Pipe();
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(context.ConnectionClosed);
            using var inputStopping = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
            Task upload = PumpAsync(
                original.Input, input.Writer, controller, controller.Upload, inputStopping.Token);
            Task download = PumpAsync(
                output.Reader, original.Output, controller, controller.Download, stopping.Token);
            context.Transport = new ShapedPipe(input.Reader, output.Writer);
            try
            {
                await next(context).ConfigureAwait(false);
                await inputStopping.CancelAsync().ConfigureAwait(false);
                await input.Reader.CompleteAsync().ConfigureAwait(false);
                await output.Writer.CompleteAsync().ConfigureAwait(false);
                await download.ConfigureAwait(false);
            }
            catch (Exception ex) when (
                (ex is OperationCanceledException or IOException) && stopping.IsCancellationRequested)
            {
                Trace.TraceInformation("The shaped connection stopped during cancellation.");
            }
            finally
            {
                await stopping.CancelAsync().ConfigureAwait(false);
                try
                {
                    await Task.WhenAll(upload, download).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException)
                {
                    Trace.TraceInformation("The shaping pumps stopped during connection cleanup.");
                }
                finally
                {
                    context.Transport = original;
                    controller.Unregister(context);
                    await input.Reader.CompleteAsync().ConfigureAwait(false);
                    await output.Writer.CompleteAsync().ConfigureAwait(false);
                }
            }
        }

        private static async Task PumpAsync(
            PipeReader source,
            PipeWriter destination,
            NetworkShapingController controller,
            NetworkShapingPacer pacer,
            CancellationToken cancellationToken)
        {
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var queue = Channel.CreateBounded<Chunk>(new BoundedChannelOptions(64)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });
            Task producer = ProduceAsync(source, queue.Writer, controller, stopping.Token);
            Exception failure = null;
            try
            {
                await foreach (Chunk chunk in queue.Reader.ReadAllAsync(stopping.Token).ConfigureAwait(false))
                {
                    using (chunk)
                    {
                        int offset = 0;
                        while (offset < chunk.Length)
                        {
                            int count = await pacer.PaceAsync(chunk.Length - offset, stopping.Token).ConfigureAwait(false);
                            await DelayAsync(chunk, controller, stopping.Token).ConfigureAwait(false);
                            FlushResult flush = await destination.WriteAsync(
                                chunk.Buffer.AsMemory(offset, count), stopping.Token).ConfigureAwait(false);
                            if (flush.IsCanceled || flush.IsCompleted)
                            {
                                return;
                            }
                            offset += count;
                        }
                    }
                }
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                failure = ex;
                throw;
            }
            finally
            {
                await stopping.CancelAsync().ConfigureAwait(false);
                try
                {
                    await producer.ConfigureAwait(false);
                }
                finally
                {
                    while (queue.Reader.TryRead(out Chunk remaining))
                    {
                        // The consumer never took ownership of these queued buffers.
                        using var abandoned = remaining;
                    }
                    await destination.CompleteAsync(failure).ConfigureAwait(false);
                }
            }
        }

        private static async Task ProduceAsync(
            PipeReader source,
            ChannelWriter<Chunk> destination,
            NetworkShapingController controller,
            CancellationToken cancellationToken)
        {
            Exception failure = null;
            try
            {
                while (true)
                {
                    ReadResult read = await source.ReadAsync(cancellationToken).ConfigureAwait(false);
                    ReadOnlySequence<byte> buffer = read.Buffer;
                    double arrived = NetworkShapingPacer.Timestamp();
                    try
                    {
                        while (!buffer.IsEmpty)
                        {
                            int length = (int)Math.Min(ChunkSize, buffer.Length);
                            await controller.BufferSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
                            Chunk chunk = null;
                            try
                            {
                                chunk = new Chunk(controller, length, arrived);
                                buffer.Slice(0, length).CopyTo(chunk.Buffer);
                                await destination.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
                            }
                            catch
                            {
                                if (chunk == null)
                                {
                                    controller.BufferSlots.Release();
                                }
                                else
                                {
                                    chunk.Dispose();
                                }
                                throw;
                            }
                            buffer = buffer.Slice(length);
                        }
                    }
                    finally
                    {
                        source.AdvanceTo(read.Buffer.End);
                    }
                    if (read.IsCanceled || read.IsCompleted)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                failure = ex;
            }
            finally
            {
                destination.TryComplete(failure);
                await source.CompleteAsync(failure).ConfigureAwait(false);
            }
        }

        private static async Task DelayAsync(
            Chunk chunk,
            NetworkShapingController controller,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                NetworkShapingController.Snapshot snapshot = controller.ReadSnapshot();
                NetworkShapingProfile profile = snapshot.Profile;
                if (profile.Mode != NetworkShapingMode.Shape)
                {
                    return;
                }

                double due = chunk.Arrived + (profile.LatencyMs + chunk.Jitter * profile.JitterMs) / 2000;
                await NetworkShapingPacer.WaitAsync(due, snapshot, cancellationToken).ConfigureAwait(false);
                if (controller.ReadSnapshot().Version == snapshot.Version)
                {
                    return;
                }
            }
        }

        private sealed class Chunk(NetworkShapingController controller, int length, double arrived) : IDisposable
        {
            internal byte[] Buffer { get; } = ArrayPool<byte>.Shared.Rent(ChunkSize);
            internal int Length { get; } = length;
            internal double Arrived { get; } = arrived;
            internal double Jitter { get; } = Random.Shared.NextDouble() * 2 - 1;

            public void Dispose()
            {
                ArrayPool<byte>.Shared.Return(Buffer, clearArray: true);
                controller.BufferSlots.Release();
            }
        }

        private sealed class ShapedPipe(PipeReader input, PipeWriter output) : IDuplexPipe
        {
            public PipeReader Input { get; } = input;
            public PipeWriter Output { get; } = output;
        }
    }
}
