// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Schema = Microsoft.Gaming.Tooling.XMAT;

namespace XMAT.Telemetry
{
    internal sealed class XboxTelemetryPayload(IReadOnlyList<KeyValuePair<string, object>> environment)
    {
        private readonly Dictionary<string, object> _environment = environment.ToDictionary();

        internal string Serialize(TelemetryEvent item, Guid sessionId)
        {
            var common = new Schema.CommonFields
            {
                SchemaVersion = 1,
                SessionId = sessionId.ToString("N"),
                Feature = Parse<Schema.FeatureName>(item.Feature),
                Operation = Parse<Schema.OperationKind>(item.Operation),
                Outcome = Parse<Schema.Outcome>(item.Outcome),
                ErrorCategory = Parse<Schema.ErrorCategory>(item.Error),
                DurationMs = item.DurationMs,
                Count = item.Count,
                ItemCountBucket = Parse<Schema.ItemCountBucket>(item.ItemCount),
                FileSizeBucket = Parse<Schema.FileSizeBucket>(item.FileSize),
                Stage = Parse<Schema.StageName>(item.Stage),
                EntryPoint = Parse<Schema.EntryPoint>(item.EntryPoint),
                Interaction = Parse<Schema.Interaction>(item.Interaction),
                OperationId = item.OperationId?.ToString("N"),
                CaptureMethod = item.Context.HasValue ? Parse<Schema.CaptureMethod>(item.Context.Value.CaptureType) : null,
                DeviceType = item.Context.HasValue ? Parse<Schema.CaptureDeviceType>(item.Context.Value.DeviceType) : null,
                CaptureOrigin = item.Context.HasValue ? Parse<Schema.CaptureOrigin>(item.Context.Value.Origin) : null,
                AppVersion = (string)_environment["ext.app.ver"],
                OsVersion = (string)_environment["ext.os.ver"],
                BuildChannel = Enum.Parse<Schema.BuildChannel>((string)_environment["BuildChannel"]),
                DeviceLocalId = _environment.TryGetValue("ext.device.localId", out var id) ? (string)id : null,
                XboxSandbox = _environment.TryGetValue("ext.xbl.sbx", out var sandbox) ? (string)sandbox : null,
                MachineIdAvailable = (bool)_environment["MachineIdAvailable"]
            };
            return item.Kind switch
            {
                TelemetryEventKind.AppLaunched => Encode(common, new Schema.AppLaunched()),
                TelemetryEventKind.AppReady => Encode(common, new Schema.AppReady()),
                TelemetryEventKind.AppClosed => Encode(common, new Schema.AppClosed()),
                TelemetryEventKind.FeatureUsed => Encode(common, new Schema.FeatureUsed()),
                TelemetryEventKind.OperationStarted => Encode(common, new Schema.OperationStarted()),
                TelemetryEventKind.OperationCompleted => Encode(common, new Schema.OperationCompleted()),
                TelemetryEventKind.OperationStage => Encode(common, new Schema.OperationStage()),
                TelemetryEventKind.CaptureFirstData => Encode(common, new Schema.CaptureFirstData()),
                TelemetryEventKind.Counter => Encode(common, new Schema.Counter()),
                TelemetryEventKind.SessionSummary => Encode(common, new Schema.SessionSummary()),
                TelemetryEventKind.UnhandledFailure => Encode(common, new Schema.UnhandledFailure()),
                _ => throw new ArgumentOutOfRangeException(nameof(item))
            };
        }

        private static T Parse<T>(Enum value) where T : struct, Enum => Enum.Parse<T>(value.ToString());

        private static string Encode<T>(Schema.CommonFields common, T item) where T : class, Schema.ITelemetryEvent =>
            JsonSerializer.Serialize(Schema.TelemetryEvent.Create(common, item));
    }
}
