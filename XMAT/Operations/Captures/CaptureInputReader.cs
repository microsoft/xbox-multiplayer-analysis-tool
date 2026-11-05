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
using Microsoft.Data.Sqlite;
using SQLitePCL;
using XMAT.DataAbstractionLayer;
using XMAT.Operations.Analysis;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.XboxLiveCaptureAnalysis;

namespace XMAT.Operations.Captures
{
    internal sealed class CaptureInputReader
    {
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

        internal Func<string, SqLtSchemaProfile, SqLtReadonlyDatabase> CaptureDatabaseFactory { get; set; } =
            static (path, profile) => new SqLtReadonlyDatabase(path, profile);

        internal LoadedInput Read(
            AnalysisInput input,
            AnalysisLimits limits,
            ICollection<OperationDiagnostic> diagnostics,
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

            if (string.IsNullOrWhiteSpace(input.Path) || !File.Exists(input.Path))
            {
                throw new CaptureInputException(
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
                throw new CaptureInputException(
                    "analysis.input.unreadable",
                    "The analysis input could not be read.",
                    ex);
            }

            using (snapshot)
            {
                if (snapshot.Length > limits.MaxInputBytes)
                {
                    throw new CaptureInputException(
                        "analysis.input.too-large",
                        "The analysis input exceeds the configured size limit.");
                }

                AnalysisInputKind? detectedKind = DetectInputKind(snapshot);
                if (!detectedKind.HasValue)
                {
                    throw new CaptureInputException(
                        "analysis.input.unsupported",
                        "The analysis input format is unsupported.");
                }
                if (detectedKind == AnalysisInputKind.XmatCapture)
                {
                    return LoadXmatCapture(input.Path, limits, diagnostics, cancellationToken);
                }

                string hash = ComputeSha256(snapshot, cancellationToken);
                snapshot.Seek(0, SeekOrigin.Begin);
                return detectedKind.Value switch
                {
                    AnalysisInputKind.Saz => new LoadedInput(
                        AnalysisInputKind.Saz,
                        hash,
                        new[]
                        {
                            new LoadedDevice(
                                string.Empty,
                                LoadSaz(snapshot, limits, diagnostics, cancellationToken),
                                ownsRecords: true),
                        },
                        inputIdentity: SqLtFileIdentity.FromHandle(snapshot.SafeFileHandle),
                        inputPath: input.Path),
                    _ => throw new CaptureInputException(
                        "analysis.input.unsupported",
                        "The analysis input format is unsupported."),
                };
            }
        }

        internal static void ValidateLimits(AnalysisLimits limits)
        {
            if (limits.MaxInputBytes <= 0 ||
                limits.MaxEntryBytes <= 0 ||
                limits.MaxTotalRetainedBytes <= 0 ||
                limits.MaxRecords <= 0 ||
                (limits.MaxArchiveEntries.HasValue && limits.MaxArchiveEntries.Value <= 0) ||
                (limits.MaxCentralDirectoryBytes.HasValue && limits.MaxCentralDirectoryBytes.Value <= 0))
            {
                throw new CaptureInputException(
                    "analysis.limits.invalid",
                    "The analysis limits are invalid.");
            }
        }

        private static AnalysisInputKind? DetectInputKind(Stream input)
        {
            var header = new byte[16];
            input.Seek(0, SeekOrigin.Begin);
            int count = input.Read(header, 0, header.Length);
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
            return count == sqliteHeader.Length && header.AsSpan().SequenceEqual(sqliteHeader)
                ? AnalysisInputKind.XmatCapture
                : null;
        }

        private static List<IDataRecord> LoadSaz(
            Stream input,
            AnalysisLimits limits,
            ICollection<OperationDiagnostic> diagnostics,
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
                    BoundedSazFailure.HeaderLimit => new CaptureInputException(
                        "analysis.input.limit",
                        "The analysis input exceeds a configured safety limit.",
                        ex),
                    _ => new CaptureInputException(
                        "analysis.input.malformed",
                        "The SAZ input is malformed.",
                        ex),
                };
            }

            if (result.SkippedIncompleteFrames > 0)
            {
                diagnostics.Add(new OperationDiagnostic(
                    "analysis.input.incomplete-frame",
                    OperationDiagnosticSeverity.Warning,
                    "One or more incomplete or malformed SAZ frames were skipped."));
            }
            if (result.SkippedMalformedHeaderLines > 0)
            {
                diagnostics.Add(new OperationDiagnostic(
                    "analysis.input.malformed-header",
                    OperationDiagnosticSeverity.Warning,
                    "One or more malformed HTTP header lines were skipped."));
            }

            return result.Records
                .Select((record, index) => (IDataRecord)AnalyzeCaptureOperation.AnalysisDataRecord.FromProxyModel(
                    index + 1,
                    record))
                .ToList();
        }

        private LoadedInput LoadXmatCapture(
            string path,
            AnalysisLimits limits,
            ICollection<OperationDiagnostic> diagnostics,
            CancellationToken cancellationToken)
        {
            try
            {
                using SqLtReadonlyDatabase database = OpenCaptureDatabase(path, limits);
                string inputSha256 = ComputeSha256(database, cancellationToken);
                IReadonlyDataTable deviceTable = database.TableByName(CaptureDevicesTableName);
                IReadonlyDataTable webProxyTable = database.TableByName(WebServiceCaptureMethod.TableName);
                ValidateTable(deviceTable, CaptureDeviceFields);
                ValidateTable(webProxyTable, WebProxyFields);

                IDataset deviceDataset = deviceTable.Dataset;
                IDataset webProxyDataset = webProxyTable.Dataset;
                var retainedRecordCount = 0;
                long retainedBytes = 0;
                List<IDataRecord> deviceRows = MaterializeRetainedRecords(
                    deviceDataset.DataRecords,
                    deviceDataset.FieldDefs,
                    limits,
                    ref retainedRecordCount,
                    ref retainedBytes,
                    cancellationToken);
                var webDeviceNames = new SortedSet<string>(StringComparer.Ordinal);
                var skippedUnsupportedDevice = false;
                foreach (IDataRecord deviceRow in deviceRows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((CaptureType)deviceRow.Int("CaptureType") != CaptureType.WebProxy)
                    {
                        skippedUnsupportedDevice = true;
                        continue;
                    }

                    string deviceName = deviceRow.Str("DeviceName");
                    if (deviceName == null)
                    {
                        throw new CaptureInputException(
                            "analysis.input.schema",
                            "The capture database schema is invalid.");
                    }
                    webDeviceNames.Add(deviceName);
                }

                if (skippedUnsupportedDevice)
                {
                    diagnostics.Add(new OperationDiagnostic(
                        "analysis.device.skipped",
                        OperationDiagnosticSeverity.Warning,
                        "A non-web-proxy device was skipped."));
                }

                var devices = new List<LoadedDevice>();
                List<IDataRecord> allRecords = MaterializeRetainedRecords(
                    webProxyDataset.DataRecords,
                    webProxyDataset.FieldDefs,
                    limits,
                    ref retainedRecordCount,
                    ref retainedBytes,
                    cancellationToken);
                var recordsByDevice = new Dictionary<string, List<IDataRecord>>(StringComparer.Ordinal);
                foreach (IDataRecord record in allRecords)
                {
                    string recordDeviceName = record.Str(WebServiceCaptureMethod.FieldKey_DeviceName);
                    if (recordDeviceName == null)
                    {
                        throw new CaptureInputException(
                            "analysis.input.schema",
                            "The capture database schema is invalid.");
                    }
                    if (!recordsByDevice.TryGetValue(recordDeviceName, out List<IDataRecord> deviceRecords))
                    {
                        deviceRecords = new List<IDataRecord>();
                        recordsByDevice.Add(recordDeviceName, deviceRecords);
                    }
                    deviceRecords.Add(record);
                }

                foreach (string deviceName in webDeviceNames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    devices.Add(new LoadedDevice(
                        deviceName,
                        recordsByDevice.TryGetValue(deviceName, out List<IDataRecord> deviceRecords)
                            ? deviceRecords
                            : new List<IDataRecord>(),
                        ownsRecords: true));
                }

                return new LoadedInput(
                    AnalysisInputKind.XmatCapture,
                    inputSha256,
                    devices,
                    ((SqLtReadonlyDataTable)webProxyTable).Columns,
                    ((SqLtReadonlyDataTable)deviceTable).Columns,
                    deviceRows,
                    database.Identity,
                    path);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (CaptureInputException)
            {
                throw;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == raw.SQLITE_TOOBIG)
            {
                throw new CaptureInputException(
                    "analysis.input.limit",
                    "The analysis input exceeds a configured safety limit.",
                    ex);
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                throw new CaptureInputException(
                    "analysis.input.malformed",
                    "The capture database is malformed.",
                    ex);
            }
        }

        private SqLtReadonlyDatabase OpenCaptureDatabase(string path, AnalysisLimits limits)
        {
            try
            {
                return CaptureDatabaseFactory(
                    path,
                    SqLtSchemaProfile.CreateCli(limits.MaxInputBytes, limits.MaxEntryBytes));
            }
            catch (SqLtSchemaException ex)
            {
                throw ex.Failure switch
                {
                    SqLtSchemaFailure.Limit => new CaptureInputException(
                        "analysis.input.limit",
                        "The analysis input exceeds a configured safety limit.",
                        ex),
                    SqLtSchemaFailure.Sidecar or SqLtSchemaFailure.WriteAheadLog => new CaptureInputException(
                        "analysis.input.live-database",
                        "A live or write-ahead capture database is unsupported.",
                        ex),
                    SqLtSchemaFailure.UnsupportedLocation => new CaptureInputException(
                        "analysis.input.location-unsupported",
                        "The capture database location cannot be opened safely.",
                        ex),
                    _ => new CaptureInputException(
                        "analysis.input.schema",
                        "The capture database schema is invalid.",
                        ex),
                };
            }
            catch (FileNotFoundException ex)
            {
                throw new CaptureInputException(
                    "analysis.input.not-found",
                    "The analysis input was not found.",
                    ex);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new CaptureInputException(
                    "analysis.input.unreadable",
                    "The analysis input could not be read.",
                    ex);
            }
        }

        private static List<IDataRecord> MaterializeRetainedRecords(
            IEnumerable<IDataRecord> records,
            IEnumerable<IFieldDefinition> fieldDefinitions,
            AnalysisLimits limits,
            ref int totalRecordCount,
            ref long totalRetainedBytes,
            CancellationToken cancellationToken)
        {
            List<IFieldDefinition> fields = fieldDefinitions?.ToList();
            if (fields == null ||
                fields.Any(field =>
                    field == null ||
                    string.IsNullOrEmpty(field.Name) ||
                    field.ValueType is not (FieldValueType.Integer or FieldValueType.String)))
            {
                throw new CaptureInputException(
                    "analysis.input.schema",
                    "The capture database schema is invalid.");
            }

            var result = new List<IDataRecord>();
            foreach (IDataRecord record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(record);
                try
                {
                    totalRecordCount = checked(totalRecordCount + 1);
                }
                catch (OverflowException ex)
                {
                    throw new CaptureInputException(
                        "analysis.input.limit",
                        "The analysis input exceeds a configured safety limit.",
                        ex);
                }
                if (totalRecordCount > limits.MaxRecords)
                {
                    throw new CaptureInputException(
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
                    throw new CaptureInputException(
                        "analysis.input.limit",
                        "The analysis input exceeds a configured safety limit.",
                        ex);
                }
                try
                {
                    totalRetainedBytes = checked(totalRetainedBytes + recordBytes);
                }
                catch (OverflowException ex)
                {
                    throw new CaptureInputException(
                        "analysis.input.limit",
                        "The analysis input exceeds a configured safety limit.",
                        ex);
                }
                if (totalRetainedBytes > limits.MaxTotalRetainedBytes)
                {
                    throw new CaptureInputException(
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
                    throw new CaptureInputException(
                        "analysis.input.limit",
                        "The analysis input exceeds a configured safety limit.");
                }
            }

            Add(sizeof(long));
            foreach (IFieldDefinition field in fields)
            {
                if (record is IRawBodyDataRecord rawBody &&
                    field.Name is
                        WebServiceCaptureMethod.FieldKey_RequestBody or
                        WebServiceCaptureMethod.FieldKey_ResponseBody)
                {
                    long length = rawBody.RawBody(field.Name).Length;
                    Add(checked(((length + 2) / 3) * 4));
                    continue;
                }
                if (record is IRawDataRecord raw &&
                    raw.TryReadRaw(field.Name, out object rawValue))
                {
                    Add(rawValue switch
                    {
                        null => 0,
                        string text => MeasureText(text),
                        byte[] data => data.LongLength,
                        long => sizeof(long),
                        double => sizeof(double),
                        _ => throw new CaptureInputException(
                            "analysis.input.schema",
                            "The capture database schema is invalid."),
                    });
                    continue;
                }
                if (record is INullableDataRecord nullable && nullable.IsNull(field.Name))
                {
                    continue;
                }

                Add(field.ValueType switch
                {
                    FieldValueType.String => MeasureText(record.Str(field.Name)),
                    FieldValueType.Integer => sizeof(long),
                    _ => throw new CaptureInputException(
                        "analysis.input.schema",
                        "The capture database schema is invalid."),
                });
            }

            return bytes;
        }

        private static int MeasureText(string value) =>
            string.IsNullOrEmpty(value) ? 0 : Encoding.UTF8.GetByteCount(value);

        private static void ValidateTable(
            IReadonlyDataTable table,
            IEnumerable<RequiredField> requiredFields)
        {
            if (table == null)
            {
                throw new CaptureInputException(
                    "analysis.input.schema",
                    "The capture database schema is invalid.");
            }

            Dictionary<string, IFieldDefinition> fields = table.Dataset.FieldDefs.ToDictionary(
                field => field.Name,
                field => field,
                StringComparer.Ordinal);
            if (requiredFields.Any(required =>
                    !fields.TryGetValue(required.Name, out IFieldDefinition actual) ||
                    actual.ValueType != required.ValueType))
            {
                throw new CaptureInputException(
                    "analysis.input.schema",
                    "The capture database schema is invalid.");
            }
        }

        private static string ComputeSha256(Stream input, CancellationToken cancellationToken)
        {
            try
            {
                input.Seek(0, SeekOrigin.Begin);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read = input.Read(buffer, 0, buffer.Length);
                    if (read == 0)
                    {
                        break;
                    }
                    hash.AppendData(buffer, 0, read);
                }
                string value = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                input.Seek(0, SeekOrigin.Begin);
                return value;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                throw new CaptureInputException(
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
                return Convert.ToHexString(database.ComputeSha256(cancellationToken)).ToLowerInvariant();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (!ExceptionUtilities.IsFatal(ex))
            {
                throw new CaptureInputException(
                    "analysis.input.unreadable",
                    "The analysis input could not be read.",
                    ex);
            }
        }

        private sealed record RequiredField(string Name, FieldValueType ValueType);
    }

    internal sealed class LoadedInput : IDisposable
    {
        internal LoadedInput(
            AnalysisInputKind kind,
            string inputSha256,
            IReadOnlyList<LoadedDevice> devices,
            IReadOnlyList<SqLtSchemaColumn> captureFields = null,
            IReadOnlyList<SqLtSchemaColumn> deviceFields = null,
            IReadOnlyList<IDataRecord> deviceRecords = null,
            SqLtFileIdentity? inputIdentity = null,
            string inputPath = null)
        {
            Kind = kind;
            InputSha256 = inputSha256;
            Devices = devices;
            CaptureFields = captureFields;
            DeviceFields = deviceFields;
            DeviceRecords = deviceRecords;
            InputIdentity = inputIdentity;
            InputPath = inputPath;
        }

        internal AnalysisInputKind Kind { get; }
        internal string InputSha256 { get; }
        internal IReadOnlyList<LoadedDevice> Devices { get; }
        internal IReadOnlyList<SqLtSchemaColumn> CaptureFields { get; }
        internal IReadOnlyList<SqLtSchemaColumn> DeviceFields { get; }
        internal IReadOnlyList<IDataRecord> DeviceRecords { get; }
        internal SqLtFileIdentity? InputIdentity { get; }
        internal string InputPath { get; }

        public void Dispose()
        {
            foreach (LoadedDevice device in Devices)
            {
                device.ReleaseOwnedRecords();
            }
        }
    }

    internal sealed class LoadedDevice
    {
        private List<IDataRecord> _ownedRecords;

        internal LoadedDevice(string name, IEnumerable<IDataRecord> records, bool ownsRecords)
        {
            Name = name;
            Records = records;
            if (ownsRecords)
            {
                _ownedRecords = records as List<IDataRecord> ?? records.ToList();
                Records = _ownedRecords;
            }
        }

        internal string Name { get; }
        internal IEnumerable<IDataRecord> Records { get; private set; }

        internal IEnumerable<IDataRecord> RecordsForConversion() =>
            _ownedRecords == null ? Records : ConsumeOwnedRecords();

        private IEnumerable<IDataRecord> ConsumeOwnedRecords()
        {
            for (var index = 0; index < _ownedRecords.Count; index++)
            {
                IDataRecord record = _ownedRecords[index];
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

    internal sealed class CaptureInputException : Exception
    {
        internal CaptureInputException(
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
