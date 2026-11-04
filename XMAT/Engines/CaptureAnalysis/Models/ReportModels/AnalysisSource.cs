// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.XboxLiveCaptureAnalysis.ReportModels
{
    // The capture tab that an analysis read, so that a report goes to its requests in that tab and nowhere else: tabs share
    // names, and request numbers, so neither identifies the source. The tab is kept by its capture controller, by
    // reference; reports keep it while they are open, which costs little once the tab is closed, since closing it clears
    // its rows. Its first row as the analysis saw it is kept weakly, so a report never keeps capture rows alive: while the
    // tab still shows that row first, it shows the rows that were analyzed, and once it was cleared, reloaded or replaced
    // by an import, a row with the same number would be other traffic.
    internal sealed class AnalysisSource
    {
        private readonly WeakReference<ProxyConnectionModel> _firstRow;

        // Created on the UI thread, which owns the tab's rows, when the analysis starts.
        internal AnalysisSource(WebServiceDeviceCaptureController controller)
        {
            Controller = controller ?? throw new ArgumentNullException(nameof(controller));
            ProxyConnectionsCollection rows = controller.ProxyConnections;
            _firstRow = rows.Count > 0 ? new WeakReference<ProxyConnectionModel>(rows[0]) : null;
        }

        internal WebServiceDeviceCaptureController Controller { get; }

        // The analyzed request with this number, on the UI thread; null when the tab no longer shows the rows that the
        // analysis read, or has no such request.
        internal ProxyConnectionModel FindRequest(long requestNumber)
        {
            ProxyConnectionsCollection rows = Controller.ProxyConnections;
            if (_firstRow == null ||
                rows.Count == 0 ||
                !_firstRow.TryGetTarget(out ProxyConnectionModel first) ||
                !ReferenceEquals(rows[0], first))
            {
                return null;
            }

            return rows.FirstOrDefault(row => row.RequestNumber == requestNumber);
        }
    }
}
