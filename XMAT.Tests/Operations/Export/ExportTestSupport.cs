// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using XMAT.DataAbstractionLayer;
using XMAT.Operations.Captures;
using XMAT.Operations.Export;
using XMAT.Operations.Processes;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;

namespace XMAT.Tests.Operations.Export
{
    internal static class ExportTestData
    {
        internal static CaptureRowKey Key(long rowId) => new(Guid.Parse("11111111-1111-1111-1111-111111111111"), 1,
            rowId, rowId);

        internal static ClientProcessInfo Process(string name = "Game.exe", int processId = 4100) =>
            new(name, processId, null, ClientProcessStatus.Local);

        internal static CaptureExportSession Session(
            long rowId,
            string deviceId = "d1",
            string host = "api.example.test",
            string path = "/api/users",
            string status = "200",
            string method = "GET",
            ClientProcessInfo process = null,
            bool responseHeaders = true,
            bool responseTimestamp = true,
            long payloadVersion = 1) =>
            new(
                Key(rowId),
                deviceId,
                host,
                path,
                status,
                method,
                process ?? Process(),
                responseHeaders,
                responseTimestamp,
                payloadVersion);

        internal static CaptureSelectionSpec Selection(
            ProcessFilterSpec process = null,
            HostSelection host = null,
            ImmutableArray<string>? statuses = null,
            ImmutableArray<string>? methods = null,
            ImmutableHashSet<CaptureRowKey> keys = null,
            bool matchNone = false) =>
            new(
                process ?? ProcessFilterSpec.Empty,
                host ?? new HostSelection(HostMatchMode.Glob, null, ImmutableArray<string>.Empty),
                statuses,
                methods,
                keys,
                matchNone);

        internal static IReadOnlyList<SqLtSchemaColumn> DeviceFields { get; } =
        [
            new("DeviceType", SqLtDeclaredType.Integer),
            new("DeviceName", SqLtDeclaredType.Text),
            new("CaptureType", SqLtDeclaredType.Integer),
        ];

        internal static IReadOnlyList<SqLtSchemaColumn> CaptureFields(params SqLtSchemaColumn[] extensions)
        {
            var fields = new List<SqLtSchemaColumn>
            {
                new(WebServiceCaptureMethod.FieldKey_DeviceName, SqLtDeclaredType.Text),
                new(WebServiceCaptureMethod.FieldKey_RequestNumber, SqLtDeclaredType.Integer),
                new(WebServiceCaptureMethod.FieldKey_ConnectionId, SqLtDeclaredType.Integer),
                new(WebServiceCaptureMethod.FieldKey_RequestTimestamp, SqLtDeclaredType.Text),
                new(WebServiceCaptureMethod.FieldKey_ResponseTimestamp, SqLtDeclaredType.Text),
                new(WebServiceCaptureMethod.FieldKey_RequestScheme, SqLtDeclaredType.Text),
                new(WebServiceCaptureMethod.FieldKey_RequestHost, SqLtDeclaredType.Text),
                new(WebServiceCaptureMethod.FieldKey_RequestPort, SqLtDeclaredType.Text),
                new(WebServiceCaptureMethod.FieldKey_RequestMethod, SqLtDeclaredType.Text),
                new(WebServiceCaptureMethod.FieldKey_RequestPath, SqLtDeclaredType.Text),
                new(WebServiceCaptureMethod.FieldKey_RequestStatus, SqLtDeclaredType.Text),
                new(WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders, SqLtDeclaredType.Text),
                new(WebServiceCaptureMethod.FieldKey_RequestBody, SqLtDeclaredType.Text),
                new(WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders, SqLtDeclaredType.Text),
                new(WebServiceCaptureMethod.FieldKey_ResponseBody, SqLtDeclaredType.Text),
                new(WebServiceCaptureMethod.FieldKey_ClientIP, SqLtDeclaredType.Text),
                new(WebProxyCaptureSchema.ClientProcessName, SqLtDeclaredType.Text),
                new(WebProxyCaptureSchema.ClientProcessId, SqLtDeclaredType.Integer),
                new(WebProxyCaptureSchema.ClientProcessStartTime, SqLtDeclaredType.Text),
                new(WebProxyCaptureSchema.ClientProcessStatus, SqLtDeclaredType.Text),
            };
            fields.AddRange(extensions);
            return fields;
        }

        internal static RawExportRow DeviceRow(long rowId, string name = "Export PC") =>
            new(
                default,
                rowId,
                1,
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["DeviceType"] = (long)DeviceType.LocalPC,
                    ["DeviceName"] = name,
                    ["CaptureType"] = (long)CaptureType.WebProxy,
                });

        internal static RawExportRow Row(
            long rowId,
            IReadOnlyDictionary<string, object> values = null,
            long payloadVersion = 1) =>
            new(
                Key(rowId),
                rowId,
                payloadVersion,
                values ?? Values(rowId));

        internal static Dictionary<string, object> Values(long rowId)
        {
            string timestamp = $"2026-11-04T12:00:{rowId % 60:00}.0000000Z";
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [WebServiceCaptureMethod.FieldKey_DeviceName] = "Export PC",
                [WebServiceCaptureMethod.FieldKey_RequestNumber] = rowId,
                [WebServiceCaptureMethod.FieldKey_ConnectionId] = 1000L + rowId,
                [WebServiceCaptureMethod.FieldKey_RequestTimestamp] = timestamp,
                [WebServiceCaptureMethod.FieldKey_ResponseTimestamp] = timestamp,
                [WebServiceCaptureMethod.FieldKey_RequestScheme] = "https",
                [WebServiceCaptureMethod.FieldKey_RequestHost] = "api.example.test",
                [WebServiceCaptureMethod.FieldKey_RequestPort] = "443",
                [WebServiceCaptureMethod.FieldKey_RequestMethod] = "GET",
                [WebServiceCaptureMethod.FieldKey_RequestPath] = "/api/users?name=O'Reilly",
                [WebServiceCaptureMethod.FieldKey_RequestStatus] = "200",
                [WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders] =
                    "GET /api/users?name=O'Reilly HTTP/1.1\r\nHost: api.example.test\r\n",
                [WebServiceCaptureMethod.FieldKey_RequestBody] = Convert.ToBase64String("request"u8),
                [WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders] =
                    "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n",
                [WebServiceCaptureMethod.FieldKey_ResponseBody] = Convert.ToBase64String("response"u8),
                [WebServiceCaptureMethod.FieldKey_ClientIP] = "127.0.0.1",
                [WebProxyCaptureSchema.ClientProcessName] = "Game.exe",
                [WebProxyCaptureSchema.ClientProcessId] = 4100L,
                [WebProxyCaptureSchema.ClientProcessStartTime] = string.Empty,
                [WebProxyCaptureSchema.ClientProcessStatus] = "local",
            };
        }

        internal static string Sha256(string path) =>
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

        internal static SqliteException SqliteFailure(int code)
        {
            ConstructorInfo constructor = typeof(SqliteException)
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(candidate => candidate.GetParameters().Length == 3);
            return (SqliteException)constructor.Invoke(new object[] { "injected sqlite failure", code, code });
        }
    }

    internal sealed class FakeExportSnapshot : ICaptureExportSnapshot
    {
        private readonly Dictionary<CaptureRowKey, RawExportRow> _rows;

        internal FakeExportSnapshot(
            IReadOnlyList<CaptureExportSession> sessions,
            IEnumerable<RawExportRow> rows)
        {
            Sessions = sessions;
            _rows = rows.ToDictionary(row => row.Key);
        }

        public IReadOnlyList<SqLtSchemaColumn> CaptureFields { get; init; } = ExportTestData.CaptureFields();
        public IReadOnlyList<SqLtSchemaColumn> DeviceFields { get; init; } = ExportTestData.DeviceFields;
        public IReadOnlyDictionary<string, RawExportRow> DeviceDescriptors { get; init; } =
            new Dictionary<string, RawExportRow> { ["d1"] = ExportTestData.DeviceRow(7) };
        public IReadOnlyList<CaptureExportSession> Sessions { get; }
        public bool IsLive { get; init; }
        public string InputKind { get; init; } = "records";
        public SqLtFileIdentity? InputIdentity => null;
        public string InputSha256 => null;
        internal List<IReadOnlyList<CaptureRowKey>> ReadRequests { get; } = new();
        internal HashSet<CaptureRowKey> Missing { get; } = new();
        internal HashSet<CaptureRowKey> Mutated { get; } = new();
        internal int MaxConsumed { get; set; } = int.MaxValue;
        internal Action<string> DestinationValidation { get; set; }

        public CaptureExportChunk ReadChunk(
            IReadOnlyList<CaptureRowKey> keys,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadRequests.Add(keys.ToArray());
            CaptureRowKey[] consumed = keys.Take(MaxConsumed).ToArray();
            if (consumed.Any(Mutated.Contains))
            {
                throw new InvalidOperationException("The payload changed.");
            }
            return new CaptureExportChunk(
                consumed,
                consumed.Where(key => !Missing.Contains(key) && _rows.ContainsKey(key))
                    .Select(key => _rows[key])
                    .ToList());
        }

        public void ValidateDestination(string path) => DestinationValidation?.Invoke(path);

        public void Dispose()
        {
        }
    }

    internal sealed class RecordingExportWriter : ICaptureExportWriter
    {
        internal List<RawExportRow> Rows { get; } = new();
        internal Exception WriteFailure { get; set; }
        internal Exception CompleteFailure { get; set; }

        public void Write(RawExportRow row, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (WriteFailure != null)
            {
                throw WriteFailure;
            }
            Rows.Add(row);
        }

        public ExportWriteSummary Complete(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CompleteFailure != null)
            {
                throw CompleteFailure;
            }
            return new ExportWriteSummary(Rows.Count, new SanitizedExportSummary());
        }

        public void Dispose()
        {
        }
    }

    internal sealed class RecordingExportPublisher : ICaptureExportPublisher
    {
        internal int Calls { get; private set; }

        public async Task<PublishedCapture> PublishAsync(
            string destination,
            bool replaceExisting,
            bool allowUnprotected,
            Action<string> validateDestination,
            Func<string, CancellationToken, Task> writeStaged,
            CancellationToken cancellationToken)
        {
            Calls++;
            _ = allowUnprotected;
            validateDestination(destination);
            await writeStaged("staged-output", cancellationToken);
            return new PublishedCapture(123, "output-sha256", replaceExisting, Protected: true);
        }
    }

    internal sealed class ExportTestDirectory : IDisposable
    {
        internal ExportTestDirectory()
        {
            Root = Path.Join(
                AppContext.BaseDirectory,
                "HeadlessTestArtifacts",
                "CaptureExport-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        internal string Root { get; }
        internal string PathOf(string name) => Path.Join(Root, name);

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
