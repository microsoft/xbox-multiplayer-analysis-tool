// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

#if XMAT_TELEMETRY_SDK
using Microsoft.Xbox.Telemetry;

namespace XMAT.Telemetry
{
    internal sealed class XboxSdkClient : IXboxTelemetryClient
    {
        private readonly TelemetryClient _client;

        private XboxSdkClient(TelemetryClient client) => _client = client;

        internal static XboxSdkClient Create(string ingestionKey)
        {
            var options = new ClientConfigurationOptions { IngestionKey = ingestionKey };
            options.PolicyEngine.UnknownEvents = UnknownEventPolicy.PassThroughInferred;
            options.PolicyEngine.UnknownEventCommonProperties = CommonPropertyBehavior.ExcludeSensitive;
            var client = TelemetryClient.CreateInstance(new TelemetryClientConfiguration(options, null));
            try
            {
                client.SetCollectOptionalData(true);
                return new(client);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public void TraceEvent(string payload) => _client.TraceEvent(payload);
        public void Shutdown() => _client.Shutdown();
        public void Dispose() => _client.Dispose();
    }
}
#endif
