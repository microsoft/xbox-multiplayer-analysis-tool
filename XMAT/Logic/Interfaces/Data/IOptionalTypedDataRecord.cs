// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

namespace XMAT.SharedInterfaces
{
    public interface IOptionalTypedDataRecord
    {
        bool TryReadString(string name, out string value);
        bool TryReadInt64(string name, out long value);
    }
}
