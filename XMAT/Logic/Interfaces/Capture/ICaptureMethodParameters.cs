// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.Json;

namespace XMAT.SharedInterfaces
{
    public interface ICaptureMethodParameters
    {
        void Serialized();
        void DeserializeFrom(JsonElement serializedObject);
    }
}
