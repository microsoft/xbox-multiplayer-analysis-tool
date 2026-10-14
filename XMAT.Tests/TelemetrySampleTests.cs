// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.IO;
using System.Text.Json;
using XMAT.SharedInterfaces;
using XMAT.Telemetry;

namespace XMAT.Tests
{
    public sealed class TelemetrySampleTests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"xmat-sample-test-{Guid.NewGuid():N}");
        private string SampleFile => Path.Combine(_directory, "events.jsonl");

        public TelemetrySampleTests()
        {
            Directory.CreateDirectory(_directory);
        }

        [Fact]
        public void SampleArgumentRequiresAnExplicitAbsolutePath()
        {
            Assert.Null(LocalTelemetrySampleReporter.SamplePath([]));
            Assert.Null(LocalTelemetrySampleReporter.SamplePath(["other-argument"]));
            Assert.Equal(SampleFile, LocalTelemetrySampleReporter.SamplePath(["--telemetry-sample", SampleFile]));
            Assert.Throws<ArgumentException>(() => LocalTelemetrySampleReporter.SamplePath(["--telemetry-sample"]));
            Assert.Throws<ArgumentException>(() => LocalTelemetrySampleReporter.SamplePath(["--telemetry-sample", "relative.jsonl"]));
            Assert.Throws<ArgumentException>(() => LocalTelemetrySampleReporter.SamplePath(["--telemetry-sample", SampleFile, "extra"]));
            Assert.Throws<ArgumentException>(() => LocalTelemetrySampleReporter.SamplePath(["extra", "--telemetry-sample", SampleFile]));
        }

        [Fact]
        public void ExportingBuildCannotStartLocalSamplesOrReadSampleMetadata()
        {
            Assert.Throws<InvalidOperationException>(() => XmatTelemetryService.CreateSample(
                new(true, true, "test-key", TelemetryBuildChannel.Test), SampleFile,
                _ => throw new InvalidOperationException("Metadata must not be read."), _ => { }));
            Assert.False(File.Exists(SampleFile));
        }

        [Fact]
        public void LocalSamplesNeedNoApprovalKeyOrNetworkReporterAndPreserveTypedAttributes()
        {
            List<string> diagnostics = [];
            using (var service = XmatTelemetryService.CreateSample(
                new(false, false, "", TelemetryBuildChannel.Local), SampleFile, channel =>
                {
                    Assert.Equal(TelemetryBuildChannel.Local, channel);
                    return
                    [
                        new("ext.app.id", "XboxMultiplayerAnalysisTool"),
                        new("BuildChannel", channel.ToString()),
                        new("ext.device.localId", "s:11111111-1111-1111-1111-111111111111"),
                        new("ext.xbl.sbx", "SAMPLE_SANDBOX"),
                        new("MachineIdAvailable", true)
                    ];
                }, diagnostics.Add))
            {
                service.Track(new(TelemetryEventKind.AppLaunched));
                service.Ready();
                using var operation = service.Begin(TelemetryOperationKind.Analysis, TelemetryFeature.XboxLiveAnalysis,
                    new(CaptureType.WebProxy, DeviceType.Unknown, CaptureOrigin.Imported), TelemetryEntryPoint.DragDrop);
                operation.MarkStage(TelemetryStage.Validate);
                operation.Complete(items: 12);
                service.UnhandledFailure(new IOException("private-token-path-host"));
            }

            var lines = File.ReadAllLines(SampleFile);
            Assert.NotEmpty(lines);
            var names = new List<string>();
            string sessionId = null;
            foreach (var line in lines)
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                Assert.Equal(["name", "time", "resources", "data"], root.EnumerateObject().Select(x => x.Name));
                Assert.True(root.GetProperty("time").TryGetDateTimeOffset(out var timestamp));
                Assert.Equal(TimeSpan.Zero, timestamp.Offset);
                var resources = root.GetProperty("resources");
                Assert.Equal("Local", resources.GetProperty("BuildChannel").GetString());
                Assert.True(resources.GetProperty("MachineIdAvailable").GetBoolean());
                var data = root.GetProperty("data");
                Assert.Equal(1, data.GetProperty("SchemaVersion").GetInt32());
                Assert.Equal(JsonValueKind.Number, data.GetProperty("DurationMs").ValueKind);
                Assert.Equal(JsonValueKind.Number, data.GetProperty("Count").ValueKind);
                var id = data.GetProperty("SessionId").GetString();
                Assert.True(Guid.TryParseExact(id, "N", out _));
                sessionId ??= id;
                Assert.Equal(sessionId, id);
                names.Add(root.GetProperty("name").GetString());
                if (root.GetProperty("name").GetString() == "Microsoft.Gaming.XMAT.OperationCompleted")
                {
                    Assert.Equal("Analysis", data.GetProperty("Operation").GetString());
                    Assert.Equal("Imported", data.GetProperty("CaptureOrigin").GetString());
                    Assert.Equal("DragDrop", data.GetProperty("EntryPoint").GetString());
                    Assert.Equal("ElevenToHundred", data.GetProperty("ItemCountBucket").GetString());
                    Assert.True(Guid.TryParseExact(data.GetProperty("OperationId").GetString(), "N", out _));
                }
                Assert.DoesNotContain("private-token-path-host", line);
                Assert.DoesNotContain(SampleFile, line);
            }
            Assert.Contains("Microsoft.Gaming.XMAT.AppLaunched", names);
            Assert.Contains("Microsoft.Gaming.XMAT.AppReady", names);
            Assert.Contains("Microsoft.Gaming.XMAT.SessionSummary", names);
            Assert.Equal("Microsoft.Gaming.XMAT.AppClosed", names[^1]);
            Assert.Empty(diagnostics);
        }

        [Fact]
        public void ExistingFilesAreNotOverwrittenAndMissingDirectoriesFailExplicitly()
        {
            File.WriteAllText(SampleFile, "existing");
            Assert.Throws<IOException>(() => new LocalTelemetrySampleReporter(SampleFile, [], _ => { }));
            Assert.Equal("existing", File.ReadAllText(SampleFile));
            Assert.Throws<DirectoryNotFoundException>(() => new LocalTelemetrySampleReporter(
                Path.Combine(_directory, "missing", "events.jsonl"), [], _ => { }));
        }

        [Fact]
        public void InvalidEventsAreDiagnosedWithoutWritingPayloads()
        {
            List<string> diagnostics = [];
            using (var reporter = new LocalTelemetrySampleReporter(SampleFile, [], diagnostics.Add))
            {
                reporter.Send(new((TelemetryEventKind)999), Guid.NewGuid());
                reporter.Dispose();
                reporter.Dispose();
            }
            Assert.Empty(File.ReadAllText(SampleFile));
            Assert.Equal(["InvalidEvent"], diagnostics);
        }

        public void Dispose()
        {
            if (File.Exists(SampleFile))
            {
                File.Delete(SampleFile);
            }
            Directory.Delete(_directory);
        }
    }
}
