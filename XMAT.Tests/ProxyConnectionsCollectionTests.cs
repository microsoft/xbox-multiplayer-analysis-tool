// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using XMAT.DataAbstractionLayer;
using XMAT.Operations.Captures;
using XMAT.Operations.Processes;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests
{
    public sealed class ProxyConnectionsCollectionTests
    {
        [Fact]
        public void RowKeyIsPrimaryAndLegacyRequestLookupRejectsAmbiguity()
        {
            var source = Guid.NewGuid();
            var firstKey = new CaptureRowKey(source, 1, 1, 1);
            var secondKey = new CaptureRowKey(source, 2, 1, 2);
            var connections = new ProxyConnectionsCollection();
            ProxyConnectionModel first = connections.AddFromDataRecord(
                new Record(rowId: 1, requestNumber: 7, connectionId: 1000),
                false,
                firstKey);
            ProxyConnectionModel second = connections.AddFromDataRecord(
                new Record(rowId: 1, requestNumber: 7, connectionId: 1001),
                false,
                secondKey);

            Assert.Same(first, connections.ByKey(firstKey));
            Assert.Same(second, connections.ByKey(secondKey));
            Assert.Null(connections.ById(7));
        }

        [Fact]
        public void MissingOrRetiredUpdateDoesNotThrowOrChangeAnotherModel()
        {
            var connections = new ProxyConnectionsCollection();
            var key = new CaptureRowKey(Guid.NewGuid(), 1, 1, 1);
            ProxyConnectionModel model = connections.AddFromDataRecord(
                new Record(rowId: 1, requestNumber: 8, connectionId: 1000),
                false,
                key);

            ProxyConnectionModel updated = connections.UpdateFromDataRecord(
                new CaptureRowKey(Guid.NewGuid(), 2, 1, 2),
                new Record(rowId: 1, requestNumber: 8, connectionId: 1000, status: "500"));

            Assert.Null(updated);
            Assert.Equal("200", model.Status);
        }

        [Fact]
        public void SetClientProcessPublishesIdentityAndResolvingState()
        {
            var model = new ProxyConnectionModel(new Record(rowId: 1, requestNumber: 9, connectionId: 1000));
            var changed = new List<string>();
            model.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
            var process = new ClientProcessInfo("game.exe", 123, null, ClientProcessStatus.Local);

            model.SetClientProcess(process, resolving: true);

            Assert.Equal(process, model.ClientProcess);
            Assert.True(model.IsProcessResolving);
            Assert.Contains(nameof(ProxyConnectionModel.ClientProcess), changed);
            Assert.Contains(nameof(ProxyConnectionModel.IsProcessResolving), changed);
        }

        [Fact]
        public void UpdateBeforeAddSnapshotCannotDowngradeResolvedProcess()
        {
            var connections = new ProxyConnectionsCollection();
            var key = new CaptureRowKey(Guid.NewGuid(), 1, 10, 10);
            ClientProcessInfo resolved = new("latest.exe", 210, null, ClientProcessStatus.Local);
            ProxyConnectionModel model = connections.AddFromDataRecord(
                new Record(rowId: 10, requestNumber: 10, connectionId: 1000, process: resolved),
                false,
                key);

            _ = connections.AddFromDataRecord(
                new Record(rowId: 10, requestNumber: 10, connectionId: 1000, process: ClientProcessInfo.NotRecorded),
                false,
                key);

            Assert.Equal(resolved, model.ClientProcess);
            Assert.False(model.IsProcessResolving);
        }

        [Fact]
        public void InheritedInsertIndexesRowKeyAndRequestNumber()
        {
            var connections = new ProxyConnectionsCollection();
            var key = new CaptureRowKey(Guid.NewGuid(), 1, 11, 11);
            var model =
                new ProxyConnectionModel(
                    new Record(
                        rowId: 11,
                        requestNumber: 11,
                        connectionId: 1000))
                {
                    RowKey = key,
                };

            connections.Insert(0, model);

            Assert.Same(model, connections.ByKey(key));
            Assert.Same(model, connections.ById(11));
        }

        [Fact]
        public void InheritedRemoveAndClearRemoveEveryIndex()
        {
            var connections = new ProxyConnectionsCollection();
            var firstKey = new CaptureRowKey(Guid.NewGuid(), 1, 12, 12);
            var secondKey = new CaptureRowKey(firstKey.SourceId, 1, 13, 13);
            ProxyConnectionModel first = connections.AddFromDataRecord(
                new Record(rowId: 12, requestNumber: 12, connectionId: 1000),
                false,
                firstKey);
            _ = connections.AddFromDataRecord(
                new Record(rowId: 13, requestNumber: 13, connectionId: 1001),
                false,
                secondKey);

            Assert.True(connections.Remove(first));
            Assert.Null(connections.ByKey(firstKey));
            Assert.Null(connections.ById(12));

            connections.Clear();
            Assert.Null(connections.ByKey(secondKey));
            Assert.Null(connections.ById(13));
        }

        [Fact]
        public void InheritedReplaceRemovesOldIndexesAndAddsNewIndexes()
        {
            var connections = new ProxyConnectionsCollection();
            var source = Guid.NewGuid();
            var oldKey = new CaptureRowKey(source, 1, 14, 14);
            var newKey = new CaptureRowKey(source, 1, 15, 15);
            _ = connections.AddFromDataRecord(
                new Record(rowId: 14, requestNumber: 14, connectionId: 1000),
                false,
                oldKey);
            var replacement =
                new ProxyConnectionModel(
                    new Record(
                        rowId: 15,
                        requestNumber: 15,
                        connectionId: 1001))
                {
                    RowKey = newKey,
                };

            connections[0] = replacement;

            Assert.Null(connections.ByKey(oldKey));
            Assert.Null(connections.ById(14));
            Assert.Same(replacement, connections.ByKey(newKey));
            Assert.Same(replacement, connections.ById(15));
        }

        [Fact]
        public void BatchRemove_PublishesOneResetAndKeepsSurvivorObjects()
        {
            var connections = new ProxyConnectionsCollection();
            Guid source = Guid.NewGuid();
            var firstKey = new CaptureRowKey(source, 1, 21, 21);
            var deletedKey = new CaptureRowKey(source, 1, 22, 22);
            var lastKey = new CaptureRowKey(source, 1, 23, 23);
            ProxyConnectionModel first = connections.AddFromDataRecord(
                new Record(21, 21, 1000), false, firstKey);
            _ = connections.AddFromDataRecord(new Record(22, 22, 1001), false, deletedKey);
            ProxyConnectionModel last = connections.AddFromDataRecord(
                new Record(23, 23, 1002), false, lastKey);
            var changes = new List<NotifyCollectionChangedAction>();
            connections.CollectionChanged += (_, args) => changes.Add(args.Action);

            int removed = connections.RemoveByRowKeys(new HashSet<CaptureRowKey> { deletedKey });

            Assert.Equal(1, removed);
            Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, changes);
            Assert.Collection(
                connections,
                item => Assert.Same(first, item),
                item => Assert.Same(last, item));
            Assert.Null(connections.ByKey(deletedKey));
            Assert.Same(first, connections.ByKey(firstKey));
            Assert.Same(last, connections.ByKey(lastKey));

            Assert.Equal(
                0,
                connections.RemoveByRowKeys(new HashSet<CaptureRowKey> { new(source, 1, 99, 99), }));
            Assert.Single(changes);
        }

        [Fact]
        public void BatchRemove_ReleasesDeletedModelAndBodies()
        {
            var connections = new ProxyConnectionsCollection();
            (WeakReference<ProxyConnectionModel> Model, WeakReference<byte[]> Body) references =
                AddAndBatchRemoveBody(connections);

            ForceCollection();

            Assert.False(references.Model.TryGetTarget(out _));
            Assert.False(references.Body.TryGetTarget(out _));
        }

        [Fact]
        public void InheritedRemoveReleasesTheRemovedModel()
        {
            var connections = new ProxyConnectionsCollection();
            WeakReference<ProxyConnectionModel> reference = AddAndRemove(connections);

            ForceCollection();

            Assert.False(reference.TryGetTarget(out _));
        }

        private sealed class Record(int rowId, long requestNumber, long connectionId, string status = "200",
            ClientProcessInfo process = null, string requestBody = null) : IDataRecord,
            IOptionalTypedDataRecord
        {
            public int RowId { get; } = rowId;

            public long Int(string fieldName) =>
                fieldName switch
                {
                    WebServiceCaptureMethod.FieldKey_RequestNumber => requestNumber,
                    WebServiceCaptureMethod.FieldKey_ConnectionId => connectionId,
                    _ => 0,
                };

            public string Str(string fieldName) =>
                fieldName switch
                {
                    WebServiceCaptureMethod.FieldKey_DeviceName => "device",
                    WebServiceCaptureMethod.FieldKey_RequestTimestamp => "2026-11-03T20:00:00.0000000Z",
                    WebServiceCaptureMethod.FieldKey_ResponseTimestamp => "2026-11-03T20:00:00.0100000Z",
                    WebServiceCaptureMethod.FieldKey_RequestScheme => "https",
                    WebServiceCaptureMethod.FieldKey_RequestHost => "example.test",
                    WebServiceCaptureMethod.FieldKey_RequestPort => "443",
                    WebServiceCaptureMethod.FieldKey_RequestMethod => "GET",
                    WebServiceCaptureMethod.FieldKey_RequestPath => "/",
                    WebServiceCaptureMethod.FieldKey_RequestStatus => status,
                    WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders => "GET / HTTP/1.1",
                    WebServiceCaptureMethod.FieldKey_RequestBody => requestBody ?? string.Empty,
                    WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders => "HTTP/1.1 200 OK",
                    WebServiceCaptureMethod.FieldKey_ResponseBody => string.Empty,
                    WebServiceCaptureMethod.FieldKey_ClientIP => "device",
                    _ => string.Empty,
                };

            public bool TryReadString(string name, out string value)
            {
                if (process == null)
                {
                    value = null;
                    return false;
                }
                value =
                    name switch
                    {
                        WebProxyCaptureSchema.ClientProcessName => process.Name,
                        WebProxyCaptureSchema.ClientProcessStartTime => process.StartTimeUtc? .ToString("o",
                            System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                        WebProxyCaptureSchema.ClientProcessStatus => ClientProcessRecordCodec.StatusToken(
                            process.Status),
                        _ => null,
                    };
                return value != null;
            }

            public bool TryReadInt64(string name, out long value)
            {
                if (process != null && name == WebProxyCaptureSchema.ClientProcessId)
                {
                    value = process.ProcessId;
                    return true;
                }
                value = 0;
                return false;
            }
        }

        [MethodImpl(
            MethodImplOptions.NoInlining)]
        private static WeakReference<ProxyConnectionModel> AddAndRemove(ProxyConnectionsCollection connections)
        {
            var key = new CaptureRowKey(Guid.NewGuid(), 1, 16, 16);
            ProxyConnectionModel model = connections.AddFromDataRecord(
                new Record(rowId: 16, requestNumber: 16, connectionId: 1000),
                false,
                key);
            var reference = new WeakReference<ProxyConnectionModel>(model);
            _ = connections.Remove(model);
            return reference;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static (WeakReference<ProxyConnectionModel> Model, WeakReference<byte[]> Body)
            AddAndBatchRemoveBody(ProxyConnectionsCollection connections)
        {
            var key = new CaptureRowKey(Guid.NewGuid(), 1, 17, 17);
            ProxyConnectionModel model = connections.AddFromDataRecord(
                new Record(
                    rowId: 17,
                    requestNumber: 17,
                    connectionId: 1000,
                    requestBody: Convert.ToBase64String(new byte[64 * 1024])),
                false,
                key);
            var references = (
                new WeakReference<ProxyConnectionModel>(model),
                new WeakReference<byte[]>(model.RequestBody));
            Assert.Equal(1, connections.RemoveByRowKeys(new HashSet<CaptureRowKey> { key }));
            return references;
        }

        private static void ForceCollection()
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
    }
}
