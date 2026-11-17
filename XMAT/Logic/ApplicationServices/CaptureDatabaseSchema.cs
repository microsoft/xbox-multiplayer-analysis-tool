// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.SharedInterfaces;

namespace XMAT.ApplicationServices
{
    internal static class CaptureDatabaseSchema
    {
        internal static IDataTable CreateDevices(IDatabase database) => database.CreateTable(
            "CaptureDevices", new Field<long>("DeviceType"), new Field<string>("DeviceName"), new Field<long>("CaptureType"));
    }
}
