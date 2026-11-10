// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Drawing;
using System.Reflection;
using System.Windows.Threading;
using XMAT.DataAbstractionLayer;
using XMAT.Models;
using XMAT.Operations.Capture;
using XMAT.SharedInterfaces;
using XMAT.Tests.Fixtures;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests
{
    // Q's interim refusal of an unmarked capture's export (Q 3.6) and its source cases, on S2's gate (design section 9, H8;
    // G13): the selected tab's own text source decides, never the loaded database's (M-Q13); a source that XMAT cannot bind,
    // an unknown one or a custom dataset without a class (M-Q15), is refused before any question, worker or file; and a
    // Literal active import passes the gate, asked nothing about its text.
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
        [InlineData("ambiguous", false)]
        [InlineData("ambiguous", true)]
        [InlineData("custom", false)]
        [InlineData("custom", true)]
        public async Task Export_RefusesTheSelectedUnknownSourceBeforeAQuestionWorkerOrFileWork(
            string sourceKind,
            bool sanitized)
        {
            SazExportKind kind =
                sanitized
                    ? SazExportKind.Sanitized
                    : SazExportKind.Plain;
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
            var logged = new List<string>();
            ScriptedCaptureExportDialogs dialogs = null;
            CaptureExportOutcome outcome = null;
            await _application.RunAsync(async () =>
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
                    new CaptureAppModel(
                        Dispatcher.CurrentDispatcher)
                    {
                        LoadedDatabase = marked,
                        SelectedDeviceContext =
                            context,
                    };
                model.CaptureDeviceContexts.Add(
                    context);
                dialogs =
                    new ScriptedCaptureExportDialogs(
                        new CaptureExportScript
                        {
                            Destination = destination,
                            AcceptAsStored = true,
                        });
                CaptureExportWorkflow workflow =
                    CaptureExportDriver.Workflow(
                        dialogs);
                workflow.LogError = logged.Add;
                outcome =
                    await workflow.RunAsync(
                        model,
                        kind,
                        owner: null);
                Assert.False(
                    model.IsCaptureDataOperationRunning);
            });

            Assert.Equal(
                CaptureTextSemantics.Literal,
                marked.CaptureTextSource.Semantics);
            Assert.Equal(
                CaptureTextSourceException.SourceUnavailableCode,
                outcome.Code);
            Assert.Equal(
                new[] { "EXPORT_SOURCE_UNAVAILABLE" },
                outcome.Shown);
            Assert.Empty(dialogs.Questions);
            Assert.Empty(dialogs.Steps);
            Assert.Equal(
                CaptureExportWorkflow.LogPrefix +
                    CaptureTextSourceException.SourceUnavailableCode,
                Assert.Single(logged));
            Assert.False(File.Exists(destination));
            Assert.Empty(
                Directory.GetFiles(
                    directory.Path,
                    ".xmat-*"));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Export_LiteralActiveImportPassesTheGate(
            bool sanitized)
        {
            SazExportKind kind =
                sanitized
                    ? SazExportKind.Sanitized
                    : SazExportKind.Plain;
            ScriptedCaptureExportDialogs dialogs = null;
            CaptureExportOutcome outcome = null;
            await _application.RunAsync(async () =>
            {
                using var database = new SqLtDatabase();
                database.Initialize();
                IDataTable table =
                    database.CreateTable(
                        WebServiceCaptureMethod.TableName,
                        new Field<string>(WebServiceCaptureMethod.FieldKey_DeviceName),
                        new Field<long>(WebServiceCaptureMethod.FieldKey_RequestNumber),
                        new Field<long>(WebServiceCaptureMethod.FieldKey_ConnectionId),
                        new Field<string>(WebServiceCaptureMethod.FieldKey_RequestTimestamp),
                        new Field<string>(WebServiceCaptureMethod.FieldKey_ResponseTimestamp),
                        new Field<string>(WebServiceCaptureMethod.FieldKey_RequestScheme),
                        new Field<string>(WebServiceCaptureMethod.FieldKey_RequestHost),
                        new Field<string>(WebServiceCaptureMethod.FieldKey_RequestPort),
                        new Field<string>(WebServiceCaptureMethod.FieldKey_RequestMethod),
                        new Field<string>(WebServiceCaptureMethod.FieldKey_RequestPath),
                        new Field<string>(WebServiceCaptureMethod.FieldKey_RequestStatus),
                        new Field<string>(WebServiceCaptureMethod.FieldKey_RequestLineAndHeaders),
                        new Field<string>(WebServiceCaptureMethod.FieldKey_RequestBody),
                        new Field<string>(WebServiceCaptureMethod.FieldKey_ResponseLineAndHeaders),
                        new Field<string>(WebServiceCaptureMethod.FieldKey_ResponseBody),
                        new Field<string>(WebServiceCaptureMethod.FieldKey_ClientIP));
                var imported =
                    new ProxyConnectionModel(new DataRecord(1, "selected"));
                ((IImportedCaptureTable)table).ReplaceImportedRows(
                    CaptureTextSource.Literal,
                    new FieldValue<string>(
                        WebServiceCaptureMethod.FieldKey_DeviceName,
                        "selected"),
                    new[] { imported.ToDataTableValues("selected") });
                var controller =
                    new WebServiceDeviceCaptureController(
                        "selected",
                        DeviceType.GenericProxyDevice,
                        true);
                typeof(WebServiceDeviceCaptureController)
                    .GetField(
                        "_dataTable",
                        BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(controller, table);
                foreach (IDataRecord record in controller.GetCaptureRecords().ToList())
                {
                    controller.ProxyConnections.AddFromDataRecord(
                        record,
                        true);
                }

                var context =
                    new Context(controller);
                var model =
                    new CaptureAppModel(
                        Dispatcher.CurrentDispatcher)
                    {
                        SelectedDeviceContext =
                            context,
                    };
                model.CaptureDeviceContexts.Add(
                    context);
                Assert.Equal(
                    CaptureTextSemantics.Literal,
                    controller.ExportTextSource.Semantics);
                dialogs =
                    new ScriptedCaptureExportDialogs(
                        new CaptureExportScript
                        {
                            Destination = null,
                        });
                outcome =
                    await CaptureExportDriver.Workflow(
                        dialogs).RunAsync(
                            model,
                            kind,
                            owner: null);
            });

            Assert.Equal(
                kind == SazExportKind.Sanitized
                    ? new[] { "SANITIZED_EXPORT_CONFIRM", "FILE_EXPORT_SANITIZED" }
                    : new[] { "FILE_EXPORT_CAPTURES" },
                outcome.Shown);
            Assert.Equal(
                new[] { CaptureExportStep.Preparing },
                dialogs.Steps.Select(step => step.Step));
            Assert.Null(outcome.Result);
            Assert.Null(outcome.Code);
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
