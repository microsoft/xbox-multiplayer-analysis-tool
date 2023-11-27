// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XMAT.NetworkTrace.NTDE;
using XMAT.SharedInterfaces;
using System;

namespace XMAT.NetworkTrace
{
    public class NetworkTraceCaptureMethod : ICaptureMethod
    {
        public static ICaptureMethod Method { get { return MethodInstance; } }

        internal static readonly NetworkTraceCaptureMethod MethodInstance = new NetworkTraceCaptureMethod();

        internal ICaptureAppModel CaptureAppModel { get; private set; }

        internal IDataTable NetworkTracePacketsTable { get; private set; }

        // TODO: implement me!
        public ICaptureMethodParameters PreferencesModel { get { return null; } }

        public void Initialize(ICaptureAppModel appModel)
        {
            CaptureAppModel = appModel;

            InitializeDataTables();
        }

        public void Shutdown()
        {
        }

        public bool OwnsDataTable(string tableName)
        {
            return !string.IsNullOrEmpty(tableName) && tableName.Equals(@"NetworkTracePackets");
        }

        private void InitializeDataTables()
        {
            // { "event": { "processId": 4, "threadId": 308, "timestamp": "2021-3-19T20:46:54.374Z" }, "packet": { "mediaType": "ethernet", "miniportIfIndex":4, "lowerIfIndex":4, "flags": [ "start", "end", "receive" ], "data": "////////xJ3tqEMCCABFAABOT/wAAIARZlDAqAEDwKgB/wCJAIkAOvm7/+ABEAABAAAAAAAAIEVKRUlGRUVQRUtFRUVPRUtGQkNBQ0FDQUNBQ0FDQUFBAAAgAAE=255" } }

            NetworkTracePacketsTable = CaptureAppModel.ActiveDatabase.CreateTable(
                @"NetworkTracePackets",
                new Field<string>(@"DeviceName"),
                new Field<Int32>(@"ProcessId"),
                new Field<Int32>(@"ThreadId"),
                new Field<string>(@"Timestamp"),
                new Field<string>(@"MediaType"),
                new Field<Int32>(@"StartPacket"),
                new Field<Int32>(@"EndPacket"),
                new Field<Int32>(@"Fragment"),
                new Field<Int32>(@"Send"),
                new Field<Int32>(@"Receive"),
                new Field<string>(@"Payload")
            );

            if (NetworkTracePacketsTable == null)
            {
                throw new Exception("failed to create network trace packets table.");
            }
        }
    }
}
