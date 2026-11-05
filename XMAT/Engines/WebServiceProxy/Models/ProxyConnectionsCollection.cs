// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using XMAT.Operations.Captures;
using XMAT.Operations.Processes;
using XMAT.SharedInterfaces;

namespace XMAT.WebServiceCapture.Models
{
    public class ProxyConnectionsCollection : ObservableCollection<ProxyConnectionModel>
    {
        private readonly object _lockObj = new object();

        public ProxyConnectionModel ById(int requestId)
        {
            lock (_lockObj)
            {
                return _requestToConnections.TryGetValue(requestId, out List<ProxyConnectionModel> models) &&
                    models.Count == 1 ? models[0] : null;
            }
        }

        public ProxyConnectionModel ByKey(CaptureRowKey key)
        {
            lock (_lockObj)
            {
                _keyToConnection.TryGetValue(key, out ProxyConnectionModel model);
                return model;
            }
        }

        internal void AssignRowKey(ProxyConnectionModel model, CaptureRowKey key)
        {
            ArgumentNullException.ThrowIfNull(model);
            lock (_lockObj)
            {
                if (model.RowKey.SourceId != Guid.Empty)
                {
                    _keyToConnection.Remove(model.RowKey);
                }
                model.RowKey = key;
                _keyToConnection[key] = model;
            }
        }

        internal ProxyConnectionModel AddFromDataRecord(
            IDataRecord dataRecord,
            bool isReadonly,
            CaptureRowKey rowKey = default)
        {
            lock (_lockObj)
            {
                if (dataRecord == null)
                {
                    throw new ArgumentNullException(nameof(dataRecord));
                }

                int requestNumber = (int)dataRecord.Int(WebServiceCaptureMethod.FieldKey_RequestNumber);
                int connectionId = (int)dataRecord.Int(WebServiceCaptureMethod.FieldKey_ConnectionId);

                if (rowKey.SourceId != Guid.Empty &&
                    _keyToConnection.TryGetValue(rowKey, out ProxyConnectionModel existing))
                {
                    existing.UpdateFromDataRecord(dataRecord);
                    MarkImported(existing, isReadonly);
                    return existing;
                }
                var newConnectionModel = new ProxyConnectionModel(dataRecord.RowId, requestNumber, connectionId);
                newConnectionModel.RowKey = rowKey;
                newConnectionModel.UpdateFromDataRecord(dataRecord);
                MarkImported(newConnectionModel, isReadonly);

                Add(newConnectionModel);
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
                if (!_requestToConnections.TryGetValue(requestNumber, out List<ProxyConnectionModel> models))
                {
                    return null;
                }
                ProxyConnectionModel model = models.FirstOrDefault(
                    candidate => candidate.RecordRowId == dataRecord.RowId);
                model?.UpdateFromDataRecord(dataRecord);
                return model;
            }
        }

        internal ProxyConnectionModel UpdateFromDataRecord(CaptureRowKey key, IDataRecord dataRecord)
        {
            lock (_lockObj)
            {
                if (dataRecord == null)
                {
                    throw new ArgumentNullException(nameof(dataRecord));
                }
                if (!_keyToConnection.TryGetValue(key, out ProxyConnectionModel model))
                {
                    return null;
                }
                model.UpdateFromDataRecord(dataRecord);
                return model;
            }
        }

        internal void RemoveAll()
        {
            Clear();
        }

        internal int RemoveByRowKeys(
            IReadOnlySet<CaptureRowKey> keys)
        {
            ArgumentNullException.ThrowIfNull(keys);
            lock (_lockObj)
            {
                ProxyConnectionModel[] survivors =
                    this.Where(
                        model =>
                            !keys.Contains(
                                model.RowKey))
                    .ToArray();
                int removed =
                    Count - survivors.Length;
                if (removed == 0)
                {
                    return 0;
                }
                CheckReentrancy();
                Items.Clear();
                _requestToConnections.Clear();
                _keyToConnection.Clear();
                foreach (
                    ProxyConnectionModel survivor in
                    survivors)
                {
                    Items.Add(survivor);
                    Index(survivor);
                }
                OnPropertyChanged(
                    new PropertyChangedEventArgs(
                        nameof(Count)));
                OnPropertyChanged(
                    new PropertyChangedEventArgs(
                        "Item[]"));
                OnCollectionChanged(
                    new NotifyCollectionChangedEventArgs(
                        NotifyCollectionChangedAction
                            .Reset));
                return removed;
            }
        }

        protected override void InsertItem(int index, ProxyConnectionModel item)
        {
            ArgumentNullException.ThrowIfNull(item);
            lock (_lockObj)
            {
                CheckReentrancy();
                Index(item);
                try
                {
                    base.InsertItem(index, item);
                }
                catch
                {
                    if (!Contains(item))
                    {
                        Unindex(item);
                    }
                    throw;
                }
            }
        }

        protected override void SetItem(int index, ProxyConnectionModel item)
        {
            ArgumentNullException.ThrowIfNull(item);
            lock (_lockObj)
            {
                CheckReentrancy();
                ProxyConnectionModel previous = this[index];
                Unindex(previous);
                var indexed = false;
                try
                {
                    Index(item);
                    indexed = true;
                    base.SetItem(index, item);
                }
                catch
                {
                    if (index >= Count || !ReferenceEquals(this[index], item))
                    {
                        if (indexed)
                        {
                            Unindex(item);
                        }
                        Index(previous);
                    }
                    throw;
                }
            }
        }

        protected override void RemoveItem(int index)
        {
            lock (_lockObj)
            {
                CheckReentrancy();
                ProxyConnectionModel item = this[index];
                Unindex(item);
                base.RemoveItem(index);
            }
        }

        protected override void ClearItems()
        {
            lock (_lockObj)
            {
                CheckReentrancy();
                _requestToConnections.Clear();
                _keyToConnection.Clear();
                base.ClearItems();
            }
        }

        private void Index(ProxyConnectionModel model)
        {
            if (model.RowKey.SourceId != Guid.Empty &&
                _keyToConnection.TryGetValue(model.RowKey, out ProxyConnectionModel existing) &&
                !ReferenceEquals(existing, model))
            {
                throw new InvalidOperationException("The capture row key is already indexed.");
            }
            if (model.RequestNumber >= int.MinValue && model.RequestNumber <= int.MaxValue)
            {
                int requestNumber = (int)model.RequestNumber;
                if (!_requestToConnections.TryGetValue(requestNumber, out List<ProxyConnectionModel> models))
                {
                    models = new List<ProxyConnectionModel>();
                    _requestToConnections.Add(requestNumber, models);
                }
                models.Add(model);
            }
            if (model.RowKey.SourceId != Guid.Empty)
            {
                _keyToConnection[model.RowKey] = model;
            }
        }

        private void Unindex(ProxyConnectionModel model)
        {
            if (model.RequestNumber >= int.MinValue && model.RequestNumber <= int.MaxValue)
            {
                int requestNumber = (int)model.RequestNumber;
                if (_requestToConnections.TryGetValue(requestNumber, out List<ProxyConnectionModel> models))
                {
                    models.Remove(model);
                    if (models.Count == 0)
                    {
                        _requestToConnections.Remove(requestNumber);
                    }
                }
            }
            if (model.RowKey.SourceId != Guid.Empty)
            {
                _keyToConnection.Remove(model.RowKey);
            }
        }

        private static void MarkImported(ProxyConnectionModel model, bool isReadonly)
        {
            if (isReadonly && model.ClientProcess.Status == ClientProcessStatus.Local)
            {
                model.SetClientProcess(
                    model.ClientProcess with
                    {
                        Status = ClientProcessStatus.Imported,
                    });
            }
        }

        private readonly Dictionary<int, List<ProxyConnectionModel>> _requestToConnections = new();
        private readonly Dictionary<CaptureRowKey, ProxyConnectionModel> _keyToConnection = new();
    }
}
