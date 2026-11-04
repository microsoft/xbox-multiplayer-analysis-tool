// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using CaptureAnalysisEngine;
using Microsoft.Data.Sqlite;
using XMAT.DataAbstractionLayer;
using XMAT.Operations;
using XMAT.Operations.Analysis;
using XMAT.SharedInterfaces;
using XMAT.Tests.Fixtures;
using XMAT.WebServiceCapture;

namespace XMAT.Tests.Operations.Analysis
{
    public class AnalyzeCaptureOperationTests
    {
        [Fact]
        public async Task Saz_ProducesDeterministicMultiConsoleSummary()
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteSaz(
                directory.Path,
                "multi.saz",
                new SyntheticSazOptions(Consoles: 2, RequestsPerConsole: 3));

            var result = await AnalyzeFile(path);

            Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
            Assert.Equal("saz", result.Value.Summary.InputKind);
            Assert.Matches("^[0-9a-f]{64}$", result.Value.Summary.InputSha256);
            var device = Assert.Single(result.Value.Summary.Devices);
            Assert.Equal("d1", device.Id);
            Assert.Equal(new[] { "c1", "c2" }, device.Consoles.Select(console => console.Id));
            Assert.All(device.Consoles, console => Assert.Equal(3, console.Calls));
            Assert.Equal(6, result.Value.Summary.TotalCalls);
            Assert.Equal(
                new[] { path },
                Directory.GetFiles(directory.Path));
        }

        [Fact]
        public async Task HostlessSaz_UsesTheUnknownServiceAliasWithoutCrashing()
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteSazFrames(
                directory.Path,
                "hostless.saz",
                Enumerable.Repeat(
                        SyntheticSazFrameKind.EmptyHostWithPort,
                        12)
                    .ToArray());
            var imported = FiddlerSazHandler.ImportSazFile(
                path,
                CancellationToken.None);

            Assert.Equal(12, imported.Count);
            Assert.All(
                imported,
                record =>
                {
                    Assert.Equal(string.Empty, record.Host);
                    Assert.Equal("8080", record.Port);
                    Assert.Null(record.ActionableHost);
                });

            OperationResult<AnalysisResult> result =
                await AnalyzeFile(path);

            Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
            Assert.Equal(12, result.Value.Summary.TotalCalls);
            FindingSummary finding = Assert.Single(
                Assert.Single(
                    Assert.Single(
                        result.Value.Summary.Devices)
                        .Consoles)
                    .Findings);
            Assert.Equal("service-1", finding.Service);
            Assert.DoesNotContain(
                result.Diagnostics,
                diagnostic =>
                    diagnostic.Code ==
                    "analysis.input.incomplete-frame");
        }

        [Fact]
        public async Task InputKind_IsDetectedFromContentInsteadOfExtension()
        {
            using var directory = new TestDirectory();
            var saz = SyntheticCaptures.WriteSaz(
                directory.Path,
                "saz-input.bin");
            var xmat = SyntheticCaptures.WriteXmatcap(
                directory.Path,
                "xmat-input.bin");

            var sazResult = await AnalyzeFile(saz);
            var xmatResult = await AnalyzeFile(xmat);

            Assert.Equal("saz", sazResult.Value.Summary.InputKind);
            Assert.Equal("xmatcap", xmatResult.Value.Summary.InputKind);
        }

        [Fact]
        public async Task XmatCapture_ProducesDistinctDevicesAndSupportsSelection()
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteXmatcap(
                directory.Path,
                "multi.xmatcap",
                new SyntheticXmatcapOptions(Devices: 2, RequestsPerDevice: 3));

            var all = await AnalyzeFile(path);
            var selected = await AnalyzeFile(
                path,
                new AnalysisOptions(DeviceId: "d2"));

            Assert.Equal(OperationOutcome.Succeeded, all.Outcome);
            Assert.Equal(new[] { "d1", "d2" }, all.Value.Summary.Devices.Select(device => device.Id));
            Assert.All(
                all.Value.Summary.Devices,
                device => Assert.Equal(3, Assert.Single(device.Consoles).Calls));
            Assert.Equal(6, all.Value.Summary.TotalCalls);
            var selectedDevice = Assert.Single(selected.Value.Summary.Devices);
            Assert.Equal("d2", selectedDevice.Id);
            Assert.Equal(3, selected.Value.Summary.TotalCalls);
        }

        [Fact]
        public async Task XmatCapture_IsClosedWhenAnalysisReturns()
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteXmatcap(
                directory.Path,
                "capture.xmatcap");

            var result = await AnalyzeFile(path);
            File.Delete(path);

            Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
            Assert.False(File.Exists(path));
        }

        [Fact]
        public async Task UnknownDevice_ReturnsInvalidInput()
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteXmatcap(
                directory.Path,
                "capture.xmatcap");

            _ = await AnalyzeFileExpecting(
                path,
                OperationOutcome.InvalidInput,
                "analysis.device.not-found",
                new AnalysisOptions(DeviceId: "d9"));
        }

        [Fact]
        public async Task NonWebProxyDevice_IsSkippedWithDataFreeWarning()
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteXmatcap(
                directory.Path,
                "mixed.xmatcap");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false,
            }.ToString();
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    "INSERT INTO CaptureDevices(DeviceType, DeviceName, CaptureType) " +
                    "VALUES (0, 'Network trace device', 1)";
                command.ExecuteNonQuery();
            }

            var result = await AnalyzeFile(path);

            Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
            Assert.Contains(
                result.Diagnostics,
                diagnostic =>
                    diagnostic.Code == "analysis.device.skipped" &&
                    !diagnostic.Message.Contains(
                        "Network trace device",
                        StringComparison.Ordinal));
        }

        [Fact]
        public async Task FileAndRecordsPaths_UseTheSameSharedAnalysis()
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteXmatcap(
                directory.Path,
                "capture.xmatcap",
                new SyntheticXmatcapOptions(RequestsPerDevice: 3));
            var fromFile = await AnalyzeFile(path);

            List<IDataRecord> rows;
            using (var database = new SqLtReadonlyDatabase(path))
            {
                rows = database
                    .TableByName(WebServiceCaptureMethod.TableName)
                    .Dataset
                    .DataRecordsWhen(
                        new FieldValue<string>(
                            WebServiceCaptureMethod.FieldKey_DeviceName,
                            "Synthetic device 1"))
                    .ToList();
            }
            IDataRecord[] borrowedRows =
                rows.ToArray();
            var identities =
                new RequestBodyIdentityInterner();
            var recordsOperation =
                new AnalyzeCaptureOperation
                {
                    BodyIdentityFactory =
                        () => identities,
                };
            var fromRecords = await recordsOperation.ExecuteAsync(
                new AnalyzeCaptureRequest(
                    AnalysisInput.FromRecords(rows, "Synthetic device 1"),
                    new AnalysisOptions()),
                OperationContext.None,
                CancellationToken.None);

            Assert.Equal(
                borrowedRows,
                rows);
            var fileConsole = Assert.Single(
                Assert.Single(fromFile.Value.Summary.Devices).Consoles);
            var recordsConsole = Assert.Single(
                Assert.Single(fromRecords.Value.Summary.Devices).Consoles);
            Assert.Null(fromRecords.Value.Summary.InputSha256);
            Assert.Equal(fileConsole.Calls, recordsConsole.Calls);
            Assert.Equal(fileConsole.Findings, recordsConsole.Findings);
            Assert.Equal(
                0,
                identities.RepresentativeCount);
        }

        [Fact]
        public async Task BodyIdentityIsSharedAcrossDevicesAndReleasedAfterConversion()
        {
            using var directory = new TestDirectory();
            string path = SyntheticCaptures.WriteXmatcap(
                directory.Path,
                "body-identity.xmatcap",
                new SyntheticXmatcapOptions(
                    Devices: 2,
                    RequestsPerDevice: 1));
            var identities =
                new RequestBodyIdentityInterner();
            var operation =
                new AnalyzeCaptureOperation
                {
                    BodyIdentityFactory =
                        () => identities,
                };

            OperationResult<AnalysisResult> result =
                await operation.ExecuteAsync(
                    new AnalyzeCaptureRequest(
                        AnalysisInput.FromFile(path),
                        new AnalysisOptions()),
                    OperationContext.None,
                    CancellationToken.None);

            Assert.Equal(
                OperationOutcome.Succeeded,
                result.Outcome);
            Assert.Equal(
                1,
                identities.IdentityCount);
            Assert.Equal(
                0,
                identities.RepresentativeCount);
        }

        [Fact]
        public async Task NullRequiredXmatField_IsMalformed()
        {
            using var directory = new TestDirectory();
            string path = SyntheticCaptures.WriteXmatcap(
                directory.Path,
                "null-required.xmatcap");
            UpdateCapture(
                path,
                "UPDATE WebProxyConnections " +
                "SET ClientIP = NULL " +
                "WHERE id = (SELECT MIN(id) " +
                "FROM WebProxyConnections)");

            _ = await AnalyzeFileExpecting(
                path,
                OperationOutcome.InvalidInput,
                "analysis.input.malformed");
            Assert.Equal(
                new[] { path },
                Directory.GetFiles(directory.Path));
        }

        [Fact]
        public async Task RecordsInput_DoesNotInheritFileInputLimits()
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteXmatcap(
                directory.Path,
                "capture.xmatcap",
                new SyntheticXmatcapOptions(RequestsPerDevice: 4));
            List<IDataRecord> rows;
            using (var database = new SqLtReadonlyDatabase(path))
            {
                rows = database
                    .TableByName(WebServiceCaptureMethod.TableName)
                    .Dataset
                    .DataRecords
                    .ToList();
            }
            var fileOnlyLimits = new AnalysisLimits(1, 1, 1, 1);

            var result = await new AnalyzeCaptureOperation().ExecuteAsync(
                new AnalyzeCaptureRequest(
                    AnalysisInput.FromRecords(rows, "Synthetic device 1"),
                    new AnalysisOptions(Limits: fileOnlyLimits)),
                OperationContext.None,
                CancellationToken.None);

            Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
            Assert.Equal(4, result.Value.Summary.TotalCalls);
        }

        [Fact]
        public async Task Xr049Violation_IsIncludedInRawFindingsSummary()
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteSaz(
                directory.Path,
                "xr049.saz",
                new SyntheticSazOptions(IncludeXr049Violation: true));

            var result = await AnalyzeFile(path);

            var console = Assert.Single(
                Assert.Single(result.Value.Summary.Devices).Consoles);
            var finding = Assert.Single(
                console.Findings,
                item => item.Rule == "XR049Rule");
            Assert.Equal("presence_service", finding.Service);
            Assert.Equal("Warning", finding.Level);
            Assert.True(finding.Count > 0);
        }

        [Fact]
        public async Task SummaryAndDiagnostics_DoNotContainCaptureCanaries()
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteSaz(
                directory.Path,
                "canaries.saz",
                new SyntheticSazOptions(IncludeCanaries: true));
            var xmatPath = SyntheticCaptures.WriteXmatcap(
                directory.Path,
                "canaries.xmatcap",
                new SyntheticXmatcapOptions(IncludeCanaries: true));

            var result = await AnalyzeFile(path);
            var xmatResult = await AnalyzeFile(xmatPath);

            Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
            Assert.Equal(OperationOutcome.Succeeded, xmatResult.Outcome);
            var serializedSummaries = JsonSerializer.Serialize(new[]
            {
                result.Value.Summary,
                xmatResult.Value.Summary,
            });
            Assert.DoesNotContain(
                directory.Path,
                serializedSummaries,
                StringComparison.OrdinalIgnoreCase);
            foreach (var value in new object[]
            {
                result.Value.Summary,
                result.Diagnostics,
                xmatResult.Value.Summary,
                xmatResult.Diagnostics,
            })
            {
                Assert.DoesNotContain(
                    SyntheticCaptures.Canary,
                    JsonSerializer.Serialize(value),
                    StringComparison.Ordinal);
            }
        }

        [Fact]
        public async Task MissingInput_IsInvalidAndIsNotCreated()
        {
            using var directory = new TestDirectory();
            var path = System.IO.Path.Combine(directory.Path, "missing.xmatcap");

            _ = await AnalyzeFileExpecting(
                path,
                OperationOutcome.InvalidInput,
                "analysis.input.not-found");
            Assert.False(File.Exists(path));
            Assert.Empty(Directory.GetFiles(directory.Path));
        }

        [Theory]
        [InlineData("corrupt", null)]
        [InlineData("truncated-signature", "analysis.input.malformed")]
        [InlineData("zip-bomb", "analysis.input.limit")]
        public async Task SazBasicInvalidInput_ReturnsExpectedDiagnostic(
            string caseName,
            string code)
        {
            using var directory = new TestDirectory();
            string path;
            if (caseName == "corrupt")
            {
                path =
                    SyntheticCaptures.WriteCorruptSaz(
                        directory.Path,
                        "corrupt.saz");
            }
            else if (caseName == "zip-bomb")
            {
                path =
                    SyntheticCaptures.WriteZipBombSaz(
                        directory.Path,
                        "bomb.saz");
            }
            else
            {
                path =
                    System.IO.Path.Combine(
                        directory.Path,
                        "truncated.saz");
                File.WriteAllBytes(
                    path,
                    new byte[]
                    {
                        0x50,
                        0x4b,
                        0x03,
                        0x04,
                        0x00,
                    });
            }

            _ = await AnalyzeFileExpecting(
                path,
                OperationOutcome.InvalidInput,
                code);
            Assert.Equal(new[] { path }, Directory.GetFiles(directory.Path));
        }

        [Theory]
        [InlineData("entry-count")]
        [InlineData("central-directory-bytes")]
        public async Task SazArchiveMetadataLimit_ReturnsInvalidInput(
            string caseName)
        {
            using var directory = new TestDirectory();
            bool entryCount =
                caseName ==
                "entry-count";
            string path =
                SyntheticCaptures.WriteZipWithEntries(
                directory.Path,
                caseName + ".saz",
                entryCount:
                    entryCount
                        ? 3
                        : 1,
                entryNameLength:
                    entryCount
                        ? 8
                        : 256);
            AnalysisLimits limits =
                entryCount
                    ? AnalysisLimits.Default with
                    {
                        MaxArchiveEntries = 2,
                    }
                    : AnalysisLimits.Default with
                    {
                        MaxCentralDirectoryBytes = 64,
                    };

            _ = await AnalyzeFileExpecting(
                path,
                OperationOutcome.InvalidInput,
                "analysis.input.limit",
                new AnalysisOptions(Limits: limits));
        }

        [Fact]
        public async Task SazUnderstatedEntryCount_IsRejectedByPreflight()
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteUnderstatedEntryCountZip(
                directory.Path,
                "understated-count.saz");

            var failure = ReadSazFailure(path);
            _ = await AnalyzeFileExpecting(
                path,
                OperationOutcome.InvalidInput,
                "analysis.input.malformed");

            Assert.Equal(BoundedSazFailure.Malformed, failure.Failure);
            Assert.IsType<SazArchiveException>(
                failure.InnerException);
            Assert.Equal(
                new[] { path },
                Directory.GetFiles(directory.Path));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SazCentralDirectoryOutsideFile_ReturnsMalformedInput(
            bool corruptSize)
        {
            using var directory = new TestDirectory();
            var path =
                SyntheticCaptures.WriteInvalidCentralDirectoryBoundsZip(
                    directory.Path,
                    "invalid-directory-bounds.saz",
                    corruptSize);

            _ = await AnalyzeFileExpecting(
                path,
                OperationOutcome.InvalidInput,
                "analysis.input.malformed");
            Assert.Equal(
                new[] { path },
                Directory.GetFiles(directory.Path));
        }

        [Fact]
        public async Task SazCommentDecoyEndRecord_ReturnsMalformedInput()
        {
            using var directory = new TestDirectory();
            var path =
                SyntheticCaptures.WriteSazWithDecoyEndRecordComment(
                    directory.Path,
                    "decoy-end-record.saz");

            _ = await AnalyzeFileExpecting(
                path,
                OperationOutcome.InvalidInput,
                "analysis.input.malformed");
            Assert.Equal(
                new[] { path },
                Directory.GetFiles(directory.Path));
        }

        [Fact]
        public async Task Zip64Saz_IsAccepted()
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteZip64Saz(
                directory.Path,
                "zip64.saz");

            OperationResult<AnalysisResult> result =
                await AnalyzeFileExpecting(
                    path,
                    OperationOutcome.Succeeded);
            Assert.Equal(1, result.Value.Summary.TotalCalls);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Zip64BoundaryOverflow_IsRejectedAsMalformed(
            bool unrepresentableRecordOffset)
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteZip64Saz(
                directory.Path,
                "invalid-zip64.saz",
                zip64RecordOffset:
                    unrepresentableRecordOffset ? ulong.MaxValue : null,
                zip64RecordBodySize:
                    unrepresentableRecordOffset ? 44UL : ulong.MaxValue);

            var failure = ReadSazFailure(path);
            _ = await AnalyzeFileExpecting(
                path,
                OperationOutcome.InvalidInput,
                "analysis.input.malformed");

            Assert.Equal(BoundedSazFailure.Malformed, failure.Failure);
            SazArchiveException archiveFailure =
                Assert.IsType<SazArchiveException>(
                    failure.InnerException);
            if (unrepresentableRecordOffset)
            {
                Assert.Null(
                    archiveFailure.InnerException);
            }
            else
            {
                Assert.IsType<OverflowException>(
                    archiveFailure.InnerException);
            }
            Assert.Equal(
                new[] { path },
                Directory.GetFiles(directory.Path));
        }

        [Fact]
        public async Task OpaqueEncodedBody_IsAnalyzedWithoutDecoding()
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteOpaqueEncodedBodySaz(
                directory.Path,
                "opaque-body.saz");
            var limits = AnalysisLimits.Default with
            {
                MaxEntryBytes = 4 * 1024,
                MaxTotalRetainedBytes = 4 * 1024,
            };

            OperationResult<AnalysisResult> result =
                await AnalyzeFileExpecting(
                path,
                OperationOutcome.Succeeded,
                options:
                    new AnalysisOptions(
                        Limits: limits));
            Assert.Equal(1, result.Value.Summary.TotalCalls);
            Assert.Empty(result.Diagnostics);
        }

        [Fact]
        public async Task IncompleteFrame_IsSkippedWhenUsableDataRemains()
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteMixedIncompleteSaz(
                directory.Path,
                "mixed.saz");

            OperationResult<AnalysisResult> result =
                await AnalyzeFileExpecting(
                    path,
                    OperationOutcome.Succeeded);
            Assert.Equal(1, result.Value.Summary.TotalCalls);
            Assert.Contains(
                result.Diagnostics,
                diagnostic =>
                    diagnostic.Code == "analysis.input.incomplete-frame");
        }

        [Fact]
        public async Task OnlyIncompleteFrames_ReturnsNoAnalyzableData()
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteIncompleteSaz(
                directory.Path,
                "incomplete.saz");

            OperationResult<AnalysisResult> result =
                await AnalyzeFileExpecting(
                    path,
                    OperationOutcome.NoAnalyzableData);
            Assert.Contains(
                result.Diagnostics,
                diagnostic =>
                    diagnostic.Code == "analysis.input.incomplete-frame");
        }

        [Fact]
        public async Task MalformedFrameBytes_StillCountTowardTheAggregateLimit()
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteSazFrames(
                directory.Path,
                "malformed-over-limit.saz",
                new[]
                {
                    SyntheticSazFrameKind.MalformedRequestStartLine,
                },
                extraResponseBodyBytes: 4 * 1024);
            var limits = AnalysisLimits.Default with
            {
                MaxEntryBytes = 8 * 1024,
                MaxTotalRetainedBytes = 1024,
            };

            _ = await AnalyzeFileExpecting(
                path,
                OperationOutcome.InvalidInput,
                "analysis.input.limit",
                new AnalysisOptions(Limits: limits));
            Assert.Equal(
                new[] { path },
                Directory.GetFiles(directory.Path));
        }

        [Theory]
        [InlineData("input-bytes")]
        [InlineData("record-count")]
        [InlineData("retained-bytes")]
        public async Task SazConfiguredLimit_ReturnsInvalidInput(
            string caseName)
        {
            using var directory = new TestDirectory();
            bool recordCount =
                caseName ==
                "record-count";
            string path =
                SyntheticCaptures.WriteSaz(
                directory.Path,
                caseName + ".saz",
                recordCount
                    ? new SyntheticSazOptions(
                        RequestsPerConsole: 2)
                    : null);
            AnalysisLimits limits =
                caseName switch
                {
                    "input-bytes" =>
                        AnalysisLimits.Default with
                        {
                            MaxInputBytes = 1,
                        },
                    "record-count" =>
                        AnalysisLimits.Default with
                        {
                            MaxRecords = 1,
                        },
                    _ =>
                        AnalysisLimits.Default with
                        {
                            MaxTotalRetainedBytes = 100,
                        },
                };
            string code =
                caseName == "input-bytes"
                    ? "analysis.input.too-large"
                    : "analysis.input.limit";

            _ = await AnalyzeFileExpecting(
                path,
                OperationOutcome.InvalidInput,
                code,
                new AnalysisOptions(Limits: limits));
            Assert.Equal(new[] { path }, Directory.GetFiles(directory.Path));
        }

        [Fact]
        public async Task XmatStoredRecordLimit_ReturnsInvalidInput()
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteXmatcap(
                directory.Path,
                "capture.xmatcap");
            var limits = AnalysisLimits.Default with
            {
                MaxEntryBytes = 32,
                MaxTotalRetainedBytes = 128,
            };

            _ = await AnalyzeFileExpecting(
                path,
                OperationOutcome.InvalidInput,
                "analysis.input.limit",
                new AnalysisOptions(Limits: limits));
            Assert.Equal(new[] { path }, Directory.GetFiles(directory.Path));
        }

        [Theory]
        [InlineData(
            "web-proxy-extension",
            "WebProxyConnections",
            "Extension",
            'w')]
        [InlineData(
            "capture-device-extension",
            "CaptureDevices",
            "Metadata",
            'd')]
        public async Task XmatExtension_UsesPerRecordLimit(
            string caseName,
            string table,
            string column,
            char value)
        {
            using var directory = new TestDirectory();
            string path =
                SyntheticCaptures.WriteXmatcap(
                directory.Path,
                caseName + ".xmatcap");
            UpdateCapture(
                path,
                $"ALTER TABLE {table} ADD COLUMN {column} TEXT");
            UpdateCapture(
                path,
                $"UPDATE {table} SET {column} = $value",
                new string(
                    value,
                    4 * 1024));
            var limits = AnalysisLimits.Default with
            {
                MaxEntryBytes = 2 * 1024,
                MaxTotalRetainedBytes = 64 * 1024,
            };

            _ = await AnalyzeFileExpecting(
                path,
                OperationOutcome.InvalidInput,
                "analysis.input.limit",
                new AnalysisOptions(Limits: limits));
        }

        [Fact]
        public async Task XmatDeviceRows_ShareTheAggregateLimit()
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteXmatcap(
                directory.Path,
                "device-aggregate.xmatcap",
                new SyntheticXmatcapOptions(
                    Devices: 3,
                    RequestsPerDevice: 1));
            var limits = AnalysisLimits.Default with
            {
                MaxEntryBytes = 10 * 1024,
                MaxTotalRetainedBytes = 16 * 1024,
            };
            var baseline = await AnalyzeFile(
                path,
                new AnalysisOptions(Limits: limits));
            UpdateCapture(
                path,
                "ALTER TABLE CaptureDevices ADD COLUMN Metadata TEXT");
            UpdateCapture(
                path,
                "UPDATE CaptureDevices SET Metadata = $value",
                new string('d', 6 * 1024));

            _ = await AnalyzeFileExpecting(
                path,
                OperationOutcome.InvalidInput,
                "analysis.input.limit",
                new AnalysisOptions(Limits: limits));

            Assert.Equal(OperationOutcome.Succeeded, baseline.Outcome);
        }

        [Fact]
        public async Task XmatRecordLimit_CountsBothTables()
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteXmatcap(
                directory.Path,
                "record-count.xmatcap",
                new SyntheticXmatcapOptions(RequestsPerDevice: 1));
            var limits = AnalysisLimits.Default with { MaxRecords = 1 };

            _ = await AnalyzeFileExpecting(
                path,
                OperationOutcome.InvalidInput,
                "analysis.input.limit",
                new AnalysisOptions(Limits: limits));
        }

        [Fact]
        public async Task XmatSmallExtensions_PreserveAnalysisSummary()
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteXmatcap(
                directory.Path,
                "extended.xmatcap",
                new SyntheticXmatcapOptions(RequestsPerDevice: 3));
            var baseline = await AnalyzeFile(path);
            UpdateCapture(
                path,
                "ALTER TABLE WebProxyConnections ADD COLUMN ExtensionText TEXT");
            UpdateCapture(
                path,
                "ALTER TABLE WebProxyConnections ADD COLUMN ExtensionNumber INTEGER");
            UpdateCapture(
                path,
                "UPDATE WebProxyConnections SET ExtensionText = 'web', ExtensionNumber = 7");
            UpdateCapture(
                path,
                "ALTER TABLE CaptureDevices ADD COLUMN ExtensionText TEXT");
            UpdateCapture(
                path,
                "ALTER TABLE CaptureDevices ADD COLUMN ExtensionNumber INTEGER");
            UpdateCapture(
                path,
                "UPDATE CaptureDevices SET ExtensionText = 'device', ExtensionNumber = 9");

            var extended = await AnalyzeFile(path);

            Assert.Equal(OperationOutcome.Succeeded, baseline.Outcome);
            Assert.Equal(OperationOutcome.Succeeded, extended.Outcome);
            Assert.Equal(
                JsonSerializer.Serialize(
                    baseline.Value.Summary.Devices),
                JsonSerializer.Serialize(
                    extended.Value.Summary.Devices));
            Assert.Equal(
                baseline.Value.Summary.TotalCalls,
                extended.Value.Summary.TotalCalls);
            Assert.Equal(
                baseline.Value.Summary.TotalFindings,
                extended.Value.Summary.TotalFindings);
        }

        [Fact]
        public async Task XmatUnsupportedExtensionType_ReturnsSchemaError()
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteXmatcap(
                directory.Path,
                "unsupported-extension.xmatcap");
            UpdateCapture(
                path,
                "ALTER TABLE WebProxyConnections ADD COLUMN Extension BLOB");

            _ = await AnalyzeFileExpecting(
                path,
                OperationOutcome.InvalidInput,
                "analysis.input.schema");
        }

        [Theory]
        [InlineData("generated", "analysis.input.schema")]
        [InlineData("unknown-table", "analysis.input.schema")]
        [InlineData("too-wide", "analysis.input.limit")]
        [InlineData("huge-ddl", "analysis.input.limit")]
        public async Task HostileSchema_IsRejectedBeforeRowsAreMaterialized(
            string kind,
            string code)
        {
            using var directory = new TestDirectory();
            string path = kind switch
            {
                "generated" =>
                    HostileSqliteSchemas.CreateGeneratedColumnCapture(
                        directory.Path,
                        "generated.xmatcap"),
                "unknown-table" =>
                    HostileSqliteSchemas.CreateDatabase(
                        directory.Path,
                        "unknown.xmatcap",
                        HostileSqliteSchemas.CaptureDevicesDdl,
                        HostileSqliteSchemas.WebProxyConnectionsDdl,
                        "CREATE TABLE XMATCANARY7f3c" +
                        "(id INTEGER PRIMARY KEY,Value TEXT)"),
                "too-wide" =>
                    HostileSqliteSchemas.CreateOrdinaryCapture(
                        directory.Path,
                        "wide.xmatcap",
                        HostileSqliteSchemas.LongExtensionDdl(
                            SqLtSchemaProfile.MaxExtensionColumns +
                            1)),
                _ => CreateHugeDdlCapture(
                    directory.Path),
            };

            var result = await AnalyzeFile(path);

            Assert.Equal(
                OperationOutcome.InvalidInput,
                result.Outcome);
            Assert.Contains(
                result.Diagnostics,
                diagnostic =>
                    diagnostic.Code == code &&
                    !diagnostic.Message.Contains(
                        SyntheticCaptures.Canary,
                        StringComparison.OrdinalIgnoreCase));
            Assert.Equal(
                new[] { path },
                Directory.GetFiles(directory.Path));
        }

        [Fact]
        public async Task SqliteTooBigDuringRowRead_ReturnsInputLimit()
        {
            using var directory = new TestDirectory();
            string path =
                HostileSqliteSchemas.CreateOversizedValueCapture(
                    directory.Path,
                    "oversized-row.xmatcap",
                    34 * 1024 * 1024);

            OperationResult<AnalysisResult> result =
                await AnalyzeFileExpecting(
                    path,
                    OperationOutcome.InvalidInput,
                    "analysis.input.limit");

            Assert.DoesNotContain(
                result.Diagnostics,
                diagnostic =>
                    diagnostic.Code == "analysis.input.malformed");
        }

        [Fact]
        public async Task XmatMalformedBase64_ReturnsInvalidInput()
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteXmatcap(
                directory.Path,
                "malformed-body.xmatcap");
            UpdateCapture(
                path,
                "UPDATE WebProxyConnections SET ResponseBody = 'not-base64!'");

            _ = await AnalyzeFileExpecting(
                path,
                OperationOutcome.InvalidInput,
                "analysis.input.malformed");
        }

        [Fact]
        public async Task MissingSchema_ReturnsInvalidInput()
        {
            using var directory = new TestDirectory();
            var path = WriteSchemaMissingCapture(directory.Path);

            _ = await AnalyzeFileExpecting(
                path,
                OperationOutcome.InvalidInput,
                "analysis.input.schema");
            Assert.Equal(new[] { path }, Directory.GetFiles(directory.Path));
        }

        [Fact]
        public async Task Cancellation_ReturnsCancelled()
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteSaz(
                directory.Path,
                "capture.saz");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            _ = await AnalyzeFileExpecting(
                path,
                OperationOutcome.Cancelled,
                "analysis.cancelled",
                cancellationToken:
                    cancellation.Token);
        }

        [Fact]
        public async Task RuleWorkLimit_ReturnsDataFreeInvalidInput()
        {
            using var directory = new TestDirectory();
            string path = SyntheticCaptures.WriteXmatcap(
                directory.Path,
                "work-limit.xmatcap",
                new SyntheticXmatcapOptions(
                    RequestsPerDevice: 3,
                    IncludeCanaries: true));
            var observedExceptions =
                new List<Type>();
            var operation = new AnalyzeCaptureOperation
            {
                WorkBudgetFactory =
                    static () =>
                        new AnalysisWorkBudget(1),
                ExceptionObserver = (type, _) =>
                    observedExceptions.Add(type),
            };

            OperationResult<AnalysisResult> result =
                await operation.ExecuteAsync(
                    new AnalyzeCaptureRequest(
                        AnalysisInput.FromFile(path),
                        new AnalysisOptions()),
                    OperationContext.None,
                    CancellationToken.None);

            Assert.Equal(
                OperationOutcome.InvalidInput,
                result.Outcome);
            Assert.Null(result.Value);
            OperationDiagnostic diagnostic =
                Assert.Single(
                    result.Diagnostics,
                    item =>
                        item.Code ==
                        AnalyzeCaptureOperation
                            .WorkLimitCode);
            Assert.Equal(
                OperationDiagnosticSeverity.Error,
                diagnostic.Severity);
            Assert.DoesNotContain(
                SyntheticCaptures.Canary,
                diagnostic.Message,
                StringComparison.Ordinal);
            Assert.Empty(observedExceptions);
            Assert.Equal(
                new[] { path },
                Directory.GetFiles(directory.Path));
        }

        [Fact]
        public async Task RegexTimeout_ReturnsDataFreeWorkLimit()
        {
            using var directory = new TestDirectory();
            string path = SyntheticCaptures.WriteXmatcap(
                directory.Path,
                "regex-timeout.xmatcap");
            var assets = new AnalysisAssetSet(
                System.Text.Encoding.UTF8.GetBytes(
                    "\"userpresence.xboxlive.com\",\"service\",\"service\",\"service\"\n" +
                    "\"POST https://userpresence.xboxlive.com/(timeoutpatterncanary)\\1\"," +
                    "\"timeoutmethodcanary\",\"timeoutmethodcanary\",\"timeoutmethodcanary\"\n"),
                System.Text.Encoding.UTF8.GetBytes(
                    "{\"Version\":\"1\",\"Rules\":[]}"));
            var observedExceptions =
                new List<Type>();
            var operation =
                new AnalyzeCaptureOperation(assets)
                {
                    WorkBudgetFactory =
                        static () =>
                            new AnalysisWorkBudget(
                                fallbackMatcher:
                                    static (_, _) =>
                                        throw new RegexMatchTimeoutException()),
                    ExceptionObserver = (type, _) =>
                        observedExceptions.Add(type),
                };

            OperationResult<AnalysisResult> result =
                await operation.ExecuteAsync(
                    new AnalyzeCaptureRequest(
                        AnalysisInput.FromFile(path),
                        new AnalysisOptions()),
                    OperationContext.None,
                    CancellationToken.None);

            Assert.Equal(
                OperationOutcome.InvalidInput,
                result.Outcome);
            Assert.Null(result.Value);
            OperationDiagnostic diagnostic =
                Assert.Single(
                    result.Diagnostics,
                    item =>
                        item.Code ==
                        AnalyzeCaptureOperation
                            .WorkLimitCode);
            Assert.DoesNotContain(
                "timeoutpatterncanary",
                diagnostic.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "timeoutmethodcanary",
                diagnostic.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Empty(observedExceptions);
            Assert.Equal(
                new[] { path },
                Directory.GetFiles(directory.Path));
        }

        [Fact]
        public void MixedCancellationAndWorkLimit_UsesCancellationOnlyWhenRequested()
        {
            var aggregate =
                new AggregateException(
                    new OperationCanceledException(),
                    new AnalysisWorkLimitException());

            Assert.Equal(
                AnalyzeCaptureOperation
                    .RuleAggregateFailure.Cancelled,
                AnalyzeCaptureOperation
                    .ClassifyRuleAggregate(
                        aggregate,
                        true));
            Assert.Equal(
                AnalyzeCaptureOperation
                    .RuleAggregateFailure.WorkLimit,
                AnalyzeCaptureOperation
                    .ClassifyRuleAggregate(
                        aggregate,
                        false));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RuleAggregateClassification_DoesNotMaskOtherFailures(
            bool fatal)
        {
            Exception other = fatal
                ? new OutOfMemoryException()
                : new InvalidOperationException();
            var aggregate =
                new AggregateException(
                    new AnalysisWorkLimitException(),
                    other);

            Assert.Equal(
                AnalyzeCaptureOperation
                    .RuleAggregateFailure.None,
                AnalyzeCaptureOperation
                    .ClassifyRuleAggregate(
                        aggregate,
                        true));
        }

        [Fact]
        public async Task RuleWorkBudget_IsSharedAcrossSelectedDevices()
        {
            using var directory = new TestDirectory();
            string path = SyntheticCaptures.WriteXmatcap(
                directory.Path,
                "two-devices.xmatcap",
                new SyntheticXmatcapOptions(
                    Devices: 2,
                    RequestsPerDevice: 8));
            UpdateCapture(
                path,
                "UPDATE WebProxyConnections SET " +
                "RequestPath = '/users/xuid(1)/devices/current/" +
                "titles/current?token=same'");
            var measuredBudget =
                new AnalysisWorkBudget(long.MaxValue);
            var selectedOperation =
                new AnalyzeCaptureOperation
                {
                    WorkBudgetFactory =
                        () => measuredBudget,
                };

            OperationResult<AnalysisResult> selected =
                await selectedOperation.ExecuteAsync(
                    new AnalyzeCaptureRequest(
                        AnalysisInput.FromFile(path),
                        new AnalysisOptions(
                            DeviceId: "d1")),
                    OperationContext.None,
                    CancellationToken.None);

            Assert.Equal(
                OperationOutcome.Succeeded,
                selected.Outcome);
            Assert.True(measuredBudget.Used > 0);
            var factoryCalls = 0;
            var sharedOperation =
                new AnalyzeCaptureOperation
                {
                    WorkBudgetFactory = () =>
                    {
                        factoryCalls++;
                        return new AnalysisWorkBudget(
                            measuredBudget.Used);
                    },
                };

            OperationResult<AnalysisResult> combined =
                await sharedOperation.ExecuteAsync(
                    new AnalyzeCaptureRequest(
                        AnalysisInput.FromFile(path),
                        new AnalysisOptions()),
                    OperationContext.None,
                    CancellationToken.None);

            Assert.Equal(1, factoryCalls);
            Assert.Equal(
                OperationOutcome.InvalidInput,
                combined.Outcome);
            Assert.Contains(
                combined.Diagnostics,
                diagnostic =>
                    diagnostic.Code ==
                    AnalyzeCaptureOperation
                        .WorkLimitCode);
        }

        [Fact]
        public async Task CancellationAtRulesPhase_IsDeterministic()
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteSaz(
                directory.Path,
                "capture.saz");
            using var cancellation = new CancellationTokenSource();
            var progress = new ImmediateProgress(update =>
            {
                if (update.Phase == "analysis.rules")
                {
                    cancellation.Cancel();
                }
            });

            var result = await new AnalyzeCaptureOperation().ExecuteAsync(
                new AnalyzeCaptureRequest(
                    AnalysisInput.FromFile(path),
                    new AnalysisOptions()),
                new OperationContext(progress: progress),
                cancellation.Token);

            Assert.Equal(OperationOutcome.Cancelled, result.Outcome);
        }

        [Fact]
        public async Task RecordsInput_IsConsumedAsASingleStreamingSequence()
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteXmatcap(
                directory.Path,
                "capture.xmatcap",
                new SyntheticXmatcapOptions(RequestsPerDevice: 3));
            List<IDataRecord> records;
            using (var database = new SqLtReadonlyDatabase(path))
            {
                records = database
                    .TableByName(WebServiceCaptureMethod.TableName)
                    .Dataset
                    .DataRecords
                    .ToList();
            }
            var source = new EphemeralRecordSource(records);

            var result = await new AnalyzeCaptureOperation().ExecuteAsync(
                new AnalyzeCaptureRequest(
                    AnalysisInput.FromRecords(source, "Synthetic device 1"),
                    new AnalysisOptions()),
                OperationContext.None,
                CancellationToken.None);

            Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
            Assert.Equal(3, result.Value.Summary.TotalCalls);
            Assert.Equal(1, source.EnumerationCount);
        }

        [Fact]
        public async Task ActiveWriter_IsRejectedAsUnreadableSnapshot()
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteSaz(
                directory.Path,
                "capture.saz");
            using var writer = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete);

            _ = await AnalyzeFileExpecting(
                path,
                OperationOutcome.InvalidInput,
                "analysis.input.unreadable");
        }

        [Fact]
        public async Task XmatCaptureWithActiveWriter_IsRejectedAsUnreadable()
        {
            using var directory = new TestDirectory();
            string path =
                SyntheticCaptures.WriteXmatcap(
                    directory.Path,
                    "capture.xmatcap");
            using var writer =
                new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite |
                        FileShare.Delete);

            _ = await AnalyzeFileExpecting(
                path,
                OperationOutcome.InvalidInput,
                "analysis.input.unreadable");
        }

        [Fact]
        public async Task UnsupportedCaptureLocation_ReturnsLocationDiagnostic()
        {
            using var directory = new TestDirectory();
            string path =
                SyntheticCaptures.WriteXmatcap(
                    directory.Path,
                    "capture.xmatcap");
            var operation =
                new AnalyzeCaptureOperation
                {
                    CaptureDatabaseFactory =
                        (databasePath, profile) =>
                            new SqLtReadonlyDatabase(
                                databasePath,
                                profile,
                                beforeSqliteOpens: null,
                                finalPathUnavailable:
                                    _ => true),
                };

            OperationResult<AnalysisResult> result =
                await operation.ExecuteAsync(
                    new AnalyzeCaptureRequest(
                        AnalysisInput.FromFile(path),
                        new AnalysisOptions()),
                    OperationContext.None,
                    CancellationToken.None);

            AssertOutcome(
                result,
                OperationOutcome.InvalidInput,
                "analysis.input.location-unsupported");
            File.Delete(path);
            Assert.False(
                File.Exists(path));
        }

        [Fact]
        public async Task FileReplacementBeforeExecution_HashMatchesAnalyzedContents()
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteSaz(
                directory.Path,
                "capture.data");
            var input = AnalysisInput.FromFile(path);
            File.Delete(path);
            SyntheticCaptures.WriteXmatcap(
                directory.Path,
                "capture.data");

            var result = await new AnalyzeCaptureOperation().ExecuteAsync(
                new AnalyzeCaptureRequest(
                    input,
                    new AnalysisOptions()),
                OperationContext.None,
                CancellationToken.None);

            Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
            Assert.Equal("xmatcap", result.Value.Summary.InputKind);
            Assert.Equal(
                Sha256(path),
                result.Value.Summary.InputSha256);
        }

        [Theory]
        [InlineData("-journal", false)]
        [InlineData("-shm", true)]
        [InlineData("-wal", false)]
        public async Task AdmissionTimeSidecar_IsRejectedAsLiveDatabase(
            string suffix,
            bool sidecarIsDirectory)
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteXmatcap(
                directory.Path,
                "capture.xmatcap");
            string sidecar =
                path +
                suffix;
            if (sidecarIsDirectory)
            {
                Directory.CreateDirectory(
                    sidecar);
            }
            else
            {
                File.WriteAllBytes(
                    sidecar,
                    new byte[] { 1 });
            }
            string[] before =
                Directory.GetFileSystemEntries(
                        directory.Path)
                    .OrderBy(
                        entry => entry,
                        StringComparer.Ordinal)
                    .ToArray();

            _ = await AnalyzeFileExpecting(
                path,
                OperationOutcome.InvalidInput,
                "analysis.input.live-database");
            Assert.Equal(
                before,
                Directory.GetFileSystemEntries(
                        directory.Path)
                    .OrderBy(
                        entry => entry,
                        StringComparer.Ordinal));
        }

        [Fact]
        public async Task WalHeaderWithoutSidecars_IsRejectedBeforeSqliteOpen()
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteXmatcap(
                directory.Path,
                "capture-wal.xmatcap");
            SwitchToWal(path);
            Assert.False(File.Exists(path + "-wal"));
            Assert.False(File.Exists(path + "-shm"));
            var before = Directory.GetFileSystemEntries(directory.Path)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            var hash = Sha256(path);

            var first = await AnalyzeFile(path);
            var second = await AnalyzeFile(path);

            foreach (var result in new[] { first, second })
            {
                AssertOutcome(
                    result,
                    OperationOutcome.InvalidInput,
                    "analysis.input.live-database");
            }
            Assert.Equal(hash, Sha256(path));
            Assert.Equal(
                before,
                Directory.GetFileSystemEntries(directory.Path)
                    .OrderBy(value => value, StringComparer.Ordinal));
        }

        [Fact]
        public async Task Alias_UsesFinalPathSidecarPolicy()
        {
            using var directory = new TestDirectory();
            string targetDirectory =
                Path.Combine(
                    directory.Path,
                    "target");
            string target =
                SyntheticCaptures.WriteXmatcap(
                    targetDirectory,
                    "capture.xmatcap");
            string link =
                Path.Combine(
                    directory.Path,
                    "link");
            CreateJunction(
                link,
                targetDirectory);
            string alias =
                Path.Combine(
                    link,
                    "capture.xmatcap");

            try
            {
                OperationResult<AnalysisResult> accepted =
                    await AnalyzeFile(alias);
                Assert.Equal(
                    OperationOutcome.Succeeded,
                    accepted.Outcome);
                Assert.Equal(
                    Sha256(target),
                    accepted.Value.Summary.InputSha256);

                File.WriteAllBytes(
                    target + "-wal",
                    new byte[] { 1 });
                OperationResult<AnalysisResult> rejected =
                    await AnalyzeFile(alias);
                AssertOutcome(
                    rejected,
                    OperationOutcome.InvalidInput,
                    "analysis.input.live-database");
            }
            finally
            {
                if (Directory.Exists(link))
                {
                    Directory.Delete(link);
                }
            }
        }

        [Fact]
        public async Task AliasRetarget_HashMatchesTheGuardedAnalyzedFile()
        {
            using var directory = new TestDirectory();
            string firstDirectory =
                Path.Combine(
                    directory.Path,
                    "first");
            string secondDirectory =
                Path.Combine(
                    directory.Path,
                    "second");
            _ = SyntheticCaptures.WriteXmatcap(
                firstDirectory,
                "capture.xmatcap",
                new SyntheticXmatcapOptions(
                    RequestsPerDevice: 1));
            string second =
                SyntheticCaptures.WriteXmatcap(
                    secondDirectory,
                    "capture.xmatcap",
                    new SyntheticXmatcapOptions(
                        RequestsPerDevice: 3));
            string link =
                Path.Combine(
                    directory.Path,
                    "link");
            CreateJunction(
                link,
                firstDirectory);
            string alias =
                Path.Combine(
                    link,
                    "capture.xmatcap");
            var retargeted = false;
            var operation =
                new AnalyzeCaptureOperation
                {
                    CaptureDatabaseFactory =
                        (databasePath, profile) =>
                        {
                            Assert.False(
                                retargeted);
                            Directory.Delete(link);
                            CreateJunction(
                                link,
                                secondDirectory);
                            retargeted = true;
                            return new SqLtReadonlyDatabase(
                                databasePath,
                                profile);
                        },
                };

            try
            {
                OperationResult<AnalysisResult> result =
                    await operation.ExecuteAsync(
                        new AnalyzeCaptureRequest(
                            AnalysisInput.FromFile(alias),
                            new AnalysisOptions()),
                        OperationContext.None,
                        CancellationToken.None);

                Assert.Equal(
                    OperationOutcome.Succeeded,
                    result.Outcome);
                Assert.True(
                    retargeted);
                Assert.Equal(
                    3,
                    result.Value.Summary.TotalCalls);
                Assert.Equal(
                    Sha256(second),
                    result.Value.Summary.InputSha256);
            }
            finally
            {
                if (Directory.Exists(link))
                {
                    Directory.Delete(link);
                }
            }
        }

        [Fact]
        public async Task LateWriteAheadLog_IsNeverConsumed()
        {
            using var directory = new TestDirectory();
            string path =
                SyntheticCaptures.WriteXmatcap(
                    directory.Path,
                    "capture.xmatcap");
            byte[] log =
                HostileSqliteSchemas.CreateWriteAheadLog(
                    path,
                    "DELETE FROM WebProxyConnections");
            string hash =
                Sha256(path);
            var operation =
                new AnalyzeCaptureOperation
                {
                    CaptureDatabaseFactory =
                        (databasePath, profile) =>
                            new SqLtReadonlyDatabase(
                                databasePath,
                                profile,
                                _ =>
                                    File.WriteAllBytes(
                                        databasePath + "-wal",
                                        log)),
                };

            OperationResult<AnalysisResult> result =
                await operation.ExecuteAsync(
                    new AnalyzeCaptureRequest(
                        AnalysisInput.FromFile(path),
                        new AnalysisOptions()),
                    OperationContext.None,
                    CancellationToken.None);

            Assert.Equal(
                OperationOutcome.Succeeded,
                result.Outcome);
            Assert.Equal(
                4,
                result.Value.Summary.TotalCalls);
            Assert.Equal(
                hash,
                result.Value.Summary.InputSha256);
            Assert.Equal(
                log,
                File.ReadAllBytes(
                    path + "-wal"));
            Assert.False(
                File.Exists(
                    path + "-shm"));
        }

        [Fact]
        public async Task RollbackJournalCapture_RemainsSupported()
        {
            using var directory = new TestDirectory();
            var path = SyntheticCaptures.WriteXmatcap(
                directory.Path,
                "capture-delete.xmatcap");

            var result = await AnalyzeFile(path);

            Assert.Equal(OperationOutcome.Succeeded, result.Outcome);
            Assert.Equal(
                new[] { path },
                Directory.GetFileSystemEntries(directory.Path));
        }

        [Fact]
        public async Task NoWriteContext_RefusesLegacyOutput()
        {
            using var directory = new TestDirectory();
            var result = await new AnalyzeCaptureOperation().ExecuteAsync(
                new AnalyzeCaptureRequest(
                    AnalysisInput.FromRecords(
                        Array.Empty<IDataRecord>(),
                        "device"),
                    new AnalysisOptions(
                        ProduceLegacyReports: true)),
                OperationContext.None,
                CancellationToken.None);

            Assert.Equal(
                OperationOutcome.PrerequisiteMissing,
                result.Outcome);
            Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
        }

        private static Task<OperationResult<AnalysisResult>> AnalyzeFile(
            string path,
            AnalysisOptions options = null,
            CancellationToken cancellationToken = default)
        {
            return new AnalyzeCaptureOperation().ExecuteAsync(
                new AnalyzeCaptureRequest(
                    AnalysisInput.FromFile(path),
                    options ?? new AnalysisOptions()),
                OperationContext.None,
                cancellationToken);
        }

        private static async Task<OperationResult<AnalysisResult>>
            AnalyzeFileExpecting(
            string path,
            OperationOutcome outcome,
            string diagnosticCode = null,
            AnalysisOptions options = null,
            CancellationToken cancellationToken = default)
        {
            OperationResult<AnalysisResult> result =
                await AnalyzeFile(
                    path,
                    options,
                    cancellationToken);
            AssertOutcome(
                result,
                outcome,
                diagnosticCode);
            return result;
        }

        private static void AssertOutcome(
            OperationResult<AnalysisResult> result,
            OperationOutcome outcome,
            string diagnosticCode = null)
        {
            Assert.Equal(
                outcome,
                result.Outcome);
            if (diagnosticCode != null)
            {
                Assert.Contains(
                    result.Diagnostics,
                    diagnostic =>
                        diagnostic.Code ==
                        diagnosticCode);
            }
        }

        private static BoundedSazException ReadSazFailure(string path)
        {
            AnalysisLimits defaults = AnalysisLimits.Default;
            using var stream = File.OpenRead(path);
            return Assert.Throws<BoundedSazException>(
                () => BoundedSazReader.Read(
                    stream,
                    new BoundedSazLimits(
                        defaults.MaxInputBytes,
                        defaults.MaxEntryBytes,
                        defaults.MaxTotalRetainedBytes,
                        defaults.MaxRecords,
                        BoundedSazReader.EntryLimitForRecords(defaults.MaxRecords),
                        BoundedSazReader.DefaultMaxCentralDirectoryBytes),
                    CancellationToken.None));
        }

        private static string WriteSchemaMissingCapture(string directory)
        {
            var path = System.IO.Path.Combine(directory, "schema-missing.xmatcap");
            using var database = new SqLtDatabase();
            database.Initialize();
            database.CreateTable(
                "Unrelated",
                new Field<string>("Value"));
            database.SaveToFile(path);
            return path;
        }

        private static string CreateHugeDdlCapture(string directory)
        {
            string path =
                HostileSqliteSchemas.CreateOrdinaryCapture(
                    directory,
                    "huge-ddl.xmatcap");
            HostileSqliteSchemas.ReplaceSchemaSql(
                path,
                "WebProxyConnections",
                "CREATE TABLE WebProxyConnections(" +
                SyntheticCaptures.Canary +
                new string(
                    'x',
                    SqLtSchemaProfile.MaxSchemaRecordBytes *
                    2) +
                ")");
            return path;
        }

        private static string Sha256(string path)
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            return Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(stream))
                .ToLowerInvariant();
        }

        private static void UpdateCapture(
            string path,
            string commandText,
            string value = null)
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false,
            }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = commandText;
            if (value != null)
            {
                command.Parameters.AddWithValue("$value", value);
            }
            command.ExecuteNonQuery();
        }

        private static void SwitchToWal(string path)
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false,
            }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA journal_mode=WAL";
                Assert.Equal(
                    "wal",
                    command.ExecuteScalar()?.ToString(),
                    ignoreCase: true);
            }
            using (var checkpoint = connection.CreateCommand())
            {
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                checkpoint.ExecuteNonQuery();
            }
        }

        private static void CreateJunction(
            string link,
            string target)
        {
            var start =
                new ProcessStartInfo("cmd.exe")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
            foreach (string argument in
                new[]
                {
                    "/c",
                    "mklink",
                    "/J",
                    link,
                    target,
                })
            {
                start.ArgumentList.Add(
                    argument);
            }
            using Process process =
                Process.Start(start);
            string output =
                process.StandardOutput
                    .ReadToEnd() +
                process.StandardError
                    .ReadToEnd();
            process.WaitForExit();
            Assert.True(
                process.ExitCode == 0,
                output);
        }

        private sealed class ImmediateProgress :
            IProgress<OperationProgress>
        {
            private readonly Action<OperationProgress> _report;

            internal ImmediateProgress(
                Action<OperationProgress> report)
            {
                _report = report;
            }

            public void Report(OperationProgress value)
            {
                _report(value);
            }
        }

        private sealed class EphemeralRecordSource :
            IEnumerable<IDataRecord>
        {
            private readonly IReadOnlyList<IDataRecord> _records;
            private int _generation;

            internal EphemeralRecordSource(
                IReadOnlyList<IDataRecord> records)
            {
                _records = records;
            }

            internal int EnumerationCount { get; private set; }

            public IEnumerator<IDataRecord> GetEnumerator()
            {
                if (EnumerationCount++ != 0)
                {
                    throw new InvalidOperationException(
                        "The record source may only be enumerated once.");
                }
                return Enumerate().GetEnumerator();
            }

            System.Collections.IEnumerator
                System.Collections.IEnumerable.GetEnumerator() =>
                GetEnumerator();

            private IEnumerable<IDataRecord> Enumerate()
            {
                foreach (var record in _records)
                {
                    var generation = ++_generation;
                    yield return new EphemeralRecord(
                        this,
                        generation,
                        record);
                }
                _generation++;
            }

            private sealed class EphemeralRecord : IDataRecord
            {
                private readonly EphemeralRecordSource _owner;
                private readonly int _generation;
                private readonly IDataRecord _inner;

                internal EphemeralRecord(
                    EphemeralRecordSource owner,
                    int generation,
                    IDataRecord inner)
                {
                    _owner = owner;
                    _generation = generation;
                    _inner = inner;
                }

                public int RowId
                {
                    get
                    {
                        EnsureCurrent();
                        return _inner.RowId;
                    }
                }

                public long Int(string fieldName)
                {
                    EnsureCurrent();
                    return _inner.Int(fieldName);
                }

                public string Str(string fieldName)
                {
                    EnsureCurrent();
                    return _inner.Str(fieldName);
                }

                private void EnsureCurrent()
                {
                    if (_owner._generation != _generation)
                    {
                        throw new InvalidOperationException(
                            "The record is no longer valid.");
                    }
                }
            }
        }

        private sealed class TestDirectory : IDisposable
        {
            internal TestDirectory()
            {
                Path = System.IO.Path.Combine(
                    AppContext.BaseDirectory,
                    "HeadlessTestArtifacts",
                    $"XMAT-headless-analysis-{Guid.NewGuid():N}");
                Directory.CreateDirectory(Path);
            }

            internal string Path { get; }

            public void Dispose()
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, true);
                }
            }
        }
    }
}
