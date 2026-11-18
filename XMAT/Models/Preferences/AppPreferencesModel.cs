// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using XMAT.SharedInterfaces;

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

        public string Theme { get; set; } = "Light";

        // False shows the original XMAT look instead of the redesign, to compare the two.
        public bool UseNewLook { get; set; } = true;

        // False gives the new look its data-dense spacing instead of Comfort view's.
        public bool ComfortView { get; set; } = true;

        // False hides the traffic overview above the capture timeline in every capture tab.
        public bool ShowTrafficOverview { get; set; } = true;

        // The edition of What's new that last opened by itself at startup. A newer edition opens once more.
        public int WhatsNewSeen { get; set; }

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
                // XMAT offers Light and Dark; any other saved theme opens in Light.
                Theme = serializedObject.Theme == "Dark" ? "Dark" : "Light";
                UseNewLook = serializedObject.UseNewLook;
                // Settings saved before Comfort view existed have no value, so it stays on.
                ComfortView = serializedObject.ComfortView;
                // The same for the traffic overview, which stays shown.
                ShowTrafficOverview = serializedObject.ShowTrafficOverview;
                // Settings saved before What's new opened by itself have none, so it opens once.
                WhatsNewSeen = serializedObject.WhatsNewSeen;
            }
        }
    }
}
