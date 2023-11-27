// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

#pragma warning disable 0649
// This moduleFile contains Internal PINVOKE declarations and has no public API surface. 
namespace Microsoft.Diagnostics.Tracing
{
    /// <summary>
    /// TraceEventNativeMethods contains the PINVOKE declarations needed
    /// to get at the Win32 TraceEvent infrastructure.  It is effectively
    /// a port of evntrace.h to C# declarations.  
    /// </summary>
    internal static unsafe class TraceEventNativeMethods
    {
        #region TimeZone type from winbase.h

        /// <summary>
        ///	Time zone info.  Used as one field of TRACE_EVENT_LOGFILE, below.
        ///	Total struct size is 0xac.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Size = 0xac, CharSet = CharSet.Unicode)]
        internal struct TIME_ZONE_INFORMATION
        {
            public uint Bias;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string StandardName;
            [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.U2, SizeConst = 8)]
            public UInt16[] StandardDate;
            public uint StandardBias;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DaylightName;
            [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.U2, SizeConst = 8)]
            public UInt16[] DaylightDate;
            public uint DaylightBias;
        }

        #endregion TimeZone type from winbase.h
        #region ETW tracing types from evntrace.h

        //	Delegates for use with ETW EVENT_TRACE_LOGFILEW struct.
        //	These are the callbacks that ETW will call while processing a moduleFile
        //	so that we can process each line of the trace moduleFile.
        internal delegate bool EventTraceBufferCallback(
            [In] IntPtr logfile); // Really a EVENT_TRACE_LOGFILEW, but more efficient to marshal manually);
        internal delegate void EventTraceEventCallback(
            [In] EVENT_RECORD* rawData);

        internal const ulong INVALID_HANDLE_VALUE = unchecked((ulong)(-1));

        internal const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
        internal const uint PROCESS_TRACE_MODE_RAW_TIMESTAMP = 0x00001000;

        /// <summary>
        /// Structure used by EVENT_TRACE_PROPERTIES
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct WNODE_HEADER
        {
            public UInt32 BufferSize;
            public UInt32 ProviderId;
            public UInt64 HistoricalContext;
            public UInt64 TimeStamp;
            public Guid Guid;
            public UInt32 ClientContext;
            public UInt32 Flags;
        }

        /// <summary>
        /// EVENT_TRACE_PROPERTIES is a structure used by StartTrace, ControlTrace
        /// however it can not be used directly in the definition of these functions
        /// because extra information has to be hung off the end of the structure
        /// before being passed.  (LofFileNameOffset, LoggerNameOffset)
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct EVENT_TRACE_PROPERTIES
        {
            public WNODE_HEADER Wnode;
            public UInt32 BufferSize;
            public UInt32 MinimumBuffers;
            public UInt32 MaximumBuffers;
            public UInt32 MaximumFileSize;
            public UInt32 LogFileMode;
            public UInt32 FlushTimer;
            public UInt32 EnableFlags;
            public Int32 AgeLimit;
            public UInt32 NumberOfBuffers;
            public UInt32 FreeBuffers;
            public UInt32 EventsLost;
            public UInt32 BuffersWritten;
            public UInt32 LogBuffersLost;
            public UInt32 RealTimeBuffersLost;
            public IntPtr LoggerThreadId;
            public UInt32 LogFileNameOffset;
            public UInt32 LoggerNameOffset;
        }

        //	TraceMessage flags
        //	These flags are overlaid into the node USHORT in the EVENT_TRACE.header.version field.
        //	These items are packed in order in the packet (MofBuffer), as indicated by the flags.
        //	I don't know what PerfTimestamp is (size?) or holds.
        internal enum TraceMessageFlags : int
        {
            Sequence = 0x01,
            Guid = 0x02,
            ComponentId = 0x04,
            Timestamp = 0x08,
            PerformanceTimestamp = 0x10,
            SystemInfo = 0x20,
            FlagMask = 0xffff,
        }

        /// <summary>
        ///	EventTraceHeader and structure used to defined EVENT_TRACE (the main packet)
        ///	I have simplified from the original struct definitions.  I have
        ///	omitted alternate union-fields which we don't use.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct EVENT_TRACE_HEADER
        {
            public ushort Size;
            public ushort FieldTypeFlags;	// holds our MarkerFlags too
            public byte Type;
            public byte Level;
            public ushort Version;
            public int ThreadId;
            public int ProcessId;
            public long TimeStamp;          // Offset 0x10 
            public Guid Guid;
            //	no access to GuidPtr, union'd with guid field
            //	no access to ClientContext & MatchAnyKeywords, ProcessorTime, 
            //	union'd with kernelTime,userTime
            public int KernelTime;         // Offset 0x28
            public int UserTime;
        }

        /// <summary>
        /// EVENT_TRACE is the structure that represents a single 'packet'
        /// of data repesenting a single event.  
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct EVENT_TRACE
        {
            public EVENT_TRACE_HEADER Header;
            public uint InstanceId;
            public uint ParentInstanceId;
            public Guid ParentGuid;
            public IntPtr MofData; // PVOID
            public int MofLength;
            public ETW_BUFFER_CONTEXT BufferContext;
        }

        /// <summary>
        /// TRACE_LOGFILE_HEADER is a header used to define EVENT_TRACE_LOGFILEW.
        ///	Total struct size is 0x110.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct TRACE_LOGFILE_HEADER
        {
            public uint BufferSize;
            public uint Version;            // This is for the operating system it was collected on.  Major, Minor, SubVerMajor, subVerMinor
            public uint ProviderVersion;
            public uint NumberOfProcessors;
            public long EndTime;            // 0x10
            public uint TimerResolution;
            public uint MaximumFileSize;
            public uint LogFileMode;        // 0x20
            public uint BuffersWritten;
            public uint StartBuffers;
            public uint PointerSize;
            public uint EventsLost;         // 0x30
            public uint CpuSpeedInMHz;
            public IntPtr LoggerName;	// string, but not CoTaskMemAlloc'd
            public IntPtr LogFileName;	// string, but not CoTaskMemAlloc'd
            public TIME_ZONE_INFORMATION TimeZone;   // 0x40         0xac size
            public long BootTime;
            public long PerfFreq;
            public long StartTime;
            public uint ReservedFlags;
            public uint BuffersLost;        // 0x10C?        
        }

        /// <summary>
        ///	EVENT_TRACE_LOGFILEW Main struct passed to OpenTrace() to be filled in.
        /// It represents the collection of ETW events as a whole.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct EVENT_TRACE_LOGFILEW
        {
            [MarshalAs(UnmanagedType.LPWStr)]
            public string LogFileName;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string LoggerName;
            public Int64 CurrentTime;
            public uint BuffersRead;
            public uint LogFileMode;
            // EVENT_TRACE for the current event.  Nulled-out when we are opening files.
            // [FieldOffset(0x18)] 
            public EVENT_TRACE CurrentEvent;
            // [FieldOffset(0x70)]
            public TRACE_LOGFILE_HEADER LogfileHeader;
            // callback before each buffer is read
            // [FieldOffset(0x180)]
            public EventTraceBufferCallback BufferCallback;
            public Int32 BufferSize;
            public Int32 Filled;
            public Int32 EventsLost;
            // callback for every 'event', each line of the trace moduleFile
            // [FieldOffset(0x190)]
            public EventTraceEventCallback EventCallback;
            public Int32 IsKernelTrace;     // TRUE for kernel logfile
            public IntPtr Context;	        // reserved for internal use
        }
        #endregion // ETW tracing types

        #region ETW tracing types from evntcons.h
        internal const ushort EVENT_HEADER_FLAG_STRING_ONLY = 0x0004;
        internal const ushort EVENT_HEADER_FLAG_32_BIT_HEADER = 0x0020;
        internal const ushort EVENT_HEADER_FLAG_64_BIT_HEADER = 0x0040;
        internal const ushort EVENT_HEADER_FLAG_CLASSIC_HEADER = 0x0100;

        /// <summary>
        ///	EventTraceHeader and structure used to define EVENT_TRACE_LOGFILE (the main packet on Vista and above)
        ///	I have simplified from the original struct definitions.  I have
        ///	omitted alternate union-fields which we don't use.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct EVENT_HEADER
        {
            public ushort Size;
            public ushort HeaderType;
            public ushort Flags;            // offset: 0x4
            public ushort EventProperty;
            public int ThreadId;            // offset: 0x8
            public int ProcessId;           // offset: 0xc
            public long TimeStamp;          // offset: 0x10
            public Guid ProviderId;         // offset: 0x18
            public ushort Id;               // offset: 0x28
            public byte Version;            // offset: 0x2a
            public byte Channel;
            public byte Level;              // offset: 0x2c
            public byte Opcode;
            public ushort Task;
            public ulong Keyword;
            public int KernelTime;         // offset: 0x38
            public int UserTime;           // offset: 0x3C
            public Guid ActivityId;
        }

        /// <summary>
        ///	Provides context information about the event
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct ETW_BUFFER_CONTEXT
        {
            public byte ProcessorNumber;
            public byte Alignment;
            public ushort LoggerId;
        }

        /// <summary>
        ///	Defines the layout of an event that ETW delivers
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct EVENT_RECORD
        {
            public EVENT_HEADER EventHeader;            //  size: 80
            public ETW_BUFFER_CONTEXT BufferContext;    //  size: 4
            public ushort ExtendedDataCount;
            public ushort UserDataLength;               //  offset: 86
            public EVENT_HEADER_EXTENDED_DATA_ITEM* ExtendedData;
            public IntPtr UserData;
            public IntPtr UserContext;
        }

        // Values for the ExtType field 
        internal const ushort EVENT_HEADER_EXT_TYPE_RELATED_ACTIVITYID = 0x0001;
        internal const ushort EVENT_HEADER_EXT_TYPE_SID = 0x0002;
        internal const ushort EVENT_HEADER_EXT_TYPE_TS_ID = 0x0003;
        internal const ushort EVENT_HEADER_EXT_TYPE_INSTANCE_INFO = 0x0004;
        internal const ushort EVENT_HEADER_EXT_TYPE_STACK_TRACE32 = 0x0005;
        internal const ushort EVENT_HEADER_EXT_TYPE_STACK_TRACE64 = 0x0006;
        internal const ushort EVENT_HEADER_EXT_TYPE_PEBS_INDEX = 0x0007;
        internal const ushort EVENT_HEADER_EXT_TYPE_PMC_COUNTERS = 0x0008;
        internal const ushort EVENT_HEADER_EXT_TYPE_PSM_KEY = 0x0009;
        internal const ushort EVENT_HEADER_EXT_TYPE_EVENT_KEY = 0x000A;
        internal const ushort EVENT_HEADER_EXT_TYPE_EVENT_SCHEMA_TL = 0x000B;
        internal const ushort EVENT_HEADER_EXT_TYPE_PROV_TRAITS = 0x000C;
        internal const ushort EVENT_HEADER_EXT_TYPE_PROCESS_START_KEY = 0x000D;
        internal const ushort EVENT_HEADER_EXT_TYPE_CONTROL_GUID = 0x000E;
        internal const ushort EVENT_HEADER_EXT_TYPE_QPC_DELTA = 0x000F;
        internal const ushort EVENT_HEADER_EXT_TYPE_CONTAINER_ID = 0x0010;
        internal const ushort EVENT_HEADER_EXT_TYPE_MAX = 0x0011;

        [StructLayout(LayoutKind.Sequential)]
        internal struct EVENT_HEADER_EXTENDED_DATA_ITEM
        {
            public ushort Reserved1;
            public ushort ExtType;
            public ushort Reserved2;
            public ushort DataSize;
            public ulong DataPtr;
        };
        #endregion

        #region ETW tracing functions
        //	TRACEHANDLE handle type is a ULONG64 in evntrace.h.  Use UInt64 here.
        [DllImport("advapi32.dll",
            EntryPoint = "OpenTraceW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        internal static extern UInt64 OpenTrace(
            [In][Out] ref EVENT_TRACE_LOGFILEW logfile);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        internal static extern int ProcessTrace(
            [In] UInt64[] handleArray,
            [In] uint handleCount,
            [In] IntPtr StartTime,
            [In] IntPtr EndTime);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        internal static extern int CloseTrace(
            [In] UInt64 traceHandle);

        #endregion // ETW tracing functions

        internal static int GetHRForLastWin32Error()
        {
            int dwLastError = Marshal.GetLastWin32Error();
            if ((dwLastError & 0x80000000) == 0x80000000)
            {
                return dwLastError;
            }
            else
            {
                return (dwLastError & 0x0000FFFF) | unchecked((int)0x80070000);
            }
        }

        internal static int GetHRFromWin32(int dwErr)
        {
            return (int)((0 != dwErr) ? (0x80070000 | ((uint)dwErr & 0xffff)) : 0);
        }

    }
}
