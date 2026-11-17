// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.IO.Compression;
using System.Text;
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

        [Theory]
        [InlineData("{\"version\":2,\"traces\":[]}")]
        [InlineData("{\"version\":1,\"traces\":null}")]
        [InlineData("{\"version\":1,\"traces\":[],\"device\":\"private-device\"}")]
        public void UnrecognizedMetadataIsNotSilentlyExported(string json) =>
            Assert.Throws<InvalidDataException>(() => NetworkShapingMetadata.Parse(json));

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
    }
}
