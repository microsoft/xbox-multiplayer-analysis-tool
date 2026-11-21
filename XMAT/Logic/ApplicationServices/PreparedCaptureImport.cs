// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XMAT.DataAbstractionLayer;
using XMAT.NetworkTrace;
using XMAT.NetworkTrace.Models;
using XMAT.NetworkTrace.NTDE;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;

namespace XMAT.ApplicationServices
{
    internal sealed class PreparedCaptureImport : IDisposable
    {
        private readonly WebServiceDeviceCaptureController.ImportedCaptures _web;
        private readonly NetworkTraceEventStage _stage;
        private readonly NetworkTracePacketsCollection _packets;
        internal int Count => _web?.Connections.Count ?? _packets.Count;
        internal XMAT.Operations.Captures.NetworkShapingMetadata Shaping => _web?.Shaping;
        private PreparedCaptureImport(WebServiceDeviceCaptureController.ImportedCaptures web) { _web = web; }
        private PreparedCaptureImport(NetworkTraceEventStage stage, NetworkTracePacketsCollection packets)
        { _stage = stage; _packets = packets; }

        internal static Task<PreparedCaptureImport> PrepareAsync(
            string path, CaptureType type, string deviceName, WebServiceDeviceCaptureController controller,
            long maximumBytes, Func<string, Action<string>, CancellationToken, Task> readEtl, CancellationToken cancellationToken) =>
            Task.Run(async () =>
            {
                if (type == CaptureType.WebProxy)
                    return new PreparedCaptureImport(WebServiceCaptureMethod.PrepareFiddlerImport(
                        path, controller, maximumBytes, cancellationToken));
                var stage = new NetworkTraceEventStage(maximumBytes, cancellationToken);
                try
                {
                    if (readEtl != null)
                        await readEtl(path, stage.Append, cancellationToken);
                    else
                    {
                        var engine = new LocalNetworkTraceEngineImpl();
                        EventHandler<string> handler = (_, record) => stage.Append(record);
                        engine.EventRecordAvailable += handler;
                        try
                        { await engine.ReadEventsFromFileAsync(path, cancellationToken); }
                        finally { engine.EventRecordAvailable -= handler; }
                    }
                    stage.Complete();
                    var packets = new NetworkTracePacketsCollection();
                    foreach (var record in stage.Records(deviceName))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        packets.AddFromDataRecord(record);
                    }
                    return new PreparedCaptureImport(stage, packets);
                }
                catch { stage.Dispose(); throw; }
            }, cancellationToken);

        internal void Persist(IDataTable table, string name, long maximumBytes, CancellationToken token)
        {
            if (_web != null)
                WebServiceCaptureMethod.PersistFiddlerImport(_web, table, name, maximumBytes, token);
            else
            {
                var ids = ((IImportedCaptureTable)table).ReplaceImportedRows(
                    CaptureTextSource.Literal, new FieldValue<string>(NetworkTraceCaptureMethod.FieldKey_DeviceName, name),
                    Rows(name, maximumBytes, token), token);
                for (var index = 0; index < _packets.Count; index++)
                    _packets[index].RecordRowId = ids[index];
            }
        }

        private IEnumerable<object[]> Rows(string name, long maximumBytes, CancellationToken token)
        {
            var remaining = maximumBytes;
            foreach (var row in _stage.Rows(name))
            {
                token.ThrowIfCancellationRequested();
                var size = 64L + row.Sum(value => value is string text
                    ? Encoding.UTF8.GetByteCount(text) + 16L : 24);
                if (size > remaining)
                    throw new QueryException(QueryError.ResultLimit, "The imported trace rows exceed the approved data limit.");
                remaining -= size;
                yield return row;
            }
        }

        internal void Apply(IDeviceCaptureController controller, IDataTable table)
        {
            if (_web != null)
                ((WebServiceDeviceCaptureController)controller).ApplyImportedCaptures(_web, table);
            else
                ((NetworkTraceCaptureController)controller).ApplyLoadedCaptures(_packets);
        }

        internal bool Notify(IDeviceCaptureController controller)
        {
            if (_web != null)
                return WebServiceCaptureMethod.NotifyFiddlerImport((WebServiceDeviceCaptureController)controller);
            try
            { ((NetworkTraceCaptureController)controller).NotifyLoadedCaptures(); return true; }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                PublicUtilities.AppLog(LogLevel.ERROR, $"Import view notification failed: {exception.GetType().Name}");
                return false;
            }
        }

        public void Dispose()
        {
            try
            { _stage?.Dispose(); }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            { PublicUtilities.AppLog(LogLevel.ERROR, $"Trace stage cleanup failed: {exception.GetType().Name}"); }
        }
    }
}
