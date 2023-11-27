// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace XMAT.SharedInterfaces
{
    public interface ICaptureMethod
    {
        ICaptureMethodParameters PreferencesModel { get; }

        void Initialize(ICaptureAppModel appModel);

        void Shutdown();

        bool OwnsDataTable(string tableName);
    }
}
