// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

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

        internal static void Update(
            ProxyConnectionModel model, DateTime? start = null, string host = null,
            TimeSpan? duration = null, string status = null)
        {
            var time = start ?? model.RequestTime;
            var values = new Dictionary<string, string>
            {
                [WebServiceCaptureMethod.FieldKey_RequestTimestamp] = time.ToString("o"),
                [WebServiceCaptureMethod.FieldKey_ResponseTimestamp] = (time + (duration ?? model.Duration)).ToString("o"),
                [WebServiceCaptureMethod.FieldKey_RequestHost] = host ?? model.Host,
                [WebServiceCaptureMethod.FieldKey_RequestStatus] = status ?? model.Status,
                [WebServiceCaptureMethod.FieldKey_RequestScheme] = "https",
                [WebServiceCaptureMethod.FieldKey_RequestPort] = "443",
                [WebServiceCaptureMethod.FieldKey_RequestMethod] = "GET",
                [WebServiceCaptureMethod.FieldKey_RequestPath] = $"/request/{model.RequestNumber}",
                [WebServiceCaptureMethod.FieldKey_DeviceName] = "Test device"
            };
            model.UpdateFromDataRecord(new Record((int)model.RequestNumber, values));
        }

        private sealed class Record(int number, Dictionary<string, string> values) : IDataRecord
        {
            public int RowId => number;
            public long Int(string fieldName) =>
                fieldName == WebServiceCaptureMethod.FieldKey_RequestNumber ? number : 1000;
            public string Str(string fieldName) => values.GetValueOrDefault(fieldName, string.Empty);
        }
    }
}
