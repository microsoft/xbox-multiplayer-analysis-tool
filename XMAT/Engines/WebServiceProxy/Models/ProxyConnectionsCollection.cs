// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XMAT.SharedInterfaces;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace XMAT.WebServiceCapture.Models
{
    public class ProxyConnectionsCollection : ObservableCollection<ProxyConnectionModel>
    {
        private readonly object _lockObj = new object();

        public ProxyConnectionModel ById(int requestId)
        {
            ProxyConnectionModel model;
            _requestToConnection.TryGetValue(requestId, out model);
            return model;
        }

        internal ProxyConnectionModel AddFromDataRecord(IDataRecord dataRecord, bool isReadonly)
        {
            lock (_lockObj)
            {
                if (dataRecord == null)
                {
                    throw new ArgumentNullException(nameof(dataRecord));
                }

                int requestNumber = (int)dataRecord.Int(WebServiceCaptureMethod.FieldKey_RequestNumber);
                int connectionId = (int)dataRecord.Int(WebServiceCaptureMethod.FieldKey_ConnectionId);

                var newConnectionModel = new ProxyConnectionModel(dataRecord.RowId, requestNumber, connectionId);
                newConnectionModel.UpdateFromDataRecord(dataRecord);

                this.Add(newConnectionModel);

                _requestToConnection[requestNumber] = newConnectionModel;
                return newConnectionModel;
            }
        }

        internal ProxyConnectionModel UpdateFromDataRecord(IDataRecord dataRecord)
        {
            lock (_lockObj)
            {
                if (dataRecord == null)
                {
                    throw new ArgumentNullException(nameof(dataRecord));
                }

                int requestNumber = (int)dataRecord.Int(WebServiceCaptureMethod.FieldKey_RequestNumber);
                int connectionId = (int)dataRecord.Int(WebServiceCaptureMethod.FieldKey_ConnectionId);

                _requestToConnection[requestNumber].UpdateFromDataRecord(dataRecord);
                return _requestToConnection[requestNumber];
            }
        }

        internal void RemoveAll()
        {
            lock (_lockObj)
            {
                this.Clear();
                _requestToConnection.Clear();
            }
        }

        private readonly Dictionary<int, ProxyConnectionModel> _requestToConnection = new Dictionary<int, ProxyConnectionModel>();
    }
}
