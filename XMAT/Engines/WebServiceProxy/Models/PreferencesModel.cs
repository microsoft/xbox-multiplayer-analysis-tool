// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XMAT.SharedInterfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace XMAT.WebServiceCapture.Models
{
    internal class PreferencesModel : ICaptureMethodParameters
    {
        public UInt16 FirstPort { get; set; }
        public UInt16 LastPort { get; set; }
        public bool PromptToDisableOnClose { get; set; }

        public void Serialized()
        {
            ProxyPortPool.Initialize(FirstPort, LastPort);
        }

        public void DeserializeFrom(JsonElement serializedObject)
        {
            JsonElement prop;

            if (serializedObject.TryGetProperty(nameof(FirstPort), out prop))
            {
                FirstPort = prop.GetUInt16();
            }

            if (serializedObject.TryGetProperty(nameof(LastPort), out prop))
            {
                LastPort = prop.GetUInt16();
            }

            if (serializedObject.TryGetProperty(nameof(PromptToDisableOnClose), out prop))
            {
                PromptToDisableOnClose = prop.GetBoolean();
            }

            ProxyPortPool.Initialize(FirstPort, LastPort);
        }

        internal PreferencesModel()
        {
            FirstPort = ProxyPortPool.DefaultFirstPort;
            LastPort = ProxyPortPool.DefaultLastPort;
            PromptToDisableOnClose = true;
        }
    }
}
