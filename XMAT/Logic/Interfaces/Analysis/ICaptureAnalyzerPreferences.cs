// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.Json;

namespace XMAT.SharedInterfaces
{
    public interface ICaptureAnalyzerPreferences
    {
        void Serialized();
        void DeserializeFrom(JsonElement serializedObject);
    }
}
