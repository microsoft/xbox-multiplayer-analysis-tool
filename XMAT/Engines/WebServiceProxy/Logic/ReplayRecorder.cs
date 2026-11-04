// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using XMAT.Operations.Replay;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture.Models;

namespace XMAT.WebServiceCapture
{
    // What a Replay menu item asks for: the captured rows to reissue, in list order, and how.
    internal sealed record ReissueRequest(
        IReadOnlyList<ProxyConnectionModel> Captures,
        int Repeat = 1,
        bool Unconditional = false,
        bool Verify = false);

    // The GUI's sink for ReplayOperation: each send becomes a row of the read-only Replays tab, with the request XMAT
    // sent and the response it got, so replays can be inspected and analyzed like captured traffic. A
    // replay row knows the number of the captured request it replays (ReplayOf); that link is kept in memory only.
    internal sealed class ReplayRecorder : IReplayExchangeSink
    {
        internal const string DeviceName = "Replays";

        private readonly WebServiceDeviceCaptureController _controller;
        private readonly IDataTable _table;
        private readonly Action<Action> _onUiThread;
        private long _lastNumber;

        // Created on the UI thread; onUiThread runs an action on it synchronously (the view passes its dispatcher).
        // Replay rows are numbered after the rows the tab already has.
        internal ReplayRecorder(WebServiceDeviceCaptureController controller, IDataTable table, Action<Action> onUiThread)
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
            _table = table;
            _onUiThread = onUiThread ?? throw new ArgumentNullException(nameof(onUiThread));
            _lastNumber = controller.ProxyConnections.Count == 0
                ? 0
                : controller.ProxyConnections.Max(row => row.RequestNumber);
        }

        // The replay that the Replay menu started, if any. One runs at a time, so Cancel replay always means that one.
        // Read and written on the UI thread only.
        internal static CancellationTokenSource Current { get; set; }

        // ReplayOperation calls this off the UI thread, one send at a time; the row is added on the UI thread.
        public void Add(ReplayItemResult result, ReplayExchange exchange)
        {
            ArgumentNullException.ThrowIfNull(result);
            ProxyConnectionModel row = ToRow(Interlocked.Increment(ref _lastNumber), result.RequestNumber, exchange);
            _onUiThread(() =>
            {
                if (_table != null)
                    row.AddToDataTable(DeviceName, _table);
                _controller.ProxyConnections.Add(row);
            });
        }

        // A capture row for one send: the request as XMAT built it and the response, with no status when none came.
        internal static ProxyConnectionModel ToRow(long number, long? replayOf, ReplayExchange exchange)
        {
            ArgumentNullException.ThrowIfNull(exchange);
            Uri uri = exchange.RequestUri;
            ReplayMessage request = exchange.Request;
            ReplayMessage response = exchange.Response;
            return new ProxyConnectionModel(
                number,
                exchange.StatusCode == 0 ? string.Empty : exchange.StatusCode.ToString(CultureInfo.InvariantCulture),
                exchange.Started,
                exchange.Duration,
                uri?.Scheme ?? string.Empty,
                uri?.Authority ?? string.Empty,
                request?.StartLine.Split(' ')[0] ?? string.Empty,
                uri?.PathAndQuery ?? string.Empty,
                Head(request),
                request?.Body ?? Array.Empty<byte>(),
                Head(response),
                response?.Body ?? Array.Empty<byte>(),
                "0.0.0.0")
            {
                ReplayOf = replayOf,
            };
        }

        // The start line and header lines, as captures store them.
        private static string Head(ReplayMessage message)
        {
            if (message == null)
                return string.Empty;
            var head = new StringBuilder(message.StartLine).Append("\r\n");
            foreach (KeyValuePair<string, string> header in message.Headers.Concat(message.ContentHeaders))
                head.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            return head.ToString();
        }
    }
}
