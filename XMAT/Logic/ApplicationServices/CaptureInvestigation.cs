// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CaptureAnalysisEngine;
using XMAT.Models;
using XMAT.NetworkTrace;
using XMAT.NetworkTrace.Models;
using XMAT.NetworkTraceCaptureAnalysis;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.XboxLiveCaptureAnalysis;
using Web = XMAT.WebServiceCapture.WebServiceCaptureMethod;

namespace XMAT.ApplicationServices
{
    internal sealed class CaptureInvestigation : ICaptureInvestigation
    {
        private readonly CaptureAppModel _model;
        private readonly Dispatcher _dispatcher;
        private readonly ICaptureQueries _queries;
        private readonly string _reportWorkspaceRoot;

        internal CaptureInvestigation(CaptureAppModel model, Dispatcher dispatcher, ICaptureQueries queries, string reportWorkspaceRoot = null)
        {
            _model = model;
            _dispatcher = dispatcher;
            _queries = queries;
            _reportWorkspaceRoot = reportWorkspaceRoot;
            model.InitializeInvestigationTracking();
        }

        private static readonly string[] WebMetadataFields =
        [
            "id", Web.FieldKey_DeviceName, Web.FieldKey_RequestNumber, Web.FieldKey_RequestTimestamp,
            Web.FieldKey_ResponseTimestamp, Web.FieldKey_RequestHost, Web.FieldKey_RequestMethod, Web.FieldKey_RequestStatus
        ];
        private static readonly string[] WebAnalysisFields =
        [
            .. WebMetadataFields, Web.FieldKey_RequestScheme, Web.FieldKey_RequestPort, Web.FieldKey_RequestPath,
            Web.FieldKey_RequestLineAndHeaders, Web.FieldKey_RequestBody, Web.FieldKey_ResponseLineAndHeaders,
            Web.FieldKey_ResponseBody, Web.FieldKey_ClientIP
        ];
        private static readonly string[] WebDetailFields = [.. WebAnalysisFields, Web.FieldKey_ConnectionId];
        private static readonly string[] PacketFields =
        [
            "id", NetworkTraceCaptureMethod.FieldKey_DeviceName, NetworkTraceCaptureMethod.FieldKey_ProcessId,
            NetworkTraceCaptureMethod.FieldKey_ThreadId, NetworkTraceCaptureMethod.FieldKey_Timestamp,
            NetworkTraceCaptureMethod.FieldKey_MediaType, NetworkTraceCaptureMethod.FieldKey_StartPacket,
            NetworkTraceCaptureMethod.FieldKey_EndPacket, NetworkTraceCaptureMethod.FieldKey_Fragment,
            NetworkTraceCaptureMethod.FieldKey_Send, NetworkTraceCaptureMethod.FieldKey_Receive, NetworkTraceCaptureMethod.FieldKey_Payload
        ];

        public Task<IReadOnlyList<CaptureAnalyzerDescriptor>> GetAnalyzersAsync(CancellationToken cancellationToken = default) =>
            InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult<IReadOnlyList<CaptureAnalyzerDescriptor>>(
                [
                    new(CaptureDataKind.WebProxy, "xbox-live-rules", Localization.GetLocalizedString("WEBCAP_ANALYZER_TITLE"),
                        _model.CaptureAnalyzers.OfType<XboxLiveCaptureAnalyzer>().Any(analyzer => analyzer.IsInitialized)),
                    new(CaptureDataKind.NetworkTrace, "network-statistics", Localization.GetLocalizedString("NETCAP_ANALYSIS_TAB"),
                        _model.CaptureAnalyzers.OfType<NetworkTraceCaptureAnalyzer>().Any())
                ]);
            });

        public Task<IReadOnlyList<CaptureWebSocketAvailability>> GetWebSocketsAsync(
            Guid sourceId, CancellationToken cancellationToken = default) =>
            InvokeAsync(async () =>
            {
                var source = (await _queries.GetSourcesAsync(cancellationToken)).SingleOrDefault(source => source.Id == sourceId)
                    ?? throw new CaptureDataException(CaptureDataError.SourceChanged);
                if (source.Kind != QuerySourceKind.Active)
                    return (IReadOnlyList<CaptureWebSocketAvailability>)Array.Empty<CaptureWebSocketAvailability>();
                return _model.CaptureDeviceContexts
                    .Where(context => !context.IsReadOnly && context.CaptureController is WebServiceDeviceCaptureController &&
                        _model.HasInvestigationSource(context, sourceId))
                    .Select(context =>
                    {
                        var proxy = (WebServiceDeviceCaptureController)context.CaptureController;
                        var target = _model.CaptureCatalog.DescribeTarget(context);
                        return new CaptureWebSocketAvailability(sourceId, target.Id, Label(context.DeviceName, 256),
                            target.CaptureEpoch, !proxy.IsTransitioning && !_model.IsCaptureDataOperationRunning,
                            target.IsRunning, proxy.WebSocketMessages?.Count ?? 0,
                            proxy.IsTransitioning || _model.IsCaptureDataOperationRunning ? "CaptureBusy" : null);
                    }).ToArray();
            });

        public Task<IReadOnlyList<CaptureCompletedReportDescriptor>> GetCompletedReportsAsync(CancellationToken cancellationToken = default) =>
            InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(_model.GetDesktopInvestigationReports());
            });

        public Task<CaptureDataResult> OpenCompletedReportAsync(Guid reportId, CancellationToken cancellationToken = default) =>
            InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(_model.RetainDesktopInvestigation(reportId));
            });

        public Task<CaptureDataResult> SearchAsync(
            Guid sourceId, CaptureDataKind kind, CaptureDataFilter filter, QueryExecutionOptions options,
            CancellationToken cancellationToken = default) => RunAsync(sourceId, kind, filter, options, false, cancellationToken);

        public Task<CaptureDataResult> AnalyzeAsync(
            Guid sourceId, CaptureDataKind kind, CaptureDataFilter filter, QueryExecutionOptions options,
            CancellationToken cancellationToken = default) => RunAsync(sourceId, kind, filter, options, true, cancellationToken);

        private async Task<CaptureDataResult> RunAsync(
            Guid sourceId, CaptureDataKind kind, CaptureDataFilter filter, QueryExecutionOptions options,
            bool analyze, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(options);
            filter ??= new();
            Validate(kind, filter);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(options.Timeout);
            IQuerySession snapshot = null;
            try
            {
                if (kind == CaptureDataKind.WebSocket)
                {
                    if (analyze)
                        throw new CaptureDataException(CaptureDataError.UnsupportedData);
                    return await ReadWebSocketsAsync(sourceId, filter, options, deadline.Token);
                }
                var acquisition = await CreateSnapshotAsync(sourceId, deadline.Token);
                snapshot = acquisition.Snapshot;
                await using var preparation = analyze ? await PrepareAnalysisAsync(sourceId, kind, deadline.Token) : null;
                var result = await ReadSnapshotAsync(snapshot, acquisition.Source, kind, filter, options, analyze, preparation,
                    null, null, null, null, acquisition.Captures, null, deadline.Token);
                snapshot = null;
                return result;
            }
            catch (QueryException exception)
            {
                throw QueryFailure(exception);
            }
            catch (AnalysisWorkLimitException)
            {
                throw new CaptureDataException(CaptureDataError.LimitExceeded);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new CaptureDataException(CaptureDataError.Timeout);
            }
            finally
            {
                if (snapshot != null)
                    await snapshot.DisposeAsync();
            }
        }

        internal async Task<CaptureDataResult> AnalyzeDesktopAsync(
            ICaptureAnalysisRun run, IDeviceCaptureController controller, XboxLiveCaptureAnalyzer analyzer,
            string outputDirectory, CancellationToken cancellationToken = default)
        {
            var source = await _model.CreateDesktopInvestigationSnapshotAsync(controller, cancellationToken);
            var retained = false;
            try
            {
                var result = await ReadSnapshotAsync(source.Snapshot, source.Source, CaptureDataKind.WebProxy, source.Filter,
                    new QueryExecutionOptions(TimeSpan.FromSeconds(60)), true,
                    new Preparation(null, analyzer, null, _dispatcher), source.CaptureId, source.CaptureEpoch, run.Id, outputDirectory,
                    _model.GetInvestigationCaptureReferences(source.Source.Id), null, cancellationToken);
                retained = true;
                _model.RegisterDesktopInvestigation(run, result);
                return result;
            }
            finally
            {
                if (!retained)
                    await source.Snapshot.DisposeAsync();
            }
        }

        internal async Task<CaptureDataResult> AnalyzeDesktopNetworkAsync(
            ICaptureAnalysisRun run, IDeviceCaptureController controller, IReadOnlyList<NetworkTracePacketDataModel> visible,
            NetworkTraceStatisticsOptions networkOptions, CancellationToken cancellationToken = default)
        {
            var ids = visible.Select(packet => packet.RecordRowId).ToHashSet();
            if (ids.Count != visible.Count || ids.Any(id => id < 0))
                throw new CaptureDataException(CaptureDataError.SourceChanged);
            var source = await _model.CreateDesktopInvestigationSnapshotAsync(controller, cancellationToken);
            var retained = false;
            try
            {
                var result = await ReadSnapshotAsync(source.Snapshot, source.Source, CaptureDataKind.NetworkTrace, source.Filter,
                    new QueryExecutionOptions(TimeSpan.FromSeconds(60)), true, new Preparation(null, null, networkOptions, _dispatcher),
                    source.CaptureId, source.CaptureEpoch, run.Id, null,
                    _model.GetInvestigationCaptureReferences(source.Source.Id), ids, cancellationToken);
                retained = true;
                _model.RegisterDesktopInvestigation(run, result);
                return result;
            }
            finally
            {
                if (!retained)
                    await source.Snapshot.DisposeAsync();
            }
        }

        private async Task<CaptureDataResult> ReadSnapshotAsync(
            IQuerySession snapshot, QuerySourceDescriptor source, CaptureDataKind kind, CaptureDataFilter filter,
            QueryExecutionOptions options, bool analyze, Preparation preparation, Guid? captureId, long? captureEpoch,
            long? desktopRunId, string desktopOutputDirectory, IReadOnlyList<CaptureDescriptor> captureReferences,
            IReadOnlySet<long> selectedRecordIds, CancellationToken cancellationToken)
        {
            var fields = kind == CaptureDataKind.WebProxy ? analyze ? WebAnalysisFields : WebMetadataFields : PacketFields;
            var table = kind == CaptureDataKind.WebProxy ? Web.TableName : NetworkTraceCaptureMethod.TableName;
            var sql = $"SELECT {string.Join(",", fields.Select(field => $"\"{field}\""))} FROM \"{table}\" ORDER BY \"id\"";
            await using var data = await snapshot.ExecuteAsync(sql, options, cancellationToken);
            var reader = new CaptureSnapshotReader(data);
            var records = new List<CaptureRecordSummary>();
            var webRecords = new List<IDataRecord>();
            var packets = new List<NetworkTracePacketDataModel>();
            var budget = new DataBudget(options.MaximumResultBytes);
            long malformed = 0;
            long retainedBytes = 0;
            await foreach (var row in reader.RowsAsync(cancellationToken))
            {
                if (selectedRecordIds != null && !selectedRecordIds.Contains(reader.Id(row)))
                    continue;
                CaptureRecordSummary summary;
                IDataRecord full = null;
                NetworkTracePacketDataModel packet = null;
                var malformedMetadata = false;
                try
                {
                    if (kind == CaptureDataKind.WebProxy)
                        summary = WebSummary(reader, row, out malformedMetadata);
                    else
                    {
                        full = await reader.ReadRecordAsync(row, cancellationToken);
                        packet = new NetworkTracePacketDataModel(full);
                        summary = PacketSummary(reader.Id(row), full, packet);
                    }
                }
                catch (Exception exception) when (exception is FormatException or ArgumentException or OverflowException)
                {
                    malformed++;
                    continue;
                }
                if (summary.Timestamp == null || malformedMetadata)
                    malformed++;
                if (filter.DeviceName != null &&
                    (reader.Cell(row, "DeviceName").IsTruncated ||
                    !string.Equals(reader.Text(row, "DeviceName"), filter.DeviceName, StringComparison.Ordinal)))
                    continue;
                if (!Matches(summary, filter))
                    continue;
                budget.Add(summary);
                records.Add(summary);
                if (analyze)
                {
                    retainedBytes += row.Cells.Sum(cell => cell.ByteLength + 128);
                    if (retainedBytes > options.MaximumResultBytes)
                        throw new CaptureDataException(CaptureDataError.LimitExceeded);
                    if (kind == CaptureDataKind.WebProxy)
                        webRecords.Add(await reader.ReadRecordAsync(row, cancellationToken));
                    else
                        packets.Add(packet);
                }
            }
            if (selectedRecordIds != null && records.Count != selectedRecordIds.Count)
                throw new CaptureDataException(CaptureDataError.SourceChanged);
            var metrics = new List<CaptureMetric>();
            var findings = new List<CaptureFinding>();
            var evidence = new Dictionary<long, IReadOnlyList<CaptureEvidence>>();
            var details = new Dictionary<long, CaptureDetailTable>();
            XboxLiveAnalysisResult canonicalWeb = null;
            NetworkTraceStatisticsResult canonicalNetwork = null;
            long eligible = 0;
            if (kind == CaptureDataKind.WebProxy)
            {
                HttpMetrics(records, metrics);
                if (analyze)
                {
                    var canonical = await Task.Run(() => preparation.Web.AnalyzeRecords(
                        webRecords, desktopOutputDirectory, desktopRunId ?? 0, cancellationToken: cancellationToken), cancellationToken);
                    canonicalWeb = canonical;
                    eligible = canonical.ConvertedRecords;
                    ProjectRules(canonical, snapshot.SnapshotId, records, metrics, findings, evidence, details, budget, cancellationToken);
                }
            }
            else if (analyze)
            {
                var canonical = await Task.Run(() => NetworkTraceStatistics.Analyze(
                    packets, preparation.NetworkOptions, cancellationToken: cancellationToken), cancellationToken);
                canonicalNetwork = canonical;
                ProjectNetwork(canonical, metrics, findings);
                foreach (var finding in findings)
                {
                    var detail = new CaptureDetailTable(snapshot.SnapshotId, ["statistic", "observations", "evidenceMapped", "interpretation"],
                        [[finding.RuleId, finding.ViolationCount, false, "Packet matching and timing statistics do not prove packet loss or causation."]]);
                    details.Add(finding.Id, detail);
                    budget.AddBytes(detail.StorageBytes);
                }
            }
            else
                PacketMetrics(records, metrics);
            foreach (var metric in metrics)
                budget.Add(metric);
            foreach (var finding in findings)
                budget.Add(finding);
            cancellationToken.ThrowIfCancellationRequested();
            var times = records.Where(record => record.Timestamp.HasValue).Select(record => record.Timestamp.Value).ToArray();
            var id = Guid.NewGuid();
            var descriptor = new CaptureCompletedReportDescriptor(id, source.Id, snapshot.SnapshotId, snapshot.CapturedAt,
                DateTimeOffset.UtcNow, kind, filter, data.RowCount, records.Count, captureId, captureEpoch, desktopRunId, [],
                selectedRecordIds == null ? CaptureAnalysisScope.ExplicitFilter : CaptureAnalysisScope.DesktopVisibleRecords);
            var recordIds = records.Select(record => record.RecordId).ToArray();
            var report = canonicalWeb != null ? new CaptureAnalysisReport(descriptor, canonicalWeb.Analysis, _reportWorkspaceRoot, recordIds) :
                canonicalNetwork != null ? new CaptureAnalysisReport(descriptor, JsonSerializer.Serialize(new
                {
                    canonicalNetwork.TotalPackets,
                    canonicalNetwork.Options,
                    statistics = canonicalNetwork.Statistics.Select(statistic => new { statistic.Id, statistic.Group, statistic.Value })
                }), _reportWorkspaceRoot, recordIds) : null;
            if (report != null)
                budget.AddBytes(report.StorageBytes);
            cancellationToken.ThrowIfCancellationRequested();
            var result = new CaptureDataResult(id, source.Id, snapshot.SnapshotId, snapshot.CapturedAt, kind, analyze,
                data.RowCount, records.Count, malformed, eligible,
                times.Length == 0 ? null : times.Min(), times.Length == 0 ? null : times.Max(),
                records.AsReadOnly(), metrics.AsReadOnly(), findings.AsReadOnly(),
                new ReadOnlyDictionary<long, IReadOnlyList<CaptureEvidence>>(evidence))
            {
                CaptureId = captureId,
                CaptureEpoch = captureEpoch,
                CaptureReferences = captureReferences,
                Details = new CaptureDataDetails(snapshot, table,
                    kind == CaptureDataKind.WebProxy ? WebDetailFields : PacketFields,
                    records.Select(record => record.RecordId), options, budget.Bytes, details, null, report,
                    desktopRunId.HasValue ? canonicalWeb : null, desktopRunId.HasValue ? canonicalNetwork : null)
            };
            _model.TrackInvestigationResult(result, source);
            return result;
        }

        private Task<(IQuerySession Snapshot, IReadOnlyList<CaptureDescriptor> Captures, QuerySourceDescriptor Source)> CreateSnapshotAsync(
            Guid sourceId, CancellationToken cancellationToken) =>
            _queries is CaptureQueries applicationQueries
                ? applicationQueries.ReadSourceAsync(sourceId, async source =>
                {
                    var descriptor = (await _queries.GetSourcesAsync(cancellationToken)).Single(source => source.Id == sourceId);
                    var snapshot = await source.CreateQuerySessionAsync(cancellationToken);
                    try
                    { return (snapshot, _model.GetInvestigationCaptureReferences(sourceId), descriptor); }
                    catch { await snapshot.DisposeAsync(); throw; }
                }, cancellationToken)
                : CreateUnregisteredSnapshotAsync(sourceId, cancellationToken);

        private async Task<(IQuerySession Snapshot, IReadOnlyList<CaptureDescriptor> Captures, QuerySourceDescriptor Source)> CreateUnregisteredSnapshotAsync(
            Guid sourceId, CancellationToken cancellationToken)
        {
            var snapshot = await _queries.CreateSessionAsync(sourceId, cancellationToken);
            try
            {
                var descriptor = (await _queries.GetSourcesAsync(cancellationToken)).SingleOrDefault(source => source.Id == sourceId)
                    ?? new QuerySourceDescriptor(sourceId, QuerySourceKind.Active, Localization.GetLocalizedString("AGENT_SOURCE_ACTIVE"));
                return (snapshot, Array.Empty<CaptureDescriptor>(), descriptor);
            }
            catch { await snapshot.DisposeAsync(); throw; }
        }

        private Task<Preparation> PrepareAnalysisAsync(Guid sourceId, CaptureDataKind kind, CancellationToken cancellationToken) =>
            InvokeAsync(async () =>
            {
                if (!(await _queries.GetSourcesAsync(cancellationToken)).Any(source => source.Id == sourceId))
                    throw new CaptureDataException(CaptureDataError.SourceChanged);
                var web = _model.CaptureAnalyzers.OfType<XboxLiveCaptureAnalyzer>().FirstOrDefault();
                var network = _model.CaptureAnalyzers.OfType<NetworkTraceCaptureAnalyzer>().FirstOrDefault();
                if (kind == CaptureDataKind.WebProxy ? web?.IsInitialized != true : network == null)
                    throw new CaptureDataException(CaptureDataError.UnsupportedData);
                if (!_model.TryBeginSourceAnalysis(out var lease))
                    throw new CaptureDataException(CaptureDataError.SourceBusy);
                return new Preparation(lease, web, NetworkTraceCaptureAnalyzer.GetOptions(), _dispatcher);
            });

        private Task<CaptureDataResult> ReadWebSocketsAsync(
            Guid sourceId, CaptureDataFilter filter, QueryExecutionOptions options, CancellationToken cancellationToken) =>
            InvokeAsync(async () =>
            {
                var source = (await _queries.GetSourcesAsync(cancellationToken)).SingleOrDefault(source => source.Id == sourceId)
                    ?? throw new CaptureDataException(CaptureDataError.SourceChanged);
                if (source.Kind != QuerySourceKind.Active)
                    throw new CaptureDataException(CaptureDataError.UnsupportedData);
                var context = _model.CaptureDeviceContexts.SingleOrDefault(context =>
                    _model.CaptureCatalog.DescribeTarget(context).Id == filter.CaptureId);
                if (context?.CaptureController is not WebServiceDeviceCaptureController proxy ||
                    context.IsReadOnly || !_model.HasInvestigationSource(context, sourceId))
                    throw new CaptureDataException(CaptureDataError.UnsupportedData);
                var target = _model.CaptureCatalog.DescribeTarget(context);
                if (target.CaptureEpoch != filter.CaptureEpoch ||
                    !string.Equals(context.DeviceName, filter.DeviceName, StringComparison.Ordinal))
                    throw new CaptureDataException(CaptureDataError.SourceChanged);
                if (proxy.IsTransitioning || _model.IsCaptureDataOperationRunning || _model.IsCaptureFileLoadRunning)
                    throw new CaptureDataException(CaptureDataError.SourceBusy);
                var id = Guid.NewGuid();
                var snapshotId = Guid.NewGuid();
                var capturedAt = DateTimeOffset.UtcNow;
                var records = new List<CaptureRecordSummary>();
                var raw = new Dictionary<long, CaptureDetailTable>();
                var budget = new DataBudget(options.MaximumResultBytes);
                long scanned = 0;
                foreach (var message in proxy.WebSocketMessages)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var recordId = ++scanned;
                    var timestamp = new DateTimeOffset(message.Timestamp).ToUniversalTime();
                    var type = message.MessageType is "Text" or "Binary" or "Close" ? message.MessageType : "Unknown";
                    var summary = new CaptureRecordSummary(recordId, context.DeviceName, timestamp, message.RequestNumber,
                        null, null, null, null, null, null, null, null, !message.FromHost, message.FromHost,
                        ConnectionId: message.ConnectionID, MessageType: type, PayloadBytes: message.Payload.LongLength,
                        PayloadTruncated: message.PayloadTruncated, FromHost: message.FromHost);
                    if (!Matches(summary, filter) ||
                        (filter.ConnectionId.HasValue && filter.ConnectionId != message.ConnectionID) ||
                        (filter.FromHost.HasValue && filter.FromHost != message.FromHost) ||
                        (filter.MessageType != null && filter.MessageType != type))
                        continue;
                    budget.Add(summary);
                    var detail = new CaptureDetailTable(snapshotId,
                        ["messageId", "connectionId", "requestNumber", "timestampUtc", "fromHost", "messageType", "payload", "payloadTruncated"],
                        [[recordId, message.ConnectionID, message.RequestNumber, timestamp.ToString("o", CultureInfo.InvariantCulture),
                            message.FromHost, message.MessageType, message.Payload, message.PayloadTruncated]]);
                    budget.AddBytes(detail.StorageBytes);
                    raw.Add(recordId, detail);
                    records.Add(summary);
                }
                var metrics = new[]
                {
                    new CaptureMetric("websocket.retained-messages", null, scanned, null, "messages"),
                    new CaptureMetric("websocket.matched-messages", null, records.Count, null, "messages"),
                    new CaptureMetric("websocket.stored-payload-bytes", null, records.Sum(record => record.PayloadBytes ?? 0), null, "bytes")
                };
                foreach (var metric in metrics)
                    budget.Add(metric);
                var result = new CaptureDataResult(id, sourceId, snapshotId, capturedAt, CaptureDataKind.WebSocket, false,
                    scanned, records.Count, 0, 0,
                    records.Count == 0 ? null : records.Min(record => record.Timestamp),
                    records.Count == 0 ? null : records.Max(record => record.Timestamp),
                    records.AsReadOnly(), Array.AsReadOnly(metrics), Array.Empty<CaptureFinding>(),
                    new ReadOnlyDictionary<long, IReadOnlyList<CaptureEvidence>>(new Dictionary<long, IReadOnlyList<CaptureEvidence>>()))
                {
                    CaptureId = target.Id,
                    CaptureEpoch = target.CaptureEpoch,
                    CaptureReferences = Array.AsReadOnly(new[] { target }),
                    SnapshotKind = "ControllerSession",
                    Details = new CaptureDataDetails(null, null, null, records.Select(record => record.RecordId),
                        options, budget.Bytes, new(), raw, null)
                };
                _model.TrackInvestigationResult(result, source);
                return result;
            });

        private static CaptureRecordSummary WebSummary(CaptureSnapshotReader reader, QueryRow row, out bool malformed)
        {
            var requestTime = Time(reader.Text(row, Web.FieldKey_RequestTimestamp));
            var responseText = reader.Text(row, Web.FieldKey_ResponseTimestamp);
            var responseTime = Time(responseText);
            double? duration = requestTime.HasValue && responseTime >= requestTime
                ? (responseTime.Value - requestTime.Value).TotalMilliseconds : null;
            var statusText = reader.Text(row, Web.FieldKey_RequestStatus);
            int? status = int.TryParse(statusText, NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
                number is >= 100 and <= 599 ? number : null;
            var device = reader.Text(row, Web.FieldKey_DeviceName);
            var hostText = reader.Text(row, Web.FieldKey_RequestHost);
            var host = HostAuthority.TryNormalizeHost(hostText, out var normalized) ? normalized : null;
            var method = reader.Text(row, Web.FieldKey_RequestMethod);
            if (method == null || method.Length > 32 || !method.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
                method = null;
            malformed = requestTime == null ||
                (!string.IsNullOrEmpty(responseText) && (responseTime == null || responseTime < requestTime)) ||
                (!string.IsNullOrEmpty(statusText) && status == null) ||
                (!string.IsNullOrEmpty(hostText) && host == null) || method == null;
            return new(reader.Id(row), Label(device, 256), requestTime, reader.Number(row, Web.FieldKey_RequestNumber),
                host, method, status, duration, null, null, null, null, false, false,
                row.Cells.Take(WebMetadataFields.Length).Any(cell => cell.IsTruncated) || device?.Length > 256);
        }

        private static CaptureRecordSummary PacketSummary(long id, IDataRecord record, NetworkTracePacketDataModel packet) =>
            new(id, Label(record.Str("DeviceName"), 256), Time(record.Str("Timestamp")), null, null, null, null, null,
                NetworkTraceStatistics.Protocol(packet), NetworkTraceStatistics.Source(packet), NetworkTraceStatistics.Destination(packet),
                packet.Payload.LongLength, (packet.Flags & NetworkPacketFlags.Send) != 0,
                (packet.Flags & NetworkPacketFlags.Receive) != 0, record.Str("DeviceName")?.Length > 256);

        private static bool Matches(CaptureRecordSummary record, CaptureDataFilter filter) =>
            (filter.DeviceName == null || string.Equals(record.DeviceName, filter.DeviceName, StringComparison.Ordinal)) &&
            (filter.Host == null || string.Equals(record.Host, NormalizeHost(filter.Host), StringComparison.OrdinalIgnoreCase)) &&
            (filter.Method == null || string.Equals(record.Method, filter.Method, StringComparison.OrdinalIgnoreCase)) &&
            (!filter.StatusCode.HasValue || record.StatusCode == filter.StatusCode) &&
            (!filter.AfterUtc.HasValue || record.Timestamp >= filter.AfterUtc) &&
            (!filter.BeforeUtc.HasValue || record.Timestamp < filter.BeforeUtc) &&
            (!filter.MinimumDurationMilliseconds.HasValue || record.DurationMilliseconds >= filter.MinimumDurationMilliseconds) &&
            (!filter.Protocol.HasValue || record.Protocol == filter.Protocol) &&
            (filter.SourceAddress == null || record.SourceAddress == filter.SourceAddress) &&
            (filter.DestinationAddress == null || record.DestinationAddress == filter.DestinationAddress);

        private static void Validate(CaptureDataKind kind, CaptureDataFilter filter)
        {
            if (!Enum.IsDefined(kind) ||
                (kind != CaptureDataKind.WebSocket && (filter.CaptureId.HasValue || filter.CaptureEpoch.HasValue ||
                    filter.ConnectionId.HasValue || filter.FromHost.HasValue || filter.MessageType != null)) ||
                (kind == CaptureDataKind.WebSocket && (!filter.CaptureId.HasValue || !filter.CaptureEpoch.HasValue ||
                    filter.DeviceName == null || filter.Host != null || filter.Method != null || filter.StatusCode.HasValue ||
                    filter.MinimumDurationMilliseconds.HasValue || filter.Protocol.HasValue ||
                    filter.SourceAddress != null || filter.DestinationAddress != null)) ||
                (kind == CaptureDataKind.WebProxy && (filter.Protocol.HasValue || filter.SourceAddress != null || filter.DestinationAddress != null)) ||
                (kind == CaptureDataKind.NetworkTrace && (filter.Host != null || filter.Method != null || filter.StatusCode.HasValue ||
                    filter.MinimumDurationMilliseconds.HasValue)) ||
                (filter.Host != null && NormalizeHost(filter.Host) == null))
                throw new CaptureDataException(CaptureDataError.InvalidFilter);
        }

        private static string NormalizeHost(string value) => HostAuthority.TryNormalizeHost(value, out var host) ? host : null;
        private static string Label(string value, int maximum) => value?.Length > maximum ? value[..maximum] : value;
        private static string ConsoleName(string value) => IPAddress.TryParse(value, out var address) ? address.ToString() : null;
        private static DateTimeOffset? Time(string value)
        {
            if (string.IsNullOrEmpty(value))
                return null;
            if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) &&
                !DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.RoundtripKind, out date))
                return null;
            return new DateTimeOffset(date).ToUniversalTime();
        }

        private static void HttpMetrics(IReadOnlyList<CaptureRecordSummary> records, List<CaptureMetric> metrics)
        {
            metrics.Add(new("http.requests", null, records.Count, null, "requests"));
            foreach (var group in records.GroupBy(record => record.StatusCode).OrderBy(group => group.Key))
                metrics.Add(new("http.status", group.Key?.ToString(CultureInfo.InvariantCulture), group.LongCount(), null, "requests"));
            foreach (var group in records.GroupBy(record => record.Host, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
                metrics.Add(new("http.host", group.Key, group.LongCount(), null, "requests"));
            foreach (var group in records.GroupBy(record => record.Method).OrderBy(group => group.Key, StringComparer.Ordinal))
                metrics.Add(new("http.method", group.Key, group.LongCount(), null, "requests"));
            var durations = records.Where(record => record.DurationMilliseconds.HasValue)
                .Select(record => record.DurationMilliseconds.Value).Order().ToArray();
            if (durations.Length == 0)
                return;
            metrics.Add(new("http.duration.mean", null, durations.Length, durations.Average(), "milliseconds"));
            metrics.Add(new("http.duration.minimum", null, durations.Length, durations[0], "milliseconds"));
            metrics.Add(new("http.duration.maximum", null, durations.Length, durations[^1], "milliseconds"));
            metrics.Add(new("http.duration.p50", null, durations.Length, durations[(int)Math.Ceiling(durations.Length * .5) - 1], "milliseconds"));
            metrics.Add(new("http.duration.p95", null, durations.Length, durations[(int)Math.Ceiling(durations.Length * .95) - 1], "milliseconds"));
        }

        private static void PacketMetrics(IReadOnlyList<CaptureRecordSummary> records, List<CaptureMetric> metrics)
        {
            metrics.Add(new("packet.count", null, records.Count, null, "packets"));
            metrics.Add(new("packet.bytes", null, records.Sum(record => record.PacketBytes ?? 0), null, "bytes"));
            foreach (var group in records.GroupBy(record => record.Protocol).OrderBy(group => group.Key))
                metrics.Add(new("packet.protocol", group.Key?.ToString(CultureInfo.InvariantCulture), group.LongCount(), null, "packets"));
        }

        private static void ProjectRules(
            XboxLiveAnalysisResult canonical, Guid snapshotId, IReadOnlyList<CaptureRecordSummary> records, List<CaptureMetric> metrics,
            List<CaptureFinding> findings, Dictionary<long, IReadOnlyList<CaptureEvidence>> evidence,
            Dictionary<long, CaptureDetailTable> details, DataBudget budget, CancellationToken cancellationToken)
        {
            var numbers = records.ToDictionary(record => record.RecordId, record => record.RequestNumber);
            var endpointIds = new Dictionary<EndpointKey, Guid>();
            foreach (var console in canonical.Analysis.Consoles.OrderBy(console => console.ConsoleId, StringComparer.Ordinal))
            {
                var consoleId = Guid.NewGuid();
                foreach (var rule in console.Rules.Where(rule => rule != null)
                    .OrderBy(rule => rule.Identity.Id, StringComparer.Ordinal).ThenBy(rule => rule.RuleType, StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var level in rule.Violations.GroupBy(violation => violation.m_level).OrderBy(group => group.Key))
                    {
                        var calls = level.SelectMany(violation => violation.m_violatingCalls).ToArray();
                        var mapped = new Dictionary<long, bool>();
                        long unmapped = 0;
                        foreach (var call in calls)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (call.BreadCrumb != null && canonical.RecordByBreadcrumb.TryGetValue(call.BreadCrumb, out var recordId))
                                mapped[recordId] = mapped.GetValueOrDefault(recordId) || call.EndpointKind == EndpointKind.SyntheticTelemetry;
                            else
                                unmapped++;
                        }
                        var id = findings.Count + 1L;
                        var references = mapped.OrderBy(pair => pair.Key).Select(pair =>
                            new CaptureEvidence(pair.Key, numbers.GetValueOrDefault(pair.Key), pair.Value)).ToArray();
                        foreach (var reference in references)
                            budget.Add(reference);
                        evidence.Add(id, Array.AsReadOnly(references));
                        var detail = CaptureFindingDetails.Project(snapshotId, rule, level, canonical.RecordByBreadcrumb);
                        budget.AddBytes(detail.StorageBytes);
                        details.Add(id, detail);
                        if (!endpointIds.TryGetValue(rule.Identity, out var endpointId))
                        {
                            endpointId = Guid.NewGuid();
                            endpointIds.Add(rule.Identity, endpointId);
                        }
                        findings.Add(new(id, ConsoleName(console.ConsoleId),
                            rule.Identity.Kind == EndpointKind.SyntheticTelemetry && rule.Identity.LogicalName == "inGameEvents"
                                ? "inGameEvents" : NormalizeHost(rule.ActionableHost), rule.Identity.Kind.ToString(), consoleId, endpointId, rule.RuleType,
                            Label(rule.RuleName, 160), level.Key switch
                            {
                                ViolationLevel.Error => CaptureFindingSeverity.Error,
                                ViolationLevel.Warning => CaptureFindingSeverity.Warning,
                                _ => CaptureFindingSeverity.Info
                            }, level.LongCount(), references.LongLength, unmapped));
                    }
                }
            }
            metrics.Add(new("analysis.eligible-service-calls", null, canonical.ConvertedRecords, null, "calls"));
            metrics.Add(new("analysis.skipped-service-calls", null, canonical.SkippedRecords, null, "calls"));
            metrics.Add(new("analysis.consoles", null, canonical.Analysis.Consoles.Count, null, "consoles"));
            metrics.Add(new("analysis.rules", null, canonical.Analysis.Consoles.Sum(console => (long)console.Rules.Count), null, "rules"));
        }

        private static void ProjectNetwork(
            NetworkTraceStatisticsResult canonical, List<CaptureMetric> metrics, List<CaptureFinding> findings)
        {
            foreach (var statistic in canonical.Statistics)
            {
                var unit = statistic.Id switch
                {
                    "packet.sent.bytes" or "packet.received.bytes" or "packet.source.average-bytes" => "bytes",
                    "packet.send-second-over-threshold" => "source-second-buckets",
                    "packet.matching-payload-pairs" => "pairs",
                    _ => "packets"
                };
                metrics.Add(new(statistic.Id, statistic.Group, statistic.Value, null, unit));
                if (statistic.Value > 0 && statistic.Id is
                    "packet.udp-over-mtu" or "packet.send-second-over-threshold" or "packet.matching-payload-pairs")
                {
                    findings.Add(new(findings.Count + 1L, null, null, "PacketStatistic", null, null, statistic.Id, statistic.Id,
                        CaptureFindingSeverity.Warning, statistic.Value, 0, statistic.Value));
                }
            }
            metrics.Add(new("packet.maximum-mtu", null, canonical.Options.MaximumMtu, null, "bytes"));
            metrics.Add(new("packet.send-second-threshold", null, canonical.Options.PacketsPerSecondThreshold, null, "packets"));
            metrics.Add(new("packet.matching-payload-window", null, canonical.Options.DuplicateWindowMilliseconds, null, "milliseconds"));
        }

        private Task<T> InvokeAsync<T>(Func<Task<T>> action) => App.RunOnUiAsync(_dispatcher, action);

        private static CaptureDataException QueryFailure(QueryException exception) => new(exception.Error switch
        {
            QueryError.SourceBusy => CaptureDataError.SourceBusy,
            QueryError.SourceChanged => CaptureDataError.SourceChanged,
            QueryError.Timeout => CaptureDataError.Timeout,
            QueryError.ResultLimit or QueryError.PageTooSmall => CaptureDataError.LimitExceeded,
            _ => CaptureDataError.UnsupportedData
        });

        private sealed record Preparation(
            IDisposable Lease, XboxLiveCaptureAnalyzer Web, NetworkTraceStatisticsOptions NetworkOptions,
            Dispatcher Dispatcher) : IAsyncDisposable
        {
            public async ValueTask DisposeAsync()
            {
                if (Lease == null)
                    return;
                await App.RunOnUiAsync(Dispatcher, Lease.Dispose);
            }
        }

        private sealed class DataBudget(long maximum)
        {
            private long _bytes = 1024;
            internal long Bytes => _bytes;
            private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
            {
                Converters = { new JsonStringEnumConverter() }
            };
            internal void Add<T>(T value)
            {
                AddBytes(JsonSerializer.SerializeToUtf8Bytes(value, Options).LongLength);
            }
            internal void AddBytes(long bytes)
            {
                _bytes = checked(_bytes + bytes);
                if (_bytes > maximum)
                    throw new CaptureDataException(CaptureDataError.LimitExceeded);
            }
        }
    }
}
