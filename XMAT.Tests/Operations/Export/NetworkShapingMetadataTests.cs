// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;
using XMAT.Operations;
using XMAT.Operations.Analysis;
using XMAT.Operations.Captures;
using XMAT.Operations.Export;
using XMAT.WebServiceCapture;
using XMAT.WebServiceCapture.Proxy;
using static XMAT.Tests.Operations.Export.ExportTestData;

namespace XMAT.Tests.Operations.Export
{
    public sealed class NetworkShapingMetadataTests
    {
        [Fact]
        public void DeadlineOnlyOverrideIsRecordedButDefaultUnshapedCaptureIsNot()
        {
            var controller = new NetworkShapingController();
            Assert.Null(NetworkShapingMetadata.From(controller, 120));
            NetworkShapingMetadata metadata = NetworkShapingMetadata.From(controller, 60);
            Assert.Equal(60, Assert.Single(metadata.Traces).BodyTimeoutSeconds);
            Assert.NotNull(NetworkShapingMetadata.Parse(NetworkShapingMetadata.Serialize(metadata)));
        }

        [Fact]
        public void FullHistoryCanStillDisconnectAndTurnOffAndIsStillExportable()
        {
            var controller = new NetworkShapingController();
            var shape = new NetworkShapingProfile(NetworkShapingMode.Shape, DownKbps: 192);
            for (int count = 1; count < NetworkShapingController.MaximumHistory; count++)
            {
                controller.Apply(shape);
            }
            Assert.Throws<InvalidOperationException>(() => controller.Apply(shape));
            Assert.Throws<InvalidOperationException>(() => controller.Apply(new(NetworkShapingMode.Broken)));
            controller.Apply(new(NetworkShapingMode.Disconnect));
            controller.Apply(NetworkShapingProfile.Off);
            Assert.Equal(NetworkShapingMode.Off, controller.Current.Mode);
            Assert.Throws<InvalidOperationException>(() => controller.Apply(new(NetworkShapingMode.Disconnect)));
            NetworkShapingMetadata metadata = NetworkShapingMetadata.From(controller, 120);
            Assert.Equal(
                NetworkShapingController.MaximumHistory + 2,
                NetworkShapingMetadata.Parse(NetworkShapingMetadata.Serialize(metadata)).Traces[0].Changes.Count);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task HistorySurvivesNativeAndSazExportAndAnalysis(bool sanitized)
        {
            CaptureExportFormat format = sanitized ? CaptureExportFormat.SanitizedSaz : CaptureExportFormat.Saz;
            using var directory = new ExportTestDirectory();
            var controller = new NetworkShapingController();
            controller.Apply(NetworkShapingProfile.Preset("min"));
            controller.Apply(NetworkShapingProfile.Off);
            NetworkShapingMetadata metadata = NetworkShapingMetadata.From(controller, 1800);
            string expected = NetworkShapingMetadata.Serialize(metadata);
            var values = new Dictionary<string, object>(DeviceRow(1).Values)
            {
                [NetworkShapingMetadata.DeviceField] = expected,
            };
            using var snapshot = new FakeExportSnapshot([Session(1)], [Row(1)])
            {
                DeviceFields = CaptureExportSnapshot.StandardDeviceFields,
                DeviceDescriptors = new Dictionary<string, RawExportRow>
                {
                    ["d1"] = new(default, 1, 0, values),
                },
            };
            var operation = new ExportCaptureSessionsOperation();
            string native = directory.PathOf("original.xmatcap");
            string saz = directory.PathOf("export.saz");
            string restored = directory.PathOf("restored.xmatcap");
            foreach (var step in new[]
            {
                (Input: (string)null, Snapshot: (ICaptureExportSnapshot)snapshot, Path: native, Format: CaptureExportFormat.Xmatcap),
                (Input: native, Snapshot: (ICaptureExportSnapshot)null, Path: saz, Format: format),
                (Input: saz, Snapshot: (ICaptureExportSnapshot)null, Path: restored, Format: CaptureExportFormat.Xmatcap),
            })
            {
                var result = await operation.ExecuteAsync(
                    new ExportCaptureRequest(
                        step.Input, step.Snapshot, null, Selection(), step.Format, step.Path,
                        false, AnalysisLimits.Default, false),
                    OperationContext.None, CancellationToken.None);
                Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(item => item.Code)));
                using LoadedInput loaded = new CaptureInputReader().Read(
                    AnalysisInput.FromFile(step.Path), AnalysisLimits.Default,
                    new List<OperationDiagnostic>(), CancellationToken.None);
                Assert.Equal(expected, NetworkShapingMetadata.Serialize(Assert.Single(loaded.Devices).Shaping));
            }
            FiddlerSazImportResult guiImport = FiddlerSazHandler.ImportSazFileWithDiagnostics(saz, CancellationToken.None);
            Assert.Equal(expected, NetworkShapingMetadata.Serialize(guiImport.Shaping));
            var analysis = await new AnalyzeCaptureOperation().ExecuteAsync(
                new AnalyzeCaptureRequest(AnalysisInput.FromFile(restored), new AnalysisOptions()),
                OperationContext.None, CancellationToken.None);
            Assert.Equal(expected, NetworkShapingMetadata.Serialize(analysis.Value?.Summary.Shaping));
            Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "analysis.network-shaped");
            if (format == CaptureExportFormat.SanitizedSaz)
            {
                using var archive = ZipFile.OpenRead(saz);
                using var text = new StreamReader(archive.GetEntry(NetworkShapingMetadata.ArchiveEntry).Open());
                string json = text.ReadToEnd();
                Assert.DoesNotContain("Export PC", json);
                Assert.DoesNotContain("127.0.0.1", json);
            }
        }

        [Fact]
        public void HeadlessReaderTakesShapingFromTheWebProxyDescriptor()
        {
            using var directory = new ExportTestDirectory();
            string path = directory.PathOf("capture.xmatcap");
            var controller = new NetworkShapingController();
            controller.Apply(NetworkShapingProfile.Preset("min"));
            NetworkShapingMetadata metadata = NetworkShapingMetadata.From(controller, 120);
            using (var recorder = new ProxyCaptureRecorder(path))
            {
                recorder.RecordShaping(metadata);
            }
            using (var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                connection.Open();
                using SqliteCommand command = connection.CreateCommand();
                // The same PC's network trace descriptor is listed before its web proxy descriptor.
                command.CommandText =
                    "UPDATE CaptureDevices SET id=2; INSERT INTO CaptureDevices VALUES (1, 0, 'Proxy capture', 1, NULL)";
                command.ExecuteNonQuery();
            }

            using LoadedInput loaded = new CaptureInputReader().Read(
                AnalysisInput.FromFile(path), AnalysisLimits.Default,
                new List<OperationDiagnostic>(), CancellationToken.None);

            Assert.Equal(
                NetworkShapingMetadata.Serialize(metadata),
                NetworkShapingMetadata.Serialize(Assert.Single(loaded.Devices).Shaping));
        }

        [Fact]
        public void BoundedSazImportKeepsArchivedShaping()
        {
            using var directory = new ExportTestDirectory();
            string path = directory.PathOf("capture.saz");
            CaptureSazImportTests.WriteSaz(path);
            var controller = new NetworkShapingController();
            controller.Apply(NetworkShapingProfile.Preset("avg"));
            NetworkShapingMetadata shaping = NetworkShapingMetadata.From(controller, 1800);
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                using Stream stream = archive.CreateEntry(NetworkShapingMetadata.ArchiveEntry).Open();
                stream.Write(Encoding.UTF8.GetBytes(NetworkShapingMetadata.Serialize(shaping)));
            }

            FiddlerSazImportResult imported = FiddlerSazHandler.ImportSazFileWithDiagnostics(
                path, 1024 * 1024, CancellationToken.None);

            Assert.Equal(
                NetworkShapingMetadata.Serialize(shaping),
                NetworkShapingMetadata.Serialize(imported.Shaping));
        }

        [Theory]
        [InlineData("{\"version\":2,\"traces\":[]}")]
        [InlineData("{\"version\":1,\"traces\":null}")]
        [InlineData("{\"version\":1,\"traces\":[],\"device\":\"private-device\"}")]
        [InlineData("{\"version\":1,\"traces\":[{\"bodyTimeoutSeconds\":120,\"changes\":[{\"profile\":{}}],\"device\":\"private-device\"}]}")]
        [InlineData("{\"version\":1,\"traces\":[{\"bodyTimeoutSeconds\":120,\"changes\":[{\"profile\":{},\"device\":\"private-device\"}]}]}")]
        [InlineData("{\"version\":1,\"traces\":[{\"bodyTimeoutSeconds\":120,\"changes\":[{\"profile\":{\"host\":\"private-host\"}}]}]}")]
        [InlineData("{\"version\":1,\"traces\":[{\"bodyTimeoutSeconds\":120,\"changes\":[{\"profile\":{\"mode\":\"unknown\"}}]}]}")]
        [InlineData("{\"version\":1,\"traces\":[{\"bodyTimeoutSeconds\":120,\"changes\":[{\"profile\":null}]}]}")]
        [InlineData("{\"version\":1,\"traces\":[{\"bodyTimeoutSeconds\":120,\"changes\":[{\"timestamp\":\"invalid\",\"profile\":{}}]}]}")]
        public void UnrecognizedMetadataIsNotSilentlyExported(string json) =>
            Assert.Throws<InvalidDataException>(() => NetworkShapingMetadata.Parse(json));

        [Theory]
        [InlineData("{}", NetworkShapingMode.Off)]
        [InlineData("{\"mode\":\"ShApE\"}", NetworkShapingMode.Shape)]
        [InlineData("{\"mode\":3}", NetworkShapingMode.Disconnect)]
        public void RecordedProfilesKeepTheirDefaultsAndSupportedModeSpellings(string profile, NetworkShapingMode mode)
        {
            NetworkShapingMetadata metadata = NetworkShapingMetadata.Parse(
                "{\"version\":1,\"traces\":[{\"bodyTimeoutSeconds\":120,\"changes\":[{\"profile\":" + profile + "}]}]}");
            NetworkShapingChange change = Assert.Single(Assert.Single(metadata.Traces).Changes);
            Assert.Equal(new NetworkShapingProfile(mode), change.Profile);
            Assert.Equal(0, change.Sequence);
            Assert.Equal(default, change.Timestamp);
        }

        [Fact]
        public void DuplicateArchiveMetadataIsRejected()
        {
            using var memory = new MemoryStream();
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
            {
                for (int i = 0; i < 2; i++)
                {
                    using Stream stream = archive.CreateEntry(NetworkShapingMetadata.ArchiveEntry).Open();
                    stream.Write(Encoding.UTF8.GetBytes("{\"version\":1,\"traces\":[]}"));
                }
            }
            memory.Position = 0;
            using var input = new ZipArchive(memory, ZipArchiveMode.Read);
            Assert.Throws<InvalidDataException>(() => NetworkShapingMetadata.ReadArchive(input));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ExportDoesNotAttachAnotherDevicesShapingHistory(bool sanitized)
        {
            using var directory = new ExportTestDirectory();
            var controller = new NetworkShapingController();
            controller.Apply(NetworkShapingProfile.Preset("min"));
            var shapedDevice = new Dictionary<string, object>(DeviceRow(2, "Other PC").Values)
            {
                [NetworkShapingMetadata.DeviceField] =
                    NetworkShapingMetadata.Serialize(NetworkShapingMetadata.From(controller, 120)),
            };
            using var snapshot = new FakeExportSnapshot([Session(1), Session(2, deviceId: "d2")], [Row(1)])
            {
                DeviceFields = CaptureExportSnapshot.StandardDeviceFields,
                DeviceDescriptors = new Dictionary<string, RawExportRow>
                {
                    ["d1"] = DeviceRow(1),
                    ["d2"] = new(default, 2, 0, shapedDevice),
                },
            };
            string path = directory.PathOf("selected.saz");
            var result = await new ExportCaptureSessionsOperation().ExecuteAsync(
                new ExportCaptureRequest(
                    null, snapshot, "d1", Selection(),
                    sanitized ? CaptureExportFormat.SanitizedSaz : CaptureExportFormat.Saz,
                    path, false, AnalysisLimits.Default, false),
                OperationContext.None, CancellationToken.None);
            Assert.True(result.Succeeded);
            using var archive = ZipFile.OpenRead(path);
            Assert.Null(archive.GetEntry(NetworkShapingMetadata.ArchiveEntry));
        }

        [Fact]
        public async Task DevicesWhoseHistoriesTogetherExceedTheLimitsAreAnalyzedWithoutThemAndNotPutInOneArchive()
        {
            using var directory = new ExportTestDirectory();
            var controller = new NetworkShapingController();
            controller.Apply(NetworkShapingProfile.Preset("min"));
            NetworkShapingTrace trace = Assert.Single(NetworkShapingMetadata.From(controller, 120).Traces);
            NetworkShapingMetadata History(int traces) => new(1, Enumerable.Repeat(trace, traces).ToArray());
            const int half = NetworkShapingMetadata.MaximumTraces / 2;
            Assert.True(NetworkShapingMetadata.FitsLimits(NetworkShapingMetadata.Combine([History(half), History(half)])));
            Assert.False(NetworkShapingMetadata.FitsLimits(NetworkShapingMetadata.Combine([History(half), History(half + 1)])));

            Dictionary<string, object> Device(long id, string name, int traces) => new(DeviceRow(id, name).Values)
            {
                [NetworkShapingMetadata.DeviceField] = NetworkShapingMetadata.Serialize(History(traces)),
            };
            var other = new Dictionary<string, object>(Values(2))
            {
                [WebServiceCaptureMethod.FieldKey_DeviceName] = "Other PC",
            };
            using var snapshot = new FakeExportSnapshot([Session(1), Session(2, deviceId: "d2")], [Row(1), Row(2, other)])
            {
                DeviceFields = CaptureExportSnapshot.StandardDeviceFields,
                DeviceDescriptors = new Dictionary<string, RawExportRow>
                {
                    ["d1"] = new(default, 1, 0, Device(1, "Export PC", half)),
                    ["d2"] = new(default, 2, 0, Device(2, "Other PC", half + 1)),
                },
            };
            var operation = new ExportCaptureSessionsOperation();
            string native = directory.PathOf("devices.xmatcap");
            string saz = directory.PathOf("devices.saz");
            var exported = await operation.ExecuteAsync(
                new ExportCaptureRequest(
                    null, snapshot, null, Selection(), CaptureExportFormat.Xmatcap, native,
                    false, AnalysisLimits.Default, false),
                OperationContext.None, CancellationToken.None);
            Assert.True(exported.Succeeded, string.Join(", ", exported.Diagnostics.Select(item => item.Code)));

            var combined = await operation.ExecuteAsync(
                new ExportCaptureRequest(
                    native, null, null, Selection(), CaptureExportFormat.Saz, saz,
                    false, AnalysisLimits.Default, false),
                OperationContext.None, CancellationToken.None);
            Assert.Equal(OperationOutcome.InvalidInput, combined.Outcome);
            Assert.Equal(CaptureExportErrorCodes.InputLimit, Assert.Single(combined.Diagnostics).Code);
            Assert.False(File.Exists(saz));

            var analysis = await new AnalyzeCaptureOperation().ExecuteAsync(
                new AnalyzeCaptureRequest(AnalysisInput.FromFile(native), new AnalysisOptions()),
                OperationContext.None, CancellationToken.None);
            Assert.Null(analysis.Value.Summary.Shaping);
            Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "analysis.network-shaped");
            Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "analysis.network-shaping.omitted");
        }
    }
}
