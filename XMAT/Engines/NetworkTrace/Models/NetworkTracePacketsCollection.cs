// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XMAT.SharedInterfaces;
using System;
using System.Collections.ObjectModel;

namespace XMAT.NetworkTrace.Models
{
    public class NetworkTracePacketsCollection : ObservableCollection<NetworkTracePacketDataModel>
    {
        private readonly object _lockObj = new object();

        internal NetworkTracePacketDataModel AddFromDataRecord(IDataRecord dataRecord)
        {
            lock (_lockObj)
            {
                if (dataRecord == null)
                {
                    throw new ArgumentNullException(nameof(dataRecord));
                }

                var newConnectionModel = new NetworkTracePacketDataModel(dataRecord);

                this.Add(newConnectionModel);

                return newConnectionModel;
            }
        }

        internal void RemoveAll()
        {
            lock (_lockObj)
            {
                this.Clear();
            }
        }
    }
}
