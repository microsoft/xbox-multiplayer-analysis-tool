// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XMAT.SharedInterfaces;
using System;
using System.Collections;
using System.Collections.Generic;

namespace XMAT.DataAbstractionLayer
{
    internal partial class SqLtDataEnumerable : IEnumerator<IDataRecord>
    {
        public IDataRecord Current
        {
            get
            {
                if (_cachedDataRecord == null)
                {
                    _cachedDataRecord = new SqLtDataRecord(_dataReader, _fieldDefs);
                }

                return _cachedDataRecord;
            }
        }

        public bool MoveNext()
        {
            _cachedDataRecord = null;
            if(_dataReader != null)
                return _dataReader.GetEnumerator().MoveNext();
            return false;
        }

        public void Reset()
        {
            _cachedDataRecord = null;
            if(_dataReader != null)
                _dataReader.GetEnumerator().Reset();
        }

        object IEnumerator.Current
        {
            get { return Current; }
        }

        void IDisposable.Dispose()
        {
            if(_dataReader != null)
            {
                _dataReader.Close();
                _dataReader.Dispose();
            }
        }

        private SqLtDataRecord _cachedDataRecord;
    }
}
