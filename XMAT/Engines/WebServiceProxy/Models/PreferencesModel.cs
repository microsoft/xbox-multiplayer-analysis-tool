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

            if (TryReadBoolean(serializedObject, nameof(PromptToDisableOnClose), out bool prompt))
            {
                PromptToDisableOnClose = prompt;
            }

            if (TryReadBoolean(serializedObject, nameof(AutoStreamMediaAndEvents), out bool autoStream))
            {
                AutoStreamMediaAndEvents = autoStream;
            }

            if (TryReadBoolean(serializedObject, nameof(StreamAllResponses), out bool streamAll))
            {
                StreamAllResponses = streamAll;
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

        // A hand-edited settings file can hold any JSON value; one that is not a Boolean leaves the setting as it is.
        private static bool TryReadBoolean(JsonElement settings, string name, out bool value)
        {
            value = false;
            if (!settings.TryGetProperty(name, out JsonElement property) ||
                property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return false;
            }
            value = property.GetBoolean();
            return true;
        }

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
