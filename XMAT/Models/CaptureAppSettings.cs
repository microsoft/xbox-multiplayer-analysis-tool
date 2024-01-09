// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using XMAT.SharedInterfaces;

namespace XMAT.Models
{
    public static class CaptureAppSettings
    {
        internal class AppSettings
        {
            public AppSettings() { }

            internal AppSettings(CaptureAppModel model)
            {
                Preferences = model.PreferencesModel;

                CaptureDevices = new();

                foreach(var deviceContext in model.CaptureDeviceContexts)
                {
                    // note: we will not remember "unknown"/readonly devices since those probably
                    // came from some sort of import...
                    if (deviceContext.DeviceType == DeviceType.Unknown || deviceContext.IsReadOnly)
                        continue;

                    CaptureDevices.Add(new CaptureDevice
                    {
                        DeviceType = deviceContext.DeviceType,
                        DeviceName = deviceContext.DeviceName,
                        CaptureType = deviceContext.CaptureType,
                        IsSelected = deviceContext.IsSelected,
                        Parameters = deviceContext.CaptureController.Parameters
                    });
                }
            }

            internal void DeserializeTo(CaptureAppModel model)
            {
                model.PreferencesModel.DeserializeFrom(Preferences);

                if (CaptureDevices == null || CaptureDevices.Count == 0)
                    return;

                foreach (var captureDevice in CaptureDevices)
                {
                    ICaptureDeviceContext deviceContext = model.AddDeviceContext(
                        captureDevice.DeviceType, 
                        captureDevice.DeviceName,
                        captureDevice.CaptureType,
                        false,
                        captureDevice.Parameters);
                }
            }

            public class CaptureDevice
            {
                // maps to ICaptureDeviceContext.DeviceType
                public DeviceType DeviceType { get; set; }

                // maps to ICaptureDeviceContext.DeviceName
                public string DeviceName { get; set; }

                // maps to ICaptureDeviceContext.CaptureType
                public CaptureType CaptureType { get; set; }

                // maps to ICaptureDeviceContext.IsEnabled
                public bool IsSelected { get; set; }

                // maps to ICaptureDeviceContext.CaptureController.Parameters
                // NOTE: this is an 'object' because the parameters are
                // serialized/deserialized and the Json serializer
                // relies on this type to handle the reflection of derived
                // classes properly as per this document:
                // https://docs.microsoft.com/en-us/dotnet/standard/serialization/system-text-json-polymorphism
                public /*CaptureMethodParameters*/object Parameters { get; set; }
            }

            public List<CaptureDevice> CaptureDevices { get; set; }
            public AppPreferencesModel Preferences { get; set; }
        }

        internal static void Serialize(CaptureAppModel model)
        {
            string fullSettingsPath = GetSerializeFullPath(SettingsFileName, model);
            Directory.CreateDirectory(Path.GetDirectoryName(fullSettingsPath)); // this will ensure the directory exists or will create if it doesn't
            AppSettings serializedObject = new AppSettings(model);
            string jsonText = JsonSerializer.Serialize<object>(serializedObject, new JsonSerializerOptions() { WriteIndented = true });

            try
            {
                File.WriteAllText(fullSettingsPath, jsonText);
            }
            catch (IOException)
            {
                // TODO: handle the IO exception
                return;
            }

            foreach (ICaptureDeviceContext captureDevice in model.CaptureDeviceContexts)
            {
                if (captureDevice.CaptureController.Parameters != null)
                {
                    (captureDevice.CaptureController.Parameters as ICaptureMethodParameters).Serialized();
                }
            }

            model.PreferencesModel.Serialized();
        }

        internal static void Deserialize(CaptureAppModel model)
        {
            var fullSettingsPath = GetSerializeFullPath(SettingsFileName, model);
            string jsonText;

            try
            {
                jsonText = File.ReadAllText(fullSettingsPath);
            }
            catch (IOException)
            {
                // TODO: handle the IO exception
                return;
            }

            var serializedObject = JsonSerializer.Deserialize<AppSettings>(jsonText);
            if (serializedObject != null)
            {
                serializedObject.DeserializeTo(model);
            }
        }

        private const string SettingsFileName = "app-settings.json";

        private static string GetSerializeFullPath(string filename, ICaptureAppModel appModel)
        {
            return Path.Combine(PublicUtilities.StorageDirectoryPath, filename);
        }
    }
}
