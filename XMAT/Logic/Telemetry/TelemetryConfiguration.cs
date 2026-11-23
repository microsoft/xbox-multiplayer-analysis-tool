// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using System.Reflection;

namespace XMAT.Telemetry
{
    internal sealed record TelemetryConfiguration(TelemetryBuildChannel Channel)
    {
        internal const string EventNamespace = "Microsoft.Gaming.Tooling.XMAT";

        internal static TelemetryConfiguration FromAssembly()
        {
            var metadata = typeof(App).Assembly.GetCustomAttributesData()
                .Where(attribute => attribute.AttributeType == typeof(AssemblyMetadataAttribute)).ToArray();
            string Read(string key) => metadata.FirstOrDefault(attribute => attribute.ConstructorArguments[0].Value as string == key)?
                .ConstructorArguments[1].Value as string ?? string.Empty;
            if (!Enum.TryParse<TelemetryBuildChannel>(Read("Xmat.Telemetry.Channel"), out var channel) ||
                !Enum.IsDefined(channel) || channel == TelemetryBuildChannel.Local)
            {
                throw new InvalidOperationException("Invalid telemetry build channel.");
            }
            return new(channel);
        }
    }
}
