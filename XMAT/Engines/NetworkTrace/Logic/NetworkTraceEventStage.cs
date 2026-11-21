// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using XMAT.SharedInterfaces;

namespace XMAT.NetworkTrace
{
    internal sealed class NetworkTraceEventStage : IDisposable
    {
        private readonly object _gate = new();
        private readonly string _directory;
        private readonly string _path;
        private StreamWriter _writer;
        private readonly long _maximumBytes;
        private readonly CancellationToken _cancellationToken;
        private long _bytes;
        internal long Count { get; private set; }

        internal NetworkTraceEventStage(long maximumBytes = long.MaxValue, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
            _maximumBytes = maximumBytes;
            _cancellationToken = cancellationToken;
            _directory = Path.Combine(Path.GetTempPath(), $"XMAT.Trace.{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            _path = Path.Combine(_directory, "events.jsonl");
            try
            { _writer = new StreamWriter(_path, append: false, new UTF8Encoding(false)); }
            catch { Directory.Delete(_directory); throw; }
        }

        internal void Append(string record)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (Encoding.UTF8.GetByteCount(record) > _maximumBytes - _bytes)
                throw new QueryException(QueryError.ResultLimit, "The expanded trace exceeds the approved input limit.");
            using var document = JsonDocument.Parse(record);
            GetValues(document.RootElement, string.Empty);
            var json = JsonSerializer.Serialize(document.RootElement);
            lock (_gate)
            {
                if (_writer == null)
                    throw new InvalidOperationException("The trace event stage has closed.");
                var bytes = Encoding.UTF8.GetByteCount(json) + 1L;
                if (bytes > _maximumBytes - _bytes)
                    throw new QueryException(QueryError.ResultLimit, "The expanded trace exceeds the approved input limit.");
                _writer.WriteLine(json);
                _bytes += bytes;
                Count++;
            }
        }

        internal void Complete()
        {
            lock (_gate)
            {
                _writer?.Dispose();
                _writer = null;
            }
        }

        internal IEnumerable<object[]> Rows(string deviceName)
        {
            using var reader = new StreamReader(_path, Encoding.UTF8);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                using var document = JsonDocument.Parse(line);
                yield return GetValues(document.RootElement, deviceName);
            }

        }

        internal IEnumerable<IDataRecord> Records(string deviceName)
        {
            foreach (var values in Rows(deviceName))
                yield return new StageRecord(values);
        }

        private sealed class StageRecord(object[] values) : IDataRecord
        {
            public int RowId => -1;
            public long Int(string fieldName) => fieldName switch
            {
                "ProcessId" => Convert.ToInt64(values[1], CultureInfo.InvariantCulture),
                "ThreadId" => Convert.ToInt64(values[2], CultureInfo.InvariantCulture),
                "StartPacket" => Convert.ToInt64(values[5], CultureInfo.InvariantCulture),
                "EndPacket" => Convert.ToInt64(values[6], CultureInfo.InvariantCulture),
                "Fragment" => Convert.ToInt64(values[7], CultureInfo.InvariantCulture),
                "Send" => Convert.ToInt64(values[8], CultureInfo.InvariantCulture),
                "Receive" => Convert.ToInt64(values[9], CultureInfo.InvariantCulture),
                _ => 0
            };
            public string Str(string fieldName) => fieldName switch
            {
                "DeviceName" => (string)values[0],
                "Timestamp" => (string)values[3],
                "MediaType" => (string)values[4],
                "Payload" => (string)values[10],
                _ => null
            };
        }

        private static object[] GetValues(JsonElement record, string deviceName)
        {
            var metadata = record.GetProperty("event");
            var packet = record.GetProperty("packet");
            var flags = new HashSet<string>();
            foreach (var flag in packet.GetProperty("flags").EnumerateArray())
                flags.Add(flag.GetString());
            var payload = packet.GetProperty("data").GetString() ?? string.Empty;
            if (!Convert.TryFromBase64String(payload, new byte[(payload.Length / 4) * 3 + 3], out _))
                throw new FormatException("Invalid packet payload encoding.");
            return
            [
                deviceName, metadata.GetProperty("processId").GetInt32(), metadata.GetProperty("threadId").GetInt32(),
                metadata.GetProperty("timestamp").GetDateTime().ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                packet.GetProperty("mediaType").GetString(), flags.Contains("start") ? 1 : 0,
                flags.Contains("end") ? 1 : 0, flags.Contains("fragment") ? 1 : 0,
                flags.Contains("send") ? 1 : 0, flags.Contains("receive") ? 1 : 0, payload
            ];
        }

        public void Dispose()
        {
            Complete();
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
    }
}
