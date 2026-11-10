// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Globalization;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests
{
    internal static class CaptureTimelineTestData
    {
        internal static readonly DateTime Origin = new(2026, 1, 1);

        internal static ProxyConnectionModel Create(
            int number = 1, DateTime? start = null, string host = "example.test",
            TimeSpan? duration = null, string status = "200")
        {
            var model = new ProxyConnectionModel(number, number, 1000);
            Update(model, start ?? Origin.AddMilliseconds(number), host, duration ?? TimeSpan.FromMilliseconds(20), status);
            return model;
        }

        // A request made at begin and answered at done, stored as the capture table stores times (UTC round-trip text): its
        // request time is begin's local time, which pass of a repeated hour included, and its duration the time between them.
        internal static ProxyConnectionModel CreateBetween(int number, DateTimeOffset begin, DateTimeOffset done, string host)
        {
            var model = new ProxyConnectionModel(number, number, 1000);
            UpdateBetween(model, begin, done, host);
            return model;
        }

        internal static void UpdateBetween(ProxyConnectionModel model, DateTimeOffset begin, DateTimeOffset done, string host) =>
            UpdateStored(
                model,
                begin.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                done.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                host);

        // A request's times as the capture table keeps them, as text, read back into the model.
        internal static void UpdateStored(ProxyConnectionModel model, string requestTimestamp, string responseTimestamp, string host = null) =>
            model.UpdateFromDataRecord(new Record((int)model.RequestNumber, Values(
                model,
                requestTimestamp,
                responseTimestamp,
                host ?? model.Host,
                "200")));

        internal static void Update(
            ProxyConnectionModel model, DateTime? start = null, string host = null,
            TimeSpan? duration = null, string status = null)
        {
            var time = start ?? model.RequestTime;
            model.UpdateFromDataRecord(new Record((int)model.RequestNumber, Values(
                model,
                time.ToString("o"),
                (time + (duration ?? model.Duration)).ToString("o"),
                host ?? model.Host,
                status ?? model.Status)));
        }

        private static Dictionary<string, string> Values(
            ProxyConnectionModel model, string requestTimestamp, string responseTimestamp, string host, string status) =>
            new()
            {
                [WebServiceCaptureMethod.FieldKey_RequestTimestamp] = requestTimestamp,
                [WebServiceCaptureMethod.FieldKey_ResponseTimestamp] = responseTimestamp,
                [WebServiceCaptureMethod.FieldKey_RequestHost] = host,
                [WebServiceCaptureMethod.FieldKey_RequestStatus] = status,
                [WebServiceCaptureMethod.FieldKey_RequestScheme] = "https",
                [WebServiceCaptureMethod.FieldKey_RequestPort] = "443",
                [WebServiceCaptureMethod.FieldKey_RequestMethod] = "GET",
                [WebServiceCaptureMethod.FieldKey_RequestPath] = $"/request/{model.RequestNumber}",
                [WebServiceCaptureMethod.FieldKey_DeviceName] = "Test device"
            };

        private sealed class Record(int number, Dictionary<string, string> values) : IDataRecord
        {
            public int RowId => number;
            public long Int(string fieldName) =>
                fieldName == WebServiceCaptureMethod.FieldKey_RequestNumber ? number : 1000;
            public string Str(string fieldName) => values.GetValueOrDefault(fieldName, string.Empty);
        }
    }
}
