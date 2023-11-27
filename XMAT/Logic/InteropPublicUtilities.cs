// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace XMAT
{
    public static class InteropPublicUtilities
    {
        public static PingReply Send(
            IntPtr icmpHandle,
            IPAddress srcAddress,
            IPAddress destAddress,
            int timeoutInMs = 3000,
            byte[] buffer = null,
            PingOptions pingOptions = null)
        {
            if (destAddress == null ||
                destAddress.AddressFamily != AddressFamily.InterNetwork ||
                destAddress.Equals(IPAddress.Any))
            {
                throw new ArgumentException("The destination IP address is not valid.");
            }

            if (srcAddress == null ||
                srcAddress.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new ArgumentException("The source IP address is not valid.");
            }

            var source = srcAddress == null ? 0 : BitConverter.ToUInt32(srcAddress.GetAddressBytes(), 0);
            var destination = BitConverter.ToUInt32(destAddress.GetAddressBytes(), 0);
            var sendbuffer = buffer ?? new byte[] { };
            var options = new IcmpInterop.Option
            {
                Ttl = (pingOptions == null ? (byte)255 : (byte)pingOptions.Ttl),
                Flags = (pingOptions == null ? (byte)0 : pingOptions.DontFragment ? (byte)0x02 : (byte)0) //0x02
            };
            var nativeReplySize = IcmpInterop.ReplyMarshalSize;
            var fullReplyBufferSize = nativeReplySize + sendbuffer.Length;

            var nativeReplyBuffer = Marshal.AllocHGlobal(fullReplyBufferSize);

            try
            {
                DateTime startTimestamp = DateTime.Now;

                // see the following for documentation about this underlying native method:
                // https://docs.microsoft.com/en-us/windows/win32/api/icmpapi/nf-icmpapi-icmpsendecho2ex
                var returnValue = IcmpInterop.IcmpSendEcho2Ex(
                    //_In_ HANDLE IcmpHandle,
                    icmpHandle,
                    //_In_opt_ HANDLE Event,
                    default(IntPtr),
                    //_In_opt_ PIO_APC_ROUTINE ApcRoutine,
                    default(IntPtr),
                    //_In_opt_ PVOID ApcContext
                    default(IntPtr),
                    //_In_ IPAddr SourceAddress,
                    source,
                    //_In_ IPAddr DestinationAddress,
                    destination,
                    //_In_ LPVOID RequestData,
                    sendbuffer,
                    //_In_ WORD RequestSize,
                    (short)sendbuffer.Length,
                    //_In_opt_ PIP_OPTION_INFORMATION RequestOptions,
                    ref options,
                    //_Out_ LPVOID ReplyBuffer,
                    nativeReplyBuffer,
                    //_In_ DWORD ReplySize,
                    fullReplyBufferSize,
                    //_In_ DWORD Timeout
                    timeoutInMs
                );

                TimeSpan duration = DateTime.Now - startTimestamp;

                var reply = (IcmpInterop.Reply)Marshal.PtrToStructure(nativeReplyBuffer, typeof(IcmpInterop.Reply)); // Parse the beginning of reply memory to reply struct

                byte[] replyBuffer = null;
                if (sendbuffer.Length != 0)
                {
                    replyBuffer = new byte[sendbuffer.Length];
                    Marshal.Copy(nativeReplyBuffer + nativeReplySize, replyBuffer, 0, sendbuffer.Length); //copy the rest of the reply memory to managed byte[]
                }

                // TODO: if the method returned an error, use GetLastError
                // and filter that back up...
                if (returnValue == 0)
                {
                    return new PingReply(
                        returnValue,
                        reply.Status,
                        new IPAddress(reply.Address),
                        duration);
                }
                else
                {
                    return new PingReply(
                        returnValue,
                        reply.Status,
                        new IPAddress(reply.Address),
                        reply.RoundTripTime,
                        replyBuffer);
                }
            }
            catch (Exception)
            {
                // TODO: report back the exception in some other fashion?
                return new PingReply(
                    // error
                    0,
                    // "Unknown"
                    -1,
                    // just return the original destination address
                    destAddress,
                    // no timestan
                    TimeSpan.Zero);
            }
            finally
            {
                Marshal.FreeHGlobal(nativeReplyBuffer); //free allocated space
            }
        }

        // Simple Interop layer class for PInvoking the Icmp functionality
        public static class IcmpInterop
        {
            public static IntPtr? GetIcmpHandle()
            {
                return IcmpCreateFile();
            }

            public static void ReleaseIcmpHandle(IntPtr? icmpHandle)
            {
                if (icmpHandle.HasValue)
                {
                    IcmpCloseHandle(icmpHandle.Value);
                }
            }

            internal static int ReplyMarshalSize
            {
                get
                {
                    return Marshal.SizeOf(typeof(Reply));
                }
            }

            [DllImport("Iphlpapi.dll", SetLastError = true)]
            private static extern IntPtr IcmpCreateFile();
            [DllImport("Iphlpapi.dll", SetLastError = true)]
            private static extern bool IcmpCloseHandle(IntPtr handle);
            [DllImport("Iphlpapi.dll", SetLastError = true)]
            internal static extern uint IcmpSendEcho2Ex(
                IntPtr icmpHandle,
                IntPtr Event,
                IntPtr apcroutine,
                IntPtr apccontext,
                UInt32 sourceAddress,
                UInt32 destinationAddress,
                byte[] requestData,
                short requestSize,
                ref Option requestOptions,
                IntPtr replyBuffer,
                int replySize,
                int timeout);

            // source: https://docs.microsoft.com/en-us/windows/win32/api/ipexport/ns-ipexport-ip_option_information
            //typedef struct ip_option_information
            //{
            //    UCHAR Ttl;
            //    UCHAR Tos;
            //    UCHAR Flags;
            //    UCHAR OptionsSize;
            //    PUCHAR OptionsData;
            //}
            //IP_OPTION_INFORMATION, *PIP_OPTION_INFORMATION;
            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
            internal struct Option
            {
                public byte Ttl;
                public readonly byte Tos;
                public byte Flags;
                public readonly byte OptionsSize;
                public readonly IntPtr OptionsData;
            }

            // source: https://docs.microsoft.com/en-us/windows/win32/api/ipexport/ns-ipexport-icmp_echo_reply
            //typedef struct icmp_echo_reply
            //{
            //    IPAddr Address;
            //    ULONG Status;
            //    ULONG RoundTripTime;
            //    USHORT DataSize;
            //    USHORT Reserved;
            //    PVOID Data;
            //    struct ip_option_information Options;
            //}
            //ICMP_ECHO_REPLY, *PICMP_ECHO_REPLY;
            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
            internal struct Reply
            {
                public readonly UInt32 Address;
                public readonly int Status;
                public readonly int RoundTripTime;
                public readonly short DataSize;
                public readonly short Reserved;
                public readonly IntPtr Data;
                public readonly Option Options;
            }
        }

        [Serializable]
        public class PingReply
        {
            private readonly byte[] _buffer = null;
            private readonly IPAddress _ipAddress = null;
            private readonly uint _returnValue = 0;
            private readonly TimeSpan _roundTripTime = TimeSpan.Zero;
            private readonly IPStatus _status = IPStatus.Unknown;
            private Win32Exception _exception;

            internal PingReply(uint returnValue, int replystatus, IPAddress ipAddress, TimeSpan duration)
            {
                _returnValue = returnValue;
                _ipAddress = ipAddress;
                if (Enum.IsDefined(typeof(IPStatus), replystatus))
                    _status = (IPStatus)replystatus;
            }

            internal PingReply(uint returnValue, int replystatus, IPAddress ipAddress, int roundTripTime, byte[] buffer)
            {
                _returnValue = returnValue;
                _ipAddress = ipAddress;
                _roundTripTime = TimeSpan.FromMilliseconds(roundTripTime);
                _buffer = buffer;
                if (Enum.IsDefined(typeof(IPStatus), replystatus))
                    _status = (IPStatus)replystatus;
            }

            public uint ReturnValue
            {
                get { return _returnValue; }
            }

            public IPStatus Status
            {
                get { return _status; }
            }

            public IPAddress SourceIpAddress
            {
                get { return _ipAddress; }
            }

            public byte[] Buffer
            {
                get { return _buffer; }
            }

            public TimeSpan RoundTripTime
            {
                get { return _roundTripTime; }
            }

            public Win32Exception Exception
            {
                get
                {
                    if (Status != IPStatus.Success)
                    {
                        return _exception ?? (_exception = new Win32Exception((int)ReturnValue, Status.ToString()));
                    }
                    else
                    {
                        return null;
                    }
                }
            }
        }
    }
}
