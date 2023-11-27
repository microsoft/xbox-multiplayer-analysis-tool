// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Threading.Tasks;

namespace XMAT.SharedInterfaces
{
    public interface IDeviceCaptureController
    {
        bool IsRunning { get; }

        ICaptureMethod CaptureMethod { get; }

        // NOTE: this is an 'object' because the parameters are
        // serialized/deserialized and the Json serializer
        // relies on this type to handle the reflection of derived
        // classes properly as per this document:
        // https://docs.microsoft.com/en-us/dotnet/standard/serialization/system-text-json-polymorphism
        object Parameters { get; }

        void Initialize();
        void LoadCaptures(IEnumerable<IDataset> tables);
        void ClearAllCaptures();
        Task<bool> CanCloseAsync();
        void Close();
    }
}
