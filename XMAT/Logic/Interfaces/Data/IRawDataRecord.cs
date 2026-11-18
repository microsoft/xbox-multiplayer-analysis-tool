// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using XMAT.DataAbstractionLayer;

namespace XMAT.SharedInterfaces
{
    internal interface IRawDataRecord
    {
        bool TryReadRaw(string name, out object value);
    }

    internal interface IRawCaptureDataset
    {
        IReadOnlyList<SqLtSchemaColumn> Columns { get; }
    }

    internal interface IVersionedCaptureTable
    {
        IDisposable EnterRead();
        long VersionOf(long rowId);
    }
}
