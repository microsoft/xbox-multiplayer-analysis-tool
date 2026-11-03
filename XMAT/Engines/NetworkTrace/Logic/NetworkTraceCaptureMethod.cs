// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using XMAT.SharedInterfaces;

namespace XMAT.NetworkTrace
{
    public class NetworkTraceCaptureMethod : ICaptureMethod
    {
        public const string TableName = "NetworkTracePackets";
        public const string FieldKey_DeviceName = "DeviceName";
        public const string FieldKey_ProcessId = "ProcessId";
        public const string FieldKey_ThreadId = "ThreadId";
        public const string FieldKey_Timestamp = "Timestamp";
        public const string FieldKey_MediaType = "MediaType";
        public const string FieldKey_StartPacket = "StartPacket";
        public const string FieldKey_EndPacket = "EndPacket";
        public const string FieldKey_Fragment = "Fragment";
        public const string FieldKey_Send = "Send";
        public const string FieldKey_Receive = "Receive";
        public const string FieldKey_Payload = "Payload";
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
            return !string.IsNullOrEmpty(tableName) && tableName.Equals(TableName);
        }

        private void InitializeDataTables()
        {
            // { "event": { "processId": 4, "threadId": 308, "timestamp": "2021-3-19T20:46:54.374Z" }, "packet": { "mediaType": "ethernet", "miniportIfIndex":4, "lowerIfIndex":4, "flags": [ "start", "end", "receive" ], "data": "////////xJ3tqEMCCABFAABOT/wAAIARZlDAqAEDwKgB/wCJAIkAOvm7/+ABEAABAAAAAAAAIEVKRUlGRUVQRUtFRUVPRUtGQkNBQ0FDQUNBQ0FDQUFBAAAgAAE=255" } }

            NetworkTracePacketsTable = CaptureAppModel.ActiveDatabase.CreateTable(
                TableName,
                new Field<string>(FieldKey_DeviceName),
                new Field<Int32>(FieldKey_ProcessId),
                new Field<Int32>(FieldKey_ThreadId),
                new Field<string>(FieldKey_Timestamp),
                new Field<string>(FieldKey_MediaType),
                new Field<Int32>(FieldKey_StartPacket),
                new Field<Int32>(FieldKey_EndPacket),
                new Field<Int32>(FieldKey_Fragment),
                new Field<Int32>(FieldKey_Send),
                new Field<Int32>(FieldKey_Receive),
                new Field<string>(FieldKey_Payload)
            );

            if (NetworkTracePacketsTable == null)
            {
                throw new Exception("failed to create network trace packets table.");
            }
        }
    }
}
