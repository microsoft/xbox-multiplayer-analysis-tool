// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace XMAT.DataAbstractionLayer
{
    internal static class SqLtConnectionGate
    {
        private static readonly ConditionalWeakTable<SqliteConnection, object> Gates = new();
        internal static object For(SqliteConnection connection) => Gates.GetValue(connection, _ => new object());
    }
}
