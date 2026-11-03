// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace XMAT.SharedInterfaces
{
    public enum CaptureDataKind { WebProxy, NetworkTrace, WebSocket }
    public enum CaptureFindingSeverity { Info, Warning, Error }
    public enum CaptureDataSection { Records, Metrics, Findings, Reports }
    public enum CaptureDataError { InvalidFilter, UnsupportedData, SourceBusy, SourceChanged, LimitExceeded, Timeout, DetailNotFound }
    public enum CaptureDetailKind { Record, Finding, Report }
    public enum CaptureAnalysisScope { ExplicitFilter, DesktopVisibleRecords }
    public sealed record CaptureDetailTarget(CaptureDetailKind Kind, long Id);

    public sealed class CaptureDataException(CaptureDataError error) : InvalidOperationException(error.ToString())
    {
        public CaptureDataError Error { get; } = error;
    }

    public sealed record CaptureAnalyzerDescriptor(
        CaptureDataKind Kind, string Id, string Description, bool Available);

    public sealed record CaptureDataFilter
    {
        public string DeviceName { get; }
        public string Host { get; }
        public string Method { get; }
        public int? StatusCode { get; }
        public DateTimeOffset? AfterUtc { get; }
        public DateTimeOffset? BeforeUtc { get; }
        public double? MinimumDurationMilliseconds { get; }
        public int? Protocol { get; }
        public string SourceAddress { get; }
        public string DestinationAddress { get; }
        public Guid? CaptureId { get; }
        public long? CaptureEpoch { get; }
        public int? ConnectionId { get; }
        public bool? FromHost { get; }
        public string MessageType { get; }

        public CaptureDataFilter(
            string deviceName = null, string host = null, string method = null, int? statusCode = null,
            DateTimeOffset? afterUtc = null, DateTimeOffset? beforeUtc = null,
            double? minimumDurationMilliseconds = null, int? protocol = null,
            string sourceAddress = null, string destinationAddress = null,
            Guid? captureId = null, long? captureEpoch = null, int? connectionId = null,
            bool? fromHost = null, string messageType = null)
        {
            CheckText(deviceName, 256);
            CheckText(host, 253);
            CheckText(method, 32);
            if (captureId == Guid.Empty || captureEpoch < 1 || connectionId < 0 ||
                (messageType != null && messageType is not ("Text" or "Binary" or "Close" or "Unknown")))
                throw new ArgumentException("Invalid WebSocket filter.");
            if (statusCode.HasValue && statusCode is < 100 or > 599)
                throw new ArgumentOutOfRangeException(nameof(statusCode));
            if (afterUtc.HasValue && beforeUtc.HasValue && beforeUtc <= afterUtc)
                throw new ArgumentException("The time range must be increasing.");
            if (minimumDurationMilliseconds.HasValue &&
                (!double.IsFinite(minimumDurationMilliseconds.Value) || minimumDurationMilliseconds < 0))
                throw new ArgumentOutOfRangeException(nameof(minimumDurationMilliseconds));
            if (protocol.HasValue && protocol is < -1 or > 255)
                throw new ArgumentOutOfRangeException(nameof(protocol));
            DeviceName = deviceName;
            Host = host;
            Method = method;
            StatusCode = statusCode;
            AfterUtc = afterUtc?.ToUniversalTime();
            BeforeUtc = beforeUtc?.ToUniversalTime();
            MinimumDurationMilliseconds = minimumDurationMilliseconds;
            Protocol = protocol;
            SourceAddress = Address(sourceAddress);
            DestinationAddress = Address(destinationAddress);
            CaptureId = captureId;
            CaptureEpoch = captureEpoch;
            ConnectionId = connectionId;
            FromHost = fromHost;
            MessageType = messageType;
        }

        private static void CheckText(string value, int maximum)
        {
            if (value != null && (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Contains('\0')))
                throw new ArgumentException("Invalid filter text.");
        }

        private static string Address(string value)
        {
            if (value == null)
                return null;
            if (value.Length > 64 || !IPAddress.TryParse(value, out var address))
                throw new ArgumentException("Invalid IP address.");
            return address.ToString();
        }
    }

    public sealed record CaptureRecordSummary(
        long RecordId,
        string DeviceName,
        DateTimeOffset? Timestamp,
        long? RequestNumber,
        string Host,
        string Method,
        int? StatusCode,
        double? DurationMilliseconds,
        int? Protocol,
        string SourceAddress,
        string DestinationAddress,
        long? PacketBytes,
        bool IsSend,
        bool IsReceive,
        bool MetadataTruncated = false,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ConnectionId = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string MessageType = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? PayloadBytes = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool PayloadTruncated = false,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? FromHost = null);

    public sealed record CaptureWebSocketAvailability(
        Guid SourceId, Guid CaptureId, string DeviceName, long CaptureEpoch, bool Available,
        bool IsRunning, long RetainedMessages, string UnavailableReason);

    public sealed record CaptureReportDocumentDescriptor(long Id, string Type, string Console, long Length);

    public sealed record CaptureCompletedReportDescriptor(
        Guid Id, Guid SourceId, Guid SnapshotId, DateTimeOffset CapturedAt, DateTimeOffset CompletedAt,
        CaptureDataKind Kind, CaptureDataFilter Filter, long ScannedRecords, long MatchedRecords,
        Guid? CaptureId, long? CaptureEpoch, long? DesktopRunId,
        IReadOnlyList<CaptureReportDocumentDescriptor> Documents,
        CaptureAnalysisScope Scope = CaptureAnalysisScope.ExplicitFilter);

    public sealed record CaptureReportArtifact(string RelativePath, long Length, string Sha256);
    public sealed record CaptureReportDestination(Guid FolderId, string DirectoryName);
    public sealed record CaptureReportExportReceipt(
        Guid ReportId, Guid SourceId, Guid SnapshotId, Guid FolderId, string DirectoryName,
        long Length, int FileCount, bool Published);

    public interface ICaptureReportFiles : IAsyncDisposable
    {
        string SourceDirectory { get; }
        CaptureCompletedReportDescriptor Report { get; }
        IReadOnlyList<CaptureReportArtifact> Manifest { get; }
    }

    public interface ICaptureDataDetails : IAsyncDisposable
    {
        CaptureCompletedReportDescriptor Report { get; }
        long RetainedBytes => 0;
        Task<QueryPage> ReadPageAsync(
            CaptureDetailTarget target, long offset, int maximumRows, int maximumBytes,
            QueryExecutionOptions options, CancellationToken cancellationToken = default);
        Task<QueryValueChunk> ReadValueAsync(
            CaptureDetailTarget target, long row, int column, long byteOffset, int maximumBytes,
            QueryExecutionOptions options, CancellationToken cancellationToken = default);
        Task<ICaptureReportFiles> CreateReportFilesAsync(
            long maximumBytes, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<CaptureReportArtifact>> GetReportManifestAsync(
            long maximumBytes, CancellationToken cancellationToken = default);
    }

    public sealed record CaptureMetric(string Id, string Group, long Count, double? Value, string Unit);

    public sealed record CaptureFinding(
        long Id,
        string Console,
        string Endpoint,
        string EndpointKind,
        Guid? ConsoleId,
        Guid? EndpointId,
        string RuleId,
        string RuleName,
        CaptureFindingSeverity Severity,
        long ViolationCount,
        long AffectedRecords,
        long UnmappedCalls);

    public sealed record CaptureEvidence(long RecordId, long? RequestNumber, bool Synthetic);

    public sealed record CaptureDataResult(
        Guid Id,
        Guid SourceId,
        Guid SnapshotId,
        DateTimeOffset CapturedAt,
        CaptureDataKind Kind,
        bool IsAnalysis,
        long ScannedRecords,
        long MatchedRecords,
        long MalformedRecords,
        long EligibleServiceCalls,
        DateTimeOffset? FirstTimestamp,
        DateTimeOffset? LastTimestamp,
        IReadOnlyList<CaptureRecordSummary> Records,
        IReadOnlyList<CaptureMetric> Metrics,
        IReadOnlyList<CaptureFinding> Findings,
        IReadOnlyDictionary<long, IReadOnlyList<CaptureEvidence>> Evidence) : IAsyncDisposable
    {
        public Guid? CaptureId { get; init; }
        public long? CaptureEpoch { get; init; }
        public string SnapshotKind { get; init; } = "Database";
        public CaptureCompletedReportDescriptor Report => Details?.Report;
        [JsonIgnore]
        public ICaptureDataDetails Details { get; init; }
        [JsonIgnore]
        public IReadOnlyList<CaptureDescriptor> CaptureReferences { get; init; } = Array.Empty<CaptureDescriptor>();
        public ValueTask DisposeAsync() => Details?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}
