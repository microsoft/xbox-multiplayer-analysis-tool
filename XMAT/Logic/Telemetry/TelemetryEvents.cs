// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using XMAT.SharedInterfaces;

namespace XMAT.Telemetry
{
    internal enum TelemetryEventKind
    {
        AppLaunched, AppReady, AppClosed, FeatureUsed, OperationStarted, OperationCompleted,
        OperationStage, CaptureFirstData, Counter, SessionSummary, UnhandledFailure
    }

    internal enum TelemetryFeature
    {
        None, WebProxyCapture, NetworkTraceCapture, XboxLiveAnalysis, NetworkTraceAnalysis,
        CaptureFiles, Scripting, BlockList, BypassList, RequestHeaders, RequestBody, RequestJson,
        WebSockets, WebSocketText, WebSocketJson, WebSocketHex, PacketData, FilterStatus,
        FilterHost, FilterMethod, FilterProcess, FilterThread, FilterProtocol, FilterSourceAddress,
        FilterDestinationAddress, FilterWebSocketDirection, FilterWebSocketType, TimelineWaterfall,
        TimelineByHost, TimelineSelection, PerEndpointReport, StatisticsReport, NetworkReport,
        ReportDrillDown, ReportRequestNavigation, ClearCaptures, DebugLogs, RootCertificate,
        GdkInfo, Preferences, LightTheme, DarkTheme, EnglishLanguage, SslScript,
        RequestScript, ResponseScript, ScriptLoad, ScriptSave, ScriptRevert
    }

    internal enum TelemetryOperationKind
    {
        None, DeviceAdd, EnableProxy, DisableProxy, Capture, Analysis, LoadCaptures, SaveCaptures,
        ImportSaz, ExportSaz, CompileScript, LoadScript, SaveScript, CollectLogs,
        ExportRootCertificate, StatisticsRender, AnalyzerInitialize
    }

    internal enum TelemetryOutcome
    {
        None, Succeeded, Failed, Cancelled, NoData, Unsupported
    }

    internal enum TelemetryError
    {
        None, AccessDenied, Io, InvalidData, InvalidOperation, Cancelled, Network,
        MissingGdk, MissingDefaultConsole, AlreadyExists, Configuration, Compilation, Unknown
    }

    internal enum CaptureOrigin { None, User, Restored, Loaded, Imported }
    internal enum TelemetryStage { None, Connect, Start, Stop, Download, Disconnect, Convert, Validate, GenerateReport }
    internal enum TelemetryCountBucket { Unknown, Zero, OneToTen, ElevenToHundred, HundredToThousand, ThousandToTenThousand, AboveTenThousand }
    internal enum TelemetrySizeBucket { Unknown, Empty, BelowOneMb, OneToTenMb, TenToHundredMb, HundredMbToGb, AboveOneGb }
    internal enum TelemetryBuildChannel { Local, Public, Internal, Test }
    internal enum TelemetryEntryPoint { None, Menu, DragDrop }
    internal enum TelemetryInteraction { None, Added, Removed, Cleared, Enabled, Disabled, Reverted }

    internal readonly record struct TelemetryContext(CaptureType CaptureType, DeviceType DeviceType, CaptureOrigin Origin);

    internal sealed record TelemetryEvent(
        TelemetryEventKind Kind,
        TelemetryFeature Feature = TelemetryFeature.None,
        TelemetryOperationKind Operation = TelemetryOperationKind.None,
        TelemetryOutcome Outcome = TelemetryOutcome.None,
        TelemetryError Error = TelemetryError.None,
        TelemetryContext? Context = null,
        Guid? OperationId = null,
        long DurationMs = 0,
        long Count = 0,
        TelemetryCountBucket ItemCount = TelemetryCountBucket.Unknown,
        TelemetrySizeBucket FileSize = TelemetrySizeBucket.Unknown,
        TelemetryStage Stage = TelemetryStage.None,
        TelemetryEntryPoint EntryPoint = TelemetryEntryPoint.None,
        TelemetryInteraction Interaction = TelemetryInteraction.None)
    {
        internal bool IsValid =>
            Enum.IsDefined(Kind) && Enum.IsDefined(Feature) && Enum.IsDefined(Operation) &&
            Enum.IsDefined(Outcome) && Enum.IsDefined(Error) && Enum.IsDefined(ItemCount) &&
            Enum.IsDefined(FileSize) && Enum.IsDefined(Stage) && Enum.IsDefined(EntryPoint) &&
            Enum.IsDefined(Interaction) && DurationMs >= 0 && Count >= 0 &&
            (!Context.HasValue || (Enum.IsDefined(Context.Value.CaptureType) &&
                Enum.IsDefined(Context.Value.DeviceType) && Enum.IsDefined(Context.Value.Origin)));

        internal List<KeyValuePair<string, object>> Attributes(Guid sessionId)
        {
            List<KeyValuePair<string, object>> attributes =
            [
                new("SchemaVersion", 1),
                new("SessionId", sessionId.ToString("N")),
                new("Feature", Feature.ToString()),
                new("Operation", Operation.ToString()),
                new("Outcome", Outcome.ToString()),
                new("ErrorCategory", Error.ToString()),
                new("DurationMs", DurationMs),
                new("Count", Count),
                new("ItemCountBucket", ItemCount.ToString()),
                new("FileSizeBucket", FileSize.ToString()),
                new("Stage", Stage.ToString()),
                new("EntryPoint", EntryPoint.ToString()),
                new("Interaction", Interaction.ToString())
            ];
            if (OperationId.HasValue)
            {
                attributes.Add(new("OperationId", OperationId.Value.ToString("N")));
            }
            if (Context.HasValue)
            {
                attributes.Add(new("CaptureMethod", Context.Value.CaptureType.ToString()));
                attributes.Add(new("DeviceType", Context.Value.DeviceType.ToString()));
                attributes.Add(new("CaptureOrigin", Context.Value.Origin.ToString()));
            }
            return attributes;
        }
    }

    internal static class TelemetryBuckets
    {
        internal static TelemetryCountBucket Items(long count) => count switch
        {
            < 0 => TelemetryCountBucket.Unknown,
            0 => TelemetryCountBucket.Zero,
            <= 10 => TelemetryCountBucket.OneToTen,
            <= 100 => TelemetryCountBucket.ElevenToHundred,
            <= 1000 => TelemetryCountBucket.HundredToThousand,
            <= 10000 => TelemetryCountBucket.ThousandToTenThousand,
            _ => TelemetryCountBucket.AboveTenThousand
        };

        internal static TelemetrySizeBucket Size(long bytes) => bytes switch
        {
            < 0 => TelemetrySizeBucket.Unknown,
            0 => TelemetrySizeBucket.Empty,
            < 1_048_576 => TelemetrySizeBucket.BelowOneMb,
            < 10_485_760 => TelemetrySizeBucket.OneToTenMb,
            < 104_857_600 => TelemetrySizeBucket.TenToHundredMb,
            < 1_073_741_824 => TelemetrySizeBucket.HundredMbToGb,
            _ => TelemetrySizeBucket.AboveOneGb
        };

        internal static TelemetryError Error(Exception exception) => exception switch
        {
            UnauthorizedAccessException => TelemetryError.AccessDenied,
            OperationCanceledException => TelemetryError.Cancelled,
            System.Net.Sockets.SocketException or System.Net.Http.HttpRequestException => TelemetryError.Network,
            System.Text.Json.JsonException or FormatException or InvalidDataException => TelemetryError.InvalidData,
            IOException => TelemetryError.Io,
            InvalidOperationException => TelemetryError.InvalidOperation,
            _ => TelemetryError.Unknown
        };
    }
}
