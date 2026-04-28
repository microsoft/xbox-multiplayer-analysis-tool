// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;

namespace XMAT.SharedInterfaces
{
    public interface ICaptureAnalyzerPreferences
    {
        void Serialized();
        void DeserializeFrom(JsonElement serializedObject);
    }
}
