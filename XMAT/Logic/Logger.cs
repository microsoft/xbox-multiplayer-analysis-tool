// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT
{
    public enum LogLevel : int
    {
        DEBUG = 0,
        INFO = 1,
        WARN = 2,
        ERROR = 3,
        FATAL = 4,
        NONE = 5
    }

    internal class Logger : IDisposable
    {
        private const int LoggingBufferSize = 4 * 1024;
        private const int MaxLoggingCharacters = LoggingBufferSize - 64;

        private readonly object _lifetimeLock = new();
        private BlockingCollection<string> _lines;
        private Task _writerTask;

        internal LogLevel CurrentLogLevel { get; set; }

        internal void InitLog(string nameOfLogFile, LogLevel level)
        {
            lock (_lifetimeLock)
            {
                EnsureCanInitialize();
                string logsPath = Path.Combine(PublicUtilities.StorageDirectoryPath, "Logs");
                Directory.CreateDirectory(logsPath);
                string finalPath = Path.Combine(logsPath, nameOfLogFile);
                InitLog(new FileStream(finalPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite), level);
            }
        }

        internal void InitLog(Stream stream, LogLevel level)
        {
            ArgumentNullException.ThrowIfNull(stream);
            lock (_lifetimeLock)
            {
                EnsureCanInitialize();
                var writer = new StreamWriter(stream, Encoding.UTF8, LoggingBufferSize);
                try
                {
                    writer.AutoFlush = true;
                    CurrentLogLevel = level;
                    var lines = new BlockingCollection<string>();
                    _lines?.Dispose();
                    _lines = lines;

                    // Only the consumer touches or disposes the writer; capture this lifetime's queue and stream.
                    _writerTask = Task.Factory.StartNew(() =>
                    {
                        using (writer)
                        {
                            foreach (string line in lines.GetConsumingEnumerable())
                            {
                                if (level < CurrentLogLevel)
                                    continue;
                                writer.WriteLine(line);
                            }
                        }
                    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                }
                catch
                {
                    writer.Dispose();
                    throw;
                }
            }
        }

        internal void Log(int id, LogLevel level, string value)
        {
            lock (_lifetimeLock)
            {
                // Calls made during or after shutdown are intentionally ignored, before the queue is touched.
                if (_lines == null || _lines.IsAddingCompleted)
                    return;
                _lines.Add($"{DateTime.Now:HH:mm:ss.fff} [{level}] ({id:D4}) {value.Substring(0, Math.Min(value.Length, MaxLoggingCharacters))}");
            }
        }

        internal void CloseLog()
        {
            Task writerTask;
            lock (_lifetimeLock)
            {
                _lines?.CompleteAdding();
                writerTask = _writerTask;
            }

            try
            {
                // A timeout cannot make disposal safe: drain and dispose on the consumer, then surface its errors.
                writerTask?.GetAwaiter().GetResult();
            }
            finally
            {
                lock (_lifetimeLock)
                {
                    // A new lifetime may have started while this caller waited for the previous consumer.
                    if (ReferenceEquals(_writerTask, writerTask))
                    {
                        _lines?.Dispose();
                        _lines = null;
                    }
                }
            }
        }

        private void EnsureCanInitialize()
        {
            if (_writerTask != null && !_writerTask.IsCompleted)
                throw new InvalidOperationException("Close the previous log before initializing another.");
            _writerTask?.GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            CloseLog();
        }
    }
}
