// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Drawing;
using XMAT.DataAbstractionLayer;
using XMAT.Models;
using XMAT.SharedInterfaces;
using XMAT.Tests.Fixtures;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests
{
    [Collection(CaptureUiApplicationCollection.Name)]
    public sealed class CaptureTextExportPolicyTests
    {
        private readonly CaptureUiApplicationFixture _application;

        public CaptureTextExportPolicyTests(
            CaptureUiApplicationFixture application)
        {
            _application = application;
        }

        [Theory]
        [InlineData("ambiguous")]
        [InlineData("custom")]
        public async Task SanitizedExport_RefusesTheSelectedUnknownSourceBeforeWorkerOrFileWork(
            string sourceKind)
        {
            using var directory = new TestDirectory();
            string markedPath =
                SyntheticCaptures.WriteXmatcap(
                    directory.Path,
                    "marked.xmatcap",
                    new SyntheticXmatcapOptions(
                        RequestsPerDevice: 1));
            using var marked =
                new SqLtReadonlyDatabase(
                    markedPath);
            string destination =
                Path.Join(
                    directory.Path,
                    "refused.saz");
            Task exporting = null;
            bool workerStarting = false;
            _application.Run(window =>
            {
                var controller =
                    new WebServiceDeviceCaptureController(
                        "selected",
                        DeviceType.GenericProxyDevice,
                        true);
                IDataset dataset =
                    sourceKind == "ambiguous"
                        ? new SourceDataset(
                            CaptureTextSource.Unknown,
                            new DataRecord(
                                1,
                                "selected"))
                        : new CustomDataset(
                            new DataRecord(
                                1,
                                "selected"));
                controller.LoadCaptureDataset(
                    dataset);
                var context =
                    new Context(controller);
                var model =
                    new CaptureAppModel
                    {
                        LoadedDatabase = marked,
                        SelectedDeviceContext =
                            context,
                        SanitizedExportWorkerStarting =
                            () => workerStarting = true,
                    };
                model.CaptureDeviceContexts.Add(
                    context);
                exporting =
                    model.ExportSanitizedDataCapturesAsync(
                        destination);
            });

            InvalidOperationException exception =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => exporting);

            Assert.Equal(
                Localization.GetLocalizedString(
                    "CAPTURE_TEXT_EXPORT_POLICY_REQUIRED"),
                exception.Message);
            Assert.Equal(
                "The sanitized export could not be completed. No successful export was recorded: " +
                    "This capture has no XMAT text-format marker. Create a marked copy with xmat mark-capture-text, " +
                    "open that copy, and export it instead.",
                Localization.GetLocalizedString(
                    "SANITIZED_EXPORT_FAILED",
                    exception.Message));
            Assert.False(workerStarting);
            Assert.False(File.Exists(destination));
            Assert.Empty(
                Directory.GetFiles(
                    directory.Path,
                    "*.tmp"));
        }

        [Fact]
        public async Task SanitizedExport_LiteralSourcePassesTheGate()
        {
            using var directory = new TestDirectory();
            string destination =
                Path.Join(
                    directory.Path,
                    "literal.saz");
            Task exporting = null;
            bool workerStarting = false;
            var expected =
                new InvalidOperationException(
                    "worker seam");
            _application.Run(window =>
            {
                var controller =
                    new WebServiceDeviceCaptureController(
                        "selected",
                        DeviceType.GenericProxyDevice,
                        true);
                controller.LoadCaptureDataset(
                    new SourceDataset(
                        CaptureTextSource.Literal,
                        new DataRecord(
                            1,
                            "selected")));
                var context =
                    new Context(controller);
                var model =
                    new CaptureAppModel
                    {
                        SelectedDeviceContext =
                            context,
                        SanitizedExportWorkerStarting =
                            () =>
                            {
                                workerStarting = true;
                                throw expected;
                            },
                    };
                model.CaptureDeviceContexts.Add(
                    context);
                exporting =
                    model.ExportSanitizedDataCapturesAsync(
                        destination);
            });

            InvalidOperationException actual =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => exporting);

            Assert.Same(expected, actual);
            Assert.True(workerStarting);
            Assert.False(File.Exists(destination));
        }

        private sealed class SourceDataset(
            CaptureTextSource source,
            params IDataRecord[] records) :
            Dataset(records),
            ICaptureTextSourceProvider
        {
            public CaptureTextSource CaptureTextSource { get; } =
                source;
        }

        private sealed class CustomDataset(
            params IDataRecord[] records) :
            Dataset(records)
        {
        }

        private class Dataset(
            params IDataRecord[] records) :
            IDataset
        {
            public IEnumerable<IFieldDefinition> FieldDefs =>
                Array.Empty<IFieldDefinition>();

            public IEnumerable<IDataRecord> DataRecords =>
                records;

            public IEnumerable<IDataRecord> DataRecordsFrom(
                long firstRowId) =>
                records.Where(
                    record =>
                        record.RowId >=
                        firstRowId);

            public IEnumerable<IDataRecord> DataRecordsFrom(
                long firstRowId,
                int limit) =>
                DataRecordsFrom(firstRowId)
                    .Take(limit);

            public IEnumerable<IDataRecord> DataRecordsWhen(
                params IFieldValue[] filters) =>
                records.Where(
                    record =>
                        filters.All(
                            filter =>
                                record.Str(
                                    filter.Name) ==
                                (filter.Value as string)));
        }

        private sealed class DataRecord(
            int number,
            string device) :
            IDataRecord
        {
            private readonly ProxyConnectionModel _source =
                new(
                    number,
                    "200",
                    DateTime.UnixEpoch,
                    TimeSpan.FromMilliseconds(10),
                    "https",
                    "example.test",
                    "GET",
                    "/",
                    "GET / HTTP/1.1\r\nHost: example.test\r\n",
                    Array.Empty<byte>(),
                    "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n",
                    Array.Empty<byte>(),
                    "10.0.0.1");

            public int RowId =>
                number;

            public long Int(string name) =>
                name ==
                    WebServiceCaptureMethod.FieldKey_RequestNumber
                    ? number
                    : 1;

            public string Str(string name) =>
                name switch
                {
                    WebServiceCaptureMethod.FieldKey_DeviceName =>
                        device,
                    WebServiceCaptureMethod.FieldKey_RequestTimestamp =>
                        ProxyConnectionModel.ToDateTimeString(
                            _source.RequestTime),
                    WebServiceCaptureMethod.FieldKey_ResponseTimestamp =>
                        ProxyConnectionModel.ToDateTimeString(
                            _source.RequestTime +
                            _source.Duration),
                    WebServiceCaptureMethod.FieldKey_RequestScheme =>
                        _source.Scheme,
                    WebServiceCaptureMethod.FieldKey_RequestHost =>
                        _source.Host,
                    WebServiceCaptureMethod.FieldKey_RequestPort =>
                        _source.Port,
                    WebServiceCaptureMethod.FieldKey_RequestMethod =>
                        _source.Method,
                    WebServiceCaptureMethod.FieldKey_RequestPath =>
                        _source.Path,
                    WebServiceCaptureMethod.FieldKey_RequestStatus =>
                        _source.Status,
                    WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders =>
                        _source.RequestLineAndHeaders,
                    WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders =>
                        _source.ResponseLineAndHeaders,
                    WebServiceCaptureMethod.FieldKey_RequestBody =>
                        string.Empty,
                    WebServiceCaptureMethod.FieldKey_ResponseBody =>
                        string.Empty,
                    WebServiceCaptureMethod.FieldKey_ClientIP =>
                        _source.ClientIP,
                    _ => string.Empty,
                };
        }

        private sealed class Context(
            WebServiceDeviceCaptureController controller) :
            ICaptureDeviceContext
        {
            public DeviceType DeviceType =>
                DeviceType.GenericProxyDevice;

            public CaptureType CaptureType =>
                CaptureType.WebProxy;

            public string DeviceName =>
                controller.DeviceName;

            public bool IsSelected { get; set; }

            public bool IsReadOnly { get; set; } =
                true;

            public Color DeviceColor { get; set; }

            public IDeviceCaptureController CaptureController =>
                controller;
        }

        private sealed class TestDirectory : IDisposable
        {
            internal TestDirectory()
            {
                Path =
                    System.IO.Path.Join(
                        AppContext.BaseDirectory,
                        "CaptureTextExportPolicy",
                        Guid.NewGuid().ToString(
                            "N"));
                Directory.CreateDirectory(Path);
            }

            internal string Path { get; }

            public void Dispose()
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(
                        Path,
                        true);
                }
            }
        }
    }
}
