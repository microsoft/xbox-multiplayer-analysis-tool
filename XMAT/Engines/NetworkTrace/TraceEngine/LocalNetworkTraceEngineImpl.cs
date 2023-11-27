// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Text;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Text.Json;
using System.IO;
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

        private long _startTimeTicks = 0;
        private string _sessionName;

        unsafe private void Dispatch(TraceEventNativeMethods.EVENT_RECORD* record)
        {
            if ((record->EventHeader.Id != PacketFragment_value) ||
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

            DateTime timestamp = DateTime.FromFileTimeUtc(_startTimeTicks + record->EventHeader.TimeStamp);

            json.Append(timestamp.ToString("yyyy-MM-ddThh:mm:ss.fffffffZ"));
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

            var header = (PacketFragmentHeader*)record->UserData;

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

            EventRecordAvailable?.Invoke(this, json.ToString());
        }

        public async Task GetAllEventsAsync()
        {
            await Task.Run(() =>
            {
                unsafe
                {
                    // netsh writes the file here:
                    // %LOCALAPPDATA%\Temp\NetTraces\[sessionname]NetTrace.etl

                    string etlPath = $"Temp\\NetTraces\\{_sessionName}NetTrace.etl";

                    var logfile = new TraceEventNativeMethods.EVENT_TRACE_LOGFILEW
                    {
                        LogFileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), etlPath),
                        EventCallback = Dispatch,
                        LogFileMode = TraceEventNativeMethods.PROCESS_TRACE_MODE_EVENT_RECORD |
                                      TraceEventNativeMethods.PROCESS_TRACE_MODE_RAW_TIMESTAMP
                    };

                    UInt64[] traceHandle = new UInt64[1];

                    traceHandle[0] = TraceEventNativeMethods.OpenTrace(ref logfile);
                    if (traceHandle[0] == TraceEventNativeMethods.INVALID_HANDLE_VALUE)
                    {
                        Marshal.ThrowExceptionForHR(TraceEventNativeMethods.GetHRForLastWin32Error());
                    }

                    _startTimeTicks = logfile.LogfileHeader.StartTime;

                    int dwErr = TraceEventNativeMethods.ProcessTrace(traceHandle, 1, (IntPtr)0, (IntPtr)0);
                    if (dwErr != 0)
                    {
                        Marshal.ThrowExceptionForHR(TraceEventNativeMethods.GetHRFromWin32(dwErr));
                    }

                    TraceEventNativeMethods.CloseTrace(traceHandle[0]);
                }
            });
        }

        private string CreateSessionName()
        {
            return $"{SessionName}-{DateTime.Now.ToString(SessionDateFormat)}-";
        }

        public async Task StartPacketTraceAsync()
        {
            _sessionName = CreateSessionName();

            await Task.Run(() =>
            {
                ProcessStartInfo info = new ProcessStartInfo
                {
                    FileName = "netsh.exe",
                    Arguments = $"trace start capture=yes sessionname={_sessionName} provider=Microsoft-Windows-NDIS-PacketCapture level=4",
                    Verb = "runas",
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                };

                var netsh = Process.Start(info);
                var sto = netsh.StandardOutput.ReadToEnd();

                netsh.WaitForExit();

                if(netsh.ExitCode != 0)
                {
                    if (sto.Contains(RunAsAdminMarker))
                    {
                        // The process needs to be run as admin so we throw
                        // a UAE and will prompt the user to restart as Admin
                        throw new UnauthorizedAccessException(sto);
                    }
                    else
                    {
                        throw new ApplicationException(sto);
                    }
                }
            });
        }

        public async Task StopPacketTraceAsync()
        {
            await Task.Run(() =>
            {
                ProcessStartInfo info = new ProcessStartInfo
                {
                    FileName = "netsh.exe",
                    Arguments = $"trace stop sessionname={_sessionName}",
                    Verb = "runas",
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                };

                var netsh = Process.Start(info);
                var sto = netsh.StandardOutput.ReadToEnd();

                netsh.WaitForExit();

                if (netsh.ExitCode != 0)
                {
                    throw new ApplicationException(sto);
                }
            });
        }
    }
}
