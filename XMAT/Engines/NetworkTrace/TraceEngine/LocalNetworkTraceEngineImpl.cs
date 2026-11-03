// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Tracing;

namespace XMAT.NetworkTrace.NTDE
{

#pragma warning disable 0649

    internal unsafe struct PacketFragmentHeader
    {
        public uint MiniportIfIndex;
        public uint LowerIfIndex;
        public uint FragmentSize;
    };

    class LocalNetworkTraceEngineImpl : INetworkTraceEngine
    {
        internal const ulong KW_MEDIA_802_3 = 0x1;
        internal const ulong KW_MEDIA_WIRELESS_WAN = 0x200;
        internal const ulong KW_MEDIA_TUNNEL = 0x8000;
        internal const ulong KW_MEDIA_NATIVE_802_11 = 0x10000;
        internal const ulong KW_VMSWITCH = 0x1000000;
        internal const ulong KW_PACKET_START = 0x40000000;
        internal const ulong KW_PACKET_END = 0x80000000;
        internal const ulong KW_SEND = 0x100000000;
        internal const ulong KW_RECEIVE = 0x200000000;
        internal const ulong KW_L3_CONNECT = 0x400000000;
        internal const ulong KW_L2_CONNECT = 0x800000000;
        internal const ulong KW_CLOSE = 0x1000000000;
        internal const ulong KW_AUTHENTICATION = 0x2000000000;
        internal const ulong KW_CONFIGURATION = 0x4000000000;
        internal const ulong KW_GLOBAL = 0x8000000000;
        internal const ulong KW_DROPPED = 0x10000000000;
        internal const ulong KW_PII_PRESENT = 0x20000000000;
        internal const ulong KW_PACKET = 0x40000000000;
        internal const ulong KW_ADDRESS = 0x80000000000;
        internal const ulong KW_STD_TEMPLATE_HINT = 0x100000000000;
        internal const ulong KW_STATE_TRANSITION = 0x200000000000;

        internal const ushort PacketFragment_value = 0x3e9;
        internal const byte CHANNEL_PCAP = 0x10;
        internal const byte WINEVENT_LEVEL_INFO = 0x4;
        internal const byte WINEVENT_OPCODE_INFO = 0x0;
        internal const ushort WINEVENT_TASK_NONE = 0x0;

        internal const string SessionName = "XMAT";
        internal const string SessionDateFormat = "HHmmss";
        internal const string RunAsAdminMarker = "requires elevation";

        public event EventHandler<string> EventRecordAvailable;

        // Microsoft-Windows-NDIS-PacketCapture
        internal static readonly Guid NdisPacketCaptureProviderId = new Guid("2ED6006E-4729-4609-B423-3EE7BCD678EF");

        private int _packetCount = 0;
        private string _sessionName;
        private readonly Func<IReadOnlyList<string>, Task<int>> _runCommand;
        private readonly Func<bool> _isElevated;
        private readonly Func<string, bool> _isSessionActive;
        private Exception _dispatchFailure;
        private CancellationToken _readCancellation;
        internal string OwnedSessionName => _sessionName;

        internal LocalNetworkTraceEngineImpl(
            Func<IReadOnlyList<string>, Task<int>> runCommand = null, Func<bool> isElevated = null,
            Func<string, bool> isSessionActive = null)
        {
            _runCommand = runCommand ?? RunNetshAsync;
            _isElevated = isElevated ?? NetworkTraceEngineFactory.IsElevated;
            _isSessionActive = isSessionActive ?? IsSessionActive;
        }

        unsafe private void Dispatch(TraceEventNativeMethods.EVENT_RECORD* record)
        {
            if (_dispatchFailure != null || _readCancellation.IsCancellationRequested)
                return;
            try
            { DispatchRecord(record); }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception)) { _dispatchFailure = exception; }
        }

        unsafe private void DispatchRecord(TraceEventNativeMethods.EVENT_RECORD* record)
        {
            if (record == null)
                throw new InvalidDataException("A trace callback did not contain an event record.");
            if ((record->EventHeader.ProviderId != NdisPacketCaptureProviderId) ||
                (record->EventHeader.Id != PacketFragment_value) ||
                (record->EventHeader.Version != 0) ||
                (record->EventHeader.Channel != CHANNEL_PCAP) ||
                (record->EventHeader.Level != WINEVENT_LEVEL_INFO) ||
                (record->EventHeader.Opcode != WINEVENT_OPCODE_INFO) ||
                (record->EventHeader.Task != WINEVENT_TASK_NONE) ||
                ((record->EventHeader.Keyword & (KW_PII_PRESENT | KW_PACKET)) != (KW_PII_PRESENT | KW_PACKET)))
            {
                return;
            }

            StringBuilder json = new StringBuilder();

            json.Append("{ \"event\": { \"processId\": ");
            json.Append(record->EventHeader.ProcessId);
            json.Append(", \"threadId\": ");
            json.Append(record->EventHeader.ThreadId);
            json.Append(", \"timestamp\": \"");

            DateTime timestamp = DateTime.FromFileTimeUtc(record->EventHeader.TimeStamp);

            json.Append(timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"));
            json.Append("\" }, \"packet\": { \"mediaType\": \"");

            if ((record->EventHeader.Keyword & KW_MEDIA_802_3) != 0)
            {
                json.Append("ethernet");
            }
            else if ((record->EventHeader.Keyword & KW_MEDIA_WIRELESS_WAN) != 0)
            {
                json.Append("wireless");
            }
            else if ((record->EventHeader.Keyword & KW_MEDIA_TUNNEL) != 0)
            {
                json.Append("tunnel");
            }
            else if ((record->EventHeader.Keyword & KW_MEDIA_NATIVE_802_11) != 0)
            {
                json.Append("wifi");
            }

            if (record->UserData == IntPtr.Zero || record->UserDataLength < sizeof(PacketFragmentHeader))
                throw new InvalidDataException("The trace fragment header is incomplete.");
            var header = (PacketFragmentHeader*)record->UserData;
            if (header->FragmentSize > record->UserDataLength - sizeof(PacketFragmentHeader))
                throw new InvalidDataException("The trace fragment extends past its event buffer.");

            json.Append("\", \"miniportIfIndex\": ");
            json.Append(header->MiniportIfIndex);
            json.Append(", \"lowerIfIndex\": ");
            json.Append(header->LowerIfIndex);
            json.Append(", \"flags\": [ ");

            if ((record->EventHeader.Keyword & KW_PACKET_START) != 0)
            {
                json.Append("\"start\", ");
            }

            if ((record->EventHeader.Keyword & KW_PACKET_END) != 0)
            {
                json.Append("\"end\", ");
            }

            if ((record->EventHeader.Keyword & KW_SEND) != 0)
            {
                json.Append("\"send\"");
            }
            else if ((record->EventHeader.Keyword & KW_RECEIVE) != 0)
            {
                json.Append("\"receive\"");
            }

            json.Append(" ], \"data\": \"");

            if (header->FragmentSize > 0)
            {
                IntPtr startPointer = record->UserData + sizeof(PacketFragmentHeader);
                var byteArray = new byte[header->FragmentSize];

                Marshal.Copy(startPointer, byteArray, 0, (int)header->FragmentSize);

                json.Append(System.Convert.ToBase64String(byteArray));
            }

            json.Append("\" } }\n");

            _packetCount++;
            try
            { EventRecordAvailable?.Invoke(this, json.ToString()); }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception)) { _dispatchFailure = exception; }
        }

        public async Task GetAllEventsAsync()
        {
            // netsh writes the file here:
            // %LOCALAPPDATA%\Temp\NetTraces\[sessionname]NetTrace.etl
            string etlPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                $"Temp\\NetTraces\\{_sessionName}NetTrace.etl");

            await ReadEventsFromFileAsync(etlPath);
        }

        /// <summary>
        /// Reads packet capture events from an existing .etl file (e.g. one produced by
        /// "netsh trace start capture=yes" on another PC) and raises EventRecordAvailable
        /// for each packet. Returns the number of packets found.
        /// </summary>
        public async Task<int> ReadEventsFromFileAsync(string etlPath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(etlPath))
            {
                throw new FileNotFoundException(etlPath);
            }

            _packetCount = 0;
            _dispatchFailure = null;
            _readCancellation = cancellationToken;

            await Task.Run(() =>
            {
                unsafe
                {
                    var logfile = new TraceEventNativeMethods.EVENT_TRACE_LOGFILEW
                    {
                        LogFileName = etlPath,
                        EventCallback = Dispatch,
                        BufferCallback = _ => !cancellationToken.IsCancellationRequested && _dispatchFailure == null,
                        LogFileMode = TraceEventNativeMethods.PROCESS_TRACE_MODE_EVENT_RECORD
                    };

                    UInt64[] traceHandle = new UInt64[1];

                    traceHandle[0] = TraceEventNativeMethods.OpenTrace(ref logfile);
                    if (traceHandle[0] == TraceEventNativeMethods.INVALID_HANDLE_VALUE)
                    {
                        Marshal.ThrowExceptionForHR(TraceEventNativeMethods.GetHRForLastWin32Error());
                    }

                    try
                    {
                        int dwErr = TraceEventNativeMethods.ProcessTrace(traceHandle, 1, (IntPtr)0, (IntPtr)0);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (_dispatchFailure != null)
                            ExceptionDispatchInfo.Capture(_dispatchFailure).Throw();
                        if (dwErr != 0)
                        {
                            Marshal.ThrowExceptionForHR(TraceEventNativeMethods.GetHRFromWin32(dwErr));
                        }
                    }
                    finally
                    {
                        TraceEventNativeMethods.CloseTrace(traceHandle[0]);
                        GC.KeepAlive(logfile);
                    }
                }
            }, cancellationToken);

            if (_dispatchFailure != null)
                ExceptionDispatchInfo.Capture(_dispatchFailure).Throw();
            return _packetCount;
        }

        private string CreateSessionName()
        {
            return $"{SessionName}-{Guid.NewGuid():N}-";
        }

        public async Task StartPacketTraceAsync()
        {
            if (!_isElevated())
                throw new UnauthorizedAccessException(Localization.GetLocalizedString("ELEVATION_REQUIRED_MESSAGE"));
            if (_sessionName != null)
                throw new NetworkTraceEngineException(Localization.GetLocalizedString("NETCAP_ERROR_START"), true);
            _sessionName = CreateSessionName();
            if (await _runCommand(["trace", "start", "capture=yes", $"sessionname={_sessionName}",
                "provider=Microsoft-Windows-NDIS-PacketCapture", "level=4"]) != 0)
                throw new NetworkTraceEngineException(Localization.GetLocalizedString("NETCAP_ERROR_START"), true);
        }

        public async Task StopPacketTraceAsync()
        {
            if (_sessionName == null)
                throw new NetworkTraceEngineException(Localization.GetLocalizedString("NETCAP_ERROR_STOP"), false);
            if (!_isElevated())
                throw new UnauthorizedAccessException(Localization.GetLocalizedString("ELEVATION_REQUIRED_MESSAGE"));
            if (!_isSessionActive(_sessionName))
                return;
            if (await _runCommand(["trace", "stop", $"sessionname={_sessionName}"]) != 0 && _isSessionActive(_sessionName))
                throw new NetworkTraceEngineException(Localization.GetLocalizedString("NETCAP_ERROR_STOP"), true);
        }

        private static unsafe bool IsSessionActive(string name)
        {
            var size = sizeof(TraceEventNativeMethods.EVENT_TRACE_PROPERTIES);
            var bytes = new byte[size + 4096];
            fixed (byte* buffer = bytes)
            {
                var properties = (TraceEventNativeMethods.EVENT_TRACE_PROPERTIES*)buffer;
                properties->Wnode.BufferSize = (uint)bytes.Length;
                properties->LoggerNameOffset = (uint)size;
                properties->LogFileNameOffset = (uint)(size + 2048);
                var result = ControlTrace(0, name, properties, 0);
                if (result == 4201)
                    return false;
                if (result == 234)
                    return true;
                if (result != 0)
                    throw new Win32Exception((int)result);
                return true;
            }
        }

        [DllImport("advapi32.dll", EntryPoint = "ControlTraceW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern unsafe uint ControlTrace(
            ulong sessionHandle, string sessionName, TraceEventNativeMethods.EVENT_TRACE_PROPERTIES* properties, uint controlCode);

        internal static ProcessStartInfo CreateNetshStartInfo(IReadOnlyList<string> arguments)
        {
            var result = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "netsh.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments)
                result.ArgumentList.Add(argument);
            return result;
        }

        private static async Task<int> RunNetshAsync(IReadOnlyList<string> arguments)
        {
            using var process = Process.Start(CreateNetshStartInfo(arguments));
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                await Task.WhenAll(output, error);
                throw new NetworkTraceEngineException(Localization.GetLocalizedString("NETCAP_COMMAND_TIMEOUT"), true);
            }
            await Task.WhenAll(output, error);
            return process.ExitCode;
        }
    }
}
