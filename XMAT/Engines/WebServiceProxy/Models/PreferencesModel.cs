// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Text.Json;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture.Proxy;

namespace XMAT.WebServiceCapture.Models
{
    internal class PreferencesModel : ICaptureMethodParameters
    {
        public UInt16 FirstPort { get; set; }
        public UInt16 LastPort { get; set; }
        public bool PromptToDisableOnClose { get; set; }
        public bool AutoStreamMediaAndEvents { get; set; }
        public bool StreamAllResponses { get; set; }
        public int CaptureLimitMegabytes { get; set; }

        public void Serialized()
        {
            ProxyPortPool.Shared.Initialize(FirstPort, LastPort);
            ApplyResponseStreaming();
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

            if (serializedObject.TryGetProperty(nameof(AutoStreamMediaAndEvents), out prop))
            {
                AutoStreamMediaAndEvents = prop.GetBoolean();
            }

            if (serializedObject.TryGetProperty(nameof(StreamAllResponses), out prop))
            {
                StreamAllResponses = prop.GetBoolean();
            }

            if (serializedObject.TryGetProperty(nameof(CaptureLimitMegabytes), out prop) &&
                prop.ValueKind == JsonValueKind.Number &&
                prop.TryGetInt32(out int captureLimitMegabytes))
            {
                CaptureLimitMegabytes = ResponseStreamingOptions.ClampMegabytes(captureLimitMegabytes);
            }

            ProxyPortPool.Shared.RestoreRange(FirstPort, LastPort);
            ApplyResponseStreaming();
        }

        // New responses use the streaming settings that were loaded or saved last.
        private void ApplyResponseStreaming() =>
            ResponseStreamingOptions.Current = ResponseStreamingOptions.FromPreferences(
                AutoStreamMediaAndEvents,
                StreamAllResponses,
                CaptureLimitMegabytes);

        internal PreferencesModel()
        {
            FirstPort = ProxyPortPool.DefaultFirstPort;
            LastPort = ProxyPortPool.DefaultLastPort;
            PromptToDisableOnClose = true;
            AutoStreamMediaAndEvents = true;
            StreamAllResponses = false;
            CaptureLimitMegabytes = ResponseStreamingOptions.DefaultCaptureLimitMegabytes;
        }
    }
}
