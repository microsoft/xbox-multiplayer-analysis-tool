// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.DataAbstractionLayer;
using XMAT.SharedInterfaces;

namespace XMAT.Tests.Operations.Export
{
    internal enum ExportReadRoute { DataRecords, DataRecordsFrom, DataRecordsFromLimited, DataRecordsWhen }

    internal sealed record ExportReadRequest(bool IsMetadata, ExportReadRoute Route, int? Limit)
    {
        internal int Materialized { get; set; }
    }

    // Forwards the real DAL while recording every requested and materialized record.
    internal sealed class ExportReadProbe : IDataTable, IVersionedCaptureTable
    {
        private readonly IDataTable _inner;
        private readonly IVersionedCaptureTable _versioned;
        private readonly List<ExportReadRequest> _reads = new();

        internal ExportReadProbe(IDataTable inner)
        {
            _inner = inner;
            _versioned = (IVersionedCaptureTable)inner;
        }

        internal IReadOnlyList<ExportReadRequest> Reads => _reads.ToArray();
        internal Exception ColumnsFailure { get; set; }
        internal Exception MetadataReadFailure { get; set; }
        internal Action<bool> DatasetDisposed { get; set; }
        internal void Reset() => _reads.Clear();

        public event Action<IDataTable, long> RowAdded
        { add => _inner.RowAdded += value; remove => _inner.RowAdded -= value; }
        public event Action<IDataTable, long> RowUpdated
        { add => _inner.RowUpdated += value; remove => _inner.RowUpdated -= value; }
        public event Action<IDataTable, long> RowsRemoved
        { add => _inner.RowsRemoved += value; remove => _inner.RowsRemoved -= value; }

        public string Name => _inner.Name;
        public IDataset Dataset => new ObservedDataset(this, _inner.Dataset, isMetadata: false);
        public IDataset Subset(params string[] fieldNames) =>
            new ObservedDataset(this, _inner.Subset(fieldNames), isMetadata: true);
        public long AddRow(params object[] fieldValueArgs) => _inner.AddRow(fieldValueArgs);
        public void UpdateRow(long rowId, params IFieldValue[] values) => _inner.UpdateRow(rowId, values);
        public void RemoveRowsWhere(params IFieldValue[] values) => _inner.RemoveRowsWhere(values);
        public IDisposable EnterRead() => _versioned.EnterRead();
        public long VersionOf(long rowId) => _versioned.VersionOf(rowId);

        private IEnumerable<IDataRecord> Observe(IEnumerable<IDataRecord> records, bool isMetadata,
            ExportReadRoute route, int? limit = null)
        {
            var read = new ExportReadRequest(isMetadata, route, limit);
            _reads.Add(read);
            return Count(records, read);
        }

        private static IEnumerable<IDataRecord> Count(IEnumerable<IDataRecord> records, ExportReadRequest read)
        {
            foreach (IDataRecord record in records)
            {
                read.Materialized++;
                yield return record;
            }
        }

        private IReadOnlyList<SqLtSchemaColumn> Columns(IRawCaptureDataset raw)
        {
            if (ColumnsFailure != null)
            {
                throw ColumnsFailure;
            }
            return raw.Columns;
        }

        private sealed class ObservedDataset(ExportReadProbe owner, IDataset inner, bool isMetadata)
            : IDataset, IRawCaptureDataset, IDisposable
        {
            private readonly IRawCaptureDataset _raw = (IRawCaptureDataset)inner;
            private bool _disposed;

            public IEnumerable<IFieldDefinition> FieldDefs => inner.FieldDefs;
            public IEnumerable<IDataRecord> DataRecords =>
                owner.Observe(inner.DataRecords, isMetadata, ExportReadRoute.DataRecords);
            public IReadOnlyList<SqLtSchemaColumn> Columns => owner.Columns(_raw);
            public IEnumerable<IDataRecord> DataRecordsFrom(long firstRowId) =>
                owner.Observe(inner.DataRecordsFrom(firstRowId), isMetadata, ExportReadRoute.DataRecordsFrom);
            public IEnumerable<IDataRecord> DataRecordsFrom(long firstRowId, int limit) =>
                owner.Observe(inner.DataRecordsFrom(firstRowId, limit), isMetadata,
                    ExportReadRoute.DataRecordsFromLimited, limit);
            public IEnumerable<IDataRecord> DataRecordsWhen(params IFieldValue[] values)
            {
                if (isMetadata && owner.MetadataReadFailure != null)
                {
                    throw owner.MetadataReadFailure;
                }
                return owner.Observe(inner.DataRecordsWhen(values), isMetadata, ExportReadRoute.DataRecordsWhen);
            }
            public void Dispose()
            {
                owner.DatasetDisposed?.Invoke(isMetadata);
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                (inner as IDisposable)?.Dispose();
            }
        }
    }
}
