// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace XMAT.SharedInterfaces
{
    public interface ICaptureMethodParameters
    {
        void Serialized();
        void DeserializeFrom(JsonElement serializedObject);
    }
}
