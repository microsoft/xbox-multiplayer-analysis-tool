// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;

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

                foreach (var deviceContext in model.CaptureDeviceContexts)
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
                        captureDevice.Parameters,
                        true);
                }
                var proxies = model.CaptureDeviceContexts
                    .Where(context => !context.IsReadOnly)
                    .Select(context => context.CaptureController)
                    .OfType<WebServiceDeviceCaptureController>().ToArray();
                foreach (var proxy in proxies)
                {
                    proxy.RestorePendingPort();
                }
                foreach (var proxy in proxies)
                {
                    proxy.RestoreCurrentPort(false);
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

        internal static bool Serialize(CaptureAppModel model)
        {
            try
            {
                SerializeChecked(model);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                PublicUtilities.AppLog(LogLevel.ERROR, $"Could not save application settings: {ex}");
                MessageBox.Show(
                    Localization.GetLocalizedString("PROXY_SETTINGS_SAVE_FAILED", ex.Message),
                    Localization.GetLocalizedString("PROXY_CAPTURE_ERROR_TITLE"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        internal static void SerializeChecked(CaptureAppModel model)
        {
            model.PreferencesModel.Serialized();
            string fullSettingsPath = GetSerializeFullPath(SettingsFileName, model);
            Directory.CreateDirectory(Path.GetDirectoryName(fullSettingsPath)); // this will ensure the directory exists or will create if it doesn't
            foreach (ICaptureDeviceContext captureDevice in model.CaptureDeviceContexts)
            {
                if (captureDevice.CaptureController.Parameters != null)
                {
                    (captureDevice.CaptureController.Parameters as ICaptureMethodParameters).Serialized();
                }
            }
            var serializedObject = new AppSettings(model);
            var jsonText = JsonSerializer.Serialize<object>(serializedObject, new JsonSerializerOptions() { WriteIndented = true });
            WriteSettingsFile(fullSettingsPath, jsonText);
        }

        internal static void WriteSettingsFile(string path, string json)
        {
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, json);
                File.Move(temporaryPath, path, true);
            }
            finally
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    PublicUtilities.AppLog(LogLevel.ERROR, $"Could not remove temporary settings file: {ex}");
                }
            }
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
