// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XMAT.SharedInterfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace XMAT.Models
{
    internal class AppPreferencesModel
    {
        // NOTE: this is an 'object' because the parameters are
        // serialized/deserialized and the Json serializer
        // relies on this type to handle the reflection of derived
        // classes properly as per this document:
        // https://docs.microsoft.com/en-us/dotnet/standard/serialization/system-text-json-polymorphism
        public /*CaptureMethodParameters*/object WebProxyCapturePreferences { get; set; }

        // NOTE: this is an 'object' because the parameters are
        // serialized/deserialized and the Json serializer
        // relies on this type to handle the reflection of derived
        // classes properly as per this document:
        // https://docs.microsoft.com/en-us/dotnet/standard/serialization/system-text-json-polymorphism
        public /*CaptureMethodParameters*/object NetworkCapturePreferences { get; set; }

        // NOTE: this is an 'object' because the parameters are
        // serialized/deserialized and the Json serializer
        // relies on this type to handle the reflection of derived
        // classes properly as per this document:
        // https://docs.microsoft.com/en-us/dotnet/standard/serialization/system-text-json-polymorphism
        public /*CaptureMethodParameters*/object NetworkAnalysisPreferences { get; set; }

        public string Language { get; set; } = "en-us"; // Default to en-us to support config files prior to loc support

        public void Serialized()
        {
            if (WebProxyCapturePreferences != null)
            {
                (WebProxyCapturePreferences as ICaptureMethodParameters).Serialized();
            }

            if (NetworkCapturePreferences != null)
            {
                (NetworkCapturePreferences as ICaptureMethodParameters).Serialized();
            }

            if (NetworkAnalysisPreferences != null)
            {
                (NetworkAnalysisPreferences as ICaptureAnalyzerPreferences).Serialized();
            }
        }

        public void DeserializeFrom(AppPreferencesModel serializedObject)
        {
            if (serializedObject != null)
            {
                if (WebProxyCapturePreferences != null)
                {
                    (WebProxyCapturePreferences as ICaptureMethodParameters).DeserializeFrom(
                        (JsonElement)serializedObject.WebProxyCapturePreferences);
                }

                if (NetworkCapturePreferences != null)
                {
                    (NetworkCapturePreferences as ICaptureMethodParameters).DeserializeFrom(
                        (JsonElement)serializedObject.NetworkCapturePreferences);
                }

                if (NetworkAnalysisPreferences != null && serializedObject.NetworkAnalysisPreferences != null)
                {
                    (NetworkAnalysisPreferences as ICaptureAnalyzerPreferences).DeserializeFrom(
                        (JsonElement)serializedObject.NetworkAnalysisPreferences);
                }

                Language = serializedObject.Language;
            }
        }
    }
}
