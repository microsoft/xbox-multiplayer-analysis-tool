// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CaptureAnalysisEngine;
using XMAT.DataAbstractionLayer;
using XMAT.Operations.Captures;
using XMAT.Operations.Processes;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using XMAT.XboxLiveCaptureAnalysis;

namespace XMAT.Operations.Analysis
{
    internal sealed class AnalyzeCaptureOperation :
        IOperation<AnalyzeCaptureRequest, AnalysisResult>
    {
        internal enum RuleAggregateFailure
        {
            None,
            Cancelled,
            WorkLimit,
        }

        private readonly Lazy<AnalysisAssetSet> _assets;
        private readonly CaptureInputReader _inputReader = new();

        internal const string WorkLimitCode =
            "analysis.rules.work-limit";
        internal const string ProcessSelectionConsistencyCode =
            "analysis.process-filter.consistency";

        internal AnalyzeCaptureOperation()
        {
            _assets = new Lazy<AnalysisAssetSet>(
                () => AnalysisAssets.Load(AppContext.BaseDirectory),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        internal AnalyzeCaptureOperation(AnalysisAssetSet assets)
        {
            ArgumentNullException.ThrowIfNull(assets);
            _assets = new Lazy<AnalysisAssetSet>(
                () => assets,
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        internal Action<Type, string> ExceptionObserver { get; set; }

        internal Func<AnalysisWorkBudget> WorkBudgetFactory { get; set; } =
            static () => new AnalysisWorkBudget();

        internal Func<RequestBodyIdentityInterner>
            BodyIdentityFactory { get; set; } =
                static () =>
                    new RequestBodyIdentityInterner();

        internal Func<
            string,
            SqLtSchemaProfile,
            SqLtReadonlyDatabase> CaptureDatabaseFactory
        {
            get => _inputReader.CaptureDatabaseFactory;
            set => _inputReader.CaptureDatabaseFactory = value;
        }

        public async Task<OperationResult<AnalysisResult>> ExecuteAsync(
            AnalyzeCaptureRequest request,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            context ??= OperationContext.None;
            try
            {
                return await Task.Run(
                    () => ExecuteCore(request, context, cancellationToken),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Failure(
                    OperationOutcome.Cancelled,
                    "analysis.cancelled",
                    OperationDiagnosticSeverity.Info,
                    "Analysis was cancelled.");
            }
            catch (AnalysisWorkLimitException)
            {
                return Failure(
                    OperationOutcome.InvalidInput,
                    WorkLimitCode,
                    OperationDiagnosticSeverity.Error,
                    "Exact rule evaluation exceeds the safe work limit.");
            }
            catch (CaptureInputException ex)
            {
                return Failure(
                    OperationOutcome.InvalidInput,
                    ex.Code,
                    OperationDiagnosticSeverity.Error,
                    ex.DiagnosticMessage);
            }
            catch (AnalysisAssetException ex)
            {
                return Failure(
                    OperationOutcome.PrerequisiteMissing,
                    ex.Code,
                    OperationDiagnosticSeverity.Error,
                    "Required analysis data is unavailable.");
            }
            catch (ProcessSelectionConsistencyException ex)
            {
                // A broken internal invariant, not bad input, so xmat keeps exit code 70; the message names no value.
                ObserveException(ex);
                return Failure(
                    OperationOutcome.InternalError,
                    ProcessSelectionConsistencyCode,
                    OperationDiagnosticSeverity.Error,
                    ProcessSelectionConsistencyException.Description);
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                ObserveException(ex);
                return Failure(
                    OperationOutcome.InternalError,
                    "analysis.internal",
                    OperationDiagnosticSeverity.Error,
                    "Analysis could not be completed because of an internal failure.");
            }
        }

        private void ObserveException(Exception exception)
        {
            var observer = ExceptionObserver;
            if (observer == null)
            {
                return;
            }
            try
            {
                observer(
                    exception.GetType(),
                    exception.StackTrace ?? string.Empty);
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                // GUI diagnostics are best effort and never affect the operation result.
            }
        }

        private OperationResult<AnalysisResult> ExecuteCore(
            AnalyzeCaptureRequest request,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            if (request?.Input == null)
            {
                throw new CaptureInputException(
                    "analysis.input.missing",
                    "An analysis input is required.");
            }

            var options = request.Options ?? new AnalysisOptions();
            var legacyOutputFailure = ValidateLegacyOutput(
                options,
                context);
            if (legacyOutputFailure != null)
            {
                return OperationResult<AnalysisResult>.Failure(
                    OperationOutcome.PrerequisiteMissing,
                    legacyOutputFailure);
            }
            var limits = request.Input.IsRecords
                ? null
                : options.Limits ?? AnalysisLimits.Default;
            if (limits != null)
            {
                CaptureInputReader.ValidateLimits(limits);
            }
            var selection = new SessionSelection(options.ProcessFilter);
            cancellationToken.ThrowIfCancellationRequested();

            var diagnostics = new List<OperationDiagnostic>();
            using LoadedInput loaded = _inputReader.Read(
                request.Input,
                limits,
                diagnostics,
                cancellationToken);
            var indexedDevices = loaded.Devices
                .Select((device, index) => new IndexedDevice($"d{index + 1}", device))
                .ToList();
            var selectedDevices = SelectDevices(
                indexedDevices,
                options.DeviceId);
            foreach (IndexedDevice indexedDevice in
                indexedDevices.Where(indexedDevice =>
                    !selectedDevices.Contains(
                        indexedDevice)))
            {
                indexedDevice.Device
                    .ReleaseOwnedRecords();
            }
            if (options.ProduceLegacyReports && selectedDevices.Count != 1)
            {
                throw new CaptureInputException(
                    "analysis.legacy.device-count",
                    "Legacy report generation requires exactly one device.");
            }

            var analyzer = _assets.Value.CreateAnalyzer(
                options.ProduceLegacyReports);
            AnalysisWorkBudget workBudget =
                WorkBudgetFactory?.Invoke() ??
                throw new InvalidOperationException(
                    "The analysis work budget factory returned no budget.");
            RequestBodyIdentityInterner bodyIdentities =
                BodyIdentityFactory?.Invoke() ??
                throw new InvalidOperationException(
                    "The request-body identity factory returned no interner.");
            var deviceAnalyses = new List<DeviceAnalysis>();
            try
            {
                foreach (var device in selectedDevices)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        deviceAnalyses.Add(AnalyzeDevice(
                            analyzer,
                            device,
                            options,
                            context,
                            workBudget,
                            bodyIdentities,
                            selection,
                            cancellationToken));
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (AnalysisWorkLimitException)
                    {
                        throw;
                    }
                    catch (Exception ex) when (
                        !request.Input.IsRecords &&
                        !ExceptionUtilities.IsFatal(ex))
                    {
                        throw new CaptureInputException(
                            "analysis.input.malformed",
                            "The analysis input contains malformed capture data.",
                            ex);
                    }
                }
            }
            finally
            {
                // Device conversion and rules alternate, so representatives remain available
                // for later devices and are released immediately after the last device run.
                bodyIdentities.ClearRepresentatives();
            }

            FilterCaptureSessionsResult selected = selection.Complete(cancellationToken);
            var labeler = new CanonicalServiceLabeler(deviceAnalyses);
            var deviceSummaries = new List<DeviceSummary>();
            var totalCalls = 0;
            var totalFindings = 0;
            LegacyAnalysisReports legacy = null;

            foreach (var deviceAnalysis in deviceAnalyses)
            {
                var consoles = CreateConsoleSummaries(
                    deviceAnalysis,
                    labeler,
                    out var deviceCalls,
                    out var deviceFindings);
                totalCalls += deviceCalls;
                totalFindings += deviceFindings;
                deviceSummaries.Add(new DeviceSummary(
                    deviceAnalysis.DeviceId,
                    consoles));

                if (options.ProduceLegacyReports)
                {
                    legacy = new LegacyAnalysisReports(
                        deviceAnalysis.ConsoleResults.ToDictionary(
                            item => item.Key,
                            item => item.Value.Reports));
                }
            }

            var summary = new AnalysisSummary(
                InputKindName(loaded.Kind),
                loaded.InputSha256,
                deviceSummaries,
                totalCalls,
                totalFindings);
            if (totalCalls == 0 ||
                deviceSummaries.All(device => device.Consoles.Count == 0))
            {
                AddDiagnostic(
                    diagnostics,
                    new OperationDiagnostic(
                        "analysis.no-data",
                        OperationDiagnosticSeverity.Info,
                        "The input contains no analyzable data."));

                // The selection still completed, so adapters can say how many sessions the process filter hid.
                return new OperationResult<AnalysisResult>(
                    OperationOutcome.NoAnalyzableData,
                    new AnalysisResult(summary)
                    {
                        ProcessScope = selected.Scope,
                        IncludedKeys = selected.IncludedKeys,
                    },
                    diagnostics);
            }

            ReportProgress(context, "analysis.complete", 1);
            return OperationResult<AnalysisResult>.Success(
                new AnalysisResult(summary)
                {
                    Legacy = legacy,
                    ProcessScope = selected.Scope,
                    IncludedKeys = selected.IncludedKeys,
                },
                diagnostics);
        }

        private static OperationDiagnostic ValidateLegacyOutput(
            AnalysisOptions options,
            OperationContext context)
        {
            if (!options.ProduceLegacyReports)
            {
                return null;
            }
            if (string.IsNullOrWhiteSpace(context.WorkingDirectory))
            {
                return new OperationDiagnostic(
                    "analysis.output.not-allowed",
                    OperationDiagnosticSeverity.Error,
                    "Legacy analysis output is not allowed in a no-write context.");
            }

            try
            {
                _ = Path.GetFullPath(context.WorkingDirectory);
            }
            catch (Exception ex) when (
                ex is ArgumentException or
                NotSupportedException or
                IOException)
            {
                return new OperationDiagnostic(
                    "analysis.output.invalid",
                    OperationDiagnosticSeverity.Error,
                    "The legacy analysis output location is invalid.");
            }
            return null;
        }

        private static DeviceAnalysis AnalyzeDevice(
            TraceAnalyzer analyzer,
            IndexedDevice device,
            AnalysisOptions options,
            OperationContext context,
            AnalysisWorkBudget workBudget,
            RequestBodyIdentityInterner bodyIdentities,
            SessionSelection selection,
            CancellationToken cancellationToken)
        {
            ReportProgress(context, "analysis.convert", null);
            var serviceCallItems = new List<ServiceCallItem>();
            try
            {
                foreach (IDataRecord record in
                    device.Device.RecordsForConversion())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!selection.Include(device.Id, record))
                    {
                        continue;
                    }

                    ServiceCallItem item =
                        WebServiceCaptureRecordConverter.Convert(
                            record,
                            bodyIdentities);
                    if (item != null)
                    {
                        serviceCallItems.Add(item);
                    }
                }
            }
            finally
            {
                device.Device.ReleaseOwnedRecords();
            }

            ReportProgress(context, "analysis.group", null);
            var serviceCallData = new ServiceCallData(false);
            serviceCallData.CreateFromServiceCallItems(
                serviceCallItems,
                cancellationToken);

            ReportProgress(context, "analysis.convert-uris", null);
            analyzer.RunUriConverterOnData(
                serviceCallData,
                workBudget,
                cancellationToken);

            var outputDirectory = options.ProduceLegacyReports
                ? context.WorkingDirectory
                : null;
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            ReportProgress(context, "analysis.rules", null);
            var rulesEngine =
                new RulesEngine(
                    false,
                    workBudget);
            Dictionary<string, TraceConsoleAnalysis>
                consoleResults;
            try
            {
                consoleResults = analyzer.RunDetailed(
                    rulesEngine,
                    serviceCallData,
                    outputDirectory,
                    options.ProduceLegacyReports,
                    cancellationToken);
            }
            catch (AggregateException ex)
            {
                bool cancellationRequested =
                    cancellationToken
                        .IsCancellationRequested;
                RuleAggregateFailure failure =
                    ClassifyRuleAggregate(
                        ex,
                        cancellationRequested);
                if (failure ==
                    RuleAggregateFailure.Cancelled)
                {
                    throw new OperationCanceledException(
                        cancellationToken);
                }
                if (failure ==
                    RuleAggregateFailure.WorkLimit)
                {
                    throw new AnalysisWorkLimitException();
                }
                throw;
            }

            if (!string.IsNullOrEmpty(outputDirectory))
            {
                var prefix = new DirectoryInfo(outputDirectory).Name;
                ReportProgress(context, "analysis.dump-data", null);
                rulesEngine.DumpServiceCallData(
                    Path.Combine(
                        outputDirectory,
                        $"{prefix}_service_call_data.txt"),
                    serviceCallData);
                ReportProgress(context, "analysis.dump-items", null);
                rulesEngine.DumpServiceCallItems(
                    Path.Combine(
                        outputDirectory,
                        $"{prefix}_service_call_items.txt"),
                    serviceCallItems);
                ReportProgress(context, "analysis.dump-results", null);
                rulesEngine.DumpResultsToFile(
                    Path.Combine(
                        outputDirectory,
                        $"{prefix}_results.txt"));
            }

            return new DeviceAnalysis(
                device.Id,
                serviceCallItems,
                serviceCallData,
                consoleResults);
        }

        internal static RuleAggregateFailure
            ClassifyRuleAggregate(
                AggregateException exception,
                bool cancellationRequested)
        {
            IReadOnlyCollection<Exception> inner =
                exception.Flatten().InnerExceptions;
            if (inner.Count == 0 ||
                inner.Any(item =>
                    item is not (
                        OperationCanceledException or
                        AnalysisWorkLimitException)))
            {
                return RuleAggregateFailure.None;
            }
            if (cancellationRequested)
            {
                return RuleAggregateFailure.Cancelled;
            }
            return inner.Any(item =>
                item is AnalysisWorkLimitException)
                    ? RuleAggregateFailure.WorkLimit
                    : RuleAggregateFailure.None;
        }

        private static IReadOnlyList<ConsoleSummary> CreateConsoleSummaries(
            DeviceAnalysis analysis,
            CanonicalServiceLabeler labeler,
            out int totalCalls,
            out int totalFindings)
        {
            var callCounts = analysis.ServiceCallItems
                .Where(item => !string.IsNullOrEmpty(item.ConsoleIP))
                .GroupBy(item => item.ConsoleIP, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Count(),
                    StringComparer.Ordinal);
            var summaries = new List<ConsoleSummary>();
            totalCalls = 0;
            totalFindings = 0;
            var consoleIndex = 0;

            foreach (var console in analysis.ConsoleResults.OrderBy(
                item => item.Key,
                StringComparer.Ordinal))
            {
                var findings = console.Value.RuleResults
                    .SelectMany(result => result.Violations.Select(
                        violation => new
                        {
                            result.RuleName,
                            Identity = result.Identity,
                            Service = labeler.Label(
                                result.Identity),
                            Level = violation.m_level.ToString(),
                        }))
                    .GroupBy(
                        finding => new
                        {
                            finding.RuleName,
                            finding.Identity,
                            finding.Service,
                            finding.Level,
                        })
                    .Select(group => new
                    {
                        group.Key.Identity,
                        Finding = new FindingSummary(
                            group.Key.RuleName,
                            group.Key.Service,
                            group.Key.Level,
                            group.Count()),
                    })
                    .OrderBy(
                        item => item.Finding.Rule,
                        StringComparer.Ordinal)
                    .ThenBy(
                        item => item.Finding.Service,
                        StringComparer.Ordinal)
                    .ThenBy(
                        item => item.Finding.Level,
                        StringComparer.Ordinal)
                    .ThenBy(
                        item => item.Identity.LogicalName,
                        StringComparer.Ordinal)
                    .ThenBy(item => item.Identity.Kind)
                    .Select(item => item.Finding)
                    .ToList();
                callCounts.TryGetValue(console.Key, out var calls);
                consoleIndex++;
                totalCalls += calls;
                totalFindings += findings.Sum(finding => finding.Count);
                summaries.Add(new ConsoleSummary(
                    $"c{consoleIndex}",
                    calls,
                    findings));
            }

            return summaries;
        }

        private static IReadOnlyList<IndexedDevice> SelectDevices(
            IReadOnlyList<IndexedDevice> devices,
            string requestedDeviceId)
        {
            if (string.IsNullOrEmpty(requestedDeviceId))
            {
                return devices;
            }

            var selected = devices.FirstOrDefault(device =>
                string.Equals(
                    device.Id,
                    requestedDeviceId,
                    StringComparison.Ordinal));
            if (selected == null)
            {
                throw new CaptureInputException(
                    "analysis.device.not-found",
                    "The requested device was not found.");
            }
            return new[] { selected };
        }

        private static string InputKindName(AnalysisInputKind kind)
        {
            return kind switch
            {
                AnalysisInputKind.Saz => "saz",
                AnalysisInputKind.XmatCapture => "xmatcap",
                AnalysisInputKind.Records => "records",
                _ => throw new InvalidOperationException(),
            };
        }

        private static void AddDiagnostic(
            ICollection<OperationDiagnostic> diagnostics,
            OperationDiagnostic diagnostic)
        {
            diagnostics.Add(diagnostic);
        }

        private static OperationResult<AnalysisResult> Failure(
            OperationOutcome outcome,
            string code,
            OperationDiagnosticSeverity severity,
            string message)
        {
            var diagnostic = new OperationDiagnostic(code, severity, message);
            return OperationResult<AnalysisResult>.Failure(outcome, diagnostic);
        }

        private static void ReportProgress(
            OperationContext context,
            string phase,
            double? fraction)
        {
            try
            {
                context.Progress.Report(new OperationProgress(phase, fraction));
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                // Progress is best effort and cannot fail the operation.
            }
        }

        private sealed record IndexedDevice(
            string Id,
            LoadedDevice Device);

        private sealed record DeviceAnalysis(
            string DeviceId,
            IReadOnlyList<ServiceCallItem> ServiceCallItems,
            ServiceCallData ServiceCallData,
            IReadOnlyDictionary<string, TraceConsoleAnalysis> ConsoleResults);

        private sealed class CanonicalServiceLabeler
        {
            private readonly Dictionary<EndpointKey, string> _labels =
                new();

            internal CanonicalServiceLabeler(IEnumerable<DeviceAnalysis> analyses)
            {
                var known = analyses
                    .SelectMany(analysis =>
                        analysis.ServiceCallData.m_endpointToService ??
                        new Dictionary<string, Tuple<string, string, string>>())
                    .GroupBy(item => item.Key, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.First().Value?.Item1,
                        StringComparer.Ordinal);
                var analyzedEndpoints = analyses
                    .SelectMany(analysis =>
                        analysis.ConsoleResults.Values)
                    .SelectMany(console =>
                        console.RuleResults)
                    .Select(result => result.Identity)
                    .Distinct()
                    .ToList();
                foreach (EndpointKey endpoint in
                    analyzedEndpoints)
                {
                    if (endpoint.LogicalName != null &&
                        known.TryGetValue(
                            endpoint.LogicalName,
                            out string service) &&
                        !string.IsNullOrWhiteSpace(service))
                    {
                        _labels[endpoint] = service;
                    }
                }

                foreach (EndpointKey endpoint in
                    analyzedEndpoints.Where(endpoint =>
                        endpoint.LogicalName != null &&
                        (endpoint.LogicalName.Contains(
                            "playfab.com") ||
                         endpoint.LogicalName.Contains(
                            "playfabapi.com"))))
                {
                    _labels[endpoint] = "playfab_service";
                }

                var unknown = analyzedEndpoints
                    .Where(endpoint =>
                        !_labels.ContainsKey(endpoint))
                    .OrderBy(
                        endpoint => endpoint.LogicalName,
                        StringComparer.Ordinal)
                    .ThenBy(endpoint => endpoint.Kind)
                    .ToList();
                for (var index = 0; index < unknown.Count; index++)
                {
                    _labels[unknown[index]] = $"service-{index + 1}";
                }
            }

            internal string Label(EndpointKey endpoint)
            {
                return _labels.TryGetValue(
                    endpoint,
                    out string label)
                    ? label
                    : "service-0";
            }
        }

        // Selects the sessions to analyze by client process while the records are converted, so a streamed record is read
        // once. Every record of a selected device is counted before service conversion; the shared selection operation
        // then derives the scope from the same body-free references, and the analyzed rows must be exactly its keys.
        private sealed class SessionSelection
        {
            private readonly ProcessFilter _filter;
            private readonly Guid _sourceId = Guid.NewGuid();
            private readonly List<CaptureSessionRef> _sessions = new();
            private readonly HashSet<CaptureRowKey> _included = new();
            private long _incarnation;

            internal SessionSelection(ProcessFilterSpec specification)
            {
                Specification = specification ?? ProcessFilterSpec.Empty;
                try
                {
                    _filter = ProcessFilter.Compile(Specification);
                }
                catch (ArgumentException ex)
                {
                    throw new CaptureInputException(
                        "analysis.process-filter.invalid",
                        "The process filter is invalid.",
                        ex);
                }
            }

            internal ProcessFilterSpec Specification { get; }

            internal bool Include(string deviceId, IDataRecord record)
            {
                // The incarnation numbers the rows of this run, so keys stay distinct even if a source repeats a row id.
                var key = new CaptureRowKey(_sourceId, 0, record.RowId, ++_incarnation);
                ClientProcessInfo process = ClientProcessRecordCodec.Read(record);
                _sessions.Add(new CaptureSessionRef(key, deviceId, process));
                if (!_filter.Matches(process))
                {
                    return false;
                }

                _included.Add(key);
                return true;
            }

            internal FilterCaptureSessionsResult Complete(CancellationToken cancellationToken)
            {
                OperationResult<FilterCaptureSessionsResult> result = FilterCaptureSessionsOperation.Select(
                    new FilterCaptureSessionsRequest(_sessions, Specification),
                    cancellationToken);
                if (!result.Succeeded ||
                    result.Value?.Scope == null ||
                    !IsExactSelection(result.Value.IncludedKeys, _included))
                {
                    throw new ProcessSelectionConsistencyException();
                }

                return result.Value;
            }
        }

        // Whether the shared selection kept each analyzed session's key exactly once, and no other key.
        internal static bool IsExactSelection(IReadOnlyList<CaptureRowKey> selected, HashSet<CaptureRowKey> analyzed)
        {
            if (selected == null || selected.Count != analyzed.Count)
            {
                return false;
            }

            var unique = new HashSet<CaptureRowKey>(selected);
            return unique.Count == selected.Count && unique.SetEquals(analyzed);
        }

        internal sealed class AnalysisDataRecord :
            IDataRecord,
            IRawBodyDataRecord,
            IOptionalTypedDataRecord
        {
            private readonly Dictionary<string, object> _values;

            private AnalysisDataRecord(int rowId, Dictionary<string, object> values)
            {
                RowId = rowId;
                _values = values;
            }

            public int RowId { get; }

            public long Int(string fieldName)
            {
                return _values.TryGetValue(fieldName, out var value) &&
                    value is long number
                    ? number
                    : 0;
            }

            public string Str(string fieldName)
            {
                if (!_values.TryGetValue(
                        fieldName,
                        out object value))
                {
                    return null;
                }
                if (fieldName is
                    WebServiceCaptureMethod.FieldKey_RequestBody or
                    WebServiceCaptureMethod.FieldKey_ResponseBody)
                {
                    return EncodeBody(
                        value as byte[]);
                }
                return value as string;
            }

            public ReadOnlyMemory<byte> RawBody(
                string fieldName)
            {
                return _values.TryGetValue(
                        fieldName,
                        out object value) &&
                    value is byte[] body
                        ? body
                        : ReadOnlyMemory<byte>.Empty;
            }

            public bool TryReadString(string name, out string value)
            {
                value = _values.TryGetValue(name, out object stored) ? stored as string : null;
                return value != null;
            }

            public bool TryReadInt64(string name, out long value)
            {
                if (_values.TryGetValue(name, out object stored) && stored is long number)
                {
                    value = number;
                    return true;
                }

                value = 0;
                return false;
            }

            // The session's fields as XMAT stores them, so SAZ and capture-file inputs read the same way. The process
            // fields use the shared projection of the capture files.
            internal static AnalysisDataRecord FromProxyModel(
                int rowId,
                ProxyConnectionModel model)
            {
                var values = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    [WebServiceCaptureMethod.FieldKey_DeviceName] = string.Empty,
                    [WebServiceCaptureMethod.FieldKey_RequestNumber] = model.RequestNumber,
                    [WebServiceCaptureMethod.FieldKey_ConnectionId] = model.Id,
                    [WebServiceCaptureMethod.FieldKey_RequestTimestamp] =
                        ProxyConnectionModel.ToDateTimeString(model.RequestTime),
                    [WebServiceCaptureMethod.FieldKey_ResponseTimestamp] =
                        ProxyConnectionModel.ToDateTimeString(
                            model.RequestTime + model.Duration),
                    [WebServiceCaptureMethod.FieldKey_RequestScheme] =
                        model.Scheme ?? string.Empty,
                    [WebServiceCaptureMethod.FieldKey_RequestHost] =
                        model.Host ?? string.Empty,
                    [WebServiceCaptureMethod.FieldKey_RequestPort] =
                        model.Port ?? string.Empty,
                    [WebServiceCaptureMethod.FieldKey_RequestMethod] =
                        model.Method ?? string.Empty,
                    [WebServiceCaptureMethod.FieldKey_RequestPath] =
                        model.Path ?? string.Empty,
                    [WebServiceCaptureMethod.FieldKey_RequestStatus] =
                        model.Status ?? string.Empty,
                    [WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders] =
                        model.RequestLineAndHeaders ?? string.Empty,
                    [WebServiceCaptureMethod.FieldKey_RequestBody] =
                        model.RequestBody,
                    [WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders] =
                        model.ResponseLineAndHeaders ?? string.Empty,
                    [WebServiceCaptureMethod.FieldKey_ResponseBody] =
                        model.ResponseBody,
                    [WebServiceCaptureMethod.FieldKey_ClientIP] =
                        model.ClientIP ?? string.Empty,
                };
                foreach (IFieldValue field in ClientProcessRecordCodec.ToFields(model.ClientProcess))
                {
                    values[field.Name] = field.Value;
                }

                return new AnalysisDataRecord(rowId, values);
            }

            private static string EncodeBody(byte[] body)
            {
                return body == null || body.Length == 0
                    ? string.Empty
                    : Convert.ToBase64String(body);
            }
        }

        // The shared selection did not keep exactly the sessions that the analysis analyzed.
        private sealed class ProcessSelectionConsistencyException : Exception
        {
            internal const string Description = "The process selection does not match the analyzed sessions.";

            internal ProcessSelectionConsistencyException()
                : base(Description)
            {
            }
        }
    }
}
