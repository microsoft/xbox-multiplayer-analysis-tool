// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;

namespace XMAT.SharedInterfaces
{
    public interface ICaptureDatabaseBackup
    {
        void SaveToFile(string filepath);
        void SaveToFile(string filepath, long maximumBytes, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Bounded capture backups are unavailable.");
        void CreateSnapshotToFile(string filepath, long maximumBytes, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Private capture snapshots are unavailable.");
    }
}
