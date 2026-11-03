// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Text.Json;
using XMAT.SharedInterfaces;

namespace XMAT.Models
{
    internal class AppPreferencesModel : PreferenceObservable
    {
        private object _webProxyCapturePreferences;
        private object _networkCapturePreferences;
        private object _networkAnalysisPreferences;
        private string _language = "en-us";
        private string _theme = "Light";

        // NOTE: this is an 'object' because the parameters are
        // serialized/deserialized and the Json serializer
        // relies on this type to handle the reflection of derived
        // classes properly as per this document:
        // https://docs.microsoft.com/en-us/dotnet/standard/serialization/system-text-json-polymorphism
        public /*CaptureMethodParameters*/object WebProxyCapturePreferences
        {
            get => _webProxyCapturePreferences;
            set => SetPreferences(ref _webProxyCapturePreferences, value, nameof(WebProxyCapturePreferences));
        }

        // NOTE: this is an 'object' because the parameters are
        // serialized/deserialized and the Json serializer
        // relies on this type to handle the reflection of derived
        // classes properly as per this document:
        // https://docs.microsoft.com/en-us/dotnet/standard/serialization/system-text-json-polymorphism
        public /*CaptureMethodParameters*/object NetworkCapturePreferences
        {
            get => _networkCapturePreferences;
            set => SetPreferences(ref _networkCapturePreferences, value, nameof(NetworkCapturePreferences));
        }

        // NOTE: this is an 'object' because the parameters are
        // serialized/deserialized and the Json serializer
        // relies on this type to handle the reflection of derived
        // classes properly as per this document:
        // https://docs.microsoft.com/en-us/dotnet/standard/serialization/system-text-json-polymorphism
        public /*CaptureMethodParameters*/object NetworkAnalysisPreferences
        {
            get => _networkAnalysisPreferences;
            set => SetPreferences(ref _networkAnalysisPreferences, value, nameof(NetworkAnalysisPreferences));
        }

        public string Language { get => _language; set => SetValue(ref _language, value); }

        public string Theme { get => _theme; set => SetValue(ref _theme, value); }

        private void SetPreferences(ref object field, object value, string property)
        {
            if (ReferenceEquals(field, value))
                return;
            if (field is PreferenceObservable previous)
                previous.Changed -= ChildChanged;
            field = value;
            if (value is PreferenceObservable current)
                current.Changed += ChildChanged;
            ValueChanged(property);
        }

        private void ChildChanged(object sender, EventArgs args) => ValueChanged(string.Empty);

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
                Theme = serializedObject.Theme ?? "Light";
            }
        }
    }
}
