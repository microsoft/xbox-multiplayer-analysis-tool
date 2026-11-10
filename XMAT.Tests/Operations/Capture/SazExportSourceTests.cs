// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using XMAT.DataAbstractionLayer;
using XMAT.Operations;
using XMAT.Operations.Capture;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Operations.Capture
{
    public sealed class SazExportSourceTests
    {
        [Fact]
        public void T13_ActiveSourceFreezesTheWatermarkAndSelectsOnlyItsDevice()
        {
            using SqLtDatabase database =
                Database(out IDataTable table);
            Add(table, 1, "selected");
            Add(table, 2, "other");
            Add(table, 3, "selected");
            SazExportSource source =
                SazExportSource.Active(
                    table,
                    "selected",
                    CaptureTextSource.Of(table));

            long watermark =
                source.GetWatermark(
                    CancellationToken.None).Value;
            Add(table, 4, "selected");
            SazExportPage page =
                source.ReadPageForKind(
                    null,
                    watermark,
                    256,
                    SazExportBudgets.Default,
                    SazExportKind.Plain,
                    CancellationToken.None);

            Assert.Equal(
                new long[] { 1, 3 },
                page.Keys.Select(
                    static key => key.RowId));
            Assert.Equal(3, page.LastScannedRowId);
        }

        [Fact]
        public void T13_EmptyAndRepeatedReadsHaveStableNullableCursors()
        {
            using SqLtDatabase database =
                Database(out IDataTable table);
            SazExportSource empty =
                SazExportSource.Active(
                    table,
                    "selected",
                    CaptureTextSource.Of(table));
            Assert.Null(
                empty.GetWatermark(
                    CancellationToken.None));

            Add(table, 1, "selected");
            Add(table, 2, "selected");
            SazExportSource source =
                SazExportSource.Active(
                    table,
                    "selected",
                    CaptureTextSource.Of(table));
            long through =
                source.GetWatermark(
                    CancellationToken.None).Value;
            long[] first = source.ReadPage(
                    null,
                    through,
                    256,
                    SazExportBudgets.Default,
                    CancellationToken.None)
                .Keys.Select(static key => key.RowId)
                .ToArray();
            long[] second = source.ReadPage(
                    null,
                    through,
                    256,
                    SazExportBudgets.Default,
                    CancellationToken.None)
                .Keys.Select(static key => key.RowId)
                .ToArray();

            Assert.Equal(first, second);
            Assert.Equal(new long[] { 1, 2 }, first);
        }

        [Fact]
        public async Task T13_ActiveRowReadsHoldTheTableLockAgainstWriters()
        {
            using SqLtDatabase database =
                Database(out IDataTable dataTable);
            var table = Assert.IsType<SqLtDataTable>(
                dataTable);
            Add(table, 1, "selected");
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            Task reader = Task.Run(
                () => table.ReadForSazExport(
                    (connection, name) =>
                    {
                        entered.Set();
                        Assert.True(
                            release.Wait(
                                TimeSpan.FromSeconds(10)));
                        return 0;
                    }));
            Assert.True(
                entered.Wait(
                    TimeSpan.FromSeconds(10)));

            Task writer = Task.Run(
                () => Add(
                    table,
                    2,
                    "selected"));
            await Task.Delay(100);
            Assert.False(writer.IsCompleted);
            release.Set();
            await Task.WhenAll(reader, writer);
        }

        [Fact]
        public async Task T13_PrepareOnLargeValuesPlansWithoutMaterializingThem()
        {
            using SqLtDatabase database =
                Database(out IDataTable table);
            string body = Convert.ToBase64String(
                new byte[4 * 1024 * 1024]);
            Add(
                table,
                1,
                "selected",
                body);
            var operation = new SazExportOperation();
            long before =
                GC.GetAllocatedBytesForCurrentThread();
            OperationResult<SazExportPlan> result =
                await operation.PrepareAsync(
                        new(
                            SazExportSource.Active(
                                table,
                                "selected",
                                CaptureTextSource.Of(table)),
                            SazExportKind.Plain,
                            null,
                            SazExportBudgets.Default),
                        OperationContext.None,
                        CancellationToken.None);
            long allocated =
                GC.GetAllocatedBytesForCurrentThread() -
                before;

            Assert.True(result.Succeeded);
            Assert.True(
                allocated < 2 * 1024 * 1024,
                $"Planning allocated {allocated:N0} bytes.");
            Assert.True(
                result.Value.StoredBytes >=
                body.Length);
        }

        private static SqLtDatabase Database(
            out IDataTable table)
        {
            var database = new SqLtDatabase();
            database.Initialize();
            table = database.CreateTable(
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
            return database;
        }

        private static void Add(
            IDataTable table,
            long number,
            string device,
            string requestBody = "")
        {
            table.AddRow(
                device,
                number,
                number,
                ProxyConnectionModel.ToDateTimeString(
                    DateTime.UnixEpoch),
                ProxyConnectionModel.ToDateTimeString(
                    DateTime.UnixEpoch.AddMilliseconds(10)),
                "https",
                "example.test",
                "443",
                "GET",
                "/",
                "200",
                "GET / HTTP/1.1\r\nHost: example.test\r\n\r\n",
                requestBody,
                "HTTP/1.1 200 OK\r\n\r\n",
                string.Empty,
                "10.0.0.1");
        }
    }
}
