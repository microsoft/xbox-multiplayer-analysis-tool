// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;
using XMAT.Models;
using XMAT.NetworkTrace;
using XMAT.NetworkTrace.Models;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.ApplicationServices
{
    internal sealed class CaptureDataOperations(CaptureAppModel model) : ICaptureDataOperations
    {
        public Task<CaptureClearReceipt> ClearAsync(
            Guid captureId, long expectedRevision, CancellationToken cancellationToken = default) =>
            model.CaptureCatalog.WithTargetAsync<CaptureClearReceipt>(captureId, expectedRevision, async (target, _) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (target.IsReadOnly || target.CaptureController.IsRunning ||
                    target.CaptureController is WebServiceDeviceCaptureController { IsTransitioning: true } ||
                    target.CaptureController is NetworkTraceCaptureController { IsTransitioning: true } ||
                    model.CaptureCatalog.DescribeTarget(target).RecoveryRequired)
                    throw new QueryException(QueryError.SourceBusy, "Stop the writable capture and resolve pending transitions/recovery before clearing.");
                var name = target.CaptureType == CaptureType.WebProxy
                    ? WebServiceCaptureMethod.TableName : NetworkTraceCaptureMethod.TableName;
                var table = model.ActiveDatabase?.TableByName(name);
                if (table is not IImportedCaptureTable imported)
                    throw new CaptureFileException(CaptureFileFailure.Unavailable);
                var web = target.CaptureController as WebServiceDeviceCaptureController;
                var empty = web == null ? null : new WebServiceDeviceCaptureController.ImportedCaptures(
                    new ProxyConnectionsCollection(), web.UpdateFilter(Array.Empty<string>()),
                    web.UpdateFilter(Array.Empty<string>()), web.UpdateFilter(Array.Empty<string>()));
                IDisposable lease = null;
                var notified = true;
                CaptureDescriptor committed = null;
                try
                {
                    if (!model.TryBeginCaptureDataOperation(out lease))
                        throw new QueryException(QueryError.SourceBusy, "Finish the current data or analysis operation first.");
                    cancellationToken.ThrowIfCancellationRequested();
                    if (model.CaptureCatalog.DescribeTarget(target).Revision != expectedRevision ||
                        target.CaptureController.IsRunning)
                        throw new QueryException(QueryError.SourceChanged, "The capture changed before clearing.");
                    await Task.Run(() => imported.ReplaceImportedRows(
                        new FieldValue<string>("DeviceName", target.DeviceName), Array.Empty<object[]>(),
                        cancellationToken), cancellationToken);
                    if (web != null)
                    {
                        web.ApplyImportedCaptures(empty, table, clearMessages: true);
                    }
                    else
                    {
                        var trace = (NetworkTraceCaptureController)target.CaptureController;
                        trace.ApplyLoadedCaptures(new NetworkTracePacketsCollection());
                    }
                    model.CaptureCatalog.RecordDataReplacement(target);
                    committed = model.CaptureCatalog.DescribeTarget(target);
                    if (web != null)
                    {
                        notified &= WebServiceCaptureMethod.NotifyFiddlerImport(web);
                        notified &= Notify(() => web.RaisePropertyChange(nameof(WebServiceDeviceCaptureController.WebSocketMessages)));
                        notified &= Notify(() => web.RaisePropertyChange(nameof(WebServiceDeviceCaptureController.SelectedWebSocketMessage)));
                    }
                    else
                        notified &= Notify(((NetworkTraceCaptureController)target.CaptureController).NotifyLoadedCaptures);
                    notified &= Notify(() => model.NotifyCaptureDataRebound(target, model.ActiveDatabase));
                }
                finally
                {
                    if (lease != null)
                        notified &= Notify(lease.Dispose);
                }
                return new(captureId, committed.Revision, committed.CaptureEpoch, true, notified);
            });

        private static bool Notify(Action action)
        {
            try
            { action(); return true; }
            catch (Exception exception) when (!ExceptionUtilities.IsFatal(exception))
            {
                PublicUtilities.AppLog(LogLevel.ERROR, $"Capture clear notification failed: {exception.GetType().Name}");
                return false;
            }
        }
    }
}
