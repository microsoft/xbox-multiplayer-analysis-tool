// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Data.Sqlite;
using XMAT.SharedInterfaces;

namespace XMAT.DataAbstractionLayer
{
    internal partial class SqLtDataEnumerable : IEnumerator<IDataRecord>
    {
        public IDataRecord Current
        {
            get
            {
                ObjectDisposedException.ThrowIf(
                    _ownerDisposed,
                    this);
                if (_cachedDataRecord == null)
                {
                    _cachedDataRecord = new SqLtDataRecord(_dataReader, _fieldDefs);
                }

                return _cachedDataRecord;
            }
        }

        public bool MoveNext()
        {
            ObjectDisposedException.ThrowIf(
                _ownerDisposed,
                this);
            _cachedDataRecord = null;
            SqliteDataReader reader =
                _dataReader;
            if (reader == null)
            {
                ObjectDisposedException.ThrowIf(
                    _ownerDisposed,
                    this);
                Dispose();
                ObjectDisposedException.ThrowIf(
                    _ownerDisposed,
                    this);
                return false;
            }
            bool moved;
            try
            {
                moved = reader.Read();
            }
            catch
            {
                ObjectDisposedException.ThrowIf(
                    _ownerDisposed,
                    this);
                throw;
            }
            if (!moved)
            {
                ObjectDisposedException.ThrowIf(
                    _ownerDisposed,
                    this);
                Dispose();
                ObjectDisposedException.ThrowIf(
                    _ownerDisposed,
                    this);
                return false;
            }
            ObjectDisposedException.ThrowIf(
                _ownerDisposed,
                this);
            return true;
        }

        public void Reset()
        {
            ObjectDisposedException.ThrowIf(
                _ownerDisposed,
                this);
            _cachedDataRecord = null;
            if (_dataReader != null)
                _dataReader.GetEnumerator().Reset();
        }

        object IEnumerator.Current
        {
            get { return Current; }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(
                    ref _isDisposed,
                    1) != 0)
            {
                return;
            }
            SqliteDataReader reader =
                _dataReader;
            _dataReader = null;
            _cachedDataRecord = null;
            try
            {
                reader?.Dispose();
            }
            finally
            {
                Action<SqLtDataEnumerable> disposed =
                    _onDisposed;
                _onDisposed = null;
                disposed?.Invoke(this);
            }
        }

        internal void DisposeFromOwner()
        {
            _ownerDisposed = true;
            Dispose();
        }

        private SqLtDataRecord _cachedDataRecord;
    }
}
