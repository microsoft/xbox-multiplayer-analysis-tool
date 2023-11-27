// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.ComponentModel;
using System.Diagnostics;
using XMAT.SharedInterfaces;

namespace XMAT.NetworkTrace.Models
{
    public enum NetworkMediaType
    {
        Unknown,
        Ethernet,
        Wireless,
        Tunnel,
        WiFi
    }

    [Flags]
    public enum NetworkPacketFlags
    {
        None = 0x00,
        StartOfPacket = 0x01,
        EndOfPacket = 0x02,
        Fragment = 0x04,
        Send = 0x08,
        Receive = 0x10
    }

    public enum NetworkProtocol
    {
        Unknown = -1,
        HOPOPT = 0,
        ICMP = 1,
        IGMP = 2,
        GGP = 3,
        TCP = 6,
        UDP = 17,
        HMP = 20,
        GRE = 47,
        DSR = 48,
        PIPE = 131,
        RAW = 255
    }

    public class NetworkTracePacketDataModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public Int64 RecordRowId { get; private set; }
        public Int64 ProcessId { get; private set; }
        public Int64 ThreadId { get; private set; }
        public DateTime Timestamp { get; private set; }
        public NetworkMediaType MediaType { get; private set; }
        public NetworkPacketFlags Flags { get; private set; }
        public byte[] Payload { get; private set; }
        public int Protocol { get; internal set; }

        internal NetworkTracePacketDataModel(IDataRecord dataRecord)
        {
            UpdateFromDataRecord(dataRecord);
        }

        internal void UpdateFromDataRecord(IDataRecord dataRecord)
        {
            var intVal = dataRecord.Int(@"ProcessId");
            if (intVal != default(Int64))
            {
                ProcessId = intVal;
            }
            else
            {
                ProcessId = 0;
            }

            intVal = dataRecord.Int(@"ThreadId");
            if (intVal != default(Int64))
            {
                ThreadId = intVal;
            }
            else
            {
                ThreadId = 0;
            }

            Flags = NetworkPacketFlags.None;

            intVal = dataRecord.Int(@"StartPacket");
            if (intVal != default(Int64))
            {
                Flags |= NetworkPacketFlags.StartOfPacket;
            }

            intVal = dataRecord.Int(@"EndPacket");
            if (intVal != default(Int64))
            {
                Flags |= NetworkPacketFlags.EndOfPacket;
            }

            intVal = dataRecord.Int(@"Fragment");
            if (intVal != default(Int64))
            {
                Flags |= NetworkPacketFlags.Fragment;
            }

            intVal = dataRecord.Int(@"Send");
            if (intVal != default(Int64))
            {
                Flags |= NetworkPacketFlags.Send;
            }

            intVal = dataRecord.Int(@"Receive");
            if (intVal != default(Int64))
            {
                Flags |= NetworkPacketFlags.Receive;
            }

            var strVal = dataRecord.Str(@"Timestamp");
            if (!string.IsNullOrEmpty(strVal))
            {
                Timestamp = DateTime.Parse(strVal);
            }

            strVal = dataRecord.Str(@"MediaType");
            if (!string.IsNullOrEmpty(strVal))
            {
                switch(strVal)
                {
                    case "ethernet": MediaType = NetworkMediaType.Ethernet; break;
                    case "wireless": MediaType = NetworkMediaType.Wireless; break;
                    case "tunnel": MediaType = NetworkMediaType.Tunnel; break;
                    case "wifi": MediaType = NetworkMediaType.WiFi; break;
                    default: MediaType = NetworkMediaType.Unknown; break;
                }
            }

            strVal = dataRecord.Str(@"Payload");
            if (!string.IsNullOrEmpty(strVal))
            {
                Payload = System.Convert.FromBase64String(strVal);
            }

            ParseIpPacketHeader();

            Protocol = RawIpHeader.Protocol;

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }

        public struct IpHeader
        {
            public int Version;
            public int IHL;
            public int TOS;
            public int TotalLength;
            public int Identification;
            public int Flags;
            public int FragmentOffset;
            public int TTL;
            public int Protocol;
            public int Checksum;
            public int SourceAddress;
            public int DestinationAddress;
        }

        public string SourceIpv4Address { get; internal set; }
        public string SourceMacAddress { get; internal set; }
        public string DestinationIpv4Address { get; internal set; }
        public string DestinationMacAddress { get; internal set; }

        public IpHeader RawIpHeader { get; internal set; }

        public readonly int minimumPacketLength = 34;

        internal void ParseIpPacketHeader()
        {
            var header = new IpHeader();

            // Try to find an IP header in the payload
            var s = FindIPHeader(Payload);

            // If a header was found the index return is the start of
            // the IP Header, but we also want the MAC addresses from the
            // IP frame, so we need to move back the size of the offset.
            // However, it's possible for packets to not be IP so we let it
            // attempt to parse it "as-is" an not adjust the offset.
            if (s >= 14)
            {
                s -= 14;
            }

            if (Payload.Length - s >= minimumPacketLength)
            {
                DestinationMacAddress =
                    $"{Payload[s + 0]:X02}." +
                    $"{Payload[s + 1]:X02}." +
                    $"{Payload[s + 2]:X02}." +
                    $"{Payload[s + 3]:X02}." +
                    $"{Payload[s + 4]:X02}." +
                    $"{Payload[s + 5]:X02}";

                SourceMacAddress =
                    $"{Payload[s + 6]:X02}." +
                    $"{Payload[s + 7]:X02}." +
                    $"{Payload[s + 8]:X02}." +
                    $"{Payload[s + 9]:X02}." +
                    $"{Payload[s + 10]:X02}." +
                    $"{Payload[s + 11]:X02}";

                header.Version = (Payload[s + 14] & 0xF0) >> 4;
                header.IHL = Payload[s + 14] & 0x0F;
                header.TOS = Payload[s + 15];
                header.TotalLength = (Payload[s + 16] << 8) + Payload[s + 17];
                header.Identification = (Payload[s + 18] << 8) + Payload[s + 19];
                header.Flags = (Payload[s + 20] & 0xE0) >> 5;
                header.FragmentOffset = ((Payload[s + 20] & 0x1F) << 8) + Payload[s + 21];
                header.TTL = Payload[s + 22];
                header.Protocol = Payload[s + 23];
                header.Checksum = (Payload[s + 24] << 8) + Payload[s + 25];
                header.SourceAddress = (Payload[s + 26] << 24) + (Payload[s + 27] << 16) + (Payload[s + 28] << 8) + Payload[s + 29];
                header.DestinationAddress = (Payload[s + 30] << 24) + (Payload[s + 31] << 16) + (Payload[s + 32] << 8) + Payload[s + 33];

                SourceIpv4Address = $"{Payload[s + 26]}.{Payload[s + 27]}.{Payload[s + 28]}.{Payload[s + 29]}";
                DestinationIpv4Address = $"{Payload[s + 30]}.{Payload[s + 31]}.{Payload[s + 32]}.{Payload[s + 33]}";
            }
            else
            {
                PublicUtilities.AppLog(LogLevel.INFO, $"Packet with payload length {Payload.Length} found, not parsing header.");
            }

            RawIpHeader = header;
        }

        // This function will scan through an ethernet frame looking
        // for a valid IP header.  It does this by scanning for the packet
        // type of IP (0x45) and then calculates the header checksum.  If
        // a valid checksum is found, it is assumed to be an IP packet.
        private int FindIPHeader(byte[] frag)
        {
            int best = 0;

            // It takes 20 bytes to run the checksum so bound
            // the search by that
            for (int i = 0; i < frag.Length - 20; i++)
            {
                // Look for the IP packet type marker
                if (frag[i] == 0x45)
                {
                    uint checksum = 0;

                    // Calculate the header checksum
                    for (int j = 0; j < 20; j++)
                    {
                        checksum += (j % 2 == 0) ? (uint)(frag[i + j] << 8) : frag[i + j];
                    }

                    checksum += checksum >> 16;
                    if ((ushort)checksum == 0xFFFF)
                    {
                        // Valid checksum found
                        return i;
                    }

                    if (best == 0 && (frag[i + 10] << 8) + frag[i + 11] == 0)
                    {
                        // We found what has an IP header marker but the
                        // checksum is zero which is *possibly* valid.  This
                        // will be returned if a better match isn't found.
                        best = i;
                    }
                }
            }

            return best;
        }
    }
}
