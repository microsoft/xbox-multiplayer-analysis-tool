// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter.OneCollector;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;

namespace XMAT.Telemetry
{
    internal interface ITelemetryReporter : IDisposable
    {
        void Send(TelemetryEvent item, Guid sessionId);
    }

    internal sealed class OneCollectorTelemetryReporter : ITelemetryReporter
    {
        internal const int QueueCapacity = 256;
        private readonly ILoggerFactory _factory;
        private readonly ILogger _logger;
        private readonly Action<string> _diagnose;
        private readonly SdkDiagnostics _diagnostics;
        private readonly IDisposable _trafficExclusion;
        private int _disposed;

        internal OneCollectorTelemetryReporter(
            TelemetryConfiguration configuration,
            IReadOnlyList<KeyValuePair<string, object>> resources,
            Action<string> diagnose,
            Uri testEndpoint = null)
        {
            if (!configuration.Enabled || !configuration.IsValid)
            {
                throw new ArgumentException("An enabled, approved telemetry configuration is required.", nameof(configuration));
            }
            if (testEndpoint != null && (configuration.Channel != TelemetryBuildChannel.Test || !testEndpoint.IsLoopback))
            {
                throw new ArgumentException("A test transport must use a loopback endpoint.", nameof(testEndpoint));
            }
            _diagnose = diagnose;
            var endpoint = testEndpoint ?? new Uri("https://mobile.events.data.microsoft.com/OneCollector/1.0/");
            _trafficExclusion = TelemetryTraffic.Exclude(endpoint);
            _diagnostics = new(diagnose);
            try
            {
                _factory = LoggerFactory.Create(logging => logging.AddOpenTelemetry(options =>
                {
                    options.IncludeScopes = false;
                    options.IncludeFormattedMessage = false;
                    options.SetResourceBuilder(ResourceBuilder.CreateEmpty().AddAttributes(resources));
                    options.AddOneCollectorExporter($"InstrumentationKey={configuration.InstrumentationKey}", builder => builder
                        .ConfigureBatchOptions(options =>
                        {
                            options.MaxQueueSize = QueueCapacity;
                            options.MaxExportBatchSize = 64;
                            options.ScheduledDelayMilliseconds = 250;
                            options.ExporterTimeoutMilliseconds = 1000;
                        })
                        .ConfigureSerializationOptions(options =>
                            options.ExceptionStackTraceHandling = OneCollectorExporterSerializationExceptionStackTraceHandlingType.Ignore)
                        .ConfigureTransportOptions(options =>
                        {
                            options.Endpoint = endpoint;
                        })
                        .ConfigureExporter(exporter => exporter.RegisterPayloadTransmittedCallback((in OneCollectorExporterPayloadTransmittedCallbackArguments args) =>
                        {
                            if (!args.Succeeded)
                            {
                                diagnose("TransmissionFailed");
                            }
                        })));
                }));
                _logger = _factory.CreateLogger(TelemetryConfiguration.EventNamespace);
            }
            catch
            {
                _diagnostics.Dispose();
                _trafficExclusion.Dispose();
                throw;
            }
        }

        public void Send(TelemetryEvent item, Guid sessionId)
        {
            if (!item.IsValid)
            {
                _diagnose("InvalidEvent");
                return;
            }
            if (!_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Information))
            {
                return;
            }
            // The isolated logger must not inherit another component's activity or baggage.
            var previousActivity = Activity.Current;
            try
            {
                Activity.Current = null;
                _logger.Log(Microsoft.Extensions.Logging.LogLevel.Information,
                    new EventId(0, item.Kind.ToString()), item.Attributes(sessionId), null,
                    static (_, _) => string.Empty);
            }
            finally
            {
                Activity.Current = previousActivity;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            var shutdown = Task.Run(() =>
            {
                try
                {
                    _factory.Dispose();
                }
                catch (Exception)
                {
                    _diagnose("ShutdownFailed");
                }
                finally
                {
                    _trafficExclusion.Dispose();
                }
            });
            if (!shutdown.Wait(2000))
            {
                _diagnose("ShutdownTimedOut");
            }
            _diagnostics.Dispose();
        }

        private sealed class SdkDiagnostics(Action<string> diagnose) : EventListener
        {
            protected override void OnEventSourceCreated(EventSource eventSource)
            {
                if (eventSource.Name.StartsWith("OpenTelemetry", StringComparison.Ordinal))
                {
                    EnableEvents(eventSource, EventLevel.Warning);
                }
            }

            protected override void OnEventWritten(EventWrittenEventArgs eventData)
            {
                if (eventData.EventName?.Contains("Drop", StringComparison.OrdinalIgnoreCase) == true)
                {
                    diagnose?.Invoke("QueueOverflow");
                }
                else if (eventData.Level == EventLevel.Error)
                {
                    diagnose?.Invoke("ExporterFailed");
                }
            }
        }
    }
}
