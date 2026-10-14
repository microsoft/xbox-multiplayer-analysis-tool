// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using System.Reflection;

namespace XMAT.Telemetry
{
    internal sealed record TelemetryConfiguration(bool Enabled, bool Approved, string InstrumentationKey, TelemetryBuildChannel Channel)
    {
        internal const string EventNamespace = "Microsoft.Gaming.XMAT";

        internal bool IsValid => !Enabled || (Approved && Channel != TelemetryBuildChannel.Local &&
            Enum.IsDefined(Channel) && !string.IsNullOrWhiteSpace(InstrumentationKey) &&
            InstrumentationKey.Length <= 128 && InstrumentationKey.Contains('-') &&
            InstrumentationKey.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));

        internal static TelemetryConfiguration FromAssembly()
        {
            var metadata = typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToArray();
            string Read(string key) => metadata.FirstOrDefault(x => x.Key == key)?.Value ?? string.Empty;
            var enabled = Read("Xmat.Telemetry.Enabled");
            if (!bool.TryParse(enabled, out var isEnabled))
            {
                throw new InvalidOperationException("Invalid telemetry build configuration.");
            }
            bool.TryParse(Read("Xmat.Telemetry.Approved"), out var approved);
            if (!Enum.TryParse<TelemetryBuildChannel>(Read("Xmat.Telemetry.Channel"), out var channel))
            {
                channel = TelemetryBuildChannel.Local;
            }
            return new(isEnabled, approved, Read("Xmat.Telemetry.Key"), channel);
        }
    }
}
