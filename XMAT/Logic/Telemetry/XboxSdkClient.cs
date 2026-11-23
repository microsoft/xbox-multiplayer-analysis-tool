// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using Microsoft.Xbox.Telemetry;

namespace XMAT.Telemetry
{
    internal sealed class XboxSdkClient : IXboxTelemetryClient
    {
        private readonly TelemetryClient _client;

        private XboxSdkClient(TelemetryClient client) => _client = client;

        internal static XboxSdkClient Create()
        {
            var options = new ClientConfigurationOptions { IngestionKey = TelemetryClient.GetDefaultIngestionKey() };
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
