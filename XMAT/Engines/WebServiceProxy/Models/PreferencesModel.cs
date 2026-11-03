// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Text.Json;
using XMAT.Models;
using XMAT.SharedInterfaces;

namespace XMAT.WebServiceCapture.Models
{
    internal class PreferencesModel : PreferenceObservable, ICaptureMethodParameters
    {
        private ushort _firstPort = ProxyPortPool.DefaultFirstPort;
        private ushort _lastPort = ProxyPortPool.DefaultLastPort;
        private bool _promptToDisableOnClose = true;

        public UInt16 FirstPort { get => _firstPort; set => SetValue(ref _firstPort, value); }
        public UInt16 LastPort { get => _lastPort; set => SetValue(ref _lastPort, value); }
        public bool PromptToDisableOnClose
        {
            get => _promptToDisableOnClose;
            set => SetValue(ref _promptToDisableOnClose, value);
        }

        public void Serialized()
        {
            ProxyPortPool.Shared.Initialize(FirstPort, LastPort);
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

            ProxyPortPool.Shared.RestoreRange(FirstPort, LastPort);
        }

        internal PreferencesModel()
        {
            FirstPort = ProxyPortPool.DefaultFirstPort;
            LastPort = ProxyPortPool.DefaultLastPort;
            PromptToDisableOnClose = true;
        }
    }
}
