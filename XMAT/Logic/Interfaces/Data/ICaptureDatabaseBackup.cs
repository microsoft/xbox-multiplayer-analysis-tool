// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

namespace XMAT.SharedInterfaces
{
    public interface ICaptureDatabaseBackup
    {
        void SaveToFile(string filepath);
    }
}
