// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT
{
    public enum LogLevel : int
    {
        DEBUG = 0,
        INFO  = 1,
        WARN  = 2,
        ERROR = 3,
        FATAL = 4,
        NONE  = 5
    }

    internal class Logger : IDisposable
    {
        private const int LoggingBufferSize = 4 * 1024;
        private const int MaxLoggingCharacters = LoggingBufferSize - 64;

        private FileStream _loggingFS;
        private StreamWriter _loggingSW;
        private BlockingCollection<string> _lines;
        private ManualResetEvent _reset = new(false);

        internal LogLevel CurrentLogLevel { get; set; }

        internal void InitLog(string nameOfLogFile, LogLevel level)
        {
            _lines = new BlockingCollection<string>();
            _reset.Reset();
            CurrentLogLevel = level;
               
            string logsPath = Path.Combine(PublicUtilities.StorageDirectoryPath, "Logs");
            Directory.CreateDirectory(logsPath);
            string finalPath = Path.Combine(logsPath, nameOfLogFile);
            _loggingFS = new FileStream(finalPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
            _loggingSW = new StreamWriter(_loggingFS, Encoding.UTF8, LoggingBufferSize)
            {
                AutoFlush = true
            };

            // this will fire and forget, but run forever due to GetConsumingEnumerable()
            Task.Factory.StartNew(() =>
            {
                foreach(string line in _lines.GetConsumingEnumerable())
                {
                    if(level < CurrentLogLevel || _loggingSW == null || _loggingFS == null)
                        continue;

                    _loggingSW.WriteLine(line);
                }
                _reset.Set();
            }, TaskCreationOptions.LongRunning);
        }

        internal void Log(int id, LogLevel level, string value)
        {
            _lines.Add($"{DateTime.Now:HH:mm:ss.fff} [{level}] ({id:D4}) {value.Substring(0, Math.Min(value.Length, MaxLoggingCharacters))}");
        }

        internal void CloseLog()
        {
            _lines.CompleteAdding();
            _reset.WaitOne(500);

            if(_loggingSW != null)
            {
                _loggingSW.Close();
                _loggingSW = null;
            }

            if(_loggingFS != null)
            {
                _loggingFS.Close();
                _loggingFS = null;
            }
        }

        public void Dispose()
        {
            CloseLog();
        }
    }
}
