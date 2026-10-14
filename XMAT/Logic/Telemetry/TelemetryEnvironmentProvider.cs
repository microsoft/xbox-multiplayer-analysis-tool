// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security;
using Microsoft.Win32;

namespace XMAT.Telemetry
{
    internal static class TelemetryEnvironmentProvider
    {
        internal static IReadOnlyList<KeyValuePair<string, object>> Read(TelemetryBuildChannel channel, Action<string> diagnose)
        {
            var version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            List<KeyValuePair<string, object>> fields =
            [
                new("ext.app.id", "XboxMultiplayerAnalysisTool"),
                new("ext.app.ver", (version ?? "Unknown").Split('+')[0]),
                new("ext.os.name", "Windows"),
                new("ext.os.ver", Environment.OSVersion.Version.ToString()),
                new("BuildChannel", channel.ToString())
            ];
            try
            {
                using var machineKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\SQMClient");
                if (machineKey?.GetValue("MachineId") is string machineId && Guid.TryParse(machineId, out var id))
                {
                    fields.Add(new("ext.device.localId", $"s:{id:D}"));
                }
                using var sandboxKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\XboxLive");
                fields.Add(new("ext.xbl.sbx", NormalizeSandbox(sandboxKey?.GetValue("Sandbox") as string, diagnose)));
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or System.IO.IOException)
            {
                diagnose("EnvironmentReadFailed");
            }
            fields.Add(new("MachineIdAvailable", fields.Exists(x => x.Key == "ext.device.localId")));
            return fields;
        }

        internal static string NormalizeSandbox(string value, Action<string> diagnose)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "RETAIL";
            }
            if (value.Length <= 128 && !value.AsSpan().ContainsAny('\r', '\n', '\0'))
            {
                return value;
            }
            diagnose("InvalidSandbox");
            return "Unknown";
        }
    }
}
