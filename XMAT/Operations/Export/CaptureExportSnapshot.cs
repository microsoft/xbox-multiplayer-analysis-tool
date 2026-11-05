// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using XMAT.DataAbstractionLayer;
using XMAT.Operations.Analysis;
using XMAT.Operations.Captures;
using XMAT.Operations.Processes;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;

namespace XMAT.Operations.Export
{
    internal sealed class CaptureExportSnapshot : ICaptureExportSnapshot
    {
        internal static IReadOnlyList<SqLtSchemaColumn> StandardDeviceFields { get; } =
        [
            new("DeviceType", SqLtDeclaredType.Integer),
            new("DeviceName", SqLtDeclaredType.Text),
            new("CaptureType", SqLtDeclaredType.Integer),
        ];

        private readonly Func<IReadOnlyList<CaptureRowKey>, CancellationToken, CaptureExportChunk> _readChunk;
        private readonly Action<string> _validateDestination;
        private readonly Action _dispose;
        private int _disposed;

        internal CaptureExportSnapshot(
            IReadOnlyList<SqLtSchemaColumn> captureFields,
            IReadOnlyList<SqLtSchemaColumn> deviceFields,
            IReadOnlyDictionary<string, RawExportRow> deviceDescriptors,
            IReadOnlyList<CaptureExportSession> sessions,
            bool isLive,
            string inputKind,
            SqLtFileIdentity? inputIdentity,
            string inputSha256,
            Func<IReadOnlyList<CaptureRowKey>, CancellationToken, CaptureExportChunk> readChunk,
            Action<string> validateDestination,
            Action dispose = null)
        {
            CaptureFields = captureFields ?? throw new ArgumentNullException(nameof(captureFields));
            DeviceFields = deviceFields ?? throw new ArgumentNullException(nameof(deviceFields));
            DeviceDescriptors = deviceDescriptors ?? throw new ArgumentNullException(nameof(deviceDescriptors));
            Sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            IsLive = isLive;
            InputKind = inputKind;
            InputIdentity = inputIdentity;
            InputSha256 = inputSha256;
            _readChunk = readChunk ?? throw new ArgumentNullException(nameof(readChunk));
            _validateDestination = validateDestination ?? (_ => { });
            _dispose = dispose;
        }

        public IReadOnlyList<SqLtSchemaColumn> CaptureFields { get; }
        public IReadOnlyList<SqLtSchemaColumn> DeviceFields { get; }
        public IReadOnlyDictionary<string, RawExportRow> DeviceDescriptors { get; }
        public IReadOnlyList<CaptureExportSession> Sessions { get; }
        public bool IsLive { get; }
        public string InputKind { get; }
        public SqLtFileIdentity? InputIdentity { get; }
        public string InputSha256 { get; }

        public CaptureExportChunk ReadChunk(
            IReadOnlyList<CaptureRowKey> keys,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            ArgumentNullException.ThrowIfNull(keys);
            return _readChunk(keys, cancellationToken);
        }

        public void ValidateDestination(string path)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _validateDestination(path);
        }

        internal static CaptureExportSnapshot FromLoadedInput(LoadedInput input)
        {
            ArgumentNullException.ThrowIfNull(input);
            IReadOnlyList<SqLtSchemaColumn> captureFields = input.CaptureFields ??
                WebProxyCaptureSchema.Fields.Select(field => new SqLtSchemaColumn(
                    field.Name,
                    field.ValueType == typeof(long)
                        ? SqLtDeclaredType.Integer
                        : SqLtDeclaredType.Text)).ToList();
            IReadOnlyList<SqLtSchemaColumn> deviceFields = input.DeviceFields ?? StandardDeviceFields;
            var sourceId = Guid.NewGuid();
            var sessions = new List<CaptureExportSession>();
            var rows = new Dictionary<CaptureRowKey, LoadedExportRow>();
            var devices = new Dictionary<string, RawExportRow>(StringComparer.Ordinal);
            var descriptorByName = (input.DeviceRecords ?? Array.Empty<IDataRecord>())
                .Where(record => (CaptureType)record.Int("CaptureType") == CaptureType.WebProxy)
                .GroupBy(record => record.Str("DeviceName") ?? string.Empty, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            long incarnation = 0;
            for (var deviceIndex = 0; deviceIndex < input.Devices.Count; deviceIndex++)
            {
                LoadedDevice device = input.Devices[deviceIndex];
                string deviceId = $"d{deviceIndex + 1}";
                if (descriptorByName.TryGetValue(device.Name, out IDataRecord descriptor))
                {
                    devices.Add(
                        deviceId,
                        new RawExportRow(
                            default,
                            descriptor.RowId,
                            0,
                            CaptureRawValues.Read(descriptor, deviceFields)));
                }
                foreach (IDataRecord record in device.Records)
                {
                    var key = new CaptureRowKey(sourceId, 0, record.RowId, ++incarnation);
                    ClientProcessInfo process = ClientProcessRecordCodec.Read(record);
                    sessions.Add(new CaptureExportSession(
                        key,
                        deviceId,
                        record.Str(WebServiceCaptureMethod.FieldKey_RequestHost) ?? string.Empty,
                        record.Str(WebServiceCaptureMethod.FieldKey_RequestPath) ?? string.Empty,
                        record.Str(WebServiceCaptureMethod.FieldKey_RequestStatus) ?? string.Empty,
                        record.Str(WebServiceCaptureMethod.FieldKey_RequestMethod) ?? string.Empty,
                        process,
                        !string.IsNullOrEmpty(
                            record.Str(WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders)),
                        !string.IsNullOrEmpty(
                            record.Str(WebServiceCaptureMethod.FieldKey_ResponseTimestamp)),
                        0));
                    rows.Add(key, new LoadedExportRow(record, process));
                }
            }

            CaptureExportChunk ReadRows(
                IReadOnlyList<CaptureRowKey> keys,
                CancellationToken cancellationToken)
            {
                var consumed = new List<CaptureRowKey>();
                var result = new List<RawExportRow>();
                long bytes = 0;
                foreach (CaptureRowKey key in keys)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!rows.TryGetValue(key, out LoadedExportRow row))
                    {
                        consumed.Add(key);
                        continue;
                    }
                    Dictionary<string, object> values =
                        CaptureRawValues.Read(row.Record, captureFields);
                    CaptureRawValues.OverlayProcess(values, row.Process);
                    long rowBytes = CaptureRawValues.Measure(values);
                    if (result.Count != 0 && bytes + rowBytes > 8 * 1024 * 1024)
                    {
                        break;
                    }
                    consumed.Add(key);
                    result.Add(new RawExportRow(key, row.Record.RowId, 0, values));
                    bytes += rowBytes;
                    if (result.Count == 64 || rowBytes > 8 * 1024 * 1024)
                    {
                        break;
                    }
                }
                return new CaptureExportChunk(consumed.ToArray(), result);
            }

            void ValidateDestination(string destination)
                => RejectSourceDestination(destination, input.InputPath, input.InputIdentity);

            return new CaptureExportSnapshot(
                captureFields,
                deviceFields,
                devices,
                sessions.AsReadOnly(),
                isLive: false,
                input.Kind == AnalysisInputKind.XmatCapture ? "xmatcap" : "saz",
                input.InputIdentity,
                input.InputSha256,
                ReadRows,
                ValidateDestination,
                input.Dispose);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _dispose?.Invoke();
            }
        }

        private sealed record LoadedExportRow(
            IDataRecord Record,
            ClientProcessInfo Process);

        internal static void RejectSourceDestination(
            string destination,
            string inputPath,
            SqLtFileIdentity? inputIdentity)
        {
            string normalized = Path.GetFullPath(destination);
            if ((!string.IsNullOrEmpty(inputPath) &&
                    string.Equals(normalized, Path.GetFullPath(inputPath), StringComparison.OrdinalIgnoreCase)) ||
                (inputIdentity.HasValue &&
                    File.Exists(normalized) &&
                    SqLtFileIdentity.FromPath(normalized) == inputIdentity.Value))
            {
                throw new CaptureExportDestinationException("export.output.is-source");
            }
        }
    }

    internal static class CaptureRawValues
    {
        internal static Dictionary<string, object> Read(
            IDataRecord record,
            IReadOnlyList<SqLtSchemaColumn> fields)
        {
            var values = new Dictionary<string, object>(fields.Count, StringComparer.Ordinal);
            foreach (SqLtSchemaColumn field in fields)
            {
                if (record is IRawDataRecord raw && raw.TryReadRaw(field.Name, out object value))
                {
                    values[field.Name] = value;
                    continue;
                }
                values[field.Name] = field.DeclaredType == SqLtDeclaredType.Integer
                    ? record.Int(field.Name)
                    : record.Str(field.Name);
            }
            return values;
        }

        internal static void OverlayProcess(
            IDictionary<string, object> values,
            ClientProcessInfo process)
        {
            foreach (IFieldValue field in ClientProcessRecordCodec.ToFields(process))
            {
                if (values.ContainsKey(field.Name))
                {
                    values[field.Name] = field.Value;
                }
            }
        }

        internal static long Measure(IReadOnlyDictionary<string, object> values)
        {
            long bytes = sizeof(long);
            foreach (object value in values.Values)
            {
                bytes = checked(bytes + value switch
                {
                    null => 0,
                    string text => Encoding.UTF8.GetByteCount(text),
                    byte[] data => data.LongLength,
                    long => sizeof(long),
                    double => sizeof(double),
                    _ => throw new CaptureExportException(
                        CaptureExportErrorCodes.InvalidBody,
                        OperationOutcome.InvalidInput),
                });
            }
            return bytes;
        }
    }

    internal sealed class CaptureExportDestinationException : IOException
    {
        internal CaptureExportDestinationException(string code)
            : base(code switch
            {
                "export.output.exists" => "The export destination already exists.",
                "export.output.is-source" => "The export destination is the capture input.",
                "export.output.locked" => "The export destination is locked.",
                _ => "The export destination conflicts with an open capture.",
            })
        {
            Code = code;
        }

        internal string Code { get; }
    }
}
