// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using XMAT.SharedInterfaces;

namespace XMAT.DataAbstractionLayer
{
    internal partial class SqLtDataEnumerable : IEnumerator<IDataRecord>
    {
        public IDataRecord Current
        {
            get
            {
                lock (_gate)
                {
                    if (_cachedDataRecord == null)
                    {
                        _cachedDataRecord = new SqLtDataRecord(_dataReader, _fieldDefs);
                    }
                    return _cachedDataRecord;
                }
            }
        }

        public bool MoveNext()
        {
            lock (_gate)
            {
                _cachedDataRecord = null;
                return _dataReader?.Read() == true;
            }
        }

        public void Reset()
        {
            lock (_gate)
            {
                _cachedDataRecord = null;
                if (_dataReader != null)
                    _dataReader.GetEnumerator().Reset();
            }
        }

        object IEnumerator.Current
        {
            get { return Current; }
        }

        void IDisposable.Dispose()
        {
            lock (_gate)
            {
                if (_dataReader != null)
                {
                    _dataReader.Close();
                    _dataReader.Dispose();
                }
            }
        }

        private SqLtDataRecord _cachedDataRecord;
    }
}
