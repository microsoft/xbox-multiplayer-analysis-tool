// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Globalization;
using XMAT.DataAbstractionLayer;
using XMAT.Operations.Analysis;
using XMAT.Operations.Processes;
using XMAT.SharedInterfaces;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Models;

namespace XMAT.Tests.Operations.Analysis
{
    // The analysis reads SAZ process metadata the way the GUI import does, and its records expose it to the shared
    // process codec through IOptionalTypedDataRecord.
    public sealed class SazProcessAnalysisTests : IDisposable
    {
        private static readonly DateTimeOffset Started = new(2026, 2, 1, 8, 30, 15, TimeSpan.Zero);
        private readonly string _directory = Path.Join(
            AppContext.BaseDirectory,
            "HeadlessTestArtifacts",
            "SazProcessAnalysis-" + Guid.NewGuid().ToString("N"));

        [Fact]
        public void BoundedReaderAndImport_ReadTheSameImportedProcesses()
        {
            string path = ProcessCaptures.WriteSaz(
                _directory,
                "processes.saz",
                new[]
                {
                    new ProcessSazFrame("x-ProcessInfo", "Game.exe:4100"),
                    new ProcessSazFrame("X-PROCESSINFO", "svchost.exe:900", Status: "local"),
                    new ProcessSazFrame("x-ProcessInfo", null),
                    new ProcessSazFrame(
                        "x-processinfo",
                        "Game.exe:4100",
                        StartTimeUtc: Started.ToString("o", CultureInfo.InvariantCulture)),
                    new ProcessSazFrame("x-ProcessInfo", null, Status: "remote"),
                });
            var expected = new[]
            {
                new ClientProcessInfo("Game.exe", 4100, null, ClientProcessStatus.Imported),
                new ClientProcessInfo("svchost.exe", 900, null, ClientProcessStatus.Imported),
                ClientProcessInfo.NotRecorded,
                new ClientProcessInfo("Game.exe", 4100, Started, ClientProcessStatus.Imported),
                new ClientProcessInfo(string.Empty, 0, null, ClientProcessStatus.Remote),
            };

            BoundedSazReadResult bounded = ReadBounded(path);
            ProxyConnectionsCollection imported = FiddlerSazHandler.ImportSazFile(path, CancellationToken.None);

            Assert.Equal(expected, Processes(bounded.Records));
            Assert.Equal(expected, Processes(imported));
        }

        [Fact]
        public void AnalysisRecord_ExposesTheProcessToTheSharedCodec()
        {
            ProxyConnectionModel model = ProcessCaptures.ExportRecord(1);
            var info = new ClientProcessInfo("Game.exe", 1234, Started, ClientProcessStatus.Imported);
            model.SetClientProcess(info);

            AnalyzeCaptureOperation.AnalysisDataRecord record =
                AnalyzeCaptureOperation.AnalysisDataRecord.FromProxyModel(1, model);

            var typed = Assert.IsAssignableFrom<IOptionalTypedDataRecord>(record);
            Assert.True(typed.TryReadString(WebProxyCaptureSchema.ClientProcessName, out string name));
            Assert.Equal("Game.exe", name);
            Assert.True(typed.TryReadInt64(WebProxyCaptureSchema.ClientProcessId, out long processId));
            Assert.Equal(1234, processId);
            Assert.True(typed.TryReadString(WebProxyCaptureSchema.ClientProcessStatus, out string status));
            Assert.Equal("imported", status);
            Assert.True(typed.TryReadString(WebProxyCaptureSchema.ClientProcessStartTime, out string start));
            Assert.Equal(Started, DateTimeOffset.Parse(start, CultureInfo.InvariantCulture));
            Assert.Equal(info, ClientProcessRecordCodec.Read(record));
        }

        [Fact]
        public void AnalysisRecord_WithoutProcessInformationReadsAsNotRecorded()
        {
            ProxyConnectionModel model = ProcessCaptures.ExportRecord(1);

            AnalyzeCaptureOperation.AnalysisDataRecord record =
                AnalyzeCaptureOperation.AnalysisDataRecord.FromProxyModel(1, model);

            Assert.Equal(ClientProcessInfo.NotRecorded, ClientProcessRecordCodec.Read(record));
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, true);
            }
        }

        private static ClientProcessInfo[] Processes(IEnumerable<ProxyConnectionModel> records) =>
            records.OrderBy(record => record.RequestNumber).Select(record => record.ClientProcess).ToArray();

        private static BoundedSazReadResult ReadBounded(string path)
        {
            AnalysisLimits limits = AnalysisLimits.Default;
            using FileStream stream = File.OpenRead(path);
            return BoundedSazReader.Read(
                stream,
                new BoundedSazLimits(
                    limits.MaxInputBytes,
                    limits.MaxEntryBytes,
                    limits.MaxTotalRetainedBytes,
                    limits.MaxRecords,
                    BoundedSazReader.EntryLimitForRecords(limits.MaxRecords),
                    BoundedSazReader.DefaultMaxCentralDirectoryBytes),
                CancellationToken.None);
        }
    }
}
