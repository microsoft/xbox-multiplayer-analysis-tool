// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XMAT.SharedInterfaces;

namespace XMAT.DataAbstractionLayer
{
    public static class DataAbstractionLayer
    {
        internal static IDisposable ReserveFilePublication(string path) =>
            LoadedCaptureDatabaseRegistry.AcquireSaveDestination(path);

        internal static IDatabase CreateWorkingDatabase()
        {
            var database = new SqLtDatabase();
            try
            { database.Initialize(); return database; }
            catch { database.Dispose(); throw; }
        }

        public static IDatabase Database
        {
            get { return DataAbstractionLayerImpl.GetDatabase(); }
        }

        public static IReadonlyDatabase LoadDatabaseFromFile(string filepath)
        {
            return DataAbstractionLayerImpl.LoadDatabaseFromFile(filepath);
        }

        internal static async Task CopyCaptureInputAsync(
            string path, FileStream input, Stream destination, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var database = new SqLtReadonlyDatabase(path);
            if (database.Identity != SqLtFileIdentity.FromHandle(input.SafeFileHandle))
                throw new SqLtSchemaException(SqLtSchemaFailure.Invalid);
            await database.CopyInputAsync(destination, cancellationToken);
        }
    }
}
