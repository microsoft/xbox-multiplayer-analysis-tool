// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.DataAbstractionLayer;
using XMAT.Operations;
using XMAT.Operations.Capture;
using XMAT.Operations.Processes;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;
using XMAT.XboxLiveCaptureAnalysis;

namespace XMAT.Tests.Operations.Capture
{
    internal static class SazExportTestSupport
    {
        internal static OperationResult<SazExportResult> ExportRecords(
            string path,
            IEnumerable<ProxyConnectionModel> records,
            SazExportKind kind,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            using var stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None);
            return ExportRecords(
                stream,
                records,
                kind,
                cancellationToken);
        }

        internal static OperationResult<SazExportResult> ExportRecords(
            Stream stream,
            IEnumerable<ProxyConnectionModel> records,
            SazExportKind kind,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(stream);
            ArgumentNullException.ThrowIfNull(records);
            IReadOnlyList<IDataRecord> source = records
                .Select(
                    (record, index) =>
                        (IDataRecord)new ModelRecord(
                            index + 1,
                            record))
                .ToArray();
            var operation = new SazExportOperation();
            OperationResult<SazExportPlan> prepared =
                operation.PrepareAsync(
                        new SazExportPlanRequest(
                            SazExportSource.FromRecords(
                                source,
                                CaptureTextSource.Literal),
                            kind,
                            null,
                            SazExportBudgets.Default),
                        OperationContext.None,
                        cancellationToken)
                    .GetAwaiter()
                    .GetResult();
            if (!prepared.Succeeded)
            {
                return new(
                    prepared.Outcome,
                    null,
                    prepared.Diagnostics);
            }

            return operation.ExecuteAsync(
                    new SazExportRequest(
                        prepared.Value,
                        new SazExportDestination.ToStream(
                            stream),
                        null),
                    OperationContext.None,
                    cancellationToken)
                .GetAwaiter()
                .GetResult();
        }

        internal static StoredRow Stored(
            ProxyConnectionModel model,
            long rowId = 1)
        {
            ArgumentNullException.ThrowIfNull(model);
            return new StoredRow(
                rowId,
                model.RequestNumber,
                StoredRowState.Available,
                0,
                model.RequestLineAndHeaders,
                StoredBody.FromBytes(
                    model.RequestBody ??
                    Array.Empty<byte>()),
                model.ResponseLineAndHeaders,
                StoredBody.FromBytes(
                    model.ResponseBody ??
                    Array.Empty<byte>()),
                model.Scheme,
                false,
                ProxyConnectionModel.ToDateTimeString(
                    model.RequestTime),
                false,
                ProxyConnectionModel.ToDateTimeString(
                    model.ResponseDoneTime),
                false,
                model.ClientIP,
                false);
        }

        internal sealed class ScriptedSource :
            SazExportSource
        {
            private readonly IReadOnlyList<SazExportKey> _keys;
            private readonly Func<int, CancellationToken, StoredRow> _read;
            private int _next;

            internal ScriptedSource(
                IReadOnlyList<SazExportKey> keys,
                Func<int, CancellationToken, StoredRow> read,
                CaptureTextSource textSource = null)
                : base(
                    textSource ??
                    CaptureTextSource.Literal)
            {
                _keys = keys;
                _read = read;
            }

            internal int Reads => Volatile.Read(ref _next);

            internal override long? GetWatermark(
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return _keys.Count == 0
                    ? null
                    : _keys.Max(
                        static key => key.RowId);
            }

            internal override SazExportPage ReadPage(
                long? afterRowId,
                long throughRowId,
                int scan,
                SazExportBudgets budgets,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SazExportKey[] page = _keys
                    .Where(
                        key =>
                            key.RowId >
                                (afterRowId ??
                                 long.MinValue) &&
                            key.RowId <= throughRowId)
                    .Take(scan)
                    .ToArray();
                return new(
                    page,
                    page.Length == 0
                        ? afterRowId
                        : page[^1].RowId);
            }

            internal override StoredRow ReadRow(
                SazExportKey key,
                SazExportBudgets budgets,
                CancellationToken cancellationToken)
            {
                int index =
                    Interlocked.Increment(
                        ref _next) -
                    1;
                return _read(
                    index,
                    cancellationToken);
            }
        }

        private sealed class ModelRecord :
            IDataRecord,
            IRawBodyDataRecord,
            ICaptureTextSourceProvider,
            IOptionalTypedDataRecord
        {
            private readonly ProxyConnectionModel _model;

            internal ModelRecord(
                int rowId,
                ProxyConnectionModel model)
            {
                ArgumentNullException.ThrowIfNull(model);
                RowId = rowId;
                _model = model;
            }

            public int RowId { get; }

            public CaptureTextSource CaptureTextSource =>
                global::XMAT.DataAbstractionLayer
                    .CaptureTextSource.Literal;

            public long Int(string fieldName) =>
                fieldName ==
                    WebServiceCaptureMethod
                        .FieldKey_RequestNumber
                    ? _model.RequestNumber
                    : 0;

            public string Str(string fieldName) =>
                fieldName switch
                {
                    WebServiceCaptureMethod
                        .FieldKey_RequestTimestamp =>
                        ProxyConnectionModel
                            .ToDateTimeString(
                                _model.RequestTime),
                    WebServiceCaptureMethod
                        .FieldKey_ResponseTimestamp =>
                        ProxyConnectionModel
                            .ToDateTimeString(
                                _model.ResponseDoneTime),
                    WebServiceCaptureMethod
                        .FieldKey_RequestScheme =>
                        _model.Scheme,
                    WebServiceCaptureMethod
                        .FieldKey_RequestLineAndHeaders =>
                        _model.RequestLineAndHeaders,
                    WebServiceCaptureMethod
                        .FieldKey_RequestBody =>
                        Convert.ToBase64String(
                            _model.RequestBody ??
                            Array.Empty<byte>()),
                    WebServiceCaptureMethod
                        .FieldKey_ResponseLineAndHeaders =>
                        _model.ResponseLineAndHeaders,
                    WebServiceCaptureMethod
                        .FieldKey_ResponseBody =>
                        Convert.ToBase64String(
                            _model.ResponseBody ??
                            Array.Empty<byte>()),
                    WebServiceCaptureMethod
                        .FieldKey_ClientIP =>
                        _model.ClientIP,
                    _ => null,
                };

            public bool TryReadString(
                string fieldName,
                out string value)
            {
                value = ProcessField(fieldName) as string;
                return value != null;
            }

            public bool TryReadInt64(
                string fieldName,
                out long value)
            {
                object field = ProcessField(fieldName);
                value = field is long number ? number : 0;
                return field is long;
            }

            private object ProcessField(
                string fieldName) =>
                ClientProcessRecordCodec
                    .ToFields(_model.ClientProcess)
                    .FirstOrDefault(
                        field => field.Name == fieldName)
                    ?.Value;

            public ReadOnlyMemory<byte> RawBody(
                string fieldName)
            {
                if (fieldName ==
                    WebServiceCaptureMethod
                        .FieldKey_RequestBody)
                {
                    return _model.RequestBody ??
                        Array.Empty<byte>();
                }

                if (fieldName ==
                    WebServiceCaptureMethod
                        .FieldKey_ResponseBody)
                {
                    return _model.ResponseBody ??
                        Array.Empty<byte>();
                }

                return ReadOnlyMemory<byte>.Empty;
            }
        }
    }
}
