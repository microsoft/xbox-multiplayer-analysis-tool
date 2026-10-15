// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;

namespace XMAT.Tests
{
    public class WebServiceDeviceCaptureControllerTests
    {
        [Fact]
        public void GetCaptureRecords_UsesLoadedCaptureDataset()
        {
            const string deviceName = "Saved Capture";
            var record = CreateAnalyzableRecord(deviceName);
            var dataset = new TestDataset(record);
            var controller = new WebServiceDeviceCaptureController(
                deviceName,
                DeviceType.Unknown,
                true);

            controller.LoadCaptureDataset(dataset);

            var loadedRecord = Assert.Single(controller.GetCaptureRecords());

            Assert.Same(record, loadedRecord);
            Assert.Equal(2, dataset.FilteredReadCount);
        }

        [Fact]
        public void BeginTransition_TracksAndReleasesTransitionState()
        {
            var controller = new WebServiceDeviceCaptureController(
                "Test device",
                DeviceType.Unknown,
                true);

            Assert.True(controller.CanBeginTransition);

            using (controller.BeginTransition())
            {
                Assert.True(controller.IsTransitioning);
                Assert.False(controller.CanBeginTransition);
                Assert.Throws<InvalidOperationException>(() => controller.BeginTransition());
            }

            Assert.False(controller.IsTransitioning);
            Assert.True(controller.CanBeginTransition);

            controller.IsRunning = true;

            Assert.False(controller.CanBeginTransition);
            Assert.Throws<InvalidOperationException>(() => controller.BeginTransition());
            Assert.True(controller.CanBeginStopTransition);

            using (controller.BeginStopTransition())
            {
                Assert.True(controller.IsTransitioning);
                Assert.False(controller.CanBeginStopTransition);
                Assert.Throws<InvalidOperationException>(
                    () => controller.BeginStopTransition());
            }

            Assert.False(controller.IsTransitioning);
            Assert.True(controller.CanBeginStopTransition);
        }

        [Fact]
        public void ClearAllCaptures_DoesNotUseActiveTableForLoadedDataset()
        {
            const string deviceName = "Saved Capture";
            var dataset = new TestDataset(CreateAnalyzableRecord(deviceName));
            var controller = new WebServiceDeviceCaptureController(
                deviceName,
                DeviceType.Unknown,
                true);
            controller.LoadCaptureDataset(dataset);

            controller.ClearAllCaptures();

            Assert.Empty(controller.ProxyConnections);
            Assert.Empty(controller.GetCaptureRecords());
        }

        private static IDataRecord CreateAnalyzableRecord(string deviceName)
        {
            var requestTime = DateTime.UtcNow;
            return new TestDataRecord(
                1,
                new Dictionary<string, long>
                {
                    [WebServiceCaptureMethod.FieldKey_RequestNumber] = 1,
                    [WebServiceCaptureMethod.FieldKey_ConnectionId] = 1
                },
                new Dictionary<string, string>
                {
                    [WebServiceCaptureMethod.FieldKey_DeviceName] = deviceName,
                    [WebServiceCaptureMethod.FieldKey_RequestTimestamp] = requestTime.ToString("o"),
                    [WebServiceCaptureMethod.FieldKey_ResponseTimestamp] = requestTime.AddMilliseconds(100).ToString("o"),
                    [WebServiceCaptureMethod.FieldKey_RequestScheme] = "https",
                    [WebServiceCaptureMethod.FieldKey_RequestHost] = "profile.xboxlive.com",
                    [WebServiceCaptureMethod.FieldKey_RequestPort] = "443",
                    [WebServiceCaptureMethod.FieldKey_RequestMethod] = "GET",
                    [WebServiceCaptureMethod.FieldKey_RequestPath] = "/users/me",
                    [WebServiceCaptureMethod.FieldKey_RequestStatus] = "200",
                    [WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders] =
                        "GET /users/me HTTP/1.1\r\n" +
                        "Host: profile.xboxlive.com\r\n" +
                        "x-xbl-api-build-version: adk\r\n\r\n",
                    [WebServiceCaptureMethod.FieldKey_RequestBody] = string.Empty,
                    [WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders] = string.Empty,
                    [WebServiceCaptureMethod.FieldKey_ResponseBody] = string.Empty,
                    [WebServiceCaptureMethod.FieldKey_ClientIP] = "10.0.0.1"
                });
        }

        private sealed class TestDataset : IDataset
        {
            private readonly IDataRecord _record;

            public IEnumerable<IFieldDefinition> FieldDefs => Array.Empty<IFieldDefinition>();
            public IEnumerable<IDataRecord> DataRecords => new[] { _record };
            public int FilteredReadCount { get; private set; }

            internal TestDataset(IDataRecord record)
            {
                _record = record;
            }

            public IEnumerable<IDataRecord> DataRecordsFrom(long firstRowId)
            {
                return DataRecords;
            }

            public IEnumerable<IDataRecord> DataRecordsFrom(long firstRowId, int limit)
            {
                return DataRecords.Take(limit);
            }

            public IEnumerable<IDataRecord> DataRecordsWhen(params IFieldValue[] fieldNamesAndValues)
            {
                FilteredReadCount++;

                var deviceFilter = fieldNamesAndValues.FirstOrDefault(
                    field => field.Name == WebServiceCaptureMethod.FieldKey_DeviceName);
                return deviceFilter?.Value as string == _record.Str(WebServiceCaptureMethod.FieldKey_DeviceName)
                    ? DataRecords
                    : Array.Empty<IDataRecord>();
            }
        }

        private sealed class TestDataRecord : IDataRecord
        {
            private readonly IReadOnlyDictionary<string, long> _integerValues;
            private readonly IReadOnlyDictionary<string, string> _stringValues;

            public int RowId { get; }

            internal TestDataRecord(
                int rowId,
                IReadOnlyDictionary<string, long> integerValues,
                IReadOnlyDictionary<string, string> stringValues)
            {
                RowId = rowId;
                _integerValues = integerValues;
                _stringValues = stringValues;
            }

            public long Int(string fieldName)
            {
                return _integerValues.TryGetValue(fieldName, out var value) ? value : 0;
            }

            public string Str(string fieldName)
            {
                return _stringValues.TryGetValue(fieldName, out var value) ? value : string.Empty;
            }
        }
    }
}
