// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Threading.Tasks;

namespace XMAT.NetworkTrace.NTDE
{
    public interface INetworkTraceEngine
    {
        public Task GetAllEventsAsync();
        public Task StartPacketTraceAsync();
        public Task StopPacketTraceAsync();

        public event EventHandler<string> EventRecordAvailable;
    }
}
