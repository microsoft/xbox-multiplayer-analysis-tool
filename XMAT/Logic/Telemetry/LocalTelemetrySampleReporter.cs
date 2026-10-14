// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace XMAT.Telemetry
{
    internal sealed class LocalTelemetrySampleReporter : ITelemetryReporter
    {
        private readonly Channel<SampleEvent> _queue = Channel.CreateBounded<SampleEvent>(
            new BoundedChannelOptions(OneCollectorTelemetryReporter.QueueCapacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait
            });
        private readonly Dictionary<string, object> _resources;
        private readonly Action<string> _diagnose;
        private readonly Task _writerTask;
        private int _disposed;

        internal LocalTelemetrySampleReporter(string path,
            IReadOnlyList<KeyValuePair<string, object>> resources, Action<string> diagnose)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            if (!Path.IsPathFullyQualified(path))
            {
                throw new ArgumentException("A fully qualified sample file path is required.", nameof(path));
            }
            _resources = resources.ToDictionary();
            _diagnose = diagnose;
            var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false))
            { AutoFlush = true };
            _writerTask = WriteSamplesAsync(writer);
        }

        internal static string SamplePath(string[] args)
        {
            var index = Array.IndexOf(args, "--telemetry-sample");
            if (index < 0)
            {
                return null;
            }
            if (args.Length != 2 || index != 0 || string.IsNullOrWhiteSpace(args[1]) || !Path.IsPathFullyQualified(args[1]))
            {
                throw new ArgumentException("Use --telemetry-sample followed by a fully qualified output file path.", nameof(args));
            }
            return args[1];
        }

        public void Send(TelemetryEvent item, Guid sessionId)
        {
            if (!item.IsValid)
            {
                _diagnose("InvalidEvent");
                return;
            }
            if (Volatile.Read(ref _disposed) != 0 || !_queue.Writer.TryWrite(new(
                $"{TelemetryConfiguration.EventNamespace}.{item.Kind}",
                DateTimeOffset.UtcNow, _resources, item.Attributes(sessionId).ToDictionary())))
            {
                _diagnose("LocalSampleDropped");
            }
        }

        private async Task WriteSamplesAsync(StreamWriter writer)
        {
            using (writer)
            {
                try
                {
                    await foreach (var sample in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
                    {
                        await writer.WriteLineAsync(JsonSerializer.Serialize(sample, SampleJsonOptions)).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
                {
                    _queue.Writer.TryComplete();
                    _diagnose("LocalSampleWriteFailed");
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            _queue.Writer.TryComplete();
            if (!_writerTask.Wait(2000))
            {
                _diagnose("LocalSampleShutdownTimedOut");
            }
        }

        private static readonly JsonSerializerOptions SampleJsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private sealed record SampleEvent(string Name, DateTimeOffset Time,
            Dictionary<string, object> Resources, Dictionary<string, object> Data);
    }
}
