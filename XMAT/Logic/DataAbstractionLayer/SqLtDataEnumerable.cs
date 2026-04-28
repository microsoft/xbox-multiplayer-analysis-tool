// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using XMAT.SharedInterfaces;

namespace XMAT.DataAbstractionLayer
{
    internal partial class SqLtDataEnumerable : IEnumerable<IDataRecord>
    {
        public IEnumerator<IDataRecord> GetEnumerator()
        {
            return this;
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return this.GetEnumerator();
        }

        internal SqLtDataEnumerable(
            IDictionary<string, IFieldDefinition> fieldDefs,
            SqliteDataReader dataReader,
            Predicate<IDataRecord> predicate)
        {
            _fieldDefs = fieldDefs;
            _dataReader = dataReader;
            _predicate = predicate;
            _cachedDataRecord = null;
        }

        private IDictionary<string, IFieldDefinition> _fieldDefs;
        private SqliteDataReader _dataReader;
        private Predicate<IDataRecord> _predicate;
    }
}
