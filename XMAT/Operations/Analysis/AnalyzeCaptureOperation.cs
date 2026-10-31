// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CaptureAnalysisEngine;
using Microsoft.Data.Sqlite;
using SQLitePCL;
using XMAT.DataAbstractionLayer;
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

        internal const string WorkLimitCode =
            "analysis.rules.work-limit";
        private const string CaptureDevicesTableName = "CaptureDevices";
        private static readonly RequiredField[] CaptureDeviceFields =
        {
            new("DeviceType", FieldValueType.Integer),
            new("DeviceName", FieldValueType.String),
            new("CaptureType", FieldValueType.Integer),
        };
        private static readonly RequiredField[] WebProxyFields =
        {
            new(WebServiceCaptureMethod.FieldKey_DeviceName, FieldValueType.String),
            new(WebServiceCaptureMethod.FieldKey_RequestNumber, FieldValueType.Integer),
            new(WebServiceCaptureMethod.FieldKey_ConnectionId, FieldValueType.Integer),
            new(WebServiceCaptureMethod.FieldKey_RequestTimestamp, FieldValueType.String),
            new(WebServiceCaptureMethod.FieldKey_ResponseTimestamp, FieldValueType.String),
            new(WebServiceCaptureMethod.FieldKey_RequestScheme, FieldValueType.String),
            new(WebServiceCaptureMethod.FieldKey_RequestHost, FieldValueType.String),
            new(WebServiceCaptureMethod.FieldKey_RequestPort, FieldValueType.String),
            new(WebServiceCaptureMethod.FieldKey_RequestMethod, FieldValueType.String),
            new(WebServiceCaptureMethod.FieldKey_RequestPath, FieldValueType.String),
            new(WebServiceCaptureMethod.FieldKey_RequestStatus, FieldValueType.String),
            new(WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders, FieldValueType.String),
            new(WebServiceCaptureMethod.FieldKey_RequestBody, FieldValueType.String),
            new(WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders, FieldValueType.String),
            new(WebServiceCaptureMethod.FieldKey_ResponseBody, FieldValueType.String),
            new(WebServiceCaptureMethod.FieldKey_ClientIP, FieldValueType.String),
        };

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
            SqLtReadonlyDatabase> CaptureDatabaseFactory { get; set; } =
                static (path, profile) =>
                    new SqLtReadonlyDatabase(
                        path,
                        profile);

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
            catch (AnalysisInputException ex)
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
                throw new AnalysisInputException(
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
                ValidateLimits(limits);
            }
            cancellationToken.ThrowIfCancellationRequested();

            var diagnostics = new List<OperationDiagnostic>();
            using LoadedInput loaded = LoadInput(
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
                indexedDevices)
            {
                if (!selectedDevices.Contains(
                        indexedDevice))
                {
                    indexedDevice.Device
                        .ReleaseOwnedRecords();
                }
            }
            if (options.ProduceLegacyReports && selectedDevices.Count != 1)
            {
                throw new AnalysisInputException(
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
                        throw new AnalysisInputException(
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

            if (totalCalls == 0 ||
                deviceSummaries.All(device => device.Consoles.Count == 0))
            {
                AddDiagnostic(
                    diagnostics,
                    new OperationDiagnostic(
                        "analysis.no-data",
                        OperationDiagnosticSeverity.Info,
                        "The input contains no analyzable data."));
                return new OperationResult<AnalysisResult>(
                    OperationOutcome.NoAnalyzableData,
                    null,
                    diagnostics);
            }

            ReportProgress(context, "analysis.complete", 1);
            var summary = new AnalysisSummary(
                InputKindName(loaded.Kind),
                loaded.InputSha256,
                deviceSummaries,
                totalCalls,
                totalFindings);
            return OperationResult<AnalysisResult>.Success(
                new AnalysisResult(summary) { Legacy = legacy },
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

        private LoadedInput LoadInput(
            AnalysisInput input,
            AnalysisLimits limits,
            List<OperationDiagnostic> diagnostics,
            CancellationToken cancellationToken)
        {
            if (input.IsRecords)
            {
                return new LoadedInput(
                    AnalysisInputKind.Records,
                    null,
                    new[]
                    {
                        new LoadedDevice(
                            input.DeviceName ?? string.Empty,
                            input.Records,
                            ownsRecords: false),
                    });
            }

            if (string.IsNullOrWhiteSpace(input.Path) ||
                !File.Exists(input.Path))
            {
                throw new AnalysisInputException(
                    "analysis.input.not-found",
                    "The analysis input was not found.");
            }
            FileStream snapshot;
            try
            {
                snapshot = new FileStream(
                    input.Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.SequentialScan);
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                throw new AnalysisInputException(
                    "analysis.input.unreadable",
                    "The analysis input could not be read.",
                    ex);
            }

            using (snapshot)
            {
                if (snapshot.Length > limits.MaxInputBytes)
                {
                    throw new AnalysisInputException(
                        "analysis.input.too-large",
                        "The analysis input exceeds the configured size limit.");
                }

                var detectedKind = DetectInputKind(snapshot);
                if (!detectedKind.HasValue)
                {
                    throw new AnalysisInputException(
                        "analysis.input.unsupported",
                        "The analysis input format is unsupported.");
                }
                if (detectedKind == AnalysisInputKind.XmatCapture)
                {
                    return LoadXmatCapture(
                        input.Path,
                        limits,
                        diagnostics,
                        cancellationToken);
                }

                var hash =
                    ComputeSha256(
                        snapshot,
                        cancellationToken);
                snapshot.Seek(
                    0,
                    SeekOrigin.Begin);
                return detectedKind.Value switch
                {
                    AnalysisInputKind.Saz => new LoadedInput(
                        AnalysisInputKind.Saz,
                        hash,
                        new[]
                        {
                            new LoadedDevice(
                                string.Empty,
                                LoadSaz(
                                    snapshot,
                                    limits,
                                    diagnostics,
                                    cancellationToken),
                                ownsRecords: true),
                        }),
                    _ => throw new AnalysisInputException(
                        "analysis.input.unsupported",
                        "The analysis input format is unsupported."),
                };
            }
        }

        private static AnalysisInputKind? DetectInputKind(Stream input)
        {
            var header = new byte[16];
            input.Seek(0, SeekOrigin.Begin);
            var count = input.Read(header, 0, header.Length);
            input.Seek(0, SeekOrigin.Begin);
            if (count >= 4 &&
                header[0] == (byte)'P' &&
                header[1] == (byte)'K' &&
                ((header[2] == 3 && header[3] == 4) ||
                 (header[2] == 5 && header[3] == 6) ||
                 (header[2] == 7 && header[3] == 8)))
            {
                return AnalysisInputKind.Saz;
            }

            ReadOnlySpan<byte> sqliteHeader =
            [
                (byte)'S', (byte)'Q', (byte)'L', (byte)'i',
                (byte)'t', (byte)'e', (byte)' ', (byte)'f',
                (byte)'o', (byte)'r', (byte)'m', (byte)'a',
                (byte)'t', (byte)' ', (byte)'3', 0,
            ];
            return count == sqliteHeader.Length &&
                header.AsSpan().SequenceEqual(sqliteHeader)
                ? AnalysisInputKind.XmatCapture
                : null;
        }

        private static List<IDataRecord> LoadSaz(
            Stream input,
            AnalysisLimits limits,
            List<OperationDiagnostic> diagnostics,
            CancellationToken cancellationToken)
        {
            BoundedSazReadResult result;
            try
            {
                result = BoundedSazReader.Read(
                    input,
                    new BoundedSazLimits(
                        limits.MaxInputBytes,
                        limits.MaxEntryBytes,
                        limits.MaxTotalRetainedBytes,
                        limits.MaxRecords,
                        limits.MaxArchiveEntries ?? BoundedSazReader.EntryLimitForRecords(limits.MaxRecords),
                        limits.MaxCentralDirectoryBytes ??
                            BoundedSazReader.CentralDirectoryLimitForRecords(limits.MaxRecords)),
                    cancellationToken);
            }
            catch (BoundedSazException ex)
            {
                throw ex.Failure switch
                {
                    BoundedSazFailure.InputTooLarge or
                    BoundedSazFailure.EntryTooLarge or
                    BoundedSazFailure.ExpandedDataTooLarge or
                    BoundedSazFailure.TooManyRecords or
                    BoundedSazFailure.TooManyEntries or
                    BoundedSazFailure.CentralDirectoryTooLarge or
                    BoundedSazFailure.HeaderLimit => new AnalysisInputException(
                        "analysis.input.limit",
                        "The analysis input exceeds a configured safety limit.",
                        ex),
                    _ => new AnalysisInputException(
                        "analysis.input.malformed",
                        "The SAZ input is malformed.",
                        ex),
                };
            }

            if (result.SkippedIncompleteFrames > 0)
            {
                AddDiagnostic(
                    diagnostics,
                    new OperationDiagnostic(
                        "analysis.input.incomplete-frame",
                        OperationDiagnosticSeverity.Warning,
                        "One or more incomplete or malformed SAZ frames were skipped."));
            }
            if (result.SkippedMalformedHeaderLines > 0)
            {
                AddDiagnostic(
                    diagnostics,
                    new OperationDiagnostic(
                        "analysis.input.malformed-header",
                        OperationDiagnosticSeverity.Warning,
                        "One or more malformed HTTP header lines were skipped."));
            }

            return result.Records
                .Select((record, index) => (IDataRecord)AnalysisDataRecord.FromProxyModel(
                    index + 1,
                    record))
                .ToList();
        }

        private LoadedInput LoadXmatCapture(
            string path,
            AnalysisLimits limits,
            List<OperationDiagnostic> diagnostics,
            CancellationToken cancellationToken)
        {
            try
            {
                using var database = OpenCaptureDatabase(
                    path,
                    limits);
                string inputSha256 =
                    ComputeSha256(
                        database,
                        cancellationToken);
                var deviceTable = database.TableByName(CaptureDevicesTableName);
                var webProxyTable = database.TableByName(WebServiceCaptureMethod.TableName);
                ValidateTable(deviceTable, CaptureDeviceFields);
                ValidateTable(webProxyTable, WebProxyFields);

                var deviceDataset = deviceTable.Dataset;
                var webProxyDataset = webProxyTable.Dataset;
                var retainedRecordCount = 0;
                long retainedBytes = 0;
                var deviceRows = MaterializeRetainedRecords(
                    deviceDataset.DataRecords,
                    deviceDataset.FieldDefs,
                    limits,
                    ref retainedRecordCount,
                    ref retainedBytes,
                    cancellationToken);
                var webDeviceNames = new SortedSet<string>(StringComparer.Ordinal);
                var skippedUnsupportedDevice = false;
                foreach (var deviceRow in deviceRows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((CaptureType)deviceRow.Int("CaptureType") != CaptureType.WebProxy)
                    {
                        skippedUnsupportedDevice = true;
                        continue;
                    }

                    var deviceName = deviceRow.Str("DeviceName");
                    if (deviceName == null)
                    {
                        throw new AnalysisInputException(
                            "analysis.input.schema",
                            "The capture database schema is invalid.");
                    }
                    webDeviceNames.Add(deviceName);
                }

                if (skippedUnsupportedDevice)
                {
                    AddDiagnostic(
                        diagnostics,
                        new OperationDiagnostic(
                            "analysis.device.skipped",
                            OperationDiagnosticSeverity.Warning,
                            "A non-web-proxy device was skipped."));
                }

                var devices = new List<LoadedDevice>();
                var allRecords = MaterializeRetainedRecords(
                    webProxyDataset.DataRecords,
                    webProxyDataset.FieldDefs,
                    limits,
                    ref retainedRecordCount,
                    ref retainedBytes,
                    cancellationToken);
                var recordsByDevice = new Dictionary<string, List<IDataRecord>>(
                    StringComparer.Ordinal);
                foreach (var record in allRecords)
                {
                    var recordDeviceName = record.Str(
                        WebServiceCaptureMethod.FieldKey_DeviceName);
                    if (recordDeviceName == null)
                    {
                        throw new AnalysisInputException(
                            "analysis.input.schema",
                            "The capture database schema is invalid.");
                    }
                    if (!recordsByDevice.TryGetValue(
                            recordDeviceName,
                            out var deviceRecords))
                    {
                        deviceRecords = new List<IDataRecord>();
                        recordsByDevice.Add(recordDeviceName, deviceRecords);
                    }
                    deviceRecords.Add(record);
                }

                foreach (var deviceName in webDeviceNames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    devices.Add(new LoadedDevice(
                        deviceName,
                        recordsByDevice.TryGetValue(
                            deviceName,
                            out var deviceRecords)
                            ? deviceRecords
                            : new List<IDataRecord>(),
                        ownsRecords: true));
                }

                return new LoadedInput(
                    AnalysisInputKind.XmatCapture,
                    inputSha256,
                    devices);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (AnalysisInputException)
            {
                throw;
            }
            catch (SqliteException ex) when (
                ex.SqliteErrorCode ==
                    raw.SQLITE_TOOBIG)
            {
                throw new AnalysisInputException(
                    "analysis.input.limit",
                    "The analysis input exceeds a configured safety limit.",
                    ex);
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                throw new AnalysisInputException(
                    "analysis.input.malformed",
                    "The capture database is malformed.",
                    ex);
            }
        }

        private SqLtReadonlyDatabase OpenCaptureDatabase(
            string path,
            AnalysisLimits limits)
        {
            try
            {
                return CaptureDatabaseFactory(
                    path,
                    SqLtSchemaProfile.CreateCli(
                        limits.MaxInputBytes,
                        limits.MaxEntryBytes));
            }
            catch (SqLtSchemaException ex)
            {
                throw ex.Failure switch
                {
                    SqLtSchemaFailure.Limit =>
                        new AnalysisInputException(
                            "analysis.input.limit",
                            "The analysis input exceeds a configured safety limit.",
                            ex),
                    SqLtSchemaFailure.Sidecar or
                    SqLtSchemaFailure.WriteAheadLog =>
                        new AnalysisInputException(
                            "analysis.input.live-database",
                            "A live or write-ahead capture database is unsupported.",
                            ex),
                    SqLtSchemaFailure.UnsupportedLocation =>
                        new AnalysisInputException(
                            "analysis.input.location-unsupported",
                            "The capture database location cannot be opened safely.",
                            ex),
                    _ => new AnalysisInputException(
                        "analysis.input.schema",
                        "The capture database schema is invalid.",
                        ex),
                };
            }
            catch (FileNotFoundException ex)
            {
                throw new AnalysisInputException(
                    "analysis.input.not-found",
                    "The analysis input was not found.",
                    ex);
            }
            catch (Exception ex) when (
                ex is IOException or
                UnauthorizedAccessException)
            {
                throw new AnalysisInputException(
                    "analysis.input.unreadable",
                    "The analysis input could not be read.",
                    ex);
            }
        }

        private static DeviceAnalysis AnalyzeDevice(
            TraceAnalyzer analyzer,
            IndexedDevice device,
            AnalysisOptions options,
            OperationContext context,
            AnalysisWorkBudget workBudget,
            RequestBodyIdentityInterner bodyIdentities,
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
                throw new AnalysisInputException(
                    "analysis.device.not-found",
                    "The requested device was not found.");
            }
            return new[] { selected };
        }

        private static List<IDataRecord> MaterializeRetainedRecords(
            IEnumerable<IDataRecord> records,
            IEnumerable<IFieldDefinition> fieldDefinitions,
            AnalysisLimits limits,
            ref int totalRecordCount,
            ref long totalRetainedBytes,
            CancellationToken cancellationToken)
        {
            var fields = fieldDefinitions?.ToList();
            if (fields == null ||
                fields.Any(field =>
                    field == null ||
                    string.IsNullOrEmpty(field.Name) ||
                    field.ValueType is not (
                        FieldValueType.Integer or
                        FieldValueType.String)))
            {
                throw new AnalysisInputException(
                    "analysis.input.schema",
                    "The capture database schema is invalid.");
            }

            var result = new List<IDataRecord>();
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(record);
                try
                {
                    totalRecordCount = checked(totalRecordCount + 1);
                }
                catch (OverflowException ex)
                {
                    throw new AnalysisInputException(
                        "analysis.input.limit",
                        "The analysis input exceeds a configured safety limit.",
                        ex);
                }
                if (totalRecordCount > limits.MaxRecords)
                {
                    throw new AnalysisInputException(
                        "analysis.input.limit",
                        "The analysis input exceeds a configured safety limit.");
                }

                long recordBytes;
                try
                {
                    recordBytes = MeasureRetainedRecord(
                        record,
                        fields,
                        limits.MaxEntryBytes,
                        limits.MaxTotalRetainedBytes - totalRetainedBytes);
                }
                catch (OverflowException ex)
                {
                    throw new AnalysisInputException(
                        "analysis.input.limit",
                        "The analysis input exceeds a configured safety limit.",
                        ex);
                }
                try
                {
                    totalRetainedBytes = checked(
                        totalRetainedBytes + recordBytes);
                }
                catch (OverflowException ex)
                {
                    throw new AnalysisInputException(
                        "analysis.input.limit",
                        "The analysis input exceeds a configured safety limit.",
                        ex);
                }
                if (totalRetainedBytes > limits.MaxTotalRetainedBytes)
                {
                    throw new AnalysisInputException(
                        "analysis.input.limit",
                        "The analysis input exceeds a configured safety limit.");
                }
            }
            return result;
        }

        private static long MeasureRetainedRecord(
            IDataRecord record,
            IEnumerable<IFieldDefinition> fields,
            long maxEntryBytes,
            long maxTotalBytes)
        {
            long bytes = 0;
            void Add(long count)
            {
                bytes = checked(bytes + count);
                if (bytes > maxEntryBytes || bytes > maxTotalBytes)
                {
                    throw new AnalysisInputException(
                        "analysis.input.limit",
                        "The analysis input exceeds a configured safety limit.");
                }
            }

            Add(sizeof(long)); // SQLite row id retained by IDataRecord.
            foreach (var field in fields)
            {
                // The data layer admits NULL only in extension columns, and a NULL retains no value.
                if (record is INullableDataRecord nullable &&
                    nullable.IsNull(field.Name))
                {
                    continue;
                }

                Add(field.ValueType switch
                {
                    FieldValueType.String =>
                        MeasureText(record.Str(field.Name)),
                    FieldValueType.Integer =>
                        sizeof(long),
                    _ => throw new AnalysisInputException(
                        "analysis.input.schema",
                        "The capture database schema is invalid."),
                });
            }

            return bytes;
        }

        private static int MeasureText(string value)
        {
            return string.IsNullOrEmpty(value)
                ? 0
                : Encoding.UTF8.GetByteCount(value);
        }

        private static void ValidateTable(
            IReadonlyDataTable table,
            IEnumerable<RequiredField> requiredFields)
        {
            if (table == null)
            {
                throw new AnalysisInputException(
                    "analysis.input.schema",
                    "The capture database schema is invalid.");
            }

            var fields = table.Dataset.FieldDefs.ToDictionary(
                field => field.Name,
                field => field,
                StringComparer.Ordinal);
            foreach (var required in requiredFields)
            {
                if (!fields.TryGetValue(required.Name, out var actual) ||
                    actual.ValueType != required.ValueType)
                {
                    throw new AnalysisInputException(
                        "analysis.input.schema",
                        "The capture database schema is invalid.");
                }
            }
        }

        private static void ValidateLimits(AnalysisLimits limits)
        {
            if (limits.MaxInputBytes <= 0 ||
                limits.MaxEntryBytes <= 0 ||
                limits.MaxTotalRetainedBytes <= 0 ||
                limits.MaxRecords <= 0 ||
                (limits.MaxArchiveEntries.HasValue &&
                 limits.MaxArchiveEntries.Value <= 0) ||
                (limits.MaxCentralDirectoryBytes.HasValue &&
                 limits.MaxCentralDirectoryBytes.Value <= 0))
            {
                throw new AnalysisInputException(
                    "analysis.limits.invalid",
                    "The analysis limits are invalid.");
            }
        }

        private static string ComputeSha256(
            Stream input,
            CancellationToken cancellationToken)
        {
            try
            {
                input.Seek(0, SeekOrigin.Begin);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = input.Read(buffer, 0, buffer.Length);
                    if (read == 0)
                    {
                        break;
                    }
                    hash.AppendData(buffer, 0, read);
                }
                var value = Convert.ToHexString(
                    hash.GetHashAndReset()).ToLowerInvariant();
                input.Seek(0, SeekOrigin.Begin);
                return value;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                throw new AnalysisInputException(
                    "analysis.input.unreadable",
                    "The analysis input could not be read.",
                    ex);
            }
        }

        private static string ComputeSha256(
            SqLtReadonlyDatabase database,
            CancellationToken cancellationToken)
        {
            try
            {
                return Convert.ToHexString(
                        database.ComputeSha256(
                            cancellationToken))
                    .ToLowerInvariant();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                throw new AnalysisInputException(
                    "analysis.input.unreadable",
                    "The analysis input could not be read.",
                    ex);
            }
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

        private sealed record RequiredField(
            string Name,
            FieldValueType ValueType);

        private sealed class LoadedInput : IDisposable
        {
            internal LoadedInput(
                AnalysisInputKind kind,
                string inputSha256,
                IReadOnlyList<LoadedDevice> devices)
            {
                Kind = kind;
                InputSha256 = inputSha256;
                Devices = devices;
            }

            internal AnalysisInputKind Kind { get; }

            internal string InputSha256 { get; }

            internal IReadOnlyList<LoadedDevice> Devices { get; }

            public void Dispose()
            {
                foreach (LoadedDevice device in
                    Devices)
                {
                    device.ReleaseOwnedRecords();
                }
            }
        }

        private sealed class LoadedDevice
        {
            private List<IDataRecord> _ownedRecords;

            internal LoadedDevice(
                string name,
                IEnumerable<IDataRecord> records,
                bool ownsRecords)
            {
                Name = name;
                Records = records;
                if (ownsRecords)
                {
                    _ownedRecords =
                        records as List<IDataRecord> ??
                        records.ToList();
                    Records = _ownedRecords;
                }
            }

            internal string Name { get; }

            internal IEnumerable<IDataRecord> Records { get; private set; }

            internal IEnumerable<IDataRecord>
                RecordsForConversion()
            {
                return _ownedRecords == null
                    ? Records
                    : ConsumeOwnedRecords();
            }

            private IEnumerable<IDataRecord>
                ConsumeOwnedRecords()
            {
                for (var index = 0;
                    index < _ownedRecords.Count;
                    index++)
                {
                    IDataRecord record =
                        _ownedRecords[index];
                    _ownedRecords[index] = null;
                    yield return record;
                }
            }

            internal void ReleaseOwnedRecords()
            {
                if (_ownedRecords == null)
                {
                    return;
                }

                _ownedRecords.Clear();
                _ownedRecords = null;
                Records = Array.Empty<IDataRecord>();
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

        internal sealed class AnalysisDataRecord :
            IDataRecord,
            IRawBodyDataRecord
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

            internal static AnalysisDataRecord FromProxyModel(
                int rowId,
                ProxyConnectionModel model)
            {
                return new AnalysisDataRecord(
                    rowId,
                    new Dictionary<string, object>(StringComparer.Ordinal)
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
                    });
            }

            private static string EncodeBody(byte[] body)
            {
                return body == null || body.Length == 0
                    ? string.Empty
                    : Convert.ToBase64String(body);
            }
        }

        private sealed class AnalysisInputException : Exception
        {
            internal AnalysisInputException(
                string code,
                string diagnosticMessage,
                Exception innerException = null)
                : base("The analysis input is invalid.", innerException)
            {
                Code = code;
                DiagnosticMessage = diagnosticMessage;
            }

            internal string Code { get; }
            internal string DiagnosticMessage { get; }
        }
    }
}
